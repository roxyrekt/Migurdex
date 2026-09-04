using AngleSharp.Html.Parser;
using Microsoft.Extensions.Logging;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Migurdex.Plugins.TrAnimeIzle;

public partial class TrAnimeIzleProvider : IAnimeProvider
{
    private readonly HttpClient                   _httpClient;
    private readonly ILogger<TrAnimeIzleProvider> _logger;

    public TrAnimeIzleProvider(ISharedBridge bridge, ILogger<TrAnimeIzleProvider> logger)
    {
        _logger = logger;

        _httpClient = bridge.CreateHttpClient(o =>
        {
            o.UseCookies        = true;
            o.AllowAutoRedirect = true;
            o.ConfigureHandler  = innerHandler => new TrAnimeCaptchaHandler(BaseUrl, logger, innerHandler);
        });

        _httpClient.DefaultRequestHeaders.Add("User-Agent",
                                              "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        _httpClient.DefaultRequestHeaders.Add("Referer", BaseUrl);
    }

    public string       Name    => "TrAnimeIzle";
    public string       BaseUrl => "https://www.tranimeizle.io";
    public ProviderType Type    => ProviderType.Anime;

    public async Task<List<SearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        try
        {
            var url  = $"{BaseUrl}/arama/{Uri.EscapeDataString(query)}";
            var html = await _httpClient.GetStringAsync(url, cancellationToken);

            var parser   = new HtmlParser();
            var document = await parser.ParseDocumentAsync(html);

            var results = new List<SearchResult>();
            var cards   = document.QuerySelectorAll("a.news-image");

            foreach (var card in cards)
            {
                var href = card.GetAttribute("href") ?? "";
                if (string.IsNullOrEmpty(href))
                {
                    continue;
                }

                if (href.StartsWith("/"))
                {
                    href = BaseUrl + href;
                }

                var segments = href.Split('/', StringSplitOptions.RemoveEmptyEntries);
                var slug     = segments.LastOrDefault() ?? "";

                var parent  = card.ParentElement;
                var titleEl = parent?.QuerySelector("h4");
                var title   = titleEl?.TextContent.Trim() ?? slug;

                if (title.EndsWith(" İzle", StringComparison.OrdinalIgnoreCase))
                {
                    title = title[..^5].Trim();
                }
                else if (title.EndsWith("İzle", StringComparison.OrdinalIgnoreCase))
                {
                    title = title[..^4].Trim();
                }

                var imgEl     = card.QuerySelector("img");
                var posterUrl = imgEl?.GetAttribute("src") ?? "";

                var     textInfo  = parent?.TextContent ?? "";
                string? year      = null;
                var     yearMatch = YearRegex().Match(textInfo);
                if (yearMatch.Success)
                {
                    year = yearMatch.Value;
                }

                results.Add(new SearchResult
                {
                    Id           = slug,
                    Title        = title,
                    PosterUrl    = posterUrl,
                    Url          = href,
                    ProviderName = Name,
                    Type         = ProviderType.Anime,
                    Year         = year,
                    Score        = null
                });
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
            var url  = $"{BaseUrl}/anime/{animeId}";
            var html = await _httpClient.GetStringAsync(url, cancellationToken);

            var parser   = new HtmlParser();
            var document = await parser.ParseDocumentAsync(html);

            var titleEl = document.QuerySelector("h1");
            var title   = titleEl?.TextContent.Trim() ?? animeId;
            if (title.EndsWith(" İzle", StringComparison.OrdinalIgnoreCase))
            {
                title = title[..^5].Trim();
            }
            else if (title.EndsWith("İzle", StringComparison.OrdinalIgnoreCase))
            {
                title = title[..^4].Trim();
            }

            var summaryEl = document.QuerySelector(".p-10 article, .p-10, article");
            var summary   = summaryEl?.TextContent.Trim() ?? "";

            if (summary.StartsWith("Anime Konusu", StringComparison.OrdinalIgnoreCase))
            {
                summary = summary[12..].Trim();
            }

            var details = new AnimeDetails
            {
                Title   = title,
                Summary = summary
            };

            var     dds     = document.QuerySelectorAll("dl dd");
            var     dts     = document.QuerySelectorAll("dl dt");
            string? typeStr = null;

            for (var i = 0; i < dds.Length; i++)
            {
                var label = dds[i].TextContent.Trim();
                if (label.Equals("Anime Tipi", StringComparison.OrdinalIgnoreCase) && i < dts.Length)
                {
                    typeStr = dts[i].TextContent.Trim();

                    break;
                }
            }

            details.Format = string.Equals(typeStr, "Film", StringComparison.OrdinalIgnoreCase)
                                 ? ContentFormat.Movie
                                 : ContentFormat.Tv;

            var epLinks       = document.QuerySelectorAll("a[href*='-bolum-izle']");
            var parsedSeasons = new HashSet<int>();

            foreach (var link in epLinks)
            {
                var href = link.GetAttribute("href") ?? "";
                if (string.IsNullOrEmpty(href))
                {
                    continue;
                }

                if (href.StartsWith("/"))
                {
                    href = href[1..];
                }

                var epText = link.QuerySelector(".etitle span")?.TextContent.Trim() ?? link.TextContent.Trim();

                var seasonNum   = 1;
                var seasonMatch = SezonTextRegex().Match(epText);
                if (seasonMatch.Success && int.TryParse(seasonMatch.Groups[1].Value, out var parsedSeason))
                {
                    seasonNum = parsedSeason;
                }

                double epNum   = 1;
                var    epMatch = BolumTextRegex().Match(epText);
                if (epMatch.Success
                    && double.TryParse(epMatch.Groups[1].Value.Replace(',', '.'),
                                       NumberStyles.Any,
                                       CultureInfo.InvariantCulture,
                                       out var parsedEp))
                {
                    epNum = parsedEp;
                }

                parsedSeasons.Add(seasonNum);

                var compositeId = href;

                if (details.Episodes.All(e => e.Id != compositeId))
                {
                    details.Episodes.Add(new Episode
                    {
                        Id     = compositeId,
                        Title  = $"{epNum}. Bölüm",
                        Number = epNum,
                        Season = seasonNum
                    });
                }
            }

            foreach (var season in parsedSeasons.OrderBy(s => s))
            {
                details.SeasonMappings.Add(new SeasonMapping
                {
                    SeasonNumber = season
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
            _logger.LogError(ex, "getDetailsAsync failed for: {AnimeId}", animeId);

            return new AnimeDetails();
        }
    }

    public async Task<List<string>> GetGroupsAsync(string episodeId, CancellationToken cancellationToken = default)
    {
        try
        {
            var watchUrl = $"{BaseUrl}/{episodeId}";
            var html     = await _httpClient.GetStringAsync(watchUrl, cancellationToken);

            var parser   = new HtmlParser();
            var document = await parser.ParseDocumentAsync(html);

            var selectors = document.QuerySelectorAll(".fansubSelector");
            var groups    = new List<string>();

            foreach (var selector in selectors)
            {
                var fansubName = selector.GetAttribute("data-fad")?.Trim() ?? selector.TextContent.Trim();
                if (!string.IsNullOrEmpty(fansubName))
                {
                    if (!groups.Contains(fansubName))
                    {
                        groups.Add(fansubName);
                    }
                }
            }

            return groups;
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
            var watchUrl = $"{BaseUrl}/{episodeId}";
            var html     = await _httpClient.GetStringAsync(watchUrl, cancellationToken);

            var parser   = new HtmlParser();
            var document = await parser.ParseDocumentAsync(html);

            var epIdMatch = EpisodeIdInputRegex().Match(html);
            if (!epIdMatch.Success)
            {
                _logger.LogWarning("could not extract episodeId from watch page HTML");

                return [];
            }

            var serverEpId = int.Parse(epIdMatch.Groups["id"].Value);

            var selectors      = document.QuerySelectorAll(".fansubSelector");
            var fansubsToFetch = new List<(int Id, string? Name)>();

            if (selectors.Length > 0)
            {
                foreach (var selector in selectors)
                {
                    var fIdStr = selector.GetAttribute("data-fid");
                    if (!int.TryParse(fIdStr, out var fId))
                    {
                        continue;
                    }

                    var fName     = selector.GetAttribute("data-fad")?.Trim() ?? selector.TextContent.Trim();
                    var groupName = fName;

                    if (!string.IsNullOrEmpty(group)
                        && !string.Equals(group, groupName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    fansubsToFetch.Add((fId, groupName));
                }
            }
            else
            {
                var fansubIdMatch = InitializeIdRegex().Match(html);
                if (fansubIdMatch.Success)
                {
                    var fId = int.Parse(fansubIdMatch.Groups["id"].Value);
                    fansubsToFetch.Add((fId, null));
                }
            }

            var fansubTasks = fansubsToFetch.Select(async fansub =>
            {
                var fansubSources = new List<VideoSource>();
                try
                {
                    _logger.LogDebug("fetching sources HTML for episodeId: {EpId}, fansubId: {FansubId}",
                                     serverEpId,
                                     fansub.Id);

                    var payload = new
                    {
                        EpisodeId = serverEpId,
                        FansubId  = fansub.Id
                    };
                    var payloadJson = JsonSerializer.Serialize(payload);
                    var content =
                        new StringContent(payloadJson, Encoding.UTF8, "application/json");

                    var sourcesRequest = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/api/fansubSources")
                    {
                        Content = content
                    };
                    sourcesRequest.Headers.Referrer = new Uri(watchUrl);
                    sourcesRequest.Headers.Add("User-Agent",
                                               "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

                    using var sourcesResponse = await _httpClient.SendAsync(sourcesRequest, cancellationToken);
                    if (!sourcesResponse.IsSuccessStatusCode)
                    {
                        _logger.LogError("failed to fetch source buttons HTML from api/fansubSources");

                        return fansubSources;
                    }

                    var buttonsHtml = await sourcesResponse.Content.ReadAsStringAsync(cancellationToken);

                    var buttonsDoc = await parser.ParseDocumentAsync(buttonsHtml);
                    var buttons    = buttonsDoc.QuerySelectorAll(".sourceBtn");

                    var buttonTasks = buttons.Select(async btn =>
                    {
                        var dataId = btn.GetAttribute("data-id");
                        if (string.IsNullOrEmpty(dataId))
                        {
                            return null;
                        }

                        try
                        {
                            var playerRequest =
                                new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/api/sourcePlayer/{dataId}");
                            playerRequest.Headers.Referrer = new Uri(watchUrl);
                            playerRequest.Headers.Add("User-Agent",
                                                      "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
                            playerRequest.Headers.Add("Accept", "application/json");

                            using var playerResponse = await _httpClient.SendAsync(playerRequest, cancellationToken);
                            if (playerResponse.IsSuccessStatusCode)
                            {
                                var       json = await playerResponse.Content.ReadAsStringAsync(cancellationToken);
                                using var doc  = JsonDocument.Parse(json);
                                if (doc.RootElement.TryGetProperty("source", out var sourceProp))
                                {
                                    var iframeHtml = sourceProp.GetString() ?? "";
                                    var srcMatch   = IframeSrcRegex().Match(iframeHtml);
                                    if (srcMatch.Success)
                                    {
                                        var embedUrl = srcMatch.Groups[1].Value;
                                        if (embedUrl.StartsWith("//"))
                                        {
                                            embedUrl = "https:" + embedUrl;
                                        }

                                        if (embedUrl.Contains("embed2/?id="))
                                        {
                                            var idIdx = embedUrl.IndexOf("id=", StringComparison.OrdinalIgnoreCase);
                                            if (idIdx >= 0)
                                            {
                                                embedUrl = embedUrl[(idIdx + 3)..];
                                            }
                                        }

                                        if (embedUrl.Equals(
                                                "https://pp.userapi.com/c857436/v857436366/6d9e/84dLrNaE_yo.jpg",
                                                StringComparison.OrdinalIgnoreCase))
                                        {
                                            return null;
                                        }

                                        return new VideoSource
                                        {
                                            Url     = embedUrl,
                                            Quality = "Embed",
                                            Type    = VideoType.Embed,
                                            Group   = fansub.Name
                                        };
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex,
                                               "failed to resolve video player for data-id: {DataId}",
                                               dataId);
                        }

                        return null;
                    });

                    var resolvedSources = await Task.WhenAll(buttonTasks);
                    fansubSources.AddRange(resolvedSources.Where(s => s != null)!);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                                       "failed to retrieve sources for fansub ID: {FansubId}",
                                       fansub.Id);
                }

                return fansubSources;
            });

            var allFansubSources = await Task.WhenAll(fansubTasks);
            foreach (var list in allFansubSources)
            {
                sources.AddRange(list);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "getVideoSourcesAsync failed for: {EpisodeId}", episodeId);
        }

        return sources.GroupBy(x => x.Url).Select(x => x.First()).ToList();
    }

    [GeneratedRegex(@"\b(19|20)\d{2}\b")]
    private static partial Regex YearRegex();

    [GeneratedRegex(@"(\d+)\.\s*Sezon", RegexOptions.IgnoreCase)]
    private static partial Regex SezonTextRegex();

    [GeneratedRegex(@"(\d+(?:[\.,]\d+)?)\.\s*Bölüm", RegexOptions.IgnoreCase)]
    private static partial Regex BolumTextRegex();

    [GeneratedRegex(@"id=""EpisodeId""\s+name=""EpisodeId""\s+value=""(?<id>\d+)""")]
    private static partial Regex EpisodeIdInputRegex();

    [GeneratedRegex(@"animeWatch\.initialize\(\s*\d+\s*,\s*\d+\s*,\s*(?<id>\d+)\s*,")]
    private static partial Regex InitializeIdRegex();

    [GeneratedRegex(@"src=""([^""]+)""")]
    private static partial Regex IframeSrcRegex();
}
