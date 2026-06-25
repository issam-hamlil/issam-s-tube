using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;

var builder = WebApplication.CreateBuilder(args);

// Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

app.MapGet("/", () => "Issam's Tube backend is running");

app.MapPost("/extract", async (ExtractRequest request) =>
{
    if (string.IsNullOrWhiteSpace(request.Url) ||
        !Uri.TryCreate(request.Url, UriKind.Absolute, out var parsedUrl) ||
        (parsedUrl.Scheme != Uri.UriSchemeHttp && parsedUrl.Scheme != Uri.UriSchemeHttps))
    {
        return Results.BadRequest(new ErrorResponse("INVALID_URL", "The provided URL is missing or not a valid http(s) URL."));
    }

    var cookiesPath = Environment.GetEnvironmentVariable("INSTAGRAM_COOKIES_PATH") ?? "/app/cookies.txt";

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

    if (parsedUrl.Host.Contains("instagram.com", StringComparison.OrdinalIgnoreCase) && File.Exists(cookiesPath))
    {
        psi.ArgumentList.Add("--cookies");
        psi.ArgumentList.Add(cookiesPath);
    }

    psi.ArgumentList.Add(request.Url);

    using var process = new Process { StartInfo = psi };

    try
    {
        process.Start();
    }
    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
    {
        return Results.Problem(
            detail: $"Could not start yt-dlp: {ex.Message}",
            title: "YTDLP_NOT_FOUND",
            statusCode: StatusCodes.Status500InternalServerError);
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
        return Results.Problem(
            detail: "yt-dlp did not respond in time.",
            title: "TIMEOUT",
            statusCode: StatusCodes.Status504GatewayTimeout);
    }

    var stdout = await stdoutTask;
    var stderr = await stderrTask;

    if (process.ExitCode != 0)
    {
        var (code, message) = ClassifyError(stderr);
        return Results.BadRequest(new ErrorResponse(code, message));
    }

    try
    {
        using var doc = JsonDocument.Parse(stdout);
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
            return Results.BadRequest(new ErrorResponse("NO_PLAYABLE_URL", "yt-dlp returned metadata but no usable video URL was found."));

        return Results.Ok(new ExtractResponse(videoUrl, title, thumbnail));
    }
    catch (JsonException)
    {
        return Results.Problem(
            detail: "yt-dlp exited successfully but its output wasn't valid JSON.",
            title: "EXTRACTION_FAILED",
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.Run();

static (string code, string message) ClassifyError(string stderr)
{
    var lower = (stderr ?? string.Empty).ToLowerInvariant();

    if (lower.Contains("unsupported url"))
        return ("UNSUPPORTED_PLATFORM", "yt-dlp doesn't recognize this URL / platform.");

    if (lower.Contains("private") || lower.Contains("login required") || lower.Contains("sign in"))
        return ("VIDEO_PRIVATE", "This video is private or requires logging in to view.");

    if (lower.Contains("unavailable") || lower.Contains("removed") || lower.Contains("not found") || lower.Contains("404"))
        return ("VIDEO_UNAVAILABLE", "This video is unavailable or has been removed.");

    if (lower.Contains("certificate") || lower.Contains("ssl") || lower.Contains("connection aborted") || lower.Contains("forcibly closed"))
        return ("NETWORK_ERROR", "A network/SSL error occurred while reaching the platform.");

    var firstLine = (stderr ?? string.Empty)
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .FirstOrDefault(l => l.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase))
        ?? (stderr ?? string.Empty).Trim();

    if (firstLine.Length > 300)
        firstLine = firstLine[..300];

    return ("EXTRACTION_FAILED", firstLine);
}

record ExtractRequest(string Url);

record ErrorResponse(
    [property: JsonPropertyName("error_code")] string ErrorCode,
    [property: JsonPropertyName("message")] string Message);

record ExtractResponse(
    [property: JsonPropertyName("video_url")] string VideoUrl,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("thumbnail")] string? Thumbnail);