using Microsoft.Extensions.Logging;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Infrastructure;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Migurdex.Core.Extractors;

public partial class FirestreamExtractor : IExtractor
{
    private readonly HttpClient                   _httpClient;
    private readonly ILogger<FirestreamExtractor> _logger;
    private readonly M3U8PlaylistExtractor        _m3U8Extractor;

    public FirestreamExtractor(M3U8PlaylistExtractor m3U8Extractor, ISharedBridge bridge)
    {
        _httpClient    = bridge.CreateHttpClient();
        _m3U8Extractor = m3U8Extractor;
        _logger        = bridge.CreateLogger<FirestreamExtractor>();
    }

    public string Name => "Firestream";

    public bool CanExtract(string url)
    {
        return url.Contains("firestream.to", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<List<VideoSource>> ExtractAsync(string url,
        IDictionary<string, string>?                         headers           = null,
        CancellationToken                                    cancellationToken = default)
    {
        var sources = new List<VideoSource>();

        try
        {
            var referer = headers.GetReferer();
            var slug    = ExtractSlug(url);
            if (string.IsNullOrEmpty(slug))
            {
                _logger.LogWarning("could not extract video slug from URL: {Url}", url);

                return sources;
            }

            var embedUrl = url.Contains("/e/") ? url : $"https://firestream.to/e/{slug}";
            _logger.LogInformation("fetching embed page: {EmbedUrl}", embedUrl);

            var pageRequest = new HttpRequestMessage(HttpMethod.Get, embedUrl);
            pageRequest.Headers.Add("User-Agent",
                                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            pageRequest.Headers.Add("Referer", referer ?? "https://firestream.to/");

            var pageResponse = await _httpClient.SendAsync(pageRequest, cancellationToken);
            if (!pageResponse.IsSuccessStatusCode)
            {
                _logger.LogWarning("embed page failed: {Status} for URL: {Url}",
                                   pageResponse.StatusCode,
                                   url);

                return sources;
            }

            var html = await pageResponse.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrEmpty(html))
            {
                _logger.LogWarning("embed page returned empty HTML for URL: {Url}", url);

                return sources;
            }

            var blobMatch = TokenBlobRegex().Match(html);
            if (!blobMatch.Success)
            {
                _logger.LogWarning("could not find token-blob element in HTML for slug: {Slug}", slug);

                return sources;
            }

            var tokenBlob = blobMatch.Groups[1].Value.Trim();
            if (string.IsNullOrEmpty(tokenBlob))
            {
                _logger.LogWarning("empty token-blob found for slug: {Slug}", slug);

                return sources;
            }

            var resolveUrl = $"https://firestream.to/api/videos/{Uri.EscapeDataString(slug)}/resolve";
            _logger.LogDebug("requesting signed URLs from: {ResolveUrl}", resolveUrl);

            var resolveBody = JsonSerializer.Serialize(new
            {
                blob = tokenBlob
            });
            var resolveRequest = new HttpRequestMessage(HttpMethod.Post, resolveUrl)
            {
                Content = new StringContent(resolveBody, Encoding.UTF8, "application/json")
            };

            resolveRequest.Headers.Add("User-Agent",
                                       "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            resolveRequest.Headers.Add("Referer", embedUrl);
            resolveRequest.Headers.Add("Origin", "https://firestream.to");

            var resolveResponse = await _httpClient.SendAsync(resolveRequest, cancellationToken);
            if (!resolveResponse.IsSuccessStatusCode)
            {
                _logger.LogWarning("resolve API returned status: {Status} for slug: {Slug}",
                                   resolveResponse.StatusCode,
                                   slug);

                return sources;
            }

            var       json = await resolveResponse.Content.ReadAsStringAsync(cancellationToken);
            using var doc  = JsonDocument.Parse(json);
            var       root = doc.RootElement;

            string? signedHdUrl = null;
            string? signedSdUrl = null;

            if (root.TryGetProperty("signedVideoUrl", out var hdProp) && hdProp.ValueKind == JsonValueKind.String)
            {
                signedHdUrl = hdProp.GetString();
            }

            if (root.TryGetProperty("signedVideoSdUrl", out var sdProp) && sdProp.ValueKind == JsonValueKind.String)
            {
                signedSdUrl = sdProp.GetString();
            }

            if (string.IsNullOrEmpty(signedHdUrl) && string.IsNullOrEmpty(signedSdUrl))
            {
                _logger.LogWarning("no signed video URLs returned for slug: {Slug}", slug);

                return sources;
            }

            if (!string.IsNullOrEmpty(signedHdUrl))
            {
                await ProcessSignedUrlAsync(signedHdUrl, "HD", embedUrl, sources);
            }

            if (!string.IsNullOrEmpty(signedSdUrl))
            {
                await ProcessSignedUrlAsync(signedSdUrl, "SD", embedUrl, sources);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "error extracting video sources for URL: {Url}", url);
        }

        return sources;
    }

    private async Task ProcessSignedUrlAsync(string signedUrl,
        string                                      defaultQuality,
        string                                      referer,
        List<VideoSource>                           sources)
    {
        if (signedUrl.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("processing M3U8 playlist for quality {Quality}: {Url}",
                                   defaultQuality,
                                   signedUrl);

            var extracted = await _m3U8Extractor.ExtractAsync(signedUrl,
                                                              new Dictionary<string, string>
                                                              {
                                                                  { "Referer", "https://firestream.to/" }
                                                              });
            foreach (var src in extracted)
            {
                sources.Add(src);
            }

            if (!extracted.Any())
            {
                sources.Add(new VideoSource
                {
                    Url  = signedUrl,
                    Type = VideoType.M3U8
                });
            }
        }
        else
        {
            sources.Add(new VideoSource
            {
                Url  = signedUrl,
                Type = VideoType.Mp4
            });
        }
    }

    private static string ExtractSlug(string url)
    {
        var match = SlugRegex().Match(url);
        if (match.Success)
        {
            return match.Groups[1].Value;
        }

        var uri         = new Uri(url);
        var lastSegment = uri.Segments.LastOrDefault()?.Trim('/');
        return lastSegment ?? string.Empty;
    }

    [GeneratedRegex(@"/(?:e|v|d)/([a-zA-Z0-9_-]+)", RegexOptions.Compiled)]
    private static partial Regex SlugRegex();

    [GeneratedRegex(@"<script\s+id=""token-blob""[^>]*>([^<]+)</script>")]
    private static partial Regex TokenBlobRegex();
}
