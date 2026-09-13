using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Migurdex.Core.Services;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Net;
using System.Text;
using Xunit;

namespace Migurdex.Tests;

public sealed class AniListOAuthTests
{
    private const string ClientId     = "test-client-id";
    private const string ClientSecret = "test-client-secret";
    private const string RedirectUri  = "http://127.0.0.1:46421/callback";

    private const string TokenJson = """
                                     {"token_type":"Bearer","expires_in":31536000,
                                      "access_token":"access-abc","refresh_token":"refresh-def"}
                                     """;

    private static AniListOAuthClient ClientWith(HttpResponseMessage response)
    {
        var handler = new ScriptedHandler([response]);
        return new AniListOAuthClient(new StubBridge(new HttpClient(handler)), ClientId, ClientSecret);
    }

    [Fact]
    public void BuildAuthorizeUrl_Contains_All_Parameters()
    {
        var client = ClientWith(new HttpResponseMessage(HttpStatusCode.OK));

        var url = client.BuildAuthorizeUrl(RedirectUri);

        Assert.StartsWith("https://anilist.co/api/v2/oauth/authorize?", url);
        Assert.Contains("client_id=test-client-id", url);
        Assert.Contains("response_type=code", url);
        Assert.Contains("redirect_uri=" + Uri.EscapeDataString(RedirectUri), url);
    }

    [Fact]
    public async Task ExchangeCode_Parses_Token_And_Expiry()
    {
        var capturing = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(TokenJson, Encoding.UTF8, "application/json")
        });
        var client = new AniListOAuthClient(new StubBridge(new HttpClient(capturing)), ClientId, ClientSecret);

        var before = DateTime.UtcNow;
        var token  = await client.ExchangeCodeAsync("auth-code-xyz", RedirectUri);

        Assert.NotNull(token);
        Assert.Equal("anilist", token!.Provider);
        Assert.Equal("access-abc", token.AccessToken);
        Assert.Equal("refresh-def", token.RefreshToken);
        Assert.True(token.ExpiresAtUtc - before >= TimeSpan.FromSeconds(31536000 - 5));

        var body = capturing.LastBody;
        Assert.Contains("grant_type=authorization_code", body);
        Assert.Contains("code=auth-code-xyz", body);
        Assert.Contains("client_secret=test-client-secret", body);
        Assert.Contains("redirect_uri=" + Uri.EscapeDataString(RedirectUri), body);
    }

    [Fact]
    public async Task Refresh_Sends_Refresh_Grant()
    {
        var capturing = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(TokenJson, Encoding.UTF8, "application/json")
        });
        var client = new AniListOAuthClient(new StubBridge(new HttpClient(capturing)), ClientId, ClientSecret);

        var token = await client.RefreshAsync("old-refresh");

        Assert.NotNull(token);
        var body = capturing.LastBody;
        Assert.Contains("grant_type=refresh_token", body);
        Assert.Contains("refresh_token=old-refresh", body);
    }

    [Fact]
    public async Task ExchangeCode_Error_Response_Returns_Null()
    {
        var client = ClientWith(new HttpResponseMessage(HttpStatusCode.BadRequest));

        Assert.Null(await client.ExchangeCodeAsync("bad-code", RedirectUri));
    }

    [Fact]
    public async Task ExchangeCode_RateLimited_Returns_Null()
    {
        var client = ClientWith(new HttpResponseMessage(HttpStatusCode.TooManyRequests));

        Assert.Null(await client.RefreshAsync("whatever"));
    }

    [Fact]
    public async Task ExchangeCode_Missing_AccessToken_Returns_Null()
    {
        var client = ClientWith(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"expires_in\":100}", Encoding.UTF8, "application/json")
        });

        Assert.Null(await client.ExchangeCodeAsync("code", RedirectUri));
    }

    [Fact]
    public async Task ExchangeCode_CancelledToken_Throws_Not_Null()
    {
        var client = ClientWith(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(TokenJson, Encoding.UTF8, "application/json")
        });

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                                                                    client.ExchangeCodeAsync(
                                                                        "code",
                                                                        RedirectUri,
                                                                        cts.Token));
    }

    [Fact]
    public async Task Loopback_Receives_Code_From_Callback()
    {
        const int port = 46499;

        var waitTask = LoopbackCodeReceiver.WaitForCodeAsync(port, "/callback", TimeSpan.FromSeconds(10));

        using var http     = new HttpClient();
        var       response = await http.GetAsync($"http://127.0.0.1:{port}/callback?code=loop-code-1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("window.close", body);
        Assert.Contains("Migurdex bağlandı", body);

        var code = await waitTask;
        Assert.Equal("loop-code-1", code);
    }

    [Fact]
    public async Task Loopback_Error_Param_Returns_Null()
    {
        const int port = 46498;

        var waitTask = LoopbackCodeReceiver.WaitForCodeAsync(port, "/callback", TimeSpan.FromSeconds(10));

        using var http = new HttpClient();
        await http.GetAsync($"http://127.0.0.1:{port}/callback?error=access_denied");

        Assert.Null(await waitTask);
    }

    [Fact]
    public async Task Loopback_Timeout_Returns_Null()
    {
        var code = await LoopbackCodeReceiver.WaitForCodeAsync(46497, "/callback", TimeSpan.FromMilliseconds(200));

        Assert.Null(code);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public CapturingHandler(HttpResponseMessage response)
        {
            _response = response;
        }

        public string LastBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken                                                           cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastBody = request.Content is not null
                           ? await request.Content.ReadAsStringAsync(cancellationToken)
                           : string.Empty;
            return _response;
        }
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;

        public ScriptedHandler(IEnumerable<HttpResponseMessage> responses)
        {
            _responses = new Queue<HttpResponseMessage>(responses);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken                                                     cancellationToken)
        {
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
