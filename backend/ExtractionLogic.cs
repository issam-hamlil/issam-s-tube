using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

public record YtDlpRunResult(int ExitCode, string Stdout, string Stderr, bool TimedOut, string? StartError);

public record DownloadResponse(
    [property: JsonPropertyName("download_url")] string DownloadUrl,
    [property: JsonPropertyName("media_type")] string MediaType);

public interface IYtDlpRunner
{
    Task<YtDlpRunResult> RunAsync(string url, string? cookiesPath);
    Task<YtDlpRunResult> DownloadAsync(string url, string? cookiesPath, string outputPath);
    Task<YtDlpRunResult> AudioAsync(string url, string? cookiesPath, string outputPath);
}

public class YtDlpProcessRunner : IYtDlpRunner
{
    public Task<YtDlpRunResult> RunAsync(string url, string? cookiesPath)
    {
        var psi = BasePsi();
        psi.ArgumentList.Add("-j");

        if (url.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) || url.Contains("youtu.be", StringComparison.OrdinalIgnoreCase))
        {
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("best[ext=mp4]/best");
        }

        AddCookiesAndUrl(psi, cookiesPath, url);
        return ExecuteAsync(psi, TimeSpan.FromSeconds(30));
    }

    public Task<YtDlpRunResult> DownloadAsync(string url, string? cookiesPath, string outputPath)
    {
        var psi = BasePsi();

        // Real download, not metadata-only — always asks for the best
        // available video+audio, even if that means two separate streams
        // that need merging. This is what actually closes the quality gap;
        // a direct CDN URL can only ever be a single pre-combined file.
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("bestvideo+bestaudio/best");
        psi.ArgumentList.Add("--merge-output-format");
        psi.ArgumentList.Add("mp4");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(outputPath);

        AddCookiesAndUrl(psi, cookiesPath, url);
        return ExecuteAsync(psi, TimeSpan.FromMinutes(10));
    }

    public Task<YtDlpRunResult> AudioAsync(string url, string? cookiesPath, string outputPath)
    {
        var psi = BasePsi();

        // Extract the best available audio stream and re-encode to MP3 at
        // VBR quality 0 (the highest setting — roughly 220-260 kbps, which
        // outperforms 320 kbps CBR in perceptual quality while staying
        // smaller). Requires ffmpeg to be in PATH for the conversion step.
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("bestaudio/best");
        psi.ArgumentList.Add("-x");
        psi.ArgumentList.Add("--audio-format");
        psi.ArgumentList.Add("mp3");
        psi.ArgumentList.Add("--audio-quality");
        psi.ArgumentList.Add("0");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(outputPath);

        AddCookiesAndUrl(psi, cookiesPath, url);
        return ExecuteAsync(psi, TimeSpan.FromMinutes(10));
    }

    private static ProcessStartInfo BasePsi() => new()
    {
        FileName = "yt-dlp",
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
        ArgumentList = { "--no-warnings", "--socket-timeout", "15" },
    };

    private static void AddCookiesAndUrl(ProcessStartInfo psi, string? cookiesPath, string url)
    {
        if (!string.IsNullOrEmpty(cookiesPath))
        {
            psi.ArgumentList.Add("--cookies");
            psi.ArgumentList.Add(cookiesPath);
        }
        psi.ArgumentList.Add(url);
    }

    private static async Task<YtDlpRunResult> ExecuteAsync(ProcessStartInfo psi, TimeSpan timeout)
    {
        using var process = new Process { StartInfo = psi };

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new YtDlpRunResult(-1, "", "", false, ex.Message);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return new YtDlpRunResult(-1, "", "", true, null);
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        return new YtDlpRunResult(process.ExitCode, stdout, stderr, false, null);
    }
}

internal static class ExtractionLogic
{
    private static readonly HttpClient _httpClient = new();

    private static readonly HashSet<string> _imageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { "jpg", "jpeg", "png", "gif", "webp" };

