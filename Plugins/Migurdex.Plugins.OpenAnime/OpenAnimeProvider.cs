using Microsoft.Extensions.Logging;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Migurdex.Plugins.OpenAnime;

public class OpenAnimeProvider : IAnimeProvider
{
    private const string SecretKey   = "pvxFURPt1O76RX9yoPPE4R2AHgkh";
    private const string ApiUrl      = "https://api.openani.me";
    private const string KyubiApiUrl = "https://kyubi-san.vercel.app/api/decode";

    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/147.0.0.0 Safari/537.36";

    private readonly Dictionary<string, string> _defaultHeaders = new()
    {
        ["Referer"]    = "https://openani.me/",
        ["User-Agent"] = BrowserUserAgent
    };

    private readonly HttpClient                 _httpClient;
    private readonly ILogger<OpenAnimeProvider> _logger;
    private readonly SemaphoreSlim              _requestSemaphore = new(5, 5);
    private readonly SemaphoreSlim              _tokenSemaphore   = new(1, 1);
    private          string?                    _gatewayToken;
    private          DateTimeOffset             _lastSigningTime = DateTimeOffset.MinValue;

    private string?        _sessionId;
    private DateTimeOffset _tokenExpiry = DateTimeOffset.MinValue;

    public OpenAnimeProvider(ISharedBridge bridge)
    {
        _httpClient = bridge.CreateHttpClient(o =>
        {
            o.Emulation = BrowserEmulation.Chrome147;
        });

        _logger = bridge.CreateLogger<OpenAnimeProvider>();
    }

    public string       Name    => "OpenAnime";
    public string       BaseUrl => "https://openani.me";
    public ProviderType Type    => ProviderType.Anime;

