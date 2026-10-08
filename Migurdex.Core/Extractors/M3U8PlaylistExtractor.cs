using Microsoft.Extensions.Logging;
using Migurdex.Core.Utils;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Infrastructure;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Migurdex.Core.Extractors;

public partial class M3U8PlaylistExtractor : IExtractor
{
    private static readonly HashSet<string> _nonTransferableHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        // Bağlantıya özel, gövde uzunluğunu belirleyen veya içeriği sıkıştıran
        // header'lar playlist isteğinden medya indirmesine taşınmamalıdır.
        "accept-encoding",
        "connection",
        "content-length",
        "expect",
        "host",
        "if-range",
        "keep-alive",
        "proxy-connection",
        "range",
        "te",
        "trailer",
        "transfer-encoding",
        "upgrade"
    };

    private readonly HttpClient                     _httpClient;
    private readonly ILogger<M3U8PlaylistExtractor> _logger;

    public M3U8PlaylistExtractor(ISharedBridge bridge, ILogger<M3U8PlaylistExtractor> logger)
    {
        _httpClient = bridge.CreateHttpClient();
        _logger     = logger;
    }

    public string Name => "M3U8Playlist";

    public bool CanExtract(string url)
    {
        return url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)
               || url.Contains(".txt", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<List<VideoSource>> ExtractAsync(string url,
        IDictionary<string, string>?                         headers           = null,
        CancellationToken                                    cancellationToken = default)
    {
        var sources = await ExtractPlaylistAsync(url, headers, cancellationToken);

        foreach (var source in sources)
        {
            source.Headers = MergeSourceHeaders(source.Headers, headers);
        }

        return sources;
    }

    private async Task<List<VideoSource>> ExtractPlaylistAsync(string url,
        IDictionary<string, string>?                         headers,
        CancellationToken                                    cancellationToken)
    {
        var sources = new List<VideoSource>();

        try
        {
            var baseUri = new Uri(url);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);

            var userAgent =
                headers?.TryGetValue("User-Agent", out var customUa) == true
                && !string.IsNullOrWhiteSpace(customUa)
                    ? customUa
                    : "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:151.0) Gecko/20100101 Firefox/151.0";

            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
            request.Headers.Add("Accept", "*/*");
            request.Headers.Add("Accept-Language", "en-US,en;q=0.9");
            request.AddHeaders(headers);

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("M3U8 playlist request failed.");

                return sources;
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrEmpty(content))
            {
                return sources;
            }

            var hasSeparateAudio =
                AudioMediaRegex().IsMatch(content);

            var lines    = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var variants = new List<(string Quality,
                                    int Width,
                                    int Height,
                                    long? Bitrate,
                                    string? VideoCodec,
                                    string? AudioCodec,
                                    string Url)>();

            for (var i = 0; i < lines.Length - 1; i++)
            {
                var line = lines[i].Trim();
                if (!line.StartsWith("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var segmentLine = lines[i + 1].Trim();
                if (string.IsNullOrEmpty(segmentLine) || segmentLine.StartsWith("#"))
                {
                    continue;
                }

                var quality  = "Auto";
                var width    = 0;
                var height   = 0;
                var resMatch = ResolutionRegex().Match(line);
                if (resMatch.Success)
                {
                    var parts = resMatch.Groups[1].Value.Split('x');
                    if (parts.Length == 2
                        && int.TryParse(parts[0], out var parsedWidth)
                        && int.TryParse(parts[1], out var parsedHeight))
                    {
                        width   = parsedWidth;
                        height  = parsedHeight;
                        quality = $"{parsedHeight}p";
                    }
                }

                var bitrate = ParseBandwidth(line);
                var (videoCodec, audioCodec) = ParseCodecs(line);

                string segUrl;
                if (Uri.TryCreate(baseUri, segmentLine, out var combinedUri))
                {
                    segUrl = string.IsNullOrEmpty(combinedUri.Query) && !string.IsNullOrEmpty(baseUri.Query)
                                 ? combinedUri.AbsoluteUri + baseUri.Query
                                 : combinedUri.AbsoluteUri;
                }
                else
                {
                    segUrl = segmentLine.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                                 ? segmentLine
                                 : baseUri + "/" + segmentLine;

                    if (!segUrl.Contains('?') && !string.IsNullOrEmpty(baseUri.Query))
                    {
                        segUrl += baseUri.Query;
                    }
                }

                variants.Add((quality, width, height, bitrate, videoCodec, audioCodec, segUrl));
                i++;
            }

            if (variants.Count > 0)
            {
                var maxVariant = variants.OrderByDescending(v => v.Height)
                                         .ThenByDescending(v => v.Bitrate ?? 0)
                                         .First();
                var maxQuality   = maxVariant.Height > 0 ? $"{maxVariant.Height}p" : "Auto";
                var mediaSeconds = await TryGetVariantDurationAsync(maxVariant.Url, headers, cancellationToken);

                if (hasSeparateAudio)
                {
                    sources.Add(new VideoSource
                    {
                        Url             = url,
                        Quality         = maxQuality,
                        Bitrate         = maxVariant.Bitrate,
                        Width           = maxVariant.Width > 0 ? maxVariant.Width : null,
                        Height          = maxVariant.Height > 0 ? maxVariant.Height : null,
                        VideoCodec      = maxVariant.VideoCodec,
                        AudioCodec      = maxVariant.AudioCodec,
                        DurationSeconds = mediaSeconds,
                        Type            = VideoType.M3U8
                    });
                }
                else
                {
                    sources.Add(new VideoSource
                    {
                        Url             = url,
                        Quality         = "Auto",
                        Bitrate         = maxVariant.Bitrate,
                        Width           = maxVariant.Width > 0 ? maxVariant.Width : null,
                        Height          = maxVariant.Height > 0 ? maxVariant.Height : null,
                        VideoCodec      = maxVariant.VideoCodec,
                        AudioCodec      = maxVariant.AudioCodec,
                        DurationSeconds = mediaSeconds,
                        Type            = VideoType.M3U8
                    });

                    foreach (var variant in variants.OrderByDescending(v => v.Height)
                                                    .ThenByDescending(v => v.Bitrate ?? 0))
                    {
                        sources.Add(new VideoSource
                        {
                            Url             = variant.Url,
                            Quality         = variant.Quality,
                            Bitrate         = variant.Bitrate,
                            Width           = variant.Width > 0 ? variant.Width : null,
                            Height          = variant.Height > 0 ? variant.Height : null,
                            VideoCodec      = variant.VideoCodec,
                            AudioCodec      = variant.AudioCodec,
                            DurationSeconds = mediaSeconds,
                            Type            = VideoType.M3U8
                        });
                    }
                }
            }
            else
            {
                var quality = "Auto";
                var firstSegment = lines.FirstOrDefault(l => !l.StartsWith('#') && !string.IsNullOrWhiteSpace(l))
                                        ?.Trim();
                if (!string.IsNullOrEmpty(firstSegment))
                {
                    string segUrl;
                    if (Uri.TryCreate(baseUri, firstSegment, out var combinedUri))
                    {
                        segUrl = string.IsNullOrEmpty(combinedUri.Query) && !string.IsNullOrEmpty(baseUri.Query)
                                     ? combinedUri.AbsoluteUri + baseUri.Query
                                     : combinedUri.AbsoluteUri;
                    }
                    else
                    {
                        segUrl = firstSegment.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                                     ? firstSegment
                                     : baseUri + "/" + firstSegment;

                        if (!segUrl.Contains('?') && !string.IsNullOrEmpty(baseUri.Query))
                        {
                            segUrl += baseUri.Query;
                        }
                    }

                    var detectedQuality = await TryDetectQualityFromTsSegmentAsync(segUrl, headers, cancellationToken);
                    if (!string.IsNullOrEmpty(detectedQuality))
                    {
                        quality = detectedQuality;
                    }
                }

                sources.Add(new VideoSource
                {
                    Url             = url,
                    Quality         = quality,
                    DurationSeconds = ParseSegmentDuration(content),
                    Type            = VideoType.M3U8
                });
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "M3U8 playlist parse failed.");
        }

        return sources;
    }

    [GeneratedRegex(@"#EXT-X-MEDIA:TYPE=AUDIO[^,\n]*,[^\n]*URI=", RegexOptions.IgnoreCase)]
    private static partial Regex AudioMediaRegex();

    [GeneratedRegex(@"RESOLUTION=(\d+x\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ResolutionRegex();

    private const long MaxBandwidthBps = 1_000_000_000;

    public static long? ParseBandwidth(string streamInfLine)
    {
        var average = AverageBandwidthRegex().Match(streamInfLine);
        if (average.Success
            && long.TryParse(average.Groups[1].Value, out var averageBits)
            && IsSaneBandwidth(averageBits))
        {
            return averageBits;
        }

        var peak = BandwidthRegex().Match(streamInfLine);
        if (peak.Success
            && long.TryParse(peak.Groups[1].Value, out var peakBits)
            && IsSaneBandwidth(peakBits))
        {
            return peakBits;
        }

        return null;
    }

    private static bool IsSaneBandwidth(long bits)
    {
        return bits is > 0 and < MaxBandwidthBps;
    }

    [GeneratedRegex(@"AVERAGE-BANDWIDTH=(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex AverageBandwidthRegex();

    [GeneratedRegex(@"(?<!-)BANDWIDTH=(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex BandwidthRegex();

    [GeneratedRegex(@"CODECS=""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex CodecsRegex();


    public static (string? VideoCodec, string? AudioCodec) ParseCodecs(string streamInfLine)
    {
        var match = CodecsRegex().Match(streamInfLine);
        if (!match.Success)
        {
            return (null, null);
        }

        string? video = null;
        string? audio = null;

        var parts = match.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries
                                                     | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            var tag    = part.Split('.')[0].Trim().ToLowerInvariant();
            var mapped = MapCodecTag(tag);
            if (mapped is null)
            {
                continue;
            }

            if (IsAudioCodecTag(tag))
            {
                audio ??= mapped;
            }
            else
            {
                video ??= mapped;
            }
        }

        return (video, audio);
    }

    public static string? MapCodecTag(string tag)
    {
        return tag.ToLowerInvariant() switch
        {
            "avc1"            => "H.264",
            "hev1" or "hvc1"  => "H.265",
            "vp09"            => "VP9",
            "av01"            => "AV1",
            "mp4v"            => "MPEG-4",
            "mp4a"            => "AAC",
            "ec-3" or "ec3"   => "E-AC-3",
            "ac-3" or "ac3"   => "AC-3",
            "opus"            => "Opus",
            "vorbis"          => "Vorbis",
            _                 => null
        };
    }

    public static double? ParseSegmentDuration(string playlistContent)
    {
        double total = 0;
        var    count = 0;

        foreach (var rawLine in playlistContent.Split('\n'))
        {
            if (count >= 10000)
            {
                break;
            }

            var line = rawLine.Trim();
            if (!line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = line["#EXTINF:".Length..];
            var comma = value.IndexOf(',');
            var number = (comma < 0 ? value : value[..comma]).Trim();

            if (double.TryParse(number,
                                System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture,
                                out var seconds)
                && seconds > 0)
            {
                total += seconds;
                count++;
            }
        }

        return total > 0 ? Math.Round(total, 3) : null;
    }

    private async Task<double?> TryGetVariantDurationAsync(string variantUrl,
        IDictionary<string, string>?                                     headers,
        CancellationToken                                                cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, variantUrl);
            request.Headers.TryAddWithoutValidation("User-Agent",
                                                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:151.0) Gecko/20100101 Firefox/151.0");
            request.Headers.Add("Accept", "*/*");
            request.AddHeaders(headers);

            using var response =
                await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            if (response.Content.Headers.ContentLength is > 2_097_152)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader       = new StreamReader(stream, Encoding.UTF8, false, 8192, true);

            var content = new StringBuilder();
            var buffer  = new char[8192];
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
            {
                content.Append(buffer, 0, count);
                if (content.Length > 2_097_152)
                {
                    return null;
                }
            }

            return ParseSegmentDuration(content.ToString());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsAudioCodecTag(string tag)
    {
        return tag is "mp4a" or "ec-3" or "ec3" or "ac-3" or "ac3" or "opus" or "vorbis";
    }

    private static Dictionary<string, string>? MergeSourceHeaders(
        Dictionary<string, string>?                  sourceHeaders,
        IDictionary<string, string>?                 requestHeaders)
    {
        if (requestHeaders is not { Count: > 0 })
        {
            return sourceHeaders;
        }

        // Her kaynak kendi sözlüğünü alır; çağıranın ya da diğer kaynakların
        // başlıklarının sonradan değiştirilmesi bu kaynağı etkilemez.
        var merged = sourceHeaders is { Count: > 0 }
                         ? new Dictionary<string, string>(sourceHeaders, StringComparer.OrdinalIgnoreCase)
                         : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value) in requestHeaders)
        {
            var name  = SanitizeHeaderValue(key);
            var val   = SanitizeHeaderValue(value);
            if (name is null || val is null)
            {
                continue;
            }

            if (_nonTransferableHeaders.Contains(name) || merged.ContainsKey(name))
            {
                continue;
            }

            merged[name] = val;
        }

        return merged.Count > 0 ? merged : sourceHeaders;
    }

    private static string? SanitizeHeaderValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var cleaned = value.Replace("\r", string.Empty)
                           .Replace("\n", string.Empty)
                           .Trim();

        return cleaned.Length == 0 ? null : cleaned;
    }

    private async Task<string?> TryDetectQualityFromTsSegmentAsync(string segmentUrl,
        IDictionary<string, string>?                                      headers,
        CancellationToken                                                 cancellationToken = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, segmentUrl);
            req.Headers.TryAddWithoutValidation("Range", "bytes=0-65535");
            req.Headers.TryAddWithoutValidation("User-Agent",
                                                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            req.AddHeaders(headers);

            using var resp = await _httpClient.SendAsync(req, cancellationToken);
            if (resp.IsSuccessStatusCode || resp.StatusCode == HttpStatusCode.PartialContent)
            {
                var bytes  = await resp.Content.ReadAsByteArrayAsync(cancellationToken);
                var parsed = H264SpsParser.TryParseFromTs(bytes);
                if (parsed.HasValue)
                {
                    return H264SpsParser.ToQualityString(parsed.Value.Height);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "TS segment quality detection failed.");
        }

        return null;
    }
}
