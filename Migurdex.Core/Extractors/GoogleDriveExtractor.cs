using Microsoft.Extensions.Logging;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Migurdex.Core.Extractors;

public partial class GoogleDriveExtractor : IExtractor
{
    private const string ApiKey = "AIzaSyDVQw45DwoYh632gvsP5vPDqEKvb-Ywnb8";

    private readonly HttpClient                    _httpClient;
    private readonly ILogger<GoogleDriveExtractor> _logger;
    private readonly M3U8PlaylistExtractor         _m3U8Extractor;
    private readonly IMp4MetadataReader            _metadataReader;

    public GoogleDriveExtractor(M3U8PlaylistExtractor m3U8Extractor, ISharedBridge bridge)
    {
        _httpClient     = bridge.CreateHttpClient();
        _m3U8Extractor  = m3U8Extractor;
        _logger         = bridge.CreateLogger<GoogleDriveExtractor>();
        _metadataReader = bridge.MetadataReader;

        _httpClient.DefaultRequestHeaders.Add("Referer", "https://drive.google.com/");
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
    }

    public string Name => "GoogleDrive";

    public bool CanExtract(string url)
    {
        return url.Contains("drive.google.com", StringComparison.OrdinalIgnoreCase)
               || url.Contains("docs.google.com", StringComparison.OrdinalIgnoreCase)
               || url.Contains("drive.usercontent.google.com", StringComparison.OrdinalIgnoreCase);
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
                _logger.LogWarning("could not extract video ID from Google Drive URL: {Url}", url);

                return sources;
            }

            _logger.LogDebug("extracting Google Drive video playback info for ID: {VideoId}", videoId);

            var apiUrl =
                $"https://content-workspacevideo-pa.googleapis.com/v1/drive/media/{videoId}/playback?key={ApiKey}";

