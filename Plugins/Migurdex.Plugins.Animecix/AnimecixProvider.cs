using Microsoft.Extensions.Logging;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace Migurdex.Plugins.Animecix;

public class AnimecixProvider : IAnimeProvider
{
    private static readonly ConcurrentDictionary<int, string> _translatorsCache = new();
    private static readonly Lock                              _translatorsLock  = new();
    private static          bool                              _translatorsLoaded;

    private readonly HttpClient                _httpClient;
    private readonly ILogger<AnimecixProvider> _logger;

    public AnimecixProvider(ISharedBridge bridge, ILogger<AnimecixProvider> logger)
    {
        _httpClient = bridge.CreateHttpClient();
        _logger     = logger;

        _httpClient.DefaultRequestHeaders.Add("Referer", BaseUrl);
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
        _httpClient.DefaultRequestHeaders.Add("X-E-H", "=.animecix");
    }

    public string       Name    => "AnimeciX";
    public string       BaseUrl => "https://animecix.tv";
    public ProviderType Type    => ProviderType.Anime;

    public async Task<List<SearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"{BaseUrl}/secure/search/{Uri.EscapeDataString(query)}?limit=24&type=undefined&provider=null";
            var response = await _httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrEmpty(json))
            {
                return [];
            }

            using var doc     = JsonDocument.Parse(json);
            var       results = new List<SearchResult>();

            if (doc.RootElement.TryGetProperty("results", out var resultsArray))
            {
                foreach (var item in resultsArray.EnumerateArray())
                {
                    var idStr = "";
                    if (item.TryGetProperty("id", out var idProp))
                    {
                        idStr = idProp.ValueKind == JsonValueKind.Number
                                    ? idProp.GetInt32().ToString()
                                    : idProp.GetString() ?? "";
                    }

                    if (string.IsNullOrEmpty(idStr) && item.TryGetProperty("_id", out var idStrProp))
                    {
                        idStr = idStrProp.GetString() ?? "";
                    }

                    string? yearStr = null;
                    if (item.TryGetProperty("year", out var yr))
                    {
                        yearStr = yr.ValueKind switch
                        {
                            JsonValueKind.Number => yr.GetInt32().ToString(),
                            JsonValueKind.String => yr.GetString(),
                            _                    => null
                        };
                    }

                    var title        = item.GetProperty("name").GetString() ?? "";
                    var englishTitle = item.TryGetProperty("name_english", out var eng) ? eng.GetString() : null;
                    var romajiTitle = item.TryGetProperty("name_romanji", out var rom)
                                          ? rom.GetString()
                                          : item.TryGetProperty("name_romaji", out var rom2)
                                              ? rom2.GetString()
                                              : null;
                    var japaneseTitle = item.TryGetProperty("original_title", out var orig) ? orig.GetString() : null;

                    var altTitles = new List<string>();
                    if (item.TryGetProperty("titles", out var titlesProp)
                        && titlesProp.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var t in titlesProp.EnumerateArray())
                        {
                            var tType  = t.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : "";
                            var tTitle = t.TryGetProperty("title", out var titleEl2) ? titleEl2.GetString() : "";
                            if (string.IsNullOrWhiteSpace(tTitle))
                            {
                                continue;
                            }

                            if (string.Equals(tType, "Japanese", StringComparison.OrdinalIgnoreCase)
                                && string.IsNullOrEmpty(japaneseTitle))
                            {
                                japaneseTitle = tTitle;
                            }
                            else if (string.Equals(tType, "English", StringComparison.OrdinalIgnoreCase)
                                     && string.IsNullOrEmpty(englishTitle))
                            {
                                englishTitle = tTitle;
                            }
                            else if (string.Equals(tType, "Synonym", StringComparison.OrdinalIgnoreCase))
                            {
                                altTitles.Add(tTitle);
                            }
                        }
                    }

                    var searchResult = new SearchResult
                    {
                        Id            = idStr,
                        Title         = title,
                        EnglishTitle  = englishTitle,
                        RomajiTitle   = romajiTitle,
                        JapaneseTitle = japaneseTitle,
                        AlternativeTitles =
                        [
                            .. altTitles.Where(t => !t.Equals(title, StringComparison.OrdinalIgnoreCase)
                                                    && (englishTitle == null
                                                        || !t.Equals(
                                                            englishTitle,
                                                            StringComparison.OrdinalIgnoreCase))
                                                    && (romajiTitle == null
                                                        || !t.Equals(
                                                            romajiTitle,
                                                            StringComparison.OrdinalIgnoreCase))
                                                    && (japaneseTitle == null
                                                        || !t.Equals(
                                                            japaneseTitle,
                                                            StringComparison.OrdinalIgnoreCase)))
                                        .Distinct()
                        ],
                        PosterUrl    = item.TryGetProperty("poster", out var pst) ? pst.GetString() : null,
                        Url          = $"{BaseUrl}/titles/{idStr}",
                        ProviderName = Name,
                        Type         = ProviderType.Anime,
                        Year         = yearStr,
                        Score        = null
                    };

                    if (item.TryGetProperty("genres", out var genres) && genres.ValueKind == JsonValueKind.Array)
                    {
                        searchResult.Categories =
                        [
                            .. genres.EnumerateArray()
                                     .Select(g => g.GetProperty("name").GetString() ?? "")
                        ];
                    }

                    results.Add(searchResult);
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
            var detailsUrl = $"{BaseUrl}/secure/titles/{animeId}";
            var response   = await _httpClient.GetAsync(detailsUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new AnimeDetails();
            }

            var detailsJson = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrEmpty(detailsJson))
            {
                return new AnimeDetails();
            }

            using var doc     = JsonDocument.Parse(detailsJson);
            var       titleEl = doc.RootElement.GetProperty("title");

            var title        = titleEl.GetProperty("name").GetString() ?? "";
            var englishTitle = titleEl.TryGetProperty("name_english", out var eng) ? eng.GetString() : null;
            var romajiTitle = titleEl.TryGetProperty("name_romanji", out var rom)
                                  ? rom.GetString()
                                  : titleEl.TryGetProperty("name_romaji", out var rom2)
                                      ? rom2.GetString()
                                      : null;
            var japaneseTitle = titleEl.TryGetProperty("original_title", out var orig) ? orig.GetString() : null;

            var altTitles = new List<string>();
            if (titleEl.TryGetProperty("titles", out var titlesProp) && titlesProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in titlesProp.EnumerateArray())
                {
                    var tType  = t.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : "";
                    var tTitle = t.TryGetProperty("title", out var titleEl2) ? titleEl2.GetString() : "";
                    if (string.IsNullOrWhiteSpace(tTitle))
                    {
                        continue;
                    }

                    if (string.Equals(tType, "Japanese", StringComparison.OrdinalIgnoreCase)
                        && string.IsNullOrEmpty(japaneseTitle))
                    {
                        japaneseTitle = tTitle;
                    }
                    else if (string.Equals(tType, "English", StringComparison.OrdinalIgnoreCase)
                             && string.IsNullOrEmpty(englishTitle))
                    {
                        englishTitle = tTitle;
                    }
                    else if (string.Equals(tType, "Synonym", StringComparison.OrdinalIgnoreCase))
                    {
                        altTitles.Add(tTitle);
                    }
                }
            }

            var details = new AnimeDetails
            {
                Title         = title,
                EnglishTitle  = englishTitle,
                RomajiTitle   = romajiTitle,
                JapaneseTitle = japaneseTitle,
                AlternativeTitles =
                [
                    .. altTitles.Where(t => !t.Equals(title, StringComparison.OrdinalIgnoreCase)
                                            && (englishTitle == null
                                                || !t.Equals(
                                                    englishTitle,
                                                    StringComparison.OrdinalIgnoreCase))
                                            && (romajiTitle == null
                                                || !t.Equals(
                                                    romajiTitle,
                                                    StringComparison.OrdinalIgnoreCase))
                                            && (japaneseTitle == null
                                                || !t.Equals(
                                                    japaneseTitle,
                                                    StringComparison.OrdinalIgnoreCase)))
                                .Distinct()
                ],
                Summary = titleEl.TryGetProperty("description", out var desc) ? desc.GetString() ?? "" : ""
            };

            string? rootAniListId = null;
            if (titleEl.TryGetProperty("anilist_id", out var aniProp))
            {
                rootAniListId = aniProp.ValueKind == JsonValueKind.Number
                                    ? aniProp.GetInt32().ToString()
                                    : aniProp.GetString();

                if (rootAniListId == "0" || string.IsNullOrEmpty(rootAniListId))
                {
                    rootAniListId = null;
                }
            }

            string? rootMalId = null;
            if (titleEl.TryGetProperty("mal_id", out var malProp))
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
            if (titleEl.TryGetProperty("tmdb_id", out var tmdbProp))
            {
                rootTmdbId = tmdbProp.ValueKind == JsonValueKind.Number
                                 ? tmdbProp.GetInt32().ToString()
                                 : tmdbProp.GetString();

                if (rootTmdbId == "0" || string.IsNullOrEmpty(rootTmdbId))
                {
                    rootTmdbId = null;
                }
            }

            if (titleEl.TryGetProperty("seasons", out var seasonsProp) && seasonsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in seasonsProp.EnumerateArray())
                {
                    var seasonNum = 1;
                    if (s.TryGetProperty("number", out var numProp))
                    {
                        seasonNum = numProp.ValueKind == JsonValueKind.Number
                                        ? (int) numProp.GetDouble()
                                        : int.TryParse(numProp.GetString(), out var parsedNum)
                                            ? parsedNum
                                            : 1;
                    }

                    details.SeasonMappings.Add(new SeasonMapping
                    {
                        SeasonNumber  = seasonNum,
                        AniListId     = seasonNum == 1 ? rootAniListId : null,
                        MyAnimeListId = seasonNum == 1 ? rootMalId : null,
                        TmdbId        = rootTmdbId
                    });
                }
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

            var typeStr = titleEl.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : "";
            details.Format = string.Equals(typeStr, "movie", StringComparison.OrdinalIgnoreCase)
                                 ? ContentFormat.Movie
                                 : ContentFormat.Tv;

            var seasonsList = details.SeasonMappings.Select(m => m.SeasonNumber).Distinct().ToList();
            if (seasonsList.Count == 0)
            {
                seasonsList.Add(1); // default to season 1
            }

            var episodesBag = new ConcurrentBag<Episode>();

            var fetchTasks = seasonsList.Select(async seasonNum =>
            {
                try
                {
                    var firstPageUrl = $"{BaseUrl}/secure/titles/{animeId}?seasonNumber={seasonNum}&page=1&perPage=200";
                    var firstResponse = await _httpClient.GetAsync(firstPageUrl, cancellationToken);
                    if (!firstResponse.IsSuccessStatusCode)
                    {
                        return;
                    }

                    var firstPageJson = await firstResponse.Content.ReadAsStringAsync(cancellationToken);
                    if (string.IsNullOrEmpty(firstPageJson))
                    {
                        return;
                    }

                    using var firstDoc  = JsonDocument.Parse(firstPageJson);
                    var       firstRoot = firstDoc.RootElement;

                    var lastPage = 1;
                    if (firstRoot.TryGetProperty("title", out var tProp)
                        && tProp.TryGetProperty("seasons", out var seasonsArray)
                        && seasonsArray.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var season in seasonsArray.EnumerateArray())
                        {
                            if (season.TryGetProperty("episodePagination", out var epPagination))
                            {
                                if (epPagination.TryGetProperty("data", out var epData)
                                    && epData.ValueKind == JsonValueKind.Array)
                                {
                                    foreach (var ep in epData.EnumerateArray())
                                    {
                                        var parsedEp = ParseEpisode(ep, animeId, seasonNum, seasonsList.Count);
                                        episodesBag.Add(parsedEp);
                                    }
                                }

                                if (epPagination.TryGetProperty("last_page", out var lastPageProp))
                                {
                                    lastPage = lastPageProp.ValueKind == JsonValueKind.Number
                                                   ? lastPageProp.GetInt32()
                                                   : int.TryParse(lastPageProp.GetString(), out var parsedLastPage)
                                                       ? parsedLastPage
                                                       : 1;
                                }

                                break;
                            }
                        }
                    }

                    if (lastPage > 1)
                    {
                        var pageTasks = Enumerable.Range(2, lastPage - 1)
                                                  .Select(async currentPage =>
                                                  {
                                                      try
                                                      {
                                                          var pageUrl =
                                                              $"{BaseUrl}/secure/titles/{animeId}?seasonNumber={seasonNum}&page={currentPage}&perPage=200";

                                                          var pageResponse = await _httpClient.GetAsync(
                                                                                 pageUrl,
                                                                                 cancellationToken);
                                                          if (!pageResponse.IsSuccessStatusCode)
                                                          {
                                                              return;
                                                          }

                                                          var pageJson =
                                                              await pageResponse.Content.ReadAsStringAsync(
                                                                  cancellationToken);
                                                          if (string.IsNullOrEmpty(pageJson))
                                                          {
                                                              return;
                                                          }

                                                          using var pageDoc  = JsonDocument.Parse(pageJson);
                                                          var       pageRoot = pageDoc.RootElement;

                                                          if (pageRoot.TryGetProperty("title", out var pTitleProp)
                                                              && pTitleProp.TryGetProperty(
                                                                  "seasons",
                                                                  out var pSeasonsArray)
                                                              && pSeasonsArray.ValueKind == JsonValueKind.Array)
                                                          {
                                                              foreach (var pSeason in pSeasonsArray.EnumerateArray())
                                                              {
                                                                  if (pSeason.TryGetProperty("episodePagination",
                                                                          out var pEpPagination))
                                                                  {
                                                                      if (pEpPagination.TryGetProperty(
                                                                              "data",
                                                                              out var pEpData)
                                                                          && pEpData.ValueKind == JsonValueKind.Array)
                                                                      {
                                                                          foreach (var ep in pEpData.EnumerateArray())
                                                                          {
                                                                              var parsedEp =
                                                                                  ParseEpisode(ep,
                                                                                      animeId,
                                                                                      seasonNum,
                                                                                      seasonsList.Count);
                                                                              episodesBag.Add(parsedEp);
                                                                          }
                                                                      }

                                                                      break;
                                                                  }
                                                              }
                                                          }
                                                      }
                                                      catch (Exception ex)
                                                      {
                                                          _logger.LogError(ex,
                                                                           "failed to load season page {Page}",
                                                                           currentPage);
                                                      }
                                                  });

                        await Task.WhenAll(pageTasks);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "failed to load details page for season {Season}", seasonNum);
                }
            });

            await Task.WhenAll(fetchTasks);

            foreach (var ep in episodesBag)
            {
                if (details.Episodes.All(e => e.Id != ep.Id))
                {
                    details.Episodes.Add(ep);
                }
            }

            if (details.Format == ContentFormat.Movie && !details.Episodes.Any())
            {
                details.Episodes.Add(new Episode
                {
                    Id     = $"{animeId}/movie/movie",
                    Title  = "Film",
                    Number = 1,
                    Season = 1
                });
            }

            details.Episodes =
            [
                .. details.Episodes
                          .OrderBy(e => e.Season ?? 1)
                          .ThenBy(e => e.Number)
            ];

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
            var parts = episodeId.Split('/');
            if (parts.Length < 3)
            {
                return [];
            }

            var titleId = parts[0];
            var season  = parts[1];
            var episode = parts[2];

            var url = $"{BaseUrl}/secure/episode-videos-points?titleId={titleId}&episode={episode}&season={season}";
            var response = await _httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrEmpty(json))
            {
                return [];
            }

            using var doc    = JsonDocument.Parse(json);
            var       groups = new List<string>();

            await EnsureTranslatorsLoadedAsync(cancellationToken);

            if (doc.RootElement.TryGetProperty("videos", out var videosArray)
                && videosArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var video in videosArray.EnumerateArray())
                {
                    var extra = video.TryGetProperty("extra", out var extraProp) ? extraProp.GetString() ?? "" : "";
                    var name  = video.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "";

                    var groupLabel = "";
                    if (video.TryGetProperty("translator", out var transProp)
                        && transProp.ValueKind == JsonValueKind.Number)
                    {
                        var transId = transProp.GetInt32();
                        _translatorsCache.TryGetValue(transId, out groupLabel);
                    }
                    else if (video.TryGetProperty("template", out var tempProp)
                             && tempProp.ValueKind == JsonValueKind.Number)
                    {
                        var tempId = tempProp.GetInt32();
                        _translatorsCache.TryGetValue(tempId, out groupLabel);
                    }

                    if (string.IsNullOrEmpty(groupLabel))
                    {
                        groupLabel = name;
                    }

                    if (!string.IsNullOrEmpty(groupLabel) && !groups.Contains(groupLabel))
                    {
                        groups.Add(groupLabel);
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
            var parts = episodeId.Split('/');
            if (parts.Length < 3)
            {
                return [];
            }

            var titleId = parts[0];
            var season  = parts[1];
            var episode = parts[2];

            var isMovie = season.Equals("movie", StringComparison.OrdinalIgnoreCase)
                          || episode.Equals("movie", StringComparison.OrdinalIgnoreCase);

            var sources = new List<VideoSource>();
            await EnsureTranslatorsLoadedAsync(cancellationToken);

            if (isMovie)
            {
                _logger.LogInformation("loading movie sources directly from title details for: {TitleId}",
                                       titleId);

                var detailsUrl = $"{BaseUrl}/secure/titles/{titleId}";
                var response   = await _httpClient.GetAsync(detailsUrl, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    return [];
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                if (string.IsNullOrEmpty(json))
                {
                    return [];
                }

                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("title", out var titleElement)
                    && titleElement.TryGetProperty("videos", out var movieVideos)
                    && movieVideos.ValueKind == JsonValueKind.Array)
                {
                    ParseVideosJson(movieVideos, sources, group);
                }

                return sources;
            }

            var tvUrl = $"{BaseUrl}/secure/episode-videos-points?titleId={titleId}&episode={episode}&season={season}";
            var tvResponse = await _httpClient.GetAsync(tvUrl, cancellationToken);

            if (!tvResponse.IsSuccessStatusCode)
            {
                return [];
            }

            var tvJson = await tvResponse.Content.ReadAsStringAsync(cancellationToken);

            if (string.IsNullOrEmpty(tvJson))
            {
                return [];
            }

            using var tvDoc = JsonDocument.Parse(tvJson);
            if (tvDoc.RootElement.TryGetProperty("videos", out var tvVideos)
                && tvVideos.ValueKind == JsonValueKind.Array)
            {
                ParseVideosJson(tvVideos, sources, group);
            }

            return sources;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "getVideoSourcesAsync failed for {EpisodeId}", episodeId);

            return [];
        }
    }

    private void ParseVideosJson(JsonElement videosArray, List<VideoSource> sources, string? group)
    {
        foreach (var video in videosArray.EnumerateArray())
        {
            var name     = video.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "";
            var embedUrl = video.TryGetProperty("url", out var urlProp) ? urlProp.GetString() ?? "" : "";
            var videoId = video.TryGetProperty("id", out var idProp)
                              ? idProp.ValueKind == JsonValueKind.Number
                                    ? idProp.GetInt32().ToString()
                                    : idProp.GetString() ?? ""
                              : "";

            var groupLabel = "";
            if (video.TryGetProperty("translator", out var transProp) && transProp.ValueKind == JsonValueKind.Number)
            {
                var transId = transProp.GetInt32();
                _translatorsCache.TryGetValue(transId, out groupLabel);
            }
            else if (video.TryGetProperty("template", out var tempProp) && tempProp.ValueKind == JsonValueKind.Number)
            {
                var tempId = tempProp.GetInt32();
                _translatorsCache.TryGetValue(tempId, out groupLabel);
            }

            if (string.IsNullOrEmpty(groupLabel))
            {
                groupLabel = name;
            }

            if (!string.IsNullOrEmpty(group) && !string.Equals(group, groupLabel, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.IsNullOrEmpty(embedUrl))
            {
                continue;
            }

            sources.Add(new VideoSource
            {
                Url     = embedUrl,
                Type    = VideoType.Embed,
                Quality = "Embed",
                Group   = groupLabel
            });
        }
    }

    private async Task EnsureTranslatorsLoadedAsync(CancellationToken cancellationToken = default)
    {
        if (_translatorsLoaded)
        {
            return;
        }

        lock (_translatorsLock)
        {
            if (_translatorsLoaded)
            {
                return;
            }
        }

        try
        {
            var url      = $"{BaseUrl}/secure/translators";
            var response = await _httpClient.GetAsync(url, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!string.IsNullOrEmpty(json))
                {
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in doc.RootElement.EnumerateArray())
                        {
                            var id = 0;
                            if (item.TryGetProperty("id", out var idProp))
                            {
                                id = idProp.ValueKind == JsonValueKind.Number
                                         ? idProp.GetInt32()
                                         : int.TryParse(idProp.GetString(), out var parsedId)
                                             ? parsedId
                                             : 0;
                            }

                            if (id > 0)
                            {
                                var name = item.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
                                var translator = item.TryGetProperty("translator", out var transProp)
                                                     ? transProp.GetString()
                                                     : null;

                                var displayName = translator ?? name ?? "";
                                if (!string.IsNullOrEmpty(displayName))
                                {
                                    _translatorsCache[id] = displayName;
                                }
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "failed to load translators map");
        }
        finally
        {
            lock (_translatorsLock)
            {
                _translatorsLoaded = true;
            }
        }
    }

    private static Episode ParseEpisode(JsonElement ep, string id, int seasonNum, int totalSeasonsCount)
    {
        double epNum = 1;
        if (ep.TryGetProperty("episode_number", out var epNumProp))
        {
            epNum = epNumProp.ValueKind switch
            {
                JsonValueKind.Number => epNumProp.GetDouble(),
                JsonValueKind.String => double.TryParse(epNumProp.GetString()?.Replace(',', '.'),
                                                        NumberStyles.Any,
                                                        CultureInfo.InvariantCulture,
                                                        out var parsedEpNum)
                                            ? parsedEpNum
                                            : 1,
                _ => epNum
            };
        }

        var name    = ep.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : "";
        var subName = ep.TryGetProperty("sub_name", out var subNameProp) ? subNameProp.GetString() : "";

        var epTitle = string.IsNullOrEmpty(name)
                          ? string.IsNullOrEmpty(subName) ? $"{epNum}. Bölüm" : subName
                          : string.IsNullOrEmpty(subName)
                              ? name
                              : $"{subName} - {name}";

        var compositeId = $"{id}/{seasonNum}/{epNum}";

        return new Episode
        {
            Id     = compositeId,
            Title  = epTitle,
            Number = epNum,
            Season = seasonNum
        };
    }
}
