using Microsoft.Extensions.Logging;
using Migurdex.Core.Extractors;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Collections.Concurrent;
using System.Net;
using Xunit;

namespace Migurdex.Tests;

public sealed class AitrVipExtractorTests
{
    private const string EmbedUrl = "https://optraco.top/explorer/fixture";
    private const string SiteUrl  = "https://www.tranimeizle.live/";

    [Fact]
    public async Task ExtractAsync_ForwardsSiteRefererAndPreservesSignedPlaylistQuery()
    {
        const string playlistUrl = "https://cdn.example/video/master.m3u8?token=fixture-token&expires=123";
        var handler = new FixtureHandler(EmbedUrl, $"<script>file: '{playlistUrl}'</script>");
        var extractor = CreateExtractor(handler);

        var sources = await extractor.ExtractAsync(EmbedUrl, new Dictionary<string, string>
        {
            ["Referer"] = SiteUrl
        }, TestContext.Current.CancellationToken);

        Assert.Single(sources);
        Assert.Equal(playlistUrl, sources[0].Url);
        Assert.Equal(SiteUrl, handler.Requests.Single(request => request.Url == EmbedUrl).Referer);
        Assert.Equal(EmbedUrl, handler.Requests.Single(request => request.Url == playlistUrl).Referer);
    }

    [Fact]
    public async Task ExtractAsync_ResolvesRootRelativePlaylistUrl()
    {
        const string playlistUrl = "https://optraco.top/media/master.m3u8?token=relative-token";
        var handler = new FixtureHandler(EmbedUrl, "<script>\"file\": \"/media/master.m3u8?token=relative-token\"</script>");
        var extractor = CreateExtractor(handler);

        var sources = await extractor.ExtractAsync(EmbedUrl, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(sources);
        Assert.Equal(playlistUrl, sources[0].Url);
        Assert.Contains(handler.Requests, request => request.Url == playlistUrl);
    }

    private static AitrVipExtractor CreateExtractor(FixtureHandler handler)
    {
        var bridge = new FixtureBridge(handler);
        var playlistExtractor = new M3U8PlaylistExtractor(bridge, bridge.CreateLogger<M3U8PlaylistExtractor>());
        return new AitrVipExtractor(playlistExtractor, bridge);
    }

    private sealed class FixtureHandler(string embedUrl, string embedHtml) : HttpMessageHandler
    {
        public ConcurrentQueue<RequestSnapshot> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var referer = request.Headers.Referrer?.ToString();
            Requests.Enqueue(new RequestSnapshot(request.RequestUri!.AbsoluteUri, referer));

            if (request.RequestUri.AbsoluteUri == embedUrl)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(embedHtml)
                });
            }

            if (request.RequestUri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("#EXTM3U\n#EXTINF:8,\nsegment.ts\n#EXT-X-ENDLIST")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed record RequestSnapshot(string Url, string? Referer);

    private sealed class FixtureBridge(HttpMessageHandler handler) : ISharedBridge
    {
        private readonly ILoggerFactory _loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning));

        public IMp4MetadataReader MetadataReader => throw new NotSupportedException();
        public ILoggerFactory LoggerFactory => _loggerFactory;

        public HttpClient CreateHttpClient(HttpClientOptions? options = null) => new(handler, disposeHandler: false);

        public HttpClient CreateHttpClient(Action<HttpClientOptions> configure) => new(handler, disposeHandler: false);

        public ILogger<T> CreateLogger<T>() => _loggerFactory.CreateLogger<T>();
    }
}
