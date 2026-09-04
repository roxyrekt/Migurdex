using AngleSharp.Html.Parser;
using Microsoft.Extensions.Logging;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;

namespace Migurdex.Plugins.AsyaAnimeleri;

public partial class AsyaAnimeleriProvider : IAnimeProvider
{
    private readonly HttpClient                     _httpClient;
    private readonly ILogger<AsyaAnimeleriProvider> _logger;

    public AsyaAnimeleriProvider(ISharedBridge bridge, ILogger<AsyaAnimeleriProvider> logger)
    {
        _httpClient = bridge.CreateHttpClient(o =>
        {
            o.UseCookies        = true;
            o.AllowAutoRedirect = true;
        });
        _logger = logger;
    }

    public string       Name    => "AsyaAnimeleri";
    public string       BaseUrl => "https://asyaanimeleri.top";
    public ProviderType Type    => ProviderType.Anime;

    public async Task<List<SearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        try
        {
            var results = new List<SearchResult>();

            try
            {
                var content = new FormUrlEncodedContent([
                    new KeyValuePair<string, string>(
                        "action",
                        "ts_ac_do_search"),
                    new KeyValuePair<string, string>("ts_ac_query", query)
                ]);

                var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/wp-admin/admin-ajax.php")
                {
                    Content = content
                };
                request.Headers.Add("X-Requested-With", "XMLHttpRequest");

                var response = await _httpClient.SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync(cancellationToken);
                    if (!string.IsNullOrWhiteSpace(json) && json.StartsWith('{'))
                    {
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("series", out var seriesArr)
                            && seriesArr.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var seriesObj in seriesArr.EnumerateArray())
                            {
                                if (seriesObj.TryGetProperty("all", out var allArr)
                                    && allArr.ValueKind == JsonValueKind.Array)
                                {
                                    foreach (var item in allArr.EnumerateArray())
                                    {
                                        var title = item.TryGetProperty("post_title", out var tProp)
                                                        ? tProp.GetString() ?? ""
                                                        : "";

                                        var link = item.TryGetProperty("post_link", out var lProp)
                                                       ? lProp.GetString() ?? ""
                                                       : "";

                                        var image = item.TryGetProperty("post_image", out var iProp)
                                                        ? iProp.GetString() ?? ""
                                                        : "";

                                        if (string.IsNullOrEmpty(link))
                                        {
                                            continue;
                                        }

                                        var slug = ExtractSlug(link);
                                        results.Add(new SearchResult
                                        {
                                            Id           = slug,
                                            Title        = HttpUtility.HtmlDecode(title),
                                            PosterUrl    = image,
                                            Url          = link,
                                            ProviderName = Name,
                                            Type         = ProviderType.Anime
                                        });
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "search failed");
            }

            return [.. results.GroupBy(x => x.Id).Select(g => g.First())];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "searchAsync failed for query: {Query}", query);
            return [];
        }
    }

