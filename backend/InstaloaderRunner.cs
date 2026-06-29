using System.Diagnostics;
using System.Text.RegularExpressions;

public record InstaloaderResult(int ExitCode, string Stdout, string Stderr, List<string>? ImagePaths, string? StartError, bool TimedOut);

public interface IInstaloaderRunner
{
    Task<InstaloaderResult> DownloadAsync(string postUrl, string? cookiesPath);
}

public class InstaloaderRunner : IInstaloaderRunner
{
    public async Task<InstaloaderResult> DownloadAsync(string postUrl, string? cookiesPath)
    {
        var shortcode = ExtractShortcode(postUrl);
        if (shortcode == null)
            return new InstaloaderResult(-1, "", "Could not find a post shortcode in this URL.", null, null, false);

        var workDir = Path.Combine(Path.GetTempPath(), "instaloader-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);

        var psi = new ProcessStartInfo
        {
            FileName = "gallery-dl",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        psi.ArgumentList.Add("--directory");
        psi.ArgumentList.Add(workDir);

        if (!string.IsNullOrEmpty(cookiesPath) && File.Exists(cookiesPath))
        {
            psi.ArgumentList.Add("--cookies");
            psi.ArgumentList.Add(cookiesPath);
        }

        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add(postUrl);

        using var process = new Process { StartInfo = psi };

        try { process.Start(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new InstaloaderResult(-1, "", "", null, ex.Message, false);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return new InstaloaderResult(-1, "", "", null, null, true);
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        List<string>? imagePaths = null;
        if (Directory.Exists(workDir))
        {
            imagePaths = Directory.GetFiles(workDir, "*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f)
                .ToList();
                
            if (imagePaths.Count == 0) imagePaths = null;
        }

        return new InstaloaderResult(process.ExitCode, stdout, stderr, imagePaths, null, false);
    }

    private static string? ExtractShortcode(string url)
    {
        var match = Regex.Match(url, @"instagram\.com/(?:p|reel|reels)/([A-Za-z0-9_-]+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }
}
