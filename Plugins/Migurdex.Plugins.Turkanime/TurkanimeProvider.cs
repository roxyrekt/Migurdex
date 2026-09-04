using AngleSharp.Html.Parser;
using Microsoft.Extensions.Logging;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Migurdex.Plugins.Turkanime;

public partial class TurkanimeProvider : IAnimeProvider
{
    private const string AesKey =
        "710^8A@3@>T2}#zN5xK?kR7KNKb@-A!LzYL5~M1qU0UfdWsZoBm4UUat%}ueUv6E--*hDPPbH7K2bp9^3o41hw,khL:}Kx8080@M";

    private readonly HttpClient                 _httpClient;
    private readonly ILogger<TurkanimeProvider> _logger;

    public TurkanimeProvider(ISharedBridge bridge, ILogger<TurkanimeProvider> logger)
    {
        _httpClient = bridge.CreateHttpClient(o => o.AllowAutoRedirect = true);
        _logger     = logger;

        _httpClient.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
    }

    public string       Name    => "TurkAnime";
    public string       BaseUrl => "https://www.turkanime.tv";
    public ProviderType Type    => ProviderType.Anime;

    public async Task<List<SearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/arama");
            var content = new FormUrlEncodedContent([new KeyValuePair<string, string>("arama", query)]);
            request.Content = content;
            request.Headers.Add("Referer", BaseUrl);

            var response = await _httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            var html   = await response.Content.ReadAsStringAsync(cancellationToken);
            var parser = new HtmlParser();
            var doc    = await parser.ParseDocumentAsync(html);

            var list = new List<SearchResult>();

            var panels = doc.QuerySelectorAll("#orta-icerik .col-md-6.col-sm-6.col-xs-12");
            foreach (var panel in panels)
            {
                var titleLink = panel.QuerySelector(".panel-title a");

                if (titleLink == null)
                {
                    continue;
                }

                var title = titleLink.GetAttribute("data-original-title")
                            ?? titleLink.GetAttribute("title")
                            ?? titleLink.TextContent.Trim();

                if (title.EndsWith(" izle", StringComparison.OrdinalIgnoreCase))
                {
                    title = title[..^5].Trim();
                }

                var href = titleLink.GetAttribute("href") ?? "";
                var slug = href.Split('/').LastOrDefault() ?? "";

                if (string.IsNullOrEmpty(slug))
                {
                    continue;
                }

                var imgEl = panel.QuerySelector(".imaj img");
                var img   = imgEl?.GetAttribute("data-src") ?? imgEl?.GetAttribute("src") ?? "";
                if (img.StartsWith("data:image"))
                {
                    img = imgEl?.GetAttribute("data-src") ?? img;
                }

                if (!string.IsNullOrEmpty(img))
                {
                    if (img.StartsWith("//"))
                    {
                        img = "https:" + img;
                    }
                    else if (img.StartsWith("/"))
                    {
                        img = BaseUrl + img;
                    }
                }

                var descEl  = panel.QuerySelector(".panel-body > .row > .media-object");
                var summary = descEl?.TextContent.Trim() ?? "";
                if (summary.Contains("Beğen", StringComparison.OrdinalIgnoreCase))
                {
                    summary = summary[..summary.IndexOf("Beğen", StringComparison.OrdinalIgnoreCase)].Trim();
                }

                list.Add(new SearchResult
                {
                    Id    = slug,
                    Title = title,
                    Url = href.StartsWith("//")
                              ? "https:" + href
                              : href.StartsWith("/")
                                  ? BaseUrl + href
                                  : href,
                    PosterUrl    = img.Replace("/seriler/", "/serilerb/"),
                    ProviderName = Name,
                    Type         = ProviderType.Anime
                });
            }