    public async Task<AnimeDetails> GetDetailsAsync(string animeId, CancellationToken cancellationToken = default)
    {
        try
        {
            var cleanSlug = CleanSlug(animeId);
            var pageUrl   = $"{BaseUrl}/series/{cleanSlug}/";
            var html      = await _httpClient.GetStringAsync(pageUrl, cancellationToken);

            var parser   = new HtmlParser();
            var document = await parser.ParseDocumentAsync(html);

            var titleEl = document.QuerySelector("h1.entry-title");
            var title   = titleEl?.TextContent.Trim() ?? cleanSlug;

            string? japaneseTitle = null;
            var     altTitles     = new List<string>();
            var     alterText     = document.QuerySelector("span.alter")?.TextContent.Trim();
            if (!string.IsNullOrEmpty(alterText))
            {
                var splits = alterText.Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries)
                                      .Select(x => x.Trim());
                foreach (var s in splits)
                {
                    if (string.IsNullOrWhiteSpace(s))
                    {
                        continue;
                    }

                    if (japaneseTitle == null && s.Any(c => c >= 0x3040 && c <= 0x9FAF))
                    {
                        japaneseTitle = s;
                    }
                    else
                    {
                        altTitles.Add(s);
                    }
                }
            }

            var summaryEl = document.QuerySelector(".synp .entry-content, .desc");
            var summary   = summaryEl?.TextContent.Trim() ?? "";

            var seasonNumber = AnimeDetails.ParseSeasonNumber(title);

            var details = new AnimeDetails
            {
                Title         = title,
                JapaneseTitle = japaneseTitle,
                AlternativeTitles =
                    altTitles.Where(t => !t.Equals(title, StringComparison.OrdinalIgnoreCase)
                                         && (japaneseTitle == null
                                             || !t.Equals(japaneseTitle, StringComparison.OrdinalIgnoreCase)))
                             .Distinct()
                             .ToList(),
                Summary = summary,
                Format  = ContentFormat.Tv,
                SeasonMappings =
                [
                    new SeasonMapping
                    {
                        SeasonNumber = seasonNumber
                    }
                ]
            };

            var speText = document.QuerySelector(".spe")?.TextContent ?? "";
            if (speText.Contains("Movie", StringComparison.OrdinalIgnoreCase)
                || speText.Contains("Film", StringComparison.OrdinalIgnoreCase))
            {
                details.Format = ContentFormat.Movie;
            }

            var epItems  = document.QuerySelectorAll(".eplister ul li a");
            var episodes = new List<Episode>();

            foreach (var ep in epItems)
            {
                var href = ep.GetAttribute("href") ?? "";
                if (string.IsNullOrEmpty(href))
                {
                    continue;
                }

                var epSlug = ExtractSlug(href);

                var epNumStr = ep.QuerySelector(".epl-num")?.TextContent.Trim() ?? "";
                var epTitle  = ep.QuerySelector(".epl-title")?.TextContent.Trim() ?? $"Bölüm {epNumStr}";

                if (!double.TryParse(epNumStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var epNum))
                {
                    var match = EpisodeNumberRegex().Match(epNumStr);
                    if (match.Success)
                    {
                        double.TryParse(match.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out epNum);
                    }
                }

                episodes.Add(new Episode
                {
                    Id     = epSlug,
                    Title  = epTitle,
                    Number = epNum,
                    Season = seasonNumber
                });
            }

            episodes.Reverse();
            details.Episodes = episodes;

            return details;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "getDetailsAsync failed for anime: {AnimeId}", animeId);
            return new AnimeDetails();
        }
    }

    public async Task<List<VideoSource>> GetVideoSourcesAsync(string episodeId,
        string?                                                      group             = null,
        CancellationToken                                            cancellationToken = default)
    {
        try
        {
            var cleanSlug = CleanSlug(episodeId);
            var epUrl     = $"{BaseUrl}/{cleanSlug}/";
            var html      = await _httpClient.GetStringAsync(epUrl, cancellationToken);

            var parser   = new HtmlParser();
            var document = await parser.ParseDocumentAsync(html);

            var sources = new List<VideoSource>();

            var options = document.QuerySelectorAll("select.mirror option, select[name='mirror'] option");
            foreach (var option in options)
            {
                var base64Val = option.GetAttribute("value") ?? "";
                var label     = option.TextContent.Trim();

                if (string.IsNullOrWhiteSpace(base64Val))
                {
                    continue;
                }

                var decodedHtml = TryDecodeBase64(base64Val);
                if (string.IsNullOrWhiteSpace(decodedHtml))
                {
                    continue;
                }

                var iframeSrc = ExtractIframeSrc(decodedHtml);
                if (!string.IsNullOrEmpty(iframeSrc))
                {
                    await ProcessPlayerUrlAsync(iframeSrc, label, sources, cancellationToken);
                }
            }

            if (sources.Count == 0)
            {
                var defaultIframe = document.QuerySelector("#pembed iframe, #embed_holder iframe");
                var iframeSrc     = defaultIframe?.GetAttribute("src");
                if (!string.IsNullOrEmpty(iframeSrc))
                {
                    await ProcessPlayerUrlAsync(iframeSrc, "VİP", sources, cancellationToken);
                }
            }

            return sources;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "getVideoSourcesAsync failed for episode: {EpisodeId}", episodeId);
            return [];
        }
    }

    public Task<List<string>> GetGroupsAsync(string episodeId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<List<string>>(["AsyaAnimeleri"]);
    }

    private async Task ProcessPlayerUrlAsync(string iframeSrc,
        string                                      hosterName,
        List<VideoSource>                           sources,
        CancellationToken                           cancellationToken = default)
    {
        try
        {
            if (iframeSrc.Contains("asyaanimeleri.", StringComparison.OrdinalIgnoreCase))
            {
                var hashMatch = VideoHashRegex().Match(iframeSrc);
                if (hashMatch.Success)
                {
                    var hash      = hashMatch.Groups[1].Value;
                    var vipSource = await ResolveVipPlayerAsync(hash, cancellationToken);

                    if (vipSource != null)
                    {
                        sources.Add(vipSource);
                        return;
                    }
                }
            }

            sources.Add(new VideoSource
            {
                Url  = iframeSrc,
                Type = VideoType.Embed
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to process player URL: {Url}", iframeSrc);
        }
    }

    private async Task<VideoSource?> ResolveVipPlayerAsync(string hash, CancellationToken cancellationToken = default)
    {
        try
        {
            var apiUrl = $"https://asyaanimeleri.pw/player/index.php?data={hash}&do=getVideo";
            var request = new HttpRequestMessage(HttpMethod.Post, apiUrl)
            {
                Content = new FormUrlEncodedContent([
                    new KeyValuePair<string, string>("hash", hash),
                    new KeyValuePair<string, string>(
                        "r",
                        "https://asyaanimeleri.top/")
                ])
            };

            request.Headers.Add("X-Requested-With", "XMLHttpRequest");
            request.Headers.Add("Origin", "https://asyaanimeleri.pw");
            request.Headers.Add("Referer", $"https://asyaanimeleri.pw/video/{hash}");

            var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrEmpty(body))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("securedLink", out var linkProp))
            {
                var m3u8Url = linkProp.GetString();
                if (!string.IsNullOrEmpty(m3u8Url))
                {
                    return new VideoSource
                    {
                        Url     = m3u8Url,
                        Type    = VideoType.M3U8,
                        Quality = "Auto",
                        Hoster  = "AsyaAnimeleri"
                    };
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to resolve VIP player for hash: {Hash}", hash);
        }

        return null;
    }

    private static string ExtractSlug(string url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return "";
        }

        var uri      = new Uri(url.StartsWith("http") ? url : $"https://asyaanimeleri.top/{url.TrimStart('/')}");
        var segments = uri.Segments.Select(s => s.Trim('/')).Where(s => !string.IsNullOrEmpty(s)).ToList();
        return segments.LastOrDefault() ?? "";
    }

    private static string CleanSlug(string slug)
    {
        slug = slug.Trim('/');
        if (slug.StartsWith("series/"))
        {
            slug = slug["series/".Length..];
        }

        return slug;
    }

    private static string TryDecodeBase64(string input)
    {
        try
        {
            var cleaned = input.Trim();
            var bytes   = Convert.FromBase64String(cleaned);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return "";
        }
    }

    private static string ExtractIframeSrc(string html)
    {
        var match = IframeSrcRegex().Match(html);
        return match.Success ? match.Groups[1].Value : "";
    }

    [GeneratedRegex(@"iframe[^>]+src=[""']([^""']+)[""']", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex IframeSrcRegex();

    [GeneratedRegex(@"\d+(?:\.\d+)?")]
    private static partial Regex EpisodeNumberRegex();

    [GeneratedRegex(@"/video/([a-f0-9]+)")]
    private static partial Regex VideoHashRegex();
}