            var response = await _httpClient.GetAsync(apiUrl, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!string.IsNullOrEmpty(responseJson))
                {
                    try
                    {
                        using var doc  = JsonDocument.Parse(responseJson);
                        var       root = doc.RootElement;

                        if (root.TryGetProperty("mediaStreamingData", out var mediaStreamingData))
                        {
                            if (mediaStreamingData.TryGetProperty("hlsManifestUrl", out var hlsUrlProp))
                            {
                                var hlsUrl = hlsUrlProp.GetString();
                                if (!string.IsNullOrEmpty(hlsUrl))
                                {
                                    var extractedM3U8 = await _m3U8Extractor.ExtractAsync(hlsUrl,
                                                            new Dictionary<string, string>
                                                            {
                                                                { "Referer", "https://drive.google.com/" },
                                                                { "User-Agent", "Mozilla/5.0" }
                                                            });

                                    if (extractedM3U8.Count > 0)
                                    {
                                        foreach (var src in extractedM3U8)
                                        {
                                            src.Headers               ??= new Dictionary<string, string>();
                                            src.Headers["User-Agent"] =   "Mozilla/5.0";

                                            sources.Add(src);
                                        }
                                    }
                                    else
                                    {
                                        sources.Add(new VideoSource
                                        {
                                            Url  = hlsUrl,
                                            Type = VideoType.M3U8,
                                            Headers = new Dictionary<string, string>
                                            {
                                                { "Referer", "https://drive.google.com/" },
                                                { "User-Agent", "Mozilla/5.0" }
                                            }
                                        });
                                    }
                                }
                            }

                            if (mediaStreamingData.TryGetProperty("formatStreamingData", out var formatStreamingData))
                            {
                                if (formatStreamingData.TryGetProperty("progressiveTranscodes",
                                                                       out var progressiveTranscodes))
                                {
                                    foreach (var item in progressiveTranscodes.EnumerateArray())
                                    {
                                        ParseAndAddSource(item, sources);
                                    }
                                }

                                if (formatStreamingData.TryGetProperty("adaptiveTranscodes",
                                                                       out var adaptiveTranscodes))
                                {
                                    foreach (var item in adaptiveTranscodes.EnumerateArray())
                                    {
                                        ParseAndAddSource(item, sources);
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                                         "failed to parse Google Drive API response JSON for video ID: {VideoId}",
                                         videoId);
                    }
                }
            }

            // fallback
            if (!sources.Any())
            {
                _logger.LogInformation(
                    "no transcode sources found, attempting fallback to direct download URL for ID: {VideoId}",
                    videoId);

                var downloadUrl =
                    $"https://drive.usercontent.google.com/download?id={videoId}&export=download&confirm=t";

                var quality =
                    await _metadataReader.GetVideoQualityAsync(downloadUrl, cancellationToken: cancellationToken);

                if (!quality.Equals("Auto", StringComparison.OrdinalIgnoreCase))
                {
                    sources.Add(new VideoSource
                    {
                        Url     = downloadUrl,
                        Quality = quality,
                        Type    = VideoType.Mp4
                    });
                }
                else
                {
                    _logger.LogWarning("dead link found for video ID: {VideoId}", videoId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "failed to extract Google Drive video sources for: {Url}", url);
        }

        return sources;
    }

    [GeneratedRegex(@"file/d/([a-zA-Z0-9_-]{28,})")]
    private static partial Regex DrivePathIdRegex();

    [GeneratedRegex(@"id=([a-zA-Z0-9_-]{28,})")]
    private static partial Regex DriveParamIdRegex();

    [GeneratedRegex(@"([a-zA-Z0-9_-]{28,})")]
    private static partial Regex DriveFallbackIdRegex();

    private void ParseAndAddSource(JsonElement item, List<VideoSource> sources)
    {
        if (!item.TryGetProperty("url", out var urlProp))
        {
            return;
        }

        var videoUrl = urlProp.GetString();
        if (string.IsNullOrEmpty(videoUrl))
        {
            return;
        }

        if (item.TryGetProperty("transcodeMetadata", out var metadata))
        {
            if (metadata.TryGetProperty("mimeType", out var mimeProp))
            {
                var mimeType = mimeProp.GetString() ?? "";
                if (mimeType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug("skipping audio-only Google Drive stream");

                    return;
                }
            }
        }

        var quality = "Auto";
        var isM3U8  = videoUrl.Contains(".m3u8");

        if (item.TryGetProperty("transcodeMetadata", out var meta))
        {
            if (meta.TryGetProperty("height", out var heightProp))
            {
                var height = heightProp.ValueKind == JsonValueKind.Number
                                 ? heightProp.GetInt32()
                                 : int.TryParse(heightProp.GetString(), out var h)
                                     ? h
                                     : 0;

                if (height > 0)
                {
                    quality = $"{height}p";
                }
            }
        }

        if (quality == "Auto" && item.TryGetProperty("itag", out var itagProp))
        {
            var itag = itagProp.ValueKind == JsonValueKind.Number
                           ? itagProp.GetInt32().ToString()
                           : itagProp.GetString() ?? "";

            quality = MapItagToQuality(itag);
        }

        sources.Add(new VideoSource
        {
            Url     = videoUrl,
            Quality = quality,
            Type    = isM3U8 ? VideoType.M3U8 : VideoType.Mp4,
            Headers = new Dictionary<string, string>
            {
                { "Referer", "https://drive.google.com/" },
                { "User-Agent", "Mozilla/5.0" }
            }
        });
    }

    private static string ExtractVideoId(string url)
    {
        var match = DrivePathIdRegex().Match(url);
        if (match.Success)
        {
            return match.Groups[1].Value;
        }

        match = DriveParamIdRegex().Match(url);
        if (match.Success)
        {
            return match.Groups[1].Value;
        }

        match = DriveFallbackIdRegex().Match(url);
        if (match.Success)
        {
            return match.Groups[1].Value;
        }

        return string.Empty;
    }

    private static string MapItagToQuality(string itag)
    {
        return itag switch
        {
            "18" => "360p",
            "22" => "720p",
            "37" => "1080p",
            "38" => "3072p",
            "59" => "480p",
            "78" => "480p",
            "43" => "360p",
            "44" => "480p",
            "45" => "720p",
            "46" => "1080p",
            _    => "Auto"
        };
    }
}
