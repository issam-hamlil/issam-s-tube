using Microsoft.AspNetCore.Mvc.Testing;
using System.Net;
using System.Net.Http.Json;
using Xunit;

public class ExtractEndpointIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ExtractEndpointIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Extract_InvalidUrl_Returns400()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/extract", new { url = "not-a-url" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Extract_KnownStableVideo_Returns200()
    {
        // Replace with a real URL you control or know is stable — e.g. one of
        // your own confirmed-working Facebook/X links from earlier testing.
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/extract", new { url = "https://x.com/i/status/2069866804776784165" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ExtractResponse>();
        Assert.False(string.IsNullOrEmpty(body!.VideoUrl));
    }
}