    // Returns true when the yt-dlp JSON object describes a still image rather
    // than a video. Checks four independent signals so that any one of them
    // is sufficient — Instagram is inconsistent about which fields it fills.
    static bool IsImagePost(JsonElement root)
    {
        // 1. ext field at the root level
        if (root.TryGetProperty("ext", out var extElem) &&
            _imageExtensions.Contains(extElem.GetString() ?? ""))
            return true;

        // 2. vcodec = "none" at root + a URL present (CDN image, no video track)
        if (root.TryGetProperty("vcodec", out var vcRoot) &&
            (vcRoot.GetString() ?? "") == "none" &&
            root.TryGetProperty("url", out _))
            return true;

        // 3. formats array — image if every entry has vcodec = "none"
        //    or at least one entry has an image ext
        if (root.TryGetProperty("formats", out var fmts) &&
            fmts.ValueKind == JsonValueKind.Array &&
            fmts.GetArrayLength() > 0)
        {
            bool anyVideo = false;
            bool anyImageExt = false;
            foreach (var f in fmts.EnumerateArray())
            {
                var vc = f.TryGetProperty("vcodec", out var vce) ? vce.GetString() ?? "" : "";
                if (vc != "none" && vc != "") anyVideo = true;
                var fe = f.TryGetProperty("ext", out var fee) ? fee.GetString() ?? "" : "";
                if (_imageExtensions.Contains(fe)) anyImageExt = true;
            }
            if (anyImageExt) return true;
            if (!anyVideo) return true; // all formats are audio-only / image
        }

        // 4. raw URL ends with an image extension (before the query string)
        if (root.TryGetProperty("url", out var rawUrlElem))
        {
            var path = (rawUrlElem.GetString() ?? "").Split('?')[0].ToLowerInvariant();
            if (path.EndsWith(".jpg") || path.EndsWith(".jpeg") ||
                path.EndsWith(".png") || path.EndsWith(".webp") ||
                path.EndsWith(".gif"))
                return true;
        }

        return false;
    }

    // Picks the best image URL from the JSON object.
    static string? PickImageUrl(JsonElement root)
    {
        if (root.TryGetProperty("url", out var u) && !string.IsNullOrEmpty(u.GetString()))
            return u.GetString();

        if (root.TryGetProperty("formats", out var fmts) &&
            fmts.ValueKind == JsonValueKind.Array &&
            fmts.GetArrayLength() > 0)
        {
            var best = fmts[fmts.GetArrayLength() - 1];
            if (best.TryGetProperty("url", out var fu)) return fu.GetString();
        }

        return null;
    }

    internal static async Task<(IResult result, bool success, string platform, string? title, string? thumbnail)> RunExtractionAsync(ExtractRequest request, IYtDlpRunner runner, ILinkedInImageFetcher linkedInFetcher, IInstaloaderRunner instaloaderRunner)
    {
        if (string.IsNullOrWhiteSpace(request.Url) ||
            !Uri.TryCreate(request.Url, UriKind.Absolute, out var parsedUrl) ||
            (parsedUrl.Scheme != Uri.UriSchemeHttp && parsedUrl.Scheme != Uri.UriSchemeHttps))
        {
            return (Results.BadRequest(new ErrorResponse("INVALID_URL", "The provided URL is missing or not a valid http(s) URL.")), false, "Unknown", null, null);
        }

        string platform = DetectPlatform(parsedUrl);

        if (platform == "LinkedIn")
        {
            var (imageUrl, liTitle, error) = await linkedInFetcher.FetchAsync(request.Url);

            if (imageUrl == null)
            {
                return (Results.BadRequest(new ErrorResponse("LINKEDIN_IMAGE_NOT_FOUND", error ?? "Could not find an image on this LinkedIn post.")), false, platform, null, null);
            }

            return (Results.Ok(new ExtractResponse(imageUrl, liTitle ?? "LinkedIn image", imageUrl, "image", null)), true, platform, liTitle, imageUrl);
        }

        var cookiesPath = Environment.GetEnvironmentVariable("INSTAGRAM_COOKIES_PATH") ?? "/app/cookies.txt";
        var cookiesToUse = parsedUrl.Host.Contains("instagram.com", StringComparison.OrdinalIgnoreCase) && File.Exists(cookiesPath)
            ? cookiesPath
            : null;

        var run = await runner.RunAsync(request.Url, cookiesToUse);

        if (run.StartError != null)
        {
            return (Results.Problem(
                detail: $"Could not start yt-dlp: {run.StartError}",
                title: "YTDLP_NOT_FOUND",
                statusCode: StatusCodes.Status500InternalServerError), false, platform, null, null);
        }

        if (run.TimedOut)
        {
            return (Results.Problem(
                detail: "yt-dlp did not respond in time.",
                title: "TIMEOUT",
                statusCode: StatusCodes.Status504GatewayTimeout), false, platform, null, null);
        }

        if (run.ExitCode != 0)
        {
            if (platform == "Instagram" && run.Stderr.Contains("no video formats found", StringComparison.OrdinalIgnoreCase))
            {
                var downloadsDir = Path.Combine(AppContext.BaseDirectory, "downloads");
                Directory.CreateDirectory(downloadsDir);
                return await TryInstagramImageFallbackAsync(request.Url, instaloaderRunner, cookiesToUse, platform, downloadsDir);
            }
            var (code, message) = ClassifyError(run.Stderr);
            return (Results.BadRequest(new ErrorResponse(code, message)), false, platform, null, null);
        }

        try
        {
            // yt-dlp emits one JSON object per image for carousel posts,
            // separated by newlines.  Take only the first entry.
            var firstLine = run.Stdout.Trim().Split('\n')[0].Trim();
            using var doc = JsonDocument.Parse(firstLine);
            var root = doc.RootElement;

            string title = root.TryGetProperty("title", out var t) ? t.GetString() ?? "Untitled" : "Untitled";
            string? thumbnail = root.TryGetProperty("thumbnail", out var th) ? th.GetString() : null;

            string? videoUrl = root.TryGetProperty("url", out var u) ? u.GetString() : null;
            Dictionary<string, string>? headers = ExtractHeaders(root);

            if (string.IsNullOrEmpty(videoUrl) &&
                root.TryGetProperty("formats", out var formats) &&
                formats.ValueKind == JsonValueKind.Array &&
                formats.GetArrayLength() > 0)
            {
                var best = formats[formats.GetArrayLength() - 1];
                videoUrl = best.TryGetProperty("url", out var fu) ? fu.GetString() : null;
                headers ??= ExtractHeaders(best);
            }

            if (string.IsNullOrEmpty(videoUrl))
            {
                return (Results.BadRequest(new ErrorResponse("NO_PLAYABLE_URL", "yt-dlp returned metadata but no usable video URL was found.")), false, platform, title, thumbnail);
            }

            string mediaType = IsImagePost(root) ? "image" : "video";

            return (Results.Ok(new ExtractResponse(videoUrl, title, thumbnail, mediaType, headers)), true, platform, title, thumbnail);
        }
        catch (JsonException)
        {
            return (Results.Problem(
                detail: "yt-dlp exited successfully but its output wasn't valid JSON.",
                title: "EXTRACTION_FAILED",
                statusCode: StatusCodes.Status500InternalServerError), false, platform, null, null);
        }
    }

