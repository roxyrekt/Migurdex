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

            var tauApiUrl = $"https://tau-video.xyz/api/video/{tauId}";
            var embedUrl  = $"https://tau-video.xyz/embed/{tauId}";

            _logger.LogDebug("resolving Tau Video: {ApiUrl}", tauApiUrl);

            var request = new HttpRequestMessage(HttpMethod.Get, tauApiUrl);
            request.Headers.Add("Referer", embedUrl);
            request.Headers.Add("Accept", "application/json");

            var response = await _httpClient.SendAsync(request, cancellationToken);
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

            using var doc = JsonDocument.Parse(responseJson);
            if (doc.RootElement.TryGetProperty("urls", out var urlsProp) && urlsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in urlsProp.EnumerateArray())
                {
                    var label = item.TryGetProperty("label", out var lProp) ? lProp.GetString() ?? "Multi" : "Multi";
                    var streamUrl = item.TryGetProperty("url", out var uProp) ? uProp.GetString() ?? "" : "";

                    if (!string.IsNullOrEmpty(streamUrl))
                    {
                        sources.Add(new VideoSource
                        {
                            Url     = streamUrl,
                            Quality = label,
                            Type = streamUrl.Contains(".m3u8")
                                       ? VideoType.M3U8
                                       : streamUrl.Contains(".mp4")
                                           ? VideoType.Mp4
                                           : VideoType.Embed
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "failed to extract Tau Video sources for: {Url}", url);
        }

        return sources;
    }

    [GeneratedRegex(@"/(?:embed|api/video)/([^/?]+)")]
    private static partial Regex TauIdRegex();
}
