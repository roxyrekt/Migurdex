using Microsoft.Extensions.Logging;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Migurdex.Plugins.Turkanime;

public partial class TurkanimeProvider : IAnimeProvider
{
    private const string TursoEndpoint = "https://turkanime-roxyrekt.aws-eu-west-1.turso.io/v2/pipeline";

    private const string TursoAuthToken =
        "eyJhbGciOiJFZERTQSIsInR5cCI6IkpXVCJ9.eyJhIjoicm8iLCJpYXQiOjE3OTAwOTU3ODEsImlkIjoiMDFhMGMwNmYtYWUwMS03MTNjLWJkZmYtM2IyYTJhNWI3OTg0Iiwia2lkIjoiak9LeUlDamRqTVZjMllyOENLU2tsMUt5Y0NXM3RUSEk3MXZDbVJFU1VJdyIsInJpZCI6ImJjNDc2NTYxLTE1MTgtNDVkYy05NWYxLWI3NTZhOWQ0OTBhNSJ9.6Vroz6uIDqlROXPcwJlqPN4mtVWOFwgGWQzsblx63-BkCNJlMSAEG4yZNsU2QzMzhTlmcb_N_tsySpE2wDexDA";

    private readonly HttpClient                 _httpClient;
    private readonly ILogger<TurkanimeProvider> _logger;

    private static          List<AnimeCatalogItem>? _catalogCache;
    private static readonly SemaphoreSlim           _catalogLock = new(1, 1);

    public TurkanimeProvider(ISharedBridge bridge, ILogger<TurkanimeProvider> logger)
    {
        _httpClient = bridge.CreateHttpClient();
        _logger     = logger;
    }

    public string       Name    => "TurkAnime";
    public string       BaseUrl => "https://turkanime.tv";
    public ProviderType Type    => ProviderType.Anime;

    public async Task<List<SearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        try
        {
            await EnsureCatalogLoadedAsync(cancellationToken);

            var q = query.Trim();

            if (_catalogCache is { Count: > 0 })
            {
                var normalizedQ = NormalizeTitle(q);

                var matches = _catalogCache
                              .Where(a => a.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                                          || a.Slug.Contains(q, StringComparison.OrdinalIgnoreCase)
                                          || (!string.IsNullOrEmpty(a.EnglishTitle)
                                              && (a.EnglishTitle.Contains(q, StringComparison.OrdinalIgnoreCase)
                                                  || (!string.IsNullOrEmpty(normalizedQ)
                                                      && NormalizeTitle(a.EnglishTitle)
                                                          .Contains(normalizedQ, StringComparison.Ordinal))))
                                          || (a.Synonyms != null
                                              && a.Synonyms.Any(s => s.Contains(q, StringComparison.OrdinalIgnoreCase)
                                                                     || (!string.IsNullOrEmpty(normalizedQ)
                                                                         && NormalizeTitle(s)
                                                                             .Contains(
                                                                                 normalizedQ,
                                                                                 StringComparison.Ordinal)))))
                              .OrderBy(a => RankCatalogMatch(a, q, normalizedQ))
                              .ThenBy(a => a.Id)
                              .Take(30)
                              .Select(MapCatalogItem)
                              .ToList();

                if (matches.Count > 0)
                {
                    return matches;
                }
            }

            var ftsRows = await QueryAsync(
                              "SELECT id, baslik, slug, bolum_sayisi FROM anime WHERE id IN (SELECT rowid FROM anime_fts WHERE anime_fts MATCH ?) LIMIT 30;",
                              [("text", $"{q}*")],
                              cancellationToken);

            if (ftsRows.Count == 0)
            {
                ftsRows = await QueryAsync(
                              "SELECT id, baslik, slug, bolum_sayisi FROM anime WHERE baslik LIKE ? LIMIT 30;",
                              [("text", $"%{q}%")],
                              cancellationToken);
            }

            if (ftsRows.Count > 0)
            {
                var bySlug = _catalogCache?.ToDictionary(a => a.Slug, StringComparer.OrdinalIgnoreCase);

                return ftsRows.Select(r =>
                              {
                                  var title = r.GetValueOrDefault("baslik", "");
                                  var slug  = r.GetValueOrDefault("slug", "");

                                  if (bySlug != null && bySlug.TryGetValue(slug, out var cached))
                                  {
                                      return MapCatalogItem(cached);
                                  }

                                  return new SearchResult
                                  {
                                      Id           = slug,
                                      Title        = title,
                                      Url          = $"{BaseUrl}/anime/{slug}",
                                      PosterUrl    = "",
                                      ProviderName = Name,
                                      Type         = ProviderType.Anime,
                                      Format = AnimeDetails.IsMovieTitle(title)
                                                   ? ContentFormat.Movie
                                                   : ContentFormat.Tv
                                  };
                              })
                              .ToList();
            }

            return await SearchViaAniListAsync(q, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TurkAnime search failed for query: {Query}", query);
            return [];
        }
    }

