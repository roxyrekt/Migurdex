using Microsoft.Extensions.Logging;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Infrastructure;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Migurdex.Core.Extractors;

public partial class FlyfileExtractor : IExtractor
{
    private const string ApiBaseUrl = "https://api.flyfile.app/api";

    private readonly HttpClient                _httpClient;
    private readonly ILogger<FlyfileExtractor> _logger;
    private readonly M3U8PlaylistExtractor     _m3U8Extractor;
    private readonly IMp4MetadataReader        _metadataReader;

    public FlyfileExtractor(M3U8PlaylistExtractor m3U8Extractor, ISharedBridge bridge)
    {
        _httpClient     = bridge.CreateHttpClient();
        _m3U8Extractor  = m3U8Extractor;
        _metadataReader = bridge.MetadataReader;
        _logger         = bridge.CreateLogger<FlyfileExtractor>();
    }

    public string Name => "Flyfile";

    public bool CanExtract(string url)
    {
        return url.Contains("flyf.lat", StringComparison.OrdinalIgnoreCase)
               || url.Contains("aflyf.cam", StringComparison.OrdinalIgnoreCase)
               || url.Contains("flyfile.app", StringComparison.OrdinalIgnoreCase)
               || url.Contains("flyfile.io", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<List<VideoSource>> ExtractAsync(string url,
        IDictionary<string, string>?                         headers           = null,
        CancellationToken                                    cancellationToken = default)
    {
        var sources = new List<VideoSource>();

        try
        {
            var fileId = ExtractFileId(url);
            if (string.IsNullOrEmpty(fileId))
            {
                _logger.LogWarning("could not extract file ID from URL: {Url}", url);
                return sources;
            }

            var uri     = new Uri(url);
            var host    = uri.Host;
            var origin  = $"{uri.Scheme}://{host}";
            var referer = headers.GetReferer() ?? url;

            _logger.LogInformation("starting extraction for File ID: {FileId} on host: {Host}", fileId, host);

            var       fileInfoUrl     = $"{ApiBaseUrl}/public/file/{fileId}";
            using var fileInfoRequest = CreateRequest(HttpMethod.Get, fileInfoUrl, host, origin, referer);

            var fileInfoResponse = await _httpClient.SendAsync(fileInfoRequest, cancellationToken);
            if (!fileInfoResponse.IsSuccessStatusCode)
            {
                _logger.LogWarning("file info endpoint returned status {StatusCode} for fileId {FileId}",
                                   fileInfoResponse.StatusCode,
                                   fileId);
                return sources;
            }

            var       fileInfoJson = await fileInfoResponse.Content.ReadAsStringAsync(cancellationToken);
            using var fileInfoDoc  = JsonDocument.Parse(fileInfoJson);
            var       fileInfoRoot = fileInfoDoc.RootElement;

            if (fileInfoRoot.TryGetProperty("contentRedirect", out var redirectProp)
                && redirectProp.GetString() is { Length: > 0 } redirectPath)
            {
                _logger.LogInformation("detected contentRedirect: {RedirectPath}", redirectPath);
                var redirectedId = ExtractFileId(redirectPath);
                if (!string.IsNullOrEmpty(redirectedId)
                    && !redirectedId.Equals(fileId, StringComparison.OrdinalIgnoreCase))
                {
                    fileId = redirectedId;
                }
            }

            var isHls = HasHlsQualities(fileInfoRoot);

            var       assignUrl     = $"{ApiBaseUrl}/streaming/assign/{fileId}";
            using var assignRequest = CreateRequest(HttpMethod.Get, assignUrl, host, origin, referer);
            assignRequest.Headers.Add("X-Adblock-Detected", "0");

            var assignResponse = await _httpClient.SendAsync(assignRequest, cancellationToken);
            if (!assignResponse.IsSuccessStatusCode)
            {
                _logger.LogWarning("streaming assign returned status: {Status} for fileId: {FileId}",
                                   assignResponse.StatusCode,
                                   fileId);
                return sources;
            }

            var       assignJson = await assignResponse.Content.ReadAsStringAsync(cancellationToken);
            using var assignDoc  = JsonDocument.Parse(assignJson);
            var       assignRoot = assignDoc.RootElement;

            if (!assignRoot.TryGetProperty("url", out var urlProp)
                || !assignRoot.TryGetProperty("token", out var tokenProp))
            {
                _logger.LogWarning("assign response missing 'url' or 'token'. Response: {Json}", assignJson);
                return sources;
            }

            var nodeUrl     = urlProp.GetString();
            var streamToken = tokenProp.GetString();

            if (string.IsNullOrEmpty(nodeUrl) || string.IsNullOrEmpty(streamToken))
            {
                _logger.LogWarning("empty nodeUrl or streamToken received");
                return sources;
            }

            if (isHls)
            {
                var m3u8MasterUrl = $"{nodeUrl}/hls/{streamToken}/master.m3u8";
                _logger.LogInformation("resolved M3U8 Master URL: {M3u8Url}", m3u8MasterUrl);

                var extracted = await _m3U8Extractor.ExtractAsync(m3u8MasterUrl,
                                                                  new Dictionary<string, string>
                                                                  {
                                                                      { "Referer", origin },
                                                                      { "Origin", origin }
                                                                  });
                sources.AddRange(extracted);

                if (!sources.Any())
                {
                    sources.Add(new VideoSource
                    {
                        Url  = m3u8MasterUrl,
                        Type = VideoType.M3U8
                    });
                }
            }

            var rawVideoUrl = $"{nodeUrl}/raw/{streamToken}";
            _logger.LogInformation("resolved RAW Video URL: {RawUrl}", rawVideoUrl);

            var quality = await _metadataReader.GetVideoQualityAsync(rawVideoUrl, cancellationToken: cancellationToken);

            sources.Add(new VideoSource
            {
                Url     = rawVideoUrl,
                Quality = quality,
                Type    = VideoType.Mp4
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "error extracting video sources for URL: {Url}", url);
        }

        return sources;
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method,
        string                                                 requestUrl,
        string                                                 host,
        string                                                 origin,
        string                                                 referer)
    {
        var request = new HttpRequestMessage(method, requestUrl);
        request.Headers.Add("User-Agent",
                            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");

        request.Headers.Add("Accept", "application/json, text/plain, */*");
        request.Headers.Add("x-flyfile-host", host);
        request.Headers.Add("X-FlyFile-View", "embed");
        request.Headers.Add("X-Embed-Referrer", referer);
        request.Headers.Add("Referer", referer);
        request.Headers.Add("Origin", origin);
        return request;
    }

    private static bool HasHlsQualities(JsonElement fileInfoRoot)
    {
        if (!fileInfoRoot.TryGetProperty("videoAsset", out var videoAsset))
        {
            return true;
        }

        if (videoAsset.TryGetProperty("qualities", out var qualitiesProp))
        {
            if (qualitiesProp.ValueKind == JsonValueKind.Array)
            {
                return qualitiesProp.EnumerateArray()
                                    .Any(q =>
                                             q.TryGetProperty("status", out var status)
                                             && status.GetString() == "READY");
            }

            if (qualitiesProp.ValueKind == JsonValueKind.String)
            {
                var rawStr = qualitiesProp.GetString();
                if (!string.IsNullOrEmpty(rawStr) && rawStr.Contains("READY", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string ExtractFileId(string url)
    {
        var match = FileIdRegex().Match(url);
        if (match.Success)
        {
            return match.Groups[1].Value;
        }

        if (Uri.TryCreate(url, UriKind.RelativeOrAbsolute, out var uri))
        {
            var path        = uri.IsAbsoluteUri ? uri.AbsolutePath : url;
            var lastSegment = path.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            return lastSegment ?? string.Empty;
        }

        return string.Empty;
    }

    [GeneratedRegex(@"/(?:e|v|f|embed|file|download)/([a-zA-Z0-9_-]+)",
                    RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex FileIdRegex();
}
