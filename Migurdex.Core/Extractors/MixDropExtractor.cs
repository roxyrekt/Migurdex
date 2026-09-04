using Microsoft.Extensions.Logging;
using Migurdex.Core.Utils;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Text.RegularExpressions;

namespace Migurdex.Core.Extractors;

public partial class MixDropExtractor : IExtractor
{
    private readonly HttpClient                _httpClient;
    private readonly ILogger<MixDropExtractor> _logger;
    private readonly IMp4MetadataReader        _metadataReader;

    public MixDropExtractor(ISharedBridge bridge)
    {
        _httpClient     = bridge.CreateHttpClient();
        _logger         = bridge.CreateLogger<MixDropExtractor>();
        _metadataReader = bridge.MetadataReader;

        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
    }

    public string Name => "MixDrop";

    public bool CanExtract(string url)
    {
        return url.Contains("mixdrop.", StringComparison.OrdinalIgnoreCase)
               || url.Contains("miixdrop.", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<List<VideoSource>> ExtractAsync(string url,
        IDictionary<string, string>?                         headers           = null,
        CancellationToken                                    cancellationToken = default)
    {
        var sources = new List<VideoSource>();

        try
        {
            if (!url.Contains("/e/"))
            {
                url = url.Replace("/f/", "/e/");
            }

            var response = await _httpClient.GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return sources;
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken);

            var unpacked = JsUnpacker.Unpack(html);
            if (!string.IsNullOrEmpty(unpacked))
            {
                var videoMatch = WurlRegex().Match(unpacked);
                if (videoMatch.Success)
                {
                    var videoUrl = videoMatch.Groups[1].Value;
                    if (videoUrl.StartsWith("//"))
                    {
                        videoUrl = "https:" + videoUrl;
                    }

                    var quality = await _metadataReader.GetVideoQualityAsync(videoUrl,
                                                                             new Dictionary<string, string>
                                                                             {
                                                                                 { "User-Agent", "Mozilla/5.0" }
                                                                             });

                    sources.Add(new VideoSource
                    {
                        Url     = videoUrl,
                        Quality = quality,
                        Type    = VideoType.Mp4,
                        Headers = new Dictionary<string, string>
                        {
                            { "User-Agent", "Mozilla/5.0" }
                        }
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "failed to extract MixDrop video sources for: {Url}", url);
        }

        return sources;
    }

    [GeneratedRegex(@"wurl=""([^""]+)""")]
    private static partial Regex WurlRegex();
}