            if (list.Count == 0)
            {
                if (html.Contains("LİMİT AŞIMI", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("search rate limit reached for query: {Query}", query);
                }

                var redirectMatch = WindowLocationRegex().Match(html);
                if (!redirectMatch.Success)
                {
                    redirectMatch = SimpleWindowLocationRegex().Match(html);
                }

                if (redirectMatch.Success)
                {
                    var slug = redirectMatch.Groups[1].Value.Trim().TrimEnd('/');
                    if (!string.IsNullOrEmpty(slug))
                    {
                        var title =
                            CultureInfo.CurrentCulture.TextInfo
                                       .ToTitleCase(slug.Replace("-", " "));

                        var posterUrl = "";
                        var imgMatch = Regex.Match(html,
                                                   $@"anime/{Regex.Escape(slug)}[""'].*?data-img=[""']([^""']+)[""']",
                                                   RegexOptions.IgnoreCase | RegexOptions.Singleline);
                        if (!imgMatch.Success)
                        {
                            imgMatch = TvSeriesImageRegex().Match(html);
                        }

                        if (imgMatch.Success)
                        {
                            posterUrl = imgMatch.Groups[1].Value;
                            if (posterUrl.StartsWith("//"))
                            {
                                posterUrl = "https:" + posterUrl;
                            }
                        }
                        else
                        {
                            posterUrl = $"{BaseUrl}/imajlar/serilerb/{slug}.jpg";
                        }

                        list.Add(new SearchResult
                        {
                            Id           = slug,
                            Title        = title,
                            Url          = $"{BaseUrl}/anime/{slug}",
                            PosterUrl    = posterUrl,
                            ProviderName = Name,
                            Type         = ProviderType.Anime
                        });
                    }
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
        try
        {
            var html     = await _httpClient.GetStringAsync($"{BaseUrl}/anime/{animeId}", cancellationToken);
            var parser   = new HtmlParser();
            var document = await parser.ParseDocumentAsync(html);

            var pageTitle = document.QuerySelector(".detay-ust .panel-title")?.TextContent.Trim()
                            ?? document.QuerySelector("title")
                                       ?.TextContent.Split('|')
                                       .FirstOrDefault()
                                       ?.Replace("izle", "", StringComparison.OrdinalIgnoreCase)
                                       .Trim()
                            ?? animeId;

            var details = new AnimeDetails
            {
                Title   = pageTitle,
                Summary = ""
            };
            var     detectedFormat = ContentFormat.Tv;
            string? englishTitle   = null;
            string? japaneseTitle  = null;
            var     altTitles      = new List<string>();

            var infoTable = document.QuerySelector("#animedetay table");
            if (infoTable != null)
            {
                var rows = infoTable.QuerySelectorAll("tr");
                for (var i = 0; i < rows.Length; i++)
                {
                    var cells = rows[i].QuerySelectorAll("td");

                    if (cells.Length == 0)
                    {
                        continue;
                    }

                    var label = cells[0].TextContent.Trim();
                    if ((label.Contains("İngilizce") || label.Contains("English")) && cells.Length >= 3)
                    {
                        var val = cells[2].TextContent.Trim();
                        if (!string.IsNullOrEmpty(val))
                        {
                            englishTitle = val;
                        }
                    }
                    else if ((label.Contains("Japonca") || label.Contains("Japanese")) && cells.Length >= 3)
                    {
                        var val = cells[2].TextContent.Trim();
                        if (!string.IsNullOrEmpty(val))
                        {
                            japaneseTitle = val;
                        }
                    }
                    else if (label.Contains("Diğer") && cells.Length >= 3)
                    {
                        var val = cells[2].TextContent.Trim();
                        if (!string.IsNullOrEmpty(val))
                        {
                            altTitles.AddRange(val.Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries)
                                                  .Select(x => x.Trim()));
                        }
                    }
                    else if (label.Contains("Kategori") && cells.Length >= 3)
                    {
                        var val = cells[2].TextContent.Trim();
                        if (val.Contains("Movie") || val.Contains("Film"))
                        {
                            detectedFormat = ContentFormat.Movie;
                        }
                        else if (val.Contains("OVA"))
                        {
                            detectedFormat = ContentFormat.Ova;
                        }
                        else if (val.Contains("Special"))
                        {
                            detectedFormat = ContentFormat.Special;
                        }
                    }
                    else if (label.StartsWith("Özet") && i + 1 < rows.Length)
                    {
                        details.Summary = rows[i + 1].TextContent.Trim();
                    }
                }
            }

            details.EnglishTitle  = englishTitle;
            details.JapaneseTitle = japaneseTitle;
            details.AlternativeTitles = altTitles
                                        .Where(t => !t.Equals(details.Title, StringComparison.OrdinalIgnoreCase)
                                                    && (englishTitle == null
                                                        || !t.Equals(englishTitle, StringComparison.OrdinalIgnoreCase))
                                                    && (japaneseTitle == null
                                                        || !t.Equals(japaneseTitle,
                                                                     StringComparison.OrdinalIgnoreCase)))
                                        .Distinct()
                                        .ToList();

            if (!string.IsNullOrEmpty(details.Summary))
            {
                if (details.Summary.Contains("anime konusu:"))
                {
                    details.Summary = details.Summary.Split(["anime konusu:"], StringSplitOptions.None)
                                             .Last()
                                             .Trim();
                }

                if (details.Summary.Contains("Türkçe altyazılı bölüm bilgileri"))
                {
                    details.Summary =
                        details.Summary.Split(["Türkçe altyazılı bölüm bilgileri"], StringSplitOptions.None)[0]
                               .Trim();
                }

                details.Summary = details.Summary.Replace("Türk Anime TV'de.", "")
                                         .Replace("Türk Anime TV'de", "")
                                         .Trim();
                if (details.Summary.EndsWith("."))
                {
                    details.Summary = details.Summary.TrimEnd('.').Trim() + ".";
                }
            }

            var isMovie = details.Title.Contains("Movie")
                          || animeId.Contains("movie")
                          || detectedFormat == ContentFormat.Movie;
            var isOva     = details.Title.Contains("OVA") || detectedFormat == ContentFormat.Ova;
            var isSpecial = details.Title.Contains("Special") || detectedFormat == ContentFormat.Special;

            var seasonNum = isMovie ? 1 : AnimeDetails.ParseSeasonNumber(details.Title);
            var seasonMapping = new SeasonMapping
            {
                SeasonNumber = seasonNum
            };
            details.SeasonMappings.Add(seasonMapping);

            var siteAnimeId = document.QuerySelector(".oylama[data-id], [data-unique-id], a.reactions[data-unique-id]")
                                      ?.GetAttribute("data-id")
                              ?? document.QuerySelector(
                                             ".oylama[data-id], [data-unique-id], a.reactions[data-unique-id]")
                                         ?.GetAttribute("data-unique-id")
                              ?? "";

            if (!string.IsNullOrEmpty(siteAnimeId))
            {
                var linksTask =
                    _httpClient.GetStringAsync($"{BaseUrl}/ajax/disbaglanti&animeId={siteAnimeId}", cancellationToken);
                var episodesTask =
                    _httpClient.GetStringAsync($"{BaseUrl}/ajax/bolumler&animeId={siteAnimeId}", cancellationToken);

                await Task.WhenAll(linksTask, episodesTask);

                var linksHtml    = await linksTask;
                var episodesHtml = await episodesTask;

                try
                {
                    var linksDoc = await parser.ParseDocumentAsync(linksHtml);
                    var malUrl   = linksDoc.QuerySelector("a[href*='myanimelist.net/anime/']")?.GetAttribute("href");
                    if (!string.IsNullOrEmpty(malUrl))
                    {
                        seasonMapping.MyAnimeListId = malUrl.TrimEnd('/').Split('/').LastOrDefault();
                    }

                    var aniUrl = linksDoc.QuerySelector("a[href*='anilist.co/anime/']")?.GetAttribute("href");
                    if (!string.IsNullOrEmpty(aniUrl))
                    {
                        seasonMapping.AniListId = aniUrl.TrimEnd('/').Split('/').LastOrDefault();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "failed to parse MAL/AniList links");
                }

                var epsDoc      = await parser.ParseDocumentAsync(episodesHtml);
                var rawEpisodes = new List<Episode>();
                foreach (var element in epsDoc.QuerySelectorAll(
                             "#bolum-list .list li a[title], .bolumler li a, a[href*='/video/']"))
                {
                    var href = element.GetAttribute("href") ?? "";
                    var title =
                        element.QuerySelector("span.bolumAdi")?.TextContent.Trim() ?? element.TextContent.Trim();

                    if (string.IsNullOrEmpty(href) && string.IsNullOrEmpty(title))
                    {
                        continue;
                    }

                    var episodeId = href.Contains("/video/") ? href.Split('/').LastOrDefault() ?? "" : "";

                    var finalNum   = 1;
                    var allNumbers = NumbersOnlyRegex().Matches(title);
                    if (allNumbers.Count > 0)
                    {
                        finalNum = int.Parse(allNumbers.Last().Value);
                    }
                    else
                    {
                        var slugNumMatch = SlugNumRegex().Match(episodeId);
                        if (slugNumMatch.Success)
                        {
                            finalNum = int.Parse(slugNumMatch.Groups[1].Value);
                        }
                    }

                    if (string.IsNullOrEmpty(episodeId))
                    {
                        episodeId = title.Contains("Movie", StringComparison.OrdinalIgnoreCase)
                                        ? $"{animeId}-movie"
                                        : $"{animeId}-{finalNum}-bolum";
                    }

                    var cleanedTitle = title.Replace(details.Title, "", StringComparison.OrdinalIgnoreCase).Trim();

                    cleanedTitle = PrefixSymbolsRegex().Replace(cleanedTitle, "").Trim();
                    if (string.IsNullOrEmpty(cleanedTitle) || DigitsOnlyRegex().IsMatch(cleanedTitle))
                    {
                        cleanedTitle =
                            $"{(string.IsNullOrEmpty(cleanedTitle) ? finalNum.ToString() : cleanedTitle)}. Bölüm";
                    }

                    rawEpisodes.Add(new Episode
                    {
                        Id     = episodeId,
                        Title  = cleanedTitle,
                        Number = finalNum,
                        Season = seasonNum
                    });
                }

                foreach (var ep in rawEpisodes.OrderByDescending(x => x.Title.Contains("Movie")
                                                                      || x.Title.Contains("Film")
                                                                      || x.Title.Contains("OVA")
                                                                      || x.Title.Contains("Special")))
                {
                    if (details.Episodes.Any(x => x.Number == ep.Number))
                    {
                        continue;
                    }

                    details.Episodes.Add(ep);
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

            return details;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "failed to get details for {AnimeId}", animeId);

            throw;
        }
    }

    public async Task<List<string>> GetGroupsAsync(string episodeId, CancellationToken cancellationToken = default)
    {
        var groups = new List<string>();
        try
        {
            var epUrl = episodeId.StartsWith("http") ? episodeId : $"{BaseUrl}/video/{episodeId}";
            var html  = await _httpClient.GetStringAsync(epUrl, cancellationToken);

            var parser = new HtmlParser();
            var doc    = await parser.ParseDocumentAsync(html);

            var buttons = doc.QuerySelectorAll("a[onclick*='ajax/videosec'], button[onclick*='ajax/videosec']");
            foreach (var btn in buttons)
            {
                var onclick = btn.GetAttribute("onclick") ?? "";
                if (onclick.Contains("&v="))
                {
                    continue;
                }

                var name = btn.TextContent.Trim();
                if (!string.IsNullOrEmpty(name) && !groups.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    groups.Add(name);
                }
            }

            if (groups.Count == 0)
            {
                var dangerHeartBtn = doc.QuerySelector(".btn-group .btn-danger .fa-heart, button.btn-danger .fa-heart")
                                        ?.ParentElement;
                if (dangerHeartBtn != null)
                {
                    var name = dangerHeartBtn.TextContent.Trim();
                    if (!string.IsNullOrEmpty(name) && !groups.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        groups.Add(name);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "failed to get fansub groups for episode {EpisodeId}", episodeId);
        }

        if (groups.Count == 0)
        {
            groups.Add("Varsayılan");
        }

        return groups;
    }

    public async Task<List<VideoSource>> GetVideoSourcesAsync(string episodeId,
        string?                                                      group             = null,
        CancellationToken                                            cancellationToken = default)
    {
        var sources = new List<VideoSource>();
        try
        {
            var epUrl = episodeId.StartsWith("http") ? episodeId : $"{BaseUrl}/video/{episodeId}";
            var html  = await _httpClient.GetStringAsync(epUrl, cancellationToken);

            var parser = new HtmlParser();
            var doc    = await parser.ParseDocumentAsync(html);

            var allButtons = doc.QuerySelectorAll("a[onclick*='ajax/videosec'], button[onclick*='ajax/videosec']");

            var directHostBtns = new List<(string HostName, string AjaxUrl)>();
            var fansubBtns     = new List<(string GroupName, string AjaxUrl)>();

            foreach (var btn in allButtons)
            {
                var text    = btn.TextContent.Trim();
                var onclick = btn.GetAttribute("onclick") ?? "";
                var match   = SingleQuoteParamRegex().Match(onclick);

                if (!match.Success)
                {
                    continue;
                }

                var ajaxUrl = match.Groups[1].Value;
                if (ajaxUrl.Contains("&v="))
                {
                    directHostBtns.Add((text, ajaxUrl));
                }
                else
                {
                    fansubBtns.Add((text, ajaxUrl));
                }
            }

            string ExtractSingleFansubName()
            {
                var dangerHeartBtn = doc.QuerySelector(".btn-group .btn-danger .fa-heart, button.btn-danger .fa-heart")
                                        ?.ParentElement;
                if (dangerHeartBtn != null)
                {
                    var name = dangerHeartBtn.TextContent.Trim();
                    if (!string.IsNullOrEmpty(name))
                    {
                        return name;
                    }
                }

                var ceviriEl = doc.QuerySelector(".alert.ceviri, .alert-info.ceviri, .alert-info, .ceviri");
                if (ceviriEl != null)
                {
                    var text  = ceviriEl.TextContent;
                    var match = CeviriTranslatorRegex().Match(text);
                    if (match.Success)
                    {
                        var name = match.Groups[1].Value.Trim();
                        if (!string.IsNullOrEmpty(name))
                        {
                            return name;
                        }
                    }
                }

                return string.IsNullOrWhiteSpace(group) ? "Varsayılan" : group;
            }

            string NormalizeUrl(string rawUrl)
            {
                if (string.IsNullOrWhiteSpace(rawUrl))
                {
                    return string.Empty;
                }

                var url = rawUrl.Replace("\\/", "/").Replace("\\", "").Trim().Trim('"');
                if (url.StartsWith("//"))
                {
                    return "https:" + url;
                }

                if (url.StartsWith("/"))
                {
                    return BaseUrl + url;
                }

                if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    return "https://" + url;
                }

                return url;
            }

            string ProcessIframeUrl(string rawUrl)
            {
                var url = NormalizeUrl(rawUrl);

                if (url.Contains("/embed/") && url.Contains("/url/"))
                {
                    var decrypted = DecryptEmbedUrl(url);
                    if (!string.IsNullOrEmpty(decrypted))
                    {
                        return NormalizeUrl(decrypted);
                    }
                }

                return url;
            }

            if (directHostBtns.Count > 0)
            {
                var singleFansubName = ExtractSingleFansubName();

                var mainPageIframeRaw =
                    doc.QuerySelector("#videodetay iframe, iframe[src*='embed'], iframe[src*='turkanime']")
                       ?.GetAttribute("src");

                if (!string.IsNullOrEmpty(mainPageIframeRaw))
                {
                    var mainIframe = ProcessIframeUrl(mainPageIframeRaw);
                    if (!sources.Any(s => s.Url.Equals(mainIframe, StringComparison.OrdinalIgnoreCase)))
                    {
                        sources.Add(new VideoSource
                        {
                            Url   = mainIframe,
                            Group = singleFansubName,
                            Type  = VideoType.Embed
                        });
                    }
                }

                var hostTasks = new List<Task<(string HostName, string Html)>>();

                foreach (var (hName, hostAjaxUrl) in directHostBtns)
                {
                    hostTasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/{hostAjaxUrl.TrimStart('/')}");
                            req.Headers.Add("X-Requested-With", "XMLHttpRequest");
                            req.Headers.Add("Referer", epUrl);

                            var res = await _httpClient.SendAsync(req, cancellationToken);
                            if (res.IsSuccessStatusCode)
                            {
                                var text = await res.Content.ReadAsStringAsync(cancellationToken);
                                return (hName, text);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "failed to fetch host player HTML for: {HostName}", hName);
                        }

                        return (hName, "");
                    }));
                }

                var hostResults = await Task.WhenAll(hostTasks);

                foreach (var (hName, hostHtml) in hostResults)
                {
                    if (string.IsNullOrEmpty(hostHtml))
                    {
                        continue;
                    }

                    var iframeMatch = IframeTagSrcRegex().Match(hostHtml);
                    if (iframeMatch.Success)
                    {
                        var iframeSrc = ProcessIframeUrl(iframeMatch.Groups[1].Value);

                        if (!sources.Any(s => s.Url.Equals(iframeSrc, StringComparison.OrdinalIgnoreCase)))
                        {
                            sources.Add(new VideoSource
                            {
                                Url   = iframeSrc,
                                Group = singleFansubName,
                                Type  = VideoType.Embed
                            });
                        }
                    }
                }

                return sources;
            }

            var targetFansubs = new List<(string GroupName, string AjaxUrl)>();

            if (!string.IsNullOrWhiteSpace(group))
            {
                foreach (var f in fansubBtns)
                {
                    if (f.GroupName.Equals(group, StringComparison.OrdinalIgnoreCase)
                        || f.GroupName.Contains(group, StringComparison.OrdinalIgnoreCase)
                        || group.Contains(f.GroupName, StringComparison.OrdinalIgnoreCase))
                    {
                        targetFansubs.Add(f);
                    }
                }
            }

            if (targetFansubs.Count == 0)
            {
                targetFansubs.AddRange(fansubBtns);
            }

            foreach (var (gName, ajaxUrl) in targetFansubs)
            {
                try
                {
                    var ajaxRequest = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/{ajaxUrl.TrimStart('/')}");
                    ajaxRequest.Headers.Add("X-Requested-With", "XMLHttpRequest");
                    ajaxRequest.Headers.Add("Referer", epUrl);

                    var ajaxResponse = await _httpClient.SendAsync(ajaxRequest, cancellationToken);
                    if (!ajaxResponse.IsSuccessStatusCode)
                    {
                        continue;
                    }

                    var ajaxHtml = await ajaxResponse.Content.ReadAsStringAsync(cancellationToken);
                    var ajaxDoc  = await parser.ParseDocumentAsync(ajaxHtml);

                    var initialIframeRaw = ajaxDoc.QuerySelector("#videodetay iframe, iframe[src*='turkanime']")
                                                  ?.GetAttribute("src");
                    if (!string.IsNullOrEmpty(initialIframeRaw))
                    {
                        var initialIframe = ProcessIframeUrl(initialIframeRaw);

                        if (!sources.Any(s => s.Url.Equals(initialIframe, StringComparison.OrdinalIgnoreCase)))
                        {
                            sources.Add(new VideoSource
                            {
                                Url     = initialIframe,
                                Hoster  = "TurkAnime",
                                Group   = gName,
                                Quality = "Auto",
                                Type    = VideoType.Embed
                            });
                        }
                    }

                    var hostButtons =
                        ajaxDoc.QuerySelectorAll("button[onclick*='ajax/videosec'], a[onclick*='ajax/videosec']");
                    var hostTasks = new List<Task<(string HostName, string Html)>>();

                    foreach (var hBtn in hostButtons)
                    {
                        var hName   = hBtn.TextContent.Trim();
                        var onclick = hBtn.GetAttribute("onclick") ?? "";

                        if (string.IsNullOrEmpty(hName)
                            || hName.Contains("Takip")
                            || hName.Contains("Mesaj")
                            || hName.Contains("Profil"))
                        {
                            continue;
                        }

                        var match = SingleQuoteParamRegex().Match(onclick);
                        if (match.Success)
                        {
                            var hostAjaxUrl = match.Groups[1].Value;
                            if (hostAjaxUrl.Contains("&v="))
                            {
                                hostTasks.Add(Task.Run(async () =>
                                {
                                    try
                                    {
                                        var req = new HttpRequestMessage(
                                            HttpMethod.Get,
                                            $"{BaseUrl}/{hostAjaxUrl.TrimStart('/')}");

                                        req.Headers.Add("X-Requested-With", "XMLHttpRequest");
                                        req.Headers.Add("Referer", epUrl);

                                        var res = await _httpClient.SendAsync(req, cancellationToken);
                                        if (res.IsSuccessStatusCode)
                                        {
                                            var text = await res.Content.ReadAsStringAsync(cancellationToken);
                                            return (hName, text);
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.LogWarning(ex, "failed to fetch player HTML for: {HostName}", hName);
                                    }

                                    return (hName, "");
                                }));
                            }
                        }
                    }

                    var hostResults = await Task.WhenAll(hostTasks);
                    foreach (var (hName, hostHtml) in hostResults)
                    {
                        if (string.IsNullOrEmpty(hostHtml))
                        {
                            continue;
                        }

                        var iframeMatch = IframeTagSrcRegex().Match(hostHtml);
                        if (iframeMatch.Success)
                        {
                            var iframeSrc = ProcessIframeUrl(iframeMatch.Groups[1].Value);

                            if (!sources.Any(s => s.Url.Equals(iframeSrc, StringComparison.OrdinalIgnoreCase)))
                            {
                                sources.Add(new VideoSource
                                {
                                    Url   = iframeSrc,
                                    Group = gName,
                                    Type  = VideoType.Embed
                                });
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                                     "failed to get video sources for group {Group} on episode {EpisodeId}",
                                     gName,
                                     episodeId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "failed to get video sources for episode {EpisodeId}", episodeId);
        }

        return sources;
    }

    [GeneratedRegex(@"(?:window|top)\.location\s*=\s*[""'](?:https?:)?//[^""']*/?anime/([^""']+)",
                    RegexOptions.IgnoreCase)]
    private static partial Regex WindowLocationRegex();

    [GeneratedRegex(@"window\.location\s*=\s*[""']anime/([^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex SimpleWindowLocationRegex();

    [GeneratedRegex(@"""@type""\s*:\s*""TVSeries"".*?""image""\s*:\s*[""']([^""']+)[""']",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TvSeriesImageRegex();

    [GeneratedRegex(@"(\d+)")]
    private static partial Regex NumbersOnlyRegex();

    [GeneratedRegex(@"-(\d+)-bolum")]
    private static partial Regex SlugNumRegex();

    [GeneratedRegex(@"^[.\s\-:]+")]
    private static partial Regex PrefixSymbolsRegex();

    [GeneratedRegex(@"^\d+$")]
    private static partial Regex DigitsOnlyRegex();

    [GeneratedRegex(@"'([^']+)'")]
    private static partial Regex SingleQuoteParamRegex();

    [GeneratedRegex(@"Çeviri\s*:\s*([^/\r\n<]+)", RegexOptions.IgnoreCase)]
    private static partial Regex CeviriTranslatorRegex();

    [GeneratedRegex(@"iframe[^>]+src=[""']([^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex IframeTagSrcRegex();

    [GeneratedRegex(@"/url/([^?&]+)")]
    private static partial Regex UrlQueryParamRegex();

    private string? DecryptEmbedUrl(string embedUrl)
    {
        try
        {
            var match = UrlQueryParamRegex().Match(embedUrl);
            if (!match.Success)
            {
                return null;
            }

            var b64       = match.Groups[1].Value;
            var jsonBytes = Convert.FromBase64String(b64);
            var jsonStr   = Encoding.UTF8.GetString(jsonBytes);

            using var doc  = JsonDocument.Parse(jsonStr);
            var       root = doc.RootElement;

            var ctStr   = root.GetProperty("ct").GetString()!;
            var ivHex   = root.GetProperty("iv").GetString()!;
            var saltHex = root.GetProperty("s").GetString()!;

            var cipherBytes = Convert.FromBase64String(ctStr);
            var ivBytes     = Convert.FromHexString(ivHex);
            var saltBytes   = Convert.FromHexString(saltHex);

            var passBytes = Encoding.UTF8.GetBytes(AesKey);

            using var md5 = MD5.Create();
            var       d1  = md5.ComputeHash(Concat(passBytes, saltBytes));
            var       d2  = md5.ComputeHash(Concat(d1, passBytes, saltBytes));

            var key = Concat(d1, d2);

            using var aes = Aes.Create();
            aes.Key     = key;
            aes.IV      = ivBytes;
            aes.Mode    = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using var decryptor      = aes.CreateDecryptor();
            var       decryptedBytes = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
            var decryptedStr = Encoding.UTF8.GetString(decryptedBytes)
                                       .Replace("\\/", "/")
                                       .Replace("\\", "")
                                       .Trim()
                                       .Trim('"');

            if (decryptedStr.StartsWith("//"))
            {
                decryptedStr = "https:" + decryptedStr;
            }

            return decryptedStr;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to decrypt embed URL");

            return null;
        }
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var result = new byte[a.Length + b.Length];
        Buffer.BlockCopy(a, 0, result, 0, a.Length);
        Buffer.BlockCopy(b, 0, result, a.Length, b.Length);
        return result;
    }

    private static byte[] Concat(byte[] a, byte[] b, byte[] c)
    {
        var result = new byte[a.Length + b.Length + c.Length];
        Buffer.BlockCopy(a, 0, result, 0, a.Length);
        Buffer.BlockCopy(b, 0, result, a.Length, b.Length);
        Buffer.BlockCopy(c, 0, result, a.Length + b.Length, c.Length);
        return result;
    }
}
