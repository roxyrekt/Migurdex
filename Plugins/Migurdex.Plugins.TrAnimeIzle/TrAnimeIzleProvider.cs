using Microsoft.Extensions.Logging;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Migurdex.Plugins.TrAnimeIzle;

public partial class TrAnimeIzleProvider : IAnimeProvider
{
    private const int WatchRequestDelayMs = 100;

    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/135.0.0.0 Safari/537.36";

    private const string StubImageUrl =
        "https://pp.userapi.com/c857436/v857436366/6d9e/84dLrNaE_yo.jpg";

    private static readonly HashSet<string> UnsupportedProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Mega",
        "Filesfm",
        "Upstream",
        "Vudeo"
    };

    private readonly HttpClient                   _httpClient;
    private readonly ILogger<TrAnimeIzleProvider> _logger;
    private readonly SemaphoreSlim                _watchSemaphore = new(3, 3);

    private volatile string? _latestWidCookie;

    public TrAnimeIzleProvider(ISharedBridge bridge, ILogger<TrAnimeIzleProvider> logger)
    {
        _logger = logger;

        _httpClient = bridge.CreateHttpClient(o =>
        {
            o.UseCookies        = false;
            o.AllowAutoRedirect = true;
        });

        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Referer", $"{BaseUrl}/");
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language",
                                                                  "tr-TR,tr;q=0.9,en-US;q=0.8,en;q=0.7");
    }

    public string       Name    => "TrAnimeIzle";
    public string       BaseUrl => "https://www.tranimeizle.live";
    public ProviderType Type    => ProviderType.Anime;

    public ProviderCapabilities Capabilities => ProviderCapabilities.Search | ProviderCapabilities.Fansubs;

    public async Task<List<SearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        try
        {
            var catalogResults = await SearchCatalogAsync(query, cancellationToken).ConfigureAwait(false);
            if (catalogResults is not null)
            {
                return catalogResults;
            }

            return await SearchSuggestAsync(query, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
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
            var encodedSlug = Uri.EscapeDataString(animeId);
            var animeTask   = GetJsonAsync($"{BaseUrl}/api/anime/{encodedSlug}", cancellationToken);
            var epsTask     = FetchAllEpisodesAsync(encodedSlug, cancellationToken);

            await Task.WhenAll(animeTask, epsTask).ConfigureAwait(false);

            using var animeDoc = await animeTask.ConfigureAwait(false);
            var       episodes = await epsTask.ConfigureAwait(false);

            if (animeDoc is null)
            {
                return new AnimeDetails();
            }

            var root = animeDoc.RootElement;
            if (TryGetBool(root, "ageGate") == true)
            {
                return new AnimeDetails();
            }

            var (primary, romaji, english) = ParseTitles(root);
            var title = !string.IsNullOrWhiteSpace(primary) ? primary : animeId;

            var altTitles = new List<string>();
            if (root.TryGetProperty("synonyms", out var synProp) && synProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var syn in synProp.EnumerateArray())
                {
                    var val = syn.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(val))
                    {
                        altTitles.Add(val);
                    }
                }
            }

            var synopsisHtml = TryGetString(root, "synopsis");
            var summary      = StripHtml(synopsisHtml);
            var posterUrl    = TryGetString(root, "posterUrl");
            var typeStr      = TryGetString(root, "type");
            var format       = MapContentFormat(typeStr, title);

            var (anilistId, malId, tmdbId) = ParseExternalLinks(root);

            var defaultSeason = AnimeDetails.ParseSeasonNumber(title);
            if (defaultSeason < 1)
            {
                defaultSeason = 1;
            }

            var details = new AnimeDetails
            {
                Title             = title,
                EnglishTitle      = english,
                RomajiTitle       = romaji,
                PosterUrl         = posterUrl,
                AlternativeTitles = altTitles,
                Summary           = summary,
                Format            = format
            };

            var parsedSeasons = new HashSet<int>();
            foreach (var ep in episodes)
            {
                if (string.IsNullOrWhiteSpace(ep.Slug))
                {
                    continue;
                }

                var seasonNum = ep.SeasonNumber ?? defaultSeason;
                if (seasonNum < 1)
                {
                    seasonNum = 1;
                }

                var epNum = ep.Number ?? TryParseEpisodeNumber(ep.Title, ep.Slug);
                if (epNum is null)
                {
                    continue;
                }

                parsedSeasons.Add(seasonNum);
                details.Episodes.Add(new Episode
                {
                    Id     = ep.Slug,
                    Title  = FormatEpisodeTitle(epNum.Value, ep),
                    Number = epNum.Value,
                    Season = seasonNum
                });
            }

            var orderedSeasons = parsedSeasons.Count > 0
                                     ? parsedSeasons.OrderBy(s => s).ToList()
                                     : [1];

            var primarySeason = orderedSeasons[0];
            foreach (var season in orderedSeasons)
            {
                details.SeasonMappings.Add(new SeasonMapping
                {
                    SeasonNumber  = season,
                    AniListId     = season == primarySeason ? anilistId : null,
                    MyAnimeListId = season == primarySeason ? malId : null,
                    TmdbId        = season == primarySeason ? tmdbId : null
                });
            }

            details.Episodes = details.Episodes
                                      .OrderBy(e => e.Season ?? 1)
                                      .ThenBy(e => e.Number)
                                      .ToList();

            return details;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "getDetailsAsync failed for: {AnimeId}", animeId);
            return new AnimeDetails();
        }
    }

    public async Task<List<string>> GetGroupsAsync(string episodeId, CancellationToken cancellationToken = default)
    {
        try
        {
            var episode = await GetEpisodePayloadAsync(episodeId, cancellationToken).ConfigureAwait(false);
            if (episode is null)
            {
                return [];
            }

            var groups = new List<string>();
            var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var video in episode.Videos)
            {
                if (!string.IsNullOrWhiteSpace(video.FansubName) && seen.Add(video.FansubName))
                {
                    groups.Add(video.FansubName);
                }
            }

            return groups;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "getGroupsAsync failed for: {EpisodeId}", episodeId);
            return [];
        }
    }

    public async Task<List<VideoSource>> GetVideoSourcesAsync(string episodeId,
        string?                                                      group             = null,
        CancellationToken                                            cancellationToken = default)
    {
        var sources = new List<VideoSource>();

        try
        {
            var episode = await GetEpisodePayloadAsync(episodeId, cancellationToken).ConfigureAwait(false);
            if (episode is null || episode.Videos.Count == 0)
            {
                return sources;
            }

            var candidates = episode.Videos
                                    .Where(v => !string.IsNullOrWhiteSpace(v.Id))
                                    .Where(v => string.IsNullOrWhiteSpace(group)
                                                || string.Equals(v.FansubName,
                                                                 group,
                                                                 StringComparison.OrdinalIgnoreCase))
                                    .Where(v => string.IsNullOrWhiteSpace(v.ProviderName)
                                                || !UnsupportedProviders.Contains(v.ProviderName))
                                    .ToList();

            if (candidates.Count == 0)
            {
                return sources;
            }

            var rateLimited = 0;

            var tasks = candidates.Select(async video =>
            {
                if (Volatile.Read(ref rateLimited) != 0)
                {
                    return (List<VideoSource>) [];
                }

                WatchResolution resolution;
                await _watchSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (Volatile.Read(ref rateLimited) != 0)
                    {
                        return [];
                    }

                    resolution = await ResolveWatchUrlAsync(video.Id, episodeId, cancellationToken)
                                     .ConfigureAwait(false);
                    if (resolution.RateLimited)
                    {
                        Interlocked.Exchange(ref rateLimited, 1);
                        return [];
                    }

                    if (WatchRequestDelayMs > 0)
                    {
                        await Task.Delay(WatchRequestDelayMs, cancellationToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    _watchSemaphore.Release();
                }

                if (string.IsNullOrWhiteSpace(resolution.Url))
                {
                    return [];
                }

                return await BuildVideoSourcesAsync(video, resolution, cancellationToken)
                           .ConfigureAwait(false);
            });

            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            foreach (var list in results)
            {
                sources.AddRange(list);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "getVideoSourcesAsync failed for: {EpisodeId}", episodeId);
        }

        return sources.GroupBy(x => x.Url, StringComparer.OrdinalIgnoreCase)
                      .Select(x => x.First())
                      .ToList();
    }

    private async Task<List<SearchResult>?> SearchCatalogAsync(string query, CancellationToken cancellationToken)
    {
        var payload = new
        {
            requests = new[]
            {
                new
                {
                    indexName = "anime",
                    @params = new
                    {
                        hitsPerPage = 36,
                        page        = 0,
                        query
                    }
                }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/api/search/catalog")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("Origin", BaseUrl);
        request.Headers.TryAddWithoutValidation("Referer", $"{BaseUrl}/katalog");

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("catalog search failed with status {StatusCode}", response.StatusCode);
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out var resultsEl)
            || resultsEl.ValueKind != JsonValueKind.Array
            || resultsEl.GetArrayLength() == 0)
        {
            return [];
        }

        var firstResult = resultsEl[0];
        if (!firstResult.TryGetProperty("hits", out var hitsEl) || hitsEl.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var results = new List<SearchResult>();
        foreach (var hit in hitsEl.EnumerateArray())
        {
            var slug = TryGetString(hit, "slug");
            if (string.IsNullOrWhiteSpace(slug))
            {
                continue;
            }

            var (primary, romaji, english) = ParseTitles(hit);
            var title   = !string.IsNullOrWhiteSpace(primary) ? primary : slug;
            var typeStr = TryGetString(hit, "type");

            results.Add(new SearchResult
            {
                Id           = slug,
                Title        = title,
                EnglishTitle = english,
                RomajiTitle  = romaji,
                PosterUrl    = TryGetString(hit, "posterUrl"),
                Url          = $"{BaseUrl}/anime/{slug}",
                ProviderName = Name,
                Type         = ProviderType.Anime,
                Format       = MapContentFormat(typeStr, title),
                Year         = TryGetInt(hit, "year")?.ToString(CultureInfo.InvariantCulture),
                Score        = TryGetDouble(hit, "averageScore")
            });
        }

        return results;
    }

    private async Task<List<SearchResult>> SearchSuggestAsync(string query, CancellationToken cancellationToken)
    {
        var       url = $"{BaseUrl}/api/search/suggest?q={Uri.EscapeDataString(query)}&limit=20";
        using var doc = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
        if (doc is null
            || !doc.RootElement.TryGetProperty("items", out var itemsEl)
            || itemsEl.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var results = new List<SearchResult>();
        foreach (var item in itemsEl.EnumerateArray())
        {
            var itemType = TryGetString(item, "type");
            if (!string.Equals(itemType, "anime", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var slug = TryGetString(item, "slug");
            if (string.IsNullOrWhiteSpace(slug))
            {
                continue;
            }

            var     title   = TryGetString(item, "title") ?? slug;
            string? romaji  = null;
            string? english = null;
            if (item.TryGetProperty("names", out var namesEl) && namesEl.ValueKind == JsonValueKind.Object)
            {
                romaji  = TryGetString(namesEl, "romaji");
                english = TryGetString(namesEl, "english");
            }

            var formatStr = TryGetString(item, "format");
            results.Add(new SearchResult
            {
                Id           = slug,
                Title        = title,
                EnglishTitle = english,
                RomajiTitle  = romaji,
                PosterUrl    = TryGetString(item, "posterUrl"),
                Url          = $"{BaseUrl}/anime/{slug}",
                ProviderName = Name,
                Type         = ProviderType.Anime,
                Format       = MapContentFormat(formatStr, title),
                Year         = TryGetInt(item, "year")?.ToString(CultureInfo.InvariantCulture),
                Score        = null
            });
        }

        return results;
    }

    private async Task<List<EpisodeSummary>> FetchAllEpisodesAsync(string encodedSlug,
        CancellationToken                                                 cancellationToken)
    {
        const int pageSize = 3000;
        var       all      = new List<EpisodeSummary>();
        var       offset   = 0;

        while (true)
        {
            var       url = $"{BaseUrl}/api/anime/{encodedSlug}/episodes?limit={pageSize}&offset={offset}";
            using var doc = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
            if (doc is null)
            {
                break;
            }

            var root = doc.RootElement;
            if (!root.TryGetProperty("items", out var itemsEl) || itemsEl.ValueKind != JsonValueKind.Array)
            {
                break;
            }

            var pageCount = 0;
            foreach (var item in itemsEl.EnumerateArray())
            {
                pageCount++;
                var slug = TryGetString(item, "slug");
                if (string.IsNullOrWhiteSpace(slug))
                {
                    continue;
                }

                int?    seasonNum   = null;
                string? designation = null;
                var     reviewed    = false;
                string? epSubTitle  = null;

                if (item.TryGetProperty("naming", out var namingEl) && namingEl.ValueKind == JsonValueKind.Object)
                {
                    reviewed    = TryGetBool(namingEl, "reviewed") == true;
                    designation = TryGetString(namingEl, "designation");
                    if (reviewed)
                    {
                        seasonNum = TryGetInt(namingEl, "seasonNumber");
                    }

                    epSubTitle = TryGetString(namingEl, "titleTr")
                                 ?? TryGetString(namingEl, "titleEn")
                                 ?? TryGetString(namingEl, "titleRomaji");
                }

                all.Add(new EpisodeSummary(
                            slug,
                            TryGetDouble(item, "number"),
                            TryGetString(item, "title") ?? string.Empty,
                            seasonNum,
                            reviewed ? designation : null,
                            epSubTitle));
            }

            var hasMore = root.TryGetProperty("pagination", out var pagEl)
                          && pagEl.ValueKind == JsonValueKind.Object
                          && TryGetBool(pagEl, "hasMore") == true;

            if (!hasMore || pageCount == 0)
            {
                break;
            }

            offset += pageCount;
        }

        return all;
    }

    private async Task<EpisodePayload?> GetEpisodePayloadAsync(string episodeId, CancellationToken cancellationToken)
    {
        var key = episodeId.Trim();
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        var       url = $"{BaseUrl}/api/episode/{Uri.EscapeDataString(key)}";
        using var doc = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
        if (doc is null)
        {
            return null;
        }

        var root = doc.RootElement;
        if (TryGetBool(root, "ageGate") == true)
        {
            return null;
        }

        var videos = new List<EpisodeVideo>();
        if (root.TryGetProperty("videos", out var videosEl) && videosEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var v in videosEl.EnumerateArray())
            {
                var id = TryGetString(v, "id");
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                videos.Add(new EpisodeVideo(
                               id,
                               TryGetString(v, "fansubName"),
                               TryGetString(v, "providerName"),
                               TryGetBool(v, "hls") == true));
            }
        }

        return new EpisodePayload(videos);
    }

    private async Task<WatchResolution> ResolveWatchUrlAsync(string videoId,
        string                                                      episodeId,
        CancellationToken                                           cancellationToken)
    {
        using var request =
            new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/api/watch/{Uri.EscapeDataString(videoId)}");
        request.Headers.TryAddWithoutValidation("Referer", $"{BaseUrl}/{episodeId.TrimStart('/')}");

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
        {
            _logger.LogWarning("watch endpoint returned {StatusCode} for videoId {VideoId}",
                               response.StatusCode,
                               videoId);
            return new WatchResolution(null, null, true);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogDebug("watch endpoint returned {StatusCode} for videoId {VideoId}",
                             response.StatusCode,
                             videoId);
            return new WatchResolution(null, null, false);
        }

        var wid = ExtractWidCookie(response);
        if (!string.IsNullOrWhiteSpace(wid))
        {
            _latestWidCookie = wid;
        }
        else
        {
            wid = _latestWidCookie;
        }

        var rawUrl = (await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).Trim();
        return string.IsNullOrWhiteSpace(rawUrl)
                   ? new WatchResolution(null, wid, false)
                   : new WatchResolution(rawUrl, wid, false);
    }

    private async Task<List<VideoSource>> BuildVideoSourcesAsync(EpisodeVideo video,
        WatchResolution                                                       resolution,
        CancellationToken                                                     cancellationToken)
    {
        var rawUrl = resolution.Url!;
        if (rawUrl.StartsWith("/w/", StringComparison.OrdinalIgnoreCase)
            || rawUrl.StartsWith(BaseUrl + "/w/", StringComparison.OrdinalIgnoreCase))
        {
            var masterUrl = rawUrl.StartsWith("/") ? BaseUrl + rawUrl : rawUrl;
            return await BuildAitrVipSourcesAsync(masterUrl, resolution.WidCookie, video, cancellationToken)
                       .ConfigureAwait(false);
        }

        var embedUrl = NormalizeEmbedUrl(rawUrl);
        if (string.IsNullOrWhiteSpace(embedUrl)
            || embedUrl.Equals(StubImageUrl, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        return
        [
            new VideoSource
            {
                Url     = embedUrl,
                Quality = "Embed",
                Type    = VideoType.Embed,
                Group   = video.FansubName,
                Hoster  = video.ProviderName
            }
        ];
    }

    private async Task<List<VideoSource>> BuildAitrVipSourcesAsync(string masterUrl,
        string?                                                           widCookie,
        EpisodeVideo                                                      video,
        CancellationToken                                                 cancellationToken)
    {
        var hoster  = string.IsNullOrWhiteSpace(video.ProviderName) ? "AitrVip" : video.ProviderName;
        var headers = BuildAitrHeaders(widCookie);

        try
        {
            using var masterReq = new HttpRequestMessage(HttpMethod.Get, masterUrl);
            if (!string.IsNullOrWhiteSpace(widCookie))
            {
                masterReq.Headers.TryAddWithoutValidation("Cookie", $"wid={widCookie}");
            }

            using var masterResp = await _httpClient.SendAsync(masterReq, cancellationToken).ConfigureAwait(false);
            if (!masterResp.IsSuccessStatusCode)
            {
                _logger.LogWarning("AitrVip master playlist failed with {StatusCode}", masterResp.StatusCode);
                return
                [
                    new VideoSource
                    {
                        Url     = masterUrl,
                        Quality = "Auto",
                        Type    = VideoType.M3U8,
                        Hoster  = hoster,
                        Group   = video.FansubName,
                        Headers = headers
                    }
                ];
            }

            var masterContent = await masterResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var variants      = ParseMasterPlaylist(masterContent, masterUrl);
            if (variants.Count == 0)
            {
                return
                [
                    new VideoSource
                    {
                        Url             = masterUrl,
                        Quality         = "Auto",
                        DurationSeconds = ParseSegmentDuration(masterContent),
                        Type            = VideoType.M3U8,
                        Hoster          = hoster,
                        Group           = video.FansubName,
                        Headers         = headers
                    }
                ];
            }

            var orderedVariants = variants
                                  .OrderByDescending(v => v.Height)
                                  .ThenByDescending(v => v.Bitrate ?? 0)
                                  .ToList();

            var topVariant = orderedVariants[0];
            var durationSeconds = await TryGetVariantDurationAsync(topVariant.Url, widCookie, cancellationToken)
                                      .ConfigureAwait(false);

            var list = new List<VideoSource>(orderedVariants.Count + 1)
            {
                new()
                {
                    Url             = masterUrl,
                    Quality         = "Auto",
                    Bitrate         = topVariant.Bitrate,
                    Width           = topVariant.Width > 0 ? topVariant.Width : null,
                    Height          = topVariant.Height > 0 ? topVariant.Height : null,
                    VideoCodec      = topVariant.VideoCodec,
                    AudioCodec      = topVariant.AudioCodec,
                    DurationSeconds = durationSeconds,
                    Type            = VideoType.M3U8,
                    Hoster          = hoster,
                    Group           = video.FansubName,
                    Headers         = headers
                }
            };

            foreach (var variant in orderedVariants)
            {
                list.Add(new VideoSource
                {
                    Url             = variant.Url,
                    Quality         = variant.Quality,
                    Bitrate         = variant.Bitrate,
                    Width           = variant.Width > 0 ? variant.Width : null,
                    Height          = variant.Height > 0 ? variant.Height : null,
                    VideoCodec      = variant.VideoCodec,
                    AudioCodec      = variant.AudioCodec,
                    DurationSeconds = durationSeconds,
                    Type            = VideoType.M3U8,
                    Hoster          = hoster,
                    Group           = video.FansubName,
                    Headers         = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase)
                });
            }

            return list;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to parse AitrVip master playlist: {Url}", masterUrl);
            return
            [
                new VideoSource
                {
                    Url     = masterUrl,
                    Quality = "Auto",
                    Type    = VideoType.M3U8,
                    Hoster  = hoster,
                    Group   = video.FansubName,
                    Headers = headers
                }
            ];
        }
    }

    private async Task<double?> TryGetVariantDurationAsync(string variantUrl,
        string?                                                   widCookie,
        CancellationToken                                         cancellationToken)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, variantUrl);
            if (!string.IsNullOrWhiteSpace(widCookie))
            {
                req.Headers.TryAddWithoutValidation("Cookie", $"wid={widCookie}");
            }

            using var resp = await _httpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            var content = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ParseSegmentDuration(content);
        }
        catch
        {
            return null;
        }
    }

    private Dictionary<string, string> BuildAitrHeaders(string? widCookie)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Referer"] = $"{BaseUrl}/"
        };

        if (!string.IsNullOrWhiteSpace(widCookie))
        {
            headers["Cookie"] = $"wid={widCookie}";
        }

        return headers;
    }

    private static string? ExtractWidCookie(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            return null;
        }

        foreach (var header in setCookies)
        {
            var match = WidCookieRegex().Match(header);
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
        }

        return null;
    }

    private async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogDebug("GET {Url} failed with status {StatusCode}", url, response.StatusCode);
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        return JsonDocument.Parse(json);
    }

    private static string NormalizeEmbedUrl(string embedUrl)
    {
        embedUrl = embedUrl.Trim();
        if (embedUrl.StartsWith("//"))
        {
            embedUrl = "https:" + embedUrl;
        }

        if (embedUrl.Contains("embed2/?id=", StringComparison.OrdinalIgnoreCase))
        {
            var idIdx = embedUrl.IndexOf("id=", StringComparison.OrdinalIgnoreCase);
            if (idIdx >= 0)
            {
                embedUrl = embedUrl[(idIdx + 3)..].Trim();
                if (embedUrl.StartsWith("//"))
                {
                    embedUrl = "https:" + embedUrl;
                }
            }
        }

        return embedUrl;
    }

    private static (string? Primary, string? Romaji, string? English) ParseTitles(JsonElement container)
    {
        if (!container.TryGetProperty("title", out var titleEl))
        {
            return (null, null, null);
        }

        if (titleEl.ValueKind == JsonValueKind.String)
        {
            return (titleEl.GetString()?.Trim(), null, null);
        }

        if (titleEl.ValueKind != JsonValueKind.Object)
        {
            return (null, null, null);
        }

        return (TryGetString(titleEl, "primary"),
                TryGetString(titleEl, "romaji"),
                TryGetString(titleEl, "english"));
    }

    private static (string? AniListId, string? MyAnimeListId, string? TmdbId) ParseExternalLinks(JsonElement root)
    {
        string? anilistId = null;
        string? malId     = null;
        string? tmdbId    = null;

        if (!root.TryGetProperty("externalLinks", out var linksEl) || linksEl.ValueKind != JsonValueKind.Array)
        {
            return (null, null, null);
        }

        foreach (var link in linksEl.EnumerateArray())
        {
            var provider = TryGetString(link, "provider");
            var url      = TryGetString(link, "url");
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            if (string.Equals(provider, "aniList", StringComparison.OrdinalIgnoreCase))
            {
                var m = AniListIdRegex().Match(url);
                if (m.Success)
                {
                    anilistId ??= m.Groups[1].Value;
                }
            }
            else if (string.Equals(provider, "myAnimeList", StringComparison.OrdinalIgnoreCase))
            {
                var m = MalIdRegex().Match(url);
                if (m.Success)
                {
                    malId ??= m.Groups[1].Value;
                }
            }
            else if (string.Equals(provider, "tmdb", StringComparison.OrdinalIgnoreCase))
            {
                var m = TmdbIdRegex().Match(url);
                if (m.Success)
                {
                    tmdbId ??= m.Groups[1].Value;
                }
            }
        }

        return (anilistId, malId, tmdbId);
    }

    private static ContentFormat MapContentFormat(string? typeStr, string? title)
    {
        if (!string.IsNullOrWhiteSpace(typeStr))
        {
            switch (typeStr.Trim().ToUpperInvariant())
            {
                case "MOVIE":
                    return ContentFormat.Movie;
                case "OVA":
                    return ContentFormat.Ova;
                case "SPECIAL":
                case "TV_SPECIAL":
                    return ContentFormat.Special;
                case "TV":
                case "ONA":
                    return ContentFormat.Tv;
            }
        }

        return AnimeDetails.IsMovieTitle(title) ? ContentFormat.Movie : ContentFormat.Tv;
    }

    private static string FormatEpisodeTitle(double epNum, EpisodeSummary ep)
    {
        var numStr = epNum % 1 == 0
                         ? ((int) epNum).ToString(CultureInfo.InvariantCulture)
                         : epNum.ToString("0.#", CultureInfo.InvariantCulture);

        var label = !string.IsNullOrWhiteSpace(ep.Designation)
                        ? $"{ep.Designation}. Bölüm"
                        : $"{numStr}. Bölüm";

        return !string.IsNullOrWhiteSpace(ep.SubTitle)
                   ? $"{label} - {ep.SubTitle}"
                   : label;
    }

    private static double? TryParseEpisodeNumber(string? title, string slug)
    {
        if (!string.IsNullOrWhiteSpace(title))
        {
            var match = BolumTextRegex().Match(title);
            if (match.Success
                && double.TryParse(match.Groups[1].Value.Replace(',', '.'),
                                   NumberStyles.Any,
                                   CultureInfo.InvariantCulture,
                                   out var parsed))
            {
                return parsed;
            }
        }

        var hrefMatch = BolumHrefRegex().Match(slug);
        if (hrefMatch.Success
            && double.TryParse(hrefMatch.Groups[1].Value.Replace(',', '.'),
                               NumberStyles.Any,
                               CultureInfo.InvariantCulture,
                               out var parsedSlug))
        {
            return parsedSlug;
        }

        return null;
    }

    private static string StripHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var text = BlockTagBreakRegex().Replace(html, "\n");
        text = HtmlTagRegex().Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text);
        text = MultiNewlineRegex().Replace(text, "\n\n");
        return text.Trim();
    }

    private static List<HlsVariant> ParseMasterPlaylist(string content, string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
        {
            return [];
        }

        var variants = new List<HlsVariant>();
        var lines    = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < lines.Length - 1; i++)
        {
            var line = lines[i].Trim();
            if (!line.StartsWith("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var targetLine = lines[i + 1].Trim();
            if (string.IsNullOrEmpty(targetLine) || targetLine.StartsWith('#'))
            {
                continue;
            }

            var quality  = "Auto";
            var width    = 0;
            var height   = 0;
            var resMatch = ResolutionRegex().Match(line);
            if (resMatch.Success)
            {
                var parts = resMatch.Groups[1].Value.Split('x');
                if (parts.Length == 2
                    && int.TryParse(parts[0], out var w)
                    && int.TryParse(parts[1], out var h))
                {
                    width   = w;
                    height  = h;
                    quality = $"{h}p";
                }
            }

            var bitrate = ParseBandwidth(line);
            var (videoCodec, audioCodec) = ParseCodecs(line);

            var variantUrl = Uri.TryCreate(baseUri, targetLine, out var combined)
                                 ? combined.AbsoluteUri
                                 : targetLine;

            variants.Add(new HlsVariant(quality, width, height, bitrate, videoCodec, audioCodec, variantUrl));
            i++;
        }

        return variants;
    }

    private static long? ParseBandwidth(string streamInfLine)
    {
        var avg = AverageBandwidthRegex().Match(streamInfLine);
        if (avg.Success && long.TryParse(avg.Groups[1].Value, out var avgBits) && avgBits is > 0 and < 1_000_000_000)
        {
            return avgBits;
        }

        var peak = BandwidthRegex().Match(streamInfLine);
        if (peak.Success
            && long.TryParse(peak.Groups[1].Value, out var peakBits)
            && peakBits is > 0 and < 1_000_000_000)
        {
            return peakBits;
        }

        return null;
    }

    private static (string? VideoCodec, string? AudioCodec) ParseCodecs(string streamInfLine)
    {
        var match = CodecsRegex().Match(streamInfLine);
        if (!match.Success)
        {
            return (null, null);
        }

        string? video = null;
        string? audio = null;

        var parts = match.Groups[1]
                         .Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            var tag = part.Split('.')[0].Trim().ToLowerInvariant();
            switch (tag)
            {
                case "avc1":
                    video ??= "H.264";
                    break;
                case "hev1":
                case "hvc1":
                    video ??= "H.265";
                    break;
                case "vp09":
                    video ??= "VP9";
                    break;
                case "av01":
                    video ??= "AV1";
                    break;
                case "mp4a":
                    audio ??= "AAC";
                    break;
                case "ec-3":
                case "ec3":
                    audio ??= "E-AC-3";
                    break;
                case "ac-3":
                case "ac3":
                    audio ??= "AC-3";
                    break;
                case "opus":
                    audio ??= "Opus";
                    break;
            }
        }

        return (video, audio);
    }

    private static double? ParseSegmentDuration(string playlistContent)
    {
        double total = 0;
        var    count = 0;

        foreach (var rawLine in playlistContent.Split('\n'))
        {
            if (count >= 10000)
            {
                break;
            }

            var line = rawLine.Trim();
            if (!line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var commaIdx = line.IndexOf(',');
            var numPart  = commaIdx > 8 ? line[8..commaIdx] : line[8..];
            if (double.TryParse(numPart.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var segSeconds)
                && segSeconds > 0)
            {
                total += segSeconds;
                count++;
            }
        }

        return count > 0 ? total : null;
    }

    private static string? TryGetString(JsonElement obj, string prop)
    {
        return obj.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.String
                   ? el.GetString()?.Trim()
                   : null;
    }

    private static int? TryGetInt(JsonElement obj, string prop)
    {
        if (!obj.TryGetProperty(prop, out var el))
        {
            return null;
        }

        return el.ValueKind switch
        {
            JsonValueKind.Number when el.TryGetInt32(out var n) => n,
            JsonValueKind.String when int.TryParse(el.GetString(),
                                                   NumberStyles.Integer,
                                                   CultureInfo.InvariantCulture,
                                                   out var n) => n,
            _ => null
        };
    }

    private static double? TryGetDouble(JsonElement obj, string prop)
    {
        if (!obj.TryGetProperty(prop, out var el))
        {
            return null;
        }

        return el.ValueKind switch
        {
            JsonValueKind.Number when el.TryGetDouble(out var d) => d,
            JsonValueKind.String when double.TryParse(el.GetString(),
                                                      NumberStyles.Any,
                                                      CultureInfo.InvariantCulture,
                                                      out var d) => d,
            _ => null
        };
    }

    private static bool? TryGetBool(JsonElement obj, string prop)
    {
        if (!obj.TryGetProperty(prop, out var el))
        {
            return null;
        }

        return el.ValueKind switch
        {
            JsonValueKind.True  => true,
            JsonValueKind.False => false,
            _                   => null
        };
    }

    [GeneratedRegex(@"(?:^|[,\s])wid=([^;\s]+)", RegexOptions.IgnoreCase)]
    private static partial Regex WidCookieRegex();

    [GeneratedRegex(@"anilist\.co/anime/(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex AniListIdRegex();

    [GeneratedRegex(@"myanimelist\.net/anime/(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex MalIdRegex();

    [GeneratedRegex(@"themoviedb\.org/(?:tv|movie)/(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex TmdbIdRegex();

    [GeneratedRegex(@"(\d+(?:[\.,]\d+)?)\.\s*Bölüm", RegexOptions.IgnoreCase)]
    private static partial Regex BolumTextRegex();

    [GeneratedRegex(@"(\d+(?:[\.,]\d+)?)-bolum", RegexOptions.IgnoreCase)]
    private static partial Regex BolumHrefRegex();

    [GeneratedRegex(@"</?(?:p|div|br|li|ul|ol|h[1-6]|article)[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockTagBreakRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex MultiNewlineRegex();

    [GeneratedRegex(@"RESOLUTION=(\d+x\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ResolutionRegex();

    [GeneratedRegex(@"AVERAGE-BANDWIDTH=(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex AverageBandwidthRegex();

    [GeneratedRegex(@"(?<!-)BANDWIDTH=(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex BandwidthRegex();

    [GeneratedRegex(@"CODECS=""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex CodecsRegex();

    private sealed record EpisodeSummary(
        string  Slug,
        double? Number,
        string  Title,
        int?    SeasonNumber,
        string? Designation,
        string? SubTitle);

    private sealed record EpisodeVideo(string Id, string? FansubName, string? ProviderName, bool Hls);

    private sealed record EpisodePayload(List<EpisodeVideo> Videos);

    private sealed record WatchResolution(string? Url, string? WidCookie, bool RateLimited);

    private sealed record HlsVariant(
        string  Quality,
        int     Width,
        int     Height,
        long?   Bitrate,
        string? VideoCodec,
        string? AudioCodec,
        string  Url);
}
