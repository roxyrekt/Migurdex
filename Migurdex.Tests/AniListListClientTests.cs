using Migurdex.Core.Services;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Net;
using System.Text;
using Xunit;

namespace Migurdex.Tests;

public sealed class AniListListClientTests
{
    private const string SaveEntryJson = """
                                         {"data":{"SaveMediaListEntry":{"id":123,"progress":5,"status":"CURRENT"}}}
                                         """;

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "migurdex-listtest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static OAuthToken FreshToken()
    {
        return new OAuthToken
        {
            Provider     = "anilist",
            AccessToken  = "access-1",
            RefreshToken = "refresh-1",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        };
    }

    private sealed class FakeFlow : IOAuthFlow
    {
        public string Provider
        {
            get { return "anilist"; }
        }

        public int RefreshCalls { get; private set; }
        public Func<string, OAuthToken?>? OnRefresh { get; set; }

        public string BuildAuthorizeUrl(string redirectUri) => string.Empty;

        public Task<OAuthToken?> ExchangeCodeAsync(string code, string redirectUri,
            CancellationToken cancellationToken = default) => Task.FromResult<OAuthToken?>(null);

        public Task<OAuthToken?> RefreshAsync(string refreshToken,
            CancellationToken cancellationToken = default)
        {
            RefreshCalls++;
            return Task.FromResult(OnRefresh?.Invoke(refreshToken));
        }
    }

    private sealed class ScriptedCapturingHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;
        public readonly List<(string? Auth, string Body)> Requests = [];

