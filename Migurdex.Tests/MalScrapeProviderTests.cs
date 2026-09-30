using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Migurdex.Core.Services;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Net;
using Xunit;

namespace Migurdex.Tests;

public sealed class MalScrapeProviderTests
{
    private const string SearchHtml = """
        <html><body><div class="js-categories-seasonal"><table>
        <tr><th>Image</th><th>Anime</th><th>Type</th><th>Eps</th><th>Score</th></tr>
        <tr>
          <td class="borderClass"><div class="picSurround"><a href="https://myanimelist.net/anime/52991/Sousou_no_Frieren"><img class="lazyload" data-src="https://cdn.myanimelist.net/r/50x70/images/anime/1015/138006.jpg" src="spacer.gif" /></a></div></td>
          <td class="borderClass"><div class="title"><a class="hoverinfo_trigger" href="https://myanimelist.net/anime/52991/Sousou_no_Frieren"><strong>Sousou no Frieren</strong></a></div><div class="pt4">Demon King...</div></td>
          <td class="borderClass ac">TV</td><td class="borderClass ac">28</td><td class="borderClass ac">9.25</td>
        </tr>
        <tr>
          <td class="borderClass"><div class="picSurround"><a href="https://myanimelist.net/anime/20/Naruto"><img src="https://cdn.myanimelist.net/images/anime/1141/142503.jpg" /></a></div></td>
          <td class="borderClass"><div class="title"><a class="hoverinfo_trigger" href="https://myanimelist.net/anime/20/Naruto"><strong>Naruto</strong></a></div></td>
          <td class="borderClass ac">TV</td><td class="borderClass ac">220</td><td class="borderClass ac">N/A</td>
        </tr>
        </table></div></body></html>
        """;

    private const string DetailHtml = """
        <html><body>
        <h1 class="title-name h1_bold_md"><strong>Sousou no Frieren</strong></h1>
        <div class="stats-block po-r clearfix">
          <div class="score-label score-9">9.25</div>
          <span class="numbers ranked">Ranked <strong>#1</strong></span>
        </div>
        <div class="leftside">
          <a><img itemprop="image" data-src="https://cdn.myanimelist.net/images/anime/1015/138006l.jpg" /></a>
          <div class="spaceit_pad"><span class="dark_text">English:</span> Frieren: Beyond Journey's End</div>
          <div class="spaceit_pad"><span class="dark_text">Japanese:</span> 葬送のフリーレン</div>
          <div class="spaceit_pad"><span class="dark_text">Synonyms:</span> Frieren at the Funeral, Frieren The Slayer</div>
          <div class="spaceit_pad"><span class="dark_text">Type:</span> <a>TV</a></div>
          <div class="spaceit_pad"><span class="dark_text">Episodes:</span> 28</div>
          <div class="spaceit_pad"><span class="dark_text">Status:</span> Finished Airing</div>
          <div class="spaceit_pad"><span class="dark_text">Aired:</span> Sep 29, 2023 to Mar 22, 2024</div>
          <div class="spaceit_pad"><span class="dark_text">Premiered:</span> <a>Fall 2023</a></div>
          <div class="spaceit_pad"><span class="dark_text">Genres:</span>
            <span itemprop="genre" style="display: none">Adventure</span><a href="/anime/genre/2/Adventure">Adventure</a>,
            <span itemprop="genre" style="display: none">Fantasy</span><a href="/anime/genre/10/Fantasy">Fantasy</a>
          </div>
          <p itemprop="description">During their decade-long quest to defeat the Demon King...</p>
        </div>
        </body></html>
        """;

    private static MalScrapeProvider Create(ScriptedHandler handler)
    {
        return new MalScrapeProvider(new StubBridge(new HttpClient(handler)));
    }

