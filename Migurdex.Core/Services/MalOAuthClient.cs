using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace Migurdex.Core.Services;

public static class MalOAuthDefaults
{
    public const string AuthorizeUrl = "https://myanimelist.net/v1/oauth2/authorize";
    public const string TokenUrl     = "https://myanimelist.net/v1/oauth2/token";
}

public sealed class MalOAuthClient : IOAuthFlow
{
    private const string PkceCharacters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._~";

    private readonly string                  _clientId;
    private readonly HttpClient              _httpClient;
    private readonly ILogger<MalOAuthClient> _logger;
    private          string?                 _codeVerifier;

    public MalOAuthClient(ISharedBridge bridge,
        string                          clientId,
        ILogger<MalOAuthClient>?        logger = null)
    {
        _clientId   = clientId;
        _httpClient = bridge.CreateHttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Migurdex/1.0");
        _logger = logger ?? NullLogger<MalOAuthClient>.Instance;
    }

    public string Provider => "mal";

    public string CodeVerifier
    {
        get => _codeVerifier ?? string.Empty;
        internal set => _codeVerifier = value;
    }

    public string BuildAuthorizeUrl(string redirectUri)
    {
        _codeVerifier = GenerateCodeVerifier();

        return $"{MalOAuthDefaults.AuthorizeUrl}"
               + $"?response_type=code"
               + $"&client_id={Uri.EscapeDataString(_clientId)}"
               + $"&code_challenge={Uri.EscapeDataString(_codeVerifier)}"
               + $"&code_challenge_method=plain"
               + $"&redirect_uri={Uri.EscapeDataString(redirectUri)}";
    }

    public async Task<OAuthToken?> ExchangeCodeAsync(string code,
        string                                              redirectUri,
        CancellationToken                                   cancellationToken = default)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"]    = "authorization_code",
            ["client_id"]     = _clientId,
            ["code"]          = code,
            ["redirect_uri"]  = redirectUri,
            ["code_verifier"] = _codeVerifier ?? string.Empty
        };

        return await RequestTokenAsync(form, cancellationToken);
    }

    public async Task<OAuthToken?> RefreshAsync(string refreshToken,
        CancellationToken                              cancellationToken = default)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"]    = "refresh_token",
            ["client_id"]     = _clientId,
            ["refresh_token"] = refreshToken
        };

        return await RequestTokenAsync(form, cancellationToken);
    }

    private async Task<OAuthToken?> RequestTokenAsync(Dictionary<string, string> form,
        CancellationToken                                                        cancellationToken)
    {
        try
        {
            using var content = new FormUrlEncodedContent(form);
            using var response = await _httpClient.PostAsync(MalOAuthDefaults.TokenUrl,
                                                             content,
                                                             cancellationToken);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                _logger.LogWarning("MyAnimeList OAuth 429; kullanıcı biraz bekleyip tekrar denemeli");
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("MyAnimeList OAuth token isteği başarısız: {Status}",
                                   (int) response.StatusCode);
                return null;
            }

            var       json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc  = JsonDocument.Parse(json);
            var       root = doc.RootElement;

            if (!root.TryGetProperty("access_token", out var accessProp)
                || accessProp.GetString() is not { Length: > 0 } accessToken)
            {
                _logger.LogWarning("MyAnimeList OAuth yanıtında access_token yok");
                return null;
            }

            var refreshToken = root.TryGetProperty("refresh_token", out var refreshProp)
                                   ? refreshProp.GetString() ?? string.Empty
                                   : string.Empty;

            var expiresIn = root.TryGetProperty("expires_in", out var expiresProp)
                            && expiresProp.TryGetInt32(out var seconds)
                                ? seconds
                                : 0;

            return new OAuthToken
            {
                Provider     = Provider,
                AccessToken  = accessToken,
                RefreshToken = refreshToken,
                ExpiresAtUtc = DateTime.UtcNow.AddSeconds(Math.Max(0, expiresIn))
            };
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "MyAnimeList OAuth token isteği hata verdi");
            return null;
        }
    }

    private static string GenerateCodeVerifier()
    {
        var bytes = RandomNumberGenerator.GetBytes(128);
        var chars = new char[128];
        for (var i = 0; i < 128; i++)
        {
            chars[i] = PkceCharacters[bytes[i] % PkceCharacters.Length];
        }

        return new string(chars);
    }
}
