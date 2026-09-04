using Microsoft.Extensions.Logging;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Migurdex.Core.Extractors;

public partial class MailRuExtractor : IExtractor
{
    private readonly HttpClient               _httpClient;
    private readonly ILogger<MailRuExtractor> _logger;

    public MailRuExtractor(ISharedBridge bridge, ILogger<MailRuExtractor> logger)
    {
        _httpClient = bridge.CreateHttpClient();
        _logger     = logger;
    }

    public string Name => "MailRu";

    public bool CanExtract(string url)
    {
        return url.Contains("my.mail.ru", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<List<VideoSource>> ExtractAsync(string url,
        IDictionary<string, string>?                         headers           = null,
        CancellationToken                                    cancellationToken = default)
    {
        var sources = new List<VideoSource>();

        try
        {
            var videoId = ExtractVideoId(url);
            if (string.IsNullOrEmpty(videoId))
            {
                _logger.LogWarning("could not extract video ID from URL: {Url}", url);

                return sources;
            }

            var metaUrl = $"https://my.mail.ru/+/video/meta/{videoId}";
            _logger.LogDebug("fetching Mail.ru meta url: {MetaUrl}", metaUrl);

            var request = new HttpRequestMessage(HttpMethod.Get, metaUrl);
            request.Headers.Add("Referer", $"https://my.mail.ru/video/embed/{videoId}");

            var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("meta page request failed");

                return sources;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrEmpty(json))
            {
                _logger.LogWarning("meta page empty response for: {MetaUrl}", metaUrl);

                return sources;
            }

            using var doc  = JsonDocument.Parse(json);
            var       root = doc.RootElement;

            if (root.TryGetProperty("videos", out var videosProp) && videosProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var video in videosProp.EnumerateArray())
                {
                    if (video.TryGetProperty("key", out var keyProp) && video.TryGetProperty("url", out var urlProp))
                    {
                        var key    = keyProp.GetString() ?? "Unknown";
                        var rawUrl = urlProp.GetString();
                        if (string.IsNullOrEmpty(rawUrl))
                        {
                            continue;
                        }

                        var videoUrl = rawUrl.StartsWith("//") ? "https:" + rawUrl : rawUrl;
                        if (!videoUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        {
                            videoUrl = "https://" + videoUrl.TrimStart('/');
                        }

                        sources.Add(new VideoSource
                        {
                            Url     = videoUrl,
                            Quality = key,
                            Type    = VideoType.Mp4
                        });
                    }
                }
            }
            else
            {
                _logger.LogWarning("could not find 'videos' array in response JSON");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "failed to extract Mail.ru video sources for: {Url}", url);
        }

        return sources;
    }

    [GeneratedRegex(@"(?:embed|meta)/([0-9]+)")]
    private static partial Regex EmbedMetaRegex();

    [GeneratedRegex(@"([0-9]{10,})")]
    private static partial Regex DirectIdRegex();

    private static string ExtractVideoId(string url)
    {
        var match = EmbedMetaRegex().Match(url);
        if (match.Success)
        {
            return match.Groups[1].Value;
        }

        match = DirectIdRegex().Match(url);
        if (match.Success)
        {
            return match.Groups[1].Value;
        }

        return string.Empty;
    }
}