    public async Task<AnimeDetails> GetDetailsAsync(string animeId, CancellationToken cancellationToken = default)
    {
        try
        {
            var animeRows = await QueryAsync(
                                """
                                SELECT a.id, a.baslik, a.slug, a.bolum_sayisi,
                                       m.english_title, m.poster_url, m.summary,
                                       m.synonyms_json, m.genres_json,
                                       m.anilist_id, m.mal_id, m.year, m.score
                                FROM anime a LEFT JOIN anime_meta m ON m.anime_id = a.id
                                WHERE a.slug = ? OR a.id = ? LIMIT 1;
                                """,
                                [("text", animeId), ("text", animeId)],
                                cancellationToken);

            if (animeRows.Count == 0)
            {
                throw new InvalidOperationException($"Anime not found: {animeId}");
            }

            var anime     = animeRows[0];
            var numericId = anime.GetValueOrDefault("id", "0");
            var title     = anime.GetValueOrDefault("baslik", animeId);
            var slug      = anime.GetValueOrDefault("slug", animeId);

            var englishTitle = anime.GetValueOrDefault("english_title", "");
            var posterUrl    = anime.GetValueOrDefault("poster_url", "");
            var summary      = anime.GetValueOrDefault("summary", "");
            var synonyms     = ParseJsonList(anime.GetValueOrDefault("synonyms_json", ""));
            var anilistId    = anime.GetValueOrDefault("anilist_id", "");
            var malId        = anime.GetValueOrDefault("mal_id", "");

            var isMovie =
                AnimeDetails.IsMovieTitle(title) || slug.Contains("movie", StringComparison.OrdinalIgnoreCase);
            var seasonNum = isMovie ? 1 : AnimeDetails.ParseSeasonNumber(title);

            var details = new AnimeDetails
            {
                Title             = title,
                EnglishTitle      = string.IsNullOrWhiteSpace(englishTitle) ? null : englishTitle,
                RomajiTitle       = title,
                PosterUrl         = string.IsNullOrWhiteSpace(posterUrl) ? null : posterUrl,
                AlternativeTitles = synonyms,
                Summary           = summary,
                Format            = isMovie ? ContentFormat.Movie : ContentFormat.Tv,
                SeasonMappings =
                [
                    new SeasonMapping
                    {
                        SeasonNumber = seasonNum,
                        AniListId = string.IsNullOrWhiteSpace(anilistId) || anilistId == "0"
                                        ? null
                                        : anilistId,
                        MyAnimeListId = string.IsNullOrWhiteSpace(malId) || malId == "0" ? null : malId
                    }
                ]
            };

            var episodeRows = await QueryAsync(
                                  "SELECT id, slug, ad FROM bolum WHERE anime_id = ? ORDER BY id ASC;",
                                  [("integer", numericId)],
                                  cancellationToken);

            var epNum = 1;
            foreach (var epRow in episodeRows)
            {
                var epId   = epRow.GetValueOrDefault("id", "");
                var epSlug = epRow.GetValueOrDefault("slug", "");
                var epAd   = epRow.GetValueOrDefault("ad", "");

                var num   = epNum;
                var match = EpisodeRegex().Match(epAd);
                if (match.Success && int.TryParse(match.Groups[1].Value, out var parsed))
                {
                    num = parsed;
                }

                details.Episodes.Add(new Episode
                {
                    Id     = string.IsNullOrEmpty(epId) ? epSlug : epId,
                    Title  = string.IsNullOrEmpty(epAd) ? $"{num}. Bölüm" : epAd,
                    Number = num,
                    Season = seasonNum
                });

                epNum++;
            }

            details.Episodes = details.Episodes.OrderBy(e => e.Number).ToList();
            return details;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get details for anime {AnimeId}", animeId);
            throw;
        }
    }

