using AngleSharp.Html.Parser;
using Microsoft.Extensions.Logging;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Migurdex.Plugins.Animexe;

public partial class AnimexeProvider : IAnimeProvider
{
    private readonly HttpClient               _httpClient;
    private readonly ILogger<AnimexeProvider> _logger;

    public AnimexeProvider(ISharedBridge bridge, ILogger<AnimexeProvider> logger)
    {
        _httpClient = bridge.CreateHttpClient();
        _logger     = logger;

        _httpClient.DefaultRequestHeaders.Add("User-Agent",
                                              "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

        _httpClient.DefaultRequestHeaders.Add("Referer", BaseUrl);
    }

    public string       Name    => "Animexe";
    public string       BaseUrl => "https://animexe.com";
    public ProviderType Type    => ProviderType.Anime;

    public async Task<List<SearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        try
        {
            var url  = $"{BaseUrl}/search?q={Uri.EscapeDataString(query)}&type=&status=&genre=";
            var html = await _httpClient.GetStringAsync(url, cancellationToken);

            var parser   = new HtmlParser();
            var document = await parser.ParseDocumentAsync(html);

            var results = new List<SearchResult>();
            var cards   = document.QuerySelectorAll("a.a-card");

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

                var titleEl = card.QuerySelector(".a-title");
                var title   = titleEl?.TextContent.Trim() ?? slug;

                var imgEl     = card.QuerySelector(".a-poster img");
                var posterUrl = imgEl?.GetAttribute("src") ?? "";

                var     metaEl = card.QuerySelector(".a-info .a-meta");
                string? year   = null;
                if (metaEl != null)
                {
                    var metaText = metaEl.TextContent;
                    var match    = YearRegex().Match(metaText);
                    if (match.Success)
                    {
                        year = match.Value;
                    }
                }

                var     scoreEl = card.QuerySelector(".a-rating");
                double? score   = null;
                if (scoreEl != null)
                {
                    var scoreText = scoreEl.TextContent.Trim();
                    if (double.TryParse(scoreText.Replace(',', '.'),
                                        NumberStyles.Any,
                                        CultureInfo.InvariantCulture,
                                        out var parsedScore))
                    {
                        score = parsedScore;
                    }
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
                    Score        = score
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

            var summaryEl = document.QuerySelector(".a-desc, .a-desc-wrap p");
            var summary   = summaryEl?.TextContent.Trim() ?? "";

            var details = new AnimeDetails
            {
                Title   = title,
                Summary = summary
            };

            var     infoRows  = document.QuerySelectorAll("#tab-info .info-table tr");
            string? malId     = null;
            string? formatStr = null;

            foreach (var row in infoRows)
            {
                var cells = row.QuerySelectorAll("td");
                if (cells.Length >= 2)
                {
                    var label = cells[0].TextContent.Trim();
                    var value = cells[1].TextContent.Trim();

                    if (label.Equals("MAL ID", StringComparison.OrdinalIgnoreCase))
                    {
                        malId = value;
                    }
                    else if (label.Equals("Tür", StringComparison.OrdinalIgnoreCase))
                    {
                        formatStr = value;
                    }
                }
            }

            if (!string.IsNullOrEmpty(formatStr))
            {
                if (formatStr.Equals("Film", StringComparison.OrdinalIgnoreCase))
                {
                    details.Format = ContentFormat.Movie;
                }
                else
                {
                    details.Format = ContentFormat.Tv;
                }
            }

            var epCards       = document.QuerySelectorAll("a.ep-card");
            var parsedSeasons = new HashSet<int>();

            foreach (var card in epCards)
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

                try
                {
                    var uri      = new Uri(href);
                    var segments = uri.Segments.Select(s => s.Trim('/')).Where(s => !string.IsNullOrEmpty(s)).ToList();

                    if (segments.Count >= 4) // watch | slug | {season} | {episode}
                    {
                        var animeSlug = segments[1];
                        var seasonStr = segments[2];
                        var epStr     = segments[3];

                        var seasonNum = 1;
                        if (int.TryParse(seasonStr, out var parsedSeason))
                        {
                            seasonNum = parsedSeason;
                        }

                        double epNum = 1;
                        if (double.TryParse(epStr.Replace(',', '.'),
                                            NumberStyles.Any,
                                            CultureInfo.InvariantCulture,
                                            out var parsedEp))
                        {
                            epNum = parsedEp;
                        }

                        parsedSeasons.Add(seasonNum);

                        var epTitleEl = card.QuerySelector(".ep-card-title");
                        var epTitle   = epTitleEl?.TextContent.Trim() ?? $"{epNum}. Bölüm";

                        var compositeId = $"{animeSlug}/{seasonStr}/{epStr}";

                        if (details.Episodes.All(e => e.Id != compositeId))
                        {
                            details.Episodes.Add(new Episode
                            {
                                Id     = compositeId,
                                Title  = epTitle,
                                Number = epNum,
                                Season = seasonNum
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "failed to parse episode card href: {Href}", href);
                }
            }

            foreach (var season in parsedSeasons.OrderBy(s => s))
            {
                details.SeasonMappings.Add(new SeasonMapping
                {
                    SeasonNumber  = season,
                    MyAnimeListId = season == 1 ? malId : null
                });
            }

            if (!details.SeasonMappings.Any())
            {
                details.SeasonMappings.Add(new SeasonMapping
                {
                    SeasonNumber  = 1,
                    MyAnimeListId = malId
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
            _logger.LogError(ex, "getDetailsAsync failed for anime: {AnimeId}", animeId);

            return new AnimeDetails();
        }
    }

    public async Task<List<string>> GetGroupsAsync(string episodeId, CancellationToken cancellationToken = default)
    {
        try
        {
            var watchUrl = $"{BaseUrl}/watch/{episodeId}";
            var html     = await _httpClient.GetStringAsync(watchUrl, cancellationToken);

            var parser   = new HtmlParser();
            var document = await parser.ParseDocumentAsync(html);

            var elements = document.QuerySelectorAll(".src-tab, .vmi-src, .vmi-dub");
            var groups   = new List<string>();

            foreach (var el in elements)
            {
                var url = el.GetAttribute("data-url") ?? "";
                if (string.IsNullOrEmpty(url))
                {
                    continue;
                }

                var lbl      = el.GetAttribute("data-lbl") ?? el.TextContent.Trim();
                var isDub    = el.ClassList.Contains("vmi-dub");
                var dataType = el.GetAttribute("data-type") ?? "";

                var info = ParseSourceInfo(lbl, url, dataType, isDub);
                if (!string.IsNullOrEmpty(info.Group) && !groups.Contains(info.Group))
                {
                    groups.Add(info.Group);
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
        try
        {
            var watchUrl = $"{BaseUrl}/watch/{episodeId}";
            var html     = await _httpClient.GetStringAsync(watchUrl, cancellationToken);

            var parser   = new HtmlParser();
            var document = await parser.ParseDocumentAsync(html);

            var subtitles = new List<Subtitle>();
            var subsMatch = SubsArrayRegex().Match(html);
            if (subsMatch.Success)
            {
                try
                {
                    var       jsonText = subsMatch.Groups[1].Value.Trim();
                    using var doc      = JsonDocument.Parse(jsonText);
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in doc.RootElement.EnumerateArray())
                        {
                            var subUrl   = item.GetProperty("url").GetString() ?? "";
                            var subLang  = item.GetProperty("lang").GetString() ?? "";
                            var subLabel = item.GetProperty("label").GetString() ?? "";

                            if (!string.IsNullOrEmpty(subUrl))
                            {
                                subUrl = subUrl.Replace("\\/", "/");
                                if (subUrl.StartsWith("/"))
                                {
                                    subUrl = BaseUrl + subUrl;
                                }

                                subtitles.Add(new Subtitle
                                {
                                    Url      = subUrl,
                                    Language = subLang,
                                    Label    = subLabel,
                                    Format   = "vtt"
                                });
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "failed to parse SUBS array from watch page");
                }
            }

            var elements = document.QuerySelectorAll(".src-tab, .vmi-src, .vmi-dub");
            var sources  = new List<VideoSource>();

            foreach (var el in elements)
            {
                var url = el.GetAttribute("data-url") ?? "";
                if (string.IsNullOrEmpty(url))
                {
                    continue;
                }

                var lbl      = el.GetAttribute("data-lbl") ?? el.TextContent.Trim();
                var isDub    = el.ClassList.Contains("vmi-dub");
                var dataType = el.GetAttribute("data-type") ?? "";

                var info = ParseSourceInfo(lbl, url, dataType, isDub);

                if (!string.IsNullOrEmpty(group)
                    && !string.Equals(group, info.Group, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var source = new VideoSource
                {
                    Url      = url,
                    Quality  = info.Quality,
                    Type     = info.Type,
                    Group    = info.Group,
                    Language = info.Language
                };

                if (string.IsNullOrEmpty(info.Group) && subtitles.Any())
                {
                    source.Subtitles = subtitles;
                }

                sources.Add(source);
            }

            return sources.GroupBy(x => x.Url).Select(x => x.First()).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "getVideoSourcesAsync failed for: {EpisodeId}", episodeId);

            return [];
        }
    }

    [GeneratedRegex(@"\b(19|20)\d{2}\b")]
    private static partial Regex YearRegex();

    [GeneratedRegex(@"const\s+SUBS\s*=\s*(.*?);", RegexOptions.Singleline)]
    private static partial Regex SubsArrayRegex();

    [GeneratedRegex(@"\b(2160p|1440p|1080p|720p|480p|360p|4k)\b", RegexOptions.IgnoreCase)]
    private static partial Regex UrlQualityRegex();

    [GeneratedRegex(@"^\b(2160p|1440p|1080p|720p|480p|360p|4k)\b$", RegexOptions.IgnoreCase)]
    private static partial Regex QualityOnlyRegex();

    [GeneratedRegex(@"\b(4K|2160p|1440p|1080p|720p|480p|360p)\b", RegexOptions.IgnoreCase)]
    private static partial Regex LabelQualityRegex();

    private static (string? Group, string Quality, VideoType Type, string Language) ParseSourceInfo(
        string lbl,
        string url,
        string dataType,
        bool   isDub)
    {
        var quality = ParseQuality(lbl);
        if (quality == "HD" && !string.IsNullOrEmpty(url))
        {
            var urlQualityMatch = UrlQualityRegex().Match(url);
            if (urlQualityMatch.Success)
            {
                quality = urlQualityMatch.Value.ToLower();
            }
        }

        string? groupName = null;
        string  language;

        if (isDub)
        {
            if (lbl.Contains("Türkçe", StringComparison.OrdinalIgnoreCase)
                || url.Contains("-trdub/", StringComparison.OrdinalIgnoreCase))
            {
                language = "Türkçe";
            }
            else if (lbl.Contains("İngilizce", StringComparison.OrdinalIgnoreCase)
                     || lbl.Contains("English", StringComparison.OrdinalIgnoreCase)
                     || url.Contains("-endub/", StringComparison.OrdinalIgnoreCase))
            {
                language = "İngilizce";
            }
            else
            {
                language = "Japonca";
            }
        }
        else
        {
            var cleanedGroup  = CleanGroupLabel(lbl);
            var isQualityOnly = QualityOnlyRegex().IsMatch(cleanedGroup);

            if (!isQualityOnly && !string.IsNullOrEmpty(cleanedGroup))
            {
                groupName = cleanedGroup;
            }

            language = "Japonca";
        }

        var videoType = VideoType.Unknown;
        if (dataType.Equals("mp4", StringComparison.OrdinalIgnoreCase)
            || url.Contains(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            videoType = VideoType.Mp4;
        }
        else if (dataType.Equals("hls", StringComparison.OrdinalIgnoreCase)
                 || dataType.Equals("m3u8", StringComparison.OrdinalIgnoreCase)
                 || url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
        {
            videoType = VideoType.M3U8;
        }
        else
        {
            videoType = VideoType.Embed;
        }

        return (groupName, quality, videoType, language);
    }

    private static string CleanGroupLabel(string label)
    {
        if (string.IsNullOrEmpty(label))
        {
            return string.Empty;
        }

        var idx   = label.IndexOf('(');
        var group = idx >= 0 ? label[..idx].Trim() : label.Trim();

        return group;
    }

    private static string ParseQuality(string label)
    {
        if (string.IsNullOrEmpty(label))
        {
            return "HD";
        }

        var match = LabelQualityRegex().Match(label);

        return match.Success ? match.Value : "HD";
    }
}
