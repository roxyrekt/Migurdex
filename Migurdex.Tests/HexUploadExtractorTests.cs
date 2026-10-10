using Microsoft.Extensions.Logging;
using Migurdex.Core.Extractors;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Collections.Concurrent;
using System.Net;
using Xunit;

namespace Migurdex.Tests;

public sealed class HexUploadExtractorTests
{
    [Theory]
    [InlineData("https://hexupload.net/fixture-id?source=tranimeizle", "fixture-id")]
    [InlineData("https://www.hexupload.net/fixture-id#embed", "fixture-id")]
    [InlineData("https://hexload.com/fixture-id", "fixture-id")]
    public async Task ExtractAsync_ResolvesHexUploadNetAndHexloadLinks(string embedUrl, string expectedId)
    {
        var handler = new FixtureHandler();
        var bridge = new FixtureBridge(handler);
        var extractor = new HexUploadExtractor(bridge);

        var sources = await extractor.ExtractAsync(embedUrl, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(extractor.CanExtract(embedUrl));
        Assert.Equal(expectedId, handler.FormFields["id"]);
        Assert.Single(sources);
        Assert.Equal("https://cdn.example/video.mp4", sources[0].Url);
        Assert.Equal(VideoType.Mp4, sources[0].Type);
    }

    [Fact]
    public void CanExtract_RejectsLookalikeDomains()
    {
        var extractor = new HexUploadExtractor(new FixtureBridge(new FixtureHandler()));

        Assert.False(extractor.CanExtract("https://hexupload.net.attacker.example/fixture-id"));
        Assert.False(extractor.CanExtract("https://not-hexupload.net/fixture-id"));
    }

    private sealed class FixtureHandler : HttpMessageHandler
    {
        public ConcurrentDictionary<string, string> FormFields { get; } = new(StringComparer.Ordinal);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal("hexload.com", request.RequestUri!.Host);
            Assert.Equal("/download", request.RequestUri.AbsolutePath);
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=', 2);
                var key = Uri.UnescapeDataString(parts[0].Replace('+', ' '));
                var value = parts.Length > 1 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : string.Empty;
                FormFields[key] = value;
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"result":{"url":"https://cdn.example/video.mp4"}}""")
            };
        }
    }

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
