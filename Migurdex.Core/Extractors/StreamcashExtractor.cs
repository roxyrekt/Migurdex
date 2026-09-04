using Microsoft.Extensions.Logging;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Infrastructure;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Migurdex.Core.Extractors;

public partial class StreamcashExtractor : IExtractor
{
    private readonly HttpClient                   _httpClient;
    private readonly ILogger<StreamcashExtractor> _logger;
    private readonly M3U8PlaylistExtractor        _m3U8Extractor;

    public StreamcashExtractor(M3U8PlaylistExtractor m3U8Extractor, ISharedBridge bridge)
    {
        _httpClient    = bridge.CreateHttpClient();
        _m3U8Extractor = m3U8Extractor;
        _logger        = bridge.CreateLogger<StreamcashExtractor>();
    }

    public string Name => "Streamcash";

    public bool CanExtract(string url)
    {
        return !string.IsNullOrWhiteSpace(url) && url.Contains("streamcash.", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<List<VideoSource>> ExtractAsync(string url,
        IDictionary<string, string>?                         headers           = null,
        CancellationToken                                    cancellationToken = default)
    {
        var sources = new List<VideoSource>();

        try
        {
            var referer = headers.GetReferer();
            _logger.LogDebug("fetching embed page: {Url}", url);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent",
                                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            if (!string.IsNullOrEmpty(referer))
            {
                request.Headers.Add("Referer", referer);
            }

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("embed page failed: {StatusCode} for URL: {Url}",
                                   response.StatusCode,
                                   url);
                return sources;
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(html))
            {
                _logger.LogWarning("embed page empty response for: {Url}", url);
                return sources;
            }

            var uri         = new Uri(url);
            var hostReferer = $"{uri.Scheme}://{uri.Host}/";

            string?         m3u8Url   = null;
            List<Subtitle>? subtitles = null;

            var pcrMatch = PcrRegex().Match(html);
            if (pcrMatch.Success)
            {
                try
                {
                    var base64Data = pcrMatch.Groups[1].Value;
                    var jsonBytes  = Convert.FromBase64String(base64Data);
                    var jsonStr    = Encoding.UTF8.GetString(jsonBytes);

                    var payload = JsonSerializer.Deserialize<StreamcashPayload>(jsonStr);
                    if (payload != null && !string.IsNullOrWhiteSpace(payload.Src))
                    {
                        m3u8Url = payload.Src;

                        if (payload.Subs is { Count: > 0 })
                        {
                            subtitles =
                            [
                                .. payload.Subs.Select(s => new Subtitle
                                {
                                    Url      = s.Src,
                                    Language = s.Srclang ?? "und",
                                    Label    = s.Label
                                })
                            ];
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "failed to parse base64 PCr payload for: {Url}", url);
                }
            }

            if (string.IsNullOrEmpty(m3u8Url))
            {
                var directMatch = DirectM3U8Regex().Match(html);
                if (directMatch.Success)
                {
                    m3u8Url = directMatch.Groups[1].Value;
                }
            }

            if (string.IsNullOrEmpty(m3u8Url))
            {
                _logger.LogWarning("could not find M3U8 stream URL in HTML for: {Url}", url);
                return sources;
            }

            _logger.LogInformation("extracted M3U8 stream: {M3U8Url}", m3u8Url);

            var extractedSources = await _m3U8Extractor.ExtractAsync(m3u8Url,
                                                                     !string.IsNullOrEmpty(hostReferer)
                                                                         ? new Dictionary<string, string>
                                                                         {
                                                                             { "Referer", hostReferer }
                                                                         }
                                                                         : null);
            foreach (var src in extractedSources)
            {
                if (subtitles is { Count: > 0 })
                {
                    src.Subtitles = subtitles;
                }

                sources.Add(src);
            }

            if (!sources.Any())
            {
                sources.Add(new VideoSource
                {
                    Url       = m3u8Url,
                    Quality   = "Auto",
                    Type      = VideoType.M3U8,
                    Subtitles = subtitles
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "error extracting video sources for: {Url}", url);
        }

        return sources;
    }

    [GeneratedRegex(@"window\.__PCr\s*=\s*['""]([^'""]+)['""]", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex PcrRegex();

    [GeneratedRegex(@"(?:file|src)\s*:\s*[""'](https?://[^""']+\.m3u8[^""']*)[""']",
                    RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex DirectM3U8Regex();

    private sealed class StreamcashPayload
    {
        [JsonPropertyName("vid")]
        public string? Vid { get; set; }

        [JsonPropertyName("src")]
        public string? Src { get; set; }

        [JsonPropertyName("typ")]
        public string? Typ { get; set; }

        [JsonPropertyName("subs")]
        public List<StreamcashSubtitle>? Subs { get; set; }
    }

    private sealed class StreamcashSubtitle
    {
        [JsonPropertyName("src")]
        public string Src { get; } = string.Empty;

        [JsonPropertyName("srclang")]
        public string? Srclang { get; set; }

        [JsonPropertyName("label")]
        public string? Label { get; set; }

        [JsonPropertyName("default")]
        public bool? Default { get; set; }
    }
}
