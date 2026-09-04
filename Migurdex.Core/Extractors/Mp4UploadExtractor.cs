using Microsoft.Extensions.Logging;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Text.RegularExpressions;

namespace Migurdex.Core.Extractors;

public partial class Mp4UploadExtractor : IExtractor
{
    private readonly HttpClient                  _httpClient;
    private readonly ILogger<Mp4UploadExtractor> _logger;
    private readonly IMp4MetadataReader          _metadataReader;

    public Mp4UploadExtractor(ISharedBridge bridge)
    {
        _httpClient     = bridge.CreateHttpClient();
        _metadataReader = bridge.MetadataReader;
        _logger         = bridge.CreateLogger<Mp4UploadExtractor>();
    }

    public string Name => "Mp4Upload";

    public bool CanExtract(string url)
    {
        return url.Contains("mp4upload.com", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<List<VideoSource>> ExtractAsync(string url,
        IDictionary<string, string>?                         headers           = null,
        CancellationToken                                    cancellationToken = default)
    {
        var sources = new List<VideoSource>();

        try
        {
            _logger.LogDebug("fetching embed page: {Url}", url);

            var html = await _httpClient.GetStringAsync(url, cancellationToken);
            if (string.IsNullOrEmpty(html))
            {
                _logger.LogWarning("embed page empty response for: {Url}", url);

                return sources;
            }

            var srcMatch = PlayerSrcRegex().Match(html);

            if (srcMatch.Success)
            {
                var fileUrl = srcMatch.Groups[1].Value;
                _logger.LogInformation("extracted MP4 URL: {FileUrl}", fileUrl);

                var quality =
                    await _metadataReader.GetVideoQualityAsync(fileUrl,
                                                               "https://www.mp4upload.com/",
                                                               cancellationToken: cancellationToken);

                sources.Add(new VideoSource
                {
                    Url     = fileUrl,
                    Quality = quality,
                    Type    = VideoType.Mp4,
                    Headers = new Dictionary<string, string>
                    {
                        { "Referer", "https://www.mp4upload.com/" }
                    }
                });
            }
            else
            {
                var fallbackMatch = FallbackUrlRegex().Match(html);
                if (fallbackMatch.Success)
                {
                    var fileUrl = fallbackMatch.Groups[1].Value;

                    var quality =
                        await _metadataReader.GetVideoQualityAsync(fileUrl,
                                                                   "https://www.mp4upload.com/",
                                                                   cancellationToken: cancellationToken);

                    sources.Add(new VideoSource
                    {
                        Url     = fileUrl,
                        Quality = quality,
                        Type    = VideoType.Mp4,
                        Headers = new Dictionary<string, string>
                        {
                            { "Referer", "https://www.mp4upload.com/" }
                        }
                    });
                }
                else
                {
                    _logger.LogWarning("could not find MP4 video URL in HTML");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "failed to extract video sources for: {Url}", url);
        }

        return sources;
    }

    [GeneratedRegex(@"src\s*[:=]\s*[""'](https?://[^""']+\.mp4(?!\w)[^""']*)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex PlayerSrcRegex();

    [GeneratedRegex(@"[""'](https?://[^""']+\.mp4(?!\w)[^""']*)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex FallbackUrlRegex();
}
