using Microsoft.Extensions.Logging;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Text.Json;

namespace Migurdex.Plugins.Anizium;

public class AniziumProvider : IAnimeProvider
{
    private const    string                   ApiUrl = "https://api.anizium.co";
    private readonly HttpClient               _httpClient;
    private readonly ILogger<AniziumProvider> _logger;

    public AniziumProvider(ISharedBridge bridge, ILogger<AniziumProvider> logger)
    {
        _httpClient = bridge.CreateHttpClient(o => o.Emulation = BrowserEmulation.OkHttp5);
        _logger     = logger;

        _httpClient.DefaultRequestHeaders.Clear();
        _httpClient.DefaultRequestHeaders.Add("Cf-Control", "134e1e595b580d51550809065948050306434c065c54530f");
    }

    public string       Name    => "Anizium";
    public string       BaseUrl => "https://anizium.co";
    public ProviderType Type    => ProviderType.Anime;

    public async Task<List<SearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        try
        {
            var results = new List<SearchResult>();
            var page    = 1;
            int notDisplayed;

            do
            {
                var url      = $"{ApiUrl}/page/search?value={Uri.EscapeDataString(query)}&page={page}";
                var response = await _httpClient.GetAsync(url, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    break;
                }

                var       json = await response.Content.ReadAsStringAsync(cancellationToken);
                using var doc  = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("page", out var pageObj))
                {
                    notDisplayed = pageObj.TryGetProperty("not_displayed", out var ndProp) ? ndProp.GetInt32() : 0;

                    if (pageObj.TryGetProperty("data", out var dataArray) && dataArray.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in dataArray.EnumerateArray())
                        {
                            var title = item.GetProperty("name").GetString() ?? "";
                            var japaneseTitle =
                                item.TryGetProperty("name_jp", out var jp) && !string.IsNullOrWhiteSpace(jp.GetString())
                                    ? jp.GetString()
                                    : null;

                            results.Add(new SearchResult
                            {
                                Id            = item.GetProperty("ID").GetString() ?? "",
                                Title         = title,
                                JapaneseTitle = japaneseTitle,
                                PosterUrl = item.TryGetProperty("poster", out var poster)
                                                ? poster.GetString()?.Replace(".co", ".de")
                                                : null,
                                Url          = $"{BaseUrl}/anime/{item.GetProperty("ID").GetString()}",
                                ProviderName = Name,
                                Type         = ProviderType.Anime,
                                Year =
                                    item.TryGetProperty("release_year", out var yr) ? yr.GetInt32().ToString() : null,
                                Score = item.TryGetProperty("imdb_point", out var sc) ? sc.GetDouble() : null
                            });
                        }
                    }
                }
                else
                {
                    break;
                }

