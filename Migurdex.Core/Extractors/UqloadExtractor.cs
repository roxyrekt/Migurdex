using Microsoft.Extensions.Logging;
using Migurdex.Core.Utils;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Text.RegularExpressions;

namespace Migurdex.Core.Extractors;

public partial class UqloadExtractor : IExtractor
{
    private readonly HttpClient               _httpClient;
    private readonly ILogger<UqloadExtractor> _logger;
    private readonly M3U8PlaylistExtractor    _m3U8Extractor;

    public UqloadExtractor(M3U8PlaylistExtractor m3U8Extractor, ISharedBridge bridge)
    {
        _httpClient    = bridge.CreateHttpClient(o => o.AllowAutoRedirect = true);
        _m3U8Extractor = m3U8Extractor;
        _logger        = bridge.CreateLogger<UqloadExtractor>();
    }

    public string Name => "Uqload";

    public bool CanExtract(string url)
    {
        return url.Contains("uqload.", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<List<VideoSource>> ExtractAsync(string url,
        IDictionary<string, string>?                         headers           = null,
        CancellationToken                                    cancellationToken = default)
    {
        var sources = new List<VideoSource>();

        try
        {
            _logger.LogDebug("fetching Uqload embed page: {Url}", url);

            var targetUrl = NormalizeUqloadUrl(url);

            var request = new HttpRequestMessage(HttpMethod.Get, targetUrl);
            request.Headers.Add("User-Agent",
                                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            request.Headers.Add("Referer", targetUrl);

            var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("embed page failed: {Url} (Status: {StatusCode})", targetUrl, response.StatusCode);

                return sources;
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrEmpty(html))
            {
                _logger.LogWarning("embed page empty response for: {Url}", targetUrl);

                return sources;
            }

            var unpacked = JsUnpacker.Unpack(html);
            if (string.IsNullOrEmpty(unpacked))
            {
                _logger.LogWarning("failed to unpack Dean Edwards packed script");

                return sources;
            }

            _logger.LogDebug("successfully unpacked player script");

            var fileMatch = PlayerFileRegex().Match(unpacked);
            if (!fileMatch.Success)
            {
                _logger.LogWarning("could not find streaming 'file' URL in unpacked script");

                return sources;
            }

            var m3U8Url = fileMatch.Groups[1].Value;
            _logger.LogInformation("resolved M3U8 playlist URL: {M3u8Url}", m3U8Url);

            var extractedSources = await _m3U8Extractor.ExtractAsync(m3U8Url,
                                                                     new Dictionary<string, string>
                                                                     {
                                                                         { "Referer", "https://uqload.vc" }
                                                                     });
            foreach (var src in extractedSources)
            {
                src.Headers = new Dictionary<string, string>
                {
                    { "Referer", "https://uqload.vc" }
                };

                sources.Add(src);
            }

            if (sources.Count == 0)
            {
                sources.Add(new VideoSource
                {
                    Url     = m3U8Url,
                    Quality = "Auto",
                    Type    = VideoType.M3U8,
                    Headers = new Dictionary<string, string>
                    {
                        { "Referer", "https://uqload.vc" }
                    }
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "failed to extract Uqload video sources for: {Url}", url);
        }

        return sources;
    }

    private static string NormalizeUqloadUrl(string url)
    {
        var target = UqloadDomainRegex().Replace(url, "uqload.vc");
        return DuplicateEmbedRegex().Replace(target, "/embed-");
    }

    [GeneratedRegex(@"uqload\.[a-z0-9]+", RegexOptions.IgnoreCase)]
    private static partial Regex UqloadDomainRegex();

    [GeneratedRegex(@"/(?:embed-)+", RegexOptions.IgnoreCase)]
    private static partial Regex DuplicateEmbedRegex();

    [GeneratedRegex(@"file\s*:\s*[""'](https?://[^""']+)[""']")]
    private static partial Regex PlayerFileRegex();
}
