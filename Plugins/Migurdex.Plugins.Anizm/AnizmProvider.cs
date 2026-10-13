using AngleSharp.Html.Parser;
using Microsoft.Extensions.Logging;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Migurdex.Plugins.Anizm;

public partial class AnizmProvider : IAnimeProvider
{
    private readonly HttpClient             _httpClient;
    private readonly ILogger<AnizmProvider> _logger;

    private sealed record V3TranslatorInfo(string SourcesUrl, string Name, bool IsActive);

    public AnizmProvider(ISharedBridge bridge, ILogger<AnizmProvider> logger)
    {
        _httpClient = bridge.CreateHttpClient(x =>
        {
            x.AllowAutoRedirect = true;
        });
        _logger = logger;

        _httpClient.DefaultRequestHeaders.Add("Referer", BaseUrl);
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
        _httpClient.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
    }

    public string       Name    => "Anizm";
    public string       BaseUrl => "https://anizm.net";
    public ProviderType Type    => ProviderType.Anime;

    public async Task<List<SearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        try
        {
            var encoded = Uri.EscapeDataString(query);
            var url =
                $"{BaseUrl}/searchAnime?query={encoded}&page=1&type=detailed&limit=10&priorityField=info_title&orderBy=info_year&orderDirection=ASC";

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

            using var doc  = JsonDocument.Parse(json);
            var       list = new List<SearchResult>();

            if (doc.RootElement.TryGetProperty("data", out var dataArray))
            {
                foreach (var item in dataArray.EnumerateArray())
                {
                    var title        = item.GetProperty("info_title").GetString() ?? "";
                    var englishTitle = item.TryGetProperty("info_titleenglish", out var eng) ? eng.GetString() : null;
                    var originalTitle =
                        item.TryGetProperty("info_titleoriginal", out var orig) ? orig.GetString() : null;
                    var otherNames = item.TryGetProperty("info_othernames", out var oth) ? oth.GetString() : null;

                    string? romajiTitle   = null;
                    string? japaneseTitle = null;

                    if (!string.IsNullOrWhiteSpace(originalTitle))
                    {
                        if (originalTitle.Any(c => c >= 0x3040 && c <= 0x9FAF))
                        {
                            japaneseTitle = originalTitle;
                        }
                        else
                        {
                            romajiTitle = originalTitle;
                        }
                    }

                    var altTitles = new List<string>();
                    if (!string.IsNullOrWhiteSpace(otherNames))
                    {
                        altTitles.AddRange(otherNames.Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries)
                                                     .Select(x => x.Trim()));
                    }

                    var slug = item.GetProperty("info_slug").GetString() ?? "";

                    var hasMovieEpisode = false;
                    if (item.TryGetProperty("lastEpisode", out var lastEpProp)
                        && lastEpProp.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var ep in lastEpProp.EnumerateArray())
                        {
                            var epTitle = ep.TryGetProperty("episode_title", out var et) ? et.GetString() ?? "" : "";
                            var epSlug  = ep.TryGetProperty("episode_slug", out var es) ? es.GetString() ?? "" : "";

                            if (epTitle.Equals("Movie", StringComparison.OrdinalIgnoreCase)
                                || epTitle.Equals("Film", StringComparison.OrdinalIgnoreCase)
                                || epSlug.EndsWith("-movie", StringComparison.OrdinalIgnoreCase)
                                || epSlug.EndsWith("-film", StringComparison.OrdinalIgnoreCase))
                            {
                                hasMovieEpisode = true;
                                break;
                            }
                        }
                    }

                    var isMovie = hasMovieEpisode
                                  || slug.Contains("-movie", StringComparison.OrdinalIgnoreCase)
                                  || slug.Contains("-film", StringComparison.OrdinalIgnoreCase)
                                  || AnimeDetails.IsMovieTitle(title)
                                  || AnimeDetails.IsMovieTitle(englishTitle)
                                  || AnimeDetails.IsMovieTitle(originalTitle)
                                  || AnimeDetails.IsMovieTitle(romajiTitle);

                    var result = new SearchResult
                    {
                        Id            = slug,
                        Title         = title,
                        EnglishTitle  = englishTitle,
                        RomajiTitle   = romajiTitle,
                        JapaneseTitle = japaneseTitle,
                        AlternativeTitles = altTitles.Where(t => !t.Equals(title, StringComparison.OrdinalIgnoreCase)
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
                                                     .ToList(),
                        PosterUrl    = $"{BaseUrl}/storage/pcovers/{item.GetProperty("info_poster").GetString()}",
                        Url          = $"{BaseUrl}/{slug}",
                        ProviderName = Name,
                        Type         = ProviderType.Anime,
                        Format       = isMovie ? ContentFormat.Movie : ContentFormat.Tv,
                        Year         = item.TryGetProperty("info_year", out var yr) ? yr.GetString() : null,
                        Score = item.TryGetProperty("info_malpoint", out var sc) && sc.ValueKind == JsonValueKind.Number
                                    ? sc.GetDouble()
                                    : null
                    };

                    if (item.TryGetProperty("categories", out var cats))
                    {
                        result.Categories = cats.EnumerateArray()
                                                .Select(c => c.GetProperty("name").GetString() ?? "")
                                                .ToList();
                    }

                    list.Add(result);
                }
            }

