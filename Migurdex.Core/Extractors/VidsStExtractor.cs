using Microsoft.Extensions.Logging;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Text.RegularExpressions;

namespace Migurdex.Core.Extractors;

public partial class VidsStExtractor : IExtractor
{
    private readonly HttpClient               _httpClient;
    private readonly ILogger<VidsStExtractor> _logger;
    private readonly M3U8PlaylistExtractor    _m3U8Extractor;
    private readonly IMp4MetadataReader       _metadataReader;

    public VidsStExtractor(M3U8PlaylistExtractor m3U8Extractor, ISharedBridge bridge)
    {
        _httpClient     = bridge.CreateHttpClient(o => o.SkipCertVerify = true);
        _m3U8Extractor  = m3U8Extractor;
        _metadataReader = bridge.MetadataReader;
        _logger         = bridge.CreateLogger<VidsStExtractor>();
    }

    public string Name => "VidsSt";

    public bool CanExtract(string url)
    {
        return url.Contains("vids.st", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<List<VideoSource>> ExtractAsync(string url,
        IDictionary<string, string>?                         headers           = null,
        CancellationToken                                    cancellationToken = default)
    {
        var sources = new List<VideoSource>();

        try
        {
            var fetchUrl = url;

            _logger.LogInformation("fetching embed page: {FetchUrl}", fetchUrl);

            var request = new HttpRequestMessage(HttpMethod.Get, fetchUrl);
            request.Headers.Add("User-Agent",
                                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("embed page failed: {StatusCode} for URL: {Url}",
                                   response.StatusCode,
                                   fetchUrl);

                return sources;
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrEmpty(html))
            {
                _logger.LogWarning("embed page empty response for: {Url}", fetchUrl);

                return sources;
            }

            string? videoUrl = null;
            var     isHls    = false;

            var urlMatch = UrlRegex().Match(html);
            if (urlMatch.Success)
            {
                videoUrl = Regex.Unescape(urlMatch.Groups[1].Value);
            }

            var isHlsMatch = IsHlsRegex().Match(html);
            if (isHlsMatch.Success)
            {
                bool.TryParse(isHlsMatch.Groups[1].Value, out isHls);
            }

            if (string.IsNullOrEmpty(videoUrl))
            {
                _logger.LogWarning("could not find video URL in HTML for: {Url}", fetchUrl);

                return sources;
            }

            _logger.LogInformation("extracted video URL: {VideoUrl} (IsHls: {IsHls})", videoUrl, isHls);

            var hostReferer = "https://vids.st/";

            if (isHls || videoUrl.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
            {
                var extractedSources = await _m3U8Extractor.ExtractAsync(videoUrl,
                                                                         !string.IsNullOrEmpty(hostReferer)
                                                                             ? new Dictionary<string, string>
                                                                             {
                                                                                 { "Referer", hostReferer }
                                                                             }
                                                                             : null);
                foreach (var src in extractedSources)
                {
                    sources.Add(src);
                }

                if (!sources.Any())
                {
                    sources.Add(new VideoSource
                    {
                        Url  = videoUrl,
                        Type = VideoType.M3U8
                    });
                }
            }
            else
            {
                var quality =
                    await _metadataReader.GetVideoQualityAsync(videoUrl,
                                                               hostReferer,
                                                               cancellationToken: cancellationToken);
                sources.Add(new VideoSource
                {
                    Url     = videoUrl,
                    Quality = quality,
                    Type    = VideoType.Mp4
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "failed to extract video sources for: {Url}", url);
        }

        return sources;
    }

    [GeneratedRegex(@"/e/([a-zA-Z0-9_-]+)", RegexOptions.Compiled)]
    private static partial Regex VideoIdRegex();

    [GeneratedRegex(@"const\s+url\s*=\s*[""']([^""']+)[""']", RegexOptions.Compiled)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"const\s+isHls\s*=\s*(true|false)", RegexOptions.Compiled)]
    private static partial Regex IsHlsRegex();
}
