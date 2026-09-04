using Microsoft.Extensions.Logging;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Text.Json;

namespace Migurdex.Plugins.Acheriya;

public class AcheriyaProvider : IAnimeProvider
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
                        Id     = $"{slug}/episode/{epNum}",
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

    public async Task<List<VideoSource>> GetVideoSourcesAsync(string episodeId,
        string?                                                      group             = null,
        CancellationToken                                            cancellationToken = default)
    {
        try
        {
            var slug        = episodeId;
            var targetEpNum = 1;

            if (episodeId.Contains('/'))
            {
                var parts = episodeId.Split('/', StringSplitOptions.RemoveEmptyEntries);
                slug = parts[0];
                var epPart = parts[^1];
                if (epPart.StartsWith("bolum-", StringComparison.OrdinalIgnoreCase))
                {
                    _ = int.TryParse(epPart[6..], out targetEpNum);
                }
                else if (int.TryParse(epPart, out var parsedEp))
                {
                    targetEpNum = parsedEp;
                }
            }

            var       url     = $"{BaseUrl}/izle/{slug}";
            using var rootDoc = await FetchRscDataAsync(url, "\"anime\":", cancellationToken);

            if (rootDoc == null)
            {
                return [];
            }

            if (!rootDoc.RootElement.TryGetProperty("episodes", out var episodesProp)
                || episodesProp.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var sources = new List<VideoSource>();

            foreach (var ep in episodesProp.EnumerateArray())
            {
                if (ep.TryGetProperty("episodeNumber", out var numProp) && numProp.GetInt32() == targetEpNum)
                {
                    if (ep.TryGetProperty("videoLinks", out var videoLinksProp)
                        && videoLinksProp.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var vLink in videoLinksProp.EnumerateArray())
                        {
                            var linkUrl  = vLink.TryGetProperty("link", out var lProp) ? lProp.GetString() ?? "" : "";
                            var linkName = vLink.TryGetProperty("name", out var nProp) ? nProp.GetString() : null;

                            if (!string.IsNullOrWhiteSpace(linkUrl))
                            {
                                sources.Add(new VideoSource
                                {
                                    Url = linkUrl,
                                    Type = linkUrl.EndsWith(".m3u8") || linkUrl.Contains("/hls/")
                                               ? VideoType.M3U8
                                               : VideoType.Embed,
                                    Hoster = "Acheriya",
                                    Group  = string.IsNullOrWhiteSpace(linkName) ? "Acheriya" : linkName
                                });
                            }
                        }
                    }

                    if (sources.Count == 0 && ep.TryGetProperty("videoLink", out var mainLinkProp))
                    {
                        var mainUrl = mainLinkProp.GetString() ?? "";
                        if (!string.IsNullOrWhiteSpace(mainUrl))
                        {
                            sources.Add(new VideoSource
                            {
                                Url = mainUrl,
                                Type = mainUrl.EndsWith(".m3u8") || mainUrl.Contains("/hls/")
                                           ? VideoType.M3U8
                                           : VideoType.Embed,
                                Hoster = "Acheriya",
                                Group  = "Acheriya"
                            });
                        }
                    }

                    break;
                }
            }

            return sources;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "failed to get video sources for: {EpisodeId}", episodeId);
            return [];
        }
    }

    private async Task<JsonDocument?> FetchRscDataAsync(string url,
        string                                                 keyword,
        CancellationToken                                      cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        var       content = await response.Content.ReadAsStringAsync(cancellationToken);
        using var reader  = new StringReader(content);

        while (await reader.ReadLineAsync() is { } line)
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
                            return JsonDocument.Parse(targetElement.GetRawText());
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
