using Microsoft.Extensions.Logging;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Globalization;
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
                    : null;
            var japaneseTitle =
                animeRoot.TryGetProperty("name_japanese", out var japName)
                && !string.IsNullOrWhiteSpace(japName.GetString())
                    ? japName.GetString()
                    : null;

            var details = new AnimeDetails
            {
                Title         = title,
                EnglishTitle  = englishTitle,
                RomajiTitle   = romajiTitle,
                JapaneseTitle = japaneseTitle,
                Summary       = animeRoot.TryGetProperty("description", out var desc) ? desc.GetString() ?? "" : ""
            };

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

            if (!string.IsNullOrEmpty(episodesJson))
            {
                using var episodesDoc = JsonDocument.Parse(episodesJson);
                if (episodesDoc.RootElement.TryGetProperty("episodes", out var episodesArray)
                    && episodesArray.ValueKind == JsonValueKind.Array)
                {
                    var uniqueEpisodes = new Dictionary<string, (JsonElement element, int versions, bool hasCdn)>();

                    foreach (var ep in episodesArray.EnumerateArray())
                    {
                        var epNum = GetInt32Value(ep, "episode_num", GetInt32Value(ep, "bolum_no"));
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
                        var epNum    = GetInt32Value(ep, "episode_num", GetInt32Value(ep, "bolum_no"));
                        var sNum     = GetInt32Value(ep, "season_num", GetInt32Value(ep, "sezon_no", 1));

                        var epName = ep.TryGetProperty("episode_name", out var nameProp)
                                         ? nameProp.GetString()
                                         : ep.TryGetProperty("baslik", out var baslikProp)
                                             ? baslikProp.GetString()
                                             : null;

                        var numText = $"S{sNum}E{epNum.ToString().PadLeft(2, '0')}";
                        var epTitle = string.IsNullOrEmpty(epName) ? numText : $"{numText} - {epName}";

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

            foreach (var season in parsedSeasons.OrderBy(s => s))
            {
                details.SeasonMappings.Add(new SeasonMapping
                {
                    SeasonNumber = season
                });
            }

            if (!details.SeasonMappings.Any())
            {
                details.SeasonMappings.Add(new SeasonMapping
                {
                    SeasonNumber = 1
                });
            }

            var titleType = animeRoot.TryGetProperty("title_type", out var tt) ? tt.GetString() : "";
            details.Format = "movie".Equals(titleType, StringComparison.OrdinalIgnoreCase)
                                 ? ContentFormat.Movie
                                 : ContentFormat.Tv;

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
                var epNum = GetInt32Value(ep, "episode_num", GetInt32Value(ep, "bolum_no"));
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
                var epNum = GetInt32Value(ep, "episode_num", GetInt32Value(ep, "bolum_no"));
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

                    var hasCdn = false;
                    foreach (var q in qualities)
                    {
                        if (ep.TryGetProperty(q.Key, out var prop) && prop.ValueKind == JsonValueKind.String)
                        {
                            var streamTokenUrl = prop.GetString();
                            if (!string.IsNullOrEmpty(streamTokenUrl))
                            {
                                hasCdn = true;
                                sources.Add(new VideoSource
                                {
                                    Url     = streamTokenUrl,
                                    Quality = q.Quality,
                                    Hoster  = hosterName,
                                    Type    = q.Key == "cdn_m3u8" ? VideoType.M3U8 : VideoType.Mp4,
                                    Group   = groupName,
                                    Headers = new Dictionary<string, string>
                                    {
                                        { "Referer", BaseUrl }
                                    }
                                });
                            }
                        }
                    }

                    if (!hasCdn
                        && ep.TryGetProperty("url", out var urlProp)
                        && urlProp.ValueKind == JsonValueKind.String)
                    {
                        var urlValue = urlProp.GetString();
                        if (!string.IsNullOrEmpty(urlValue))
                        {
                            sources.Add(new VideoSource
                            {
                                Url = urlValue,
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

    [GeneratedRegex(@"watch/(\d+)/s(\d+)e(\d+)")]
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

    private async Task<string> FetchApiAsync(string url, CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Session-Id", _sessionId);
        request.Headers.Add("Referer", BaseUrl + "/");
        request.Headers.Add("Origin", BaseUrl);
        request.Headers.Add("Accept", "application/json");

        var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
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
            var coreId = GetInt32Value(item, "animpow_core_id").ToString();
            if (coreId == "0")
            {
                return null;
            }

            double? parsedScore = null;
            if (item.TryGetProperty("jikan_score", out var sc))
            {
                switch (sc.ValueKind)
                {
                    case JsonValueKind.Number:
                        parsedScore = sc.GetDouble();
                        break;
                    case JsonValueKind.String:
                        {
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
            }

            var yearVal = item.TryGetProperty("year", out _) ? GetInt32Value(item, "year").ToString() : null;

            var title = item.GetProperty("name").GetString() ?? "";
            var englishTitle =
                item.TryGetProperty("name_english", out var eng) && !string.IsNullOrWhiteSpace(eng.GetString())
                    ? eng.GetString()
                    : null;

            return new SearchResult
            {
                Id           = coreId,
                Title        = title,
                EnglishTitle = englishTitle,
                PosterUrl    = item.TryGetProperty("poster", out var pst) ? pst.GetString() : null,
                Url          = $"{BaseUrl}/anime/{coreId}",
                ProviderName = Name,
                Type         = ProviderType.Anime,
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
