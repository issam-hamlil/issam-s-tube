using Moq;
using Xunit;

public class ExtractionLogicTests
{
    [Fact]
    public async Task InvalidUrl_ReturnsBadRequest_WithoutCallingRunner()
    {
        var mockRunner = new Mock<IYtDlpRunner>();
        var request = new ExtractRequest("not-a-url");

        var (result, success, platform, _, _) = await ExtractionLogic.RunExtractionAsync(request, mockRunner.Object, Mock.Of<ILinkedInImageFetcher>(), Mock.Of<IInstaloaderRunner>());

        Assert.False(success);
        Assert.Equal("Unknown", platform);
        mockRunner.Verify(r => r.RunAsync(It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task SuccessfulExtraction_UsesTopLevelUrl()
    {
        var mockRunner = new Mock<IYtDlpRunner>();
        mockRunner
            .Setup(r => r.RunAsync(It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync(new YtDlpRunResult(0,
                "{\"title\":\"Cool clip\",\"thumbnail\":\"https://cdn.example.com/t.jpg\",\"url\":\"https://cdn.example.com/v.mp4\"}",
                "", false, null));

        var request = new ExtractRequest("https://www.tiktok.com/@user/video/123");

        var (result, success, platform, title, _) = await ExtractionLogic.RunExtractionAsync(request, mockRunner.Object, Mock.Of<ILinkedInImageFetcher>(), Mock.Of<IInstaloaderRunner>());

        Assert.True(success);
        Assert.Equal("TikTok", platform);
        Assert.Equal("Cool clip", title);
    }

    [Fact]
    public async Task SuccessfulExtraction_FallsBackToFormatsArray()
    {
        var mockRunner = new Mock<IYtDlpRunner>();
        mockRunner
            .Setup(r => r.RunAsync(It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync(new YtDlpRunResult(0,
                "{\"title\":\"Reel\",\"formats\":[{\"url\":\"https://cdn.example.com/low.mp4\"},{\"url\":\"https://cdn.example.com/high.mp4\"}]}",
                "", false, null));

        var request = new ExtractRequest("https://www.instagram.com/reel/abc/");

        var (result, success, _, _, _) = await ExtractionLogic.RunExtractionAsync(request, mockRunner.Object, Mock.Of<ILinkedInImageFetcher>(), Mock.Of<IInstaloaderRunner>());

        Assert.True(success);
        var ok = Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.Ok<ExtractResponse>>(result);
        Assert.Equal("https://cdn.example.com/high.mp4", ok.Value!.VideoUrl);
    }

    [Fact]
    public async Task NonZeroExit_PrivateMessage_ClassifiesAsVideoPrivate()
    {
        var mockRunner = new Mock<IYtDlpRunner>();
        mockRunner
            .Setup(r => r.RunAsync(It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync(new YtDlpRunResult(1, "", "ERROR: This video is private", false, null));

        var request = new ExtractRequest("https://www.instagram.com/reel/abc/");

        var (result, success, _, _, _) = await ExtractionLogic.RunExtractionAsync(request, mockRunner.Object, Mock.Of<ILinkedInImageFetcher>(), Mock.Of<IInstaloaderRunner>());

        Assert.False(success);
        var badRequest = Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.BadRequest<ErrorResponse>>(result);
        Assert.Equal("VIDEO_PRIVATE", badRequest.Value!.ErrorCode);
    }

    [Fact]
    public async Task InstagramUrl_PassesCookiesPath_WhenFileExists()
    {
        var tempCookiesFile = Path.GetTempFileName();
        Environment.SetEnvironmentVariable("INSTAGRAM_COOKIES_PATH", tempCookiesFile);

        var mockRunner = new Mock<IYtDlpRunner>();
        mockRunner
            .Setup(r => r.RunAsync(It.IsAny<string>(), tempCookiesFile))
            .ReturnsAsync(new YtDlpRunResult(0, "{\"title\":\"x\",\"url\":\"https://cdn.example.com/v.mp4\"}", "", false, null));

        var request = new ExtractRequest("https://www.instagram.com/reel/abc/");

        var (_, success, _, _, _) = await ExtractionLogic.RunExtractionAsync(request, mockRunner.Object, Mock.Of<ILinkedInImageFetcher>(), Mock.Of<IInstaloaderRunner>());

        Assert.True(success);
        mockRunner.Verify(r => r.RunAsync(request.Url, tempCookiesFile), Times.Once);

        File.Delete(tempCookiesFile);
        Environment.SetEnvironmentVariable("INSTAGRAM_COOKIES_PATH", null);
    }

    [Theory]
    [InlineData("https://www.tiktok.com/@x/video/1", "TikTok")]
    [InlineData("https://www.instagram.com/reel/x/", "Instagram")]
    [InlineData("https://www.facebook.com/watch/?v=1", "Facebook")]
    [InlineData("https://x.com/i/status/1", "X/Twitter")]
    [InlineData("https://example.com/", "Unknown")]
    public void DetectPlatform_MatchesExpectedPlatform(string url, string expected)
    {
        Assert.Equal(expected, ExtractionLogic.DetectPlatform(new Uri(url)));
    }

    [Theory]
    [InlineData("ERROR: Unsupported URL: https://example.com", "UNSUPPORTED_PLATFORM")]
    [InlineData("ERROR: This video is private", "VIDEO_PRIVATE")]
    [InlineData("ERROR: Video unavailable", "VIDEO_UNAVAILABLE")]
    [InlineData("SSL: CERTIFICATE_VERIFY_FAILED", "NETWORK_ERROR")]
    [InlineData("ERROR: something brand new", "EXTRACTION_FAILED")]
    public void ClassifyError_MatchesExpectedCode(string stderr, string expectedCode)
    {
        var (code, _) = ExtractionLogic.ClassifyError(stderr);
        Assert.Equal(expectedCode, code);
    }

    [Fact]
    public async Task LinkedInUrl_ReturnsImageFromFetcher_WithoutCallingYtDlp()
    {
        var mockRunner = new Mock<IYtDlpRunner>();
        var mockFetcher = new Mock<ILinkedInImageFetcher>();
        mockFetcher
            .Setup(f => f.FetchAsync(It.IsAny<string>()))
            .ReturnsAsync(("https://media.licdn.com/dms/image/abc.jpg", "A nice post", (string?)null));

        var request = new ExtractRequest("https://www.linkedin.com/posts/someone_abc-activity-123/");

        var (result, success, platform, _, _) = await ExtractionLogic.RunExtractionAsync(request, mockRunner.Object, mockFetcher.Object, Mock.Of<IInstaloaderRunner>());

        Assert.True(success);
        Assert.Equal("LinkedIn", platform);
        var ok = Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.Ok<ExtractResponse>>(result);
        Assert.Equal("https://media.licdn.com/dms/image/abc.jpg", ok.Value!.VideoUrl);
        Assert.Equal("image", ok.Value!.MediaType);
        mockRunner.Verify(r => r.RunAsync(It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task LinkedInUrl_NoImageFound_ReturnsError()
    {
        var mockRunner = new Mock<IYtDlpRunner>();
        var mockFetcher = new Mock<ILinkedInImageFetcher>();
        mockFetcher
            .Setup(f => f.FetchAsync(It.IsAny<string>()))
            .ReturnsAsync(((string?)null, (string?)null, "No og:image tag found"));

        var request = new ExtractRequest("https://www.linkedin.com/posts/someone_abc-activity-123/");

        var (result, success, _, _, _) = await ExtractionLogic.RunExtractionAsync(request, mockRunner.Object, mockFetcher.Object, Mock.Of<IInstaloaderRunner>());

        Assert.False(success);
        var badRequest = Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.BadRequest<ErrorResponse>>(result);
        Assert.Equal("LINKEDIN_IMAGE_NOT_FOUND", badRequest.Value!.ErrorCode);
    }

    [Fact]
    public async Task InstagramNoFormats_FallsBackToInstaloader()
    {
        var mockRunner = new Mock<IYtDlpRunner>();
        mockRunner
            .Setup(r => r.RunAsync(It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync(new YtDlpRunResult(1, "", "ERROR: [Instagram] abc: No video formats found!", false, null));

        var mockInstaloader = new Mock<IInstaloaderRunner>();
        var tempFile = Path.GetTempFileName() + ".jpg";
        File.WriteAllBytes(tempFile, new byte[] { 0xFF, 0xD8 }); // minimal fake JPEG bytes
        mockInstaloader
            .Setup(r => r.DownloadAsync(It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync(new InstaloaderResult(0, "", "", tempFile, null, false));

        var request = new ExtractRequest("https://www.instagram.com/p/DaHKUWNjhjR/");

        var (result, success, platform, _, _) = await ExtractionLogic.RunExtractionAsync(
            request, mockRunner.Object, Mock.Of<ILinkedInImageFetcher>(), mockInstaloader.Object);

        Assert.True(success);
        Assert.Equal("Instagram", platform);
        var ok = Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.Ok<ExtractResponse>>(result);
        Assert.Equal("image", ok.Value!.MediaType);

        File.Delete(tempFile);
    }
}