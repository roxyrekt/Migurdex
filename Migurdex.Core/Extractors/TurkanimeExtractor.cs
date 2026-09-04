using Microsoft.Extensions.Logging;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Infrastructure;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Migurdex.Core.Extractors;

public partial class TurkanimeExtractor : IExtractor
{
    private readonly HttpClient                  _httpClient;
    private readonly ILogger<TurkanimeExtractor> _logger;
    private readonly IMp4MetadataReader          _metadataReader;

    public TurkanimeExtractor(ISharedBridge bridge)
    {
        _httpClient     = bridge.CreateHttpClient(o => o.UseCookies = true);
        _logger         = bridge.CreateLogger<TurkanimeExtractor>();
        _metadataReader = bridge.MetadataReader;
    }

    public string Name => "Turkanime";

    public bool CanExtract(string url)
    {
        return url.Contains("turkanime.tv/player/", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<List<VideoSource>> ExtractAsync(string url,
        IDictionary<string, string>?                         headers           = null,
        CancellationToken                                    cancellationToken = default)
    {
        var sources = new List<VideoSource>();

        try
        {
            _logger.LogDebug("fetching player page: {Url}", url);

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent",
                                "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:152.0) Gecko/20100101 Firefox/152.0");
            request.AddHeaders(headers);

            var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("player page failed: {StatusCode}",
                                   response.StatusCode);

                return sources;
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken);

            if (html.Contains("Bot erişimi engellendi", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("refreshing cookies...");

                request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("User-Agent",
                                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:152.0) Gecko/20100101 Firefox/152.0");
                request.AddHeaders(headers);

                response = await _httpClient.SendAsync(request, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("player page failed: {StatusCode}",
                                       response.StatusCode);

                    return sources;
                }

                html = await response.Content.ReadAsStringAsync(cancellationToken);
            }

            var apiUrlMatch = ApiUrlRegex().Match(html);
            if (!apiUrlMatch.Success)
            {
                _logger.LogWarning("could not find apiURL in HTML for {Url}", url);

                return sources;
            }

            var apiUrl = apiUrlMatch.Groups[1].Value;
            if (!apiUrl.EndsWith("true"))
            {
                apiUrl = apiUrl.TrimEnd('/') + "/true";
            }

            _logger.LogDebug("fetching sources from API: {ApiUrl}", apiUrl);

            var apiRequest = new HttpRequestMessage(HttpMethod.Get, apiUrl);
            apiRequest.Headers.Add("User-Agent",
                                   "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:152.0) Gecko/20100101 Firefox/152.0");

            apiRequest.Headers.Add("Referer", url);
            apiRequest.Headers.Add("X-Requested-With", "XMLHttpRequest");

            const string csrfToken = "EqdGHqwZJvydjfbmuYsZeGvBxDxnQXeARRqUNbhRYnPEWqdDnYFEKVBaUPCAGTZA";

            apiRequest.Headers.Add("Csrf-Token", csrfToken);

            var apiResponse = await _httpClient.SendAsync(apiRequest, cancellationToken);
            if (!apiResponse.IsSuccessStatusCode)
            {
                _logger.LogWarning("API failed: {StatusCode}",
                                   apiResponse.StatusCode);

                return sources;
            }

            var       jsonString = await apiResponse.Content.ReadAsStringAsync(cancellationToken);
            using var doc        = JsonDocument.Parse(jsonString);

            _logger.LogDebug("parsed API response: {JsonString}", jsonString);

            if (doc.RootElement.TryGetProperty("response", out var responseObj)
                && responseObj.TryGetProperty("sources", out var sourcesArr))
            {
                foreach (var source in sourcesArr.EnumerateArray())
                {
                    var fileUrl = source.GetProperty("file").GetString() ?? "";
                    var label   = source.TryGetProperty("label", out var lbl) ? lbl.GetString() ?? "Auto" : "Auto";
                    var typeStr = source.TryGetProperty("type", out var typeProp) ? typeProp.GetString() ?? "" : "";

                    if (string.IsNullOrEmpty(fileUrl))
                    {
                        continue;
                    }

                    var type = VideoType.Mp4;
                    if (fileUrl.Contains(".m3u8") || typeStr.Contains("application/x-mpegURL"))
                    {
                        type = VideoType.M3U8;
                    }

                    if (!label.Contains("p", StringComparison.OrdinalIgnoreCase))
                    {
                        label = await _metadataReader.GetVideoQualityAsync(
                                    fileUrl,
                                    cancellationToken: cancellationToken);
                    }

                    var srcHeaders = new Dictionary<string, string>
                    {
                        {
                            "User-Agent",
                            "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:152.0) Gecko/20100101 Firefox/152.0"
                        }
                    };

                    sources.Add(new VideoSource
                    {
                        Url     = fileUrl,
                        Quality = label,
                        Type    = type,
                        Headers = srcHeaders
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "failed to extract video sources for: {Url}", url);
        }

        return sources;
    }

    [GeneratedRegex(@"const\s+apiURL\s*=\s*'([^']+)'")]
    private static partial Regex ApiUrlRegex();
}
