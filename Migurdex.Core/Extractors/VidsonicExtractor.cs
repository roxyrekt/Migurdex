using Microsoft.Extensions.Logging;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Infrastructure;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Text;
using System.Text.RegularExpressions;

namespace Migurdex.Core.Extractors;

public partial class VidsonicExtractor : IExtractor
{
    private readonly HttpClient                 _httpClient;
    private readonly ILogger<VidsonicExtractor> _logger;
    private readonly M3U8PlaylistExtractor      _m3U8Extractor;

    public VidsonicExtractor(M3U8PlaylistExtractor m3U8Extractor, ISharedBridge bridge)
    {
        _httpClient    = bridge.CreateHttpClient();
        _m3U8Extractor = m3U8Extractor;
        _logger        = bridge.CreateLogger<VidsonicExtractor>();
    }

    public string Name => "Vidsonic";

    public bool CanExtract(string url)
    {
        return url.Contains("vidsonic.net", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<List<VideoSource>> ExtractAsync(string url,
        IDictionary<string, string>?                         headers           = null,
        CancellationToken                                    cancellationToken = default)
    {
        var sources = new List<VideoSource>();

        try
        {
            var referer = headers.GetReferer();
            _logger.LogInformation("fetching embed page: {Url}", url);

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent",
                                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            request.Headers.Add("Referer", referer ?? "https://vidsonic.net/");

            var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("embed page returned status {StatusCode} for URL: {Url}",
                                   response.StatusCode,
                                   url);
                return sources;
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrEmpty(html))
            {
                _logger.LogWarning("embed page returned empty HTML for URL: {Url}", url);
                return sources;
            }

            var hexMatch = HexStringRegex().Match(html);
            if (!hexMatch.Success)
            {
                _logger.LogWarning("could not find obfuscated video URL hex string in HTML for: {Url}", url);
                return sources;
            }

            var hexString = hexMatch.Groups[1].Value;
            var videoUrl  = DecodeHexUrl(hexString);

            if (string.IsNullOrEmpty(videoUrl))
            {
                _logger.LogWarning("failed to decode video URL from hex string for: {Url}", url);
                return sources;
            }

            _logger.LogInformation("extracted video URL: {VideoUrl}", videoUrl);

            var hostReferer = "https://vidsonic.net/";

            if (videoUrl.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
            {
                var extractedSources = await _m3U8Extractor.ExtractAsync(videoUrl,
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
                        { "Referer", hostReferer },
                        {
                            "User-Agent",
                            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"
                        }
                    };
                    sources.Add(src);
                }

                if (!sources.Any())
                {
                    sources.Add(new VideoSource
                    {
                        Url  = videoUrl,
                        Type = VideoType.M3U8,
                        Headers = new Dictionary<string, string>
                        {
                            { "Referer", hostReferer },
                            {
                                "User-Agent",
                                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"
                            }
                        }
                    });
                }
            }
            else
            {
                sources.Add(new VideoSource
                {
                    Url  = videoUrl,
                    Type = VideoType.Mp4,
                    Headers = new Dictionary<string, string>
                    {
                        { "Referer", hostReferer },
                        {
                            "User-Agent",
                            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"
                        }
                    }
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "error extracting video sources for URL: {Url}", url);
        }

        return sources;
    }

    private static string DecodeHexUrl(string hexString)
    {
        try
        {
            var clean = hexString.Replace("|", "");
            var bytes = new byte[clean.Length / 2];
            for (var i = 0; i < clean.Length; i += 2)
            {
                bytes[i / 2] = Convert.ToByte(clean.Substring(i, 2), 16);
            }

            var decoded = Encoding.UTF8.GetString(bytes);
            return new string(decoded.Reverse().ToArray());
        }
        catch
        {
            return string.Empty;
        }
    }

    [GeneratedRegex(@"_0x1\s*=\s*['""]([0-9a-fA-F]+(?:\|[0-9a-fA-F]+)+)['""]", RegexOptions.Compiled)]
    private static partial Regex HexStringRegex();
}
