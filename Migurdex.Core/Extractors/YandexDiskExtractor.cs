using Microsoft.Extensions.Logging;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Migurdex.Core.Extractors;

public partial class YandexDiskExtractor : IExtractor
{
    private readonly HttpClient                   _httpClient;
    private readonly ILogger<YandexDiskExtractor> _logger;
    private readonly M3U8PlaylistExtractor        _m3U8Extractor;

    public YandexDiskExtractor(M3U8PlaylistExtractor m3U8Extractor, ISharedBridge bridge)
    {
        _httpClient    = bridge.CreateHttpClient(o => o.AllowAutoRedirect = true);
        _m3U8Extractor = m3U8Extractor;
        _logger        = bridge.CreateLogger<YandexDiskExtractor>();
    }

    public string Name => "YandexDisk";

    public bool CanExtract(string url)
    {
        return url.Contains("yadi.sk", StringComparison.OrdinalIgnoreCase)
               || url.Contains("disk.yandex", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<List<VideoSource>> ExtractAsync(string url,
        IDictionary<string, string>?                         headers           = null,
        CancellationToken                                    cancellationToken = default)
    {
        var sources = new List<VideoSource>();

        try
        {
            _logger.LogInformation("starting extraction for URL: {Url}", url);

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent",
                                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("failed to fetch page. Status: {StatusCode}", response.StatusCode);

                return sources;
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken);

            var prefetchMatch = StorePrefetchRegex().Match(html);
            if (!prefetchMatch.Success)
            {
                _logger.LogWarning("could not find store-prefetch JSON");

                return sources;
            }

            var       json = prefetchMatch.Groups[1].Value;
            using var doc  = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("resources", out var resources))
            {
                _logger.LogWarning("no resources found in JSON");

                return sources;
            }

            foreach (var resource in resources.EnumerateObject())
            {
                if (resource.Value.TryGetProperty("videoStreams", out var videoStreams))
                {
                    if (videoStreams.TryGetProperty("videos", out var videosArr))
                    {
                        foreach (var video in videosArr.EnumerateArray())
                        {
                            var streamUrl = video.GetProperty("url").GetString();
                            var dimension = video.GetProperty("dimension").GetString() ?? "Auto";

                            if (string.IsNullOrEmpty(streamUrl))
                            {
                                continue;
                            }

                            if (streamUrl.Contains(".m3u8"))
                            {
                                var extracted = await _m3U8Extractor.ExtractAsync(streamUrl,
                                                    new Dictionary<string, string>
                                                    {
                                                        { "Referer", url }
                                                    });
                                foreach (var src in extracted)
                                {
                                    src.Quality = dimension == "adaptive" ? src.Quality : dimension;
                                    sources.Add(src);
                                }
                            }
                            else
                            {
                                sources.Add(new VideoSource
                                {
                                    Url     = streamUrl,
                                    Quality = dimension,
                                    Type    = VideoType.Mp4
                                });
                            }
                        }
                    }
                }
            }

            _logger.LogInformation("extracted {Count} sources", sources.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "failed to extract from {Url}", url);
        }

        return sources;
    }

    [GeneratedRegex(@"<script [^>]*id=""store-prefetch""[^>]*>(.*?)</script>", RegexOptions.Singleline)]
    private static partial Regex StorePrefetchRegex();
}