    public async Task<List<string>> GetGroupsAsync(string episodeId, CancellationToken cancellationToken = default)
    {
        try
        {
            var rows = await QueryAsync(
                           "SELECT DISTINCT fansub FROM link WHERE (bolum_id = ? OR bolum_id IN (SELECT id FROM bolum WHERE slug = ?)) AND tip = 'url' AND fansub IS NOT NULL AND fansub != '';",
                           [("text", episodeId), ("text", episodeId)],
                           cancellationToken);

            var groups = rows
                         .Select(r => r.GetValueOrDefault("fansub", "").Trim())
                         .Where(f => !string.IsNullOrEmpty(f))
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .ToList();

            if (groups.Count == 0)
            {
                groups.Add("Varsayılan");
            }

            return groups;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get fansub groups for episode {EpisodeId}", episodeId);
            return ["Varsayılan"];
        }
    }

    public async Task<List<VideoSource>> GetVideoSourcesAsync(
        string            episodeId,
        string?           group             = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            List<Dictionary<string, string>> rows;
            if (!string.IsNullOrWhiteSpace(group) && !group.Equals("Varsayılan", StringComparison.OrdinalIgnoreCase))
            {
                rows = await QueryAsync(
                           "SELECT player, fansub, deger FROM link WHERE (bolum_id = ? OR bolum_id IN (SELECT id FROM bolum WHERE slug = ?)) AND tip = 'url' AND fansub = ?;",
                           [("text", episodeId), ("text", episodeId), ("text", group)],
                           cancellationToken);
            }
            else
            {
                rows = await QueryAsync(
                           "SELECT player, fansub, deger FROM link WHERE (bolum_id = ? OR bolum_id IN (SELECT id FROM bolum WHERE slug = ?)) AND tip = 'url';",
                           [("text", episodeId), ("text", episodeId)],
                           cancellationToken);
            }

            var sources  = new List<VideoSource>();
            var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var r in rows)
            {
                var rawUrl   = r.GetValueOrDefault("deger", "").Trim();
                var cleanUrl = CleanVideoUrl(rawUrl);
                if (string.IsNullOrEmpty(cleanUrl) || !seenUrls.Add(cleanUrl))
                {
                    continue;
                }

                var player = r.GetValueOrDefault("player", "Video");
                var fansub = r.GetValueOrDefault("fansub", group ?? "Varsayılan");

                var serverName = NormalizeServerName(player);

                sources.Add(new VideoSource
                {
                    Hoster  = serverName,
                    Quality = "Bilinmiyor",
                    Url     = cleanUrl,
                    Group   = string.IsNullOrWhiteSpace(fansub) ? "Varsayılan" : fansub,
                    Type    = VideoType.Embed
                });
            }

