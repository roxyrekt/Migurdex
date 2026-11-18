using Microsoft.Extensions.Logging;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Migurdex.Core.Extractors;

public partial class TauVideoExtractor : IExtractor
{
    private readonly HttpClient                 _httpClient;
    private readonly ILogger<TauVideoExtractor> _logger;

    public TauVideoExtractor(ISharedBridge bridge, ILogger<TauVideoExtractor> logger)
    {
        _httpClient = bridge.CreateHttpClient();
        _logger     = logger;
    }

    public string Name => "Tau Video";

    public bool CanExtract(string url)
    {
        return url.Contains("tau-video.xyz", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<List<VideoSource>> ExtractAsync(string url,
        IDictionary<string, string>?                         headers           = null,
        CancellationToken                                    cancellationToken = default)
    {
        var sources = new List<VideoSource>();

        try
        {
            var tauIdMatch = TauIdRegex().Match(url);
            if (!tauIdMatch.Success)
            {
                _logger.LogWarning("could not extract Tau ID from URL: {Url}", url);

                return sources;
            }

            var tauId = tauIdMatch.Groups[1].Value;
            var vid   = ExtractQueryParam(url, "vid");

            var tauApiUrl = string.IsNullOrEmpty(vid)
                                ? $"https://tau-video.xyz/api/video/{tauId}"
                                : $"https://tau-video.xyz/api/video/{tauId}?vid={Uri.EscapeDataString(vid)}";
            var embedUrl = string.IsNullOrEmpty(vid)
                               ? $"https://tau-video.xyz/embed/{tauId}"
                               : $"https://tau-video.xyz/embed/{tauId}?vid={Uri.EscapeDataString(vid)}";

            _logger.LogDebug("resolving Tau Video: {ApiUrl}", tauApiUrl);

            using var request = new HttpRequestMessage(HttpMethod.Get, tauApiUrl);
            request.Headers.Add("Referer", embedUrl);
            request.Headers.Add("Accept", "application/json");

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("tau API failed for: {ApiUrl}", tauApiUrl);

                return sources;
            }

            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrEmpty(responseJson))
            {
                _logger.LogWarning("tau API empty response for: {ApiUrl}", tauApiUrl);

                return sources;
            }

            using var doc  = JsonDocument.Parse(responseJson);
            var       root = doc.RootElement;

            var subtitles = ParseSubtitles(root);

            if (root.TryGetProperty("urls", out var urlsProp) && urlsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in urlsProp.EnumerateArray())
                {
                    var label = item.TryGetProperty("label", out var lProp) ? lProp.GetString() ?? "Multi" : "Multi";
                    var streamUrl = item.TryGetProperty("url", out var uProp) ? uProp.GetString() ?? "" : "";

                    if (!string.IsNullOrEmpty(streamUrl))
                    {
                        sources.Add(new VideoSource
                        {
                            Url       = streamUrl,
                            Quality   = label,
                            Subtitles = subtitles.Count > 0 ? subtitles : null,
                            Type = streamUrl.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)
                                       ? VideoType.M3U8
                                       : streamUrl.Contains(".mp4", StringComparison.OrdinalIgnoreCase)
                                           ? VideoType.Mp4
                                           : VideoType.Embed
                        });
                    }
                }
            }

            if (sources.Count == 0
                && root.TryGetProperty("hls", out var hlsProp)
                && hlsProp.ValueKind == JsonValueKind.String)
            {
                var hlsUrl = hlsProp.GetString();
                if (!string.IsNullOrWhiteSpace(hlsUrl))
                {
                    sources.Add(new VideoSource
                    {
                        Url       = hlsUrl.Trim(),
                        Quality   = "Auto",
                        Subtitles = subtitles.Count > 0 ? subtitles : null,
                        Type      = VideoType.M3U8
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "failed to extract Tau Video sources for: {Url}", url);
        }

        return sources;
    }

    private static string? ExtractQueryParam(string url, string paramName)
    {
        var qIdx = url.IndexOf('?');
        if (qIdx < 0 || qIdx >= url.Length - 1)
        {
            return null;
        }

        var query = url[(qIdx + 1)..];
        var hashIdx = query.IndexOf('#');
        if (hashIdx >= 0)
        {
            query = query[..hashIdx];
        }

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eqIdx = pair.IndexOf('=');
            if (eqIdx <= 0)
            {
                continue;
            }

            var key = Uri.UnescapeDataString(pair[..eqIdx]);
            if (string.Equals(key, paramName, StringComparison.OrdinalIgnoreCase))
            {
                var val = Uri.UnescapeDataString(pair[(eqIdx + 1)..]).Trim();
                return string.IsNullOrEmpty(val) ? null : val;
            }
        }

        return null;
    }

    private static List<Subtitle> ParseSubtitles(JsonElement root)
    {
        var list = new List<Subtitle>();
        if (!root.TryGetProperty("subs", out var subsProp) || subsProp.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var item in subsProp.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var subUrl = item.TryGetProperty("url", out var urlProp) && urlProp.ValueKind == JsonValueKind.String
                             ? urlProp.GetString()
                             : null;

            if (string.IsNullOrWhiteSpace(subUrl)
                && item.TryGetProperty("originalUrl", out var origProp)
                && origProp.ValueKind == JsonValueKind.String)
            {
                subUrl = origProp.GetString();
            }

            if (string.IsNullOrWhiteSpace(subUrl) && item.TryGetProperty("id", out var idProp))
            {
                var subId = idProp.ValueKind switch
                {
                    JsonValueKind.Number => idProp.GetRawText(),
                    JsonValueKind.String => idProp.GetString()?.Trim(),
                    _                    => null
                };

                if (!string.IsNullOrWhiteSpace(subId))
                {
                    subUrl = $"https://tau-video.xyz/vtt/{Uri.EscapeDataString(subId)}";
                }
            }

            if (string.IsNullOrWhiteSpace(subUrl))
            {
                continue;
            }

            var trimmedUrl = subUrl.Trim();
            if (list.Any(s => string.Equals(s.Url, trimmedUrl, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var label = item.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String
                            ? nameProp.GetString()?.Trim()
                            : null;
            var language = item.TryGetProperty("language", out var langProp)
                           && langProp.ValueKind == JsonValueKind.String
                               ? langProp.GetString()?.Trim()
                               : null;

            list.Add(new Subtitle
            {
                Url      = trimmedUrl,
                Label    = string.IsNullOrWhiteSpace(label) ? null : label,
                Language = string.IsNullOrWhiteSpace(language) ? "" : language,
                Format   = ResolveSubtitleFormat(trimmedUrl)
            });
        }

        return list;
    }

    private static string? ResolveSubtitleFormat(string url)
    {
        var cleanUrl = url.Split('?', '#')[0];
        var ext      = Path.GetExtension(cleanUrl).TrimStart('.').ToLowerInvariant();
        if (ext is "ass" or "ssa" or "srt" or "vtt")
        {
            return ext;
        }

        return url.Contains("/vtt/", StringComparison.OrdinalIgnoreCase) ? "vtt" : null;
    }

    [GeneratedRegex(@"/(?:embed|api/video)/([^/?]+)")]
    private static partial Regex TauIdRegex();
}
