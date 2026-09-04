using Microsoft.Extensions.Logging;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Text.RegularExpressions;

namespace Migurdex.Core.Extractors;

public partial class SendvidExtractor : IExtractor
{
    private readonly HttpClient                _httpClient;
    private readonly ILogger<SendvidExtractor> _logger;

    public SendvidExtractor(ISharedBridge bridge, ILogger<SendvidExtractor> logger)
    {
        _httpClient = bridge.CreateHttpClient();
        _logger     = logger;
    }

    public string Name => "Sendvid";

    public bool CanExtract(string url)
    {
        return url.Contains("sendvid.com", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<List<VideoSource>> ExtractAsync(string url,
        IDictionary<string, string>?                         headers           = null,
        CancellationToken                                    cancellationToken = default)
    {
        var sources = new List<VideoSource>();

        try
        {
            _logger.LogDebug("fetching Sendvid page: {TargetUrl}", url);

            var html = await _httpClient.GetStringAsync(url, cancellationToken);
            if (string.IsNullOrEmpty(html))
            {
                _logger.LogWarning("sendvid page empty response for: {TargetUrl}", url);

                return sources;
            }

            var videoUrlMatch = OgVideoRegex().Match(html);
            if (!videoUrlMatch.Success)
            {
                videoUrlMatch = OgVideoSecureRegex().Match(html);
            }

            if (!videoUrlMatch.Success)
            {
                _logger.LogWarning("could not find og:video URL in Sendvid HTML");

                return sources;
            }

            var videoUrl = videoUrlMatch.Groups[1].Value;

            var quality     = "Auto";
            var heightMatch = OgVideoHeightRegex().Match(html);
            if (heightMatch.Success)
            {
                quality = heightMatch.Groups[1].Value + "p";
            }

            _logger.LogDebug("successfully resolved Sendvid video: {VideoUrl} ({Quality})", videoUrl, quality);

            sources.Add(new VideoSource
            {
                Url     = videoUrl,
                Quality = quality,
                Type    = VideoType.Mp4
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "failed to extract Sendvid video sources for: {Url}", url);
        }

        return sources;
    }

    [GeneratedRegex(@"<meta\s+property=""og:video""\s+content=""([^""]+)""")]
    private static partial Regex OgVideoRegex();

    [GeneratedRegex(@"<meta\s+property=""og:video:secure_url""\s+content=""([^""]+)""")]
    private static partial Regex OgVideoSecureRegex();

    [GeneratedRegex(@"<meta\s+property=""og:video:height""\s+content=""(\d+)""")]
    private static partial Regex OgVideoHeightRegex();
}
