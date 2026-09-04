using Microsoft.Extensions.Logging;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Text.RegularExpressions;

namespace Migurdex.Core.Extractors;

public partial class AitrVipExtractor : IExtractor
{
    private readonly HttpClient                _httpClient;
    private readonly ILogger<AitrVipExtractor> _logger;
    private readonly M3U8PlaylistExtractor     _m3U8Extractor;

    public AitrVipExtractor(M3U8PlaylistExtractor m3U8Extractor, ISharedBridge bridge)
    {
        _httpClient    = bridge.CreateHttpClient();
        _m3U8Extractor = m3U8Extractor;
        _logger        = bridge.CreateLogger<AitrVipExtractor>();

        _httpClient.DefaultRequestHeaders.Add("User-Agent",
                                              "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:109.0) Gecko/20100101 Firefox/119.0");
        _httpClient.DefaultRequestHeaders.Add("Accept",
                                              "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
        _httpClient.DefaultRequestHeaders.Add("Accept-Language", "tr,en-US;q=0.7,en;q=0.3");
    }

    public string Name => "AitrVip";

    public bool CanExtract(string url)
    {
        return url.Contains("optraco.top", StringComparison.OrdinalIgnoreCase)
               || url.Contains("optraco", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<List<VideoSource>> ExtractAsync(string url,
        IDictionary<string, string>?                         headers           = null,
        CancellationToken                                    cancellationToken = default)
    {
        var sources = new List<VideoSource>();

        try
        {
            _logger.LogInformation("starting extraction for URL: {Url}", url);

            var response = await _httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("failed to fetch page. Status: {StatusCode}", response.StatusCode);

                return sources;
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrEmpty(html))
            {
                _logger.LogWarning("page content is empty");

                return sources;
            }

            var m3U8Match = M3u8UrlRegex().Match(html);
            if (!m3U8Match.Success)
            {
                _logger.LogWarning("could not find M3U8 stream URL in page source");

                return sources;
            }

            var    m3U8Path = m3U8Match.Groups[1].Value;
            string m3U8Url;

            if (m3U8Path.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                m3U8Url = m3U8Path;
            }
            else
            {
                var uri = new Uri(url);
                m3U8Url = $"{uri.Scheme}://{uri.Host}{m3U8Path}";
            }

            _logger.LogInformation("found M3U8 URL: {M3u8Url}", m3U8Url);

            var extracted = await _m3U8Extractor.ExtractAsync(m3U8Url,
                                                              new Dictionary<string, string>
                                                              {
                                                                  { "Referer", url }
                                                              });
            foreach (var src in extracted)
            {
                src.Headers            ??= new Dictionary<string, string>();
                src.Headers["Referer"] =   url;
                sources.Add(src);
            }

            _logger.LogInformation("extracted {Count} sources successfully", sources.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "failed to extract video sources for URL: {Url}", url);
        }

        return sources;
    }

    [GeneratedRegex(@"(?:file|""file"")\s*:\s*[""']([^""']+\.m3u8)[""']")]
    private static partial Regex M3u8UrlRegex();
}