            return sources;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get video sources for episode {EpisodeId}", episodeId);
            return [];
        }
    }

    private async Task<List<SearchResult>> SearchViaAniListAsync(string query,
        CancellationToken                                               cancellationToken = default)
    {
        try
        {
            if (_catalogCache is not { Count: > 0 })
            {
                return [];
            }

            var mediaGroups = await ResolveTitlesViaAniListAsync(query, cancellationToken);
            if (mediaGroups.Count == 0)
            {
                return [];
            }

            var normalizedQuery = NormalizeTitle(query);

            var seen           = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var results        = new List<SearchResult>();
            var usedCandidates = new List<string>();

            foreach (var candidates in mediaGroups)
            {
                if (results.Count >= 30)
                {
                    break;
                }

                var queryMatchesMedia = !string.IsNullOrEmpty(normalizedQuery)
                                        && candidates.Any(c => !string.IsNullOrEmpty(c.Normalized)
                                                               && (c.Normalized.Contains(normalizedQuery,
                                                                       StringComparison.Ordinal)
                                                                   || normalizedQuery.Contains(
                                                                       c.Normalized,
                                                                       StringComparison.Ordinal)));

                if (!queryMatchesMedia)
                {
                    continue;
                }

                foreach (var candidate in candidates)
                {
                    if (candidate.Normalized.Length < 4)
                    {
                        continue;
                    }

                    usedCandidates.Add(candidate.Normalized);
                    foreach (var item in _catalogCache)
                    {
                        if (results.Count >= 30)
                        {
                            break;
                        }

                        if (string.IsNullOrWhiteSpace(item.Title) || string.IsNullOrWhiteSpace(item.Slug))
                        {
                            continue;
                        }

                        var normalizedTitle = NormalizeTitle(item.Title);
                        var normalizedSlug  = NormalizeTitle(item.Slug.Replace('-', ' '));

                        if (string.IsNullOrEmpty(normalizedTitle) || string.IsNullOrEmpty(normalizedSlug))
                        {
                            continue;
                        }

                        if (normalizedTitle.Contains(candidate.Normalized, StringComparison.Ordinal)
                            || normalizedSlug.Contains(candidate.Normalized, StringComparison.Ordinal))
                        {
                            if (seen.Add(item.Slug))
                            {
                                results.Add(MapCatalogItem(item));
                            }
                        }
                    }

                    if (results.Count >= 30)
                    {
                        break;
                    }
                }

                if (results.Count >= 30)
                {
                    break;
                }
            }

            return results
                   .OrderBy(r => RankFallbackMatch(r.Title, usedCandidates))
                   .ThenBy(r => r.Id, StringComparer.OrdinalIgnoreCase)
                   .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TurkAnime AniList fallback failed for query: {Query}", query);

            return [];
        }
    }

    private static int RankFallbackMatch(string title, List<string> candidates)
    {
        var normalizedTitle = NormalizeTitle(title);
        if (string.IsNullOrEmpty(normalizedTitle))
        {
            return 3;
        }

        foreach (var candidate in candidates)
        {
            if (normalizedTitle.Equals(candidate, StringComparison.Ordinal))
            {
                return 0;
            }
        }

        foreach (var candidate in candidates)
        {
            if (normalizedTitle.StartsWith(candidate, StringComparison.Ordinal))
            {
                return 1;
            }
        }

        return 2;
    }

    private async Task<List<List<(string Raw, string Normalized)>>> ResolveTitlesViaAniListAsync(
        string            query,
        CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(8));

        const string gql = """
                           query ($search: String) {
                             Page (perPage: 5) {
                               media (search: $search, type: ANIME) {
                                 title { romaji english native }
                                 synonyms
                               }
                             }
                           }
                           """;

        var payload = JsonSerializer.Serialize(new
        {
            query = gql,
            variables = new
            {
                search = query
            }
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://graphql.anilist.co");
        request.Headers.Add("User-Agent", "Migurdex/1.0");
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, cts.Token);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var       doc    = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token);

        if (!doc.RootElement.TryGetProperty("data", out var data)
            || !data.TryGetProperty("Page", out var page)
            || !page.TryGetProperty("media", out var mediaArr)
            || mediaArr.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var groups = new List<List<(string Raw, string Normalized)>>();

        foreach (var media in mediaArr.EnumerateArray())
        {
            if (media.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var titles = new List<(string Raw, string Normalized)>();

            void AddTitle(string? raw)
            {
                if (string.IsNullOrWhiteSpace(raw))
                {
                    return;
                }

                var normalized = NormalizeTitle(raw);
                if (!string.IsNullOrEmpty(normalized)
                    && titles.All(t => !t.Normalized.Equals(normalized, StringComparison.Ordinal)))
                {
                    titles.Add((raw, normalized));
                }
            }

            if (media.TryGetProperty("title", out var titleObj) && titleObj.ValueKind == JsonValueKind.Object)
            {
                AddTitle(titleObj.TryGetProperty("romaji", out var romaji) ? romaji.GetString() : null);
                AddTitle(titleObj.TryGetProperty("english", out var english) ? english.GetString() : null);
                AddTitle(titleObj.TryGetProperty("native", out var native) ? native.GetString() : null);
            }

            if (media.TryGetProperty("synonyms", out var synonyms) && synonyms.ValueKind == JsonValueKind.Array)
            {
                foreach (var syn in synonyms.EnumerateArray().Take(5))
                {
                    AddTitle(syn.GetString());
                }
            }

            if (titles.Count > 0)
            {
                groups.Add(titles);
            }
        }

        return groups;
    }

    private static string NormalizeTitle(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(s.Length);
        foreach (var c in s.ToLowerInvariant())
        {
            sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }

        return string.Join(" ", sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private async Task EnsureCatalogLoadedAsync(CancellationToken ct)
    {
        if (_catalogCache != null)
        {
            return;
        }

        await _catalogLock.WaitAsync(ct);
        try
        {
            if (_catalogCache != null)
            {
                return;
            }

            var rows = await QueryAsync(
                           """
                           SELECT a.id, a.baslik, a.slug, a.bolum_sayisi,
                                  m.english_title, m.year, m.poster_url, m.score,
                                  m.genres_json, m.synonyms_json,
                                  m.anilist_id, m.mal_id
                           FROM anime a LEFT JOIN anime_meta m ON m.anime_id = a.id
                           ORDER BY a.id ASC;
                           """,
                           null,
                           ct);
            if (rows.Count > 0)
            {
                _catalogCache = rows.Select(r => new AnimeCatalogItem(
                                                int.TryParse(r.GetValueOrDefault("id", "0"), out var id) ? id : 0,
                                                r.GetValueOrDefault("baslik", ""),
                                                r.GetValueOrDefault("slug", ""),
                                                int.TryParse(r.GetValueOrDefault("bolum_sayisi", "0"), out var cnt)
                                                    ? cnt
                                                    : 0,
                                                r.GetValueOrDefault("english_title", ""),
                                                r.GetValueOrDefault("year", ""),
                                                r.GetValueOrDefault("poster_url", ""),
                                                ParseJsonList(r.GetValueOrDefault("synonyms_json", "")),
                                                ParseJsonList(r.GetValueOrDefault("genres_json", "")),
                                                double.TryParse(r.GetValueOrDefault("score", ""),
                                                                System.Globalization.NumberStyles.Any,
                                                                System.Globalization.CultureInfo
                                                                      .InvariantCulture,
                                                                out var sc)
                                                    ? sc
                                                    : null,
                                                r.GetValueOrDefault("anilist_id", ""),
                                                r.GetValueOrDefault("mal_id", "")
                                            ))
                                    .ToList();

                _logger.LogInformation("TurkAnime catalog initialized with {Count} animes.", _catalogCache.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to prefetch TurkAnime catalog, will query remote Turso database directly.");
        }
        finally
        {
            _catalogLock.Release();
        }
    }

    private async Task<List<Dictionary<string, string>>> QueryAsync(
        string                         sql,
        (string Type, string Value)[]? args = null,
        CancellationToken              ct   = default)
    {
        var stmtObj = new Dictionary<string, object>
        {
            ["sql"] = sql
        };

        if (args is { Length: > 0 })
        {
            stmtObj["args"] = args.Select(a => new Dictionary<string, string>
                                  {
                                      ["type"]  = a.Type,
                                      ["value"] = a.Value
                                  })
                                  .ToList();
        }

        var payload = new
        {
            requests = new[]
            {
                new
                {
                    type = "execute",
                    stmt = stmtObj
                }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, TursoEndpoint);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TursoAuthToken);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Turso query failed with status code: {StatusCode}", response.StatusCode);
            return [];
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var       doc    = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        if (!doc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
        {
            return [];
        }

        var firstRes = results[0];
        if (!firstRes.TryGetProperty("response", out var respObj)
            || !respObj.TryGetProperty("result", out var resultObj))
        {
            return [];
        }

        var cols = new List<string>();
        if (resultObj.TryGetProperty("cols", out var colsArr))
        {
            foreach (var col in colsArr.EnumerateArray())
            {
                cols.Add(col.GetProperty("name").GetString() ?? "");
            }
        }

        var rowsList = new List<Dictionary<string, string>>();
        if (resultObj.TryGetProperty("rows", out var rowsArr))
        {
            foreach (var rowArr in rowsArr.EnumerateArray())
            {
                var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var idx  = 0;
                foreach (var cell in rowArr.EnumerateArray())
                {
                    if (idx < cols.Count)
                    {
                        var val = "";
                        if (cell.TryGetProperty("value", out var v))
                        {
                            val = v.ValueKind switch
                            {
                                JsonValueKind.String => v.GetString() ?? "",
                                JsonValueKind.Null   => "",
                                _                    => v.ToString()
                            };
                        }

                        dict[cols[idx]] = val;
                    }

                    idx++;
                }

                rowsList.Add(dict);
            }
        }

        return rowsList;
    }

    private static string CleanVideoUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        url = url.Trim().Trim('"').Replace("\\/", "/");

        if (url.Contains("href.li/?", StringComparison.OrdinalIgnoreCase))
        {
            var idx = url.IndexOf("href.li/?", StringComparison.OrdinalIgnoreCase);
            url = url[(idx + 9)..];
        }

        if (url.StartsWith("https:https://", StringComparison.OrdinalIgnoreCase))
        {
            url = url["https:".Length..];
        }
        else if (url.StartsWith("http:https://", StringComparison.OrdinalIgnoreCase))
        {
            url = url["http:".Length..];
        }
        else if (url.StartsWith("//"))
        {
            url = "https:" + url;
        }

        return url;
    }

    private static string NormalizeServerName(string player)
    {
        return player.ToUpperInvariant() switch
        {
            "SIBNET"                   => "Sibnet",
            "MAIL" or "MAIL.RU"        => "Mail.ru",
            "ODNOKLASSNIKI" or "OK.RU" => "Okru",
            "GDRIVE" or "GOOGLE DRIVE" => "GoogleDrive",
            "MP4UPLOAD"                => "Mp4Upload",
            "UQLOAD"                   => "Uqload",
            "SENDVID"                  => "Sendvid",
            "VOE"                      => "Voe",
            "VK"                       => "VK",
            "DOODSTREAM" or "DOOD"     => "DoodStream",
            "FILEMOON"                 => "Filemoon",
            "YOURUPLOAD"               => "YourUpload",
            "VUDEA" or "VUDEO"         => "Vudeo",
            _                          => player
        };
    }

    [GeneratedRegex(@"(\d+)\.\s*Bölüm", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeRegex();

    private static int RankCatalogMatch(AnimeCatalogItem a, string query, string normalizedQuery)
    {
        if (a.Title.Equals(query, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrEmpty(normalizedQuery)
                && NormalizeTitle(a.Title).Equals(normalizedQuery, StringComparison.Ordinal)))
        {
            return 0;
        }

        if (a.Title.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (a.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrEmpty(normalizedQuery)
                && NormalizeTitle(a.Title).Contains(normalizedQuery, StringComparison.Ordinal)))
        {
            return 2;
        }

        return 3;
    }

    private SearchResult MapCatalogItem(AnimeCatalogItem a)
    {
        return new SearchResult
        {
            Id           = a.Slug,
            Title        = a.Title,
            Url          = $"{BaseUrl}/anime/{a.Slug}",
            PosterUrl    = string.IsNullOrWhiteSpace(a.PosterUrl) ? "" : a.PosterUrl,
            EnglishTitle = string.IsNullOrWhiteSpace(a.EnglishTitle) ? null : a.EnglishTitle,
            RomajiTitle  = a.Title,
            Year         = string.IsNullOrWhiteSpace(a.Year) ? null : a.Year,
            Score        = a.Score,
            Categories   = a.Genres is { Count: > 0 } ? a.Genres : null,
            ProviderName = Name,
            Type         = ProviderType.Anime,
            Format       = AnimeDetails.IsMovieTitle(a.Title) ? ContentFormat.Movie : ContentFormat.Tv
        };
    }

    private static List<string> ParseJsonList(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return doc.RootElement.EnumerateArray()
                      .Select(e => e.GetString() ?? "")
                      .Where(s => !string.IsNullOrWhiteSpace(s))
                      .ToList();
        }
        catch
        {
            return [];
        }
    }

    private record AnimeCatalogItem(
        int           Id,
        string        Title,
        string        Slug,
        int           EpisodeCount,
        string        EnglishTitle = "",
        string        Year         = "",
        string        PosterUrl    = "",
        List<string>? Synonyms     = null,
        List<string>? Genres       = null,
        double?       Score        = null,
        string        AniListId    = "",
        string        MalId        = "");
}
