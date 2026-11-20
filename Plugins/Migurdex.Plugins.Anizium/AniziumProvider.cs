using Microsoft.Extensions.Logging;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Migurdex.Plugins.Anizium;

public class AniziumProvider : IAnimeProvider
{
    private const string CoBaseUrl    = "https://anizium.co";
    private const string DeBaseUrl    = "https://anizium.de";
    private const string CoApiUrl     = "https://api.anizium.co";
    private const string DeApiUrl     = "https://api.anizium.de";
    private const string CoPlayerHost = "https://x.anizium.co";
    private const string DePlayerHost = "https://x.anizium.de";
    private const string TokenKey     = "hlxjl1c2w281ax473rt1ofgrvhyjvi";

    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/147.0.0.0 Safari/537.36";

    private static readonly TimeZoneInfo _istanbulTimeZone = ResolveIstanbulTimeZone();

    private readonly HttpClient               _httpClient;
    private readonly ILogger<AniziumProvider> _logger;

    private volatile bool _useDeFallback;

    public AniziumProvider(ISharedBridge bridge, ILogger<AniziumProvider> logger)
    {
        _httpClient = bridge.CreateHttpClient(o =>
        {
            o.Emulation          = BrowserEmulation.Chrome147;
            o.SkipDefaultHeaders = true;
        });
        _logger = logger;
    }

    private string ApiUrl     => _useDeFallback ? DeApiUrl : CoApiUrl;
    private string PlayerHost => _useDeFallback ? DePlayerHost : CoPlayerHost;
    private string SiteParam  => _useDeFallback ? "world" : "main";

    public string       Name    => "Anizium";
    public string       BaseUrl => _useDeFallback ? DeBaseUrl : CoBaseUrl;
    public ProviderType Type    => ProviderType.Anime;

    public async Task<List<SearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        try
        {
            const int maxPages = 25;
            var       results  = new List<SearchResult>();
            var       page     = 1;

            while (page <= maxPages)
            {
                var       path     = $"/page/search?value={Uri.EscapeDataString(query)}&page={page}";
                using var response = await SendApiGetAsync(path, cancellationToken).ConfigureAwait(false);
                if (response is null || !response.IsSuccessStatusCode)
                {
                    if (page == 1)
                    {
                        response?.EnsureSuccessStatusCode();
                        throw new HttpRequestException("Anizium arama yanıtı alınamadı.");
                    }

                    break;
                }

                var       json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var doc  = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("page", out var pageObj)
                    || pageObj.ValueKind != JsonValueKind.Object)
                {
                    break;
                }

                var notDisplayed = TryGetInt(pageObj, "not_displayed") ?? 0;
                var nextPage     = TryGetInt(pageObj, "next_page");

                if (!pageObj.TryGetProperty("data", out var dataArray)
                    || dataArray.ValueKind != JsonValueKind.Array
                    || dataArray.GetArrayLength() == 0)
                {
                    break;
                }

                foreach (var item in dataArray.EnumerateArray())
                {
                    var id = TryGetStringOrNumber(item, "ID");
                    if (string.IsNullOrWhiteSpace(id))
                    {
                        continue;
                    }

                    var title         = TryGetString(item, "name") ?? "";
                    var japaneseTitle = TryGetString(item, "name_jp");
                    var typeStr       = TryGetString(item, "type") ?? "";
                    var isMovie = "movie".Equals(typeStr, StringComparison.OrdinalIgnoreCase)
                                  || AnimeDetails.IsMovieTitle(title);
                    var year  = TryGetInt(item, "release_year");
                    var score = TryGetDouble(item, "imdb_point");

                    results.Add(new SearchResult
                    {
                        Id            = id,
                        Title         = title,
                        JapaneseTitle = string.IsNullOrWhiteSpace(japaneseTitle) ? null : japaneseTitle,
                        PosterUrl     = NormalizeDomainUrl(TryGetString(item, "poster")),
                        Url           = $"{BaseUrl}/anime/{id}",
                        ProviderName  = Name,
                        Type          = ProviderType.Anime,
                        Format        = isMovie ? ContentFormat.Movie : ContentFormat.Tv,
                        Year          = year?.ToString(CultureInfo.InvariantCulture),
                        Score         = score
                    });
                }

                if (notDisplayed <= 0 && nextPage is null)
                {
                    break;
                }

                page = nextPage ?? page + 1;
            }