    public static async Task<IResult> DownloadVideoAsync(ExtractRequest request, IYtDlpRunner runner, ILinkedInImageFetcher linkedInFetcher, IInstaloaderRunner instaloaderRunner, string downloadsDirectory)
    {
        if (string.IsNullOrWhiteSpace(request.Url) ||
            !Uri.TryCreate(request.Url, UriKind.Absolute, out var parsedUrl) ||
            (parsedUrl.Scheme != Uri.UriSchemeHttp && parsedUrl.Scheme != Uri.UriSchemeHttps))
        {
            return Results.BadRequest(new ErrorResponse("INVALID_URL", "The provided URL is missing or not a valid http(s) URL."));
        }

        string platform = DetectPlatform(parsedUrl);

        // Images have no separate audio stream to merge — just resolve and
        // hand back the same direct URL the preview step already found.
        if (platform == "LinkedIn")
        {
            var (imageUrl, _, error) = await linkedInFetcher.FetchAsync(request.Url);
            if (imageUrl == null)
            {
                return Results.BadRequest(new ErrorResponse("LINKEDIN_IMAGE_NOT_FOUND", error ?? "Could not find an image on this LinkedIn post."));
            }
            return Results.Ok(new DownloadResponse(imageUrl, "image"));
        }

        var cookiesPath = Environment.GetEnvironmentVariable("INSTAGRAM_COOKIES_PATH") ?? "/app/cookies.txt";
        var cookiesToUse = platform == "Instagram" && File.Exists(cookiesPath) ? cookiesPath : null;

        // Run metadata first to detect whether this is an image post or a video post.
        // Instagram reels → video path. Instagram photo posts → image path.
        var metaRun = await runner.RunAsync(request.Url, cookiesToUse);

        if (metaRun.StartError != null)
            return Results.Problem(
                detail: $"Could not start yt-dlp: {metaRun.StartError}",
                title: "YTDLP_NOT_FOUND",
                statusCode: StatusCodes.Status500InternalServerError);

        if (metaRun.TimedOut)
            return Results.Problem(
                detail: "The request timed out while reading media metadata.",
                title: "TIMEOUT",
                statusCode: StatusCodes.Status504GatewayTimeout);

        if (metaRun.ExitCode != 0)
        {
            if (platform == "Instagram" && metaRun.Stderr.Contains("no video formats found", StringComparison.OrdinalIgnoreCase))
            {
                var (fallbackResult, fallbackSuccess, _, _, fallbackUrl) =
                    await TryInstagramImageFallbackAsync(request.Url, instaloaderRunner, cookiesToUse, platform, downloadsDirectory);
                return fallbackSuccess
                    ? Results.Ok(new DownloadResponse(fallbackUrl!, "image"))
                    : fallbackResult;
            }
            var (errCode, errMsg) = ClassifyError(metaRun.Stderr);
            return Results.BadRequest(new ErrorResponse(errCode, errMsg));
        }

        try
        {
            // yt-dlp emits one JSON object per image for carousel posts,
            // separated by newlines.  Parse only the first entry.
            var firstLine = metaRun.Stdout.Trim().Split('\n')[0].Trim();
            using var doc = JsonDocument.Parse(firstLine);
            var root = doc.RootElement;

            if (IsImagePost(root))
            {
                // ── Image path ────────────────────────────────────────────
                string? imageUrl = PickImageUrl(root);

                if (string.IsNullOrEmpty(imageUrl))
                    return Results.BadRequest(new ErrorResponse("NO_PLAYABLE_URL", "Could not find image URL in yt-dlp output."));

                var imageFileName = $"{Guid.NewGuid()}.jpg";
                var imagePath = Path.Combine(downloadsDirectory, imageFileName);

                using var httpReq = new HttpRequestMessage(HttpMethod.Get, imageUrl);
                var imgHeaders = ExtractHeaders(root);
                if (imgHeaders != null)
                    foreach (var h in imgHeaders)
                        httpReq.Headers.TryAddWithoutValidation(h.Key, h.Value);

                var httpResp = await _httpClient.SendAsync(httpReq);
                httpResp.EnsureSuccessStatusCode();
                await File.WriteAllBytesAsync(imagePath, await httpResp.Content.ReadAsByteArrayAsync());

                return Results.Ok(new DownloadResponse($"/files/{imageFileName}", "image"));
            }
        }
        catch (JsonException) { /* fall through to video path */ }

        // ── Video path ────────────────────────────────────────────────────
        var fileName = $"{Guid.NewGuid()}.mp4";
        var outputPath = Path.Combine(downloadsDirectory, fileName);

        var run = await runner.DownloadAsync(request.Url, cookiesToUse, outputPath);

        if (run.StartError != null)
            return Results.Problem(
                detail: $"Could not start yt-dlp: {run.StartError}",
                title: "YTDLP_NOT_FOUND",
                statusCode: StatusCodes.Status500InternalServerError);

        if (run.TimedOut)
            return Results.Problem(
                detail: "The download took too long.",
                title: "TIMEOUT",
                statusCode: StatusCodes.Status504GatewayTimeout);

        if (run.ExitCode != 0 || !File.Exists(outputPath))
        {
            var (code, message) = ClassifyError(run.Stderr);
            return Results.BadRequest(new ErrorResponse(code, message));
        }

        return Results.Ok(new DownloadResponse($"/files/{fileName}", "video"));
    }

