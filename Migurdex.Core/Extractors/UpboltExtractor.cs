using Microsoft.Extensions.Logging;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Infrastructure;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Text.RegularExpressions;
using System.Web;

namespace Migurdex.Core.Extractors;

public partial class UpboltExtractor : IExtractor
{
    private readonly HttpClient               _httpClient;
    private readonly ILogger<UpboltExtractor> _logger;
    private readonly M3U8PlaylistExtractor    _m3U8Extractor;

    public UpboltExtractor(M3U8PlaylistExtractor m3U8Extractor, ISharedBridge bridge)
    {
        _httpClient    = bridge.CreateHttpClient();
        _m3U8Extractor = m3U8Extractor;
        _logger        = bridge.CreateLogger<UpboltExtractor>();
    }

    public string Name => "Upbolt";

    public bool CanExtract(string url)
    {
        return !string.IsNullOrWhiteSpace(url) && url.Contains("upbolt.to", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<List<VideoSource>> ExtractAsync(string url,
        IDictionary<string, string>?                         headers           = null,
        CancellationToken                                    cancellationToken = default)
    {
        var sources = new List<VideoSource>();

        try
        {
            var referer  = headers.GetReferer();
            var fileCode = ExtractFileCode(url);
            if (string.IsNullOrEmpty(fileCode))
            {
                _logger.LogWarning("could not extract file code from URL: {Url}", url);
                return sources;
            }

            _logger.LogInformation("posting to /dl for file code: {FileCode}", fileCode);

            const string postUrl      = "https://upbolt.to/dl";
            var          embedReferer = $"https://upbolt.to/e/{fileCode}";

            using var request = new HttpRequestMessage(HttpMethod.Post, postUrl);
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                { "op", "embed" },
                { "file_code", fileCode },
                { "auto", "1" },
                { "referer", referer ?? "" }
            });

            request.Headers.Add("User-Agent",
                                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            request.Headers.Add("Referer", embedReferer);
            request.Headers.Add("Origin", "https://upbolt.to");

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("pOST /dl returned status {StatusCode} for file code: {FileCode}",
                                   response.StatusCode,
                                   fileCode);
                return sources;
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(html))
            {
                _logger.LogWarning("pOST /dl empty response for file code: {FileCode}", fileCode);
                return sources;
            }

            var fileMatch = FileRegex().Match(html);
            if (!fileMatch.Success)
            {
                _logger.LogWarning("could not find M3U8 file URL in HTML response for file code: {FileCode}",
                                   fileCode);
                return sources;
            }

            var m3u8Url = fileMatch.Groups[1].Value;
            _logger.LogInformation("extracted M3U8 URL: {M3U8Url}", m3u8Url);

            const string hostReferer = "https://upbolt.to/";
            var defaultHeaders = new Dictionary<string, string>
            {
                { "Referer", hostReferer },
                {
                    "User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"
                }
            };

            var extractedSources = await _m3U8Extractor.ExtractAsync(m3u8Url,
                                                                     !string.IsNullOrEmpty(hostReferer)
                                                                         ? new Dictionary<string, string>
                                                                         {
                                                                             { "Referer", hostReferer }
                                                                         }
                                                                         : null);
            foreach (var src in extractedSources)
            {
                src.Headers = defaultHeaders;
                sources.Add(src);
            }

            if (!sources.Any())
            {
                sources.Add(new VideoSource
                {
                    Url     = m3u8Url,
                    Type    = VideoType.M3U8,
                    Headers = defaultHeaders
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "error extracting video sources for URL: {Url}", url);
        }

        return sources;
    }

    private static string ExtractFileCode(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length > 0)
            {
                var lastSegment = segments[^1].Replace(".html", "", StringComparison.OrdinalIgnoreCase);

                if (lastSegment.StartsWith("emb-", StringComparison.OrdinalIgnoreCase))
                {
                    lastSegment = lastSegment[4..];
                }

                if (lastSegment.Equals("dl", StringComparison.OrdinalIgnoreCase)
                    || lastSegment.Equals("embed", StringComparison.OrdinalIgnoreCase)
                    || lastSegment.Equals("e", StringComparison.OrdinalIgnoreCase))
                {
                    var query = HttpUtility.ParseQueryString(uri.Query);
                    return query["file_code"] ?? query["code"] ?? string.Empty;
                }

                return lastSegment;
            }
        }

        var match = FileCodeRegex().Match(url);
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    [GeneratedRegex(@"file\s*:\s*[""'](https?://[^""']+\.m3u8[^""']*)[""']",
                    RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex FileRegex();

    [GeneratedRegex(@"[/?](?:e/|d/|v/|f/|emb-)?([a-zA-Z0-9_-]{6,})", RegexOptions.IgnoreCase)]
    private static partial Regex FileCodeRegex();
}
