using Microsoft.Extensions.Logging;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Text.RegularExpressions;

namespace Migurdex.Core.Extractors;

public partial class YourUploadExtractor : IExtractor
{
    private readonly HttpClient                   _httpClient;
    private readonly ILogger<YourUploadExtractor> _logger;
    private readonly IMp4MetadataReader           _metadataReader;

    public YourUploadExtractor(ISharedBridge bridge)
    {
        _httpClient     = bridge.CreateHttpClient(o => o.AllowAutoRedirect = true);
        _metadataReader = bridge.MetadataReader;
        _logger         = bridge.CreateLogger<YourUploadExtractor>();
    }

    public string Name => "YourUpload";

    public bool CanExtract(string url)
    {
        return url.Contains("yourupload.com", StringComparison.OrdinalIgnoreCase);
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

            var ogVideoMatch = OgVideoRegex().Match(html);

            var jwFileMatch = JwFileRegex().Match(html);

            string? fileUrl = null;

            if (ogVideoMatch.Success)
            {
                fileUrl = ogVideoMatch.Groups[1].Value;
            }
            else if (jwFileMatch.Success)
            {
                fileUrl = jwFileMatch.Groups[1].Value;
            }

            if (!string.IsNullOrEmpty(fileUrl))
            {
                _logger.LogInformation("extracted MP4 URL: {FileUrl}", fileUrl);

                var quality =
                    await _metadataReader.GetVideoQualityAsync(fileUrl,
                                                               "https://www.yourupload.com/",
                                                               cancellationToken: cancellationToken);

                sources.Add(new VideoSource
                {
                    Url     = fileUrl,
                    Quality = quality,
                    Type    = VideoType.Mp4,
                    Headers = new Dictionary<string, string>
                    {
                        { "Referer", "https://www.yourupload.com/" }
                    }
                });
            }
            else
            {
                _logger.LogWarning("could not find MP4 video URL in HTML");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "failed to extract video sources for: {Url}", url);
        }

        return sources;
    }

    [GeneratedRegex(@"<meta\s+property=""og:video""\s+content=""(https?://[^""]+\.mp4[^""]*)""",
                    RegexOptions.IgnoreCase)]
    private static partial Regex OgVideoRegex();

    [GeneratedRegex(@"file\s*:\s*['""](https?://[^'""]+\.mp4[^'""]*)['""]", RegexOptions.IgnoreCase)]
    private static partial Regex JwFileRegex();
}
