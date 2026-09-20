using Microsoft.Extensions.Logging;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Migurdex.Plugins.Animpow;

public partial class AnimpowProvider : IAnimeProvider
{
    private readonly SemaphoreSlim            _handshakeLock = new(1, 1);
    private readonly HttpClient               _httpClient;
    private readonly ILogger<AnimpowProvider> _logger;
    private          byte[]?                  _aesKey;
    private          bool                     _handshakeComplete;
    private          string?                  _sessionId;

    public AnimpowProvider(ISharedBridge bridge, ILogger<AnimpowProvider> logger)
    {
        _httpClient = bridge.CreateHttpClient();
        _logger     = logger;
    }

    public string Name    => "AnimPow";
    public string BaseUrl => "https://animpow.com";

    public ProviderType Type => ProviderType.Anime;

    public async Task<List<SearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureHandshakeAsync(cancellationToken);

            var searchUrl =
                $"https://client-api.animpow.com/api/v1/anime/arama?q={Uri.EscapeDataString(query)}&limit=50";

            var decryptedJson = await FetchApiAsync(searchUrl, cancellationToken);
            if (string.IsNullOrEmpty(decryptedJson))
            {
                return [];
            }

            using var doc     = JsonDocument.Parse(decryptedJson);
            var       results = new List<SearchResult>();

