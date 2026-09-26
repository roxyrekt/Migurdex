using AngleSharp.Html.Parser;
using Microsoft.Extensions.Logging;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Migurdex.Plugins.Deokwave;

public partial class DeokwaveProvider : IAnimeProvider
{
    private readonly HttpClient                _httpClient;
    private readonly ILogger<DeokwaveProvider> _logger;

    public DeokwaveProvider(ISharedBridge bridge, ILogger<DeokwaveProvider> logger)
    {
        _httpClient = bridge.CreateHttpClient(o => o.Emulation = BrowserEmulation.Chrome147);
        _logger     = logger;

        if (!_httpClient.DefaultRequestHeaders.Contains("Referer"))
        {
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://deokwave.com/");
        }
    }

    public string       Name    => "Deokwave";
    public string       BaseUrl => "https://deokwave.com";
    public ProviderType Type    => ProviderType.Anime;

    public async Task<List<SearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        try
        {
            var q       = Uri.EscapeDataString(query);
            var results = await SearchV1Async(q, cancellationToken);
            if (results.Count > 0)
            {
                return results;
            }

            return await SearchLegacyAsync(q, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "deokwave search failed");
            return [];
        }
    }

    public async Task<AnimeDetails> GetDetailsAsync(string animeId, CancellationToken cancellationToken = default)
    {
        var cleanId = animeId.Split(['|', '/', ':'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? animeId;
        cleanId = cleanId.Trim().Trim('/');

        var       url      = $"{BaseUrl}/anime/{cleanId}/";
        using var response = await _httpClient.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"deokwave details failed: {response.StatusCode}");
        }

        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(html))
        {
            throw new InvalidOperationException("deokwave empty details page");
        }

        var       parser = new HtmlParser();
        using var doc    = await parser.ParseDocumentAsync(html, cancellationToken);

        var     title     = doc.QuerySelector("h1")?.TextContent.Trim() ?? cleanId;
        var     summary   = "";
        string? poster    = null;
        var     genres    = new List<string>();
        var     isMovieLd = false;

        foreach (var script in doc.QuerySelectorAll("script[type='application/ld+json']"))
        {
            try
            {
                using var ld   = JsonDocument.Parse(script.TextContent);
                var       root = ld.RootElement;
                if (root.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in root.EnumerateArray())
                    {
                        ExtractLd(el, ref title, ref summary, ref poster, genres, ref isMovieLd);
                    }
                }
                else
                {
                    ExtractLd(root, ref title, ref summary, ref poster, genres, ref isMovieLd);
                }
            }
            catch
            {
                // ignored
            }
        }

        var details = new AnimeDetails
        {
            Title     = title,
            Summary   = summary,
            PosterUrl = poster,
            Format    = isMovieLd ? ContentFormat.Movie : ContentFormat.Tv
        };

        // allSezonlar: {"1":{"1":{...}},"2":{...}} / filmde {"-1":[]}
        var seasons = ParseAllSezonlar(html);
        if (seasons.Count > 0 && !(seasons.Count == 1 && seasons.ContainsKey("-1")))
        {
            foreach (var (seasonStr, eps) in seasons.OrderBy(k => ParseInt(k.Key)))
            {
                var seasonNum = ParseInt(seasonStr);
                details.SeasonMappings.Add(new SeasonMapping
                {
                    SeasonNumber = seasonNum
                });
                foreach (var epNum in eps.OrderBy(e => e))
                {
                    details.Episodes.Add(new Episode
                    {
                        Id     = $"{cleanId}|{seasonNum}|{epNum}",
                        Title  = $"{epNum}. Bölüm",
                        Number = epNum,
                        Season = seasonNum
                    });
                }
            }
        }
        else
        {
            var found = new SortedDictionary<int, SortedSet<int>>();
            foreach (var a in doc.QuerySelectorAll("a[href*='/watch/']"))
            {
                var href = a.GetAttribute("href") ?? "";
                var m    = WatchUrlRegex().Match(href);
                if (!m.Success || !string.Equals(m.Groups[1].Value, cleanId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (m.Groups[2].Success
                    && m.Groups[3].Success
                    && int.TryParse(m.Groups[2].Value, out var s)
                    && int.TryParse(m.Groups[3].Value, out var e))
                {
                    if (!found.TryGetValue(s, out var set))
                    {
                        set      = [];
                        found[s] = set;
                    }

                    set.Add(e);
                }
            }

            if (found.Count > 0)
            {
                foreach (var (s, set) in found)
                {
                    details.SeasonMappings.Add(new SeasonMapping
                    {
                        SeasonNumber = s
                    });
                    foreach (var e in set)
                    {
                        details.Episodes.Add(new Episode
                        {
                            Id     = $"{cleanId}|{s}|{e}",
                            Title  = $"{e}. Bölüm",
                            Number = e,
                            Season = s
                        });
                    }
                }
            }
            else
            {
                details.Format = ContentFormat.Movie;
                details.SeasonMappings.Add(new SeasonMapping
                {
                    SeasonNumber = 1
                });
                details.Episodes.Add(new Episode
                {
                    Id     = $"{cleanId}|0|0",
                    Title  = "Film",
                    Number = 1,
                    Season = 1
                });
            }
        }

        details.Normalize();
        return details;
    }

    public async Task<List<string>> GetGroupsAsync(string episodeId, CancellationToken cancellationToken = default)
    {
        try
        {
            var fansubs = await FetchFansubsAsync(episodeId, cancellationToken);
            return fansubs
                   .Select(f => f.Name)
                   .Where(n => !string.IsNullOrWhiteSpace(n))
                   .Distinct(StringComparer.OrdinalIgnoreCase)
                   .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "deokwave groups failed");
            return [];
        }
    }

    public async Task<List<VideoSource>> GetVideoSourcesAsync(string episodeId,
        string?                                                      group             = null,
        CancellationToken                                            cancellationToken = default)
    {
        try
        {
            var (animeId, season, episode) = ParseEpisodeId(episodeId);

            using var infoDoc = await FetchVideoInfoDocAsync(animeId, season, episode, cancellationToken);
            if (infoDoc is null)
            {
                return [];
            }

            var root = infoDoc.RootElement;
            var hasFansubs = root.TryGetProperty("fansubs", out var fansubs)
                             && fansubs.ValueKind == JsonValueKind.Array
                             && fansubs.GetArrayLength() > 0;

            var     sources = new List<VideoSource>();
            string? token   = null;

            if (hasFansubs)
            {
                foreach (var f in fansubs.EnumerateArray())
                {
                    var isSibnet = f.TryGetProperty("isSibnet", out var sb) && sb.ValueKind == JsonValueKind.True;
                    var name = f.TryGetProperty("name", out var nProp) ? nProp.GetString() ?? "Deokwave" : "Deokwave";
                    var extra = f.TryGetProperty("extra", out var eProp) ? eProp.GetString() : null;

                    if (!MatchesGroup(name, extra, group))
                    {
                        continue;
                    }

                    if (isSibnet)
                    {
                        var sibnetId = ParseSibnetId(f);
                        if (string.IsNullOrWhiteSpace(sibnetId))
                        {
                            continue;
                        }

                        var sibQuality = "480p";
                        if (f.TryGetProperty("qualities", out var sqArr)
                            && sqArr.ValueKind == JsonValueKind.Array
                            && sqArr.EnumerateArray()
                                    .Select(q => q.GetString())
                                    .FirstOrDefault(q => !string.IsNullOrWhiteSpace(q)) is { } firstQ)
                        {
                            sibQuality = NormalizeQuality(firstQ);
                        }

                        sources.Add(new VideoSource
                        {
                            Url     = $"https://video.sibnet.ru/shell.php?videoid={sibnetId}",
                            Quality = sibQuality,
                            Type    = VideoType.Embed,
                            Hoster  = "Sibnet",
                            Group   = BuildGroupLabel(name, extra),
                        });
                        continue;
                    }

                    var vid = f.TryGetProperty("videoid", out var vProp) ? vProp.GetString() : null;
                    if (string.IsNullOrWhiteSpace(vid))
                    {
                        continue;
                    }

                    var qualities = CollectQualities(f, root);
                    token ??= await FetchVideoTokenAsync(animeId, season, episode, cancellationToken);
                    if (string.IsNullOrWhiteSpace(token))
                    {
                        continue;
                    }

                    foreach (var q in qualities.OrderByDescending(RankQuality))
                    {
                        sources.Add(new VideoSource
                        {
                            Url     = BuildSw2Url(vid, q, token),
                            Quality = NormalizeQuality(q),
                            Type    = VideoType.Mp4,
                            Hoster  = "Deokwave",
                            Group   = BuildGroupLabel(name, extra),
                            Headers = new Dictionary<string, string>
                            {
                                { "Referer", "https://deokwave.com/" }
                            }
                        });
                    }
                }
            }

            if (sources.Count == 0
                && root.TryGetProperty("videoid", out var rootVidProp)
                && !string.IsNullOrWhiteSpace(rootVidProp.GetString())
                && string.IsNullOrWhiteSpace(group))
            {
                var rootVid   = rootVidProp.GetString()!;
                var qualities = CollectQualities(null, root);
                token ??= await FetchVideoTokenAsync(animeId, season, episode, cancellationToken);
                if (!string.IsNullOrWhiteSpace(token))
                {
                    foreach (var q in qualities.OrderByDescending(RankQuality))
                    {
                        sources.Add(new VideoSource
                        {
                            Url     = BuildSw2Url(rootVid, q, token),
                            Quality = NormalizeQuality(q),
                            Type    = VideoType.Mp4,
                            Hoster  = "Deokwave",
                            Group   = "Deokwave",
                            Headers = new Dictionary<string, string>
                            {
                                { "Referer", "https://deokwave.com/" }
                            }
                        });
                    }
                }
            }

            return sources;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "deokwave video sources failed");
            return [];
        }
    }

    private static bool MatchesGroup(string name, string? extra, string? group)
    {
        if (string.IsNullOrWhiteSpace(group))
        {
            return true;
        }

        return name.Contains(group, StringComparison.OrdinalIgnoreCase)
               || group.Contains(name, StringComparison.OrdinalIgnoreCase)
               || (!string.IsNullOrWhiteSpace(extra) && extra.Contains(group, StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildGroupLabel(string name, string? extra)
    {
        return name.Trim();
    }

    private static List<string> CollectQualities(JsonElement? fansub, JsonElement root)
    {
        var qualities = new List<string>();
        if (fansub.HasValue
            && fansub.Value.TryGetProperty("qualities", out var qArr)
            && qArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var q in qArr.EnumerateArray())
            {
                var qs = q.GetString();
                if (!string.IsNullOrWhiteSpace(qs))
                {
                    qualities.Add(qs);
                }
            }
        }

        if (qualities.Count == 0
            && root.TryGetProperty("qualities", out var rq)
            && rq.ValueKind == JsonValueKind.Array)
        {
            foreach (var q in rq.EnumerateArray())
            {
                var qs = q.GetString();
                if (!string.IsNullOrWhiteSpace(qs))
                {
                    qualities.Add(qs);
                }
            }
        }

        if (qualities.Count == 0)
        {
            qualities.Add("720p");
        }

        return qualities;
    }

    private static string NormalizeQuality(string q)
    {
        q = q.Trim();
        return q.EndsWith("p", StringComparison.OrdinalIgnoreCase) ? q : q + "p";
    }

    private static string BuildSw2Url(string videoid, string quality, string token)
    {
        var qNum = new string(quality.Where(char.IsDigit).ToArray());
        if (string.IsNullOrWhiteSpace(qNum))
        {
            qNum = quality;
        }

        return $"https://sw2.deokwave.com/v/{Uri.EscapeDataString(videoid)}/{Uri.EscapeDataString(qNum)}/?vt={token}";
    }

    private async Task<JsonDocument?> FetchVideoInfoDocAsync(string animeId,
        string                                                      season,
        string                                                      episode,
        CancellationToken                                           ct)
    {
        var candidates = new List<string>
        {
            $"{BaseUrl}/watch/video-info/?animeid={Uri.EscapeDataString(animeId)}&season={season}&episode={episode}"
        };
        if (!(season == "0" && episode == "0"))
        {
            candidates.Add($"{BaseUrl}/watch/video-info/?animeid={Uri.EscapeDataString(animeId)}&season=0&episode=0");
        }

        candidates.Add($"{BaseUrl}/watch/video-info/?animeid={Uri.EscapeDataString(animeId)}");

        foreach (var url in candidates)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("Referer", BuildWatchReferer(animeId, season, episode));
                using var resp = await _httpClient.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    continue;
                }

                var json = await resp.Content.ReadAsStringAsync(ct);
                var doc  = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("success", out var ok)
                    && ok.ValueKind == JsonValueKind.False)
                {
                    doc.Dispose();
                    continue;
                }

                return doc;
            }
            catch
            {
                // ignored
            }
        }

        _logger.LogInformation(
            "deokwave video-info success:false ({animeId}|{season}|{episode}) - uyelik gerekli olabilir",
            animeId,
            season,
            episode);
        return null;
    }

    private static string BuildWatchReferer(string animeId, string season, string episode)
    {
        return season == "0" && episode == "0"
                   ? $"https://deokwave.com/watch/{animeId}/"
                   : $"https://deokwave.com/watch/{animeId}/season/{season}/episode/{episode}";
    }

    private async Task<List<SearchResult>> SearchV1Async(string escapedQuery, CancellationToken ct)
    {
        var       url      = $"{BaseUrl}/api/v1/animes/search/?q={escapedQuery}";
        using var response = await _httpClient.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("animes", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<SearchResult>();
        foreach (var item in arr.EnumerateArray())
        {
            var id   = item.TryGetProperty("animeid", out var idProp) ? idProp.GetString() : null;
            var name = item.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var type = item.TryGetProperty("type", out var tProp) ? tProp.GetString() : null;
            var isMovie = "movie".Equals(type, StringComparison.OrdinalIgnoreCase)
                          || AnimeDetails.IsMovieTitle(name);

            list.Add(new SearchResult
            {
                Id           = id,
                Title        = name,
                PosterUrl    = item.TryGetProperty("poster", out var pProp) ? pProp.GetString() : null,
                Url          = $"{BaseUrl}/anime/{id}/",
                ProviderName = Name,
                Type         = ProviderType.Anime,
                Format       = isMovie ? ContentFormat.Movie : ContentFormat.Tv,
                Year         = item.TryGetProperty("year", out var yProp) ? yProp.GetString() : null
            });
        }

        return list;
    }

    private async Task<List<SearchResult>> SearchLegacyAsync(string escapedQuery, CancellationToken ct)
    {
        try
        {
            var       url      = $"{BaseUrl}/search_api.php?q={escapedQuery}";
            using var response = await _httpClient.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            var       json = await response.Content.ReadAsStringAsync(ct);
            using var doc  = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("animes", out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var list = new List<SearchResult>();
            foreach (var item in arr.EnumerateArray())
            {
                var id = item.TryGetProperty("animeid", out var idProp) ? idProp.GetString() : null;
                var name = item.TryGetProperty("name", out var n1)
                               ? n1.GetString()
                               : item.TryGetProperty("name_english", out var n2)
                                   ? n2.GetString()
                                   : null;
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                list.Add(new SearchResult
                {
                    Id           = id,
                    Title        = name,
                    PosterUrl    = item.TryGetProperty("poster", out var pProp) ? pProp.GetString() : null,
                    Url          = $"{BaseUrl}/anime/{id}/",
                    ProviderName = Name,
                    Type         = ProviderType.Anime,
                    Format       = AnimeDetails.IsMovieTitle(name) ? ContentFormat.Movie : ContentFormat.Tv
                });
            }

            return list;
        }
        catch
        {
            return [];
        }
    }

    private static void ExtractLd(JsonElement el,
        ref string                            title,
        ref string                            summary,
        ref string?                           poster,
        List<string>                          genres,
        ref bool                              isMovie)
    {
        var type = el.TryGetProperty("@type", out var tp) ? tp.GetString() : null;
        if (!string.IsNullOrWhiteSpace(type) && type.Contains("Movie", StringComparison.OrdinalIgnoreCase))
        {
            isMovie = true;
        }

        if (el.TryGetProperty("name", out var n) && !string.IsNullOrWhiteSpace(n.GetString()))
        {
            title = n.GetString()!;
        }

        if (el.TryGetProperty("description", out var d) && string.IsNullOrWhiteSpace(summary))
        {
            summary = d.GetString() ?? "";
        }

        if (el.TryGetProperty("image", out var img))
        {
            poster ??= img.ValueKind == JsonValueKind.Array
                           ? img.EnumerateArray().FirstOrDefault().GetString()
                           : img.GetString();
        }

        if (el.TryGetProperty("genre", out var g))
        {
            if (g.ValueKind == JsonValueKind.Array)
            {
                foreach (var x in g.EnumerateArray())
                {
                    var v = x.ValueKind == JsonValueKind.Object && x.TryGetProperty("display_name", out var dn)
                                ? dn.GetString()
                                : x.GetString();
                    if (!string.IsNullOrWhiteSpace(v))
                    {
                        genres.Add(v);
                    }
                }
            }
            else if (!string.IsNullOrWhiteSpace(g.GetString()))
            {
                genres.Add(g.GetString()!);
            }
        }
    }

    private static Dictionary<string, List<int>> ParseAllSezonlar(string html)
    {
        var result = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var idx = html.IndexOf("allSezonlar", StringComparison.Ordinal);
            if (idx == -1)
            {
                return result;
            }

            var eq = html.IndexOf('=', idx);
            if (eq != -1)
            {
                for (var j = eq + 1; j < html.Length && j < eq + 50; j++)
                {
                    var bc = html[j];
                    if (char.IsWhiteSpace(bc))
                    {
                        continue;
                    }

                    if (bc == '[')
                    {
                        return result;
                    }

                    if (bc == '{')
                    {
                        break;
                    }

                    break;
                }
            }

            var braceStart = html.IndexOf('{', idx);
            if (braceStart == -1)
            {
                return result;
            }

            var depth = 0;
            var inStr = false;
            var esc   = false;
            var end   = -1;
            for (var i = braceStart; i < html.Length; i++)
            {
                var c = html[i];
                if (inStr)
                {
                    if (esc)
                    {
                        esc = false;
                    }
                    else if (c == '\\')
                    {
                        esc = true;
                    }
                    else if (c == '"')
                    {
                        inStr = false;
                    }

                    continue;
                }

                if (c == '"')
                {
                    inStr = true;
                }
                else if (c == '{')
                {
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        end = i;
                        break;
                    }
                }
            }

            if (end == -1)
            {
                return result;
            }

            var       json = html[braceStart..(end + 1)];
            using var doc  = JsonDocument.Parse(json);
            foreach (var seasonProp in doc.RootElement.EnumerateObject())
            {
                var eps = new List<int>();
                if (seasonProp.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var epProp in seasonProp.Value.EnumerateObject())
                    {
                        if (int.TryParse(epProp.Name, out var epNum))
                        {
                            eps.Add(epNum);
                        }
                    }
                }

                result[seasonProp.Name] = eps;
            }
        }
        catch
        {
            // ignored
        }

        return result;
    }

    private static int ParseInt(string s)
    {
        return int.TryParse(s, out var v) ? v : 1;
    }

    private static (string animeId, string season, string episode) ParseEpisodeId(string episodeId)
    {
        var parts   = episodeId.Split(['|', '/', ':'], StringSplitOptions.RemoveEmptyEntries);
        var id      = parts.Length > 0 ? parts[0].Trim().Trim('/') : episodeId;
        var season  = parts.Length > 1 ? parts[1] : "1";
        var episode = parts.Length > 2 ? parts[2] : "1";
        return (id, season, episode);
    }

    private async Task<List<(string Name, string? Extra)>> FetchFansubsAsync(string episodeId, CancellationToken ct)
    {
        var (animeId, season, episode) = ParseEpisodeId(episodeId);
        using var doc = await FetchVideoInfoDocAsync(animeId, season, episode, ct);
        if (doc is null)
        {
            return [];
        }

        if (!doc.RootElement.TryGetProperty("fansubs", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<(string, string?)>();
        foreach (var f in arr.EnumerateArray())
        {
            var name  = f.TryGetProperty("name", out var n) ? n.GetString() : null;
            var extra = f.TryGetProperty("extra", out var e) ? e.GetString() : null;
            if (!string.IsNullOrWhiteSpace(name))
            {
                list.Add((name, extra));
            }
        }

        return list;
    }

    private async Task<string?> FetchVideoTokenAsync(string animeId,
        string                                              season,
        string                                              episode,
        CancellationToken                                   ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/api/v1/video/token/");
        req.Headers.TryAddWithoutValidation("Referer", BuildWatchReferer(animeId, season, episode));
        using var resp = await _httpClient.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }

        var       json = await resp.Content.ReadAsStringAsync(ct);
        using var doc  = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("token", out var t) ? t.GetString() : null;
    }

    private static int RankQuality(string q)
    {
        var num = new string(q.Where(char.IsDigit).ToArray());
        return int.TryParse(num, out var v) ? v : 0;
    }

    private static string? ParseSibnetId(JsonElement fansub)
    {
        if (fansub.TryGetProperty("key", out var keyProp))
        {
            var key = keyProp.GetString() ?? "";
            var m   = SibnetKeyRegex().Match(key);
            if (m.Success)
            {
                return m.Groups[1].Value;
            }
        }

        if (fansub.TryGetProperty("videoid", out var vidProp))
        {
            var vid = vidProp.GetString() ?? "";
            if (!string.IsNullOrWhiteSpace(vid) && vid.All(char.IsDigit))
            {
                return vid;
            }
        }

        return null;
    }

    [GeneratedRegex(@"sibnet_(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex SibnetKeyRegex();

    [GeneratedRegex(@"/watch/([A-Fa-f0-9]{7})/season/(\d+)/episode/(\d+)", RegexOptions.Compiled)]
    private static partial Regex WatchUrlRegex();
}
