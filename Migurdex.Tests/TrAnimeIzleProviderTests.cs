using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Migurdex.Plugins.TrAnimeIzle;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Net;
using System.Text;
using Xunit;

namespace Migurdex.Tests;

public sealed class TrAnimeIzleProviderTests
{
    [Fact]
    public async Task GetVideoSources_UsesCurrentTranslatorApiAndResolvesFirePlayerHash()
    {
        const string hash = "4faf133ea46f7ee2eac98fb2c9481c6a";
        var requestedUrls = new List<string>();
        using var client = new HttpClient(new RoutingHandler((request) =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            requestedUrls.Add(url);

            if (url == "https://tranimeizle.org.tr/episode-1-izle")
            {
                return Ok("""
                    <a data-translatorclick translator="https://tranimeizle.org.tr/episode/16907/translator/87492"
                       data-fansub-name="Akira"></a>
                    """);
            }

            if (url == "https://tranimeizle.org.tr/episode/16907/translator/87492")
            {
                return Ok("""
                    {"data":"<a video=\"https://tranimeizle.org.tr/video/1468796\" data-video-name=\"Aincrad\"></a>"}
                    """, "application/json");
            }

            if (url == "https://tranimeizle.org.tr/video/1468796")
            {
                return Ok("""
                    {"player":"<iframe src=\"https://tranimeizle.org.tr/player/1468796\"></iframe>"}
                    """, "application/json");
            }

            if (url == "https://tranimeizle.org.tr/player/1468796")
            {
                return Ok($"<script>eval(function(p,a,c,k,e,d){{return p}}('0(\"{hash}\")',62,1,'FirePlayer'.split('|'),0,0))</script>");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));

        var provider = new TrAnimeIzleProvider(new StubBridge(client), NullLogger<TrAnimeIzleProvider>.Instance);
        var sources = await provider.GetVideoSourcesAsync("episode-1",
                                                           cancellationToken: TestContext.Current.CancellationToken);

        var source = Assert.Single(sources);
        Assert.Equal($"https://anizmplayer.com/video/{hash}", source.Url);
        Assert.Equal("Akira", source.Group);
        Assert.Equal("Aincrad", source.Quality);
        Assert.Equal(VideoType.Embed, source.Type);
        Assert.Contains(requestedUrls, url => url.Contains("/episode/16907/translator/87492", StringComparison.Ordinal));
        Assert.Contains(requestedUrls, url => url.Contains("/player/1468796", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Search_UsesCurrentJsonEndpoint()
    {
        var provider = Create(url => url.Contains("/searchAnime?query=Fullmetal", StringComparison.Ordinal)
                                         ? Ok("""
                                             {"data":[{"info_title":"Fullmetal Alchemist: Brotherhood","info_titleenglish":"Fullmetal Alchemist: Brotherhood","info_slug":"fullmetal-alchemist-brotherhood","info_poster":"poster.webp","info_year":"2009","info_malpoint":9.1}]}
                                             """, "application/json")
                                         : new HttpResponseMessage(HttpStatusCode.NotFound));

        var result = Assert.Single(await provider.SearchAsync("Fullmetal", TestContext.Current.CancellationToken));

        Assert.Equal("fullmetal-alchemist-brotherhood", result.Id);
        Assert.Equal("Fullmetal Alchemist: Brotherhood", result.Title);
        Assert.Equal("https://tranimeizle.org.tr/fullmetal-alchemist-brotherhood", result.Url);
        Assert.Equal("https://tranimeizle.org.tr/storage/pcovers/poster.webp", result.PosterUrl);
        Assert.Equal(9.1, result.Score);
    }

    [Fact]
    public async Task GetDetails_ConvertsAbsoluteCurrentEpisodeLinksToProviderIds()
    {
        var provider = Create(url => url == "https://tranimeizle.org.tr/anime/fullmetal-alchemist-brotherhood"
                                         ? Ok("""
                                             <html><body>
                                               <h1>Fullmetal Alchemist: Brotherhood</h1>
                                               <a href="https://tranimeizle.org.tr/fullmetal-alchemist-brotherhood-1-bolum-izle"><div class="episodeBlock">1. Bölüm</div></a>
                                               <a href="https://tranimeizle.org.tr/fullmetal-alchemist-brotherhood-2-bolum-izle"><div class="episodeBlock">2. Bölüm</div></a>
                                             </body></html>
                                             """)
                                         : new HttpResponseMessage(HttpStatusCode.NotFound));

        var details = await provider.GetDetailsAsync("fullmetal-alchemist-brotherhood",
                                                      TestContext.Current.CancellationToken);

        Assert.Equal("Fullmetal Alchemist: Brotherhood", details.Title);
        Assert.Equal(2, details.Episodes.Count);
        Assert.Equal("fullmetal-alchemist-brotherhood-1-bolum", details.Episodes[0].Id);
        Assert.Equal(1, details.Episodes[0].Number);
        Assert.Equal("fullmetal-alchemist-brotherhood-2-bolum", details.Episodes[1].Id);
        Assert.Equal(2, details.Episodes[1].Number);
    }

    private static TrAnimeIzleProvider Create(Func<string, HttpResponseMessage> route)
    {
        return new TrAnimeIzleProvider(
            new StubBridge(new HttpClient(new RoutingHandler(request => route(request.RequestUri!.AbsoluteUri)))),
            NullLogger<TrAnimeIzleProvider>.Instance);
    }

    private static HttpResponseMessage Ok(string content, string mediaType = "text/html")
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, mediaType)
        };
    }

    private sealed class RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(route(request));
        }
    }

    private sealed class StubBridge(HttpClient client) : ISharedBridge
    {
        public IMp4MetadataReader MetadataReader => throw new NotSupportedException();
        public ILoggerFactory LoggerFactory => NullLoggerFactory.Instance;

        public HttpClient CreateHttpClient(HttpClientOptions? options = null) => client;

        public HttpClient CreateHttpClient(Action<HttpClientOptions> configure) => client;

        public ILogger<T> CreateLogger<T>() => NullLogger<T>.Instance;
    }
}
