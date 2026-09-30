using Migurdex.Cli.Configuration;
using Migurdex.Cli.Services;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Models;
using System.Net;
using System.Text;
using Xunit;

namespace Migurdex.Tests;

/// <summary>
/// BULGU 17: CLI, ayakta olan API'nin sürümünü doğrulamıyordu. `/health` zaten
/// sürüm döndürüyor; artık okunup CLI'ninkiyle karşılaştırılıyor ve yalnız gerçek
/// bir karışıklık varsa uyarı üretiliyor.
/// </summary>
public sealed class ApiVersionCheckTests
{
    [Theory]
    [InlineData("1.11.0", "1.11.0", false)]
    [InlineData("v1.11.0", "1.11.0", false)] // 'v' öneki normalize edilir
    [InlineData("1.11.0+abc123", "1.11.0", false)] // build metadata ayrıştırılır
    [InlineData("1.12.0-beta.1", "1.12.0-beta.1", false)]
    [InlineData("0.0.0", "0.0.0", false)] // geliştirme build'leri iki tarafta da 0.0.0
    [InlineData("1.11.0", "1.12.0", true)]
    [InlineData("1.11.0", "0.0.0", true)]
    [InlineData("1.12.0-beta.1", "1.12.0", true)]
    public void IsMismatch_ComparesNormalizedTags(string apiVersion, string cliVersion, bool expected)
    {
        Assert.Equal(expected, ApiVersionCheck.IsMismatch(apiVersion, cliVersion));
    }

    [Theory]
    [InlineData(null, "1.11.0")]
    [InlineData("", "1.11.0")]
    [InlineData("   ", "1.11.0")]
    [InlineData("1.11.0", null)]
    [InlineData("1.11.0", "")]
    public void IsMismatch_IsFalseWhenEitherSideIsUnknown(string? apiVersion, string? cliVersion)
    {
        Assert.False(ApiVersionCheck.IsMismatch(apiVersion, cliVersion));
    }

    [Fact]
    public void BuildWarningMessage_NamesThePortAndBothVersions()
    {
        var message = ApiVersionCheck.BuildWarningMessage("http://127.0.0.1:7045", "1.11.0", "1.12.0");

        Assert.Contains("Port 7045'te farklı bir Migurdex sürümü çalışıyor", message, StringComparison.Ordinal);
        Assert.Contains("API: 1.11.0", message, StringComparison.Ordinal);
        Assert.Contains("CLI: 1.12.0", message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildWarningMessage_FallsBackToTheAddressWhenThereIsNoPort()
    {
        var message = ApiVersionCheck.BuildWarningMessage("http://api.example.com", "1.11.0", "1.12.0");

        Assert.Contains("'http://api.example.com' adresinde farklı bir Migurdex sürümü",
                        message,
                        StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildWarningAsync_WarnsWhenTheApiRunsAnotherVersion()
    {
        var service = CreateService(StubHandler.Json("""{"status":"OK","version":"1.11.0","providers":12}"""));

        var warning = await ApiVersionCheck.BuildWarningAsync(service,
                                                              "http://127.0.0.1:7045",
                                                              "1.12.0",
                                                              TestContext.Current.CancellationToken);

        Assert.NotNull(warning);
        Assert.Contains("Port 7045'te farklı bir Migurdex sürümü çalışıyor", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildWarningAsync_StaysSilentWhenVersionsMatch()
    {
        var service = CreateService(StubHandler.Json("""{"status":"OK","version":"0.0.0"}"""));

        var warning = await ApiVersionCheck.BuildWarningAsync(service,
                                                              "http://127.0.0.1:7045",
                                                              "0.0.0",
                                                              TestContext.Current.CancellationToken);

        Assert.Null(warning);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task BuildWarningAsync_StaysSilentWhenHealthCannotBeRead(HttpStatusCode statusCode)
    {
        var service = CreateService(StubHandler.Status(statusCode));

        var warning = await ApiVersionCheck.BuildWarningAsync(service,
                                                              "http://127.0.0.1:7045",
                                                              "1.12.0",
                                                              TestContext.Current.CancellationToken);

        Assert.Null(warning);
    }

    [Fact]
    public async Task BuildWarningAsync_StaysSilentOnAnUnparseableBody()
    {
        var service = CreateService(StubHandler.Json("not json"));

        var warning = await ApiVersionCheck.BuildWarningAsync(service,
                                                              "http://127.0.0.1:7045",
                                                              "1.12.0",
                                                              TestContext.Current.CancellationToken);

        Assert.Null(warning);
    }

    [Fact]
    public async Task GetApiHealthAsync_ReadsVersionAndCounts()
    {
        var service =
            CreateService(StubHandler.Json("""{"status":"OK","version":"1.11.0","providers":14,"extractors":9,"rust":false}"""));

        var health = await service.GetApiHealthAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(health);
        Assert.Equal("1.11.0", health.Version);
        Assert.Equal(14, health.Providers);
        Assert.Equal(9, health.Extractors);
    }

    [Fact]
    public async Task GetApiHealthAsync_RethrowsUserCancellation()
    {
        using var httpClient = new HttpClient(new CancelingHandler());
        var service = new ApiClientService(httpClient, new TestConfigurationService());
        using var cts = new CancellationTokenSource();
        var request = service.GetApiHealthAsync(cts.Token);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await request);
    }

    [Fact]
    public async Task BuildWarningAsync_RethrowsUserCancellation()
    {
        using var httpClient = new HttpClient(new CancelingHandler());
        var service = new ApiClientService(httpClient, new TestConfigurationService());
        using var cts = new CancellationTokenSource();
        var request = ApiVersionCheck.BuildWarningAsync(service, "http://127.0.0.1:7045", "1.12.0", cts.Token);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await request);
    }

    private static ApiClientService CreateService(HttpMessageHandler handler)
    {
        return new ApiClientService(new HttpClient(handler), new TestConfigurationService());
    }

    private sealed class StubHandler(HttpStatusCode statusCode, string? body) : HttpMessageHandler
    {
        public static StubHandler Json(string body) => new(HttpStatusCode.OK, body);

        public static StubHandler Status(HttpStatusCode statusCode) => new(statusCode, "{}");

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                                                                 CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body ?? string.Empty, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }

    private sealed class CancelingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                                                                      CancellationToken cancellationToken)
        {
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