    public static async Task<IResult> DownloadAudioAsync(ExtractRequest request, IYtDlpRunner runner, string downloadsDirectory)
    {
        if (string.IsNullOrWhiteSpace(request.Url) ||
            !Uri.TryCreate(request.Url, UriKind.Absolute, out var parsedUrl) ||
            (parsedUrl.Scheme != Uri.UriSchemeHttp && parsedUrl.Scheme != Uri.UriSchemeHttps))
        {
            return Results.BadRequest(new ErrorResponse("INVALID_URL", "The provided URL is missing or not a valid http(s) URL."));
        }

        string platform = DetectPlatform(parsedUrl);

        if (platform == "LinkedIn")
            return Results.BadRequest(new ErrorResponse("UNSUPPORTED_PLATFORM", "Audio extraction is not supported for LinkedIn posts."));

        var cookiesPath = Environment.GetEnvironmentVariable("INSTAGRAM_COOKIES_PATH") ?? "/app/cookies.txt";
        var cookiesToUse = platform == "Instagram" && File.Exists(cookiesPath) ? cookiesPath : null;

        var fileName = $"{Guid.NewGuid()}.mp3";
        var outputPath = Path.Combine(downloadsDirectory, fileName);

        var run = await runner.AudioAsync(request.Url, cookiesToUse, outputPath);

        if (run.StartError != null)
            return Results.Problem(
                detail: $"Could not start yt-dlp: {run.StartError}",
                title: "YTDLP_NOT_FOUND",
                statusCode: StatusCodes.Status500InternalServerError);

        if (run.TimedOut)
            return Results.Problem(
                detail: "Audio extraction took too long.",
                title: "TIMEOUT",
                statusCode: StatusCodes.Status504GatewayTimeout);

        if (run.ExitCode != 0 || !File.Exists(outputPath))
        {
            var (code, message) = ClassifyError(run.Stderr);
            return Results.BadRequest(new ErrorResponse(code, message));
        }

        return Results.Ok(new DownloadResponse($"/files/{fileName}", "audio"));
    }

