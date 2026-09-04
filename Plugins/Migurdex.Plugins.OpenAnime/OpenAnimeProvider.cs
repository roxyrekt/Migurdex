using Microsoft.Extensions.Logging;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Migurdex.Plugins.OpenAnime;

public class OpenAnimeProvider : IAnimeProvider
{
    private const string SecretKey = "pvxFURPt1O76RX9yoPPE4R2AHgkh";
    private const string ApiUrl    = "https://api.openani.me";

    private static readonly byte[] _invTable = BuildInvTable();

    private readonly Dictionary<string, string> _defaultHeaders = new()
    {
        { "Referer", "https://openani.me/" },
        {
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"
        }
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
        _httpClient = bridge.CreateHttpClient();
        _logger     = bridge.CreateLogger<OpenAnimeProvider>();
    }

    public string       Name    => "OpenAnime";
    public string       BaseUrl => "https://openani.me";
    public ProviderType Type    => ProviderType.Anime;

    public async Task<List<SearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        try
        {
            var url      = $"{ApiUrl}/anime/search?q={Uri.EscapeDataString(query)}";
            var response = await SendRequestAsync<List<OpenAnimeSearchItem>>(url, cancellationToken: cancellationToken);

            if (response == null)
            {
                return [];
            }

            return response.Select(item =>
                           {
                               var title        = item.Turkish ?? item.Romaji ?? item.English ?? "";
                               var englishTitle = !string.IsNullOrWhiteSpace(item.English) ? item.English : null;
                               var romajiTitle  = !string.IsNullOrWhiteSpace(item.Romaji) ? item.Romaji : null;

                               return new SearchResult
                               {
                                   Id           = item.Slug ?? "",
                                   Title        = title,
                                   EnglishTitle = englishTitle,
                                   RomajiTitle  = romajiTitle,
                                   PosterUrl    = item.Pictures?.Avatar,
                                   Url          = $"{BaseUrl}/anime/{item.Slug}",
                                   ProviderName = Name,
                                   Type         = ProviderType.Anime,
                                   Score        = item.TmdbScore
                               };
                           })
                           .ToList();
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
            var url      = $"{ApiUrl}/anime/{animeId}";
            var response = await SendRequestAsync<OpenAnimeDetails>(url, cancellationToken: cancellationToken);

            if (response == null)
            {
                return new AnimeDetails();
            }

            var title        = response.Turkish ?? response.Romaji ?? response.English ?? animeId;
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
                Summary       = response.Summary ?? "",
                Format        = ParseFormat(response.Type)
            };

            if (response.Seasons != null)
            {
                foreach (var season in response.Seasons)
                {
                    if (season.MalID != 0)
                    {
                        details.SeasonMappings.Add(new SeasonMapping
                        {
                            SeasonNumber  = season.SeasonNumber,
                            MyAnimeListId = season.MalID.ToString()
                        });
                    }
                }
            }

            if (details.SeasonMappings.Count == 0 && response.MalID != 0)
            {
                details.SeasonMappings.Add(new SeasonMapping
                {
                    SeasonNumber  = 1,
                    MyAnimeListId = response.MalID.ToString()
                });
            }

            if (response.Type?.Equals("movie", StringComparison.OrdinalIgnoreCase) == true)
            {
                details.Episodes.Add(new Episode
                {
                    Id     = $"{animeId}/1/1",
                    Title  = details.Title,
                    Number = 1,
                    Season = 1
                });
            }
            else if (response.Seasons != null)
            {
                var seasonTasks = response.Seasons.Where(s => s.HasEpisode)
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
                var results = await Task.WhenAll(seasonTasks);
                foreach (var eps in results)
                {
                    details.Episodes.AddRange(eps);
                }
            }