    public async Task<List<SearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        try
        {
            var url      = $"{ApiUrl}/anime/search?q={Uri.EscapeDataString(query)}";
            var response = await SendRequestAsync<List<OpenAnimeSearchItem>>(url, cancellationToken: cancellationToken);

            if (response is null)
            {
                return [];
            }

            return response.Select(item =>
                           {
                               var title = new[] { item.Turkish, item.Romaji, item.English }
                                               .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))
                                           ?? string.Empty;
                               var englishTitle = !string.IsNullOrWhiteSpace(item.English) ? item.English : null;
                               var romajiTitle  = !string.IsNullOrWhiteSpace(item.Romaji) ? item.Romaji : null;

                               var fmt = ParseFormat(item.Type);
                               if (fmt is ContentFormat.Tv or ContentFormat.Unknown
                                   && (AnimeDetails.IsMovieTitle(title) || AnimeDetails.IsMovieTitle(englishTitle)))
                               {
                                   fmt = ContentFormat.Movie;
                               }

                               return new SearchResult
                               {
                                   Id           = item.Slug ?? string.Empty,
                                   Title        = title,
                                   EnglishTitle = englishTitle,
                                   RomajiTitle  = romajiTitle,
                                   PosterUrl    = item.Pictures?.Avatar,
                                   Url          = $"{BaseUrl}/anime/{item.Slug}",
                                   ProviderName = Name,
                                   Type         = ProviderType.Anime,
                                   Format       = fmt,
                                   Score        = item.TmdbScore
                               };
                           })
                           .ToList();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "search failed for query: {Query}", query);
            throw;
        }
    }

    public async Task<AnimeDetails> GetDetailsAsync(string animeId, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"{ApiUrl}/anime/{animeId}";
            var response = await SendRequestAsync<OpenAnimeDetails>(url, cancellationToken: cancellationToken)
                           ?? throw new HttpRequestException($"OpenAnime boş detay yanıtı döndü ({animeId}).");

            var title = new[] { response.Turkish, response.Romaji, response.English }
                            .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))
                        ?? animeId;
            var englishTitle = !string.IsNullOrWhiteSpace(response.English) ? response.English : null;
            var romajiTitle  = !string.IsNullOrWhiteSpace(response.Romaji) ? response.Romaji : null;
            var japaneseTitle =
                !string.IsNullOrWhiteSpace(response.Japanese) ? response.Japanese : response.OriginalName;

            var details = new AnimeDetails
            {
                Title         = title,
                EnglishTitle  = englishTitle,
                RomajiTitle   = romajiTitle,
                JapaneseTitle = japaneseTitle,
                Summary       = response.Summary ?? string.Empty,
                Format        = ParseFormat(response.Type)
            };

            if (response.Seasons is not null)
            {
                foreach (var season in response.Seasons)
                {
                    if (season.MalID is > 0)
                    {
                        details.SeasonMappings.Add(new SeasonMapping
                        {
                            SeasonNumber  = season.SeasonNumber,
                            MyAnimeListId = season.MalID.Value.ToString()
                        });
                    }
                }
            }

            if (details.SeasonMappings.Count == 0 && response.MalID is > 0)
            {
                details.SeasonMappings.Add(new SeasonMapping
                {
                    SeasonNumber  = 1,
                    MyAnimeListId = response.MalID.Value.ToString()
                });
            }

            if (string.Equals(response.Type, "movie", StringComparison.OrdinalIgnoreCase))
            {
                details.Episodes.Add(new Episode
                {
                    Id     = $"{animeId}/1/1",
                    Title  = details.Title,
                    Number = 1,
                    Season = 1
                });
            }
            else if (response.Seasons is not null)
            {
                var seasonTasks = response.Seasons
                                          .Where(s => s.HasEpisode)
                                          .Select(async season =>
                                          {
                                              var eps = await GetSeasonEpisodesAsync(
                                                            animeId,
                                                            season.SeasonNumber,
                                                            cancellationToken);

                                              return eps.Select(ep => new Episode
                                              {
                                                  Id     = $"{animeId}/{season.SeasonNumber}/{ep.Number}",
                                                  Title  = ep.Title,
                                                  Number = ep.Number,
                                                  Season = season.SeasonNumber
                                              });
                                          });

                foreach (var eps in await Task.WhenAll(seasonTasks))
                {
                    details.Episodes.AddRange(eps);
                }
            }

            return details;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "getDetailsAsync failed for animeId: {AnimeId}", animeId);
            throw;
        }
    }

    public async Task<List<string>> GetGroupsAsync(string episodeId, CancellationToken cancellationToken = default)
    {
        var (fansubs, _) = await GetFansubDataAsync(episodeId, cancellationToken);
        return fansubs.Select(x => x.Name).ToList();
    }

    public async Task<List<VideoSource>> GetVideoSourcesAsync(string episodeId,
        string?                                                      group             = null,
        CancellationToken                                            cancellationToken = default)
    {
        var parts = episodeId.Split('/');
        if (parts.Length < 3)
        {
            return [];
        }

        var slug    = parts[0];
        var season  = parts[1];
        var episode = parts[2];

        var (fansubs, discoveredCdnLink) = await GetFansubDataAsync(episodeId, cancellationToken);
        var cdnBase = (discoveredCdnLink ?? string.Empty).TrimEnd('/');
        if (fansubs.Count == 0 || string.IsNullOrEmpty(cdnBase))
        {
            return [];
        }

        var sourcesBag = new ConcurrentBag<VideoSource>();
        var fansubTasks = fansubs
                          .Where(fs => string.IsNullOrEmpty(group)
                                       || string.Equals(fs.Name, group, StringComparison.OrdinalIgnoreCase))
                          .Select(async fs =>
                          {
                              try
                              {
                                  var apiUrl =
                                      $"{ApiUrl}/anime/{slug}/season/{season}/episode/{episode}?fansub={fs.Id}";
                                  var subUrl =
                                      $"{ApiUrl}/anime/{slug}/season/{season}/episode/{episode}/subtitles/{fs.SecureName}?type=ass";

                                  var episodeTask =
                                      SendRequestAsync<OpenAnimeEpisodeResponse>(
                                          apiUrl,
                                          cancellationToken: cancellationToken);
                                  var subtitleTask =
                                      SendRequestAsync<byte[]>(
                                          subUrl,
                                          throwOnError: false,
                                          cancellationToken: cancellationToken);

                                  await Task.WhenAll(episodeTask, subtitleTask);

                                  var response = await episodeTask;
                                  var rawSub   = await subtitleTask;

                                  if (response?.EpisodeData?.Files is null)
                                  {
                                      return;
                                  }

                                  List<Subtitle>? subtitles = null;
                                  if (rawSub is { Length: >= 8 }
                                      && Encoding.ASCII.GetString(rawSub, 0, 8) == "KYUBI2\r\n")
                                  {
                                      var decryptedAss = await DecodeKyubiSubtitleAsync(rawSub, cancellationToken);
                                      if (!string.IsNullOrEmpty(decryptedAss))
                                      {
                                          subtitles =
                                          [
                                              new Subtitle
                                              {
                                                  Url =
                                                      $"data:text/x-ssa;base64,{Convert.ToBase64String(Encoding.UTF8.GetBytes(decryptedAss))}",
                                                  Language = "tr",
                                                  Label    = $"{fs.Name} (Softsub)",
                                                  Format   = "ass"
                                              }
                                          ];
                                      }
                                      else
                                      {
                                          var isMissing = string.IsNullOrEmpty(GetConfiguredApiKey());
                                          subtitles =
                                          [
                                              new Subtitle
                                              {
                                                  Url = isMissing
                                                            ? "migurdex://openanime_sub_locked"
                                                            : "migurdex://openanime_sub_invalid",
                                                  Language = "tr",
                                                  Label = isMissing
                                                              ? $"{fs.Name} [Kilitli: Discord anahtarı girilmedi]"
                                                              : $"{fs.Name} [Kilitli: Anahtar geçersiz/yetkisiz]",
                                                  Format = "ass"
                                              }
                                          ];
                                      }
                                  }

                                  var isLocked =
                                      subtitles?.Any(s => s.Url.StartsWith("migurdex://openanime_sub_")) == true;
                                  var groupLabel = isLocked ? $"🔒 {fs.Name}" : fs.Name;

                                  foreach (var file in response.EpisodeData.Files)
                                  {
                                      if (string.IsNullOrEmpty(file.File))
                                      {
                                          continue;
                                      }

                                      sourcesBag.Add(new VideoSource
                                      {
                                          Url       = $"{cdnBase}/{slug}/{season}/{file.File.TrimStart('/')}",
                                          Quality   = file.Resolution is > 0 ? $"{file.Resolution}p" : "Auto",
                                          Type      = VideoType.Mp4,
                                          Hoster    = "OpenAnime",
                                          Group     = groupLabel,
                                          Headers   = new Dictionary<string, string>(_defaultHeaders),
                                          Subtitles = subtitles is not null ? [.. subtitles] : null
                                      });
                                  }
                              }
                              catch (OperationCanceledException)
                              {
                                  throw;
                              }
                              catch (Exception ex)
                              {
                                  _logger.LogWarning(ex, "failed to resolve sources for fansub: {Fansub}", fs.Name);
                              }
                          });

        await Task.WhenAll(fansubTasks);
        return sourcesBag.ToList();
    }

    private async Task<string> EnsureTokenAsync(bool forceRefresh      = false,
        CancellationToken                            cancellationToken = default)
    {
        await _tokenSemaphore.WaitAsync(cancellationToken);
        try
        {
            var now            = DateTimeOffset.UtcNow;
            var needNewSession = forceRefresh || string.IsNullOrEmpty(_sessionId) || now >= _tokenExpiry.AddMinutes(-1);

            if (needNewSession)
            {
                _sessionId       = null;
                _gatewayToken    = null;
                _tokenExpiry     = DateTimeOffset.MinValue;
                _lastSigningTime = DateTimeOffset.MinValue;

                using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiUrl}/session/init")
                {
                    Content = JsonContent.Create(new
                    {
                        cr = Guid.NewGuid().ToString()
                    })
                };
                foreach (var (key, value) in _defaultHeaders)
                {
                    request.Headers.Add(key, value);
                }

                request.Headers.Add("Gateway-Token", "null");
                request.Headers.Add("Origin", "https://openani.me");

                using var response = await _httpClient.SendAsync(request, cancellationToken);
                response.EnsureSuccessStatusCode();

                var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
                var sessionId = result.TryGetProperty("sessionId", out var sessionProp)
                                    ? sessionProp.GetString()
                                    : null;
                if (string.IsNullOrWhiteSpace(sessionId))
                {
                    throw new InvalidOperationException("OpenAnime sessionId alınamadı.");
                }

                _sessionId = sessionId;
                var parts = _sessionId.Split('.');
                if (parts.Length > 1)
                {
                    var       payloadJson = Encoding.UTF8.GetString(DecodeBase64Url(parts[1]));
                    using var doc         = JsonDocument.Parse(payloadJson);
                    if (doc.RootElement.TryGetProperty("exp", out var expProp) && expProp.TryGetInt64(out var expSec))
                    {
                        _tokenExpiry = DateTimeOffset.FromUnixTimeSeconds(expSec);
                    }
                }
            }

            if (forceRefresh
                || needNewSession
                || string.IsNullOrEmpty(_gatewayToken)
                || now >= _lastSigningTime.AddSeconds(30))
            {
                _gatewayToken    = SignLocally(_sessionId!);
                _lastSigningTime = DateTimeOffset.UtcNow;
            }

            return _gatewayToken!;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "token/Session refresh failed");
            throw;
        }
        finally
        {
            _tokenSemaphore.Release();
        }
    }

    private static string SignLocally(string sessionId)
    {
        var       timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var       message   = $"{sessionId}:{timestamp}:openani.me";
        using var hmac      = new HMACSHA256(Encoding.UTF8.GetBytes(SecretKey));
        var       hash      = hmac.ComputeHash(Encoding.UTF8.GetBytes(message));
        var payload = new
        {
            session_id = sessionId,
            timestamp,
            hostname = "openani.me",
            hmac     = Convert.ToHexStringLower(hash)
        };

        return Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)));
    }

    private static byte[] DecodeBase64Url(string base64Url)
    {
        var base64 = base64Url.Replace('-', '+').Replace('_', '/');
        base64 += (base64.Length % 4) switch
        {
            2 => "==",
            3 => "=",
            _ => string.Empty
        };

        return Convert.FromBase64String(base64);
    }

    private async Task<T?> SendRequestAsync<T>(string url,
        HttpMethod?                                   method            = null,
        object?                                       body              = null,
        bool                                          useGateway        = true,
        bool                                          throwOnError      = true,
        CancellationToken                             cancellationToken = default)
    {
        var maxAttempts = useGateway ? 2 : 1;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var gatewayToken = useGateway
                                   ? await EnsureTokenAsync(attempt > 0, cancellationToken)
                                   : null;

            await _requestSemaphore.WaitAsync(cancellationToken);
            try
            {
                using var request = new HttpRequestMessage(method ?? HttpMethod.Get, url);
                foreach (var (key, value) in _defaultHeaders)
                {
                    request.Headers.Add(key, value);
                }

                if (useGateway)
                {
                    request.Headers.Add("Origin", "https://openani.me");
                    request.Headers.Add("Gateway-Token", gatewayToken);
                }

                if (body is not null)
                {
                    request.Content = JsonContent.Create(body);
                }

                using var response = await _httpClient.SendAsync(request, cancellationToken);
                if (useGateway
                    && response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    && attempt == 0)
                {
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    if (throwOnError)
                    {
                        response.EnsureSuccessStatusCode();
                    }

                    return default;
                }

                if (typeof(T) == typeof(byte[]))
                {
                    return (T) (object) await response.Content.ReadAsByteArrayAsync(cancellationToken);
                }

                if (typeof(T) == typeof(string))
                {
                    return (T) (object) await response.Content.ReadAsStringAsync(cancellationToken);
                }

                return await response.Content.ReadFromJsonAsync<T>(cancellationToken);
            }
            finally
            {
                _requestSemaphore.Release();
            }
        }

        throw new HttpRequestException($"OpenAnime isteği başarısız oldu: {url}");
    }

    private async Task<List<SimpleEpisode>> GetSeasonEpisodesAsync(string slug,
        int                                                               seasonNumber,
        CancellationToken                                                 cancellationToken = default)
    {
        try
        {
            var url      = $"{BaseUrl}/anime/{slug}/{seasonNumber}/__data.json?x-sveltekit-invalidated=01";
            var response = await SendRequestAsync<string>(url, useGateway: false, cancellationToken: cancellationToken);

            if (string.IsNullOrEmpty(response))
            {
                return [];
            }

            var data = GetFlatData(response);
            if (data.ValueKind == JsonValueKind.Undefined)
            {
                return [];
            }

            var episodes = new List<SimpleEpisode>();
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var episodesProp = FindProperty(item, "episodes", data);
                if (episodesProp.ValueKind == JsonValueKind.Undefined)
                {
                    continue;
                }

                var epIndices = Resolve(episodesProp, data);
                if (epIndices.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var idxToken in epIndices.EnumerateArray())
                {
                    var epObj = Resolve(idxToken, data);
                    if (epObj.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var resolvedNum = Resolve(FindProperty(epObj, "episodeNumber", data), data);
                    if (resolvedNum.ValueKind != JsonValueKind.Number || !resolvedNum.TryGetInt32(out var epNum))
                    {
                        continue;
                    }

                    var resolvedName = Resolve(FindProperty(epObj, "name", data), data);
                    var epName = resolvedName.ValueKind == JsonValueKind.String
                                     ? resolvedName.GetString() ?? $"{epNum}. Bölüm"
                                     : $"{epNum}. Bölüm";

                    episodes.Add(new SimpleEpisode
                    {
                        Number = epNum,
                        Title  = epName
                    });
                }

                break;
            }

            return episodes.OrderBy(x => x.Number).ToList();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogWarning(ex, "season episodes not found for {Slug} season {Season}", slug, seasonNumber);
            return [];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "failed to get episodes list for {Slug} season {Season}", slug, seasonNumber);
            throw;
        }
    }

    private async Task<(List<OpenAnimeFansubInfo> Fansubs, string? CdnLink)> GetFansubDataAsync(string episodeId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var url      = $"{BaseUrl}/anime/{episodeId}/__data.json?x-sveltekit-invalidated=01";
            var response = await SendRequestAsync<string>(url, useGateway: false, cancellationToken: cancellationToken);

            if (string.IsNullOrEmpty(response))
            {
                return ([], null);
            }

            var data = GetFlatData(response);
            if (data.ValueKind == JsonValueKind.Undefined)
            {
                return ([], null);
            }

            var     fansubs = new List<OpenAnimeFansubInfo>();
            string? cdnLink = null;

            foreach (var line in response.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    FindCdnAndFansubsRecursive(doc.RootElement, data, fansubs, ref cdnLink);
                }
                catch (JsonException)
                {
                    // ignored
                }
            }

            return (fansubs, cdnLink);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "getFansubData failed for episode: {EpisodeId}", episodeId);
            throw;
        }
    }

    private static JsonElement FindProperty(JsonElement obj, string name, JsonElement data)
    {
        if (obj.ValueKind != JsonValueKind.Object)
        {
            return default;
        }

        if (obj.TryGetProperty(name, out var val))
        {
            return val;
        }

        for (var i = 0; i < data.GetArrayLength(); i++)
        {
            if (data[i].ValueKind == JsonValueKind.String
                && data[i].GetString() == name
                && obj.TryGetProperty(i.ToString(), out var indexedVal))
            {
                return indexedVal;
            }
        }

        return default;
    }

    private static JsonElement GetFlatData(string response)
    {
        var lines = response.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("type", out var typeProp)
                    && typeProp.GetString() == "chunk"
                    && doc.RootElement.TryGetProperty("data", out var dataArr)
                    && dataArr.ValueKind == JsonValueKind.Array)
                {
                    return dataArr.Clone();
                }
            }
            catch
            {
                // ignored
            }
        }

        JsonElement bestData = default;
        var         maxLen   = -1;
        foreach (var line in lines)
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                FindDataArray(doc.RootElement, ref bestData, ref maxLen);
            }
            catch
            {
                // ignored
            }
        }

        return bestData;
    }

    private static void FindDataArray(JsonElement element, ref JsonElement bestData, ref int maxLen)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    if (prop is { Name: "data", Value.ValueKind: JsonValueKind.Array })
                    {
                        var len = prop.Value.GetArrayLength();
                        if (len > maxLen)
                        {
                            maxLen   = len;
                            bestData = prop.Value.Clone();
                        }
                    }

                    FindDataArray(prop.Value, ref bestData, ref maxLen);
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    FindDataArray(item, ref bestData, ref maxLen);
                }

                break;
        }
    }

    private static JsonElement Resolve(JsonElement element, JsonElement data)
    {
        if (element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out var index)
            && index >= 0
            && index < data.GetArrayLength())
        {
            return data[index];
        }

        return element;
    }

    private static void FindCdnAndFansubsRecursive(JsonElement element,
        JsonElement                                            data,
        List<OpenAnimeFansubInfo>                              fansubs,
        ref string?                                            cdnLink)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var cdnProp = FindProperty(element, "CDN_LINK", data);
            if (cdnProp.ValueKind != JsonValueKind.Undefined && string.IsNullOrEmpty(cdnLink))
            {
                var resolvedCdn = Resolve(cdnProp, data);
                if (resolvedCdn.ValueKind == JsonValueKind.String)
                {
                    cdnLink = resolvedCdn.GetString();
                }
            }

            var fsProp = FindProperty(element, "fansubs", data);
            if (fsProp.ValueKind != JsonValueKind.Undefined)
            {
                var fsIndices = Resolve(fsProp, data);
                if (fsIndices.ValueKind == JsonValueKind.Array)
                {
                    foreach (var idxToken in fsIndices.EnumerateArray())
                    {
                        var fsObj = Resolve(idxToken, data);
                        if (fsObj.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        var idEl         = Resolve(FindProperty(fsObj, "id", data), data);
                        var nameEl       = Resolve(FindProperty(fsObj, "name", data), data);
                        var secureNameEl = Resolve(FindProperty(fsObj, "secureName", data), data);

                        var id   = idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : null;
                        var name = nameEl.ValueKind == JsonValueKind.String ? nameEl.GetString() : null;
                        var secureName = secureNameEl.ValueKind == JsonValueKind.String
                                             ? secureNameEl.GetString()
                                             : null;

                        if (!string.IsNullOrEmpty(id)
                            && !string.IsNullOrEmpty(name)
                            && fansubs.All(x => x.Id != id))
                        {
                            fansubs.Add(new OpenAnimeFansubInfo
                            {
                                Id         = id,
                                Name       = name,
                                SecureName = secureName ?? string.Empty
                            });
                        }
                    }
                }
            }

            foreach (var prop in element.EnumerateObject())
            {
                FindCdnAndFansubsRecursive(prop.Value, data, fansubs, ref cdnLink);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                FindCdnAndFansubsRecursive(item, data, fansubs, ref cdnLink);
            }
        }
    }

    private static ContentFormat ParseFormat(string? type)
    {
        return type?.ToLowerInvariant() switch
        {
            "tv"    => ContentFormat.Tv,
            "movie" => ContentFormat.Movie,
            "ova"   => ContentFormat.Ova,
            _       => ContentFormat.Tv
        };
    }

    private static string? GetConfiguredApiKey()
    {
        var envKey = Environment.GetEnvironmentVariable("OPENANIME_API_KEY");
        if (!string.IsNullOrWhiteSpace(envKey))
        {
            return envKey.Trim();
        }

        try
        {
            var configPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config",
                "migurdex",
                "config.json");

            if (File.Exists(configPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
                if (doc.RootElement.TryGetProperty("OpenAnimeApiKey", out var prop))
                {
                    var val = prop.GetString();
                    if (!string.IsNullOrWhiteSpace(val))
                    {
                        return val.Trim();
                    }
                }
            }
        }
        catch
        {
            // ignored
        }

        return null;
    }

    private async Task<string?> DecodeKyubiSubtitleAsync(byte[] packageBytes,
        CancellationToken                                       cancellationToken = default)
    {
        try
        {
            var apiKey = GetConfiguredApiKey();
            if (string.IsNullOrEmpty(apiKey))
            {
                _logger.LogWarning(
                    "OpenAnime altyazı anahtarı tanımlı değil. Discord üzerinden alıp Ayarlar menüsünden girebilirsiniz.");
                return null;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, KyubiApiUrl)
            {
                Content = JsonContent.Create(new KyubiDecodePayload
                {
                    EncodedData = Convert.ToBase64String(packageBytes)
                })
            };
            request.Headers.Add("x-api-key", apiKey);

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    _logger.LogWarning(
                        "OpenAnime altyazı anahtarı geçersiz veya yetkisiz. Discord sunucusundan yeni bir anahtar alabilirsiniz.");
                }
                else
                {
                    _logger.LogWarning("kyubi decoder returned status {StatusCode}", response.StatusCode);
                }

                return null;
            }

            var result = await response.Content.ReadFromJsonAsync<KyubiDecodeResponse>(cancellationToken);
            if (result is not { Ok: true } || string.IsNullOrEmpty(result.Ass))
            {
                _logger.LogWarning("kyubi decoder returned invalid response");
                return null;
            }

            return Encoding.UTF8.GetString(Convert.FromBase64String(result.Ass));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to decode kyubi subtitle via remote api");
            return null;
        }
    }

    private sealed class KyubiDecodePayload
    {
        [JsonPropertyName("encodedData")]
        public string EncodedData { get; set; } = string.Empty;
    }

    private sealed class KyubiDecodeResponse
    {
        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        [JsonPropertyName("ass")]
        public string? Ass { get; set; }
    }

    private sealed class SimpleEpisode
    {
        public int    Number { get; set; }
        public string Title  { get; set; } = string.Empty;
    }

    private sealed class OpenAnimeFansubInfo
    {
        public string Id         { get; set; } = string.Empty;
        public string Name       { get; set; } = string.Empty;
        public string SecureName { get; set; } = string.Empty;
    }

    private sealed class OpenAnimeEpisodeResponse
    {
        [JsonPropertyName("episodeData")]
        public OpenAnimeEpisodeData? EpisodeData { get; set; }
    }

    private sealed class OpenAnimeEpisodeData
    {
        [JsonPropertyName("files")]
        public List<OpenAnimeFile>? Files { get; set; }
    }

    private sealed class OpenAnimeFile
    {
        [JsonPropertyName("file")]
        public string? File { get; set; }

        [JsonPropertyName("resolution")]
        public int? Resolution { get; set; }
    }

    private sealed class OpenAnimeSearchItem
    {
        public string?            Slug      { get; set; }
        public string?            English   { get; set; }
        public string?            Turkish   { get; set; }
        public string?            Romaji    { get; set; }
        public OpenAnimePictures? Pictures  { get; set; }
        public string?            Type      { get; set; }
        public double?            TmdbScore { get; set; }
        public string?            Summary   { get; set; }
        public bool?              Is4K      { get; set; }
    }

    private sealed class OpenAnimePictures
    {
        public string? Banner { get; set; }
        public string? Avatar { get; set; }
    }

    private sealed class OpenAnimeDetails
    {
        public string? English      { get; set; }
        public string? Turkish      { get; set; }
        public string? Romaji       { get; set; }
        public string? Japanese     { get; set; }
        public string? OriginalName { get; set; }
        public string? Summary      { get; set; }
        public string? Type         { get; set; }

        [JsonPropertyName("malID")]
        public int? MalID { get; set; }

        public List<OpenAnimeSeason>? Seasons { get; set; }
    }

    private sealed class OpenAnimeSeason
    {
        [JsonPropertyName("season_number")]
        public int SeasonNumber { get; set; }

        [JsonPropertyName("hasEpisode")]
        public bool HasEpisode { get; set; }

        [JsonPropertyName("mal_id")]
        public int? MalID { get; set; }
    }
}
