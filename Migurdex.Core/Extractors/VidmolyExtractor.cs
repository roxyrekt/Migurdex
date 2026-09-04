using Microsoft.Extensions.Logging;
using Migurdex.Shared.Infrastructure;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Text.RegularExpressions;

namespace Migurdex.Core.Extractors;

public partial class VidmolyExtractor : IExtractor
{
    private readonly HttpClient                _httpClient;
    private readonly ILogger<VidmolyExtractor> _logger;
    private readonly M3U8PlaylistExtractor     _m3U8Extractor;

    public VidmolyExtractor(M3U8PlaylistExtractor m3U8Extractor, ISharedBridge bridge)
    {
        _httpClient    = bridge.CreateHttpClient();
        _m3U8Extractor = m3U8Extractor;
        _logger        = bridge.CreateLogger<VidmolyExtractor>();
    }

    public string Name => "Vidmoly";

    public bool CanExtract(string url)
    {
        return url.Contains("vidmoly", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<List<VideoSource>> ExtractAsync(string url,
        IDictionary<string, string>?                         headers           = null,
        CancellationToken                                    cancellationToken = default)
    {
        var sources = new List<VideoSource>();

        try
        {
            var referer = headers.GetReferer();
            _logger.LogDebug("fetching Vidmoly embed page: {Url}", url);

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent",
                                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            if (!string.IsNullOrEmpty(referer))
            {
                request.Headers.Add("Referer", referer);
            }

            var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("embed page failed: {StatusCode} for URL: {Url}",
                                   response.StatusCode,
                                   url);

                return sources;
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrEmpty(html))
            {
                _logger.LogWarning("embed page empty response for: {Url}", url);

                return sources;
            }

            string? m3U8Url = null;

            var match = SourcesFileRegex().Match(html);
            if (match.Success)
            {
                m3U8Url = match.Groups[1].Value;
            }
            else
            {
                var matchAlt = FileAltRegex().Match(html);
                if (matchAlt.Success)
                {
                    m3U8Url = matchAlt.Groups[1].Value;
                }
            }

            if (string.IsNullOrEmpty(m3U8Url))
            {
                _logger.LogWarning("could not find M3U8 playlist URL in Vidmoly HTML");

                return sources;
            }

            _logger.LogInformation("extracted master M3U8 playlist: {M3u8Url}", m3U8Url);

            var uri         = new Uri(url);
            var hostReferer = $"{uri.Scheme}://{uri.Host}/";

            var extractedSources = await _m3U8Extractor.ExtractAsync(m3U8Url,
                                                                     !string.IsNullOrEmpty(hostReferer)
                                                                         ? new Dictionary<string, string>
                                                                         {
                                                                             { "Referer", hostReferer }
                                                                         }
                                                                         : null);
            foreach (var src in extractedSources)
            {
                src.Headers = new Dictionary<string, string>
                {
                    { "Referer", hostReferer }
                };

                sources.Add(src);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "failed to extract video sources for: {Url}", url);
        }

        return sources;
    }

    [GeneratedRegex(@"sources:\s*\[\s*\{\s*file:\s*'([^']+)'")]
    private static partial Regex SourcesFileRegex();

    [GeneratedRegex(@"file:\s*""([^""]+master\.m3u8[^""]*)""")]
    private static partial Regex FileAltRegex();
}