                page++;
            }
            while (notDisplayed > 0);

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "search failed");

            return [];
        }
    }

    public async Task<AnimeDetails> GetDetailsAsync(string animeId, CancellationToken cancellationToken = default)
    {
        var url      = $"{ApiUrl}/anime/get?id={animeId}";
        var response = await _httpClient.GetAsync(url, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"get anime details failed: {response.StatusCode}");
        }

        var       json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc  = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("data", out var data))
        {
            throw new InvalidOperationException("API returned no data in get anime response");
        }

        var isMovie = data.TryGetProperty("type", out var tProp) && tProp.GetString() == "movie";
        var tmdbId  = data.TryGetProperty("tmdb_id", out var tmdbProp) ? tmdbProp.GetString() : null;

        var title = data.GetProperty("name").GetString() ?? animeId;
        var japaneseTitle =
            data.TryGetProperty("name_jp", out var jpProp) && !string.IsNullOrWhiteSpace(jpProp.GetString())
                ? jpProp.GetString()
                : null;

        var altTitles = new List<string>();
        if (data.TryGetProperty("name_tr", out var trProp) && trProp.GetString() is { Length: > 0 } trName)
        {
            altTitles.Add(trName);
        }

        if (data.TryGetProperty("name_short", out var shortProp) && shortProp.GetString() is { Length: > 0 } shortName)
        {
            altTitles.Add(shortName);
        }

        var animeDetails = new AnimeDetails
        {
            Title         = title,
            JapaneseTitle = japaneseTitle,
            AlternativeTitles =
                altTitles.Where(t => !t.Equals(title, StringComparison.OrdinalIgnoreCase)
                                     && (japaneseTitle == null
                                         || !t.Equals(japaneseTitle, StringComparison.OrdinalIgnoreCase)))
                         .Distinct()
                         .ToList(),
            Summary = data.TryGetProperty("overview", out var ov) ? ov.GetString() ?? "" : "",
            Format  = isMovie ? ContentFormat.Movie : ContentFormat.Tv
        };

        if (isMovie)
        {
            animeDetails.SeasonMappings.Add(new SeasonMapping
            {
                SeasonNumber = 1,
                TmdbId       = tmdbId
            });

            animeDetails.Episodes.Add(new Episode
            {
                Id     = $"{animeId}|movie|movie",
                Title  = "Film",
                Number = 1,
                Season = 1
            });
        }
        else if (data.TryGetProperty("seasons", out var seasonsArray) && seasonsArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var season in seasonsArray.EnumerateArray())
            {
                var seasonNum = season.GetProperty("number").GetInt32();
                animeDetails.SeasonMappings.Add(new SeasonMapping
                {
                    SeasonNumber = seasonNum,
                    TmdbId       = tmdbId
                });

                if (season.TryGetProperty("episodes", out var epArray) && epArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var ep in epArray.EnumerateArray())
                    {
                        var epNum = ep.GetProperty("number").GetInt32();
                        animeDetails.Episodes.Add(new Episode
                        {
                            Id = $"{animeId}|{seasonNum}|{epNum}",
                            Title = ep.TryGetProperty("name", out var epName)
                                        ? epName.GetString() ?? $"{epNum}. Bölüm"
                                        : $"{epNum}. Bölüm",
                            Number = epNum,
                            Season = seasonNum
                        });
                    }
                }
            }
        }

        return animeDetails;
    }

    public async Task<List<VideoSource>> GetVideoSourcesAsync(string episodeId,
        string?                                                      group             = null,
        CancellationToken                                            cancellationToken = default)
    {
        try
        {
            var parts = episodeId.Split('|');

            if (parts.Length != 3)
            {
                return [];
            }

            var id      = parts[0];
            var season  = parts[1];
            var episode = parts[2];

            var servers = new[] { 1, 2 };

            var isMovie = season.Equals("movie", StringComparison.OrdinalIgnoreCase)
                          || episode.Equals("movie", StringComparison.OrdinalIgnoreCase);

            var partUrl = isMovie ? "" : $"&season={season}&episode={episode}";

            var sources = new List<VideoSource>();

            foreach (var server in servers)
            {
                var url = $"{ApiUrl}/anime/source?id={id}&plan=standart{partUrl}&server={server}";

                var response = await _httpClient.GetAsync(url, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                var       json = await response.Content.ReadAsStringAsync(cancellationToken);
                using var doc  = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("groups", out var groupsArray)
                    || groupsArray.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var subtitlesList = new List<Subtitle>();
                if (doc.RootElement.TryGetProperty("subtitles", out var subsArray)
                    && subsArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var s in subsArray.EnumerateArray())
                    {
                        subtitlesList.Add(new Subtitle
                        {
                            Language = s.GetProperty("group").GetString() ?? "",
                            Label    = s.GetProperty("name").GetString() ?? "",
                            Url      = s.GetProperty("link").GetString() ?? "",
                            Format   = "vtt"
                        });
                    }
                }

                foreach (var g in groupsArray.EnumerateArray())
                {
                    var groupName = g.GetProperty("name").GetString() ?? "Unknown";
                    var groupType = g.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : "hls";

                    var isMp4 = groupType?.Equals("mp4", StringComparison.OrdinalIgnoreCase) ?? false;

                    var rawLanguage = g.TryGetProperty("group", out var langProp) ? langProp.GetString() : null;

                    var language = rawLanguage switch
                    {
                        "trdub"    => "Türkçe",
                        "original" => "Japonca",
                        "endub"    => "İngilizce",
                        _          => rawLanguage ?? "Japonca"
                    };

                    if (g.TryGetProperty("items", out var itemsArray) && itemsArray.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in itemsArray.EnumerateArray())
                        {
                            var qualityVal = item.GetProperty("quality");
                            var quality = qualityVal.ValueKind == JsonValueKind.Number
                                              ? $"{qualityVal.GetInt32()}p"
                                              : $"{qualityVal.GetString()}p";

                            var linkUrl = item.GetProperty("link").GetString() ?? "";

                            var videoType = isMp4 || linkUrl.Contains(".mp4", StringComparison.OrdinalIgnoreCase)
                                                ? VideoType.Mp4
                                                : VideoType.M3U8;

                            sources.Add(new VideoSource
                            {
                                Url       = linkUrl,
                                Quality   = quality,
                                Type      = videoType,
                                Hoster    = "Anizium",
                                Group     = $"{groupName} (Server {server})",
                                Language  = language,
                                Subtitles = subtitlesList.Count > 0 ? subtitlesList : null
                            });
                        }
                    }
                }
            }

            return sources;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "failed to get video sources");

            return [];
        }
    }
}
