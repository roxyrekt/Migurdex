using Migurdex.Cli.Configuration;
using Migurdex.Cli.Services;
using System.Net;
using Xunit;

namespace Migurdex.Tests;

public sealed class ApiClientServiceTests
{
    [Fact]
    public async Task SearchAnimeAsync_RethrowsUserCancellation()
    {
        var handler = new CancelingHandler();
        using var httpClient = new HttpClient(handler);
        var service = new ApiClientService(httpClient, new TestConfigurationService());
        using var cts = new CancellationTokenSource();
        var request = service.SearchAnimeAsync("test", cancellationToken: cts.Token);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await request);
    }

    [Fact]
    public async Task IsApiOnlineAsync_RethrowsUserCancellationInsteadOfReturningFalse()
    {
        var handler = new CancelingHandler();
        using var httpClient = new HttpClient(handler);
        var service = new ApiClientService(httpClient, new TestConfigurationService());
        using var cts = new CancellationTokenSource();
        var request = service.IsApiOnlineAsync(cts.Token);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await request);
    }

    private sealed class CancelingHandler : HttpMessageHandler
    {
        public TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }
    }

    private sealed class TestConfigurationService : IConfigurationService
    {
        public CliConfig Config { get; } = new();
        public string ConfigDirectory { get; } = Path.GetTempPath();

        public void Save()
        {
        }

        public void Reload()
        {
        }
    }

    [Fact]
    public async Task SearchCanonicalAsync_ParsesResults()
    {
        var handler = new JsonHandler(_ =>
            """[{"canonicalId":"anilist:20","aniListId":"20","title":"NARUTO","year":2002}]""");
        var service = new ApiClientService(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") },
                                           new TestConfigurationService());

        var result = await service.SearchCanonicalAsync("naruto", TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        var single = Assert.Single(result.Data);
        Assert.Equal("anilist:20", single.CanonicalId);
        Assert.Equal("NARUTO", single.Title);
    }

    [Fact]
    public async Task SearchCanonicalAsync_BlankQuery_FailsWithoutRequest()
    {
        var handler = new JsonHandler(_ => "[]");
        var service = new ApiClientService(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") },
                                           new TestConfigurationService());

        var result = await service.SearchCanonicalAsync("   ", TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task GetCanonicalEpisodesAsync_ParsesResult()
    {
        var handler = new JsonHandler(_ =>
            """{"anime":{"canonicalId":"anilist:1535","title":"DEATH NOTE"},"episodes":[{"season":1,"number":1,"sources":[]}],"providersQueried":14,"providersMatched":12,"matchedProviders":["A"],"warnings":[],"providerErrors":{}}""");
        var service = new ApiClientService(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") },
                                           new TestConfigurationService());

        var result = await service.GetCanonicalEpisodesAsync("anilist:1535",
                                                             TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Data);
        Assert.Equal(12, result.Data!.ProvidersMatched);
        Assert.Single(result.Data.Episodes);
        Assert.Equal(["A"], result.Data.MatchedProviders);
    }

    [Fact]
    public async Task GetCanonicalSourcesAsync_SendsNumberAndGroup()
    {
        HttpRequestMessage? seen = null;
        var handler = new JsonHandler(req =>
        {
            seen = req;
            return """[{"url":"https://cdn.site/v.mp4","quality":"1080p","type":0}]""";
        });
        var service = new ApiClientService(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") },
                                           new TestConfigurationService());

        var result = await service.GetCanonicalSourcesAsync("anilist:1535",
                                                            1,
                                                            "SonAnime",
                                                            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        var single = Assert.Single(result.Data);
        Assert.Equal("https://cdn.site/v.mp4", single.Url);
        Assert.NotNull(seen);
        Assert.Contains("number=1", seen!.RequestUri!.Query);
        Assert.Contains("group=SonAnime", seen.RequestUri.Query);
    }

    private sealed class JsonHandler(Func<HttpRequestMessage, string> responder) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken                                                     cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responder(request))
            });
        }
    }
}
