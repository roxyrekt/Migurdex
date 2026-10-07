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
    public async Task GetBlameReportAsync_ParsesReport()
    {
        var handler = new OkHandler(
            """{"since":"2026-10-07T00:00:00Z","operations":[{"operation":"GET /x","calls":2,"avgMs":150,"maxMs":200,"errors":0}],"providers":[]}""");
        var service = new ApiClientService(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") },
                                           new TestConfigurationService());

        var result = await service.GetBlameReportAsync(TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Data);
        var op = Assert.Single(result.Data!.Operations);
        Assert.Equal("GET /x", op.Operation);
        Assert.Empty(result.Data.Providers);
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
}
