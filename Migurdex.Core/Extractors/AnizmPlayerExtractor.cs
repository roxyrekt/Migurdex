using Microsoft.Extensions.Logging;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Infrastructure;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Migurdex.Core.Extractors;

public partial class AnizmPlayerExtractor : IExtractor
{
    private readonly HttpClient                    _httpClient;
    private readonly ILogger<AnizmPlayerExtractor> _logger;
    private readonly M3U8PlaylistExtractor         _m3U8Extractor;

    public AnizmPlayerExtractor(M3U8PlaylistExtractor m3U8Extractor, ISharedBridge bridge)
    {
        _httpClient    = bridge.CreateHttpClient();
        _m3U8Extractor = m3U8Extractor;
        _logger        = bridge.CreateLogger<AnizmPlayerExtractor>();
    }

    public string Name => "AnizmPlayer";

    public bool CanExtract(string url)
    {
        return url.Contains("anizmplayer.com/video/", StringComparison.OrdinalIgnoreCase)
               || url.Contains("anizmplayer.com/player/", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<List<VideoSource>> ExtractAsync(string url,
        IDictionary<string, string>?                         headers           = null,
        CancellationToken                                    cancellationToken = default)
    {
        var sources = new List<VideoSource>();

        var hash = ExtractHashFromUrl(url);
        if (string.IsNullOrEmpty(hash))
        {
            _logger.LogWarning("could not extract hash from AnizmPlayer URL: {Url}", url);

            return sources;
        }

        var refererUrl = headers.GetReferer();
        var apiBody    = $"hash={hash}&r={WebUtility.UrlEncode(refererUrl)}";

        var apiUrl = $"https://anizmplayer.com/player/index.php?data={hash}&do=getVideo";
        var request = new HttpRequestMessage(HttpMethod.Post, apiUrl)
        {
            Content = new StringContent(apiBody, Encoding.UTF8, "application/x-www-form-urlencoded")
        };
        request.Headers.Add("Referer", "https://anizmplayer.com/");
        request.Headers.Add("Origin", "https://anizmplayer.com");
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");

        var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("player API request failed");

            return sources;
        }

        var jsonResponse = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrEmpty(jsonResponse))
        {
            _logger.LogWarning("player API empty response for hash: {Hash}", hash);

            return sources;
        }

        try
        {
            using var doc = JsonDocument.Parse(jsonResponse);
            if (doc.RootElement.TryGetProperty("securedLink", out var secLink))
            {
                var sUrl = secLink.GetString() ?? "";
                if (!string.IsNullOrEmpty(sUrl))
                {
                    var extracted = await _m3U8Extractor.ExtractAsync(sUrl,
                                                                      new Dictionary<string, string>
                                                                      {
                                                                          { "Referer", "https://anizmplayer.com/" }
                                                                      });
                    if (extracted.Count > 0)
                    {
                        sources.AddRange(extracted);
                    }
                    else
                    {
                        sources.Add(new VideoSource
                        {
                            Url  = sUrl,
                            Type = VideoType.M3U8
                        });
                    }
                }
            }

            if (doc.RootElement.TryGetProperty("video", out var videoArray))
            {
                foreach (var video in videoArray.EnumerateArray())
                {
                    var fileUrl = video.GetProperty("file").GetString() ?? "";
                    var label   = video.TryGetProperty("label", out var lbl) ? lbl.GetString() ?? "Auto" : "Auto";

                    if (fileUrl.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
                    {
                        var extracted = await _m3U8Extractor.ExtractAsync(fileUrl,
                                                                          new Dictionary<string, string>
                                                                          {
                                                                              { "Referer", "https://anizmplayer.com/" }
                                                                          });
                        if (extracted.Count > 0)
                        {
                            sources.AddRange(extracted);
                        }
                        else
                        {
                            sources.Add(new VideoSource
                            {
                                Url  = fileUrl,
                                Type = VideoType.M3U8
                            });
                        }
                    }
                    else
                    {
                        sources.Add(new VideoSource
                        {
                            Url     = fileUrl,
                            Quality = label,
                            Type    = VideoType.Mp4
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "failed to parse AnizmPlayer json response for: {Url}", url);
        }

        return sources;
    }

    [GeneratedRegex(@"anizmplayer\.com/(?:video|player)/([a-f0-9]{32})")]
    private static partial Regex PlayerHashRegex();

    [GeneratedRegex(@"index\.php\?data=([a-f0-9]{32})")]
    private static partial Regex DataParamRegex();

    private static string ExtractHashFromUrl(string url)
    {
        var match = PlayerHashRegex().Match(url);
        if (match.Success)
        {
            return match.Groups[1].Value;
        }

        match = DataParamRegex().Match(url);
        if (match.Success)
        {
            return match.Groups[1].Value;
        }

        return string.Empty;
    }
}
