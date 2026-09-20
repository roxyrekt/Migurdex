using Microsoft.Extensions.Logging;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Migurdex.Plugins.AniHub;

public partial class AniHubProvider : IAnimeProvider
{
    private const string ApiBase      = "https://api.anihub.com.tr";
    private const string MediaBase    = "https://media.anihub.com.tr";
    private const string DefaultGroup = "AniHub";

    private readonly HttpClient              _httpClient;
    private readonly ILogger<AniHubProvider> _logger;

    public AniHubProvider(ISharedBridge bridge, ILogger<AniHubProvider> logger)
    {
        _httpClient = bridge.CreateHttpClient(o => o.AllowAutoRedirect = true);
        _logger     = logger;

        _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
        _httpClient.DefaultRequestHeaders.Add("Origin", "https://anihub.com.tr");
        _httpClient.DefaultRequestHeaders.Add("Referer", "https://anihub.com.tr/");
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                                                                  "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
    }

    public string       Name    => "AniHub";
    public string       BaseUrl => "https://anihub.com.tr";
    public ProviderType Type    => ProviderType.Anime;

    public async Task<List<SearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return [];
            }

            var url = $"{ApiBase}/v1/animes?q={Uri.EscapeDataString(query.Trim())}";
            var response =
                await _httpClient.GetFromJsonAsync<AniHubEnvelope<AniHubAnimeListData>>(url, cancellationToken);

            var items = response?.Data?.Items;
            if (items is null)
            {
                return [];
            }

            return items
                   .Where(x => !string.IsNullOrWhiteSpace(x.Slug))
                   .Select(x =>
                   {
                       var fmt = ParseFormat(x.ContentType, x.Type, x.Format);
                       if (fmt is ContentFormat.Tv or ContentFormat.Unknown && AnimeDetails.IsMovieTitle(x.Title))
                       {
                           fmt = ContentFormat.Movie;
                       }

                       return new SearchResult
                       {
                           Id           = x.Slug!,
                           Title        = x.Title ?? x.Slug!,
                           Url          = $"{BaseUrl}/anime/{x.Slug}",
                           PosterUrl    = BuildMediaUrl(x.PosterKey),
                           ProviderName = Name,
                           Type         = ProviderType.Anime,
                           Format       = fmt,
                           Year         = x.Year?.ToString()
                       };
                   })
                   .ToList();
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
            var slug = NormalizeAnimeSlug(animeId);
            var url  = $"{ApiBase}/v1/animes/{Uri.EscapeDataString(slug)}";
            var response =
                await _httpClient.GetFromJsonAsync<AniHubEnvelope<AniHubAnimeDetailData>>(url, cancellationToken);

            var data = response?.Data;
            if (data?.Anime is null)
            {
                throw new InvalidOperationException($"Anime bulunamadı: {animeId}");
            }

            var anime = data.Anime;
            var details = new AnimeDetails
            {
                Title     = anime.Title ?? slug,
                Summary   = anime.Description ?? "",
                PosterUrl = BuildMediaUrl(anime.PosterKey),
                Format    = ParseFormat(anime.ContentType, anime.Type, anime.Format)
            };

            var seasonNumber = ResolveSeasonNumber(slug, data.Series);
            details.SeasonMappings.Add(new SeasonMapping
            {
                SeasonNumber = seasonNumber
            });

            if (data.Episodes is not null)
            {
                foreach (var ep in data.Episodes.OrderBy(x => x.Number))
                {
                    if (string.IsNullOrWhiteSpace(ep.Slug))
                    {
                        continue;
                    }

                    details.Episodes.Add(new Episode
                    {
                        Id     = ep.Slug!,
                        Title  = string.IsNullOrWhiteSpace(ep.Title) ? $"{ep.Number}. Bölüm" : ep.Title!,
                        Number = ep.Number,
                        Season = seasonNumber
                    });
                }
            }

            return details;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "failed to get details for {AnimeId}", animeId);

            throw;
        }
    }

    public Task<List<string>> GetGroupsAsync(string episodeId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new List<string>
        {
            DefaultGroup
        });
    }

    public async Task<List<VideoSource>> GetVideoSourcesAsync(string episodeId,
        string?                                                      group             = null,
        CancellationToken                                            cancellationToken = default)
    {
        try
        {
            var slug = NormalizeEpisodeSlug(episodeId);
            if (string.IsNullOrWhiteSpace(slug))
            {
                return [];
            }

            var url = $"{ApiBase}/v1/episodes/{Uri.EscapeDataString(slug)}";
            var response =
                await _httpClient.GetFromJsonAsync<AniHubEnvelope<AniHubEpisodeDetailData>>(url, cancellationToken);

            var playback = response?.Data?.Playback;
            if (playback is null || !string.Equals(playback.Status, "ready", StringComparison.OrdinalIgnoreCase))
            {
                return [];
            }

            if (string.IsNullOrWhiteSpace(playback.MasterPlaylistKey)
                || string.IsNullOrWhiteSpace(playback.MasterPlaylistUrl))
            {
                return [];
            }

            var tokenResponse = await _httpClient.PostAsJsonAsync(
                                    $"{ApiBase}/v1/video-tokens",
                                    new
                                    {
                                        masterPlaylistKey = playback.MasterPlaylistKey
                                    },
                                    cancellationToken);

            if (!tokenResponse.IsSuccessStatusCode)
            {
                return [];
            }

            var tokenEnvelope =
                await tokenResponse.Content.ReadFromJsonAsync<AniHubEnvelope<AniHubTokenData>>(cancellationToken);
            var token = tokenEnvelope?.Data?.Token;
            if (string.IsNullOrWhiteSpace(token))
            {
                return [];
            }

            var finalUrl = $"{playback.MasterPlaylistUrl}?token={Uri.EscapeDataString(token)}";
            var quality  = await TryDetectMaxQualityAsync(finalUrl, cancellationToken);

            return
            [
                new VideoSource
                {
                    Url      = finalUrl,
                    Quality  = quality,
                    Type     = VideoType.M3U8,
                    Hoster   = DefaultGroup,
                    Group    = DefaultGroup,
                    Language = "tr",
                    Subtitles = playback.Subtitles?
                                        .Where(s => !string.IsNullOrWhiteSpace(s.Url))
                                        .Select(s => new Subtitle
                                        {
                                            Url      = s.Url!,
                                            Language = s.Language ?? "",
                                            Label    = s.Label
                                        })
                                        .ToList()
                }
            ];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "failed to get video sources for episode {EpisodeId}", episodeId);

            return [];
        }
    }

    private async Task<string> TryDetectMaxQualityAsync(string masterUrl, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync(masterUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return "Auto";
            }

            var content   = await response.Content.ReadAsStringAsync(cancellationToken);
            var maxHeight = 0;
            foreach (Match match in ResolutionRegex().Matches(content))
            {
                if (int.TryParse(match.Groups[2].Value, out var height) && height > maxHeight)
                {
                    maxHeight = height;
                }
            }

            return maxHeight > 0 ? $"{maxHeight}p" : "Auto";
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "could not detect quality from master playlist");

            return "Auto";
        }
    }

    [GeneratedRegex(@"RESOLUTION=(\d+)x(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ResolutionRegex();

    private static string? BuildMediaUrl(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        if (key.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return key;
        }

        return $"{MediaBase}/{key.TrimStart('/')}";
    }

    private static string NormalizeAnimeSlug(string animeId)
    {
        var slug = animeId.Trim().Trim('/');
        if (slug.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            var uri = new Uri(slug);
            slug = uri.AbsolutePath.Trim('/');
            if (slug.StartsWith("anime/", StringComparison.OrdinalIgnoreCase))
            {
                slug = slug["anime/".Length..];
            }
        }
        else if (slug.StartsWith("anime/", StringComparison.OrdinalIgnoreCase))
        {
            slug = slug["anime/".Length..];
        }

        return slug;
    }

    private static string NormalizeEpisodeSlug(string episodeId)
    {
        var slug = episodeId.Trim().Trim('/');
        if (slug.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            slug = new Uri(slug).AbsolutePath.Trim('/');
        }

        return slug;
    }

    private static int ResolveSeasonNumber(string slug, AniHubSeriesData? series)
    {
        if (series?.Seasons is not null)
        {
            foreach (var season in series.Seasons)
            {
                if (season.Slug?.Equals(slug, StringComparison.OrdinalIgnoreCase) == true)
                {
                    return season.Order <= 0 ? 1 : season.Order;
                }
            }

            if (series.CurrentSeasonIndex >= 0 && series.CurrentSeasonIndex < series.Seasons.Count)
            {
                var order = series.Seasons[series.CurrentSeasonIndex].Order;

                return order <= 0 ? 1 : order;
            }
        }

        return AnimeDetails.ParseSeasonNumber(slug.Replace("-", " "));
    }

    private static ContentFormat ParseFormat(string? contentType, string? type = null, string? format = null)
    {
        var val = (contentType ?? type ?? format ?? "").Trim().ToLowerInvariant();
        return val switch
        {
            "movie" or "film" => ContentFormat.Movie,
            "ova"             => ContentFormat.Ova,
            "special"         => ContentFormat.Special,
            _                 => ContentFormat.Tv
        };
    }

    private sealed class AniHubEnvelope<T>
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("data")]
        public T? Data { get; set; }
    }

    private sealed class AniHubAnimeListData
    {
        [JsonPropertyName("items")]
        public List<AniHubAnimeItem>? Items { get; set; }
    }

    private sealed class AniHubAnimeDetailData
    {
        [JsonPropertyName("anime")]
        public AniHubAnimeItem? Anime { get; set; }

        [JsonPropertyName("episodes")]
        public List<AniHubEpisodeItem>? Episodes { get; set; }

        [JsonPropertyName("series")]
        public AniHubSeriesData? Series { get; set; }
    }

    private sealed class AniHubEpisodeDetailData
    {
        [JsonPropertyName("playback")]
        public AniHubPlaybackData? Playback { get; set; }
    }

    private sealed class AniHubAnimeItem
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("slug")]
        public string? Slug { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("year")]
        public int? Year { get; set; }

        [JsonPropertyName("posterKey")]
        public string? PosterKey { get; set; }

        [JsonPropertyName("contentType")]
        public string? ContentType { get; set; }

        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("format")]
        public string? Format { get; set; }
    }

    private sealed class AniHubEpisodeItem
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("slug")]
        public string? Slug { get; set; }

        [JsonPropertyName("number")]
        public double Number { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }
    }

    private sealed class AniHubSeriesData
    {
        [JsonPropertyName("currentSeasonIndex")]
        public int CurrentSeasonIndex { get; set; }

        [JsonPropertyName("seasons")]
        public List<AniHubSeasonItem>? Seasons { get; set; }
    }

    private sealed class AniHubSeasonItem
    {
        [JsonPropertyName("order")]
        public int Order { get; set; }

        [JsonPropertyName("slug")]
        public string? Slug { get; set; }
    }

    private sealed class AniHubPlaybackData
    {
        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("masterPlaylistKey")]
        public string? MasterPlaylistKey { get; set; }

        [JsonPropertyName("masterPlaylistUrl")]
        public string? MasterPlaylistUrl { get; set; }

        [JsonPropertyName("subtitles")]
        public List<AniHubSubtitleItem>? Subtitles { get; set; }
    }

    private sealed class AniHubSubtitleItem
    {
        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("language")]
        public string? Language { get; set; }

        [JsonPropertyName("label")]
        public string? Label { get; set; }
    }

    private sealed class AniHubTokenData
    {
        [JsonPropertyName("token")]
        public string? Token { get; set; }

        [JsonPropertyName("expiresAt")]
        public long ExpiresAt { get; set; }
    }
}