            return details;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "getDetailsAsync failed for animeId: {AnimeId}", animeId);

            return new AnimeDetails();
        }
    }

    public async Task<List<string>> GetGroupsAsync(string episodeId, CancellationToken cancellationToken = default)
    {
        var result = await GetFansubDataAsync(episodeId, cancellationToken);

        return result.Fansubs.Select(x => x.Name).ToList();
    }

    public async Task<List<VideoSource>> GetVideoSourcesAsync(string episodeId,
        string?                                                      group             = null,
        CancellationToken                                            cancellationToken = default)
    {
        try
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

            if (!fansubs.Any())
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
                                          SendRequestAsync<OpenAnimeSubtitleResponse>(
                                              subUrl,
                                              cancellationToken: cancellationToken);

                                      await Task.WhenAll(episodeTask, subtitleTask);

                                      var response = await episodeTask;
                                      var subObj   = await subtitleTask;

                                      if (response?.EpisodeData?.Files == null)
                                      {
                                          return;
                                      }

                                      var cdnBase = (discoveredCdnLink ?? "").TrimEnd('/');

                                      if (string.IsNullOrEmpty(cdnBase))
                                      {
                                          return;
                                      }

                                      List<Subtitle>? subtitles = null;
                                      if (subObj != null
                                          && !string.IsNullOrEmpty(subObj.X)
                                          && !string.IsNullOrEmpty(subObj.Q))
                                      {
                                          var decryptedAss = DecryptSubtitle(subObj.X, subObj.Q);
                                          if (!string.IsNullOrEmpty(decryptedAss))
                                          {
                                              subtitles = new List<Subtitle>
                                              {
                                                  new()
                                                  {
                                                      Url =
                                                          $"data:text/x-ssa;base64,{Convert.ToBase64String(Encoding.UTF8.GetBytes(decryptedAss))}",
                                                      Language = "tr",
                                                      Label    = $"{fs.Name} (Softsub)",
                                                      Format   = "ass"
                                                  }
                                              };
                                          }
                                      }

                                      foreach (var file in response.EpisodeData.Files)
                                      {
                                          if (string.IsNullOrEmpty(file.File))
                                          {
                                              continue;
                                          }

                                          var videoSource = new VideoSource
                                          {
                                              Url       = $"{cdnBase}/{slug}/{season}/{file.File.TrimStart('/')}",
                                              Quality   = file.Resolution > 0 ? $"{file.Resolution}p" : "Auto",
                                              Type      = VideoType.Mp4,
                                              Hoster    = "OpenAnime",
                                              Group     = fs.Name,
                                              Headers   = new Dictionary<string, string>(_defaultHeaders),
                                              Subtitles = subtitles != null ? new List<Subtitle>(subtitles) : null
                                          };

                                          sourcesBag.Add(videoSource);
                                      }
                                  }
                                  catch (Exception ex)
                                  {
                                      _logger.LogWarning(ex, "failed to resolve sources for fansub: {Fansub}", fs.Name);
                                  }
                              });
            await Task.WhenAll(fansubTasks);

            return sourcesBag.ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "failed to get video sources for episode: {EpisodeId}", episodeId);

            return [];
        }
    }

    private async Task EnsureTokenAsync(CancellationToken cancellationToken = default)
    {
        var now              = DateTimeOffset.UtcNow;
        var needNewSession   = string.IsNullOrEmpty(_sessionId) || now >= _tokenExpiry.AddMinutes(-1);
        var needNewSignature = string.IsNullOrEmpty(_gatewayToken) || now >= _lastSigningTime.AddSeconds(30);

        if (!needNewSession && !needNewSignature)
        {
            return;
        }

        await _tokenSemaphore.WaitAsync(cancellationToken);
        try
        {
            needNewSession   = string.IsNullOrEmpty(_sessionId) || now >= _tokenExpiry.AddMinutes(-1);
            needNewSignature = string.IsNullOrEmpty(_gatewayToken) || now >= _lastSigningTime.AddSeconds(30);
            if (needNewSession)
            {
                const string sessionInitUrl = $"{ApiUrl}/session/init";
                var request = new HttpRequestMessage(HttpMethod.Post, sessionInitUrl)
                {
                    Content = JsonContent.Create(new
                    {
                        cr = Guid.NewGuid().ToString()
                    })
                };
                foreach (var header in _defaultHeaders)
                {
                    request.Headers.Add(header.Key, header.Value);
                }

                request.Headers.Add("Gateway-Token", _gatewayToken ?? "null");
                request.Headers.Add("Origin", "https://openani.me");
                var response = await _httpClient.SendAsync(request, cancellationToken);
                response.EnsureSuccessStatusCode();
                var result = await response.Content.ReadFromJsonAsync<JsonElement>();
                _sessionId = result.GetProperty("sessionId").GetString() ?? throw new Exception("Session ID not found");
                var parts = _sessionId.Split('.');
                if (parts.Length > 1)
                {
                    var       payloadJson = Encoding.UTF8.GetString(DecodeBase64Url(parts[1]));
                    using var doc         = JsonDocument.Parse(payloadJson);
                    if (doc.RootElement.TryGetProperty("exp", out var expProp))
                    {
                        _tokenExpiry = DateTimeOffset.FromUnixTimeSeconds(expProp.GetInt64());
                    }
                }

                needNewSignature = true;
            }

            if (needNewSignature && !string.IsNullOrEmpty(_sessionId))
            {
                _gatewayToken    = SignLocally(_sessionId);
                _lastSigningTime = DateTimeOffset.UtcNow;
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "token/Session refresh failed"); }
        finally { _tokenSemaphore.Release(); }
    }

    private static string SignLocally(string sessionId)
    {
        var       now       = DateTime.UtcNow;
        var       timestamp = now.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var       message   = $"{sessionId}:{timestamp}:openani.me";
        using var hmac      = new HMACSHA256(Encoding.UTF8.GetBytes(SecretKey));
        var       hash      = hmac.ComputeHash(Encoding.UTF8.GetBytes(message));
        var payload = new
        {
            session_id = sessionId,
            timestamp,
            hostname = "openani.me",
            hmac     = Convert.ToHexString(hash).ToLower()
        };

        return Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)));
    }

    private static byte[] DecodeBase64Url(string base64Url)
    {
        var base64 = base64Url.Replace('-', '+').Replace('_', '/');
        switch (base64.Length % 4)
        {
            case 2:
                base64 += "==";

                break;
            case 3:
                base64 += "=";

                break;
        }

        return Convert.FromBase64String(base64);
    }

    private async Task<T?> SendRequestAsync<T>(string url,
        HttpMethod?                                   method            = null,
        object?                                       body              = null,
        bool                                          isRetry           = false,
        CancellationToken                             cancellationToken = default)
    {
        await _requestSemaphore.WaitAsync(cancellationToken);
        try
        {
            await EnsureTokenAsync(cancellationToken);
            var request = new HttpRequestMessage(method ?? HttpMethod.Get, url);
            foreach (var header in _defaultHeaders)
            {
                request.Headers.Add(header.Key, header.Value);
            }

            request.Headers.Add("Origin", "https://openani.me");
            if (_gatewayToken != null)
            {
                request.Headers.Add("Gateway-Token", _gatewayToken);
            }

            if (body != null)
            {
                request.Content = JsonContent.Create(body);
            }

            var response = await _httpClient.SendAsync(request, cancellationToken);
            if ((response.StatusCode == HttpStatusCode.Unauthorized
                 || response.StatusCode == HttpStatusCode.Forbidden)
                && !isRetry)
            {
                _sessionId    = null;
                _gatewayToken = null;

                return await SendRequestAsync<T>(url, method, body, true, cancellationToken);
            }

            if (!response.IsSuccessStatusCode)
            {
                return default;
            }

            if (typeof(T) == typeof(string))
            {
                return (T) (object) await response.Content.ReadAsStringAsync(cancellationToken);
            }

            return await response.Content.ReadFromJsonAsync<T>(cancellationToken);
        }
        finally { _requestSemaphore.Release(); }
    }

    private async Task<List<SimpleEpisode>> GetSeasonEpisodesAsync(string slug,
        int                                                               seasonNumber,
        CancellationToken                                                 cancellationToken = default)
    {
        try
        {
            var url      = $"{BaseUrl}/anime/{slug}/{seasonNumber}/__data.json?x-sveltekit-invalidated=01";
            var response = await SendRequestAsync<string>(url, cancellationToken: cancellationToken);

            if (string.IsNullOrEmpty(response))
            {
                return [];
            }

            var episodes = new List<SimpleEpisode>();
            var data     = GetFlatData(response);

            if (data.ValueKind == JsonValueKind.Undefined)
            {
                return [];
            }

            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    var episodesProp = FindProperty(item, "episodes", data);
                    if (episodesProp.ValueKind != JsonValueKind.Undefined)
                    {
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

                            var epNumVal = FindProperty(epObj, "episodeNumber", data);
                            var epNum    = Resolve(epNumVal, data).GetInt32();
                            var nameVal  = FindProperty(epObj, "name", data);
                            var epName   = Resolve(nameVal, data).GetString() ?? $"{epNum}. Bölüm";
                            episodes.Add(new SimpleEpisode
                            {
                                Number = epNum,
                                Title  = epName
                            });
                        }

                        break;
                    }
                }
            }

            return episodes.OrderBy(x => x.Number).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "failed to get episodes list");

            return [];
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
            if (data[i].ValueKind == JsonValueKind.String && data[i].GetString() == name)
            {
                if (obj.TryGetProperty(i.ToString(), out var indexedVal))
                {
                    return indexedVal;
                }
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
                if (doc.RootElement.TryGetProperty("type", out var typeProp) && typeProp.GetString() == "chunk")
                {
                    if (doc.RootElement.TryGetProperty("data", out var dataArr)
                        && dataArr.ValueKind == JsonValueKind.Array)
                    {
                        return dataArr.Clone();
                    }
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
                {
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
                }
            case JsonValueKind.Array:
                {
                    foreach (var item in element.EnumerateArray())
                    {
                        FindDataArray(item, ref bestData, ref maxLen);
                    }

                    break;
                }
        }
    }

    private static JsonElement Resolve(JsonElement element, JsonElement data)
    {
        if (element.ValueKind == JsonValueKind.Number)
        {
            var index = element.GetInt32();

            if (index >= 0 && index < data.GetArrayLength())
            {
                return data[index];
            }
        }

        return element;
    }

    private async Task<(List<OpenAnimeFansubInfo> Fansubs, string? CdnLink)> GetFansubDataAsync(string episodeId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var url      = $"{BaseUrl}/anime/{episodeId}/__data.json?x-sveltekit-invalidated=01";
            var response = await SendRequestAsync<string>(url, cancellationToken: cancellationToken);

            if (string.IsNullOrEmpty(response))
            {
                return ([], null);
            }

            var data = GetFlatData(response);

            if (data.ValueKind == JsonValueKind.Undefined)
            {
                return ([], null);
            }

            var       fansubs = new List<OpenAnimeFansubInfo>();
            string?   cdnLink = null;
            using var doc     = JsonDocument.Parse(response.Split('\n')[0]);
            FindCdnAndFansubsRecursive(doc.RootElement, data, fansubs, ref cdnLink);

            return (fansubs, cdnLink);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "getEpisodesAndDiscoveredCdn failed");

            return ([], null);
        }
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
                cdnLink = Resolve(cdnProp, data).GetString();
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
                        if (fsObj.ValueKind == JsonValueKind.Object)
                        {
                            var id         = Resolve(FindProperty(fsObj, "id", data), data).GetString();
                            var name       = Resolve(FindProperty(fsObj, "name", data), data).GetString();
                            var secureName = Resolve(FindProperty(fsObj, "secureName", data), data).GetString();
                            if (!string.IsNullOrEmpty(id)
                                && !string.IsNullOrEmpty(name)
                                && fansubs.All(x => x.Id != id))
                            {
                                fansubs.Add(new OpenAnimeFansubInfo
                                {
                                    Id         = id,
                                    Name       = name,
                                    SecureName = secureName ?? ""
                                });
                            }
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
        return type?.ToLower() switch
        {
            "tv"    => ContentFormat.Tv,
            "movie" => ContentFormat.Movie,
            "ova"   => ContentFormat.Ova,
            _       => ContentFormat.Tv
        };
    }

    private static byte[] BuildInvTable()
    {
        var table = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            table[i] = (byte) i;
        }

        var sboxSeed = 412906018u;
        for (var i = 255; i > 0; i--)
        {
            unchecked
            {
                sboxSeed = (sboxSeed * 1664525u) + 1013904223u;
            }

            var j = (int) (sboxSeed % (uint) (i + 1));
            (table[i], table[j]) = (table[j], table[i]);
        }

        var invTable = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            invTable[table[i]] = (byte) i;
        }

        return invTable;
    }

    private string? DecryptSubtitle(string hexX, string base64Q)
    {
        try
        {
            var keyBytes = Convert.FromHexString(hexX.Trim());
            var rawQ     = Convert.FromBase64String(base64Q.Trim());

            if (rawQ.Length < 16)
            {
                return null;
            }

            var step3   = new byte[rawQ.Length];
            var lcgSeed = 386190471u;

            for (var i = 0; i < rawQ.Length; i++)
            {
                unchecked
                {
                    lcgSeed = (lcgSeed * 1664525u) + 1013904223u;
                }

                step3[i] = (byte) (rawQ[i] ^ (lcgSeed & 0xFF));
            }

            var ivBytes = step3[..16];
            var payload = step3[16..];

            using var aes = Aes.Create();
            aes.Key     = keyBytes;
            aes.IV      = ivBytes;
            aes.Mode    = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using var decryptor        = aes.CreateDecryptor();
            var       decryptedPayload = decryptor.TransformFinalBlock(payload, 0, payload.Length);

            var plainBytes = new byte[decryptedPayload.Length];
            for (var i = 0; i < decryptedPayload.Length; i++)
            {
                plainBytes[i] = _invTable[decryptedPayload[i]];
            }

            return Encoding.UTF8.GetString(plainBytes);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to decrypt subtitle payload");

            return null;
        }
    }

    private class OpenAnimeSubtitleResponse
    {
        [JsonPropertyName("x")]
        public string? X { get; set; }

        [JsonPropertyName("q")]
        public string? Q { get; set; }
    }

    private class SimpleEpisode
    {
        public int    Number { get; set; }
        public string Title  { get; set; } = "";
    }

    private class OpenAnimeFansubInfo
    {
        public string Id         { get; set; } = "";
        public string Name       { get; set; } = "";
        public string SecureName { get; set; } = "";
    }

    private class OpenAnimeEpisodeResponse
    {
        [JsonPropertyName("episodeData")]
        public OpenAnimeEpisodeData? EpisodeData { get; set; }
    }

    private class OpenAnimeEpisodeData
    {
        [JsonPropertyName("files")]
        public List<OpenAnimeFile>? Files { get; set; }
    }

    private class OpenAnimeFile
    {
        [JsonPropertyName("file")]
        public string? File { get; set; }

        [JsonPropertyName("resolution")]
        public int Resolution { get; set; }
    }

    private class OpenAnimeSearchItem
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

    private class OpenAnimePictures
    {
        public string? Banner { get; set; }
        public string? Avatar { get; set; }
    }

    private class OpenAnimeDetails
    {
        public string? English      { get; set; }
        public string? Turkish      { get; set; }
        public string? Romaji       { get; set; }
        public string? Japanese     { get; set; }
        public string? OriginalName { get; set; }
        public string? Summary      { get; set; }
        public string? Type         { get; set; }

        [JsonPropertyName("malID")]
        public int MalID { get; set; }

        public List<OpenAnimeSeason>? Seasons { get; set; }
    }

    private class OpenAnimeSeason
    {
        [JsonPropertyName("season_number")]
        public int SeasonNumber { get; set; }

        [JsonPropertyName("hasEpisode")]
        public bool HasEpisode { get; set; }

        [JsonPropertyName("mal_id")]
        public int MalID { get; set; }
    }
}
