using Microsoft.Extensions.Logging;
using Migurdex.Plugins.TrAnimeIzle;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Xunit;

namespace Migurdex.Tests;

public sealed class TrAnimeIzleProviderSourceTests
{
    private static readonly string[] _embedUrls =
    [
        "https://optraco.top/explorer/aitrvip-1",
        "https://optraco.top/explorer/aitrvip-2",
        "https://filemoon.sx/e/filemoon-1",
        "https://filemoon.sx/e/filemoon-2",
        "https://filemoon.sx/e/filemoon-3",
        "https://hexupload.net/hexupload-1",
        "https://hexupload.net/hexupload-2",
        "https://hexupload.net/hexupload-3",
        "https://hexupload.net/hexupload-4",
        "https://mega.nz/embed/mega-1#key1",
        "https://mega.nz/embed/mega-2#key2",
        "https://mixdrop.co/e/mixdrop-1",
        "https://mixdrop.co/e/mixdrop-2",
        "https://mixdrop.co/e/mixdrop-3",
        "https://mixdrop.co/e/mixdrop-4",
        "https://mixdrop.co/e/mixdrop-5",
        "https://odnoklassniki.ru/videoembed/okru-1",
        "https://odnoklassniki.ru/videoembed/okru-2",
        "https://odnoklassniki.ru/videoembed/okru-3",
        "https://odnoklassniki.ru/videoembed/okru-4",
        "https://video.sibnet.ru/shell.php?videoid=140001",
        "https://video.sibnet.ru/shell.php?videoid=140002",
        "https://vidmoly.me/embed-vidmoly-1.html",
        "https://vidmoly.me/embed-vidmoly-2.html",
        "https://vidmoly.me/embed-vidmoly-3.html",
        "https://vidmoly.me/embed-vidmoly-4.html",
        "https://vidmoly.me/embed-vidmoly-5.html"
    ];

    [Fact]
    public async Task GetVideoSourcesAsync_ReturnsEveryPlayerFromFansubSourceList()
    {
        var handler = new SourceApiFixtureHandler(_embedUrls);
        var bridge = new FixtureBridge(handler);
        var provider = new TrAnimeIzleProvider(bridge, bridge.CreateLogger<TrAnimeIzleProvider>());

        var sources = await provider.GetVideoSourcesAsync("oshi-no-ko-1-bolum-izle",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(_embedUrls.Length, sources.Count);
        Assert.Equal(_embedUrls.Order(StringComparer.Ordinal), sources.Select(source => source.Url).Order(StringComparer.Ordinal));
        Assert.Equal(_embedUrls.Length, handler.SourcePlayerRequests);
        Assert.All(sources, source => Assert.Equal("HolySubs", source.Group));
        Assert.InRange(handler.MaxConcurrentPlayerRequests, 2, 4);
    }

    [Fact]
    public async Task GetVideoSourcesAsync_ParsesAttributeOrderSingleQuotesAndRelativeIframeUrls()
    {
        var handler = new SourceApiFixtureHandler(
            ["https://www.tranimeizle.live/media/episode.m3u8?token=a&expires=2"],
            watchPageHtml: "<input value='42' name='EpisodeId' id='EpisodeId' />"
                           + "<button class='fansubSelector' data-fad='HolySubs' data-fid='7'>HolySubs</button>",
            iframeTemplate: "<IFRAME SRC='/media/episode.m3u8?token=a&amp;expires=2'></IFRAME>");
        var bridge = new FixtureBridge(handler);
        var provider = new TrAnimeIzleProvider(bridge, bridge.CreateLogger<TrAnimeIzleProvider>());

        var sources = await provider.GetVideoSourcesAsync("episode-1-bolum-izle",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("https://www.tranimeizle.live/media/episode.m3u8?token=a&expires=2", Assert.Single(sources).Url);
        Assert.Equal(1, handler.SourcePlayerRequests);
    }

    [Fact]
    public async Task GetVideoSourcesAsync_SkipsMissingIframeAndContinuesResolvingOtherPlayers()
    {
        var handler = new SourceApiFixtureHandler(
            ["https://valid.example/embed/1", "https://valid.example/embed/2"],
            playerHtmlFactory: (index, url) => index == 1 ? "<div>player unavailable</div>" : $"<iframe src=\"{url}\"></iframe>");
        var bridge = new FixtureBridge(handler);
        var provider = new TrAnimeIzleProvider(bridge, bridge.CreateLogger<TrAnimeIzleProvider>());

        var sources = await provider.GetVideoSourcesAsync("oshi-no-ko-1-bolum-izle",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("https://valid.example/embed/2", Assert.Single(sources).Url);
        Assert.Equal(2, handler.SourcePlayerRequests);
    }

    [Fact]
    public async Task GetVideoSourcesAsync_ReturnsEveryPlayerAcrossEpisodesAndFansubGroups()
    {
        var pages = new Dictionary<string, EpisodeFixture>(StringComparer.Ordinal)
        {
            ["anime-a-1-bolum-izle"] = new(101,
            [
                new(7, "Group A", ["https://optraco.top/explorer/a", "https://mega.nz/embed/a#key"]),
                new(8, "Group B", ["https://filemoon.sx/e/b"])
            ]),
            ["anime-b-4-bolum-izle"] = new(204,
            [
                new(9, "Group C", ["https://video.sibnet.ru/shell.php?videoid=204"])
            ])
        };
        var handler = new MultiEpisodeFixtureHandler(pages);
        var bridge = new FixtureBridge(handler);
        var provider = new TrAnimeIzleProvider(bridge, bridge.CreateLogger<TrAnimeIzleProvider>());

        var firstEpisode = await provider.GetVideoSourcesAsync("anime-a-1-bolum-izle",
            cancellationToken: TestContext.Current.CancellationToken);
        var secondEpisode = await provider.GetVideoSourcesAsync("anime-b-4-bolum-izle",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["Group A", "Group A", "Group B"], firstEpisode.Select(source => source.Group));
        Assert.Equal("Group C", Assert.Single(secondEpisode).Group);
        Assert.Equal(4, handler.SourcePlayerRequests);
        Assert.Equal(3, handler.FansubSourceRequests);
    }

    private sealed class SourceApiFixtureHandler(
        IReadOnlyList<string> embedUrls,
        string? watchPageHtml = null,
        string? iframeTemplate = null,
        Func<int, string, string>? playerHtmlFactory = null) : HttpMessageHandler
    {
        private int _sourcePlayerRequests;
        private int _activePlayerRequests;
        private int _maxConcurrentPlayerRequests;

        public int SourcePlayerRequests => Volatile.Read(ref _sourcePlayerRequests);
        public int MaxConcurrentPlayerRequests => Volatile.Read(ref _maxConcurrentPlayerRequests);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path.EndsWith("-bolum-izle", StringComparison.Ordinal))
            {
                return Ok(watchPageHtml ?? "<input id=\"EpisodeId\" name=\"EpisodeId\" value=\"42\" />"
                          + "<button class=\"fansubSelector\" data-fid=\"7\" data-fad=\"HolySubs\">HolySubs</button>");
            }

            if (request.Method == HttpMethod.Post && path == "/api/fansubSources")
            {
                var buttons = string.Concat(Enumerable.Range(0, embedUrls.Count)
                    .Select(index => $"<button class=\"sourceBtn\" data-id=\"{index + 1}\">Source</button>"));
                return Ok(buttons);
            }

            if (request.Method == HttpMethod.Post && path.StartsWith("/api/sourcePlayer/", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _sourcePlayerRequests);
                var active = Interlocked.Increment(ref _activePlayerRequests);
                UpdateMaxConcurrent(active);
                await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
                var sourceId = int.Parse(path["/api/sourcePlayer/".Length..], System.Globalization.CultureInfo.InvariantCulture);
                var url = embedUrls[sourceId - 1];
                var html = playerHtmlFactory?.Invoke(sourceId, url)
                           ?? string.Format(System.Globalization.CultureInfo.InvariantCulture,
                                            iframeTemplate ?? "<iframe src=\"{0}\"></iframe>",
                                            url);
                Interlocked.Decrement(ref _activePlayerRequests);
                return Ok(JsonSerializer.Serialize(new { source = html }));
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private void UpdateMaxConcurrent(int active)
        {
            while (active > Volatile.Read(ref _maxConcurrentPlayerRequests))
            {
                var current = Volatile.Read(ref _maxConcurrentPlayerRequests);
                if (active <= current || Interlocked.CompareExchange(ref _maxConcurrentPlayerRequests, active, current) == current)
                {
                    return;
                }
            }
        }

        private static HttpResponseMessage Ok(string content)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content)
            };
        }
    }

