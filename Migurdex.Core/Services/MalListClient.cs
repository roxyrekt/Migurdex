using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Migurdex.Shared.Interfaces;
using System.Net;
using System.Net.Http.Headers;

namespace Migurdex.Core.Services;

public sealed class MalListClient
{
    private readonly IOAuthFlow             _oauth;
    private readonly OAuthTokenStore        _tokenStore;
    private readonly HttpClient             _httpClient;
    private readonly ILogger<MalListClient> _logger;

    public MalListClient(ISharedBridge bridge,
        IOAuthFlow                     oauth,
        OAuthTokenStore                tokenStore,
        ILogger<MalListClient>?        logger = null)
    {
        _oauth      = oauth;
        _tokenStore = tokenStore;
        _httpClient = bridge.CreateHttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Migurdex/1.0");
        _logger = logger ?? NullLogger<MalListClient>.Instance;
    }

    public async Task<bool> UpdateProgressAsync(int animeId,
        int                                         progress,
        bool                                        isCompleted,
        CancellationToken                           cancellationToken = default)
    {
        var accessToken = await GetValidAccessTokenAsync(false, cancellationToken);
        if (accessToken is null)
        {
            return false;
        }

        var first = await TryUpdateAsync(animeId, progress, isCompleted, accessToken, cancellationToken);
        if (first is not UpdateResult.Unauthorized)
        {
            return first is UpdateResult.Updated;
        }

        accessToken = await GetValidAccessTokenAsync(true, cancellationToken);
        if (accessToken is null)
        {
            return false;
        }

        return await TryUpdateAsync(animeId, progress, isCompleted, accessToken, cancellationToken)
                   is UpdateResult.Updated;
    }

    private async Task<string?> GetValidAccessTokenAsync(bool forceRefresh,
        CancellationToken                                     cancellationToken)
    {
        if (!_tokenStore.TryGet(_oauth.Provider, out var token) || token is null)
        {
            _logger.LogWarning("MyAnimeList token yok; önce giriş yapılmalı");
            return null;
        }

        if (!forceRefresh && !token.NeedsRefresh)
        {
            return token.AccessToken;
        }

        if (string.IsNullOrEmpty(token.RefreshToken))
        {
            _logger.LogWarning("MyAnimeList refresh token yok; tekrar giriş gerekli");
            return null;
        }

        var refreshed = await _oauth.RefreshAsync(token.RefreshToken, cancellationToken);
        if (refreshed is null)
        {
            _logger.LogWarning("MyAnimeList token yenileme başarısız; tekrar giriş gerekli");
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

    private async Task<UpdateResult> TryUpdateAsync(int animeId,
        int                                             progress,
        bool                                            isCompleted,
        string                                          accessToken,
        CancellationToken                               cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["status"]               = isCompleted ? "completed" : "watching",
            ["num_watched_episodes"] = progress.ToString()
        };

        try
        {
            var       url     = $"https://api.myanimelist.net/v2/anime/{animeId}/my_list_status";
            using var request = new HttpRequestMessage(HttpMethod.Put, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Content               = new FormUrlEncodedContent(form);

            using var response = await _httpClient.SendAsync(request, cancellationToken);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return UpdateResult.Unauthorized;
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                _logger.LogWarning("MyAnimeList 429; liste güncellemesi kuyrukta tekrar denenecek");
                return UpdateResult.Failed;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("MyAnimeList liste güncelleme başarısız: {Status}", (int) response.StatusCode);
                return UpdateResult.Failed;
            }

            return UpdateResult.Updated;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "MyAnimeList liste güncelleme hata verdi");
            return UpdateResult.Failed;
        }
    }
}
