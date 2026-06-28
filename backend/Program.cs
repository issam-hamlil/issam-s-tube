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

builder.Services.AddSingleton<IYtDlpRunner, YtDlpProcessRunner>();
builder.Services.AddHttpClient<ILinkedInImageFetcher, LinkedInImageFetcher>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

app.UseSwagger();
app.UseSwaggerUI();

app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/swagger") || context.Request.Path == "/")
    {
        await next();
        return;
    }

    var apiKey = Environment.GetEnvironmentVariable("API_KEY");
    if (!string.IsNullOrEmpty(apiKey))
    {
        if (!context.Request.Headers.TryGetValue("X-Api-Key", out var providedKey) || providedKey != apiKey)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Unauthorized");
            return;
        }
    }

    await next();
});

var downloadsPath = Path.Combine(AppContext.BaseDirectory, "downloads");
Directory.CreateDirectory(downloadsPath);
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(downloadsPath),
    RequestPath = "/files"
});

app.MapGet("/", () => "Issam's Tube backend is running");

app.MapGet("/history", async (AppDbContext db) =>
{
    var recent = await db.History
        .OrderByDescending(h => h.Timestamp)
        .Take(50)
        .ToListAsync();
    return Results.Ok(recent);
});

app.MapPost("/download", async (ExtractRequest request, IYtDlpRunner runner, ILinkedInImageFetcher linkedInFetcher) =>
{
    var downloadsDir = Path.Combine(AppContext.BaseDirectory, "downloads");
    Directory.CreateDirectory(downloadsDir);

    // Bounds disk usage without a background job — sweep anything older
    // than an hour before creating the new file.
    foreach (var file in Directory.GetFiles(downloadsDir))
    {
        if (DateTime.UtcNow - File.GetCreationTimeUtc(file) > TimeSpan.FromHours(1))
        {
            try { File.Delete(file); } catch { /* best effort */ }
        }
    }

    return await ExtractionLogic.DownloadVideoAsync(request, runner, linkedInFetcher, downloadsDir);
});

app.MapPost("/extract", async (ExtractRequest request, AppDbContext db, IYtDlpRunner runner, ILinkedInImageFetcher linkedInFetcher) =>
{
    var stopwatch = Stopwatch.StartNew();
    var (result, success, platform, title, thumbnail) = await ExtractionLogic.RunExtractionAsync(request, runner, linkedInFetcher);
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


record ExtractRequest(string Url);

record ExtractResponse(
    [property: JsonPropertyName("video_url")] string VideoUrl,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("thumbnail")] string? Thumbnail,
    [property: JsonPropertyName("media_type")] string MediaType,
    [property: JsonPropertyName("headers")] Dictionary<string, string>? Headers);

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

public partial class Program { }
