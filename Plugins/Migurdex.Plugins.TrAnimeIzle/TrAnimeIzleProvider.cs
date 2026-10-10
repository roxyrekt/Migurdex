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
                                              "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0.0.0 Safari/537.36");
        _httpClient.DefaultRequestHeaders.Add("Referer", BaseUrl);
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
        _httpClient.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
    }

    public string       Name    => "TrAnimeIzle";
    public string       BaseUrl => "https://tranimeizle.org.tr";
    public ProviderType Type    => ProviderType.Anime;

    public async Task<List<SearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        try
        {
            var results = new List<SearchResult>();
            var url = $"{BaseUrl}/searchAnime?query={Uri.EscapeDataString(query)}&page=1&type=detailed&limit=10&priorityField=info_title&orderBy=info_year&orderDirection=ASC";
            using var response = await _httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return results;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (!doc.RootElement.TryGetProperty("data", out var items) || items.ValueKind != JsonValueKind.Array)
            {
                return results;
            }

            foreach (var item in items.EnumerateArray())
            {
                var title = item.TryGetProperty("info_title", out var titleProp) ? titleProp.GetString() ?? "" : "";
                var slug = item.TryGetProperty("info_slug", out var slugProp) ? slugProp.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(slug))
                {
                    continue;
                }

                var englishTitle = item.TryGetProperty("info_titleenglish", out var englishProp)
                                       ? englishProp.GetString()
                                       : null;
                var poster = item.TryGetProperty("info_poster", out var posterProp)
                                 ? posterProp.GetString()
                                 : null;
                var year = item.TryGetProperty("info_year", out var yearProp) ? yearProp.GetString() : null;
                var isMovie = AnimeDetails.IsMovieTitle(title) || AnimeDetails.IsMovieTitle(englishTitle);

                results.Add(new SearchResult
                {
                    Id            = slug,
                    Title         = title,
                    EnglishTitle  = englishTitle,
                    PosterUrl     = string.IsNullOrWhiteSpace(poster) ? null : $"{BaseUrl}/storage/pcovers/{poster}",
                    Url           = $"{BaseUrl}/{slug}",
                    ProviderName  = Name,
                    Type          = ProviderType.Anime,
                    Format        = isMovie ? ContentFormat.Movie : ContentFormat.Tv,
                    Year          = year,
                    Score         = item.TryGetProperty("info_malpoint", out var scoreProp)
                                    && scoreProp.ValueKind == JsonValueKind.Number
                                        ? scoreProp.GetDouble()
                                        : null
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
                             || AnimeDetails.IsMovieTitle(details.Title)
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

                if (Uri.TryCreate(href, UriKind.Absolute, out var episodeUri))
                {
                    href = episodeUri.AbsolutePath.Trim('/');
                }
                else
                {
                    href = href.TrimStart('/');
                }

                var epText = link.QuerySelector(".etitle span")?.TextContent.Trim() ?? link.TextContent.Trim();

                var seasonNum   = 1;
                var seasonMatch = SezonTextRegex().Match(epText);
                if (seasonMatch.Success && int.TryParse(seasonMatch.Groups[1].Value, out var parsedSeason))
                {
                    seasonNum = parsedSeason;
                }

                double epNum;
                var    epMatch = BolumTextRegex().Match(epText);
                if (epMatch.Success
                    && double.TryParse(epMatch.Groups[1].Value.Replace(',', '.'),
                                       NumberStyles.Any,
                                       CultureInfo.InvariantCulture,
                                       out var parsedEp))
                {
                    epNum = parsedEp;
                }
                else
                {
                    var hrefMatch = BolumHrefRegex().Match(href);
                    if (!hrefMatch.Success
                        || !double.TryParse(hrefMatch.Groups[1].Value.Replace(',', '.'),
                                            NumberStyles.Any,
                                            CultureInfo.InvariantCulture,
                                            out var parsedHrefEp)
                        || !epText.Any(char.IsDigit))
                    {
                        continue;
                    }

                    epNum = parsedHrefEp;
                }

                parsedSeasons.Add(seasonNum);

                var compositeId = href.EndsWith("-izle", StringComparison.OrdinalIgnoreCase)
                                      ? href[..^5]
                                      : href;

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

            var selectors = document.QuerySelectorAll("a[data-translatorclick][data-fansub-name]");
            var groups    = new List<string>();

            foreach (var selector in selectors)
            {
                var fansubName = selector.GetAttribute("data-fansub-name")?.Trim() ?? selector.TextContent.Trim();
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
            var watchUrl = $"{BaseUrl}/{episodeId}-izle";
            var html = await _httpClient.GetStringAsync(watchUrl, cancellationToken);
            var parser = new HtmlParser();
            var document = await parser.ParseDocumentAsync(html);
            var translators = document.QuerySelectorAll("a[data-translatorclick][data-fansub-name]");

            foreach (var translator in translators)
            {
                var groupName = translator.GetAttribute("data-fansub-name")?.Trim() ?? "";
                if (!string.IsNullOrEmpty(group)
                    && !string.Equals(group, groupName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var translatorUrl = translator.GetAttribute("translator")
                                   ?? translator.GetAttribute("href")
                                   ?? "";
                translatorUrl = NormalizeUrl(translatorUrl);
                if (string.IsNullOrWhiteSpace(translatorUrl))
                {
                    continue;
                }

                try
                {
                    using var translatorResponse = await _httpClient.GetAsync(translatorUrl, cancellationToken);
                    if (!translatorResponse.IsSuccessStatusCode)
                    {
                        _logger.LogDebug("translator endpoint returned {StatusCode}: {Url}",
                                         (int) translatorResponse.StatusCode,
                                         translatorUrl);
                        continue;
                    }

                    using var translatorJson = JsonDocument.Parse(
                        await translatorResponse.Content.ReadAsStringAsync(cancellationToken));
                    if (!translatorJson.RootElement.TryGetProperty("data", out var data)
                        || data.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var buttonsDocument = await parser.ParseDocumentAsync(data.GetString() ?? "");
                    var videoButtons = buttonsDocument.QuerySelectorAll("[video][data-video-name]");
                    foreach (var button in videoButtons)
                    {
                        var videoUrl = NormalizeUrl(button.GetAttribute("video") ?? "");
                        if (string.IsNullOrWhiteSpace(videoUrl))
                        {
                            continue;
                        }

                        try
                        {
                            using var videoResponse = await _httpClient.GetAsync(videoUrl, cancellationToken);
                            if (!videoResponse.IsSuccessStatusCode)
                            {
                                continue;
                            }

                            using var videoJson = JsonDocument.Parse(
                                await videoResponse.Content.ReadAsStringAsync(cancellationToken));
                            if (!videoJson.RootElement.TryGetProperty("player", out var playerProp)
                                || playerProp.ValueKind != JsonValueKind.String)
                            {
                                continue;
                            }

                            var playerMatch = IframeSrcRegex().Match(playerProp.GetString() ?? "");
                            if (!playerMatch.Success)
                            {
                                continue;
                            }

                            var playerUrl = NormalizeUrl(playerMatch.Groups[1].Value);
                            var resolvedUrl = playerUrl;
                            if (Uri.TryCreate(playerUrl, UriKind.Absolute, out var playerUri)
                                && playerUri.Host.Equals(new Uri(BaseUrl).Host, StringComparison.OrdinalIgnoreCase)
                                && playerUri.AbsolutePath.StartsWith("/player/", StringComparison.OrdinalIgnoreCase))
                            {
                                using var playerRequest = new HttpRequestMessage(HttpMethod.Get, playerUrl);
                                playerRequest.Headers.Referrer = new Uri(watchUrl);
                                playerRequest.Headers.Accept.ParseAdd(
                                    "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
                                playerRequest.Options.Set(new HttpRequestOptionsKey<bool>("NoFollow"), true);

                                using var playerResponse = await _httpClient.SendAsync(playerRequest, cancellationToken);
                                if (!playerResponse.IsSuccessStatusCode)
                                {
                                    continue;
                                }

                                if (playerResponse.Headers.Location is { } location)
                                {
                                    resolvedUrl = location.IsAbsoluteUri
                                                      ? location.ToString()
                                                      : new Uri(playerUri, location).ToString();
                                }
                                else
                                {
                                    var playerHtml = await playerResponse.Content.ReadAsStringAsync(cancellationToken);
                                    var firePlayerHash = ExtractFirePlayerHash(playerHtml);
                                    if (!string.IsNullOrEmpty(firePlayerHash))
                                    {
                                        resolvedUrl = $"https://anizmplayer.com/video/{firePlayerHash}";
                                    }
                                }
                            }

                            if (resolvedUrl != playerUrl || !playerUrl.Contains("/player/", StringComparison.OrdinalIgnoreCase))
                            {
                                sources.Add(new VideoSource
                                {
                                    Url = resolvedUrl,
                                    Quality = button.GetAttribute("data-video-name")?.Trim() ?? "Embed",
                                    Type = VideoType.Embed,
                                    Group = groupName
                                });
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            _logger.LogDebug(ex, "failed to resolve video source URL: {Url}", videoUrl);
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "failed to retrieve videos for translator URL: {Url}", translatorUrl);
                }
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

        return sources.GroupBy(source => source.Url, StringComparer.OrdinalIgnoreCase)
                      .Select(grouping => grouping.First())
                      .ToList();
    }

    private string NormalizeUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return "";
        }

        if (url.StartsWith("//", StringComparison.Ordinal))
        {
            return "https:" + url;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            return url;
        }

        return $"{BaseUrl}/{url.TrimStart('/')}";
    }

    private static string? ExtractFirePlayerHash(string html)
    {
        foreach (Match scriptMatch in ScriptRegex().Matches(html))
        {
            var script = scriptMatch.Groups[1].Value;
            var unpacked = UnpackDeanEdwardsScript(script);
            var match = FirePlayerHashRegex().Match(unpacked ?? script);
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
        }

        return null;
    }

    private static string? UnpackDeanEdwardsScript(string script)
    {
        var evalStart = script.IndexOf("eval(function(p,a,c,k,e,d)", StringComparison.Ordinal);
        if (evalStart < 0)
        {
            return null;
        }

        const string payloadMarker = "return p}('";
        var markerIndex = script.IndexOf(payloadMarker, evalStart, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return null;
        }

        var payloadStart = markerIndex + payloadMarker.Length;
        var payloadEnd = script.IndexOf("',", payloadStart, StringComparison.Ordinal);
        if (payloadEnd < 0)
        {
            return null;
        }

        var args = script[(payloadEnd + 2)..];
        var argsMatch = PackerArgumentsRegex().Match(args);
        if (!argsMatch.Success
            || !int.TryParse(argsMatch.Groups["radix"].Value, out var radix)
            || !int.TryParse(argsMatch.Groups["count"].Value, out var count)
            || radix is < 2 or > 62
            || count is < 1 or > 1000)
        {
            return null;
        }

        var payload = script[payloadStart..payloadEnd];
        var symbols = argsMatch.Groups["symbols"].Value.Split('|');
        for (var index = count - 1; index >= 0; index--)
        {
            var key = ToRadix(index, radix);
            var value = index < symbols.Length && !string.IsNullOrEmpty(symbols[index])
                            ? symbols[index]
                            : key;
            payload = Regex.Replace(payload,
                                    $@"\b{Regex.Escape(key)}\b",
                                    _ => value,
                                    RegexOptions.CultureInvariant);
        }

        return payload;
    }

    private static string ToRadix(int value, int radix)
    {
        const string alphabet = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
        if (value == 0)
        {
            return "0";
        }

        var result = new StringBuilder();
        while (value > 0)
        {
            result.Insert(0, alphabet[value % radix]);
            value /= radix;
        }

        return result.ToString();
    }

    [GeneratedRegex(@"\b(19|20)\d{2}\b")]
    private static partial Regex YearRegex();

    [GeneratedRegex(@"(\d+)\.\s*Sezon", RegexOptions.IgnoreCase)]
    private static partial Regex SezonTextRegex();

    [GeneratedRegex(@"(\d+(?:[\.,]\d+)?)\.\s*Bölüm", RegexOptions.IgnoreCase)]
    private static partial Regex BolumTextRegex();

    [GeneratedRegex(@"(\d+(?:[\.,]\d+)?)-bolum-izle", RegexOptions.IgnoreCase)]
    private static partial Regex BolumHrefRegex();

    [GeneratedRegex(@"src=""([^""]+)""")]
    private static partial Regex IframeSrcRegex();

    [GeneratedRegex("<script[^>]*>(.*?)</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptRegex();

    [GeneratedRegex(@"^(?<radix>\d+),(?<count>\d+),'(?<symbols>.*?)'\.split\('\|'\)", RegexOptions.Singleline)]
    private static partial Regex PackerArgumentsRegex();

    [GeneratedRegex(@"FirePlayer\(""([a-f0-9]{32})""", RegexOptions.IgnoreCase)]
    private static partial Regex FirePlayerHashRegex();
}
