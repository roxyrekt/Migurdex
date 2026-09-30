using Migurdex.Cli.Services.Downloads;
using System.Net;
using Xunit;

namespace Migurdex.Tests;

public sealed class DownloadHttpValidateTests
{
    // C1: ValidateHttpUri yalnizca sema ve bos-olmayan host kontrol ediyordu.
    // Ozel IP / loopback adresleri hicbir yerde reddedilmiyordu.
    [Theory]
    [InlineData("http://127.0.0.1/video.mp4")]
    [InlineData("https://127.0.0.1:7045/api/v1/extractors/resolve")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://192.168.1.1/a.mp4")]
    [InlineData("http://10.0.0.5/a.mp4")]
    [InlineData("http://[::1]/video.mp4")]
    [InlineData("http://0.0.0.0/video.mp4")]
    [InlineData("http://localhost:8080/video.mp4")]
    public void ValidateHttpUri_RejectsPrivateAndLoopbackTargets(string url)
    {
        var uri = new Uri(url);
        var ex  = Assert.Throws<DownloadException>(() => DownloadHttp.ValidateHttpUri(uri));
        Assert.Equal("Bu host'a istek gönderilemez.", ex.Message);
    }

    [Theory]
    [InlineData("https://cdn.example/video.mp4")]
    [InlineData("http://8.8.8.8/video.mp4")]
    [InlineData("https://172.32.0.1/video.mp4")]
    [InlineData("https://100.128.0.1/video.mp4")]
    public void ValidateHttpUri_AllowsPublicTargets(string url)
    {
        DownloadHttp.ValidateHttpUri(new Uri(url));
    }

    [Theory]
    [InlineData("ftp://example.com/a.mp4")]
    [InlineData("file:///C:/Windows/System32/config/SAM")]
    public void ValidateHttpUri_StillRejectsNonHttpSchemes(string url)
    {
        var ex = Assert.Throws<DownloadException>(() => DownloadHttp.ValidateHttpUri(new Uri(url)));
        Assert.Equal("Yalnız HTTP ve HTTPS kaynakları desteklenir.", ex.Message);
    }

    [Fact]
    public async Task ValidateHttpUriAsync_AppliesSameSyncChecks()
    {
        await Assert.ThrowsAsync<DownloadException>(
            () => DownloadHttp.ValidateHttpUriAsync(new Uri("http://127.0.0.1/a.mp4"),
                                                    TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<DownloadException>(
            () => DownloadHttp.ValidateHttpUriAsync(new Uri("ftp://example.com/a.mp4"),
                                                    TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SendWithRedirectsAsync_RejectsRedirectToPrivateAddress()
    {
        // Yonlendirme hedefi de korumadan gecmeli: uzak sunucu 30x ile CLI'yi
        // yerel servise yonlendirebilirdi.
        var handler = new RedirectToPrivateHandler();
        using var client = new HttpClient(handler);

        // Reddedilmek için istediğimiz şey: DownloadException, 2. istek yapılmadan.
        var ex = await Assert.ThrowsAsync<DownloadException>(
            () => DownloadHttp.SendWithRedirectsAsync(client,
                                                     new Uri("https://cdn.example/start"),
                                                     null,
                                                     TestContext.Current.CancellationToken));

        Assert.Equal("Bu host'a istek gönderilemez.", ex.Message);
        Assert.Equal(1, handler.RedirectsServed); // yalnız ilk istek yapıldı
    }

    private sealed class RedirectToPrivateHandler : HttpMessageHandler
    {
        public int RedirectsServed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RedirectsServed++;
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri("http://169.254.169.254/latest/meta-data/");
            return Task.FromResult(response);
        }
    }
}