        public ScriptedCapturingHandler(IEnumerable<HttpResponseMessage> responses)
        {
            _responses = new Queue<HttpResponseMessage>(responses);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken                                                             cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var body = request.Content is not null
                           ? await request.Content.ReadAsStringAsync(cancellationToken)
                           : string.Empty;
            Requests.Add((request.Headers.Authorization?.Parameter, body));
            return _responses.Count > 0
                       ? _responses.Dequeue()
                       : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }
    }

    private static HttpResponseMessage JsonOk(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    [Fact]
    public async Task UpdateProgress_Sends_Bearer_And_Mutation()
    {
        var handler = new ScriptedCapturingHandler([JsonOk(SaveEntryJson)]);
        var store   = new OAuthTokenStore(NewTempDir());
        store.Set(FreshToken());
        var flow   = new FakeFlow();
        var client = new AniListListClient(new StubBridge(new HttpClient(handler)), flow, store);

        Assert.True(await client.UpdateProgressAsync(113415, 5, AniListListStatus.Current));

        Assert.Equal(0, flow.RefreshCalls);
        var (auth, body) = Assert.Single(handler.Requests);
        Assert.Equal("access-1", auth);
        Assert.Contains("SaveMediaListEntry", body);
        Assert.Contains("\"mediaId\":113415", body);
        Assert.Contains("\"progress\":5", body);
        Assert.Contains("\"status\":\"CURRENT\"", body);
    }

    [Fact]
    public async Task UpdateProgress_ExpiredToken_Refreshes_Proactively()
    {
        var handler = new ScriptedCapturingHandler([JsonOk(SaveEntryJson)]);
        var store   = new OAuthTokenStore(NewTempDir());
        var expired = FreshToken();
        expired.ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1);
        store.Set(expired);

        var refreshed = FreshToken();
        refreshed.AccessToken = "access-2";
        var flow = new FakeFlow
        {
            OnRefresh = _ => refreshed
        };
        var client = new AniListListClient(new StubBridge(new HttpClient(handler)), flow, store);

        Assert.True(await client.UpdateProgressAsync(113415, 5, AniListListStatus.Completed));

        Assert.Equal(1, flow.RefreshCalls);
        var (auth, body) = Assert.Single(handler.Requests);
        Assert.Equal("access-2", auth);
        Assert.Contains("\"status\":\"COMPLETED\"", body);
        Assert.True(store.TryGet("anilist", out var saved));
        Assert.Equal("access-2", saved!.AccessToken);
    }

    [Fact]
    public async Task UpdateProgress_401_Refreshes_And_Retries_Once()
    {
        var handler = new ScriptedCapturingHandler(
        [
            new HttpResponseMessage(HttpStatusCode.Unauthorized),
            JsonOk(SaveEntryJson)
        ]);
        var store = new OAuthTokenStore(NewTempDir());
        store.Set(FreshToken());

        var refreshed = FreshToken();
        refreshed.AccessToken = "access-2";
        var flow = new FakeFlow
        {
            OnRefresh = _ => refreshed
        };
        var client = new AniListListClient(new StubBridge(new HttpClient(handler)), flow, store);

        Assert.True(await client.UpdateProgressAsync(113415, 5, AniListListStatus.Current));

        Assert.Equal(1, flow.RefreshCalls);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("access-1", handler.Requests[0].Auth);
        Assert.Equal("access-2", handler.Requests[1].Auth);
    }

    [Fact]
    public async Task UpdateProgress_401_RefreshFails_Returns_False()
    {
        var handler = new ScriptedCapturingHandler([new HttpResponseMessage(HttpStatusCode.Unauthorized)]);
        var store   = new OAuthTokenStore(NewTempDir());
        store.Set(FreshToken());
        var flow   = new FakeFlow();
        var client = new AniListListClient(new StubBridge(new HttpClient(handler)), flow, store);

        Assert.False(await client.UpdateProgressAsync(113415, 5, AniListListStatus.Current));

        Assert.Equal(1, flow.RefreshCalls);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task UpdateProgress_NoToken_Returns_False_Without_Http()
    {
        var handler = new ScriptedCapturingHandler([JsonOk(SaveEntryJson)]);
        var flow    = new FakeFlow();
        var client  = new AniListListClient(new StubBridge(new HttpClient(handler)), flow,
                                            new OAuthTokenStore(NewTempDir()));

        Assert.False(await client.UpdateProgressAsync(113415, 5, AniListListStatus.Current));

        Assert.Empty(handler.Requests);
        Assert.Equal(0, flow.RefreshCalls);
    }

    [Fact]
    public async Task UpdateProgress_RateLimited_Returns_False_Without_Refresh()
    {
        var handler = new ScriptedCapturingHandler([new HttpResponseMessage(HttpStatusCode.TooManyRequests)]);
        var store   = new OAuthTokenStore(NewTempDir());
        store.Set(FreshToken());
        var flow = new FakeFlow
        {
            OnRefresh = _ => FreshToken()
        };
        var client = new AniListListClient(new StubBridge(new HttpClient(handler)), flow, store);

        Assert.False(await client.UpdateProgressAsync(113415, 5, AniListListStatus.Current));

        Assert.Single(handler.Requests);
        Assert.Equal(0, flow.RefreshCalls);
    }

    [Fact]
    public async Task UpdateProgress_CancelledToken_Throws()
    {
        var handler = new ScriptedCapturingHandler([JsonOk(SaveEntryJson)]);
        var store   = new OAuthTokenStore(NewTempDir());
        store.Set(FreshToken());
        var client = new AniListListClient(new StubBridge(new HttpClient(handler)), new FakeFlow(), store);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.UpdateProgressAsync(113415, 5, AniListListStatus.Current, cts.Token));
    }

    private sealed class StubBridge : ISharedBridge
    {
        private readonly HttpClient _client;
        public StubBridge(HttpClient client) => _client = client;
        public IMp4MetadataReader MetadataReader => throw new NotSupportedException();

        public Microsoft.Extensions.Logging.ILoggerFactory LoggerFactory =>
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;

        public HttpClient CreateHttpClient(HttpClientOptions?        options = null) => _client;
        public HttpClient CreateHttpClient(Action<HttpClientOptions> configure)      => _client;

        public Microsoft.Extensions.Logging.ILogger<T> CreateLogger<T>() =>
            Microsoft.Extensions.Logging.Abstractions.NullLogger<T>.Instance;
    }
}
