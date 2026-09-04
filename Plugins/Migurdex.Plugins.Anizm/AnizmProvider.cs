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

    public AnizmProvider(ISharedBridge bridge, ILogger<AnizmProvider> logger)
    {
        _httpClient = bridge.CreateHttpClient();
        _logger     = logger;

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
            var url =
                $"{BaseUrl}/searchAnime?query={query}&page=1&type=detailed&limit=10&priorityField=info_title&orderBy=info_year&orderDirection=ASC";

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

                    var result = new SearchResult
                    {
                        Id            = item.GetProperty("info_slug").GetString() ?? "",
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
                        Url          = $"{BaseUrl}/{item.GetProperty("info_slug").GetString()}",
                        ProviderName = Name,
                        Type         = ProviderType.Anime,
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
        var html     = await _httpClient.GetStringAsync($"{BaseUrl}/{animeId}", cancellationToken);
        var parser   = new HtmlParser();
        var document = await parser.ParseDocumentAsync(html);

        var     titleRaw      = document.QuerySelector("h1")?.TextContent.Trim() ?? animeId;
        var     title         = Regex.Replace(titleRaw, @"\s+", " ").Trim();
        string? japaneseTitle = null;
        string? englishTitle  = null;
        var     altTitles     = new List<string>();

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
                japaneseTitle = dataValue;
            }
            else if (dataTitle.Contains("İngilizce", StringComparison.OrdinalIgnoreCase))
            {
                englishTitle = dataValue;
            }
            else if (dataTitle.Contains("Diğer", StringComparison.OrdinalIgnoreCase))
            {
                altTitles.AddRange(dataValue.Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries)
                                            .Select(x => x.Trim()));
            }
        }

        var details = new AnimeDetails
        {
            Title         = title,
            EnglishTitle  = englishTitle,
            JapaneseTitle = japaneseTitle,
            AlternativeTitles = altTitles.Where(t => !t.Equals(title, StringComparison.OrdinalIgnoreCase)
                                                     && (englishTitle == null
                                                         || !t.Equals(englishTitle, StringComparison.OrdinalIgnoreCase))
                                                     && (japaneseTitle == null
                                                         || !t.Equals(japaneseTitle,
                                                                      StringComparison.OrdinalIgnoreCase)))
                                         .Distinct()
                                         .ToList(),
            Summary = CleanSummary(document.QuerySelector(".anizm_boxContent .infoDesc, .infoDesc")
                                           ?.TextContent
                                   ?? "")
        };

        var isMovie = details.Title.Contains("Movie", StringComparison.OrdinalIgnoreCase)
                      || animeId.Contains("movie", StringComparison.OrdinalIgnoreCase);
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

        var episodeElements = document.QuerySelectorAll(".episodeListTabContent a, .bolumKutucugu a");
        if (episodeElements.Length == 0)
        {
            episodeElements = document.QuerySelectorAll("a[href*='bolum'], a[href*='movie-izle'], a[href*='/izle/']");
        }

        foreach (var element in episodeElements)
        {
            var href = element.GetAttribute("href") ?? "";

            if (string.IsNullOrEmpty(href))
            {
                continue;
            }

            var epTitle   = element.QuerySelector(".episodeBlock")?.TextContent.Trim() ?? element.TextContent.Trim();
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

    public async Task<List<string>> GetGroupsAsync(string episodeId, CancellationToken cancellationToken = default)
    {
        try
        {
            var watchUrl = $"{BaseUrl}/{episodeId}-izle";
            var html     = await _httpClient.GetStringAsync(watchUrl, cancellationToken);
            var groups   = new List<string>();

            var matches = FansubNameRegex().Matches(html);
            foreach (Match m in matches)
            {
                if (!groups.Contains(m.Groups[1].Value.Trim()))
                {
                    groups.Add(m.Groups[1].Value.Trim());
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

            var translatorMap = new Dictionary<string, string?>();
            var matches       = TranslatorRegex().Matches(html);
            foreach (Match m in matches)
            {
                var url  = m.Groups[1].Value;
                var name = m.Groups[2].Value.Trim();
                if (string.IsNullOrEmpty(group) || name.Equals(group, StringComparison.OrdinalIgnoreCase))
                {
                    translatorMap[url] = name;
                }
            }

            if (translatorMap.Count == 0)
            {
                return sources;
            }

            var batchTasks = translatorMap.Select(async pair =>
            {
                var tUrl      = pair.Key;
                var groupName = pair.Value;

                try
                {
                    var response = await _httpClient.GetAsync(tUrl, cancellationToken);
                    if (!response.IsSuccessStatusCode)
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
                catch (Exception ex)
                {
                    _logger.LogError(ex, "batch video parse failed on URL: {Url}", tUrl);
                }
            });

            await Task.WhenAll(batchTasks);

            return sources.GroupBy(x => x.Url).Select(x => x.First()).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "getVideoSourcesAsync failed");

            return [];
        }
    }

    [GeneratedRegex(@"data-fansub-name=""([^""]+)""")]
    private static partial Regex FansubNameRegex();

    [GeneratedRegex(@"translator=""([^""]+)"".*?data-fansub-name=""([^""]+)""")]
    private static partial Regex TranslatorRegex();

    [GeneratedRegex(@"video=""([^""]+)""")]
    private static partial Regex VideoAttrRegex();

    [GeneratedRegex(@"src=""([^""]+)""")]
    private static partial Regex IframeSrcRegex();

    [GeneratedRegex("<.*?>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"\b\d+(?:[\.,]\d+)?\b")]
    private static partial Regex EpisodeNumRegex();

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
