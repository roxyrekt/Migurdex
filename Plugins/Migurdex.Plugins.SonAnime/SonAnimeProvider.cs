using Microsoft.Extensions.Logging;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Text.Json;

namespace Migurdex.Plugins.SonAnime;

public class SonAnimeProvider : IAnimeProvider
{
    private const    string                    ApiBaseUrl = "https://api.sonanime.com/api";
    private readonly HttpClient                _httpClient;
    private readonly ILogger<SonAnimeProvider> _logger;

    public SonAnimeProvider(ISharedBridge bridge, ILogger<SonAnimeProvider> logger)
    {
        _httpClient = bridge.CreateHttpClient();
        _logger     = logger;

        _httpClient.DefaultRequestHeaders.Add("Referer", "https://sonanime.com/");
        _httpClient.DefaultRequestHeaders.Add("Origin", "https://sonanime.com");
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
    }

    public string       Name    => "SonAnime";
    public string       BaseUrl => "https://sonanime.com";
    public ProviderType Type    => ProviderType.Anime;

    public async Task<List<SearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        try
        {
            var searchUrl = $"{ApiBaseUrl}/anime/search?q={Uri.EscapeDataString(query)}";
            var response  = await _httpClient.GetAsync(searchUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(json))
            {
                return [];
            }

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var results = new List<SearchResult>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var searchResult = ParseSearchResult(item);
                if (searchResult != null)
                {
                    results.Add(searchResult);
                }
            }

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SonAnime search failed for query: {Query}", query);
            return [];
        }
    }

    public async Task<AnimeDetails> GetDetailsAsync(string animeId, CancellationToken cancellationToken = default)
    {
        try
        {
            var endpoint = int.TryParse(animeId, out _)
                               ? $"{ApiBaseUrl}/anime/id/{animeId}"
                               : $"{ApiBaseUrl}/anime/link/{animeId}";

            var response = await _httpClient.GetAsync(endpoint, cancellationToken);
            if (!response.IsSuccessStatusCode && !int.TryParse(animeId, out _))
            {
                endpoint = $"{ApiBaseUrl}/anime/id/{animeId}";
                response = await _httpClient.GetAsync(endpoint, cancellationToken);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new AnimeDetails();
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new AnimeDetails();
            }

            using var doc  = JsonDocument.Parse(json);
            var       root = doc.RootElement;

            var title        = GetStringProperty(root, "anime_name", animeId);
            var englishTitle = GetStringProperty(root, "anime_name_en", null);
            var animeType    = GetStringProperty(root, "anime_type", "anime");
            var slug         = GetStringProperty(root, "anime_link", animeId);
            var posterUrl    = NormalizePhotoUrl(GetStringProperty(root, "anime_photo", null));
            var summary      = GetStringProperty(root, "anime_description", "");
            var tmdbId       = GetIdStringProperty(root, "tmdb_id");
            var malId        = GetIdStringProperty(root, "mal_id");

            var isMovie = "movie".Equals(animeType, StringComparison.OrdinalIgnoreCase)
                          || AnimeDetails.IsMovieTitle(title)
                          || AnimeDetails.IsMovieTitle(englishTitle);

            var altTitles = new List<string>();
            if (!string.IsNullOrWhiteSpace(englishTitle)
                && !englishTitle.Equals(title, StringComparison.OrdinalIgnoreCase))
            {
                altTitles.Add(englishTitle);
            }

            var details = new AnimeDetails
            {
                Title             = title,
                EnglishTitle      = englishTitle,
                PosterUrl         = posterUrl,
                Summary           = summary,
                AlternativeTitles = altTitles,
                Format            = isMovie ? ContentFormat.Movie : ContentFormat.Tv
            };

            var parsedSeasons = new HashSet<int>();

            if (root.TryGetProperty("episodes", out var episodesProp) && episodesProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var ep in episodesProp.EnumerateArray())
                {
                    var seasonNum = GetIntProperty(ep, "anime_season", 1);
                    var epNum     = GetIntProperty(ep, "episode_number", 1);
                    var epId      = GetIntProperty(ep, "id", 0);

                    parsedSeasons.Add(seasonNum);

                    var epIdentifier = $"{slug}:{seasonNum}:{epNum}:{epId}";
                    var epTitle      = isMovie ? "Film" : $"S{seasonNum}E{epNum:D2} - Bölüm {epNum}";

                    details.Episodes.Add(new Episode
                    {
                        Id     = epIdentifier,
                        Title  = epTitle,
                        Number = isMovie ? 1 : epNum,
                        Season = isMovie ? 1 : seasonNum
                    });
                }
            }

            if (!parsedSeasons.Any())
            {
                parsedSeasons.Add(1);
            }

            foreach (var s in parsedSeasons.OrderBy(s => s))
            {
                details.SeasonMappings.Add(new SeasonMapping
                {
                    SeasonNumber  = s,
                    TmdbId        = s == 1 ? tmdbId : null,
                    MyAnimeListId = s == 1 ? malId : null
                });
            }

            details.Episodes = details.Episodes
                                      .OrderBy(e => e.Season ?? 1)
                                      .ThenBy(e => e.Number)
                                      .ToList();

            details.Normalize();
            return details;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SonAnime GetDetailsAsync failed for: {AnimeId}", animeId);
            return new AnimeDetails();
        }
    }

    public Task<List<string>> GetGroupsAsync(string episodeId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new List<string>
        {
            "SonAnime"
        });
    }

    public async Task<List<VideoSource>> GetVideoSourcesAsync(
        string            episodeId,
        string?           group             = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var parts       = episodeId.Split(':');
            var slug        = parts.Length > 0 ? parts[0] : episodeId;
            var targetEpNum = parts.Length > 2 && int.TryParse(parts[2], out var parsedEp) ? parsedEp : (int?) null;
            var targetEpId  = parts.Length > 3 && int.TryParse(parts[3], out var parsedId) ? parsedId : (int?) null;

            var endpoint = $"{ApiBaseUrl}/anime/link/{slug}";
            var response = await _httpClient.GetAsync(endpoint, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                endpoint = $"{ApiBaseUrl}/anime/id/{slug}";
                response = await _httpClient.GetAsync(endpoint, cancellationToken);
            }

            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(json))
            {
                return [];
            }

            using var doc  = JsonDocument.Parse(json);
            var       root = doc.RootElement;

            if (!root.TryGetProperty("episodes", out var episodesProp) || episodesProp.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var sources = new List<VideoSource>();
            foreach (var ep in episodesProp.EnumerateArray())
            {
                var epNum = GetIntProperty(ep, "episode_number", 1);
                var epId  = GetIntProperty(ep, "id", 0);

                if ((targetEpId.HasValue && epId == targetEpId.Value)
                    || (targetEpNum.HasValue && epNum == targetEpNum.Value)
                    || (!targetEpId.HasValue && !targetEpNum.HasValue))
                {
                    AddVideoSourceIfPresent(ep, "episode_link_1080", "1080p", sources);
                    AddVideoSourceIfPresent(ep, "episode_link_720", "720p", sources);
                    AddVideoSourceIfPresent(ep, "episode_link_480", "480p", sources);
                    AddVideoSourceIfPresent(ep, "episode_link_360", "360p", sources);

                    if (sources.Count > 0)
                    {
                        break;
                    }
                }
            }

            return sources;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SonAnime GetVideoSourcesAsync failed for episode: {EpisodeId}", episodeId);
            return [];
        }
    }

    private static void AddVideoSourceIfPresent(JsonElement element,
        string                                              propName,
        string                                              quality,
        List<VideoSource>                                   sources)
    {
        if (element.TryGetProperty(propName, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            var url = prop.GetString();

            if (!string.IsNullOrWhiteSpace(url))
            {
                sources.Add(new VideoSource
                {
                    Url     = url,
                    Quality = quality,
                    Hoster  = "SonAnime",
                    Type    = VideoType.Mp4,
                    Group   = "SonAnime",
                    Headers = new Dictionary<string, string>
                    {
                        ["Referer"] = "https://sonanime.com/"
                    }
                });
            }
        }
    }

    private SearchResult? ParseSearchResult(JsonElement item)
    {
        try
        {
            var animeId = GetIntProperty(item, "anime_id", 0);
            var slug    = GetStringProperty(item, "anime_link", animeId.ToString());
            var title   = GetStringProperty(item, "anime_name", "");

            if (string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            var englishTitle = GetStringProperty(item, "anime_name_en", null);
            var posterUrl    = NormalizePhotoUrl(GetStringProperty(item, "anime_photo", null));
            var year         = GetStringProperty(item, "anime_year", null);
            var categories   = ParseGenres(item, "anime_genres");

            double? score = null;
            if (item.TryGetProperty("anime_malScore", out var sc) && sc.ValueKind == JsonValueKind.Number)
            {
                score = sc.GetDouble();
            }

            var animeType = GetStringProperty(item, "anime_type", "anime");
            var isMovie = "movie".Equals(animeType, StringComparison.OrdinalIgnoreCase)
                          || AnimeDetails.IsMovieTitle(title)
                          || AnimeDetails.IsMovieTitle(englishTitle);

            var altTitles = new List<string>();
            if (!string.IsNullOrWhiteSpace(englishTitle)
                && !englishTitle.Equals(title, StringComparison.OrdinalIgnoreCase))
            {
                altTitles.Add(englishTitle);
            }

            var result = new SearchResult
            {
                Id                = slug,
                Title             = title,
                EnglishTitle      = englishTitle,
                AlternativeTitles = altTitles,
                PosterUrl         = posterUrl,
                Url               = $"{BaseUrl}/anime/{slug}",
                ProviderName      = Name,
                Type              = ProviderType.Anime,
                Format            = isMovie ? ContentFormat.Movie : ContentFormat.Tv,
                Year              = year,
                Score             = score,
                Categories        = categories
            };

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse SonAnime search item");
            return null;
        }
    }

    private string? NormalizePhotoUrl(string? photo)
    {
        if (string.IsNullOrWhiteSpace(photo))
        {
            return null;
        }

        photo = photo.Trim();
        if (photo.StartsWith("//"))
        {
            return "https:" + photo;
        }

        if (photo.StartsWith("/"))
        {
            return BaseUrl + photo;
        }

        if (!photo.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !photo.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return $"{BaseUrl}/{photo}";
        }

        return photo;
    }

    private static List<string>? ParseGenres(JsonElement element, string propName)
    {
        if (!element.TryGetProperty(propName, out var prop))
        {
            return null;
        }

        if (prop.ValueKind == JsonValueKind.Array)
        {
            var list = new List<string>();
            foreach (var item in prop.EnumerateArray())
            {
                var str = item.GetString()?.Trim();
                if (!string.IsNullOrEmpty(str))
                {
                    list.Add(str);
                }
            }

            return list.Count > 0 ? list : null;
        }

        if (prop.ValueKind == JsonValueKind.String)
        {
            var raw = prop.GetString()?.Trim();
            if (string.IsNullOrEmpty(raw))
            {
                return null;
            }

            if (raw.StartsWith('[') && raw.EndsWith(']'))
            {
                try
                {
                    using var parsed = JsonDocument.Parse(raw);
                    if (parsed.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        var list = new List<string>();
                        foreach (var item in parsed.RootElement.EnumerateArray())
                        {
                            var str = item.GetString()?.Trim();
                            if (!string.IsNullOrEmpty(str))
                            {
                                list.Add(str);
                            }
                        }

                        return list.Count > 0 ? list : null;
                    }
                }
                catch
                {
                    // ignored
                }
            }

            var split = raw.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries)
                           .Select(x => x.Trim().Trim('"', '\''))
                           .Where(x => !string.IsNullOrEmpty(x))
                           .ToList();
            return split.Count > 0 ? split : null;
        }

        return null;
    }

    private static string GetStringProperty(JsonElement element, string propName, string? defaultValue)
    {
        if (element.TryGetProperty(propName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.String)
            {
                return prop.GetString() ?? defaultValue ?? "";
            }

            if (prop.ValueKind == JsonValueKind.Number)
            {
                return prop.ToString();
            }
        }

        return defaultValue ?? "";
    }

    private static string? GetIdStringProperty(JsonElement element, string propName)
    {
        if (element.TryGetProperty(propName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var num) && num > 0)
            {
                return num.ToString();
            }

            if (prop.ValueKind == JsonValueKind.String)
            {
                var val = prop.GetString()?.Trim();
                if (!string.IsNullOrEmpty(val) && val != "0" && val != "null")
                {
                    return val;
                }
            }
        }

        return null;
    }

    private static int GetIntProperty(JsonElement element, string propName, int defaultValue)
    {
        if (element.TryGetProperty(propName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var val))
            {
                return val;
            }

            if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out var strVal))
            {
                return strVal;
            }
        }

        return defaultValue;
    }
}