    private sealed record EpisodeFixture(int EpisodeId, IReadOnlyList<FansubFixture> Fansubs);

    private sealed record FansubFixture(int Id, string Name, IReadOnlyList<string> EmbedUrls);

    private sealed class MultiEpisodeFixtureHandler(IReadOnlyDictionary<string, EpisodeFixture> pages) : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, string> _sources = new(StringComparer.Ordinal);
        private int _fansubSourceRequests;
        private int _sourcePlayerRequests;

        public int FansubSourceRequests => Volatile.Read(ref _fansubSourceRequests);
        public int SourcePlayerRequests => Volatile.Read(ref _sourcePlayerRequests);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get)
            {
                var slug = path.Trim('/');
                if (pages.TryGetValue(slug, out var page))
                {
                    var fansubs = string.Concat(page.Fansubs.Select(fansub =>
                        $"<button class=\"fansubSelector\" data-fid=\"{fansub.Id}\" data-fad=\"{fansub.Name}\">{fansub.Name}</button>"));
                    return Ok($"<input id=\"EpisodeId\" name=\"EpisodeId\" value=\"{page.EpisodeId}\" />{fansubs}");
                }
            }

            if (request.Method == HttpMethod.Post && path == "/api/fansubSources")
            {
                Interlocked.Increment(ref _fansubSourceRequests);
                using var body = await JsonDocument.ParseAsync(await request.Content!.ReadAsStreamAsync(cancellationToken),
                    cancellationToken: cancellationToken);
                var episodeId = body.RootElement.GetProperty("EpisodeId").GetInt32();
                var fansubId = body.RootElement.GetProperty("FansubId").GetInt32();
                var fansub = pages.Values.Single(page => page.EpisodeId == episodeId).Fansubs.Single(group => group.Id == fansubId);
                var dataIds = Enumerable.Range(0, fansub.EmbedUrls.Count)
                    .Select(index => $"{episodeId}-{fansubId}-{index}")
                    .ToArray();
                for (var index = 0; index < dataIds.Length; index++)
                {
                    _sources[dataIds[index]] = fansub.EmbedUrls[index];
                }

                var buttons = string.Concat(dataIds.Select(dataId => $"<button class=\"sourceBtn\" data-id=\"{dataId}\">Source</button>"));
                return Ok(buttons);
            }

            if (request.Method == HttpMethod.Post && path.StartsWith("/api/sourcePlayer/", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _sourcePlayerRequests);
                var dataId = path["/api/sourcePlayer/".Length..];
                if (_sources.TryGetValue(dataId, out var embedUrl))
                {
                    return Ok(JsonSerializer.Serialize(new { source = $"<iframe src=\"{embedUrl}\"></iframe>" }));
                }
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Ok(string content) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(content)
        };
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
