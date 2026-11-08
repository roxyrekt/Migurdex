using Microsoft.Extensions.Logging;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Migurdex.Core.Extractors;

public partial class EmbedOriginExtractor : IExtractor
{
    private readonly HttpClient                    _httpClient;
    private readonly ILogger<EmbedOriginExtractor> _logger;
    private readonly M3U8PlaylistExtractor         _m3U8Extractor;

    public EmbedOriginExtractor(M3U8PlaylistExtractor m3U8Extractor, ISharedBridge bridge)
    {
        _m3U8Extractor = m3U8Extractor;
        _httpClient    = bridge.CreateHttpClient();
        _logger        = bridge.CreateLogger<EmbedOriginExtractor>();
    }

    public string Name => "EmbedOrigin";

    public bool CanExtract(string url)
    {
        return url.Contains("embedorigin.cc", StringComparison.OrdinalIgnoreCase)
               || url.Contains("streamorigin.cc", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<List<VideoSource>> ExtractAsync(string url,
        IDictionary<string, string>?                         headers           = null,
        CancellationToken                                    cancellationToken = default)
    {
        var sources = new List<VideoSource>();

        try
        {
            var match = CodeRegex().Match(url);
            if (!match.Success)
            {
                return sources;
            }

            var fileCode = match.Groups[1].Value;
            var apiUrl   = $"https://embedorigin.cc/app/v1/play/{fileCode}?embed=1";

            var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
            request.Headers.Add("Referer", url);

            var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("failed to fetch EmbedOrigin API ({StatusCode}) for {Url}",
                                   response.StatusCode,
                                   url);
                return sources;
            }

            var       json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc  = JsonDocument.Parse(json);
            var       root = doc.RootElement;

            double? duration = null;
            if (root.TryGetProperty("duration", out var durEl) && durEl.TryGetDouble(out var durVal) && durVal > 0)
            {
                duration = durVal;
            }

            var streamHeaders = new Dictionary<string, string>
            {
                { "Referer", "https://embedorigin.cc/" }
            };

            if (root.TryGetProperty("hls", out var hlsEl)
                && hlsEl.GetString() is { Length: > 0 } hlsUrl)
            {
                _logger.LogInformation("extracted EmbedOrigin HLS: {HlsUrl}", hlsUrl);

                var extractedSources = await _m3U8Extractor.ExtractAsync(hlsUrl, streamHeaders, cancellationToken);
                foreach (var src in extractedSources)
                {
                    src.Hoster  = Name;
                    src.Headers = new Dictionary<string, string>(streamHeaders);
                    if (src.DurationSeconds is null or <= 0 && duration is not null)
                    {
                        src.DurationSeconds = duration;
                    }

                    sources.Add(src);
                }

                if (sources.Count == 0)
                {
                    sources.Add(new VideoSource
                    {
                        Url             = hlsUrl,
                        Quality         = "Auto",
                        Type            = VideoType.M3U8,
                        Hoster          = Name,
                        DurationSeconds = duration,
                        Headers         = streamHeaders
                    });
                }
            }

            if (root.TryGetProperty("mp4", out var mp4El)
                && mp4El.GetString() is { Length: > 0 } mp4Url)
            {
                sources.Add(new VideoSource
                {
                    Url             = mp4Url,
                    Quality         = "Auto",
                    Type            = VideoType.Mp4,
                    Hoster          = Name,
                    DurationSeconds = duration,
                    Headers         = streamHeaders
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "failed to extract video sources for: {Url}", url);
        }

        return sources;
    }

    [GeneratedRegex(@"(?:embedorigin|streamorigin)\.cc/(?:e/)?([a-zA-Z0-9_-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex CodeRegex();
}
