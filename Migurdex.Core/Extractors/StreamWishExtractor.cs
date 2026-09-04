using Microsoft.Extensions.Logging;
using Migurdex.Core.Utils;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Infrastructure;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Text.RegularExpressions;

namespace Migurdex.Core.Extractors;

public partial class StreamWishExtractor : IExtractor
{
    private static readonly HashSet<string> _supportedDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "streamwish.com",
        "embedwish.com",
        "wishembed.pro",
        "vidcloud.top",
        "jwplayerhls.com",
        "wishonly.site",
        "dwish.pro",
        "cloudwish.xyz",
        "playerwish.com",
        "rapidplayers.com",
        "streamhg.com",
        "hlsflex.com",
        "swiftplayers.com",
        "ultpreplayer.com",
        "recordplay.biz",
        "hgplaycdn.com",
        "hailindihg.com",
        "davioad.com",
        "hglink.to",
        "medixiru.com",
        "hgcloud.to",
        "hgplayer.sbs",
        "audinifer.com",
        "vibuxer.com",
        "hanerix.com",
        "masukestin.com",
        "playnixes.com",
        "hglamioz.com",
        "niramirus.com",
        "ghbrisk.com"
    };

    private static readonly string[] _activeMirrors =
    [
        "audinifer.com",
        "ghbrisk.com",
        "hanerix.com",
        "vibuxer.com",
        "playnixes.com"
    ];

    private readonly HttpClient                   _httpClient;
    private readonly ILogger<StreamWishExtractor> _logger;
    private readonly M3U8PlaylistExtractor        _m3U8Extractor;

    public StreamWishExtractor(M3U8PlaylistExtractor m3U8Extractor, ISharedBridge bridge)
    {
        _httpClient    = bridge.CreateHttpClient(o => o.AllowAutoRedirect = true);
        _m3U8Extractor = m3U8Extractor;
        _logger        = bridge.CreateLogger<StreamWishExtractor>();
    }

    public string Name => "StreamWish";

    public bool CanExtract(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var host = uri.Host;

            return _supportedDomains.Any(d => host.Equals(d, StringComparison.OrdinalIgnoreCase)
                                              || host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase));
        }

        return _supportedDomains.Any(d => url.Contains(d, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<List<VideoSource>> ExtractAsync(string url,
        IDictionary<string, string>?                         headers           = null,
        CancellationToken                                    cancellationToken = default)
    {
        var sources = new List<VideoSource>();

        try
        {
            var referer = headers.GetReferer();
            _logger.LogDebug("fetching extraction for URL: {Url}", url);

            var normalizedUrl = NormalizePlayerUrl(url);
            var (html, targetUrl) = await FetchHtmlAsync(normalizedUrl, referer, cancellationToken);

            var playlistUrl = TryExtractFromHtml(html);

            if (string.IsNullOrEmpty(playlistUrl))
            {
                var fileCode = ExtractFileCode(url);

                if (!string.IsNullOrEmpty(fileCode))
                {
                    _logger.LogDebug("attempting active mirror fallback for code: {Code}", fileCode);

                    foreach (var mirror in _activeMirrors)
                    {
                        var mirrorUrl = $"https://{mirror}/e/{fileCode}";
                        var (mirrorHtml, _) = await FetchHtmlAsync(mirrorUrl, referer, cancellationToken);
                        playlistUrl         = TryExtractFromHtml(mirrorHtml);

                        if (!string.IsNullOrEmpty(playlistUrl))
                        {
                            targetUrl = mirrorUrl;
                            break;
                        }
                    }
                }
            }

            if (string.IsNullOrEmpty(playlistUrl))
            {
                _logger.LogWarning("could not find HLS/M3U8 playlist in StreamWish page: {Url}", url);

                return sources;
            }

            _logger.LogInformation("extracted StreamWish playlist URL: {PlaylistUrl}", playlistUrl);

            var uri         = new Uri(targetUrl);
            var hostReferer = $"{uri.Scheme}://{uri.Host}/";
            var streamHeaders = new Dictionary<string, string>
            {
                { "Referer", hostReferer }
            };

            var extractedSources = await _m3U8Extractor.ExtractAsync(playlistUrl, streamHeaders);
            foreach (var src in extractedSources)
            {
                src.Headers = new Dictionary<string, string>(streamHeaders);
                sources.Add(src);
            }

            if (sources.Count == 0)
            {
                sources.Add(new VideoSource
                {
                    Url     = playlistUrl,
                    Quality = "Auto",
                    Type    = VideoType.M3U8,
                    Headers = new Dictionary<string, string>(streamHeaders)
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "failed to extract video sources for: {Url}", url);
        }

        return sources;
    }

    private async Task<(string? Html, string FinalUrl)> FetchHtmlAsync(string url,
        string?                                                               referer,
        CancellationToken                                                     cancellationToken = default)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(referer))
            {
                request.Headers.Add("Referer", referer);
            }

            var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return (null, url);
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            return (html, url);
        }
        catch
        {
            return (null, url);
        }
    }

    private static string NormalizePlayerUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var path = uri.AbsolutePath.Trim('/');
            if (!string.IsNullOrEmpty(path) && !path.Contains('/'))
            {
                return $"{uri.Scheme}://{uri.Host}/e/{path}";
            }
        }

        return url;
    }

    private static string? ExtractFileCode(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var match = FileCodeRegex().Match(url);
        if (match.Success)
        {
            return match.Groups[1].Value;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var segment = uri.AbsolutePath.Trim('/').Split('/').LastOrDefault();
            if (!string.IsNullOrEmpty(segment) && segment.Length >= 8)
            {
                return segment.Replace(".html", "").Replace("embed-", "");
            }
        }

        return null;
    }

    private static string? TryExtractFromHtml(string? html)
    {
        if (string.IsNullOrEmpty(html) || html.Contains("loading-container", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var unpacked      = JsUnpacker.Unpack(html);
        var targetContent = !string.IsNullOrEmpty(unpacked) ? unpacked : html;

        return ExtractPlaylistUrl(targetContent, html);
    }

    private static string? ExtractPlaylistUrl(string targetContent, string rawHtml)
    {
        var matches = StreamUrlRegex().Matches(targetContent);
        if (matches.Count == 0 && targetContent != rawHtml)
        {
            matches = StreamUrlRegex().Matches(rawHtml);
        }

        if (matches.Count > 0)
        {
            var urls = matches.Select(m => m.Groups[1].Value).Distinct().ToList();

            return urls.FirstOrDefault(u => u.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)) ?? urls.First();
        }

        var fallbackMatch = DirectPlaylistRegex().Match(targetContent);
        if (!fallbackMatch.Success && targetContent != rawHtml)
        {
            fallbackMatch = DirectPlaylistRegex().Match(rawHtml);
        }

        return fallbackMatch.Success ? fallbackMatch.Groups[1].Value : null;
    }

    [GeneratedRegex(@"(?:/(?:e|f|embed-|d)/|/|^)([a-zA-Z0-9]{8,20})(?:\.html)?", RegexOptions.IgnoreCase)]
    private static partial Regex FileCodeRegex();

    [GeneratedRegex(@"(?:hls\d*|file)[""']?\s*:\s*[""'](https?://[^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex StreamUrlRegex();

    [GeneratedRegex(@"(https?://[^\s""'<>]+\.(?:m3u8|txt)(?:\?[^\s""'<>]*)?)", RegexOptions.IgnoreCase)]
    private static partial Regex DirectPlaylistRegex();
}