            if (doc.RootElement.TryGetProperty("veri", out var dataArray)
                || doc.RootElement.TryGetProperty("data", out dataArray))
            {
                foreach (var item in dataArray.EnumerateArray())
                {
                    var searchResult = ParseItem(item);
                    if (searchResult != null)
                    {
                        results.Add(searchResult);
                    }
                }
            }

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "search failed for query: {Query}", query);

            return [];
        }
    }

    public async Task<AnimeDetails> GetDetailsAsync(string animeId, CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureHandshakeAsync(cancellationToken);

            var animeTask = FetchApiAsync($"https://client-api.animpow.com/api/v1/anime/{animeId}", cancellationToken);
            var episodesTask = FetchApiAsync($"https://client-api.animpow.com/api/v1/anime/{animeId}/bolumler",
                                             cancellationToken);
            var seasonsTask = FetchApiAsync($"https://client-api.animpow.com/api/v1/anime/{animeId}/sezonlar",
                                            cancellationToken);

            await Task.WhenAll(animeTask, episodesTask, seasonsTask);

            var animeJson    = await animeTask;
            var episodesJson = await episodesTask;
            var seasonsJson  = await seasonsTask;

            if (string.IsNullOrEmpty(animeJson))
            {
                return new AnimeDetails();
            }

            using var animeDoc  = JsonDocument.Parse(animeJson);
            var       animeRoot = animeDoc.RootElement.GetProperty("anime");

            var title = animeRoot.GetProperty("name").GetString() ?? animeId;
            var englishTitle =
                animeRoot.TryGetProperty("name_english", out var engName)
                && !string.IsNullOrWhiteSpace(engName.GetString())
                    ? engName.GetString()
                    : null;
            var romajiTitle =
                animeRoot.TryGetProperty("name_romaji", out var romName)
                && !string.IsNullOrWhiteSpace(romName.GetString())
                    ? romName.GetString()
                    : animeRoot.TryGetProperty("name_romanji", out var rom2)
                      && !string.IsNullOrWhiteSpace(rom2.GetString())
                        ? rom2.GetString()
                        : null;
            var japaneseTitle =
                animeRoot.TryGetProperty("name_japanese", out var japName)
                && !string.IsNullOrWhiteSpace(japName.GetString())
                    ? japName.GetString()
                    : animeRoot.TryGetProperty("original_title", out var orig)
                      && !string.IsNullOrWhiteSpace(orig.GetString())
                        ? orig.GetString()
                        : null;

            var poster = animeRoot.TryGetProperty("poster", out var pst) ? pst.GetString() : null;

            var details = new AnimeDetails
            {
                Title         = title,
                EnglishTitle  = englishTitle,
                RomajiTitle   = romajiTitle,
                JapaneseTitle = japaneseTitle,
                PosterUrl     = poster,
                Summary       = animeRoot.TryGetProperty("description", out var desc) ? desc.GetString() ?? "" : ""
            };

            string? rootMalId = null;
            if (animeRoot.TryGetProperty("mal_id", out var malProp))
            {
                rootMalId = malProp.ValueKind == JsonValueKind.Number
                                ? malProp.GetInt32().ToString()
                                : malProp.GetString();
                if (rootMalId == "0" || string.IsNullOrEmpty(rootMalId))
                {
                    rootMalId = null;
                }
            }

            string? rootTmdbId = null;
            if (animeRoot.TryGetProperty("tmdb_id", out var tmdbProp))
            {
                rootTmdbId = tmdbProp.ValueKind == JsonValueKind.Number
                                 ? tmdbProp.GetInt32().ToString()
                                 : tmdbProp.GetString();
                if (rootTmdbId == "0" || string.IsNullOrEmpty(rootTmdbId))
                {
                    rootTmdbId = null;
                }
            }

            string? rootAniListId = null;
            if (animeRoot.TryGetProperty("anilist_id", out var aniProp))
            {
                rootAniListId = aniProp.ValueKind == JsonValueKind.Number
                                    ? aniProp.GetInt32().ToString()
                                    : aniProp.GetString();
                if (rootAniListId == "0" || string.IsNullOrEmpty(rootAniListId))
                {
                    rootAniListId = null;
                }
            }

            var parsedSeasons = new HashSet<int>();

            if (!string.IsNullOrEmpty(seasonsJson))
            {
                using var seasonsDoc = JsonDocument.Parse(seasonsJson);
                if (seasonsDoc.RootElement.TryGetProperty("seasons", out var seasonsArray)
                    && seasonsArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var s in seasonsArray.EnumerateArray())
                    {
                        var sNum = GetInt32Value(s, "season_number", GetInt32Value(s, "sezon_no", 1));
                        parsedSeasons.Add(sNum);
                    }
                }
            }

            var isSeries = !animeRoot.TryGetProperty("is_series", out var isSeriesProp)
                           || isSeriesProp.ValueKind != JsonValueKind.False;
            var titleType = animeRoot.TryGetProperty("title_type", out var tt) ? tt.GetString() : "";
            var isMovie = "movie".Equals(titleType, StringComparison.OrdinalIgnoreCase)
                          || !isSeries
                          || AnimeDetails.IsMovieTitle(title)
                          || AnimeDetails.IsMovieTitle(englishTitle);

            details.Format = isMovie ? ContentFormat.Movie : ContentFormat.Tv;

            if (!string.IsNullOrEmpty(episodesJson))
            {
                using var episodesDoc = JsonDocument.Parse(episodesJson);
                if (episodesDoc.RootElement.TryGetProperty("episodes", out var episodesArray)
                    && episodesArray.ValueKind == JsonValueKind.Array)
                {
                    var uniqueEpisodes = new Dictionary<string, (JsonElement element, int versions, bool hasCdn)>();

                    foreach (var ep in episodesArray.EnumerateArray())
                    {
                        var epNum = GetInt32Value(ep, "episode_num", GetInt32Value(ep, "bolum_no", 1));
                        var sNum  = GetInt32Value(ep, "season_num", GetInt32Value(ep, "sezon_no", 1));

                        var key = $"{sNum}-{epNum}";
                        var hasCdn = (ep.TryGetProperty("animpow_cdn_v1_active", out var cdn1) && cdn1.GetBoolean())
                                     || (ep.TryGetProperty("pro_cdn_active", out var cdn2) && cdn2.GetBoolean());

                        if (uniqueEpisodes.TryGetValue(key, out var existing))
                        {
                            if (hasCdn && !existing.hasCdn)
                            {
                                uniqueEpisodes[key] = (ep, existing.versions + 1, true);
                            }
                            else
                            {
                                uniqueEpisodes[key] = (existing.element, existing.versions + 1, existing.hasCdn);
                            }
                        }
                        else
                        {
                            uniqueEpisodes[key] = (ep, 1, hasCdn);
                        }
                    }

                    foreach (var kvp in uniqueEpisodes)
                    {
                        var ep       = kvp.Value.element;
                        var sourceId = GetStringValue(ep, "id");
                        var epNum    = GetInt32Value(ep, "episode_num", GetInt32Value(ep, "bolum_no", 1));
                        var sNum     = GetInt32Value(ep, "season_num", GetInt32Value(ep, "sezon_no", 1));

                        var epName = ep.TryGetProperty("episode_name", out var nameProp)
                                         ? nameProp.GetString()
                                         : ep.TryGetProperty("baslik", out var baslikProp)
                                             ? baslikProp.GetString()
                                             : null;

                        string epTitle;
                        if (details.Format == ContentFormat.Movie && uniqueEpisodes.Count <= 1)
                        {
                            epTitle = "Film";
                        }
                        else
                        {
                            var numText = $"S{sNum}E{epNum.ToString().PadLeft(2, '0')}";
                            epTitle = string.IsNullOrEmpty(epName) ? numText : $"{numText} - {epName}";
                        }

                        parsedSeasons.Add(sNum);

                        details.Episodes.Add(new Episode
                        {
                            Id     = $"watch/{animeId}/s{sNum}e{epNum}?source={sourceId}",
                            Title  = epTitle,
                            Number = epNum,
                            Season = sNum
                        });
                    }
                }
            }

            if (details.Format == ContentFormat.Movie && !details.Episodes.Any())
            {
                details.Episodes.Add(new Episode
                {
                    Id     = $"watch/{animeId}/s1e1",
                    Title  = "Film",
                    Number = 1,
                    Season = 1
                });
            }

            foreach (var season in parsedSeasons.OrderBy(s => s))
            {
                details.SeasonMappings.Add(new SeasonMapping
                {
                    SeasonNumber  = season,
                    AniListId     = season == 1 ? rootAniListId : null,
                    MyAnimeListId = season == 1 ? rootMalId : null,
                    TmdbId        = rootTmdbId
                });
            }

            if (!details.SeasonMappings.Any())
            {
                details.SeasonMappings.Add(new SeasonMapping
                {
                    SeasonNumber  = 1,
                    AniListId     = rootAniListId,
                    MyAnimeListId = rootMalId,
                    TmdbId        = rootTmdbId
                });
            }

            details.Episodes = details.Episodes
                                      .OrderBy(e => e.Season ?? 1)
                                      .ThenBy(e => e.Number)
                                      .ToList();

            return details;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "getDetailsAsync failed for anime: {AnimeId}", animeId);

            return new AnimeDetails();
        }
    }

    public async Task<List<string>> GetGroupsAsync(string episodeId, CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureHandshakeAsync(cancellationToken);

            var match = WatchPathRegex().Match(episodeId);
            if (!match.Success)
            {
                return [];
            }

            var animeId    = match.Groups[1].Value;
            var seasonNum  = int.Parse(match.Groups[2].Value);
            var episodeNum = int.Parse(match.Groups[3].Value);

            var episodesJson = await FetchApiAsync($"https://client-api.animpow.com/api/v1/anime/{animeId}/bolumler",
                                                   cancellationToken);
            if (string.IsNullOrEmpty(episodesJson))
            {
                return [];
            }

            using var doc = JsonDocument.Parse(episodesJson);
            if (!doc.RootElement.TryGetProperty("episodes", out var episodesArray)
                || episodesArray.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var groups = new List<string>();
            foreach (var ep in episodesArray.EnumerateArray())
            {
                var epNum = GetInt32Value(ep, "episode_num", GetInt32Value(ep, "bolum_no", 1));
                var sNum  = GetInt32Value(ep, "season_num", GetInt32Value(ep, "sezon_no", 1));

                if (sNum == seasonNum && epNum == episodeNum)
                {
                    var groupName =
                        ep.TryGetProperty("fansub_name", out var fansubProp) ? fansubProp.GetString() : null;
                    if (!string.IsNullOrEmpty(groupName) && !groups.Contains(groupName))
                    {
                        groups.Add(groupName);
                    }
                }
            }

            return groups;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "getGroupsAsync failed for {EpisodeId}", episodeId);

            return [];
        }
    }

    public async Task<List<VideoSource>> GetVideoSourcesAsync(string episodeId,
        string?                                                      group             = null,
        CancellationToken                                            cancellationToken = default)
    {
        try
        {
            await EnsureHandshakeAsync(cancellationToken);

            var match = WatchPathRegex().Match(episodeId);
            if (!match.Success)
            {
                return [];
            }

            var animeId    = match.Groups[1].Value;
            var seasonNum  = int.Parse(match.Groups[2].Value);
            var episodeNum = int.Parse(match.Groups[3].Value);

            var episodesJson = await FetchApiAsync($"https://client-api.animpow.com/api/v1/anime/{animeId}/bolumler",
                                                   cancellationToken);
            if (string.IsNullOrEmpty(episodesJson))
            {
                return [];
            }

            using var doc = JsonDocument.Parse(episodesJson);
            if (!doc.RootElement.TryGetProperty("episodes", out var episodesArray)
                || episodesArray.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var sources = new List<VideoSource>();
            foreach (var ep in episodesArray.EnumerateArray())
            {
                var epNum = GetInt32Value(ep, "episode_num", GetInt32Value(ep, "bolum_no", 1));
                var sNum  = GetInt32Value(ep, "season_num", GetInt32Value(ep, "sezon_no", 1));

                if (sNum == seasonNum && epNum == episodeNum)
                {
                    var groupName = ep.TryGetProperty("fansub_name", out var fansubProp)
                                        ? fansubProp.GetString() ?? ""
                                        : "";

                    if (!string.IsNullOrEmpty(group) && !groupName.Equals(group, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var hosterName = ep.TryGetProperty("video_source_name", out var hosterProp)
                                         ? hosterProp.GetString() ?? "AnimPow"
                                         : "AnimPow";

                    var qualities = new List<(string Key, string Quality)>
                    {
                        ("cdn_mp4_1080", "1080p"),
                        ("cdn_mp4_720", "720p"),
                        ("cdn_mp4_480", "480p"),
                        ("cdn_m3u8", "Multi")
                    };

                    foreach (var q in qualities)
                    {
                        if (ep.TryGetProperty(q.Key, out var prop) && prop.ValueKind == JsonValueKind.String)
                        {
                            var streamTokenUrl = prop.GetString();
                            if (!string.IsNullOrEmpty(streamTokenUrl))
                            {
                                sources.Add(new VideoSource
                                {
                                    Url     = streamTokenUrl,
                                    Quality = q.Quality,
                                    Hoster  = hosterName,
                                    Type    = q.Key == "cdn_m3u8" ? VideoType.M3U8 : VideoType.Mp4,
                                    Group   = groupName,
                                    Headers = new Dictionary<string, string>
                                    {
                                        { "Referer", BaseUrl + "/" }
                                    }
                                });
                            }
                        }
                    }

                    if (ep.TryGetProperty("url", out var urlProp) && urlProp.ValueKind == JsonValueKind.String)
                    {
                        var urlValue = urlProp.GetString();
                        if (!string.IsNullOrEmpty(urlValue))
                        {
                            var effectiveUrl = urlValue;
                            if (urlValue.Contains("/api/sibnet?url=", StringComparison.OrdinalIgnoreCase))
                            {
                                var uIdx = urlValue.IndexOf("url=", StringComparison.OrdinalIgnoreCase);
                                if (uIdx >= 0)
                                {
                                    var rawUrl = urlValue[(uIdx + 4)..];
                                    effectiveUrl = Uri.UnescapeDataString(rawUrl);
                                }
                            }

                            sources.Add(new VideoSource
                            {
                                Url = effectiveUrl,
                                Quality = ep.TryGetProperty("quality", out var qualProp)
                                              ? qualProp.GetString() ?? ""
                                              : "",
                                Hoster = hosterName,
                                Type   = VideoType.Embed,
                                Group  = groupName
                            });
                        }
                    }
                }
            }

            return sources;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "failed to load watch sources for {EpisodeId}", episodeId);

            return [];
        }
    }

    [GeneratedRegex(@"watch/([^/?]+)/s(\d+)e(\d+)")]
    private static partial Regex WatchPathRegex();

    private async Task EnsureHandshakeAsync(CancellationToken cancellationToken = default)
    {
        if (_handshakeComplete)
        {
            return;
        }

        await _handshakeLock.WaitAsync(cancellationToken);
        try
        {
            if (_handshakeComplete)
            {
                return;
            }

            _logger.LogInformation("API handshake starting...");

            var rsaKeyRequest =
                new HttpRequestMessage(HttpMethod.Get, "https://client-api.animpow.com/api/v1/auth/public-key");

            rsaKeyRequest.Headers.Add("Referer", BaseUrl + "/");
            rsaKeyRequest.Headers.Add("Origin", BaseUrl);

            var rsaKeyResponse = await _httpClient.SendAsync(rsaKeyRequest, cancellationToken);
            rsaKeyResponse.EnsureSuccessStatusCode();

            var       pubKeyJson   = await rsaKeyResponse.Content.ReadAsStringAsync(cancellationToken);
            using var pubKeyDoc    = JsonDocument.Parse(pubKeyJson);
            var       publicKeyPem = pubKeyDoc.RootElement.GetProperty("publicKey").GetString();
            if (string.IsNullOrEmpty(publicKeyPem))
            {
                throw new InvalidOperationException("Can not get AnimPow RSA Public Key.");
            }

            var aesKey = new byte[32];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(aesKey);
            }

            var sessionId = Guid.NewGuid().ToString();

            using var rsa = RSA.Create();
            rsa.ImportFromPem(publicKeyPem);

            var encryptedAesKey    = rsa.Encrypt(aesKey, RSAEncryptionPadding.OaepSHA256);
            var encryptedAesKeyB64 = Convert.ToBase64String(encryptedAesKey);

            var handshakePayload = new
            {
                sessionId,
                encryptedKey = encryptedAesKeyB64
            };

            var handshakeRequest =
                new HttpRequestMessage(HttpMethod.Post, "https://client-api.animpow.com/api/v1/auth/handshake");

            handshakeRequest.Headers.Add("Referer", BaseUrl + "/");
            handshakeRequest.Headers.Add("Origin", BaseUrl);
            handshakeRequest.Content = new StringContent(
                JsonSerializer.Serialize(handshakePayload),
                Encoding.UTF8,
                "application/json"
            );

            var handshakeResponse = await _httpClient.SendAsync(handshakeRequest, cancellationToken);
            handshakeResponse.EnsureSuccessStatusCode();

            _aesKey            = aesKey;
            _sessionId         = sessionId;
            _handshakeComplete = true;

            _logger.LogInformation("API handshake completed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "API handshake error");
            throw;
        }
        finally
        {
            _handshakeLock.Release();
        }
    }

    private string DecryptPayload(string ivB64, string authTagB64, string dataB64)
    {
        if (_aesKey == null)
        {
            throw new InvalidOperationException("AES key is not ready.");
        }

        var iv         = Convert.FromBase64String(ivB64);
        var authTag    = Convert.FromBase64String(authTagB64);
        var ciphertext = Convert.FromBase64String(dataB64);

        using var aesGcm    = new AesGcm(_aesKey, 16);
        var       plaintext = new byte[ciphertext.Length];

        aesGcm.Decrypt(iv, ciphertext, authTag, plaintext);

        return Encoding.UTF8.GetString(plaintext);
    }

    private async Task<string> FetchApiAsync(string url,
        CancellationToken                           cancellationToken = default,
        bool                                        isRetry           = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Session-Id", _sessionId);
        request.Headers.Add("Referer", BaseUrl + "/");
        request.Headers.Add("Origin", BaseUrl);
        request.Headers.Add("Accept", "application/json");

        var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            if (!isRetry
                && (response.StatusCode == HttpStatusCode.BadRequest
                    || response.StatusCode == HttpStatusCode.Unauthorized))
            {
                _logger.LogWarning("AnimPow session invalid or expired ({Status}). Re-handshaking...",
                                   response.StatusCode);
                _handshakeComplete = false;
                await EnsureHandshakeAsync(cancellationToken);
                return await FetchApiAsync(url, cancellationToken, true);
            }

            return string.Empty;
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrEmpty(json))
        {
            return string.Empty;
        }

        using var initDoc = JsonDocument.Parse(json);
        var       root    = initDoc.RootElement;
        if (root.TryGetProperty("iv", out var ivProp)
            && root.TryGetProperty("authTag", out var tagProp)
            && root.TryGetProperty("data", out var dataProp))
        {
            return DecryptPayload(ivProp.GetString()!, tagProp.GetString()!, dataProp.GetString()!);
        }

        return json;
    }

    private static int GetInt32Value(JsonElement element, string propertyName, int defaultValue = 0)
    {
        if (element.TryGetProperty(propertyName, out var prop))
        {
            switch (prop.ValueKind)
            {
                case JsonValueKind.Number:
                    return prop.GetInt32();
                case JsonValueKind.String when int.TryParse(prop.GetString(), out var val):
                    return val;
            }
        }

        return defaultValue;
    }

    private static string GetStringValue(JsonElement element, string propertyName, string defaultValue = "")
    {
        if (element.TryGetProperty(propertyName, out var prop))
        {
            switch (prop.ValueKind)
            {
                case JsonValueKind.String:
                    return prop.GetString() ?? defaultValue;
                case JsonValueKind.Number:
                    return prop.GetRawText();
            }
        }

        return defaultValue;
    }

    private SearchResult? ParseItem(JsonElement item)
    {
        try
        {
            var coreId = GetStringValue(item, "animpow_core_id");
            if (string.IsNullOrWhiteSpace(coreId))
            {
                coreId = GetStringValue(item, "main_anime_id");
            }

            if (string.IsNullOrWhiteSpace(coreId))
            {
                coreId = GetStringValue(item, "uuid");
            }

            if (string.IsNullOrWhiteSpace(coreId))
            {
                return null;
            }

            double? parsedScore = null;
            if (item.TryGetProperty("jikan_score", out var sc) || item.TryGetProperty("vote_average", out sc))
            {
                switch (sc.ValueKind)
                {
                    case JsonValueKind.Number:
                        parsedScore = sc.GetDouble();
                        break;
                    case JsonValueKind.String:
                        if (double.TryParse(sc.GetString()?.Replace(",", "."),
                                            NumberStyles.Any,
                                            CultureInfo.InvariantCulture,
                                            out var scoreVal))
                        {
                            parsedScore = scoreVal;
                        }

                        break;
                }
            }

            string? yearVal = null;
            if (item.TryGetProperty("year", out var yrProp))
            {
                var yInt = GetInt32Value(item, "year");
                if (yInt > 0)
                {
                    yearVal = yInt.ToString();
                }
            }
            else if (item.TryGetProperty("release_date", out var rdProp) && rdProp.ValueKind == JsonValueKind.String)
            {
                var rdStr = rdProp.GetString();
                if (!string.IsNullOrEmpty(rdStr) && DateTime.TryParse(rdStr, out var parsedDate))
                {
                    yearVal = parsedDate.Year.ToString();
                }
            }

            var title = item.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "";
            var englishTitle =
                item.TryGetProperty("name_english", out var eng) && !string.IsNullOrWhiteSpace(eng.GetString())
                    ? eng.GetString()
                    : null;
            var romajiTitle =
                item.TryGetProperty("name_romanji", out var rom) && !string.IsNullOrWhiteSpace(rom.GetString())
                    ? rom.GetString()
                    : item.TryGetProperty("title_romaji", out var rom2) && !string.IsNullOrWhiteSpace(rom2.GetString())
                        ? rom2.GetString()
                        : null;

            var titleType = item.TryGetProperty("title_type", out var tt)
                                ? tt.GetString()
                                : item.TryGetProperty("type", out var tProp)
                                    ? tProp.GetString()
                                    : "";

            var isSeries = !item.TryGetProperty("is_series", out var isSeriesProp)
                           || isSeriesProp.ValueKind != JsonValueKind.False;
            var isMovie = "movie".Equals(titleType, StringComparison.OrdinalIgnoreCase)
                          || !isSeries
                          || AnimeDetails.IsMovieTitle(title)
                          || AnimeDetails.IsMovieTitle(englishTitle);

            var poster = item.TryGetProperty("poster", out var pst)
                             ? pst.GetString()
                             : item.TryGetProperty("poster_path", out var pstPath)
                                 ? pstPath.GetString()
                                 : null;

            return new SearchResult
            {
                Id           = coreId,
                Title        = title,
                EnglishTitle = englishTitle,
                RomajiTitle  = romajiTitle,
                PosterUrl    = poster,
                Url          = $"{BaseUrl}/anime/{coreId}",
                ProviderName = Name,
                Type         = ProviderType.Anime,
                Format       = isMovie ? ContentFormat.Movie : ContentFormat.Tv,
                Year         = yearVal,
                Score        = parsedScore
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to parse search result item");
            return null;
        }
    }
}
