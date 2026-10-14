using Microsoft.Extensions.Logging;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace Migurdex.Plugins.Animim;

public class AnimimProvider : IAnimeProvider
{
    private readonly HttpClient                           _httpClient;
    private readonly ILogger<AnimimProvider>              _logger;
    private readonly ConcurrentDictionary<string, string> _fansubCache = new();

    public AnimimProvider(ISharedBridge bridge)
    {
        _httpClient = bridge.CreateHttpClient();
        _logger     = bridge.CreateLogger<AnimimProvider>();

        _httpClient.DefaultRequestHeaders.Remove("User-Agent");
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
    }

    public string       Name    => "Animim";
    public string       BaseUrl => "https://animim.app";
    public ProviderType Type    => ProviderType.Anime;

    public async Task<List<SearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        try
        {
            var url      = $"{BaseUrl}/api/search?q={Uri.EscapeDataString(query)}";
            var response = await _httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            var       json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc  = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("anime", out var animeArr) || animeArr.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var results = new List<SearchResult>();
            foreach (var item in animeArr.EnumerateArray())
            {
                var id = item.TryGetProperty("_id", out var idProp) ? idProp.GetString() : null;
                if (string.IsNullOrEmpty(id))
                {
                    continue;
                }

                var title = item.TryGetProperty("title", out var titleProp) ? titleProp.GetString() ?? "" : "";
                var originalTitle =
                    item.TryGetProperty("originalTitle", out var origProp) ? origProp.GetString() : null;
                var englishTitle = item.TryGetProperty("englishTitle", out var engProp) ? engProp.GetString() : null;
                var cover        = item.TryGetProperty("cover", out var coverProp) ? coverProp.GetString() : null;

                double? rating = null;
                if (item.TryGetProperty("rating", out var ratingProp) && ratingProp.ValueKind == JsonValueKind.Number)
                {
                    rating = ratingProp.GetDouble();
                }

                string? year = null;
                if (item.TryGetProperty("releaseYear", out var yearProp))
                {
                    year = yearProp.ValueKind == JsonValueKind.Number
                               ? yearProp.GetInt32().ToString()
                               : yearProp.GetString();
                }

                var typeStr = item.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;
                var format = string.Equals(typeStr, "Movie", StringComparison.OrdinalIgnoreCase)
                                 ? ContentFormat.Movie
                                 : ContentFormat.Tv;

                results.Add(new SearchResult
                {
                    Id           = id,
                    Title        = title,
                    RomajiTitle  = !string.IsNullOrWhiteSpace(originalTitle) ? originalTitle : null,
                    EnglishTitle = !string.IsNullOrWhiteSpace(englishTitle) ? englishTitle : null,
                    PosterUrl    = cover,
                    Score        = rating,
                    Year         = year,
                    Format       = format,
                    Url          = $"{BaseUrl}/anime/{id}",
                    ProviderName = Name,
                    Type         = ProviderType.Anime
                });
            }

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SearchAsync failed for query: {Query}", query);
            return [];
        }
    }

    public async Task<AnimeDetails> GetDetailsAsync(string animeId, CancellationToken cancellationToken = default)
    {
        try
        {
            var animeUrl  = $"{BaseUrl}/api/animes/{animeId}";
            var animeResp = await _httpClient.GetAsync(animeUrl, cancellationToken);
            if (!animeResp.IsSuccessStatusCode)
            {
                return new AnimeDetails();
            }

            var       animeJson = await animeResp.Content.ReadAsStringAsync(cancellationToken);
            using var animeDoc  = JsonDocument.Parse(animeJson);
            var       root      = animeDoc.RootElement;

            var title         = root.TryGetProperty("title", out var titleProp) ? titleProp.GetString() ?? "" : "";
            var description   = root.TryGetProperty("description", out var descProp) ? descProp.GetString() ?? "" : "";
            var cover         = root.TryGetProperty("cover", out var coverProp) ? coverProp.GetString() : null;
            var originalTitle = root.TryGetProperty("originalTitle", out var origProp) ? origProp.GetString() : null;
            var englishTitle  = root.TryGetProperty("englishTitle", out var engProp) ? engProp.GetString() : null;

            var typeStr = root.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;
            var format = string.Equals(typeStr, "Movie", StringComparison.OrdinalIgnoreCase)
                             ? ContentFormat.Movie
                             : ContentFormat.Tv;

            var details = new AnimeDetails
            {
                Title        = title,
                EnglishTitle = !string.IsNullOrWhiteSpace(englishTitle) ? englishTitle : null,
                RomajiTitle  = !string.IsNullOrWhiteSpace(originalTitle) ? originalTitle : null,
                PosterUrl    = cover,
                Summary      = description,
                Format       = format
            };

            string? malId = null;
            if (root.TryGetProperty("malId", out var malProp)
                && malProp.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            {
                malId = malProp.ValueKind == JsonValueKind.Number ? malProp.GetInt64().ToString() : malProp.GetString();
            }

            string? aniListId = null;
            if (root.TryGetProperty("aniListId", out var aniProp)
                && aniProp.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            {
                aniListId = aniProp.ValueKind == JsonValueKind.Number
                                ? aniProp.GetInt64().ToString()
                                : aniProp.GetString();
            }

            if (!string.IsNullOrWhiteSpace(malId) || !string.IsNullOrWhiteSpace(aniListId))
            {
                details.SeasonMappings.Add(new SeasonMapping
                {
                    SeasonNumber  = 1,
                    MyAnimeListId = string.IsNullOrWhiteSpace(malId) ? null : malId,
                    AniListId     = string.IsNullOrWhiteSpace(aniListId) ? null : aniListId
                });
            }

            var epUrl  = $"{BaseUrl}/api/episodes?animeId={animeId}";
            var epResp = await _httpClient.GetAsync(epUrl, cancellationToken);
            if (epResp.IsSuccessStatusCode)
            {
                var       epJson = await epResp.Content.ReadAsStringAsync(cancellationToken);
                using var epDoc  = JsonDocument.Parse(epJson);
                if (epDoc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in epDoc.RootElement.EnumerateArray())
                    {
                        var epId = item.TryGetProperty("_id", out var epIdProp) ? epIdProp.GetString() : null;
                        if (string.IsNullOrEmpty(epId))
                        {
                            continue;
                        }

                        var epTitle = item.TryGetProperty("title", out var tProp) ? tProp.GetString() ?? "" : "";

                        double epNum = 0;
                        if (item.TryGetProperty("number", out var numProp))
                        {
                            if (numProp.ValueKind == JsonValueKind.Number)
                            {
                                epNum = numProp.GetDouble();
                            }
                            else if (double.TryParse(numProp.GetString(),
                                                     CultureInfo.InvariantCulture,
                                                     out var parsedNum))
                            {
                                epNum = parsedNum;
                            }
                        }

                        int? season = null;
                        if (item.TryGetProperty("seasonNumber", out var sProp))
                        {
                            if (sProp.ValueKind == JsonValueKind.Number)
                            {
                                season = sProp.GetInt32();
                            }
                            else if (int.TryParse(sProp.GetString(),
                                                  CultureInfo.InvariantCulture,
                                                  out var parsedSeason))
                            {
                                season = parsedSeason;
                            }
                        }

                        details.Episodes.Add(new Episode
                        {
                            Id     = epId,
                            Title  = epTitle,
                            Number = epNum,
                            Season = season
                        });
                    }
                }
            }

            return details;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetDetailsAsync failed for animeId: {AnimeId}", animeId);
            return new AnimeDetails();
        }
    }

    public async Task<List<string>> GetGroupsAsync(string episodeId, CancellationToken cancellationToken = default)
    {
        try
        {
            var url      = $"{BaseUrl}/api/episodes/{episodeId}";
            var response = await _httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            var       json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc  = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("fanSubVideos", out var fanSubsArr)
                || fanSubsArr.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var groups = new List<string>();
            foreach (var item in fanSubsArr.EnumerateArray())
            {
                var fanSubId = item.TryGetProperty("fanSubId", out var idProp) ? idProp.GetString() : null;
                if (string.IsNullOrWhiteSpace(fanSubId))
                {
                    continue;
                }

                var name = await GetFansubNameAsync(fanSubId, cancellationToken);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    groups.Add(name);
                }
            }

            return groups.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetGroupsAsync failed for episodeId: {EpisodeId}", episodeId);
            return [];
        }
    }

    public async Task<List<VideoSource>> GetVideoSourcesAsync(string episodeId,
        string?                                                      group             = null,
        CancellationToken                                            cancellationToken = default)
    {
        try
        {
            var url      = $"{BaseUrl}/api/episodes/{episodeId}";
            var response = await _httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            var       json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc  = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("fanSubVideos", out var fanSubsArr)
                || fanSubsArr.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var sources = new List<VideoSource>();
            foreach (var item in fanSubsArr.EnumerateArray())
            {
                var fanSubId = item.TryGetProperty("fanSubId", out var idProp) ? idProp.GetString() : null;
                if (string.IsNullOrWhiteSpace(fanSubId))
                {
                    continue;
                }

                var fansubName = await GetFansubNameAsync(fanSubId, cancellationToken);

                if (!string.IsNullOrWhiteSpace(group)
                    && !string.Equals(fansubName, group, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!item.TryGetProperty("videoUrls", out var videoUrlsArr)
                    || videoUrlsArr.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var subtitles = ParseSubtitles(item);

                foreach (var videoEntry in videoUrlsArr.EnumerateArray())
                {
                    AddSourceIfValid(sources, videoEntry, "1080p_url", "1080p", fansubName, subtitles);
                    AddSourceIfValid(sources, videoEntry, "720p_url", "720p", fansubName, subtitles);
                    AddSourceIfValid(sources, videoEntry, "480p_url", "480p", fansubName, subtitles);
                }
            }

            return sources;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetVideoSourcesAsync failed for episodeId: {EpisodeId}", episodeId);
            return [];
        }
    }

    private async Task<string> GetFansubNameAsync(string fanSubId, CancellationToken cancellationToken)
    {
        if (_fansubCache.TryGetValue(fanSubId, out var cachedName))
        {
            return cachedName;
        }

        try
        {
            var url      = $"{BaseUrl}/api/fansubs/{fanSubId}";
            var response = await _httpClient.GetAsync(url, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var       json = await response.Content.ReadAsStringAsync(cancellationToken);
                using var doc  = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("name", out var nameProp)
                    && nameProp.ValueKind == JsonValueKind.String)
                {
                    var name = nameProp.GetString();
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        _fansubCache[fanSubId] = name;
                        return name;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve fansub name for {FanSubId}", fanSubId);
        }

        var fallback = fanSubId.Length > 8 ? fanSubId[..8] : fanSubId;
        _fansubCache[fanSubId] = fallback;
        return fallback;
    }

    private static void AddSourceIfValid(List<VideoSource> sources,
        JsonElement                                        videoEntry,
        string                                             propertyName,
        string                                             quality,
        string                                             fansubName,
        List<Subtitle>?                                    subtitles = null)
    {
        if (videoEntry.TryGetProperty(propertyName, out var urlProp)
            && urlProp.ValueKind == JsonValueKind.String)
        {
            var url = urlProp.GetString();
            if (!string.IsNullOrWhiteSpace(url))
            {
                sources.Add(new VideoSource
                {
                    Url       = url,
                    Quality   = quality,
                    Type      = VideoType.Mp4,
                    Hoster    = "Animim",
                    Group     = fansubName,
                    Subtitles = subtitles is { Count: > 0 } ? new List<Subtitle>(subtitles) : null
                });
            }
        }
    }

    private static List<Subtitle>? ParseSubtitles(JsonElement fanSubVideo)
    {
        if (!fanSubVideo.TryGetProperty("assSubtitles", out var subsArr)
            || subsArr.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        List<Subtitle>? result = null;
        foreach (var sub in subsArr.EnumerateArray())
        {
            if (sub.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var url = sub.TryGetProperty("url", out var urlProp) ? urlProp.GetString() : null;
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            var language = sub.TryGetProperty("language", out var langProp) ? langProp.GetString() : null;
            var fileName = sub.TryGetProperty("fileName", out var nameProp) ? nameProp.GetString() : null;
            var format   = GetSubtitleFormat(url, fileName);

            result ??= new List<Subtitle>();
            result.Add(new Subtitle
            {
                Url      = url,
                Language = string.IsNullOrWhiteSpace(language) ? "tr" : language,
                Label    = fileName ?? "Altyazı",
                Format   = format
            });
        }

        return result;
    }

    private static string GetSubtitleFormat(string url, string? fileName)
    {
        var path = url.Split('?')[0];
        var dot  = path.LastIndexOf('.');
        if (dot >= 0)
        {
            var ext = path[(dot + 1)..].ToLowerInvariant();
            if (ext is "srt" or "vtt" or "ass" or "ssa" or "sub" or "idx" or "smi")
            {
                return ext;
            }
        }

        if (!string.IsNullOrWhiteSpace(fileName))
        {
            var fdot = fileName.LastIndexOf('.');
            if (fdot >= 0)
            {
                var fext = fileName[(fdot + 1)..].ToLowerInvariant();
                if (fext is "srt" or "vtt" or "ass" or "ssa" or "sub" or "idx" or "smi")
                {
                    return fext;
                }
            }
        }

        return "ass";
    }
}
