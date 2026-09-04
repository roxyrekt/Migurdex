using Microsoft.Extensions.Logging;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Text;
using System.Text.RegularExpressions;

namespace Migurdex.Core.Extractors;

public partial class CyberfileExtractor : IExtractor
{
    private readonly HttpClient                  _httpClient;
    private readonly ILogger<CyberfileExtractor> _logger;

    public CyberfileExtractor(ISharedBridge bridge)
    {
        _httpClient = bridge.CreateHttpClient();
        _logger     = bridge.CreateLogger<CyberfileExtractor>();

        _httpClient.DefaultRequestHeaders.Add("User-Agent",
                                              "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
    }

    public string Name => "Cyberfile";

    public bool CanExtract(string url)
    {
        return url.Contains("cyberfile.me", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<List<VideoSource>> ExtractAsync(string url,
        IDictionary<string, string>?                         headers           = null,
        CancellationToken                                    cancellationToken = default)
    {
        var sources = new List<VideoSource>();

        try
        {
            _logger.LogDebug("fetching Cyberfile embed page: {Url}", url);

            var response = await _httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("embed page failed for: {Url}", url);

                return sources;
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrEmpty(html))
            {
                _logger.LogWarning("embed page empty response for: {Url}", url);

                return sources;
            }

            string? videoUrl = null;

            var srcMatch = PlayerSrcColonRegex().Match(html);
            if (!srcMatch.Success)
            {
                srcMatch = PlayerSrcEqualRegex().Match(html);
            }

            if (srcMatch.Success)
            {
                videoUrl = srcMatch.Groups[1].Value;
            }

            if (string.IsNullOrEmpty(videoUrl))
            {
                var base64Match = Base64TrackerRegex().Match(html);
                if (base64Match.Success)
                {
                    try
                    {
                        var base64Str    = base64Match.Groups[1].Value;
                        var decodedBytes = Convert.FromBase64String(base64Str);
                        var decodedUrl   = Encoding.UTF8.GetString(decodedBytes);

                        if (decodedUrl.StartsWith("http") && decodedUrl.Contains(".mp4"))
                        {
                            videoUrl = decodedUrl;
                            _logger.LogDebug("successfully extracted video URL from base64 tracker");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "failed to decode base64 tracker string");
                    }
                }
            }

            if (string.IsNullOrEmpty(videoUrl))
            {
                _logger.LogWarning("could not resolve direct video stream URL for: {Url}", url);

                return sources;
            }

            var quality   = "Auto";
            var sizeMatch = SizeRegex().Match(url);
            if (sizeMatch.Success)
            {
                var parts = sizeMatch.Groups[1].Value.Split('x');
                if (parts.Length == 2)
                {
                    quality = parts[1] + "p";
                }
            }

            _logger.LogInformation("successfully resolved video URL: {VideoUrl} ({Quality})",
                                   videoUrl,
                                   quality);

            sources.Add(new VideoSource
            {
                Url     = videoUrl,
                Quality = quality,
                Type    = VideoType.Mp4
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "failed to extract Cyberfile video sources for: {Url}", url);
        }

        return sources;
    }

    [GeneratedRegex(@"src\s*:\s*""(https?://[^\s""]+\.mp4\?download_token=[a-f0-9]+)""")]
    private static partial Regex PlayerSrcColonRegex();

    [GeneratedRegex(@"src\s*=\s*""(https?://[^\s""]+\.mp4\?download_token=[a-f0-9]+)""")]
    private static partial Regex PlayerSrcEqualRegex();

    [GeneratedRegex(@"(aHR0cHM6Ly[a-zA-Z0-9+/=]{80,})")]
    private static partial Regex Base64TrackerRegex();

    [GeneratedRegex(@"/(\d+x\d+)/?")]
    private static partial Regex SizeRegex();
}
