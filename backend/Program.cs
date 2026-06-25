using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Serilog;

var dbPath = Environment.GetEnvironmentVariable("DB_PATH") ?? "history.db";
var logPath = Environment.GetEnvironmentVariable("LOG_PATH") ?? "logs/issamstube-.log";

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, configuration) => configuration
    .MinimumLevel.Information()
    .WriteTo.Console()
    .WriteTo.File(
        path: logPath,
        rollingInterval: RollingInterval.Day,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}"));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite($"Data Source={dbPath}"));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

app.UseSwagger();
app.UseSwaggerUI();

app.MapGet("/", () => "Issam's Tube backend is running");

app.MapGet("/history", async (AppDbContext db) =>
{
    var recent = await db.History
        .OrderByDescending(h => h.Timestamp)
        .Take(50)
        .ToListAsync();
    return Results.Ok(recent);
});

app.MapPost("/extract", async (ExtractRequest request, AppDbContext db) =>
{
    var stopwatch = Stopwatch.StartNew();
    var (result, success, platform, title, thumbnail) = await RunExtractionAsync(request);
    stopwatch.Stop();

    var urlForLog = !string.IsNullOrEmpty(request.Url) && request.Url.Length > 60
        ? request.Url[..60] + "..."
        : request.Url;

    Log.Information("Extraction {Platform} {Success} in {DurationMs}ms — {UrlTruncated}",
        platform, success, stopwatch.ElapsedMilliseconds, urlForLog);

    db.History.Add(new DownloadHistory
    {
        Url = request.Url ?? "",
        Platform = platform,
        Title = title,
        Thumbnail = thumbnail,
        Timestamp = DateTime.UtcNow,
        Success = success
    });
    await db.SaveChangesAsync();

    return result;
});

app.Run();

static async Task<(IResult result, bool success, string platform, string? title, string? thumbnail)> RunExtractionAsync(ExtractRequest request)
{
    if (string.IsNullOrWhiteSpace(request.Url) ||
        !Uri.TryCreate(request.Url, UriKind.Absolute, out var parsedUrl) ||
        (parsedUrl.Scheme != Uri.UriSchemeHttp && parsedUrl.Scheme != Uri.UriSchemeHttps))
    {
        return (Results.BadRequest(new ErrorResponse("INVALID_URL", "The provided URL is missing or not a valid http(s) URL.")), false, "Unknown", null, null);
    }

    string platform = DetectPlatform(parsedUrl);

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
        return (Results.Problem(
            detail: $"Could not start yt-dlp: {ex.Message}",
            title: "YTDLP_NOT_FOUND",
            statusCode: StatusCodes.Status500InternalServerError), false, platform, null, null);
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
        return (Results.Problem(
            detail: "yt-dlp did not respond in time.",
            title: "TIMEOUT",
            statusCode: StatusCodes.Status504GatewayTimeout), false, platform, null, null);
    }

    var stdout = await stdoutTask;
    var stderr = await stderrTask;

    if (process.ExitCode != 0)
    {
        var (code, message) = ClassifyError(stderr);
        return (Results.BadRequest(new ErrorResponse(code, message)), false, platform, null, null);
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
        {
            return (Results.BadRequest(new ErrorResponse("NO_PLAYABLE_URL", "yt-dlp returned metadata but no usable video URL was found.")), false, platform, title, thumbnail);
        }

        return (Results.Ok(new ExtractResponse(videoUrl, title, thumbnail)), true, platform, title, thumbnail);
    }
    catch (JsonException)
    {
        return (Results.Problem(
            detail: "yt-dlp exited successfully but its output wasn't valid JSON.",
            title: "EXTRACTION_FAILED",
            statusCode: StatusCodes.Status500InternalServerError), false, platform, null, null);
    }
}

static string DetectPlatform(Uri url)
{
    var host = url.Host.ToLowerInvariant();
    if (host.Contains("tiktok")) return "TikTok";
    if (host.Contains("instagram")) return "Instagram";
    if (host.Contains("facebook") || host.Contains("fb.watch")) return "Facebook";
    if (host.Contains("twitter") || host.Contains("x.com")) return "X/Twitter";
    return "Unknown";
}

static (string code, string message) ClassifyError(string stderr)
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

record ExtractRequest(string Url);

record ExtractResponse(
    [property: JsonPropertyName("video_url")] string VideoUrl,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("thumbnail")] string? Thumbnail);

record ErrorResponse(
    [property: JsonPropertyName("error_code")] string ErrorCode,
    [property: JsonPropertyName("message")] string Message);

class DownloadHistory
{
    public int Id { get; set; }
    public string Url { get; set; } = "";
    public string Platform { get; set; } = "";
    public string? Title { get; set; }
    public string? Thumbnail { get; set; }
    public DateTime Timestamp { get; set; }
    public bool Success { get; set; }
}

class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }
    public DbSet<DownloadHistory> History => Set<DownloadHistory>();
}