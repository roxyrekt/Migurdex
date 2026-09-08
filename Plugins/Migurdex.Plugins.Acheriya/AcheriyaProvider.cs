using Microsoft.Extensions.Logging;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Migurdex.Plugins.Acheriya;

public partial class AcheriyaProvider : IAnimeProvider
{
    private readonly HttpClient                _httpClient;
    private readonly ILogger<AcheriyaProvider> _logger;

    public AcheriyaProvider(ISharedBridge bridge)
    {
        _httpClient = bridge.CreateHttpClient(o => o.AllowAutoRedirect = true);
        _logger     = bridge.CreateLogger<AcheriyaProvider>();

        _httpClient.DefaultRequestHeaders.Add("RSC", "1");
        _httpClient.DefaultRequestHeaders.Add("User-Agent",
                                              "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
    }

    public string       Name    => "Acheriya";
    public string       BaseUrl => "https://acheriya.com";
    public ProviderType Type    => ProviderType.Anime;

    public async Task<List<SearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        try
        {
            var       url     = $"{BaseUrl}/ara?q={Uri.EscapeDataString(query)}";
            using var rootDoc = await FetchRscDataAsync(url, "initialData", cancellationToken);

            if (rootDoc == null)
            {
                return [];
            }

            if (!rootDoc.RootElement.TryGetProperty("initialData", out var initialData)
                || !initialData.TryGetProperty("results", out var results)
                || results.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var list = new List<SearchResult>();

            foreach (var item in results.EnumerateArray())
            {
                var slug   = item.GetProperty("slug").GetString() ?? "";
                var title  = item.GetProperty("title").GetString() ?? "";
                var poster = item.TryGetProperty("coverImageLink", out var coverProp) ? coverProp.GetString() : null;

                var englishTitle =
                    item.TryGetProperty("titleEnglish", out var engProp)
                    && !string.IsNullOrWhiteSpace(engProp.GetString())
                        ? engProp.GetString()
                        : null;
                var romajiTitle =
                    item.TryGetProperty("titleRomaji", out var romProp)
                    && !string.IsNullOrWhiteSpace(romProp.GetString())
                        ? romProp.GetString()
                        : null;

                string? yearStr = null;
                if (item.TryGetProperty("year", out var yearProp) && yearProp.ValueKind == JsonValueKind.Number)
                {
                    yearStr = yearProp.GetInt32().ToString();
                }

                var categories = new List<string>();
                if (item.TryGetProperty("genres", out var genresProp) && genresProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var g in genresProp.EnumerateArray())
                    {
                        if (g.TryGetProperty("title", out var titleProp) && titleProp.GetString() is string cat)
                        {
                            categories.Add(cat);
                        }
                    }
                }

                list.Add(new SearchResult
                {
                    Id           = slug,
                    Title        = title,
                    EnglishTitle = englishTitle,
                    RomajiTitle  = romajiTitle,
                    PosterUrl    = poster,
                    Year         = yearStr,
                    Categories   = categories,
                    Url          = $"{BaseUrl}/izle/{slug}",
                    ProviderName = Name,
                    Type         = ProviderType.Anime
                });
            }

            return list;
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
            var       slug    = animeId.Contains('/') ? animeId.Split('/')[0] : animeId;
            var       url     = $"{BaseUrl}/izle/{slug}";
            using var rootDoc = await FetchRscDataAsync(url, "\"anime\":", cancellationToken);

            if (rootDoc == null)
            {
                return new AnimeDetails();
            }

            if (!rootDoc.RootElement.TryGetProperty("anime", out var animeProp))
            {
                return new AnimeDetails();
            }

            var title   = animeProp.TryGetProperty("title", out var titleProp) ? titleProp.GetString() ?? slug : slug;
            var summary = animeProp.TryGetProperty("synopsis", out var synProp) ? synProp.GetString() ?? "" : "";
            var englishTitle =
                animeProp.TryGetProperty("titleEnglish", out var engProp)
                && !string.IsNullOrWhiteSpace(engProp.GetString())
                    ? engProp.GetString()
                    : null;
            var romajiTitle =
                animeProp.TryGetProperty("titleRomaji", out var romProp)
                && !string.IsNullOrWhiteSpace(romProp.GetString())
                    ? romProp.GetString()
                    : null;
            var japaneseTitle =
                animeProp.TryGetProperty("titleJapanese", out var japProp)
                && !string.IsNullOrWhiteSpace(japProp.GetString())
                    ? japProp.GetString()
                    : null;

            var seasonNum = AnimeDetails.ParseSeasonNumber(title);

            var details = new AnimeDetails
            {
                Title         = title,
                EnglishTitle  = englishTitle,
                RomajiTitle   = romajiTitle,
                JapaneseTitle = japaneseTitle,
                Summary       = summary,
                Format        = ContentFormat.Tv
            };

            if (animeProp.TryGetProperty("myAnimeListId", out var malProp) && malProp.ValueKind == JsonValueKind.Number)
            {
                var malId = malProp.GetInt32();
                if (malId > 0)
                {
                    details.SeasonMappings.Add(new SeasonMapping
                    {
                        SeasonNumber  = seasonNum,
                        MyAnimeListId = malId.ToString()
                    });
                }
            }

            if (rootDoc.RootElement.TryGetProperty("episodes", out var episodesProp)
                && episodesProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var ep in episodesProp.EnumerateArray())
                {
                    var epNum = ep.GetProperty("episodeNumber").GetInt32();
                    var epTitle = ep.TryGetProperty("title", out var epTitleProp)
                                  && !string.IsNullOrEmpty(epTitleProp.GetString())
                                      ? epTitleProp.GetString()
                                      : $"{epNum}. Bölüm";

                    details.Episodes.Add(new Episode
                    {
                        Id     = $"{slug}/bolum-{epNum}",
                        Title  = epTitle ?? $"{epNum}. Bölüm",
                        Number = epNum,
                        Season = seasonNum
                    });
                }
            }

            return details;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "failed to get details for: {AnimeId}", animeId);
            return new AnimeDetails();
        }
    }

    public async Task<List<string>> GetGroupsAsync(string episodeId, CancellationToken cancellationToken = default)
    {
        try
        {
            var (slug, epNum) = ParseEpisodeId(episodeId);
            var       epUrl   = $"{BaseUrl}/izle/{slug}/bolum-{epNum}";
            using var rootDoc = await FetchRscDataAsync(epUrl, "episodeDetail", cancellationToken);

            if (rootDoc == null)
            {
                return [];
            }

            var links = ExtractLinksFromEpisodeDetail(rootDoc.RootElement);
            return links.Select(l => l.FansubName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "failed to get groups for episode: {EpisodeId}", episodeId);
            return [];
        }
    }

    public async Task<List<VideoSource>> GetVideoSourcesAsync(string episodeId,
        string?                                                      group             = null,
        CancellationToken                                            cancellationToken = default)
    {
        try
        {
            var (slug, epNum) = ParseEpisodeId(episodeId);
            var epUrl = $"{BaseUrl}/izle/{slug}/bolum-{epNum}";

            using var rootDoc = await FetchRscDataAsync(epUrl, "episodeDetail", cancellationToken);

            List<AcheriyaLink> links = [];
            if (rootDoc != null)
            {
                links = ExtractLinksFromEpisodeDetail(rootDoc.RootElement);
            }

            if (links.Count == 0)
            {
                var       animeUrl = $"{BaseUrl}/izle/{slug}";
                using var animeDoc = await FetchRscDataAsync(animeUrl, "\"anime\":", cancellationToken);
                if (animeDoc != null
                    && animeDoc.RootElement.TryGetProperty("episodes", out var episodesProp)
                    && episodesProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var ep in episodesProp.EnumerateArray())
                    {
                        if (ep.TryGetProperty("episodeNumber", out var numProp) && numProp.GetInt32() == epNum)
                        {
                            if (ep.TryGetProperty("videoLinks", out var videoLinksProp)
                                && videoLinksProp.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var vLink in videoLinksProp.EnumerateArray())
                                {
                                    var linkUrl = vLink.TryGetProperty("link", out var lProp)
                                                      ? lProp.GetString() ?? ""
                                                      : "";
                                    if (!string.IsNullOrWhiteSpace(linkUrl))
                                    {
                                        var fansub = vLink.TryGetProperty("fansubName", out var fProp)
                                                         ? fProp.GetString()
                                                         : null;
                                        if (string.IsNullOrWhiteSpace(fansub))
                                        {
                                            fansub = vLink.TryGetProperty("name", out var nProp)
                                                         ? nProp.GetString()
                                                         : "Varsayılan";
                                        }

                                        links.Add(new AcheriyaLink
                                        {
                                            Url        = NormalizeLink(linkUrl),
                                            FansubName = fansub ?? "Varsayılan",
                                            Type = vLink.TryGetProperty("type", out var tProp)
                                                       ? tProp.GetString() ?? ""
                                                       : ""
                                        });
                                    }
                                }
                            }

                            break;
                        }
                    }
                }
            }

            if (links.Count == 0)
            {
                return [];
            }

            var matchingLinks = links;
            if (!string.IsNullOrWhiteSpace(group))
            {
                var filtered =
                    links.Where(l => l.FansubName.Equals(group, StringComparison.OrdinalIgnoreCase)).ToList();
                if (filtered.Count > 0)
                {
                    matchingLinks = filtered;
                }
            }

            var sources = new List<VideoSource>();
            foreach (var link in matchingLinks)
            {
                var isM3U8 = link.Url.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
                             || link.Url.Contains("/hls/", StringComparison.OrdinalIgnoreCase);

                sources.Add(new VideoSource
                {
                    Url = link.Url,
                    Type = isM3U8
                               ? VideoType.M3U8
                               : (link.Url.Contains(".mp4", StringComparison.OrdinalIgnoreCase)
                                      ? VideoType.Mp4
                                      : VideoType.Embed),
                    Hoster = string.IsNullOrWhiteSpace(link.Type) ? "Acheriya" : link.Type,
                    Group  = link.FansubName
                });
            }

            return sources;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "failed to get video sources for: {EpisodeId}", episodeId);
            return [];
        }
    }

    private static (string Slug, int EpNum) ParseEpisodeId(string episodeId)
    {
        var slug  = episodeId;
        var epNum = 1;

        if (episodeId.Contains('/'))
        {
            var parts = episodeId.Split('/', StringSplitOptions.RemoveEmptyEntries);
            slug = parts[0];
            var epPart = parts[^1];
            if (epPart.StartsWith("bolum-", StringComparison.OrdinalIgnoreCase))
            {
                _ = int.TryParse(epPart[6..], out epNum);
            }
            else if (int.TryParse(epPart, out var parsedEp))
            {
                epNum = parsedEp;
            }
        }

        return (slug, epNum);
    }

    private static List<AcheriyaLink> ExtractLinksFromEpisodeDetail(JsonElement rootElement)
    {
        var result = new List<AcheriyaLink>();

        JsonElement? detail = null;
        if (rootElement.TryGetProperty("episodeDetail", out var epDetail))
        {
            detail = epDetail;
        }
        else if (rootElement.TryGetProperty("fallback", out var fb)
                 && fb.ValueKind == JsonValueKind.Array
                 && fb.GetArrayLength() > 3
                 && fb[3].ValueKind == JsonValueKind.Object
                 && fb[3].TryGetProperty("episodeDetail", out var fbEpDetail))
        {
            detail = fbEpDetail;
        }

        if (detail.HasValue
            && detail.Value.TryGetProperty("links", out var linksProp)
            && linksProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in linksProp.EnumerateArray())
            {
                var rawLink = item.TryGetProperty("link", out var lProp) ? lProp.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(rawLink))
                {
                    continue;
                }

                var fansub = item.TryGetProperty("fansubName", out var fProp) ? fProp.GetString() : null;
                if (string.IsNullOrWhiteSpace(fansub))
                {
                    fansub = item.TryGetProperty("name", out var nProp) ? nProp.GetString() : null;
                }

                if (string.IsNullOrWhiteSpace(fansub))
                {
                    fansub = "Varsayılan";
                }

                var type = item.TryGetProperty("type", out var tProp) ? tProp.GetString() ?? "" : "";

                result.Add(new AcheriyaLink
                {
                    Url        = NormalizeLink(rawLink),
                    FansubName = fansub,
                    Type       = type
                });
            }
        }

        return result;
    }

    private static string NormalizeLink(string url)
    {
        if (url.Contains("mediadelivery.net", StringComparison.OrdinalIgnoreCase))
        {
            var match = GuidRegex().Match(url);
            if (match.Success)
            {
                return $"https://tatsumi.acheriya.com/hls/{match.Groups[1].Value}/playlist.m3u8";
            }
        }

        return url;
    }

    private struct AcheriyaLink
    {
        public string Url        { get; set; }
        public string FansubName { get; set; }
        public string Type       { get; set; }
    }

    [GeneratedRegex(@"([a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12})",
                    RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex GuidRegex();

    private async Task<JsonDocument?> FetchRscDataAsync(string url,
        string                                                 keyword,
        CancellationToken                                      cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        var       content = await response.Content.ReadAsStringAsync(cancellationToken);
        using var reader  = new StringReader(content);

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Contains(keyword))
            {
                var idx = line.IndexOf(":[", StringComparison.Ordinal);
                if (idx != -1)
                {
                    var jsonStr = line[(idx + 1)..];
                    try
                    {
                        var doc  = JsonDocument.Parse(jsonStr);
                        var root = doc.RootElement;
                        if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 3)
                        {
                            var targetElement = root[3];
                            var rawText       = targetElement.GetRawText();
                            if (rawText.Contains(keyword, StringComparison.Ordinal))
                            {
                                return JsonDocument.Parse(rawText);
                            }
                        }

                        doc.Dispose();
                    }
                    catch
                    {
                        // ignored
                    }
                }
            }
        }

        return null;
    }
}