    private static HttpResponseMessage Ok(string body, string mediaType = "text/html")
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, mediaType)
        };
    }

    [Fact]
    public async Task Search_HtmlTable_MapsFields()
    {
        var provider = Create(new ScriptedHandler([Ok(SearchHtml)]));

        var list = await provider.SearchMetadataAsync("frieren", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, list.Count);
        var first = list[0];
        Assert.Equal("52991", first.ExternalId);
        Assert.Equal("52991", first.MyAnimeListId);
        Assert.Equal(MetadataSource.MyAnimeList, first.Source);
        Assert.Equal("Sousou no Frieren", first.Title);
        Assert.Equal(28, first.TotalEpisodes);
        Assert.Equal(9.25, first.Score);
        Assert.Equal(ContentFormat.Tv, first.Format);
        Assert.Null(first.Year);
        Assert.StartsWith("Demon King", first.Summary);
        Assert.Equal("MyAnimeList", provider.Name);
        Assert.Null(list[1].Score);
    }

    [Fact]
    public async Task Search_HtmlError_ReturnsEmpty()
    {
        var provider = Create(new ScriptedHandler(
        [
            new HttpResponseMessage(HttpStatusCode.InternalServerError)
        ]));

        var list = await provider.SearchMetadataAsync("frieren", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(list);
    }

    [Fact]
    public async Task GetById_ParsesDetail()
    {
        var provider = Create(new ScriptedHandler([Ok(DetailHtml)]));

        var meta = await provider.GetMetadataByIdAsync("52991", TestContext.Current.CancellationToken);

        Assert.NotNull(meta);
        Assert.Equal("52991", meta.ExternalId);
        Assert.Equal("52991", meta.MyAnimeListId);
        Assert.Equal("Sousou no Frieren", meta.Title);
        Assert.Equal("Frieren: Beyond Journey's End", meta.EnglishTitle);
        Assert.Equal("葬送のフリーレン", meta.JapaneseTitle);
        Assert.Equal(2023, meta.Year);
        Assert.Equal(9.25, meta.Score);
        Assert.Equal(28, meta.TotalEpisodes);
        Assert.Equal(ContentFormat.Tv, meta.Format);
        Assert.Equal("Finished Airing", meta.Status);
        Assert.Contains("Adventure", meta.Genres);
        Assert.Contains("Frieren at the Funeral", meta.Synonyms);
        Assert.StartsWith("https://cdn.myanimelist.net", meta.PosterUrl);
    }

    [Fact]
    public async Task GetById_NonNumeric_NoRequest()
    {
        var handler = new ScriptedHandler([]);
        var provider = Create(handler);

        var meta = await provider.GetMetadataByIdAsync("not-a-number", TestContext.Current.CancellationToken);

        Assert.Null(meta);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Search_EmptyQuery_NoRequest()
    {
        var handler = new ScriptedHandler([]);
        var provider = Create(handler);

        var list = await provider.SearchMetadataAsync("   ", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(list);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Search_Cancelled_Propagates()
    {
        var provider = Create(new ScriptedHandler([]));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.SearchMetadataAsync("frieren", cancellationToken: cts.Token));
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;

        public ScriptedHandler(IEnumerable<HttpResponseMessage> responses)
        {
            _responses = new Queue<HttpResponseMessage>(responses);
        }

        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_responses.Count > 0
                ? _responses.Dequeue()
                : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }

    private sealed class StubBridge : ISharedBridge
    {
        private readonly HttpClient _client;

        public StubBridge(HttpClient client)
        {
            _client = client;
        }

        public IMp4MetadataReader MetadataReader => throw new NotSupportedException();
        public ILoggerFactory LoggerFactory => NullLoggerFactory.Instance;

        public HttpClient CreateHttpClient(HttpClientOptions? options = null)
        {
            return _client;
        }

        public HttpClient CreateHttpClient(Action<HttpClientOptions> configure)
        {
            return _client;
        }

        public ILogger<T> CreateLogger<T>()
        {
            return NullLogger<T>.Instance;
        }
    }
}
