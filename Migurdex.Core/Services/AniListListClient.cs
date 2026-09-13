using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Interfaces;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Migurdex.Core.Services;

public sealed class AniListListClient
{
    private readonly IOAuthFlow _oauth;
    private readonly OAuthTokenStore _tokenStore;
    private readonly HttpClient _httpClient;
    private readonly ILogger<AniListListClient> _logger;

    public AniListListClient(ISharedBridge             bridge,
        IOAuthFlow                                    oauth,
        OAuthTokenStore                               tokenStore,
        ILogger<AniListListClient>?                   logger = null)
    {
        _oauth      = oauth;
        _tokenStore = tokenStore;
        _httpClient = bridge.CreateHttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Migurdex/1.0");
        _logger = logger ?? NullLogger<AniListListClient>.Instance;
    }

    public async Task<bool> UpdateProgressAsync(int               mediaId,
        int                                                     progress,
        AniListListStatus                                       status,
        CancellationToken                                       cancellationToken = default)
    {
        var accessToken = await GetValidAccessTokenAsync(false, cancellationToken);
        if (accessToken is null)
        {
            return false;
        }

        var first = await TryUpdateAsync(mediaId, progress, status, accessToken, cancellationToken);
        if (first is not UpdateResult.Unauthorized)
        {
            return first is UpdateResult.Updated;
        }

        accessToken = await GetValidAccessTokenAsync(true, cancellationToken);
        if (accessToken is null)
        {
            return false;
        }

        return await TryUpdateAsync(mediaId, progress, status, accessToken, cancellationToken)
               is UpdateResult.Updated;
    }

    private async Task<string?> GetValidAccessTokenAsync(bool              forceRefresh,
        CancellationToken                                                 cancellationToken)
    {
        if (!_tokenStore.TryGet(_oauth.Provider, out var token) || token is null)
        {
            _logger.LogWarning("AniList token yok; önce giriş yapılmalı");
            return null;
        }

        if (!forceRefresh && !token.NeedsRefresh)
        {
            return token.AccessToken;
        }

        if (string.IsNullOrEmpty(token.RefreshToken))
        {
            _logger.LogWarning("AniList refresh token yok; tekrar giriş gerekli");
            return null;
        }

        var refreshed = await _oauth.RefreshAsync(token.RefreshToken, cancellationToken);
        if (refreshed is null)
        {
            _logger.LogWarning("AniList token yenileme başarısız; tekrar giriş gerekli");
            return null;
        }

        _tokenStore.Set(refreshed);
        return refreshed.AccessToken;
    }

    private enum UpdateResult
    {
        Updated,
        Unauthorized,
        Failed
    }

    private async Task<UpdateResult> TryUpdateAsync(int               mediaId,
        int                                                         progress,
        AniListListStatus                                           status,
        string                                                      accessToken,
        CancellationToken                                           cancellationToken)
    {
        const string query = """
                             mutation ($mediaId: Int, $progress: Int, $status: MediaListStatus) {
                               SaveMediaListEntry(mediaId: $mediaId, progress: $progress, status: $status) {
                                 id
                                 progress
                                 status
                               }
                             }
                             """;

        var requestBody = new
        {
            query,
            variables = new
            {
                mediaId,
                progress,
                status = status switch
                {
                    AniListListStatus.Current   => "CURRENT",
                    AniListListStatus.Completed => "COMPLETED",
                    AniListListStatus.Paused    => "PAUSED",
                    AniListListStatus.Dropped   => "DROPPED",
                    AniListListStatus.Planning  => "PLANNING",
                    _                           => "REPEATING"
                }
            }
        };

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://graphql.anilist.co");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8,
                                                "application/json");

            using var response = await _httpClient.SendAsync(request, cancellationToken);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return UpdateResult.Unauthorized;
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                _logger.LogWarning("AniList 429; liste güncellemesi kuyrukta tekrar denenecek");
                return UpdateResult.Failed;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("AniList liste güncelleme başarısız: {Status}", (int)response.StatusCode);
                return UpdateResult.Failed;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);

            return doc.RootElement.TryGetProperty("data", out var data)
                   && data.TryGetProperty("SaveMediaListEntry", out var entry)
                   && entry.ValueKind == JsonValueKind.Object
                       ? UpdateResult.Updated
                       : UpdateResult.Failed;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "AniList liste güncelleme hata verdi");
            return UpdateResult.Failed;
        }
    }
}
