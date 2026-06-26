using System.Net.Http;
using System.Text.RegularExpressions;

public interface ILinkedInImageFetcher
{
    Task<(string? imageUrl, string? title, string? error)> FetchAsync(string url);
}

public class LinkedInImageFetcher : ILinkedInImageFetcher
{
    private readonly HttpClient _httpClient;

    public LinkedInImageFetcher(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<(string? imageUrl, string? title, string? error)> FetchAsync(string url)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                return (null, null, $"LinkedIn returned {(int)response.StatusCode} — likely blocked the request or the post requires login.");
            }

            var html = await response.Content.ReadAsStringAsync();

            var imageUrl = ExtractMetaTagContent(html, "og:image");
            var title = ExtractMetaTagContent(html, "og:title");

            if (string.IsNullOrEmpty(imageUrl))
            {
                return (null, null, "No og:image tag found — this post may require login, or LinkedIn served a different page than expected.");
            }

            return (imageUrl, title, null);
        }
        catch (Exception ex)
        {
            return (null, null, ex.Message);
        }
    }

    private static string? ExtractMetaTagContent(string html, string property)
    {
        var pattern = $"<meta[^>]*property=[\"']{Regex.Escape(property)}[\"'][^>]*content=[\"']([^\"']+)[\"']";
        var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase);
        if (match.Success) return System.Net.WebUtility.HtmlDecode(match.Groups[1].Value);

        // Attribute order sometimes flips (content before property)
        pattern = $"<meta[^>]*content=[\"']([^\"']+)[\"'][^>]*property=[\"']{Regex.Escape(property)}[\"']";
        match = Regex.Match(html, pattern, RegexOptions.IgnoreCase);
        return match.Success ? System.Net.WebUtility.HtmlDecode(match.Groups[1].Value) : null;
    }
}
