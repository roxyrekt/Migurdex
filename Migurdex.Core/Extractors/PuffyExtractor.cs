using Microsoft.Extensions.Logging;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Text.RegularExpressions;

namespace Migurdex.Core.Extractors;

public partial class PuffyExtractor : IExtractor
{
    private readonly HttpClient              _httpClient;
    private readonly ILogger<PuffyExtractor> _logger;
    private readonly M3U8PlaylistExtractor   _m3U8Extractor;

    public PuffyExtractor(M3U8PlaylistExtractor m3U8Extractor, ISharedBridge bridge)
    {
        _httpClient    = bridge.CreateHttpClient();
        _m3U8Extractor = m3U8Extractor;
        _logger        = bridge.CreateLogger<PuffyExtractor>();
    }

    public string Name => "Puffy";

    public bool CanExtract(string url)
    {
        return url.Contains("puffytr.tr", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<List<VideoSource>> ExtractAsync(string url,
        IDictionary<string, string>?                         headers           = null,
        CancellationToken                                    cancellationToken = default)
    {
        var sources = new List<VideoSource>();

        try
        {
            var hash = ExtractHashFromUrl(url);
            if (string.IsNullOrEmpty(hash))
            {
                var response = await _httpClient.GetAsync(url, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    var html        = await response.Content.ReadAsStringAsync(cancellationToken);
                    var masterMatch = MasterUrlRegex().Match(html);
                    if (masterMatch.Success)
                    {
                        var masterPath = masterMatch.Groups[1].Value;
                        var uri        = new Uri(url);
                        var masterUrl  = $"{uri.Scheme}://{uri.Host}{masterPath}";

                        return await _m3U8Extractor.ExtractAsync(masterUrl,
                                                                 new Dictionary<string, string>
                                                                 {
                                                                     { "Referer", url }
                                                                 },
                                                                 cancellationToken);
                    }
                }

                _logger.LogWarning("could not extract hash or masterUrl from: {Url}", url);

                return sources;
            }

            var baseUri = new Uri(url);
            var m3U8Url = $"{baseUri.Scheme}://{baseUri.Host}/stream/{hash}/master.txt";

            _logger.LogDebug("extracted M3U8 URL: {M3U8Url}", m3U8Url);

            return await _m3U8Extractor.ExtractAsync(m3U8Url,
                                                     new Dictionary<string, string>
                                                     {
                                                         { "Referer", url }
                                                     },
                                                     cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "failed to extract video sources for URL: {Url}", url);
        }

        return sources;
    }

    [GeneratedRegex(@"const masterUrl\s*=\s*""([^""]+)""")]
    private static partial Regex MasterUrlRegex();

    [GeneratedRegex(@"puffytr\.tr/(?:watch|embed|video)/([a-f0-9]{32})")]
    private static partial Regex PuffyHashRegex();

    private static string ExtractHashFromUrl(string url)
    {
        var match = PuffyHashRegex().Match(url);

        return match.Success ? match.Groups[1].Value : string.Empty;
    }
}
