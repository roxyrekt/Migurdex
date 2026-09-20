using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Migurdex.Core.Services;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Net;
using System.Text;
using Xunit;

namespace Migurdex.Tests;

public sealed class MalOAuthTests
{
    private const string ClientId    = "test-mal-client-id";
    private const string RedirectUri = "http://127.0.0.1:46421/callback";

    private const string TokenJson = """
                                     {"token_type":"Bearer","expires_in":2415600,
                                      "access_token":"mal-access-123","refresh_token":"mal-refresh-456"}
                                     """;

    private static MalOAuthClient ClientWith(HttpResponseMessage response)
    {
        var handler = new ScriptedHandler([response]);
        return new MalOAuthClient(new StubBridge(new HttpClient(handler)), ClientId);
    }

    [Fact]
    public void BuildAuthorizeUrl_Contains_Pkce_And_All_Parameters()
    {
        var client = ClientWith(new HttpResponseMessage(HttpStatusCode.OK));

        var url = client.BuildAuthorizeUrl(RedirectUri);

        Assert.StartsWith("https://myanimelist.net/v1/oauth2/authorize?", url);
        Assert.Contains("client_id=test-mal-client-id", url);
        Assert.Contains("response_type=code", url);
        Assert.Contains("code_challenge_method=plain", url);
        Assert.Contains("code_challenge=", url);
        Assert.Contains("redirect_uri=" + Uri.EscapeDataString(RedirectUri), url);
        Assert.Equal(128, client.CodeVerifier.Length);
    }

    [Fact]
    public async Task ExchangeCode_Sends_CodeVerifier_And_Parses_Token()
    {
        var ct = TestContext.Current.CancellationToken;
        var capturing = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(TokenJson, Encoding.UTF8, "application/json")
        });
        var client = new MalOAuthClient(new StubBridge(new HttpClient(capturing)), ClientId);

        _ = client.BuildAuthorizeUrl(RedirectUri);
        var expectedVerifier = client.CodeVerifier;

        var before = DateTime.UtcNow;
        var token  = await client.ExchangeCodeAsync("mal-auth-code", RedirectUri, ct);

        Assert.NotNull(token);
        Assert.Equal("mal", token!.Provider);
        Assert.Equal("mal-access-123", token.AccessToken);
        Assert.Equal("mal-refresh-456", token.RefreshToken);
        Assert.True(token.ExpiresAtUtc - before >= TimeSpan.FromSeconds(2415600 - 5));

        var body = capturing.LastBody;
        Assert.Contains("grant_type=authorization_code", body);
        Assert.Contains("code=mal-auth-code", body);
        Assert.Contains($"code_verifier={expectedVerifier}", body);
        Assert.Contains("redirect_uri=" + Uri.EscapeDataString(RedirectUri), body);
    }

    [Fact]
    public async Task Refresh_Sends_Refresh_Grant()
    {
        var ct = TestContext.Current.CancellationToken;
        var capturing = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(TokenJson, Encoding.UTF8, "application/json")
        });
        var client = new MalOAuthClient(new StubBridge(new HttpClient(capturing)), ClientId);

        var token = await client.RefreshAsync("old-mal-refresh", ct);

        Assert.NotNull(token);
        var body = capturing.LastBody;
        Assert.Contains("grant_type=refresh_token", body);
        Assert.Contains("refresh_token=old-mal-refresh", body);
        Assert.Contains("client_id=test-mal-client-id", body);
    }

    [Fact]
    public async Task ExchangeCode_Error_Response_Returns_Null()
    {
        var ct     = TestContext.Current.CancellationToken;
        var client = ClientWith(new HttpResponseMessage(HttpStatusCode.BadRequest));

        Assert.Null(await client.ExchangeCodeAsync("bad-code", RedirectUri, ct));
    }

    [Fact]
    public async Task ExchangeCode_RateLimited_Returns_Null()
    {
        var ct     = TestContext.Current.CancellationToken;
        var client = ClientWith(new HttpResponseMessage(HttpStatusCode.TooManyRequests));

        Assert.Null(await client.RefreshAsync("whatever", ct));
    }

    [Fact]
    public async Task ExchangeCode_Missing_AccessToken_Returns_Null()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = ClientWith(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"expires_in\":100}", Encoding.UTF8, "application/json")
        });

        Assert.Null(await client.ExchangeCodeAsync("code", RedirectUri, ct));
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
