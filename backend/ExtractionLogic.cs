using System.Diagnostics;
using System.Text.Json;

public record YtDlpRunResult(int ExitCode, string Stdout, string Stderr, bool TimedOut, string? StartError);

public interface IYtDlpRunner
{
    Task<YtDlpRunResult> RunAsync(string url, string? cookiesPath);
}

public class YtDlpProcessRunner : IYtDlpRunner
{
    public async Task<YtDlpRunResult> RunAsync(string url, string? cookiesPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "yt-dlp",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        psi.ArgumentList.Add("-j");
        psi.ArgumentList.Add("--no-warnings");
        psi.ArgumentList.Add("--socket-timeout");
        psi.ArgumentList.Add("15");

        // YouTube's default format selection prefers separate video+audio
        // streams that need ffmpeg to merge — no single playable "url" in
        // that case. Force one pre-combined file instead.
        if (url.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) || url.Contains("youtu.be", StringComparison.OrdinalIgnoreCase))
        {
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("best[ext=mp4]/best");
        }

        if (!string.IsNullOrEmpty(cookiesPath))
        {
            psi.ArgumentList.Add("--cookies");
            psi.ArgumentList.Add(cookiesPath);
        }

        psi.ArgumentList.Add(url);

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

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
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
    internal static async Task<(IResult result, bool success, string platform, string? title, string? thumbnail)> RunExtractionAsync(ExtractRequest request, IYtDlpRunner runner, ILinkedInImageFetcher linkedInFetcher)
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

            return (Results.Ok(new ExtractResponse(imageUrl, liTitle ?? "LinkedIn image", imageUrl, "image")), true, platform, liTitle, imageUrl);
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
            var (code, message) = ClassifyError(run.Stderr);
            return (Results.BadRequest(new ErrorResponse(code, message)), false, platform, null, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(run.Stdout);
            var root = doc.RootElement;

            string title = root.TryGetProperty("title", out var t) ? t.GetString() ?? "Untitled" : "Untitled";
            string? thumbnail = root.TryGetProperty("thumbnail", out var th) ? th.GetString() : null;

            string? videoUrl = root.TryGetProperty("url", out var u) ? u.GetString() : null;

            if (string.IsNullOrEmpty(videoUrl) &&
                root.TryGetProperty("formats", out var formats) &&
                formats.ValueKind == JsonValueKind.Array &&
                formats.GetArrayLength() > 0)
            {
                var best = formats[formats.GetArrayLength() - 1];
                videoUrl = best.TryGetProperty("url", out var fu) ? fu.GetString() : null;
            }

            if (string.IsNullOrEmpty(videoUrl))
            {
                return (Results.BadRequest(new ErrorResponse("NO_PLAYABLE_URL", "yt-dlp returned metadata but no usable video URL was found.")), false, platform, title, thumbnail);
            }

            return (Results.Ok(new ExtractResponse(videoUrl, title, thumbnail, "video")), true, platform, title, thumbnail);
        }
        catch (JsonException)
        {
            return (Results.Problem(
                detail: "yt-dlp exited successfully but its output wasn't valid JSON.",
                title: "EXTRACTION_FAILED",
                statusCode: StatusCodes.Status500InternalServerError), false, platform, null, null);
        }
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