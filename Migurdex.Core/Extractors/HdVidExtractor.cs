using Microsoft.Extensions.Logging;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Text.RegularExpressions;

namespace Migurdex.Core.Extractors;

public partial class HdVidExtractor : IExtractor
{
    private readonly HttpClient              _httpClient;
    private readonly ILogger<HdVidExtractor> _logger;

    public HdVidExtractor(ISharedBridge bridge, ILogger<HdVidExtractor> logger)
    {
        _httpClient = bridge.CreateHttpClient();
        _logger     = logger;
    }

    public string Name => "HdVid";

    public bool CanExtract(string url)
    {
        return url.Contains("hdvid.tv", StringComparison.OrdinalIgnoreCase)
               || url.Contains("vidhdnow3.space", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<List<VideoSource>> ExtractAsync(string url,
        IDictionary<string, string>?                         headers           = null,
        CancellationToken                                    cancellationToken = default)
    {
        var sources = new List<VideoSource>();

        try
        {
            _logger.LogDebug("fetching HdVid/VidHdNow embed page: {Url}", url);

            var requestHeaders = new Dictionary<string, string>
            {
                {
                    "User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"
                }
            };

            var targetUrl = url;

            var request = new HttpRequestMessage(HttpMethod.Get, targetUrl);
            request.Headers.Add("User-Agent", requestHeaders["User-Agent"]);
            request.Options.Set(new HttpRequestOptionsKey<bool>("NoFollow"), true);

            var response    = await _httpClient.SendAsync(request, cancellationToken);
            var redirectUrl = response.Headers.Location?.ToString();

            string html;
            if (!string.IsNullOrEmpty(redirectUrl))
            {
                _logger.LogInformation("following HTTP redirect: {TargetUrl} -> {RedirectUrl}",
                                       targetUrl,
                                       redirectUrl);

                targetUrl = redirectUrl;

                var secondRequest = new HttpRequestMessage(HttpMethod.Get, targetUrl);
                secondRequest.Headers.Add("User-Agent", requestHeaders["User-Agent"]);
                secondRequest.Headers.Add("Referer", url);

                var secondResponse = await _httpClient.SendAsync(secondRequest, cancellationToken);
                if (!secondResponse.IsSuccessStatusCode)
                {
                    _logger.LogWarning("redirected page failed: {Url}", targetUrl);

                    return sources;
                }

                html = await secondResponse.Content.ReadAsStringAsync(cancellationToken);
            }
            else
            {
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("embed page failed: {Url}", targetUrl);

                    return sources;
                }

                html = await response.Content.ReadAsStringAsync(cancellationToken);
            }

            if (string.IsNullOrEmpty(html))
            {
                _logger.LogWarning("embed page empty response for: {Url}", targetUrl);

                return sources;
            }

            var sourcesMatch = SourcesRegex().Match(html);
            if (sourcesMatch.Success)
            {
                var sourcesBlock = sourcesMatch.Groups[1].Value;
                var matches      = FileLabelRegex().Matches(sourcesBlock);

                foreach (Match match in matches)
                {
                    var fileUrl = match.Groups[1].Value;
                    var label   = match.Groups[2].Success ? match.Groups[2].Value : "Auto";

                    sources.Add(new VideoSource
                    {
                        Url     = fileUrl,
                        Quality = label,
                        Type = fileUrl.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)
                                   ? VideoType.M3U8
                                   : VideoType.Mp4,
                        Headers = new Dictionary<string, string>
                        {
                            { "Referer", url }
                        }
                    });
                }
            }

            if (sources.Count == 0)
            {
                var matches = FallbackRegex().Matches(html);

                foreach (Match match in matches)
                {
                    var fileUrl = match.Groups[1].Value;
                    sources.Add(new VideoSource
                    {
                        Url     = fileUrl,
                        Quality = "Auto",
                        Type = fileUrl.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)
                                   ? VideoType.M3U8
                                   : VideoType.Mp4,
                        Headers = new Dictionary<string, string>
                        {
                            { "Referer", url }
                        }
                    });
                }
            }

            _logger.LogDebug("successfully extracted {Count} sources", sources.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "failed to extract video sources for: {Url}", url);
        }

        return sources;
    }

    [GeneratedRegex(@"sources\s*:\s*(\[[^\]]+\])")]
    private static partial Regex SourcesRegex();

    [GeneratedRegex(@"file\s*:\s*[""'](https?://[^""']+)[""'](?:\s*,\s*label\s*:\s*[""']([^""']+)[""'])?",
                    RegexOptions.IgnoreCase)]
    private static partial Regex FileLabelRegex();

    [GeneratedRegex(@"file\s*:\s*[""'](https?://[^""']+\.(?:mp4|m3u8)[^""']*)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex FallbackRegex();
}
