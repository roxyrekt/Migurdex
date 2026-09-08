using Microsoft.Extensions.Logging;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Infrastructure;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Text.RegularExpressions;

namespace Migurdex.Core.Extractors;

public partial class StreamainExtractor : IExtractor
{
    private readonly HttpClient                  _httpClient;
    private readonly ILogger<StreamainExtractor> _logger;
    private readonly IMp4MetadataReader          _metadataReader;

    public StreamainExtractor(ISharedBridge bridge)
    {
        _httpClient     = bridge.CreateHttpClient();
        _logger         = bridge.CreateLogger<StreamainExtractor>();
        _metadataReader = bridge.MetadataReader;
    }

    public string Name => "Streamain";

    public bool CanExtract(string url)
    {
        return url.Contains("streamain.com", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<List<VideoSource>> ExtractAsync(string url,
        IDictionary<string, string>?                         headers           = null,
        CancellationToken                                    cancellationToken = default)
    {
        var sources = new List<VideoSource>();

        try
        {
            var pageUrl = url;
            if (!url.Contains("/embed/", StringComparison.OrdinalIgnoreCase))
            {
                var watchHtml = await GetPageHtmlAsync(url, referer: null, headers, cancellationToken);
                if (watchHtml is null)
                {
                    return sources;
                }

                var embedMatch = EmbedUrlRegex().Match(watchHtml);
                if (embedMatch.Success)
                {
                    pageUrl = "https://streamain.com/embed/" + embedMatch.Groups[1].Value;
                }
                else if (TryGetIdFromWatchUrl(url, out var id))
                {
                    pageUrl = "https://streamain.com/embed/" + id;
                }
                else
                {
                    _logger.LogWarning("could not find embed URL in watch page for: {Url}", url);
                    return sources;
                }

                _logger.LogDebug("resolved embed page: {EmbedUrl}", pageUrl);
            }

            var html = await GetPageHtmlAsync(pageUrl, url, headers, cancellationToken);
            if (html is null)
            {
                return sources;
            }

            var dataLinkMatch = PlaybobVideoRegex().Match(html);
            if (dataLinkMatch.Success)
            {
                var videoUrl = dataLinkMatch.Groups[1].Value;
                var quality =
                    await _metadataReader.GetVideoQualityAsync(videoUrl, cancellationToken: cancellationToken);
                sources.Add(new VideoSource
                {
                    Url     = videoUrl,
                    Quality = quality,
                    Type    = VideoType.Mp4
                });
            }
            else
            {
                dataLinkMatch = DataLinkRegex().Match(html);
                if (dataLinkMatch.Success)
                {
                    var videoUrl = dataLinkMatch.Groups[1].Value;
                    var quality =
                        await _metadataReader.GetVideoQualityAsync(videoUrl, cancellationToken: cancellationToken);
                    sources.Add(new VideoSource
                    {
                        Url     = videoUrl,
                        Quality = quality,
                        Type    = VideoType.Mp4
                    });
                }
                else
                {
                    _logger.LogWarning("could not find video link in HTML for: {Url}", url);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "failed to extract video sources for: {Url}", url);
        }

        return sources;
    }

    private async Task<string?> GetPageHtmlAsync(string pageUrl,
        string?                                         referer,
        IDictionary<string, string>?                    headers,
        CancellationToken                               cancellationToken)
    {
        _logger.LogDebug("fetching page: {Url}", pageUrl);

        using var request = new HttpRequestMessage(HttpMethod.Get, pageUrl);
        request.Headers.Add("User-Agent",
                            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

        if (!string.IsNullOrEmpty(referer))
        {
            request.Headers.Add("Referer", referer);
        }

        request.AddHeaders(headers);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("page failed: {StatusCode} for {Url}", response.StatusCode, pageUrl);
            return null;
        }

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static bool TryGetIdFromWatchUrl(string url, out string id)
    {
        id = string.Empty;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length; i++)
        {
            if (segments[i].Equals("watch", StringComparison.OrdinalIgnoreCase) && i > 0)
            {
                id = segments[i - 1];
                return true;
            }
        }

        return false;
    }

    [GeneratedRegex(@"streamain\.com/embed/([A-Za-z0-9_-]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex EmbedUrlRegex();

    [GeneratedRegex(@"id=""playbob-video""[^>]+data-link=""([^""]+)""")]
    private static partial Regex PlaybobVideoRegex();

    [GeneratedRegex(@"data-link=""([^""]+\.mp4[^""]*)""")]
    private static partial Regex DataLinkRegex();
}
