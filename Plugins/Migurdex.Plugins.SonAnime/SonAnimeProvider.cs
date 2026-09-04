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

            var title          = GetStringProperty(root, "anime_name", animeId);
            var englishTitle   = GetStringProperty(root, "anime_name_en", null);
            var animeType      = GetStringProperty(root, "anime_type", "anime");
            var slug           = GetStringProperty(root, "anime_link", animeId);
            var animeNumericId = GetIntProperty(root, "anime_id", 0);

            var isMovie = "movie".Equals(animeType, StringComparison.OrdinalIgnoreCase);

            var details = new AnimeDetails
            {
                Title        = title,
                EnglishTitle = englishTitle,
                Summary      = GetStringProperty(root, "anime_description", ""),
                Format       = isMovie ? ContentFormat.Movie : ContentFormat.Tv
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

            foreach (var s in parsedSeasons.OrderBy(s => s))
            {
                details.SeasonMappings.Add(new SeasonMapping
                {
                    SeasonNumber = s
                });
            }

            if (!details.SeasonMappings.Any())
            {
                details.SeasonMappings.Add(new SeasonMapping
                {
                    SeasonNumber = 1
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
                    Group   = "SonAnime"
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
            var posterUrl    = GetStringProperty(item, "anime_photo", null);
            var year         = GetStringProperty(item, "anime_year", null);

            double? score = null;
            if (item.TryGetProperty("anime_malScore", out var sc) && sc.ValueKind == JsonValueKind.Number)
            {
                score = sc.GetDouble();
            }

            var result = new SearchResult
            {
                Id           = slug,
                Title        = title,
                EnglishTitle = englishTitle,
                PosterUrl    = posterUrl,
                Url          = $"{BaseUrl}/anime/{slug}",
                ProviderName = Name,
                Type         = ProviderType.Anime,
                Year         = year,
                Score        = score
            };

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse SonAnime search item");
            return null;
        }
    }

    private static string GetStringProperty(JsonElement element, string propName, string? defaultValue)
    {
        if (element.TryGetProperty(propName, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString() ?? defaultValue ?? "";
        }

        return defaultValue ?? "";
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
