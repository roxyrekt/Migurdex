using Microsoft.Extensions.Logging;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Text.RegularExpressions;

namespace Migurdex.Core.Extractors;

public partial class SibnetExtractor : IExtractor
{
    private readonly HttpClient               _httpClient;
    private readonly ILogger<SibnetExtractor> _logger;
    private readonly IMp4MetadataReader       _metadataReader;

    public SibnetExtractor(ISharedBridge bridge)
    {
        _httpClient     = bridge.CreateHttpClient();
        _metadataReader = bridge.MetadataReader;
        _logger         = bridge.CreateLogger<SibnetExtractor>();
    }

    public string Name => "Sibnet";

    public bool CanExtract(string url)
    {
        return url.Contains("sibnet.ru", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<List<VideoSource>> ExtractAsync(string url,
        IDictionary<string, string>?                         headers           = null,
        CancellationToken                                    cancellationToken = default)
    {
        var sources = new List<VideoSource>();

        try
        {
            var videoId = ExtractVideoId(url);
            if (string.IsNullOrEmpty(videoId))
            {
                _logger.LogWarning("could not extract video ID from Sibnet URL: {Url}", url);

                return sources;
            }

            var shellUrl = $"https://video.sibnet.ru/shell.php?videoid={videoId}";
            _logger.LogDebug("fetching Sibnet shell page: {ShellUrl}", shellUrl);

            var html = await _httpClient.GetStringAsync(shellUrl, cancellationToken);
            if (string.IsNullOrEmpty(html))
            {
                _logger.LogWarning("shell page empty response for: {ShellUrl}", shellUrl);

                return sources;
            }

            var srcMatch = PlayerSrcRegex().Match(html);
            if (!srcMatch.Success)
            {
                _logger.LogWarning("could not find player src in Sibnet HTML");

                return sources;
            }

            var relativePath = srcMatch.Groups[1].Value;
            var relativeUrl = relativePath.StartsWith("http") ? relativePath : "https://video.sibnet.ru" + relativePath;

            _logger.LogDebug("following redirect for relative Sibnet URL to obtain CDN path: {RelativeUrl}",
                             relativeUrl);

            var request = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
            request.Headers.Add("Referer", "https://video.sibnet.ru/");
            request.Options.Set(new HttpRequestOptionsKey<bool>("NoFollow"), true);

            var response = await _httpClient.SendAsync(request, cancellationToken);
            var cdnUrl   = response.Headers.Location?.ToString();

            if (string.IsNullOrEmpty(cdnUrl))
            {
                cdnUrl = relativeUrl;
                _logger.LogWarning("fetchNoFollow did not return redirect location, falling back to: {RelativeUrl}",
                                   relativeUrl);
            }
            else if (cdnUrl.StartsWith("//"))
            {
                cdnUrl = "https:" + cdnUrl;
            }

            _logger.LogDebug("successfully resolved Sibnet CDN URL: {CdnUrl}", cdnUrl);

            var quality =
                await _metadataReader.GetVideoQualityAsync(cdnUrl,
                                                           "https://video.sibnet.ru/",
                                                           cancellationToken: cancellationToken);

            sources.Add(new VideoSource
            {
                Url     = cdnUrl,
                Quality = quality,
                Type    = VideoType.Mp4
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "failed to extract Sibnet video sources for: {Url}", url);
        }

        return sources;
    }

    [GeneratedRegex(@"src:\s*""([^""]+\.mp4)""")]
    private static partial Regex PlayerSrcRegex();

    [GeneratedRegex(@"videoid=(\d+)")]
    private static partial Regex VideoIdParamRegex();

    [GeneratedRegex(@"video(\d+)")]
    private static partial Regex VideoIdPathRegex();

    private static string ExtractVideoId(string url)
    {
        var match = VideoIdParamRegex().Match(url);
        if (match.Success)
        {
            return match.Groups[1].Value;
        }

        match = VideoIdPathRegex().Match(url);
        if (match.Success)
        {
            return match.Groups[1].Value;
        }

        return string.Empty;
    }
}