            return list;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "search failed");

            return [];
        }
    }

    public async Task<AnimeDetails> GetDetailsAsync(string animeId, CancellationToken cancellationToken = default)
    {
        var html = await _httpClient.GetStringAsync($"{BaseUrl}/{animeId}", cancellationToken);

        if (TryParseV3DetailsFast(html, animeId, out var fastDetails) && fastDetails is not null)
        {
            return fastDetails;
        }

        var parser   = new HtmlParser();
        var document = await parser.ParseDocumentAsync(html);

        var breadcrumbTitle = document.QuerySelector(".v3-breadcrumb [aria-current='page']")?.TextContent.Trim();
        var h1Raw           = document.QuerySelector("h1")?.TextContent.Trim() ?? animeId;
        var h1Clean         = Regex.Replace(h1Raw, @"\s+", " ").Trim();

        h1Clean = Regex.Replace(h1Clean, @"\s+izle\s*$", "", RegexOptions.IgnoreCase).Trim();

        var title = !string.IsNullOrWhiteSpace(breadcrumbTitle) ? breadcrumbTitle : h1Clean;
        if (string.IsNullOrWhiteSpace(title))
        {
            title = animeId;
        }

        string? japaneseTitle = null;
        string? englishTitle  = null;
        var     altTitles     = new List<string>();

        var factRows = document.QuerySelectorAll(".v3-anime-facts > div");
        foreach (var row in factRows)
        {
            var label = row.QuerySelector("span")?.TextContent.Trim() ?? "";
            var value = row.QuerySelector("strong")?.TextContent.Trim() ?? "";

            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (label.Contains("Japonca", StringComparison.OrdinalIgnoreCase))
            {
                japaneseTitle = value;
            }
            else if (label.Contains("İngilizce", StringComparison.OrdinalIgnoreCase)
                     || label.Contains("Ingilizce", StringComparison.OrdinalIgnoreCase))
            {
                englishTitle = value;
            }
            else if (label.Contains("Diğer", StringComparison.OrdinalIgnoreCase))
            {
                altTitles.AddRange(value.Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries)
                                        .Select(x => x.Trim()));
            }
        }

        var dataRows = document.QuerySelectorAll(".infoExtraData ul.dataRows li.dataRow, ul.dataRows li.dataRow");
        foreach (var row in dataRows)
        {
            var dataTitle = row.QuerySelector(".dataTitle")?.TextContent.Trim() ?? "";
            var dataValue = row.QuerySelector(".dataValue")?.TextContent.Trim() ?? "";

            if (string.IsNullOrWhiteSpace(dataValue))
            {
                continue;
            }

            if (dataTitle.Contains("Japonca", StringComparison.OrdinalIgnoreCase))
            {
                japaneseTitle ??= dataValue;
            }
            else if (dataTitle.Contains("İngilizce", StringComparison.OrdinalIgnoreCase))
            {
                englishTitle ??= dataValue;
            }
            else if (dataTitle.Contains("Diğer", StringComparison.OrdinalIgnoreCase))
            {
                altTitles.AddRange(dataValue.Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries)
                                            .Select(x => x.Trim()));
            }
        }

        var poster = NormalizePosterUrl(document.QuerySelector("meta[property='og:image']")?.GetAttribute("content")
                                        ?? document.QuerySelector(".v3-anime-detail-cover img")?.GetAttribute("src")
                                        ?? document.QuerySelector(
                                                       ".infoPoster img, .posterImg img, .anizm_boxPoster img, img[src*='pcovers']")
                                                   ?.GetAttribute("src"));

        var summaryRaw = document.QuerySelector("#v3-anime-summary-content")?.TextContent
                         ?? document.QuerySelector(".v3-anime-summary p")?.TextContent
                         ?? document.QuerySelector(".anizm_boxContent .infoDesc, .infoDesc")?.TextContent
                         ?? "";

        var details = new AnimeDetails
        {
            Title         = title,
            EnglishTitle  = englishTitle,
            JapaneseTitle = japaneseTitle,
            PosterUrl     = poster,
            AlternativeTitles = altTitles.Where(t => !t.Equals(title, StringComparison.OrdinalIgnoreCase)
                                                     && (englishTitle == null
                                                         || !t.Equals(englishTitle, StringComparison.OrdinalIgnoreCase))
                                                     && (japaneseTitle == null
                                                         || !t.Equals(japaneseTitle,
                                                                      StringComparison.OrdinalIgnoreCase)))
                                         .Distinct()
                                         .ToList(),
            Summary = CleanSummary(summaryRaw)
        };

        var isMovie = details.Title.Contains("Movie", StringComparison.OrdinalIgnoreCase)
                      || animeId.Contains("movie", StringComparison.OrdinalIgnoreCase)
                      || animeId.Contains("film", StringComparison.OrdinalIgnoreCase)
                      || AnimeDetails.IsMovieTitle(details.Title)
                      || AnimeDetails.IsMovieTitle(details.EnglishTitle)
                      || AnimeDetails.IsMovieTitle(details.JapaneseTitle);
        var isOva     = details.Title.Contains("OVA", StringComparison.OrdinalIgnoreCase);
        var isSpecial = details.Title.Contains("Special", StringComparison.OrdinalIgnoreCase);

        var seasonNum = isMovie ? 1 : AnimeDetails.ParseSeasonNumber(details.Title);

        if (isMovie)
        {
            details.SeasonMappings.Add(new SeasonMapping
            {
                SeasonNumber = 1
            });
        }
        else
        {
            details.SeasonMappings.Add(new SeasonMapping
            {
                SeasonNumber = seasonNum
            });
        }

        var v3Episodes = document.QuerySelectorAll("a[data-episode-number]");
        if (v3Episodes.Length > 0)
        {
            foreach (var element in v3Episodes)
            {
                var href = element.GetAttribute("href") ?? "";
                if (string.IsNullOrEmpty(href))
                {
                    continue;
                }

                var episodeId = href.Split('/').LastOrDefault()?.Replace("-izle", "") ?? "";
                if (string.IsNullOrEmpty(episodeId) || details.Episodes.Any(e => e.Id == episodeId))
                {
                    continue;
                }

                var numAttr   = element.GetAttribute("data-episode-number") ?? "";
                var titleAttr = element.GetAttribute("data-episode-title") ?? "";
                var epText    = Regex.Replace(element.TextContent.Trim(), @"\s+", " ").Trim();

                var num = ResolveV3EpisodeNumber(numAttr,
                                                 string.IsNullOrWhiteSpace(titleAttr) ? epText : titleAttr,
                                                 href);

                var epTitle = !string.IsNullOrWhiteSpace(titleAttr) ? titleAttr.Trim() : epText;
                if (string.IsNullOrWhiteSpace(epTitle))
                {
                    epTitle = $"{num}. Bölüm";
                }

                var finalNum = isMovie
                                   ? 1
                                   : num == 0
                                       ? 1
                                       : (int) num;

                if (!details.Episodes.Any(e => e.Number == finalNum))
                {
                    details.Episodes.Add(new Episode
                    {
                        Id     = episodeId,
                        Title  = isMovie ? "Film" : epTitle,
                        Number = finalNum,
                        Season = isMovie ? 1 : seasonNum
                    });
                }
            }
        }
        else
        {
            var episodeElements = document.QuerySelectorAll(".episodeListTabContent a, .bolumKutucugu a");
            if (episodeElements.Length == 0)
            {
                episodeElements =
                    document.QuerySelectorAll("a[href*='bolum'], a[href*='movie-izle'], a[href*='/izle/']");
            }

            foreach (var element in episodeElements)
            {
                var href = element.GetAttribute("href") ?? "";

                if (string.IsNullOrEmpty(href))
                {
                    continue;
                }

                var epTitle = element.QuerySelector(".episodeBlock")?.TextContent.Trim() ?? element.TextContent.Trim();
                var episodeId = href.Split('/').LastOrDefault()?.Replace("-izle", "") ?? "";

                if (!string.IsNullOrEmpty(episodeId) && details.Episodes.All(e => e.Id != episodeId))
                {
                    var parsedNumbers = ParseEpisodeNumbers(epTitle);
                    foreach (var num in parsedNumbers)
                    {
                        var finalNum = isMovie
                                           ? 1
                                           : num == 0
                                               ? 1
                                               : (int) num;
                        if (!details.Episodes.Any(e => e.Number == finalNum))
                        {
                            details.Episodes.Add(new Episode
                            {
                                Id = episodeId,
                                Title = isMovie
                                            ? "Film"
                                            : parsedNumbers.Count > 1
                                                ? $"{finalNum}. Bölüm ({title})"
                                                : title,
                                Number = finalNum,
                                Season = isMovie ? 1 : seasonNum
                            });
                        }
                    }
                }
            }
        }

        if (!isMovie && details.Episodes.Count == 1)
        {
            var singleEp = details.Episodes[0];
            if (singleEp.Id.EndsWith("-movie", StringComparison.OrdinalIgnoreCase)
                || singleEp.Id.EndsWith("-film", StringComparison.OrdinalIgnoreCase)
                || singleEp.Title.Equals("Movie", StringComparison.OrdinalIgnoreCase)
                || singleEp.Title.Equals("Film", StringComparison.OrdinalIgnoreCase)
                || AnimeDetails.IsMovieSummary(details.Summary))
            {
                isMovie         = true;
                singleEp.Title  = "Film";
                singleEp.Number = 1;
                singleEp.Season = 1;
            }
        }

        details.Format = isMovie
                             ? ContentFormat.Movie
                             : isOva
                                 ? ContentFormat.Ova
                                 : isSpecial
                                     ? ContentFormat.Special
                                     : ContentFormat.Tv;
        details.Episodes = details.Episodes.OrderBy(e => e.Number).ToList();
        details.Normalize();

        return details;
    }

    public async Task<List<string>> GetGroupsAsync(string episodeId, CancellationToken cancellationToken = default)
    {
        try
        {
            var watchUrl = $"{BaseUrl}/{episodeId}-izle";
            var html     = await _httpClient.GetStringAsync(watchUrl, cancellationToken);

            var parser   = new HtmlParser();
            var document = await parser.ParseDocumentAsync(html);
            var groups   = new List<string>();

            var translatorButtons = document.QuerySelectorAll("button[data-v3-translator]");
            foreach (var btn in translatorButtons)
            {
                var name = NormalizeFansubName(
                    btn.GetAttribute("title") ?? btn.TextContent);

                if (!groups.Contains(name))
                {
                    groups.Add(name);
                }
            }

            if (groups.Count > 0)
            {
                return groups;
            }

            var matches = FansubNameRegex().Matches(html);
            foreach (Match m in matches)
            {
                var name = NormalizeFansubName(m.Groups[1].Value);

                if (!groups.Contains(name))
                {
                    groups.Add(name);
                }
            }

            return groups;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "failed to get groups for episode: {Id}", episodeId);

            return [];
        }
    }

    public async Task<List<VideoSource>> GetVideoSourcesAsync(string episodeId,
        string?                                                      group             = null,
        CancellationToken                                            cancellationToken = default)
    {
        try
        {
            var watchUrl = $"{BaseUrl}/{episodeId}-izle";
            var html     = await _httpClient.GetStringAsync(watchUrl, cancellationToken);
            var sources  = new List<VideoSource>();

            var parser   = new HtmlParser();
            var document = await parser.ParseDocumentAsync(html);

            var csrfToken = document.QuerySelector("meta[name='csrf-token']")?.GetAttribute("content");

            var translatorInfos   = new List<V3TranslatorInfo>();
            var translatorButtons = document.QuerySelectorAll("button[data-v3-translator]");
            foreach (var btn in translatorButtons)
            {
                var sourcesUrl = btn.GetAttribute("data-sources") ?? "";
                if (string.IsNullOrWhiteSpace(sourcesUrl))
                {
                    continue;
                }

                translatorInfos.Add(new V3TranslatorInfo(
                                        NormalizeUrl(sourcesUrl),
                                        NormalizeFansubName(btn.GetAttribute("title") ?? btn.TextContent),
                                        btn.GetAttribute("aria-pressed")
                                           ?.Equals("true", StringComparison.OrdinalIgnoreCase)
                                        == true));
            }

            var preResolved = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var activeInfo  = translatorInfos.FirstOrDefault(t => t.IsActive);
            if (activeInfo is not null)
            {
                var activeBolum = BolumIdRegex().Match(activeInfo.SourcesUrl) is { Success: true } ab
                                      ? ab.Groups[1].Value
                                      : null;

                preResolved[activeInfo.SourcesUrl] = document.QuerySelectorAll("button[data-video][data-url]")
                                                             .Select(b => NormalizeUrl(
                                                                         b.GetAttribute("data-url") ?? ""))
                                                             .Where(u => !string.IsNullOrEmpty(u)
                                                                         && BelongsToBolum(u, activeBolum))
                                                             .Distinct(StringComparer.OrdinalIgnoreCase)
                                                             .ToList();

                if (preResolved[activeInfo.SourcesUrl].Count == 0)
                {
                    preResolved.Remove(activeInfo.SourcesUrl);
                }
            }

            var translatorMap = new Dictionary<string, string?>();
            foreach (var info in translatorInfos)
            {
                if (string.IsNullOrEmpty(group) || info.Name.Equals(group, StringComparison.OrdinalIgnoreCase))
                {
                    translatorMap[info.SourcesUrl] = info.Name;
                }
            }

            if (translatorMap.Count > 0)
            {
                return await ResolveV3TranslatorsAsync(
                           translatorMap,
                           preResolved,
                           watchUrl,
                           csrfToken,
                           cancellationToken);
            }

            var legacyMap = new Dictionary<string, string?>();
            var matches   = TranslatorRegex().Matches(html);
            foreach (Match m in matches)
            {
                var url  = m.Groups[1].Value;
                var name = NormalizeFansubName(m.Groups[2].Value);

                if (string.IsNullOrEmpty(group) || name.Equals(group, StringComparison.OrdinalIgnoreCase))
                {
                    legacyMap[url] = name;
                }
            }

            if (legacyMap.Count == 0)
            {
                var embedded = document.QuerySelectorAll("button[data-video][data-url]");
                foreach (var btn in embedded)
                {
                    var dataUrl = btn.GetAttribute("data-url") ?? "";
                    if (string.IsNullOrWhiteSpace(dataUrl))
                    {
                        continue;
                    }

                    if (!string.IsNullOrEmpty(group) && !group.Equals("Bağımsız", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    legacyMap[NormalizeUrl(dataUrl)] = "Bağımsız";
                }

                if (legacyMap.Count > 0)
                {
                    return await ResolveDirectVideoUrlsAsync(legacyMap.Values.First() ?? "Bağımsız",
                                                             legacyMap.Keys.ToList(),
                                                             watchUrl,
                                                             csrfToken,
                                                             cancellationToken);
                }

                return sources;
            }

            var batchTasks = legacyMap.Select(async pair =>
            {
                var tUrl      = pair.Key;
                var groupName = pair.Value;

                try
                {
                    var response = await SendV3JsonAsync(tUrl, watchUrl, csrfToken, cancellationToken);
                    if (response == null)
                    {
                        return;
                    }

                    var body = await response.Content.ReadAsStringAsync(cancellationToken);
                    if (string.IsNullOrEmpty(body))
                    {
                        return;
                    }

                    using var doc       = JsonDocument.Parse(body);
                    var       foundUrls = new List<string>();

                    if (doc.RootElement.TryGetProperty("data", out var data))
                    {
                        foreach (Match m in VideoAttrRegex().Matches(data.GetString() ?? ""))
                        {
                            foundUrls.Add(m.Groups[1].Value);
                        }
                    }

                    if (doc.RootElement.TryGetProperty("player", out var player))
                    {
                        var srcMatch = IframeSrcRegex().Match(player.GetString() ?? "");
                        if (srcMatch.Success)
                        {
                            foundUrls.Add(srcMatch.Groups[1].Value);
                        }
                    }

                    await ResolveAndAddAsync(foundUrls, groupName, watchUrl, sources, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "batch video parse failed on URL: {Url}", tUrl);
                }
            });

            await Task.WhenAll(batchTasks);

            return sources.GroupBy(x => x.Url).Select(x => x.First()).ToList();

            bool BelongsToBolum(string url, string? bolumId)
            {
                if (bolumId is null)
                {
                    return true;
                }

                var m = BolumIdRegex().Match(url);

                return m.Success && m.Groups[1].Value == bolumId;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "getVideoSourcesAsync failed");

            return [];
        }
    }

    private async Task<List<VideoSource>> ResolveV3TranslatorsAsync(
        Dictionary<string, string?>       translatorMap,
        Dictionary<string, List<string>>? preResolvedVideoUrls,
        string                            watchUrl,
        string?                           csrfToken,
        CancellationToken                 cancellationToken)
    {
        var sources = new List<VideoSource>();

        var batchTasks = translatorMap.Select(async pair =>
        {
            var sourcesUrl = pair.Key;
            var groupName  = pair.Value ?? "Bağımsız";

            try
            {
                List<string> videoUrls;
                if (preResolvedVideoUrls is not null
                    && preResolvedVideoUrls.TryGetValue(sourcesUrl, out var embedded)
                    && embedded.Count > 0)
                {
                    _logger.LogDebug("reusing {Count} embedded video urls for group {Group}",
                                     embedded.Count,
                                     groupName);

                    videoUrls = embedded;
                }
                else
                {
                    videoUrls = await FetchTranslatorVideoUrlsAsync(sourcesUrl,
                                                                    watchUrl,
                                                                    csrfToken,
                                                                    cancellationToken);
                }

                await ResolveDirectVideoUrlsIntoAsync(groupName,
                                                      videoUrls,
                                                      watchUrl,
                                                      csrfToken,
                                                      sources,
                                                      cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "v3 translator parse failed on URL: {Url}", sourcesUrl);
            }
        });

        await Task.WhenAll(batchTasks);

        return sources.GroupBy(x => x.Url).Select(x => x.First()).ToList();
    }

    private async Task<List<string>> FetchTranslatorVideoUrlsAsync(
        string            sourcesUrl,
        string            watchUrl,
        string?           csrfToken,
        CancellationToken cancellationToken)
    {
        var videoUrls = new List<string>();

        var response = await SendV3JsonAsync(sourcesUrl, watchUrl, csrfToken, cancellationToken);
        if (response == null || !response.IsSuccessStatusCode)
        {
            return videoUrls;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrEmpty(body))
        {
            return videoUrls;
        }

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("videos", out var videos))
        {
            return videoUrls;
        }

        foreach (var v in videos.EnumerateArray())
        {
            string? vid = null;
            if (v.TryGetProperty("id", out var idProp))
            {
                vid = idProp.ValueKind switch
                {
                    JsonValueKind.Number => idProp.GetInt64().ToString(CultureInfo.InvariantCulture),
                    JsonValueKind.String => idProp.GetString(),
                    _                    => null
                };
            }

            if (string.IsNullOrWhiteSpace(vid))
            {
                continue;
            }

            var videoUrl = CevirmenSuffixRegex().Replace(sourcesUrl, $"/video/{vid}");
            if (videoUrl == sourcesUrl)
            {
                videoUrl = sourcesUrl.TrimEnd('/') + $"/video/{vid}";
            }

            videoUrls.Add(videoUrl);
        }

        return videoUrls;
    }

    private async Task<List<VideoSource>> ResolveDirectVideoUrlsAsync(
        string            groupName,
        List<string>      videoUrls,
        string            watchUrl,
        string?           csrfToken,
        CancellationToken cancellationToken)
    {
        var sources = new List<VideoSource>();
        await ResolveDirectVideoUrlsIntoAsync(groupName, videoUrls, watchUrl, csrfToken, sources, cancellationToken);

        return sources.GroupBy(x => x.Url).Select(x => x.First()).ToList();
    }

    private async Task ResolveDirectVideoUrlsIntoAsync(
        string            groupName,
        List<string>      videoUrls,
        string            watchUrl,
        string?           csrfToken,
        List<VideoSource> sources,
        CancellationToken cancellationToken)
    {
        var tasks = videoUrls.Select(async videoUrl =>
        {
            try
            {
                var response = await SendV3JsonAsync(videoUrl, watchUrl, csrfToken, cancellationToken);
                if (response == null || !response.IsSuccessStatusCode)
                {
                    return;
                }

                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (string.IsNullOrEmpty(body))
                {
                    return;
                }

                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("url", out var urlProp))
                {
                    return;
                }

                var playerUrl = urlProp.GetString() ?? "";
                if (string.IsNullOrWhiteSpace(playerUrl))
                {
                    return;
                }

                playerUrl = NormalizeUrl(playerUrl);

                if (playerUrl.Contains("/player/", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var playerRequest = new HttpRequestMessage(HttpMethod.Get, playerUrl);
                        playerRequest.Headers.Add("Referer", watchUrl);
                        playerRequest.Options.Set(new HttpRequestOptionsKey<bool>("NoFollow"), true);

                        var playerResponse = await _httpClient.SendAsync(playerRequest, cancellationToken);
                        var locationHeader = playerResponse.Headers.Location?.ToString();

                        var resolvedUrl = !string.IsNullOrEmpty(locationHeader)
                                              ? NormalizeUrl(locationHeader)
                                              : playerUrl;

                        lock (sources)
                        {
                            sources.Add(new VideoSource
                            {
                                Url   = resolvedUrl,
                                Type  = VideoType.Embed,
                                Group = groupName
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "failed to resolve player URL: {Url}", playerUrl);
                    }
                }
                else
                {
                    var normalized = NormalizeUrl(playerUrl);
                    if (normalized.Contains("/video/", StringComparison.OrdinalIgnoreCase))
                    {
                        normalized = normalized.Replace("/video/",
                                                        "/player/",
                                                        StringComparison.OrdinalIgnoreCase);
                    }

                    lock (sources)
                    {
                        sources.Add(new VideoSource
                        {
                            Url   = normalized,
                            Type  = VideoType.Embed,
                            Group = groupName
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "failed to resolve video URL: {Url}", videoUrl);
            }
        });

        await Task.WhenAll(tasks);
    }

    private async Task<HttpResponseMessage?> SendV3JsonAsync(
        string            url,
        string            referer,
        string?           csrfToken,
        CancellationToken cancellationToken)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Referer", referer);
            request.Headers.Add("Accept", "application/json");
            request.Headers.Add("X-Requested-With", "XMLHttpRequest");
            if (!string.IsNullOrWhiteSpace(csrfToken))
            {
                request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", csrfToken);
            }

            return await _httpClient.SendAsync(request, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "request failed: {Url}", url);

            return null;
        }
    }

    private async Task ResolveAndAddAsync(
        List<string>      foundUrls,
        string?           groupName,
        string            watchUrl,
        List<VideoSource> sources,
        CancellationToken cancellationToken)
    {
        var playerTasks = foundUrls.Select(async url =>
        {
            var normalized = NormalizeUrl(url);
            if (normalized.Contains("/video/", StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized.Replace("/video/", "/player/", StringComparison.OrdinalIgnoreCase);
            }

            if (normalized.Contains($"{BaseUrl}/player/", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var playerRequest = new HttpRequestMessage(HttpMethod.Get, normalized);
                    playerRequest.Headers.Add("Referer", watchUrl);
                    playerRequest.Options.Set(new HttpRequestOptionsKey<bool>("NoFollow"), true);

                    var playerResponse = await _httpClient.SendAsync(playerRequest, cancellationToken);
                    var locationHeader = playerResponse.Headers.Location?.ToString();

                    var resolvedUrl = !string.IsNullOrEmpty(locationHeader)
                                          ? NormalizeUrl(locationHeader)
                                          : normalized;

                    lock (sources)
                    {
                        sources.Add(new VideoSource
                        {
                            Url   = resolvedUrl,
                            Type  = VideoType.Embed,
                            Group = groupName
                        });
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "failed to resolve player URL: {Url}", normalized);
                }
            }
            else
            {
                lock (sources)
                {
                    sources.Add(new VideoSource
                    {
                        Url   = normalized,
                        Type  = VideoType.Embed,
                        Group = groupName
                    });
                }
            }
        });

        await Task.WhenAll(playerTasks);
    }

    private bool TryParseV3DetailsFast(string html, string animeId, out AnimeDetails? details)
    {
        details = null;

        try
        {
            var title = BreadcrumbTitleRegex().Match(html) is { Success: true } bc
                            ? WebUtility.HtmlDecode(bc.Groups[1].Value).Trim()
                            : null;

            if (string.IsNullOrWhiteSpace(title))
            {
                var h1 = H1Regex().Match(html);
                if (!h1.Success)
                {
                    return false;
                }

                title = Regex.Replace(WebUtility.HtmlDecode(h1.Groups[1].Value), @"\s+", " ").Trim();
                title = Regex.Replace(title, @"\s+izle\s*$", "", RegexOptions.IgnoreCase).Trim();
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                return false;
            }

            string? japaneseTitle = null;
            string? englishTitle  = null;
            var     altTitles     = new List<string>();

            if (FactsBlockRegex().Match(html) is { Success: true } facts)
            {
                foreach (Match m in FactRowRegex().Matches(facts.Value))
                {
                    var label = WebUtility.HtmlDecode(m.Groups[1].Value).Trim();
                    var value = WebUtility.HtmlDecode(m.Groups[2].Value).Trim();

                    if (string.IsNullOrWhiteSpace(value))
                    {
                        continue;
                    }

                    if (label.Contains("Japonca", StringComparison.OrdinalIgnoreCase))
                    {
                        japaneseTitle = value;
                    }
                    else if (label.Contains("İngilizce", StringComparison.OrdinalIgnoreCase)
                             || label.Contains("Ingilizce", StringComparison.OrdinalIgnoreCase))
                    {
                        englishTitle = value;
                    }
                    else if (label.Contains("Diğer", StringComparison.OrdinalIgnoreCase))
                    {
                        altTitles.AddRange(value.Split([',', ';', '|'],
                                                       StringSplitOptions.RemoveEmptyEntries)
                                                .Select(x => x.Trim()));
                    }
                }
            }

            var poster = OgImageRegex().Match(html) is { Success: true } og
                             ? NormalizePosterUrl(og.Groups[1].Value)
                             : null;

            var summaryRaw = SummaryContentRegex().Match(html) is { Success: true } sum
                                 ? sum.Groups[1].Value
                                 : "";

            var result = new AnimeDetails
            {
                Title         = title,
                EnglishTitle  = englishTitle,
                JapaneseTitle = japaneseTitle,
                PosterUrl     = poster,
                AlternativeTitles = altTitles.Where(t => !t.Equals(title, StringComparison.OrdinalIgnoreCase)
                                                         && (englishTitle == null
                                                             || !t.Equals(englishTitle,
                                                                          StringComparison.OrdinalIgnoreCase))
                                                         && (japaneseTitle == null
                                                             || !t.Equals(japaneseTitle,
                                                                          StringComparison.OrdinalIgnoreCase)))
                                             .Distinct()
                                             .ToList(),
                Summary = CleanSummary(summaryRaw)
            };

            var isMovie = result.Title.Contains("Movie", StringComparison.OrdinalIgnoreCase)
                          || animeId.Contains("movie", StringComparison.OrdinalIgnoreCase)
                          || animeId.Contains("film", StringComparison.OrdinalIgnoreCase)
                          || AnimeDetails.IsMovieTitle(result.Title)
                          || AnimeDetails.IsMovieTitle(result.EnglishTitle)
                          || AnimeDetails.IsMovieTitle(result.JapaneseTitle);
            var isOva     = result.Title.Contains("OVA", StringComparison.OrdinalIgnoreCase);
            var isSpecial = result.Title.Contains("Special", StringComparison.OrdinalIgnoreCase);

            var seasonNum = isMovie ? 1 : AnimeDetails.ParseSeasonNumber(result.Title);

            result.SeasonMappings.Add(new SeasonMapping
            {
                SeasonNumber = isMovie ? 1 : seasonNum
            });

            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in V3EpisodeRegex().Matches(html))
            {
                var href = m.Groups[1].Value;
                if (string.IsNullOrWhiteSpace(href))
                {
                    continue;
                }

                var episodeId = href.Split('/').LastOrDefault()?.Replace("-izle", "") ?? "";
                if (string.IsNullOrEmpty(episodeId) || !seenIds.Add(episodeId))
                {
                    continue;
                }

                var epTitleAttr = WebUtility.HtmlDecode(m.Groups[3].Value).Trim();
                var num         = ResolveV3EpisodeNumber(m.Groups[2].Value, epTitleAttr, href);

                var epTitle = string.IsNullOrWhiteSpace(epTitleAttr) ? $"{num}. Bölüm" : epTitleAttr;
                var finalNum = isMovie
                                   ? 1
                                   : num == 0
                                       ? 1
                                       : (int) num;

                if (result.Episodes.Any(e => e.Number == finalNum))
                {
                    continue;
                }

                result.Episodes.Add(new Episode
                {
                    Id     = episodeId,
                    Title  = isMovie ? "Film" : epTitle,
                    Number = finalNum,
                    Season = isMovie ? 1 : seasonNum
                });
            }

            if (result.Episodes.Count == 0)
            {
                return false;
            }

            if (!isMovie && result.Episodes.Count == 1)
            {
                var singleEp = result.Episodes[0];
                if (singleEp.Id.EndsWith("-movie", StringComparison.OrdinalIgnoreCase)
                    || singleEp.Id.EndsWith("-film", StringComparison.OrdinalIgnoreCase)
                    || singleEp.Title.Equals("Movie", StringComparison.OrdinalIgnoreCase)
                    || singleEp.Title.Equals("Film", StringComparison.OrdinalIgnoreCase)
                    || AnimeDetails.IsMovieSummary(result.Summary))
                {
                    isMovie         = true;
                    singleEp.Title  = "Film";
                    singleEp.Number = 1;
                    singleEp.Season = 1;
                }
            }

            result.Format = isMovie
                                ? ContentFormat.Movie
                                : isOva
                                    ? ContentFormat.Ova
                                    : isSpecial
                                        ? ContentFormat.Special
                                        : ContentFormat.Tv;
            result.Episodes = result.Episodes.OrderBy(e => e.Number).ToList();
            result.Normalize();

            details = result;

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "v3 fast details parse failed, falling back to DOM");

            return false;
        }
    }

    private static double ResolveV3EpisodeNumber(string numAttr, string title, string href)
    {
        if (double.TryParse(numAttr.Replace(',', '.'),
                            NumberStyles.Any,
                            CultureInfo.InvariantCulture,
                            out var num)
            && num is > 0 and < 10000)
        {
            return num;
        }

        if (!string.IsNullOrWhiteSpace(title))
        {
            var fromTitle = ParseEpisodeNumbers(title);
            if (fromTitle.Count > 0 && fromTitle[0] != 0)
            {
                return fromTitle[0];
            }
        }

        var fromHref = ParseEpisodeNumbers(href);
        for (var i = fromHref.Count - 1; i >= 0; i--)
        {
            if (fromHref[i] != 0)
            {
                return fromHref[i];
            }
        }

        return 0;
    }

    private string? NormalizePosterUrl(string? url)
    {
        return string.IsNullOrWhiteSpace(url) ? url : NormalizeUrl(url);
    }

    private static string NormalizeFansubName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "Bağımsız";
        }

        var cleaned = Regex.Replace(WebUtility.HtmlDecode(raw), @"\s+", " ").Trim();
        cleaned = Regex.Replace(cleaned, @"^TR\s+", "", RegexOptions.IgnoreCase).Trim();
        cleaned = Regex.Replace(cleaned, @"\s+(BD|WEB|TV|Blu-?Ray)$", "", RegexOptions.IgnoreCase).Trim();

        if (string.IsNullOrWhiteSpace(cleaned) || cleaned == "-")
        {
            return "Bağımsız";
        }

        return cleaned;
    }

    [GeneratedRegex(@"data-fansub-name=""([^""]*)""")]
    private static partial Regex FansubNameRegex();

    [GeneratedRegex(@"translator=""([^""]+)"".*?data-fansub-name=""([^""]*)""")]
    private static partial Regex TranslatorRegex();

    [GeneratedRegex(@"video=""([^""]+)""")]
    private static partial Regex VideoAttrRegex();

    [GeneratedRegex(@"src=""([^""]+)""")]
    private static partial Regex IframeSrcRegex();

    [GeneratedRegex("<.*?>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"\b\d+(?:[\.,]\d+)?\b")]
    private static partial Regex EpisodeNumRegex();

    [GeneratedRegex(@"/cevirmen/\d+$")]
    private static partial Regex CevirmenSuffixRegex();

    [GeneratedRegex(@"/bolum/(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex BolumIdRegex();

    [GeneratedRegex(@"<nav[^>]*v3-breadcrumb[\s\S]*?<span aria-current=""page"">([^<]+)</span>",
                    RegexOptions.IgnoreCase)]
    private static partial Regex BreadcrumbTitleRegex();

    [GeneratedRegex(@"<h1>([^<]+)</h1>", RegexOptions.IgnoreCase)]
    private static partial Regex H1Regex();

    [GeneratedRegex(@"<div class=""v3-anime-facts""[\s\S]*?</div>\s*</div>", RegexOptions.IgnoreCase)]
    private static partial Regex FactsBlockRegex();

    [GeneratedRegex(@"<span>([^<]+)</span>\s*<strong[^>]*>([^<]+)</strong>", RegexOptions.IgnoreCase)]
    private static partial Regex FactRowRegex();

    [GeneratedRegex(@"<meta property=""og:image"" content=""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex OgImageRegex();

    [GeneratedRegex(@"<p id=""v3-anime-summary-content"">([\s\S]*?)</p>", RegexOptions.IgnoreCase)]
    private static partial Regex SummaryContentRegex();

    [GeneratedRegex(
        @"<a[^>]+href=""([^""]+)""[^>]*data-episode-number=""([^""]+)""[^>]*data-episode-title=""([^""]*)""",
        RegexOptions.IgnoreCase)]
    private static partial Regex V3EpisodeRegex();

    private string NormalizeUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return "";
        }

        if (url.StartsWith("//"))
        {
            return "https:" + url;
        }

        if (url.StartsWith("/"))
        {
            return BaseUrl + url;
        }

        return url;
    }

    private static string CleanSummary(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        var decoded = WebUtility.HtmlDecode(text);

        return HtmlTagRegex().Replace(decoded, "").Replace("Özet:", "").Trim();
    }

    private static List<double> ParseEpisodeNumbers(string title)
    {
        var numbers = new List<double>();
        var matches = EpisodeNumRegex().Matches(title);

        foreach (Match match in matches)
        {
            var value = match.Value.Replace(',', '.');
            if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var num))
            {
                numbers.Add(num);
            }
        }

        if (numbers.Count == 0)
        {
            numbers.Add(0);
        }

        return numbers;
    }
}
