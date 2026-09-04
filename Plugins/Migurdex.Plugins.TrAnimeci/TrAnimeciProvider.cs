using Microsoft.Extensions.Logging;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Migurdex.Plugins.TRAnimeci;

public partial class TRAnimeciProvider : IAnimeProvider
{
    private static readonly JsonSerializerOptions      _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly        HttpClient                 _httpClient;
    private readonly        ILogger<TRAnimeciProvider> _logger;
    private                 string?                    _vDDoSCookie;

    public TRAnimeciProvider(ISharedBridge bridge, ILogger<TRAnimeciProvider> logger)
    {
        _httpClient = bridge.CreateHttpClient(o =>
        {
            o.UseCookies = true;
            o.Emulation  = BrowserEmulation.OkHttp5;
        });

        _logger = logger;
    }

    public string       Name    => "TRAnimeci";
    public string       BaseUrl => "https://tranimaci.com";
    public ProviderType Type    => ProviderType.Anime;

    public async Task<List<SearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        try
        {
            var encodedQuery = Uri.EscapeDataString(query);
            var apiUrl       = $"{BaseUrl}/api/search?q={encodedQuery}&limit=24";

            var content = await GetContentAsync(apiUrl, $"{BaseUrl}/arama?q={encodedQuery}", cancellationToken);

            if (string.IsNullOrWhiteSpace(content) || content.TrimStart().StartsWith('<'))
            {
                return [];
            }

            return MapToSearchResults(JsonSerializer.Deserialize<SearchApiResponse>(content, _jsonOptions)?.Data);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "search failed");
            return [];
        }
    }

    public async Task<AnimeDetails> GetDetailsAsync(string animeId, CancellationToken cancellationToken = default)
    {
        try
        {
            var cleanSlug = animeId.TrimStart('/');
            if (cleanSlug.StartsWith("anime/"))
            {
                cleanSlug = cleanSlug["anime/".Length..];
            }

            var pageUrl = $"{BaseUrl}/anime/{cleanSlug}";
            var content = await GetContentAsync(pageUrl, pageUrl, cancellationToken);

            if (string.IsNullOrWhiteSpace(content))
            {
                return new AnimeDetails();
            }

            var unescapedContent = content.Replace("\\\"", "\"");
            var match            = AnimeJsonRegex().Match(unescapedContent);

            if (!match.Success)
            {
                _logger.LogWarning("anime JSON payload could not be extracted for anime: {animeId}", animeId);
                return new AnimeDetails();
            }

            var animeJson = match.Groups[1].Value;
            var anime     = TryDeserializeJson<AnimeDetailDto>(animeJson);

            if (anime is null)
            {
                return new AnimeDetails();
            }

            var title         = !string.IsNullOrWhiteSpace(anime.Title) ? anime.Title : anime.TitleEnglish ?? "";
            var englishTitle  = !string.IsNullOrWhiteSpace(anime.TitleEnglish) ? anime.TitleEnglish : null;
            var japaneseTitle = !string.IsNullOrWhiteSpace(anime.JapaneseTitle) ? anime.JapaneseTitle : null;
            var seasonNumber  = AnimeDetails.ParseSeasonNumber(title);
            var summary       = ResolveDescription(anime.Description, content);

            var details = new AnimeDetails
            {
                Title         = title,
                EnglishTitle  = englishTitle,
                JapaneseTitle = japaneseTitle,
                Summary       = summary,
                Format        = ContentFormat.Tv,
                SeasonMappings =
                [
                    new SeasonMapping
                    {
                        SeasonNumber = seasonNumber
                    }
                ]
            };

            if (anime.Episodes is not null)
            {
                foreach (var ep in anime.Episodes)
                {
                    var epNum = ep.Number ?? 1;
                    var epSlug = !string.IsNullOrEmpty(anime.Slug)
                                     ? $"{anime.Slug}-{epNum}-bolum"
                                     : $"{cleanSlug}-{epNum}-bolum";

                    details.Episodes.Add(new Episode
                    {
                        Id     = epSlug,
                        Number = epNum,
                        Title = string.IsNullOrWhiteSpace(ep.Title) || ep.Title == epNum.ToString()
                                    ? $"Bölüm {epNum}"
                                    : ep.Title,
                        Season = seasonNumber
                    });
                }
            }

            return details;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "getDetailsAsync failed for anime: {animeId}", animeId);
            return new AnimeDetails();
        }
    }

    public async Task<List<VideoSource>> GetVideoSourcesAsync(string episodeId,
        string?                                                      group             = null,
        CancellationToken                                            cancellationToken = default)
    {
        try
        {
            var cleanId = episodeId.TrimStart('/');
            if (cleanId.StartsWith("video/"))
            {
                cleanId = cleanId["video/".Length..];
            }

            var pageUrl = $"{BaseUrl}/video/{cleanId}";
            var content = await GetContentAsync(pageUrl, pageUrl, cancellationToken);

            if (string.IsNullOrWhiteSpace(content))
            {
                return [];
            }

            var unescapedContent = content.Replace("\\\"", "\"");
            var match            = ActiveEpisodeJsonRegex().Match(unescapedContent);

            ActiveEpisodeDto? activeEpisode = null;
            if (match.Success)
            {
                activeEpisode = TryDeserializeJson<ActiveEpisodeDto>(match.Groups[1].Value);
            }

            if (activeEpisode?.VideoSources is null || activeEpisode.VideoSources.Count == 0)
            {
                var nonceMatch   = SourcesNonceRegex().Match(unescapedContent);
                var animeIdMatch = AnimeIdRegex().Match(unescapedContent);

                if (nonceMatch.Success && animeIdMatch.Success)
                {
                    var nonce   = nonceMatch.Groups[1].Value;
                    var animeId = animeIdMatch.Groups[1].Value;

                    var epNum = activeEpisode?.Number;
                    if (!epNum.HasValue || epNum.Value <= 0)
                    {
                        var epMatch = EpisodeNumberRegex().Match(cleanId);
                        if (epMatch.Success && int.TryParse(epMatch.Groups[1].Value, out var parsedEp))
                        {
                            epNum = parsedEp;
                        }
                        else
                        {
                            epNum = 1;
                        }
                    }

                    var apiUrl =
                        $"{BaseUrl}/api/video/episode-data/{animeId}?episode={epNum.Value}&n={Uri.EscapeDataString(nonce)}";
                    var apiContent = await GetContentAsync(apiUrl, pageUrl, cancellationToken);

                    if (!string.IsNullOrWhiteSpace(apiContent))
                    {
                        var apiData = JsonSerializer.Deserialize<EpisodeApiResponse>(apiContent, _jsonOptions);
                        if (apiData?.ActiveEpisode?.VideoSources is not null
                            && apiData.ActiveEpisode.VideoSources.Count > 0)
                        {
                            activeEpisode = apiData.ActiveEpisode;
                        }
                    }
                }
            }

            if (activeEpisode?.VideoSources is null || activeEpisode.VideoSources.Count == 0)
            {
                _logger.LogWarning(
                    "active episode JSON / video sources could not be extracted for episode: {episodeId}",
                    episodeId);
                return [];
            }

            var subtitles = new List<Subtitle>();
            if (activeEpisode.SubtitleSources is not null)
            {
                foreach (var sub in activeEpisode.SubtitleSources)
                {
                    var subUrl = sub.Url ?? sub.Urls?.FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(subUrl))
                    {
                        subtitles.Add(new Subtitle
                        {
                            Url      = subUrl,
                            Language = sub.Language ?? "tr",
                            Label    = sub.Label ?? sub.Language ?? "Türkçe",
                            Format   = InferSubtitleFormat(subUrl),
                            Headers = new Dictionary<string, string>
                            {
                                { "Referer", BaseUrl }
                            }
                        });
                    }
                }
            }

            var result = new List<VideoSource>();

            foreach (var vs in activeEpisode.VideoSources)
            {
                if (vs.Urls is null || vs.Urls.Count == 0)
                {
                    continue;
                }

                var quality = vs.Quality ?? "";

                for (var i = 0; i < vs.Urls.Count; i++)
                {
                    var videoUrl = vs.Urls[i];
                    if (string.IsNullOrWhiteSpace(videoUrl))
                    {
                        continue;
                    }

                    var qualityLabel = vs.Urls.Count > 1 ? $"{quality} (Sunucu {i + 1})" : quality;

                    result.Add(new VideoSource
                    {
                        Url       = videoUrl,
                        Quality   = qualityLabel,
                        Type      = VideoType.Mp4,
                        Hoster    = "TRAnimeci CDN",
                        Group     = "TRAnimeci",
                        Subtitles = subtitles,
                        Headers = new Dictionary<string, string>
                        {
                            { "Referer", BaseUrl }
                        }
                    });
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "getVideoSourcesAsync failed for episode: {episodeId}", episodeId);
            return [];
        }
    }

    public Task<List<string>> GetGroupsAsync(string episodeId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<List<string>>(["TRAnimeci"]);
    }

    private static T? TryDeserializeJson<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, _jsonOptions);
        }
        catch (JsonException)
        {
            try
            {
                return JsonSerializer.Deserialize<T>(json.Replace("\\\"", "\""), _jsonOptions);
            }
            catch (JsonException)
            {
                return default;
            }
        }
    }

    private static string? InferSubtitleFormat(string url)
    {
        if (url.Contains(".ass", StringComparison.OrdinalIgnoreCase))
        {
            return "ass";
        }

        if (url.Contains(".vtt", StringComparison.OrdinalIgnoreCase))
        {
            return "vtt";
        }

        if (url.Contains(".srt", StringComparison.OrdinalIgnoreCase))
        {
            return "srt";
        }

        return null;
    }

    private static string ResolveDescription(string? description, string htmlContent)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return TryGetFallbackDescription(htmlContent);
        }

        if (description.StartsWith('$') && int.TryParse(description[1..], out var refId))
        {
            var match = Regex.Match(htmlContent, $@"""{refId}:T\d+,(.+?)""\]\)", RegexOptions.Singleline);
            if (match.Success)
            {
                var text = match.Groups[1].Value;
                return Regex.Unescape(text).Trim();
            }

            return TryGetFallbackDescription(htmlContent);
        }

        return description;
    }

    private static string TryGetFallbackDescription(string htmlContent)
    {
        var metaMatch = DescriptionRegex().Match(htmlContent);
        if (metaMatch.Success)
        {
            return WebUtility.HtmlDecode(metaMatch.Groups[1].Value).Trim();
        }

        return string.Empty;
    }

    private async Task<string?> GetContentAsync(string url,
        string                                         referer,
        CancellationToken                              cancellationToken = default)
    {
        var response = await SendRequestAsync(url, referer, cancellationToken);
        var content  = await response.Content.ReadAsStringAsync(cancellationToken);

        if (content.Contains("Security Verification")
            || content.Contains("__waf_challenge")
            || content.Contains("challenge_required"))
        {
            var solved = await SolveWafChallengeAsync(referer, cancellationToken);
            if (solved)
            {
                _logger.LogInformation("WAF challenge solved");
                response = await SendRequestAsync(url, referer, cancellationToken);
                content  = await response.Content.ReadAsStringAsync(cancellationToken);
            }
        }
        else if (content.Contains("slowAES.decrypt"))
        {
            _vDDoSCookie = SolveVDDoS(content);
            if (!string.IsNullOrEmpty(_vDDoSCookie))
            {
                _logger.LogInformation("vDDoS-Wy challenge solved: {cookie}", _vDDoSCookie);
                response = await SendRequestAsync(url, referer, cancellationToken);
                content  = await response.Content.ReadAsStringAsync(cancellationToken);
            }
        }

        if (!response.IsSuccessStatusCode || string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        return content;
    }

    private async Task<bool> SolveWafChallengeAsync(string referer, CancellationToken cancellationToken = default)
    {
        try
        {
            var mainResp = await SendRequestAsync(BaseUrl, referer, cancellationToken);
            var html     = await mainResp.Content.ReadAsStringAsync(cancellationToken);

            var challengeMatch = ChallengeRegex().Match(html);
            var timestampMatch = TimestampRegex().Match(html);
            var sessionMatch   = SessionIdRegex().Match(html);
            var diffMatch      = DifficultyRegex().Match(html);

            if (!challengeMatch.Success || !timestampMatch.Success || !sessionMatch.Success)
            {
                return false;
            }

            var challenge  = challengeMatch.Groups[1].Value;
            var timestamp  = timestampMatch.Groups[1].Value;
            var sessionId  = sessionMatch.Groups[1].Value;
            var difficulty = diffMatch.Success && int.TryParse(diffMatch.Groups[1].Value, out var d) ? d : 4;

            var     prefix   = new string('0', difficulty);
            var     nonce    = 0;
            string? solution = null;

            while (nonce < 5_000_000)
            {
                var input     = $"{sessionId}:{challenge}:{timestamp}{nonce}";
                var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
                var hashHex   = Convert.ToHexString(hashBytes).ToLowerInvariant();

                if (hashHex.StartsWith(prefix, StringComparison.Ordinal))
                {
                    solution = nonce.ToString();
                    break;
                }

                nonce++;
            }

            if (solution is null)
            {
                return false;
            }

            var postContent = new FormUrlEncodedContent([
                new KeyValuePair<string, string>("solution", solution),
                new KeyValuePair<string, string>("challenge", challenge),
                new KeyValuePair<string, string>("timestamp", timestamp)
            ]);

            var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/__waf_challenge")
            {
                Content = postContent
            };
            request.Headers.Add("User-Agent", "Mozilla/5.0 (X11; Linux x86_64; rv:152.0) Gecko/20100101 Firefox/152.0");
            request.Headers.Add("Referer", referer);

            var postResp = await _httpClient.SendAsync(request, cancellationToken);
            return postResp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "solveWafChallengeAsync failed");
            return false;
        }
    }

    private Task<HttpResponseMessage> SendRequestAsync(string url,
        string                                                referer,
        CancellationToken                                     cancellationToken = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("User-Agent", "Mozilla/5.0 (X11; Linux x86_64; rv:152.0) Gecko/20100101 Firefox/152.0");
        request.Headers.Add("Accept-Language", "tr-TR,tr;q=0.9,en-US;q=0.8,en;q=0.7");
        request.Headers.Add("Referer", referer);

        if (!string.IsNullOrEmpty(_vDDoSCookie))
        {
            request.Headers.Add("Cookie", $"vDDoS-Wy={_vDDoSCookie}");
        }

        return _httpClient.SendAsync(request, cancellationToken);
    }

    private static string SolveVDDoS(string html)
    {
        var matches = VDDoSParamsRegex().Matches(html);
        if (matches.Count < 3)
        {
            return string.Empty;
        }

        var key    = Convert.FromHexString(matches[0].Groups[1].ValueSpan);
        var iv     = Convert.FromHexString(matches[1].Groups[1].ValueSpan);
        var cipher = Convert.FromHexString(matches[2].Groups[1].ValueSpan);

        using var aes = Aes.Create();
        aes.Key     = key;
        aes.IV      = iv;
        aes.Mode    = CipherMode.CBC;
        aes.Padding = PaddingMode.None;

        using var decryptor = aes.CreateDecryptor();
        var       decrypted = decryptor.TransformFinalBlock(cipher, 0, cipher.Length);

        return Convert.ToHexString(decrypted).ToLowerInvariant();
    }

    private List<SearchResult> MapToSearchResults(List<AnimeDto>? items)
    {
        if (items is null || items.Count == 0)
        {
            return [];
        }

        return items.Select(item => new SearchResult
                    {
                        Id    = item.Id ?? "",
                        Title = item.Title ?? "",
                        PosterUrl = !string.IsNullOrEmpty(item.Image) && item.Image.StartsWith('/')
                                        ? BaseUrl + item.Image
                                        : item.Image ?? "",
                        Url          = $"{BaseUrl}/anime/{item.Id}",
                        ProviderName = Name,
                        Type         = ProviderType.Anime,
                        Year         = item.Year?.ToString(),
                        Score        = item.Rating,
                        Categories   = item.Genres ?? []
                    })
                    .ToList();
    }

    [GeneratedRegex("""toNumbers\("([0-9a-fA-F]+)"\)""")]
    private static partial Regex VDDoSParamsRegex();

    [GeneratedRegex(@"const\s+challenge\s*=\s*""([^""]+)""")]
    private static partial Regex ChallengeRegex();

    [GeneratedRegex(@"const\s+timestamp\s*=\s*""([^""]+)""")]
    private static partial Regex TimestampRegex();

    [GeneratedRegex(@"const\s+sessionId\s*=\s*""([^""]+)""")]
    private static partial Regex SessionIdRegex();

    [GeneratedRegex(@"const\s+difficulty\s*=\s*(\d+)")]
    private static partial Regex DifficultyRegex();

    [GeneratedRegex(@"""anime""\s*:\s*(\{.*?\})\s*,\s*""(?:similarAnime|pageId)""")]
    private static partial Regex AnimeJsonRegex();

    [GeneratedRegex(@"""activeEpisode""\s*:\s*(\{.*?\})\s*,\s*""episodes""")]
    private static partial Regex ActiveEpisodeJsonRegex();

    [GeneratedRegex(@"""sourcesNonce""\s*:\s*""([^""]+)""")]
    private static partial Regex SourcesNonceRegex();

    [GeneratedRegex(@"""id""\s*:\s*""([^""]+)""\s*,\s*""slug""")]
    private static partial Regex AnimeIdRegex();

    [GeneratedRegex(@"-(\d+)-bolum$", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeNumberRegex();

    [GeneratedRegex(@"<meta\s+name=""description""\s+content=""(.+?)""", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex DescriptionRegex();

    private sealed record SearchApiResponse(List<AnimeDto>? Data);

    private sealed record EpisodeApiResponse(
        string?           SourcesNonce,
        ActiveEpisodeDto? ActiveEpisode
    );

    private sealed record AnimeDto(
        string?       Id,
        string?       Title,
        string?       Image,
        int?          Year,
        double?       Rating,
        List<string>? Genres);

    private sealed record AnimeDetailDto(
        string?                Id,
        string?                Slug,
        string?                Title,
        string?                TitleEnglish,
        string?                JapaneseTitle,
        string?                Description,
        string?                Poster,
        double?                Rating,
        int?                   Year,
        string?                Status,
        List<string>?          Genres,
        int?                   TotalEpisodes,
        List<AnimeEpisodeDto>? Episodes
    );

    private sealed record AnimeEpisodeDto(
        string? Id,
        int?    Number,
        string? Title,
        string? Duration,
        string? Thumbnail
    );

    private sealed record ActiveEpisodeDto(
        string?                  Id,
        int?                     Number,
        string?                  Title,
        List<VideoSourceDto>?    VideoSources,
        List<SubtitleSourceDto>? SubtitleSources
    );

    private sealed record VideoSourceDto(
        string?       Quality,
        List<string>? Urls
    );

    private sealed record SubtitleSourceDto(
        string?       Label,
        string?       Language,
        string?       Url,
        List<string>? Urls
    );
}
