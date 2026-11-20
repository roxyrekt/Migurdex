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
        var       handler    = new CancelingHandler();
        using var httpClient = new HttpClient(handler);
        var       service    = new ApiClientService(httpClient, new TestConfigurationService());
        using var cts        = new CancellationTokenSource();
        var       request    = service.SearchAnimeAsync("test", cancellationToken: cts.Token);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await request);
    }

    [Fact]
    public async Task GetBlameReportAsync_ParsesReport()
    {
        var handler = new OkHandler(
            """{"since":"2026-10-07T00:00:00Z","operations":[{"operation":"GET /x","calls":2,"avgMs":150,"maxMs":200,"errors":0}],"providers":[]}""");
        var service = new ApiClientService(new HttpClient(handler)
                                           {
                                               BaseAddress = new Uri("http://localhost/")
                                           },
                                           new TestConfigurationService());

        var result = await service.GetBlameReportAsync(TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Data);
        var op = Assert.Single(result.Data!.Operations);
        Assert.Equal("GET /x", op.Operation);
        Assert.Empty(result.Data.Providers);
    }

    [Fact]
    public async Task GetVideoSourcesAsync_ParsesEnvelope()
    {
        var handler = new OkHandler(
            """{"sources":[{"url":"https://x/y.mp4","quality":"720p","type":1}],"warnings":[{"code":"QuotaExceeded","message":"HTTP 429"}]}""");
        var service = new ApiClientService(new HttpClient(handler)
                                           {
                                               BaseAddress = new Uri("http://localhost/")
                                           },
                                           new TestConfigurationService());

        var result =
            await service.GetVideoSourcesAsync("P", "ep", cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        var src = Assert.Single(result.Data!);
        Assert.Equal("https://x/y.mp4", src.Url);
    }

    [Fact]
    public async Task GetVideoSourcesAsync_FallsBackToLegacyArray()
    {
        var handler = new OkHandler(
            """[{"url":"https://x/y.mp4","quality":"720p","type":1}]""");
        var service = new ApiClientService(new HttpClient(handler)
                                           {
                                               BaseAddress = new Uri("http://localhost/")
                                           },
                                           new TestConfigurationService());

        var result =
            await service.GetVideoSourcesAsync("P", "ep", cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Data!);
    }

    [Fact]
    public async Task IsApiOnlineAsync_RethrowsUserCancellationInsteadOfReturningFalse()
    {
        var       handler    = new CancelingHandler();
        using var httpClient = new HttpClient(handler);
        var       service    = new ApiClientService(httpClient, new TestConfigurationService());
        using var cts        = new CancellationTokenSource();
        var       request    = service.IsApiOnlineAsync(cts.Token);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await request);
    }

    [Fact]
    public async Task GetAnimeDetailsAsync_ReadsProblemDetailsMessage_On502()
    {
        var handler = new StatusHandler(
            HttpStatusCode.BadGateway,
            """{"title":"Upstream hata","status":502,"detail":"Upstream detay hatası (OpenAnime). (HTTP 403)"}""");
        var service = new ApiClientService(new HttpClient(handler)
                                           {
                                               BaseAddress = new Uri("http://localhost/")
                                           },
                                           new TestConfigurationService());

        var result =
            await service.GetAnimeDetailsAsync("OpenAnime",
                                               "jojos-bizarre-adventure",
                                               TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Data);
        Assert.Equal("Upstream detay hatası (OpenAnime). (HTTP 403)", result.Error);
    }

    [Fact]
    public async Task SearchAnimeStreamAsync_RecordsProviderErrorDetailsInStats()
    {
        var sse =
            "event: providerError\ndata: {\"provider\":\"OpenAnime\",\"scope\":\"search\",\"error\":\"Upstream arama hatası. (HTTP 403)\"}\n\nevent: done\ndata: {\"succeeded\":0,\"failed\":1,\"errors\":[],\"totalItems\":0}\n\n";
        var handler = new OkHandler(sse);
        var service = new ApiClientService(new HttpClient(handler)
                                           {
                                               BaseAddress = new Uri("http://localhost/")
                                           },
                                           new TestConfigurationService());
        var stats = new StreamScanStats();

        var items = new List<StreamedSearchResult>();
        await foreach (var item in service.SearchAnimeStreamAsync("naruto",
                                                                  stats: stats,
                                                                  cancellationToken: TestContext.Current
                                                                      .CancellationToken))
        {
            items.Add(item);
        }

        Assert.Equal(1, stats.Errors);
        var detail = Assert.Single(stats.GetErrorDetailsSnapshot());
        Assert.Equal("OpenAnime: Upstream arama hatası. (HTTP 403)", detail);
        Assert.Single(items);
        Assert.Equal("error", items[0].Status);
    }

    private sealed class StatusHandler(HttpStatusCode status, string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken                                                     cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(json)
            });
        }
    }

    private sealed class OkHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken                                                     cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json)
            });
        }
    }

    private sealed class CancelingHandler : HttpMessageHandler
    {
        public TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken  cancellationToken)
        {
            Started.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }
    }

    private sealed class TestConfigurationService : IConfigurationService
    {
        public CliConfig Config          { get; } = new();
        public string    ConfigDirectory { get; } = Path.GetTempPath();

        public void Save()
        {
        }

        public void Reload()
        {
        }
    }
}
