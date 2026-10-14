using AngleSharp.Html.Parser;
using Microsoft.Extensions.Logging;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Migurdex.Plugins.CizgiMax;

public partial class CizgiMaxProvider : IAnimeProvider
{
    private readonly ISharedBridge             _bridge;
    private readonly HttpClient                _httpClient;
    private readonly HttpClient                _noRedirectClient;
    private readonly ILogger<CizgiMaxProvider> _logger;

    public CizgiMaxProvider(ISharedBridge bridge, ILogger<CizgiMaxProvider> logger)
    {
        _bridge = bridge;
        _logger = logger;

        _httpClient = bridge.CreateHttpClient();
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                                                                  "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://cizgimax.online/");

        _noRedirectClient = bridge.CreateHttpClient(o =>
        {
            o.AllowAutoRedirect = false;
            o.UseCookies        = true;
        });
        _noRedirectClient.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        _noRedirectClient.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://cizgimax.online/");
    }

    public string       Name    => "CizgiMax";
    public string       BaseUrl => "https://cizgimax.online";
    public ProviderType Type    => ProviderType.Anime;

    public async Task<List<SearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        try
        {
            var       searchUrl = $"{BaseUrl}/api/search/suggest/?q={Uri.EscapeDataString(query)}";
            using var request   = new HttpRequestMessage(HttpMethod.Get, searchUrl);
            request.Headers.TryAddWithoutValidation("Referer", $"{BaseUrl}/");

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            var       json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc  = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("animes", out var animesElement)
                || animesElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var results = new List<SearchResult>();
            foreach (var item in animesElement.EnumerateArray())
            {
                var name = item.TryGetProperty("name", out var nameProp)
                               ? nameProp.GetString()?.Trim() ?? string.Empty
                               : string.Empty;
                var rawUrl = item.TryGetProperty("url", out var urlProp)
                                 ? urlProp.GetString()?.Trim() ?? string.Empty
                                 : string.Empty;

                if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(rawUrl))
                {
                    continue;
                }

                var slugMatch = DizilerSlugRegex().Match(rawUrl);
                var slug = slugMatch.Success
                               ? slugMatch.Groups["slug"].Value
                               : rawUrl.Trim('/').Split('/').LastOrDefault() ?? rawUrl;

                string? posterUrl = null;
                if (item.TryGetProperty("poster", out var posterProp))
                {
                    var poster = posterProp.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(poster))
                    {
                        posterUrl = poster.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                                    || poster.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                                        ? poster
                                        : $"{BaseUrl}{(poster.StartsWith('/') ? "" : "/")}{poster}";
                    }
                }

                string? year = null;
                if (item.TryGetProperty("year", out var yearProp)
                    && yearProp.ValueKind != JsonValueKind.Null
                    && yearProp.ValueKind != JsonValueKind.Undefined)
                {
                    year = yearProp.ToString();
                }

                var fullUrl = rawUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                              || rawUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                                  ? rawUrl
                                  : $"{BaseUrl}{(rawUrl.StartsWith('/') ? "" : "/")}{rawUrl}";

                var isCartoon = item.TryGetProperty("kind", out var kindProp)
                                && string.Equals(kindProp.GetString()?.Trim(),
                                                 "cizgi-film",
                                                 StringComparison.OrdinalIgnoreCase);

                results.Add(new SearchResult
                {
                    Id           = slug,
                    Title        = name,
                    PosterUrl    = posterUrl,
                    Year         = year,
                    Type         = isCartoon ? ProviderType.Cartoon : ProviderType.Anime,
                    Url          = fullUrl,
                    ProviderName = "CizgiMax"
                });
            }

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Search failed for query: {Query}", query);
            return [];
        }
    }

    public async Task<AnimeDetails> GetDetailsAsync(string animeId, CancellationToken cancellationToken = default)
    {
        try
        {
            var       detailsUrl = $"{BaseUrl}/diziler/{animeId.Trim('/')}/";
            using var req        = new HttpRequestMessage(HttpMethod.Get, detailsUrl);
            req.Headers.TryAddWithoutValidation("Referer", $"{BaseUrl}/");

            using var res = await _httpClient.SendAsync(req, cancellationToken);
            if (!res.IsSuccessStatusCode)
            {
                return new AnimeDetails();
            }

            var       html     = await res.Content.ReadAsStringAsync(cancellationToken);
            var       parser   = new HtmlParser();
            using var document = await parser.ParseDocumentAsync(html, cancellationToken);

            var title = document.QuerySelector("h1")?.TextContent?.Trim();
            if (string.IsNullOrWhiteSpace(title))
            {
                title = document.QuerySelector("meta[property='og:title']")?.GetAttribute("content")?.Trim()
                        ?? document.QuerySelector("meta[name='og:title']")?.GetAttribute("content")?.Trim()
                        ?? animeId;
            }

            var summary = document.QuerySelector("meta[property='og:description']")?.GetAttribute("content")?.Trim()
                          ?? document.QuerySelector("meta[name='description']")?.GetAttribute("content")?.Trim()
                          ?? FindLdJsonDescription(document)
                          ?? document.QuerySelector(".anime-details")?.TextContent?.Trim()
                          ?? string.Empty;
            summary = WhitespaceRegex().Replace(summary, " ").Trim();

            var poster = document.QuerySelector("meta[property='og:image']")?.GetAttribute("content")?.Trim()
                         ?? document.QuerySelector("meta[name='og:image']")?.GetAttribute("content")?.Trim();
            string? posterUrl = null;
            if (!string.IsNullOrWhiteSpace(poster))
            {
                posterUrl = poster.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                            || poster.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                                ? poster
                                : $"{BaseUrl}{(poster.StartsWith('/') ? "" : "/")}{poster}";
            }

            var seen          = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var episodes      = new List<Episode>();
            var fallbackOrder = 1;

            foreach (var a in document.QuerySelectorAll("a"))
            {
                var href = a.GetAttribute("href")?.Trim();
                if (string.IsNullOrEmpty(href))
                {
                    continue;
                }

                var episodeMatch = EpisodePathRegex().Match(href);
                if (!href.Contains("-bolum-izle", StringComparison.OrdinalIgnoreCase) && !episodeMatch.Success)
                {
                    continue;
                }

                var id = (Uri.TryCreate(href, UriKind.Absolute, out var parsedUri)
                              ? parsedUri.AbsolutePath
                              : href.Split('?', '#')[0]).Trim('/');

                if (string.IsNullOrEmpty(id) || !seen.Add(id))
                {
                    continue;
                }

                double number     = fallbackOrder;
                var    bolumMatch = BolumRegex().Match(href);
                if (bolumMatch.Success
                    && double.TryParse(bolumMatch.Groups[1].Value,
                                       NumberStyles.Any,
                                       CultureInfo.InvariantCulture,
                                       out var parsedNum))
                {
                    number = parsedNum;
                }

                var epTitle = a.TextContent?.Trim();
                if (string.IsNullOrWhiteSpace(epTitle)
                    || epTitle.Length > 6
                    || !epTitle.All(char.IsDigit))
                {
                    epTitle = $"{number}. Bölüm";
                }

                var season     = 1;
                var sezonMatch = SezonRegex().Match(href);
                if (sezonMatch.Success
                    && int.TryParse(sezonMatch.Groups[1].Value,
                                    NumberStyles.Any,
                                    CultureInfo.InvariantCulture,
                                    out var parsedSeason))
                {
                    season = parsedSeason;
                }
                else if (episodeMatch.Success)
                {
                    if (int.TryParse(episodeMatch.Groups["season"].Value,
                                     NumberStyles.Any,
                                     CultureInfo.InvariantCulture,
                                     out var epSeason))
                    {
                        season = epSeason;
                    }

                    if (double.TryParse(episodeMatch.Groups["episode"].Value,
                                        NumberStyles.Any,
                                        CultureInfo.InvariantCulture,
                                        out var epNum))
                    {
                        number = epNum;
                    }
                }

                episodes.Add(new Episode
                {
                    Id     = id,
                    Title  = epTitle,
                    Number = number,
                    Season = season
                });

                fallbackOrder++;
            }

            episodes.Sort((a, b) =>
            {
                var sComp = (a.Season ?? 1).CompareTo(b.Season ?? 1);
                return sComp != 0 ? sComp : a.Number.CompareTo(b.Number);
            });

            var details = new AnimeDetails
            {
                Title     = title,
                Summary   = summary,
                PosterUrl = posterUrl,
                Episodes  = episodes
            };
            details.Normalize();
            return details;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get details for anime: {AnimeId}", animeId);
            return new AnimeDetails();
        }
    }

    public async Task<List<string>> GetGroupsAsync(string episodeId, CancellationToken cancellationToken = default)
    {
        try
        {
            var servers = await FetchServersAsync(episodeId, cancellationToken);
            return servers
                   .Select(s => s.Label)
                   .Where(l => !string.IsNullOrWhiteSpace(l))
                   .Select(l => l!.Trim())
                   .Distinct(StringComparer.OrdinalIgnoreCase)
                   .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get groups for episode: {EpisodeId}", episodeId);
            return [];
        }
    }

    public async Task<List<VideoSource>> GetVideoSourcesAsync(string episodeId,
        string?                                                      group             = null,
        CancellationToken                                            cancellationToken = default)
    {
        try
        {
            var episodeUrl = episodeId.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                             || episodeId.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                                 ? episodeId
                                 : $"{BaseUrl}/{(episodeId.StartsWith('/') ? episodeId.TrimStart('/') : episodeId)}";

            var servers = await FetchServersAsync(episodeId, cancellationToken);
            if (!string.IsNullOrWhiteSpace(group))
            {
                servers = servers
                          .Where(s => string.Equals(s.Label?.Trim(), group.Trim(), StringComparison.OrdinalIgnoreCase))
                          .ToList();
            }

            var sources = new List<VideoSource>();
            foreach (var server in servers)
            {
                var     label    = server.Label?.Trim();
                string? finalUrl = null;

                if (!string.IsNullOrEmpty(server.Src)
                    && server.Src.StartsWith("/oynat/?t=", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var       srcUrl = $"{BaseUrl}{(server.Src.StartsWith('/') ? "" : "/")}{server.Src}";
                        using var req    = new HttpRequestMessage(HttpMethod.Get, srcUrl);
                        req.Headers.TryAddWithoutValidation("User-Agent",
                                                            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
                        req.Headers.TryAddWithoutValidation("Referer", episodeUrl);

                        using var resp = await _noRedirectClient.SendAsync(req, cancellationToken);
                        if (resp.Headers.Location != null)
                        {
                            var loc = resp.Headers.Location;
                            finalUrl = loc.IsAbsoluteUri ? loc.ToString() : new Uri(new Uri(BaseUrl), loc).ToString();
                        }
                        else
                        {
                            var body        = await resp.Content.ReadAsStringAsync(cancellationToken);
                            var iframeMatch = IframeSrcRegex().Match(body);
                            if (iframeMatch.Success)
                            {
                                var raw = iframeMatch.Groups["url"].Value.Trim();
                                if (raw.StartsWith("//"))
                                {
                                    finalUrl = "https:" + raw;
                                }
                                else if (raw.StartsWith("/"))
                                {
                                    finalUrl = $"{BaseUrl}{raw}";
                                }
                                else
                                {
                                    finalUrl = raw;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to resolve oynat src for server: {Label}", label);
                    }
                }
                else if (!string.IsNullOrEmpty(server.ResolveUrl)
                         && server.ResolveUrl.StartsWith("/api/np-resolve/?t=", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var resolveFullUrl =
                            $"{BaseUrl}{(server.ResolveUrl.StartsWith('/') ? "" : "/")}{server.ResolveUrl}";
                        using var req = new HttpRequestMessage(HttpMethod.Get, resolveFullUrl);
                        req.Headers.TryAddWithoutValidation("User-Agent",
                                                            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
                        req.Headers.TryAddWithoutValidation("Referer", episodeUrl);

                        using var resp = await _httpClient.SendAsync(req, cancellationToken);
                        if (resp.IsSuccessStatusCode)
                        {
                            var       json = await resp.Content.ReadAsStringAsync(cancellationToken);
                            using var doc  = JsonDocument.Parse(json);
                            if (doc.RootElement.TryGetProperty("id", out var idProp))
                            {
                                var id = idProp.GetString()?.Trim();
                                if (!string.IsNullOrEmpty(id))
                                {
                                    finalUrl = $"https://tau-video.xyz/embed/{id}";
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to resolve np-resolve for server: {Label}", label);
                    }
                }
                else if (!string.IsNullOrEmpty(server.StreamUrl)
                         && server.StreamUrl.StartsWith("/api/stream/", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var qualityUrls = new List<(string Url, string Quality)>();
                        if (!string.IsNullOrEmpty(server.QualitiesUrl))
                        {
                            try
                            {
                                var qualFullUrl =
                                    $"{BaseUrl}{(server.QualitiesUrl.StartsWith('/') ? "" : "/")}{server.QualitiesUrl}";
                                using var qualReq = new HttpRequestMessage(HttpMethod.Get, qualFullUrl);
                                qualReq.Headers.TryAddWithoutValidation(
                                    "User-Agent",
                                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
                                qualReq.Headers.TryAddWithoutValidation("Referer", episodeUrl);

                                using var qualResp = await _httpClient.SendAsync(qualReq, cancellationToken);
                                if (qualResp.IsSuccessStatusCode)
                                {
                                    var       qualJson = await qualResp.Content.ReadAsStringAsync(cancellationToken);
                                    using var qualDoc  = JsonDocument.Parse(qualJson);
                                    if (qualDoc.RootElement.TryGetProperty("qualities", out var qualsArr)
                                        && qualsArr.ValueKind == JsonValueKind.Array)
                                    {
                                        foreach (var q in qualsArr.EnumerateArray())
                                        {
                                            var qUrl = q.TryGetProperty("url", out var quEl) ? quEl.GetString() : null;
                                            if (string.IsNullOrWhiteSpace(qUrl))
                                            {
                                                continue;
                                            }

                                            var qLabel = q.TryGetProperty("q", out var qEl)
                                                             ? qEl.GetString()
                                                             : q.TryGetProperty("label", out var qlEl)
                                                                 ? qlEl.GetString()
                                                                 : null;
                                            qualityUrls.Add(
                                                (qUrl, string.IsNullOrWhiteSpace(qLabel) ? "Auto" : qLabel));
                                        }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to fetch qualities for server: {Label}", label);
                            }
                        }

                        if (qualityUrls.Count == 0)
                        {
                            qualityUrls.Add((server.StreamUrl, "Auto"));
                        }

                        foreach (var (qUrl, qLabel) in qualityUrls)
                        {
                            try
                            {
                                var streamFullUrl = qUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                                                    || qUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                                                        ? qUrl
                                                        : $"{BaseUrl}{(qUrl.StartsWith('/') ? "" : "/")}{qUrl}";
                                using var streamReq = new HttpRequestMessage(HttpMethod.Get, streamFullUrl);
                                streamReq.Headers.TryAddWithoutValidation(
                                    "User-Agent",
                                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
                                streamReq.Headers.TryAddWithoutValidation("Referer", episodeUrl);

                                using var streamResp = await _noRedirectClient.SendAsync(streamReq, cancellationToken);
                                var       loc        = streamResp.Headers.Location;
                                if (loc == null)
                                {
                                    continue;
                                }

                                var directUrl = loc.IsAbsoluteUri
                                                    ? loc.ToString()
                                                    : new Uri(new Uri(BaseUrl), loc).ToString();
                                sources.Add(new VideoSource
                                {
                                    Url     = directUrl,
                                    Quality = qLabel,
                                    Type = directUrl.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)
                                               ? VideoType.M3U8
                                               : VideoType.Mp4,
                                    Hoster  = label,
                                    Group   = ResolveLanguageGroup(label),
                                    Headers = null
                                });
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex,
                                                   "Failed to resolve stream quality {Quality} for server: {Label}",
                                                   qLabel,
                                                   label);
                            }
                        }

                        continue;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to resolve stream for server: {Label}", label);
                    }
                }
                else if (!string.IsNullOrEmpty(server.Src)
                         && (server.Src.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                             || server.Src.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                             || server.Src.StartsWith("//", StringComparison.OrdinalIgnoreCase)))
                {
                    finalUrl = server.Src.StartsWith("//") ? "https:" + server.Src : server.Src;
                }

                if (!string.IsNullOrWhiteSpace(finalUrl))
                {
                    sources.Add(new VideoSource
                    {
                        Url     = finalUrl,
                        Quality = "Embed",
                        Type    = VideoType.Embed,
                        Hoster  = label,
                        Group   = label,
                        Headers = null
                    });
                }
            }

            return sources;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get video sources for episode: {EpisodeId}", episodeId);
            return [];
        }
    }

    private async Task<List<CizgiMaxServer>> FetchServersAsync(string episodeId, CancellationToken cancellationToken)
    {
        var episodeUrl = episodeId.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                         || episodeId.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                             ? episodeId
                             : $"{BaseUrl}/{(episodeId.StartsWith('/') ? episodeId.TrimStart('/') : episodeId)}";

        using var req = new HttpRequestMessage(HttpMethod.Get, episodeUrl);
        req.Headers.TryAddWithoutValidation("Referer", $"{BaseUrl}/");

        using var res = await _httpClient.SendAsync(req, cancellationToken);
        if (!res.IsSuccessStatusCode)
        {
            return [];
        }

        var html  = await res.Content.ReadAsStringAsync(cancellationToken);
        var match = ServersRegex().Match(html);
        if (!match.Success)
        {
            return [];
        }

        var b64 = match.Groups["b64"].Value;

        b64 = b64.Replace("\\/", "/").Replace("\\\\", "\\");
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(b64);
        }
        catch
        {
            return [];
        }

        var json = Encoding.UTF8.GetString(bytes);

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<CizgiMaxServer>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var server = new CizgiMaxServer
            {
                Type         = item.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null,
                Src          = item.TryGetProperty("src", out var srcEl) ? srcEl.GetString() : null,
                ResolveUrl   = item.TryGetProperty("resolveUrl", out var resEl) ? resEl.GetString() : null,
                StreamUrl    = item.TryGetProperty("streamUrl", out var streamEl) ? streamEl.GetString() : null,
                QualitiesUrl = item.TryGetProperty("qualitiesUrl", out var qualEl) ? qualEl.GetString() : null,
                Label        = item.TryGetProperty("label", out var labelEl) ? labelEl.GetString() : null
            };
            list.Add(server);
        }

        return list;
    }

    private sealed class CizgiMaxServer
    {
        public string? Type         { get; set; }
        public string? Src          { get; set; }
        public string? ResolveUrl   { get; set; }
        public string? StreamUrl    { get; set; }
        public string? QualitiesUrl { get; set; }
        public string? Label        { get; set; }
    }

    [GeneratedRegex(@"(?:^|/)diziler/(?<slug>[^/]+)", RegexOptions.IgnoreCase)]
    private static partial Regex DizilerSlugRegex();

    [GeneratedRegex(@"(\d+)-bolum", RegexOptions.IgnoreCase)]
    private static partial Regex BolumRegex();

    [GeneratedRegex(@"(\d+)-sezon", RegexOptions.IgnoreCase)]
    private static partial Regex SezonRegex();

    [GeneratedRegex(@"/episode-\d+-s(?<season>\d+)e(?<episode>[\d.]+)/?", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodePathRegex();

    [GeneratedRegex(@"var\s+servers\s*=\s*JSON\.parse\(atob\([""'](?<b64>[^""']+)[""']\)\)")]
    private static partial Regex ServersRegex();

    [GeneratedRegex(@"<iframe[^>]*\bsrc\s*=\s*[""'](?<url>[^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex IframeSrcRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    private static string? ResolveLanguageGroup(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return null;
        }

        var text = label.Trim();
        if (text.StartsWith("altyaz", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("sub", StringComparison.OrdinalIgnoreCase))
        {
            return "Altyazı";
        }

        if (text.StartsWith("dublaj", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("dub", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("türkçe dublaj", StringComparison.OrdinalIgnoreCase))
        {
            return "Dublaj";
        }

        return null;
    }

    private static string? FindLdJsonDescription(AngleSharp.Html.Dom.IHtmlDocument document)
    {
        foreach (var script in document.QuerySelectorAll("script[type='application/ld+json']"))
        {
            var content = script.TextContent?.Trim();
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(content);
                var root = doc.RootElement.ValueKind == JsonValueKind.Array
                               ? doc.RootElement.EnumerateArray().FirstOrDefault()
                               : doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("description", out var desc)
                    && desc.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(desc.GetString()))
                {
                    return desc.GetString();
                }
            }
            catch
            {
                // ignored
            }
        }

        return null;
    }
}
