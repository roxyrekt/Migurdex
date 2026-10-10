using Microsoft.Extensions.Logging;
using Migurdex.Shared.Diagnostics;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Migurdex.Core.Extractors;

public class DailymotionExtractor : IExtractor
{
    private readonly HttpClient                    _httpClient;
    private readonly ILogger<DailymotionExtractor> _logger;
    private readonly M3U8PlaylistExtractor         _m3U8Extractor;

    public DailymotionExtractor(M3U8PlaylistExtractor m3U8Extractor, ISharedBridge bridge)
    {
        _httpClient    = bridge.CreateHttpClient();
        _m3U8Extractor = m3U8Extractor;
        _logger        = bridge.CreateLogger<DailymotionExtractor>();
    }

    public string Name => "Dailymotion";

    public bool CanExtract(string url)
    {
        return url.Contains("dailymotion.com", StringComparison.OrdinalIgnoreCase)
               || url.Contains("dai.ly", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<List<VideoSource>> ExtractAsync(string url,
        IDictionary<string, string>?                         headers           = null,
        CancellationToken                                    cancellationToken = default)
    {
        var sources = new List<VideoSource>();

        try
        {
            _logger.LogDebug("extracting URL: {Url}", url);

            string? videoId = null;
            if (url.Contains("/video/"))
            {
                videoId = url.Split("/video/").LastOrDefault()?.Split('?').FirstOrDefault();
            }
            else if (url.Contains("dai.ly/"))
            {
                videoId = url.Split("dai.ly/").LastOrDefault()?.Split('?').FirstOrDefault();
            }

            videoId = string.IsNullOrEmpty(videoId) ? GetQueryVideoId(url) : videoId;

            if (string.IsNullOrEmpty(videoId))
            {
                _logger.LogWarning("could not extract video ID from URL: {Url}", url);

                return sources;
            }

            var metadataUrl = $"https://www.dailymotion.com/player/metadata/video/{videoId}";
            _logger.LogDebug("fetching metadata from: {MetadataUrl}", metadataUrl);

            var request = new HttpRequestMessage(HttpMethod.Get, metadataUrl);
            request.Headers.Add("User-Agent",
                                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("metadata request failed with status: {StatusCode} for video ID: {VideoId}",
                                   response.StatusCode,
                                   videoId);

                return sources;
            }

            var jsonContent = await response.Content.ReadAsStringAsync(cancellationToken);
            var metadata    = JsonSerializer.Deserialize<DailymotionMetadata>(jsonContent);

            if (!string.IsNullOrEmpty(metadata?.Error?.Title) || !string.IsNullOrEmpty(metadata?.Error?.Message))
            {
                var reason = $"{metadata?.Error?.Title} {metadata?.Error?.Message}";

                throw new ExtractionException(UpstreamErrorClassifier.Detect(200, reason),
                                              UpstreamErrorClassifier.Summarize(reason));
            }

            var m3U8Url = metadata?.Qualities?.Auto?.FirstOrDefault()?.Url;
            if (string.IsNullOrEmpty(m3U8Url))
            {
                throw new ExtractionException(UpstreamErrorKind.UpstreamChanged,
                                                  $"metadata okundu ancak M3U8 yok (video ID: {videoId})");
            }

            _logger.LogInformation("found M3U8 URL: {M3u8Url}", m3U8Url);

            var extractedSources = await _m3U8Extractor.ExtractAsync(m3U8Url,
                                                                     new Dictionary<string, string>
                                                                     {
                                                                         { "Referer", "https://www.dailymotion.com/" }
                                                                     });
            sources.AddRange(extractedSources);

            if (metadata?.Subtitles is { Enable: true, Data: not null })
            {
                var subtitles = new List<Subtitle>();
                foreach (var (lang, subData) in metadata.Subtitles.Data
                                                ?? new Dictionary<string, DailymotionSubtitleData>())
                {
                    var subUrl = subData.Urls?.FirstOrDefault();
                    if (!string.IsNullOrEmpty(subUrl))
                    {
                        subtitles.Add(new Subtitle
                        {
                            Language = subData.Label ?? lang,
                            Url      = subUrl
                        });
                    }
                }

                if (subtitles.Any())
                {
                    foreach (var source in sources)
                    {
                        source.Subtitles = subtitles;
                    }
                }
            }
        }
        catch (ExtractionException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "failed to extract video sources for: {Url}", url);
        }

        return sources;
    }

    private static string? GetQueryVideoId(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Query))
        {
            return null;
        }

        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var key = pair[..eq];
            if (key is "video" or "v")
            {
                var value = Uri.UnescapeDataString(pair[(eq + 1)..]);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }
        }

        return null;
    }

    private class DailymotionMetadata
    {
        [JsonPropertyName("error")]
        public DailymotionError? Error { get; set; }

        [JsonPropertyName("qualities")]
        public DailymotionQualities? Qualities { get; set; }

        [JsonPropertyName("subtitles")]
        public DailymotionSubtitlesInfo? Subtitles { get; set; }
    }

    private class DailymotionQualities
    {
        [JsonPropertyName("auto")]
        public List<DailymotionQuality>? Auto { get; set; }
    }

    private class DailymotionQuality
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }
    }

    private class DailymotionError
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }

    private class DailymotionSubtitlesInfo
    {
        [JsonPropertyName("enable")]
        public bool Enable { get; set; }

        [JsonPropertyName("data")]
        public Dictionary<string, DailymotionSubtitleData>? Data { get; set; }
    }

    private class DailymotionSubtitleData
    {
        [JsonPropertyName("label")]
        public string? Label { get; set; }

        [JsonPropertyName("urls")]
        public List<string>? Urls { get; set; }
    }
}
