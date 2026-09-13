using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Net;
using System.Text.Json;

namespace Migurdex.Core.Services;

public static class AniListOAuthDefaults
{
    public const string AuthorizeUrl = "https://anilist.co/api/v2/oauth/authorize";
    public const string TokenUrl     = "https://anilist.co/api/v2/oauth/token";
    public const int    LoopbackPort = 46421;
    public const string CallbackPath = "/callback";

    public static string LoopbackRedirectUri
    {
        get { return $"http://127.0.0.1:{LoopbackPort}{CallbackPath}"; }
    }
}

public sealed class AniListOAuthClient : IOAuthFlow
{
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly HttpClient _httpClient;
    private readonly ILogger<AniListOAuthClient> _logger;

    public AniListOAuthClient(ISharedBridge                  bridge,
        string                                               clientId,
        string                                               clientSecret,
        ILogger<AniListOAuthClient>?                         logger = null)
    {
        _clientId     = clientId;
        _clientSecret = clientSecret;
        _httpClient   = bridge.CreateHttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Migurdex/1.0");
        _logger = logger ?? NullLogger<AniListOAuthClient>.Instance;
    }

    public string Provider
    {
        get { return "anilist"; }
    }

    public string BuildAuthorizeUrl(string redirectUri)
    {
        return $"{AniListOAuthDefaults.AuthorizeUrl}" +
               $"?client_id={Uri.EscapeDataString(_clientId)}" +
               $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
               "&response_type=code";
    }

    public async Task<OAuthToken?> ExchangeCodeAsync(string            code,
        string                                                         redirectUri,
        CancellationToken                                              cancellationToken = default)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"]    = "authorization_code",
            ["client_id"]     = _clientId,
            ["client_secret"] = _clientSecret,
            ["redirect_uri"]  = redirectUri,
            ["code"]          = code
        };

        return await RequestTokenAsync(form, cancellationToken);
    }

    public async Task<OAuthToken?> RefreshAsync(string            refreshToken,
        CancellationToken                                       cancellationToken = default)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"]    = "refresh_token",
            ["client_id"]     = _clientId,
            ["client_secret"] = _clientSecret,
            ["refresh_token"] = refreshToken
        };

        return await RequestTokenAsync(form, cancellationToken);
    }

    private async Task<OAuthToken?> RequestTokenAsync(Dictionary<string, string> form,
        CancellationToken                                                        cancellationToken)
    {
        try
        {
            using var content  = new FormUrlEncodedContent(form);
            using var response = await _httpClient.PostAsync(AniListOAuthDefaults.TokenUrl, content,
                                                             cancellationToken);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                _logger.LogWarning("AniList OAuth 429; kullanıcı biraz bekleyip tekrar denemeli");
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("AniList OAuth token isteği başarısız: {Status}",
                                   (int)response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc  = JsonDocument.Parse(json);
            var       root = doc.RootElement;

            if (!root.TryGetProperty("access_token", out var accessProp)
                || accessProp.GetString() is not { Length: > 0 } accessToken)
            {
                _logger.LogWarning("AniList OAuth yanıtında access_token yok");
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
            _logger.LogWarning(ex, "AniList OAuth token isteği hata verdi");
            return null;
        }
    }
}