    private static async Task<(IResult result, bool success, string platform, string? title, string? thumbnail)> TryInstagramImageFallbackAsync(
        string postUrl, IInstaloaderRunner instaloaderRunner, string? cookiesPath, string platform, string downloadsDirectory)
    {
        var result = await instaloaderRunner.DownloadAsync(postUrl, cookiesPath);

        if (result.StartError != null)
            return (Results.Problem(
                detail: $"Could not start instaloader: {result.StartError}",
                title: "INSTALOADER_NOT_FOUND",
                statusCode: StatusCodes.Status500InternalServerError), false, platform, null, null);

        if (result.TimedOut)
            return (Results.Problem(
                detail: "instaloader did not respond in time.",
                title: "TIMEOUT",
                statusCode: StatusCodes.Status504GatewayTimeout), false, platform, null, null);

        if (result.ImagePath == null)
            return (Results.BadRequest(new ErrorResponse("INSTAGRAM_IMAGE_UNAVAILABLE",
                "This looks like an Instagram photo post, but it couldn't be retrieved. " +
                "It may need a fresh cookies.txt — try re-exporting it from your browser.")),
                false, platform, null, null);

        Directory.CreateDirectory(downloadsDirectory);
        var fileName = $"{Guid.NewGuid()}{Path.GetExtension(result.ImagePath)}";
        var destPath = Path.Combine(downloadsDirectory, fileName);
        File.Copy(result.ImagePath, destPath, overwrite: true);
        try { Directory.Delete(Path.GetDirectoryName(result.ImagePath)!, recursive: true); } catch { }

        var fileUrl = $"/files/{fileName}";
        return (Results.Ok(new ExtractResponse(fileUrl, "Instagram photo", fileUrl, "image", null)),
            true, platform, "Instagram photo", fileUrl);
    }

    static Dictionary<string, string>? ExtractHeaders(JsonElement element)
    {
        if (!element.TryGetProperty("http_headers", out var headersElement) || headersElement.ValueKind != JsonValueKind.Object)
            return null;

        var headers = new Dictionary<string, string>();
        foreach (var prop in headersElement.EnumerateObject())
        {
            // Only the two that actually gate access on these CDNs — passing
            // every yt-dlp-internal header through is unnecessary, and some
            // (like Accept-Encoding) can confuse a mobile HTTP client.
            if (prop.Name.Equals("Referer", StringComparison.OrdinalIgnoreCase) ||
                prop.Name.Equals("User-Agent", StringComparison.OrdinalIgnoreCase))
            {
                headers[prop.Name] = prop.Value.GetString() ?? "";
            }
        }
        return headers.Count > 0 ? headers : null;
    }

    internal static string DetectPlatform(Uri url)
    {
        var host = url.Host.ToLowerInvariant();
        if (host.Contains("tiktok")) return "TikTok";
        if (host.Contains("instagram")) return "Instagram";
        if (host.Contains("facebook") || host.Contains("fb.watch")) return "Facebook";
        if (host.Contains("twitter") || host.Contains("x.com")) return "X/Twitter";
        if (host.Contains("youtube") || host.Contains("youtu.be")) return "YouTube";
        if (host.Contains("linkedin")) return "LinkedIn";
        return "Unknown";
    }

    internal static (string code, string message) ClassifyError(string stderr)
    {
        var lower = stderr.ToLowerInvariant();

        if (lower.Contains("unsupported url"))
            return ("UNSUPPORTED_PLATFORM", "yt-dlp doesn't recognize this URL / platform.");

        if (lower.Contains("private") || lower.Contains("login required") || lower.Contains("sign in"))
            return ("VIDEO_PRIVATE", "This video is private or requires logging in to view.");

        if (lower.Contains("unavailable") || lower.Contains("removed") || lower.Contains("not found") || lower.Contains("404"))
            return ("VIDEO_UNAVAILABLE", "This video is unavailable or has been removed.");

        if (lower.Contains("certificate") || lower.Contains("ssl") || lower.Contains("connection aborted") || lower.Contains("forcibly closed"))
            return ("NETWORK_ERROR", "A network/SSL error occurred while reaching the platform.");

        var firstLine = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                              .FirstOrDefault(l => l.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase))
                       ?? stderr.Trim();

        return ("EXTRACTION_FAILED", firstLine.Length > 300 ? firstLine[..300] : firstLine);
    }
}