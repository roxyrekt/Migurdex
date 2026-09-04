using Microsoft.Extensions.Logging;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Infrastructure;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Migurdex.Core.Extractors;

public partial class VkExtractor : IExtractor
{
    private readonly HttpClient            _httpClient;
    private readonly ILogger<VkExtractor>  _logger;
    private readonly M3U8PlaylistExtractor _m3U8Extractor;

    public VkExtractor(M3U8PlaylistExtractor m3U8Extractor, ISharedBridge bridge)
    {
        _httpClient    = bridge.CreateHttpClient(o => o.AllowAutoRedirect = true);
        _m3U8Extractor = m3U8Extractor;
        _logger        = bridge.CreateLogger<VkExtractor>();
    }

    public string Name => "VK";

    public bool CanExtract(string url)
    {
        return url.Contains("vk.com", StringComparison.OrdinalIgnoreCase)
               || url.Contains("vkvideo.ru", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<List<VideoSource>> ExtractAsync(string url,
        IDictionary<string, string>?                         headers           = null,
        CancellationToken                                    cancellationToken = default)
    {
        var sources = new List<VideoSource>();

        try
        {
            var referer = headers.GetReferer();
            _logger.LogInformation("starting extraction for URL: {Url}", url);

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent",
                                "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:151.0) Gecko/20100101 Firefox/151.0");

            if (!string.IsNullOrEmpty(referer))
            {
                request.Headers.Add("Referer", referer);
            }

            var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("failed to fetch page. Status: {StatusCode}", response.StatusCode);

                return sources;
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken);

            var filesMatch = FilesJsonRegex().Match(html);
            if (!filesMatch.Success)
            {
                _logger.LogWarning("could not find files JSON object in page source");

                return [];
            }

            var       filesJson = filesMatch.Groups[1].Value;
            using var doc       = JsonDocument.Parse(filesJson);
            var       root      = doc.RootElement;

            foreach (var prop in root.EnumerateObject())
            {
                var key   = prop.Name;
                var value = prop.Value.GetString();

                if (string.IsNullOrEmpty(value) || !value.StartsWith("http"))
                {
                    continue;
                }

                if (key.Contains("hls"))
                {
                    var extracted = await _m3U8Extractor.ExtractAsync(value,
                                                                      new Dictionary<string, string>
                                                                      {
                                                                          { "Referer", url }
                                                                      });
                    foreach (var src in extracted)
                    {
                        src.Headers ??= new Dictionary<string, string>();
                        src.Headers["User-Agent"] =
                            "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:151.0) Gecko/20100101 Firefox/151.0";
                        sources.Add(src);
                    }
                }
                else if (key.StartsWith("mp4_") || key.StartsWith("url"))
                {
                    var quality = key.Replace("mp4_", "").Replace("url", "") + "p";
                    if (quality == "p")
                    {
                        quality = "Auto";
                    }

                    sources.Add(new VideoSource
                    {
                        Url     = value,
                        Quality = quality,
                        Type    = VideoType.Mp4,
                        Headers = new Dictionary<string, string>
                        {
                            {
                                "User-Agent",
                                "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:151.0) Gecko/20100101 Firefox/151.0"
                            }
                        }
                    });
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

    [GeneratedRegex(@"[""']files[""']\s*:\s*(\{.*?\})")]
    private static partial Regex FilesJsonRegex();
}
