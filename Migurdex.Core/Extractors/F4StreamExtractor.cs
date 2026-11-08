using Microsoft.Extensions.Logging;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Migurdex.Core.Extractors;

public partial class F4StreamExtractor : IExtractor
{
    private readonly HttpClient                 _httpClient;
    private readonly ILogger<F4StreamExtractor> _logger;
    private readonly M3U8PlaylistExtractor      _m3U8Extractor;

    public F4StreamExtractor(M3U8PlaylistExtractor m3U8Extractor, ISharedBridge bridge)
    {
        _m3U8Extractor = m3U8Extractor;
        _httpClient    = bridge.CreateHttpClient();
        _logger        = bridge.CreateLogger<F4StreamExtractor>();
    }

    public string Name => "F4Stream";

    public bool CanExtract(string url)
    {
        return url.Contains("f4scdn.com", StringComparison.OrdinalIgnoreCase)
               || url.Contains("f4stream.com", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<List<VideoSource>> ExtractAsync(string url,
        IDictionary<string, string>?                         headers           = null,
        CancellationToken                                    cancellationToken = default)
    {
        var sources = new List<VideoSource>();

        try
        {
            var uri    = new Uri(url);
            var domain = $"{uri.Scheme}://{uri.Host}";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent",
                                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            var response = await _httpClient.SendAsync(request, cancellationToken);
            var html     = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(html))
            {
                return sources;
            }

            var videoIdMatch = VideoIdRegex().Match(html);
            var videoId      = videoIdMatch.Success ? videoIdMatch.Groups[1].Value : null;

            if (string.IsNullOrEmpty(videoId))
            {
                var apiMatch = ApiPathRegex().Match(html);
                if (apiMatch.Success)
                {
                    videoId = apiMatch.Groups[1].Value;
                }
            }

            if (string.IsNullOrEmpty(videoId))
            {
                return sources;
            }

            var apiUrl = $"{domain}/api/play/{videoId}?ab=0&ad=0&r=&w=1";
            var apiReq = new HttpRequestMessage(HttpMethod.Get, apiUrl);
            apiReq.Headers.Add("Referer", url);
            apiReq.Headers.Add("Accept", "application/json");
            apiReq.Headers.Add("User-Agent",
                               "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            var apiResp = await _httpClient.SendAsync(apiReq, cancellationToken);
            if (!apiResp.IsSuccessStatusCode)
            {
                _logger.LogWarning("failed to fetch F4Stream API ({StatusCode}) for {Url}", apiResp.StatusCode, url);
                return sources;
            }

            var       json = await apiResp.Content.ReadAsStringAsync(cancellationToken);
            using var doc  = JsonDocument.Parse(json);
            var       root = doc.RootElement;

            if (!root.TryGetProperty("src", out var srcEl) || srcEl.GetString() is not { Length: > 0 } playlistUrl)
            {
                return sources;
            }

            _logger.LogInformation("extracted F4Stream playlist: {PlaylistUrl}", playlistUrl);

            var streamHeaders = new Dictionary<string, string>
            {
                { "Referer", $"{domain}/" },
                { "Origin", domain }
            };

            var extractedSources = await _m3U8Extractor.ExtractAsync(playlistUrl, streamHeaders, cancellationToken);
            foreach (var src in extractedSources)
            {
                src.Hoster  = Name;
                src.Headers = new Dictionary<string, string>(streamHeaders);
                sources.Add(src);
            }

            if (sources.Count == 0)
            {
                sources.Add(new VideoSource
                {
                    Url     = playlistUrl,
                    Quality = "Auto",
                    Type    = VideoType.M3U8,
                    Hoster  = Name,
                    Headers = streamHeaders
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "failed to extract video sources for: {Url}", url);
        }

        return sources;
    }

    [GeneratedRegex(@"data-video-id\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex VideoIdRegex();

    [GeneratedRegex(@"data-api\s*=\s*[""']/api/play/([^""'?]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ApiPathRegex();
}