            return results;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Anizium search failed");
            throw;
        }
    }

    public async Task<AnimeDetails> GetDetailsAsync(string animeId, CancellationToken cancellationToken = default)
    {
        var       path     = $"/anime/get?id={Uri.EscapeDataString(animeId)}";
        using var response = await SendApiGetAsync(path, cancellationToken).ConfigureAwait(false);

        if (response is null || !response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"get anime details failed: {response?.StatusCode}");
        }

        var       json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var doc  = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("API returned no data in get anime response");
        }

        var isMovie = "movie".Equals(TryGetString(data, "type"), StringComparison.OrdinalIgnoreCase);
        var tmdbId  = TryGetStringOrNumber(data, "tmdb_id");
        var title   = TryGetString(data, "name") ?? animeId;

        var japaneseTitle = TryGetString(data, "name_jp");
        if (string.IsNullOrWhiteSpace(japaneseTitle))
        {
            japaneseTitle = null;
        }

        var altTitles = new List<string>();
        if (TryGetString(data, "name_tr") is { Length: > 0 } trName)
        {
            altTitles.Add(trName);
        }

        if (TryGetString(data, "name_short") is { Length: > 0 } shortName)
        {
            altTitles.Add(shortName);
        }

        var animeDetails = new AnimeDetails
        {
            Title         = title,
            JapaneseTitle = japaneseTitle,
            AlternativeTitles = altTitles
                                .Where(t => !t.Equals(title, StringComparison.OrdinalIgnoreCase)
                                            && (japaneseTitle == null
                                                || !t.Equals(japaneseTitle, StringComparison.OrdinalIgnoreCase)))
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToList(),
            Summary   = TryGetString(data, "overview") ?? "",
            PosterUrl = NormalizeDomainUrl(TryGetString(data, "poster")),
            Format    = isMovie ? ContentFormat.Movie : ContentFormat.Tv
        };

        if (isMovie)
        {
            animeDetails.SeasonMappings.Add(new SeasonMapping
            {
                SeasonNumber = 1,
                TmdbId       = tmdbId
            });

            animeDetails.Episodes.Add(new Episode
            {
                Id     = $"{animeId}|movie|movie",
                Title  = "Film",
                Number = 1,
                Season = 1
            });
        }
        else if (data.TryGetProperty("seasons", out var seasonsArray) && seasonsArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var season in seasonsArray.EnumerateArray())
            {
                var seasonNum = TryGetInt(season, "number") ?? 1;
                animeDetails.SeasonMappings.Add(new SeasonMapping
                {
                    SeasonNumber = seasonNum,
                    TmdbId       = tmdbId
                });

                if (season.TryGetProperty("episodes", out var epArray) && epArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var ep in epArray.EnumerateArray())
                    {
                        var epNum = TryGetInt(ep, "number") ?? 0;
                        animeDetails.Episodes.Add(new Episode
                        {
                            Id     = $"{animeId}|{seasonNum}|{epNum}",
                            Title  = TryGetString(ep, "name") ?? $"{epNum}. Bölüm",
                            Number = epNum,
                            Season = seasonNum
                        });
                    }
                }
            }
        }

        animeDetails.Normalize();
        return animeDetails;
    }

    public async Task<List<VideoSource>> GetVideoSourcesAsync(string episodeId,
        string?                                                      group             = null,
        CancellationToken                                            cancellationToken = default)
    {
        try
        {
            var parts = episodeId.Split(['|', '/', ':'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                return [];
            }

            var id      = parts[0];
            var season  = parts.Length > 1 ? parts[1] : "1";
            var episode = parts.Length > 2 ? parts[2] : "1";

            var isMovie = season.Equals("movie", StringComparison.OrdinalIgnoreCase)
                          || episode.Equals("movie", StringComparison.OrdinalIgnoreCase);

            var partUrl = isMovie
                              ? ""
                              : $"&season={Uri.EscapeDataString(season)}&episode={Uri.EscapeDataString(episode)}";

            var serverTasks = new[] { 1, 2 }.Select(async server =>
            {
                var       path = $"/anime/source?id={Uri.EscapeDataString(id)}&plan=standart{partUrl}&server={server}";
                using var response = await SendApiGetAsync(path, cancellationToken).ConfigureAwait(false);
                if (response is null || !response.IsSuccessStatusCode)
                {
                    return (Success: false, Status: response?.StatusCode, Sources: (List<VideoSource>) []);
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return (Success: true, Status: response.StatusCode,
                        Sources: ParseSourcesPayload(json, server));
            });

            var results = await Task.WhenAll(serverTasks).ConfigureAwait(false);
            if (results.All(r => !r.Success))
            {
                var status = results.Select(r => r.Status).FirstOrDefault(s => s.HasValue);
                throw new HttpRequestException($"Anizium kaynak isteği başarısız: {status}", null, status);
            }

            var sources = new List<VideoSource>();
            foreach (var r in results)
            {
                sources.AddRange(r.Sources);
            }

            return sources;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Anizium failed to get video sources for {EpisodeId}", episodeId);
            throw;
        }
    }

    private List<VideoSource> ParseSourcesPayload(string json, int server)
    {
        using var doc  = JsonDocument.Parse(json);
        var       root = doc.RootElement;

        if (!root.TryGetProperty("groups", out var groupsArray) || groupsArray.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var subtitlesList = new List<Subtitle>();
        if (root.TryGetProperty("subtitles", out var subsArray) && subsArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in subsArray.EnumerateArray())
            {
                var subUrl = NormalizeDomainUrl(TryGetString(s, "link"));
                if (string.IsNullOrWhiteSpace(subUrl))
                {
                    continue;
                }

                subtitlesList.Add(new Subtitle
                {
                    Language = TryGetString(s, "group") ?? "",
                    Label    = TryGetString(s, "name") ?? "",
                    Url      = subUrl,
                    Format   = "vtt"
                });
            }
        }

        var mediaHeaders = BuildMediaHeaders();
        var sources      = new List<VideoSource>();

        foreach (var g in groupsArray.EnumerateArray())
        {
            var groupName   = TryGetString(g, "name") ?? "Unknown";
            var groupType   = TryGetString(g, "type") ?? "hls";
            var isMp4       = groupType.Equals("mp4", StringComparison.OrdinalIgnoreCase);
            var rawLanguage = TryGetString(g, "group");

            var language = rawLanguage switch
            {
                "trdub"    => "Türkçe",
                "original" => "Japonca",
                "endub"    => "İngilizce",
                _          => string.IsNullOrWhiteSpace(rawLanguage) ? "Japonca" : rawLanguage
            };

            if (!g.TryGetProperty("items", out var itemsArray) || itemsArray.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var item in itemsArray.EnumerateArray())
            {
                var linkUrl = NormalizeDomainUrl(TryGetString(item, "link"));
                if (string.IsNullOrWhiteSpace(linkUrl))
                {
                    continue;
                }

                var rawQuality = TryGetStringOrNumber(item, "quality");
                var quality    = FormatQuality(rawQuality);
                var itemType   = TryGetString(item, "type");

                var videoType = isMp4
                                || "mp4".Equals(itemType, StringComparison.OrdinalIgnoreCase)
                                || linkUrl.Contains(".mp4", StringComparison.OrdinalIgnoreCase)
                                    ? VideoType.Mp4
                                    : VideoType.M3U8;

                sources.Add(new VideoSource
                {
                    Url       = linkUrl,
                    Quality   = quality,
                    Type      = videoType,
                    Hoster    = "Anizium",
                    Group     = $"{groupName} (Server {server})",
                    Language  = language,
                    Headers   = new Dictionary<string, string>(mediaHeaders, StringComparer.OrdinalIgnoreCase),
                    Subtitles = subtitlesList.Count > 0 ? subtitlesList : null
                });
            }
        }

        return sources;
    }

    private async Task<HttpResponseMessage?> SendApiGetAsync(string pathAndQuery, CancellationToken cancellationToken)
    {
        var wasDeFallback = _useDeFallback;
        var response = await SendSingleApiGetAsync(ApiUrl, BaseUrl, SiteParam, pathAndQuery, cancellationToken)
                           .ConfigureAwait(false);

        if (!wasDeFallback && response.StatusCode == HttpStatusCode.Forbidden)
        {
            response.Dispose();
            if (!_useDeFallback)
            {
                _useDeFallback = true;
                _logger.LogInformation("Anizium .co returned 403 Forbidden, falling back to anizium.de");
            }

            response = await SendSingleApiGetAsync(DeApiUrl, DeBaseUrl, "world", pathAndQuery, cancellationToken)
                           .ConfigureAwait(false);
        }

        return response;
    }

    private async Task<HttpResponseMessage> SendSingleApiGetAsync(
        string            apiHost,
        string            siteBaseUrl,
        string            siteParam,
        string            pathAndQuery,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{apiHost}{pathAndQuery}");
        request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
        request.Headers.TryAddWithoutValidation("Origin", siteBaseUrl);
        request.Headers.TryAddWithoutValidation("Referer", $"{siteBaseUrl}/");
        request.Headers.TryAddWithoutValidation("Cf-Control", CreateCfControlHeader());
        request.Headers.TryAddWithoutValidation("device", "browser");
        request.Headers.TryAddWithoutValidation("language", "tr");
        request.Headers.TryAddWithoutValidation("site", siteParam);

        return await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private Dictionary<string, string> BuildMediaHeaders()
    {
        var playerOrigin = PlayerHost;
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Referer"]    = $"{playerOrigin}/",
            ["Origin"]     = playerOrigin,
            ["User-Agent"] = BrowserUserAgent
        };
    }

    private string? NormalizeDomainUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return url;
        }

        return _useDeFallback
                   ? url.Replace("anizium.co", "anizium.de", StringComparison.OrdinalIgnoreCase)
                   : url;
    }

    private static string CreateCfControlHeader()
    {
        var istanbulNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _istanbulTimeZone);
        var weekday     = istanbulNow.DayOfWeek.ToString().ToLowerInvariant();
        var key         = $"{TokenKey}_{weekday}";
        var randomKey   = CreateRandomBase36(6);
        var timestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var payload     = $"{{\"{randomKey}\":{timestampMs}}}";

        return XorToHex(payload, key);
    }

    private static string CreateRandomBase36(int length)
    {
        const string alphabet = "0123456789abcdefghijklmnopqrstuvwxyz";
        Span<char>   buffer   = stackalloc char[length];
        for (var i = 0; i < length; i++)
        {
            buffer[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        }

        return new string(buffer);
    }

    private static string XorToHex(string plainText, string key)
    {
        var textBytes = Encoding.UTF8.GetBytes(plainText);
        var keyBytes  = Encoding.UTF8.GetBytes(key);
        var xored     = new byte[textBytes.Length];

        for (var i = 0; i < textBytes.Length; i++)
        {
            xored[i] = (byte) (textBytes[i] ^ keyBytes[i % keyBytes.Length]);
        }

        return Convert.ToHexString(xored).ToLowerInvariant();
    }

    private static TimeZoneInfo ResolveIstanbulTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");
        }
        catch (TimeZoneNotFoundException)
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById("Turkey Standard Time");
            }
            catch (TimeZoneNotFoundException)
            {
                return TimeZoneInfo.CreateCustomTimeZone("TRT", TimeSpan.FromHours(3), "Turkey Time", "Turkey Time");
            }
        }
    }

    private static string FormatQuality(string? rawQuality)
    {
        if (string.IsNullOrWhiteSpace(rawQuality))
        {
            return "Auto";
        }

        var trimmed = rawQuality.Trim();
        if (trimmed.Equals("4k", StringComparison.OrdinalIgnoreCase))
        {
            return "2160p";
        }

        return trimmed.EndsWith("p", StringComparison.OrdinalIgnoreCase) ? trimmed : $"{trimmed}p";
    }

    private static string? TryGetString(JsonElement obj, string prop)
    {
        return obj.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.String
                   ? el.GetString()?.Trim()
                   : null;
    }

    private static string? TryGetStringOrNumber(JsonElement obj, string prop)
    {
        if (!obj.TryGetProperty(prop, out var el))
        {
            return null;
        }

        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString()?.Trim(),
            JsonValueKind.Number => el.GetRawText(),
            _                    => null
        };
    }

    private static int? TryGetInt(JsonElement obj, string prop)
    {
        if (!obj.TryGetProperty(prop, out var el))
        {
            return null;
        }

        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var val))
        {
            return val;
        }

        if (el.ValueKind == JsonValueKind.String
            && int.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static double? TryGetDouble(JsonElement obj, string prop)
    {
        if (!obj.TryGetProperty(prop, out var el))
        {
            return null;
        }

        if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var val))
        {
            return val;
        }

        if (el.ValueKind == JsonValueKind.String
            && double.TryParse(el.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }
}
