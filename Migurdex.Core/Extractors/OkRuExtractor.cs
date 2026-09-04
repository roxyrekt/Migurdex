using Microsoft.Extensions.Logging;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Migurdex.Core.Extractors;

public partial class OkRuExtractor : IExtractor
{
    private readonly HttpClient             _httpClient;
    private readonly ILogger<OkRuExtractor> _logger;
    private readonly M3U8PlaylistExtractor  _m3U8Extractor;

    public OkRuExtractor(M3U8PlaylistExtractor m3U8Extractor, ISharedBridge bridge, ILogger<OkRuExtractor> logger)
    {
        _httpClient    = bridge.CreateHttpClient(o => o.AllowAutoRedirect = true);
        _m3U8Extractor = m3U8Extractor;
        _logger        = logger;

        _httpClient.DefaultRequestHeaders.Add("User-Agent",
                                              "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
    }

    public string Name => "OkRu";

    public bool CanExtract(string url)
    {
        return url.Contains("ok.ru", StringComparison.OrdinalIgnoreCase)
               || url.Contains("odnoklassniki.ru", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<List<VideoSource>> ExtractAsync(string url,
        IDictionary<string, string>?                         headers           = null,
        CancellationToken                                    cancellationToken = default)
    {
        var sources = new List<VideoSource>();

        try
        {
            _logger.LogDebug("fetching Ok.ru embed page: {Url}", url);

            var response = await _httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("embed page failed for: {Url}", url);

                return sources;
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrEmpty(html))
            {
                _logger.LogWarning("embed page empty response for: {Url}", url);

                return sources;
            }

            var optionsMatch = OkRuOptionsRegex().Match(html);
            if (!optionsMatch.Success)
            {
                _logger.LogWarning("could not find data-options attribute in Ok.ru HTML");

                return sources;
            }

            var encodedOptions = optionsMatch.Groups[1].Value;
            var decodedOptions = WebUtility.HtmlDecode(encodedOptions);

            using var doc  = JsonDocument.Parse(decodedOptions);
            var       root = doc.RootElement;

            if (!root.TryGetProperty("flashvars", out var flashvarsProp))
            {
                _logger.LogWarning("flashvars not found in data-options");

                return sources;
            }

            if (!flashvarsProp.TryGetProperty("metadata", out var metadataProp))
            {
                _logger.LogWarning("metadata not found in flashvars");

                return sources;
            }

            var metadataJson = metadataProp.GetString();
            if (string.IsNullOrEmpty(metadataJson))
            {
                _logger.LogWarning("metadata is empty");

                return sources;
            }

            using var metaDoc  = JsonDocument.Parse(metadataJson);
            var       metaRoot = metaDoc.RootElement;

            if (metaRoot.TryGetProperty("ondemandHls", out var hlsProp))
            {
                var hlsUrl = hlsProp.GetString();
                if (!string.IsNullOrEmpty(hlsUrl))
                {
                    var extracted = await _m3U8Extractor.ExtractAsync(hlsUrl,
                                                                      new Dictionary<string, string>
                                                                      {
                                                                          {
                                                                              "User-Agent",
                                                                              "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"
                                                                          }
                                                                      });
                    if (extracted.Count > 0)
                    {
                        foreach (var src in extracted)
                        {
                            src.Headers = new Dictionary<string, string>
                            {
                                {
                                    "User-Agent",
                                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"
                                }
                            };
                            sources.Add(src);
                        }
                    }
                    else
                    {
                        sources.Add(new VideoSource
                        {
                            Url     = hlsUrl,
                            Quality = "Auto",
                            Type    = VideoType.M3U8,
                            Headers = new Dictionary<string, string>
                            {
                                {
                                    "User-Agent",
                                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"
                                }
                            }
                        });
                    }
                }
            }

            if (metaRoot.TryGetProperty("videos", out var videosProp) && videosProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var video in videosProp.EnumerateArray())
                {
                    if (video.TryGetProperty("name", out var nameProp) && video.TryGetProperty("url", out var urlProp))
                    {
                        var name        = nameProp.GetString() ?? "unknown";
                        var rawVideoUrl = urlProp.GetString();
                        if (string.IsNullOrEmpty(rawVideoUrl))
                        {
                            continue;
                        }

                        var quality = MapQualityName(name);

                        sources.Add(new VideoSource
                        {
                            Url     = rawVideoUrl,
                            Quality = quality,
                            Type    = VideoType.Mp4,
                            Headers = new Dictionary<string, string>
                            {
                                {
                                    "User-Agent",
                                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"
                                }
                            }
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "failed to extract Ok.ru video sources for: {Url}", url);
        }

        return sources;
    }

    [GeneratedRegex(@"data-options=""([^""]+)""")]
    private static partial Regex OkRuOptionsRegex();

    private static string MapQualityName(string name)
    {
        return name.ToLowerInvariant() switch
        {
            "mobile" => "144p",
            "lowest" => "240p",
            "low"    => "360p",
            "sd"     => "480p",
            "hd"     => "720p",
            "full"   => "1080p",
            _        => name
        };
    }
}
