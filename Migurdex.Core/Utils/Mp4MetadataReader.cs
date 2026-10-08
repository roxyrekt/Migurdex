using Microsoft.Extensions.DependencyInjection;
using Migurdex.Core.Extractors;
using Migurdex.Shared.Infrastructure;
using Migurdex.Shared.Interfaces;
using System.Net;
using System.Text;

namespace Migurdex.Core.Utils;

public class Mp4MetadataReader : IMp4MetadataReader
{
    private const int HeadFetchLength    = 131072;
    private const int ChunkFetchBytes   = 16384;
    private const int MaxTopLevelBoxes  = 15;
    private const int MaxMoovChildren   = 64;
    private const int MaxChildScan      = 32;
    private const int MvhdFetchBytes    = 256;
    private const int MaxDurationSecs   = 86400;
    private const long MaxBitrateBps    = 1_000_000_000;

    private readonly Lazy<ISharedBridge> _bridge;
    private readonly Lazy<HttpClient>    _client;

    public Mp4MetadataReader(IServiceProvider serviceProvider)
    {
        _bridge = new Lazy<ISharedBridge>(serviceProvider.GetRequiredService<ISharedBridge>);
        _client = new Lazy<HttpClient>(() => _bridge.Value.CreateHttpClient(o =>
        {
            o.AllowAutoRedirect = true;
            o.SkipCertVerify    = true;
        }));
    }

    private HttpClient Client => _client.Value;

    public Task<string> GetVideoQualityAsync(string videoUrl,
        string?                                     referer           = null,
        string?                                     userAgent         = null,
        CancellationToken                           cancellationToken = default)
    {
        var headers = new Dictionary<string, string>();

        if (!string.IsNullOrWhiteSpace(referer))
        {
            headers.Add("Referer", referer);
        }

        if (!string.IsNullOrWhiteSpace(userAgent))
        {
            headers.Add("User-Agent", userAgent);
        }

        return GetVideoQualityAsync(videoUrl, headers, cancellationToken);
    }

    public async Task<string> GetVideoQualityAsync(string videoUrl,
        Dictionary<string, string>                        headers,
        CancellationToken                                 cancellationToken = default)
    {
        var metadata = await GetVideoMetadataAsync(videoUrl, headers, cancellationToken);

        return metadata.Quality;
    }

    public Task<Mp4Metadata> GetVideoMetadataAsync(string videoUrl,
        string?                                                          referer           = null,
        string?                                                          userAgent         = null,
        CancellationToken                                                cancellationToken = default)
    {
        var headers = new Dictionary<string, string>();

        if (!string.IsNullOrWhiteSpace(referer))
        {
            headers.Add("Referer", referer);
        }

        if (!string.IsNullOrWhiteSpace(userAgent))
        {
            headers.Add("User-Agent", userAgent);
        }

        return GetVideoMetadataAsync(videoUrl, headers, cancellationToken);
    }

    public async Task<Mp4Metadata> GetVideoMetadataAsync(string videoUrl,
        Dictionary<string, string>                             headers,
        CancellationToken                                      cancellationToken = default)
    {
        try
        {
            var (bytes, sizeBytes) = await FetchBytesAbsoluteAsync(videoUrl,
                                                                     0,
                                                                     HeadFetchLength - 1,
                                                                     headers,
                                                                     cancellationToken);

            if (bytes.Length == 0 || !IsMp4Start(bytes))
            {
                return new Mp4Metadata("Auto", null, null, null);
            }

            var totalSize = sizeBytes ?? -1;

            long currentOffset = 0;
            var  step          = 0;

            while (currentOffset < bytes.Length - 8 && step < MaxTopLevelBoxes)
            {
                step++;
                long boxSize = (uint) ((bytes[currentOffset] << 24)
                                       | (bytes[currentOffset + 1] << 16)
                                       | (bytes[currentOffset + 2] << 8)
                                       | bytes[currentOffset + 3]);

                var boxType = Encoding.ASCII.GetString(bytes, (int) currentOffset + 4, 4);

                if (boxSize == 0)
                {
                    break;
                }

                if (boxSize == 1)
                {
                    if (currentOffset + 16 > bytes.Length)
                    {
                        break;
                    }

                    boxSize = 0;
                    for (var i = 0; i < 8; i++)
                    {
                        boxSize = (boxSize << 8) | bytes[currentOffset + 8 + i];
                    }
                }

                if (boxType == "moov")
                {
                    var (quality, width, height, videoCodec, audioCodec, duration) =
                        await ParseMoovBoxAsync(videoUrl, currentOffset, boxSize, headers, bytes, cancellationToken);

                    return BuildMetadata(quality, duration, sizeBytes, width, height, videoCodec, audioCodec);
                }

                if (boxType == "mdat")
                {
                    var moovOffset = currentOffset + boxSize;

                    if (totalSize > 0 && moovOffset < totalSize)
                    {
                        var moovSize = totalSize - moovOffset;

                        var (quality, width, height, videoCodec, audioCodec, duration) =
                            await ParseMoovBoxAsync(videoUrl,
                                                    moovOffset,
                                                    moovSize,
                                                    headers,
                                                    bytes,
                                                    cancellationToken);

                        if (quality != "Auto")
                        {
                            return BuildMetadata(quality, duration, sizeBytes, width, height, videoCodec, audioCodec);
                        }
                    }

                    break;
                }

                currentOffset += boxSize;

                if (boxSize < 0)
                {
                    break;
                }
            }

            return new Mp4Metadata("Auto", null, sizeBytes, null);
        }
        catch
        {
            // ignored
        }

        return new Mp4Metadata("Auto", null, null, null);
    }

    public static Mp4Metadata BuildMetadata(string quality,
        double?                                     durationSeconds,
        long?                                       sizeBytes,
        int?                                        width          = null,
        int?                                        height         = null,
        string?                                     videoCodec     = null,
        string?                                     audioCodec     = null)
    {
        return new Mp4Metadata(quality,
                               ComputeBitrate(sizeBytes, durationSeconds),
                               sizeBytes,
                               durationSeconds,
                               width,
                               height,
                               videoCodec,
                               audioCodec);
    }

    public static long? ComputeBitrate(long? sizeBytes, double? durationSeconds)
    {
        if (sizeBytes is null or <= 0 || durationSeconds is null or <= 0)
        {
            return null;
        }

        var bitrate = (long) (sizeBytes.Value * 8 / durationSeconds.Value);

        return bitrate is > 0 and < MaxBitrateBps ? bitrate : null;
    }


    public static bool IsMp4Start(byte[] bytes)
    {
        if (bytes.Length < 8)
        {
            return false;
        }

        return Encoding.ASCII.GetString(bytes, 4, 4) is "ftyp" or "moov" or "mdat" or "free" or "wide" or "skip";
    }

    public static long? ParseTotalSize(string? contentRange)
    {
        if (string.IsNullOrEmpty(contentRange))
        {
            return null;
        }

        var slashIndex = contentRange.LastIndexOf('/');
        if (slashIndex != -1 && long.TryParse(contentRange[(slashIndex + 1)..], out var size) && size > 0)
        {
            return size;
        }

        return null;
    }


    public static double? ParseDurationFromMvhd(byte[] bytes)
    {
        if (bytes.Length < 32)
        {
            return null;
        }

        var version = bytes[8];

        long   timescale;
        double duration;

        if (version == 1)
        {
            if (bytes.Length < 40)
            {
                return null;
            }

            timescale = (uint) ((bytes[28] << 24) | (bytes[29] << 16) | (bytes[30] << 8) | bytes[31]);

            var high = (uint) ((bytes[32] << 24) | (bytes[33] << 16) | (bytes[34] << 8) | bytes[35]);
            var low  = (uint) ((bytes[36] << 24) | (bytes[37] << 16) | (bytes[38] << 8) | bytes[39]);
            duration = high * 4294967296.0 + low;
        }
        else if (version == 0)
        {
            timescale = (uint) ((bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23]);
            duration  = (uint) ((bytes[24] << 24) | (bytes[25] << 16) | (bytes[26] << 8) | bytes[27]);
        }
        else
        {
            return null;
        }

        if (timescale <= 0 || duration <= 0)
        {
            return null;
        }

        var seconds = duration / timescale;

        return seconds is > 0 and <= MaxDurationSecs ? Math.Round(seconds, 3) : null;
    }

    private async Task<(string Quality,
        int? Width,
        int? Height,
        string? VideoCodec,
        string? AudioCodec,
        double? DurationSeconds)> ParseMoovBoxAsync(
        string                                                                       videoUrl,
        long                                                                         moovOffset,
        long                                                                         moovSize,
        Dictionary<string, string>                                                   headers,
        byte[]                                                                       headBytes,
        CancellationToken                                                            cancellationToken)
    {
        var currentOffset = moovOffset + 8;
        var moovEndOffset = moovOffset + moovSize;

        byte[] buffer            = headBytes;
        long   bufferStartOffset = 0;

        string? quality         = null;
        int?    width           = null;
        int?    height          = null;
        string? videoCodec      = null;
        string? audioCodec      = null;
        double? durationSeconds = null;

        while (currentOffset < moovEndOffset - 8)
        {
            var headerBytes = await GetBytesAtOffsetAsync(currentOffset, 8);
            if (headerBytes.Length < 8)
            {
                break;
            }

            long boxSize = (uint) ((headerBytes[0] << 24)
                                   | (headerBytes[1] << 16)
                                   | (headerBytes[2] << 8)
                                   | headerBytes[3]);

            var boxType = Encoding.ASCII.GetString(headerBytes, 4, 4);

            if (boxSize == 1)
            {
                var extraHeaderBytes = await GetBytesAtOffsetAsync(currentOffset + 8, 8);
                if (extraHeaderBytes.Length < 8)
                {
                    break;
                }

                boxSize = 0;
                for (var i = 0; i < 8; i++)
                {
                    boxSize = (boxSize << 8) | extraHeaderBytes[i];
                }
            }

            if (boxSize <= 0)
            {
                break;
            }

            if (boxType == "mvhd")
            {
                var mvhdBytes = await GetBytesAtOffsetAsync(currentOffset, Math.Min((int) boxSize, MvhdFetchBytes));
                var parsed      = ParseDurationFromMvhd(mvhdBytes);
                if (parsed.HasValue)
                {
                    durationSeconds = parsed.Value;
                }

                currentOffset += boxSize;

                continue;
            }

            if (boxType == "trak")
            {
                var trakContentOffset = currentOffset + 8;
                var trakEndOffset     = currentOffset + boxSize;
                var isAudioTrack      = false;
                var isVideoTrack      = false;

                while (trakContentOffset < trakEndOffset - 8)
                {
                    var subHeaderBytes = await GetBytesAtOffsetAsync(trakContentOffset, 8);
                    if (subHeaderBytes.Length < 8)
                    {
                        break;
                    }

                    long subBoxSize = (uint) ((subHeaderBytes[0] << 24)
                                              | (subHeaderBytes[1] << 16)
                                              | (subHeaderBytes[2] << 8)
                                              | subHeaderBytes[3]);

                    var subBoxType = Encoding.ASCII.GetString(subHeaderBytes, 4, 4);

                    if (subBoxSize <= 0)
                    {
                        break;
                    }

                    if (subBoxType == "tkhd")
                    {
                        var tkhdBytes = await GetBytesAtOffsetAsync(trakContentOffset, (int) subBoxSize);

                        if (tkhdBytes.Length >= subBoxSize)
                        {
                            var (parsedWidth, parsedHeight) = ParseResolutionFromTkhd(tkhdBytes);
                            if (parsedWidth.HasValue && parsedHeight.HasValue)
                            {
                                quality ??= $"{parsedHeight.Value}p";
                                width   ??= parsedWidth.Value;
                                height  ??= parsedHeight.Value;
                                isVideoTrack = true;
                            }
                            else
                            {
                                isAudioTrack = true;
                            }

                            break;
                        }
                    }

                    trakContentOffset += subBoxSize;
                }

                if ((isVideoTrack && videoCodec is null) || (isAudioTrack && audioCodec is null))
                {
                    var codec = await ParseTrakCodecAsync(currentOffset, currentOffset + boxSize);
                    if (codec is not null)
                    {
                        if (isVideoTrack)
                        {
                            videoCodec ??= codec;
                        }
                        else
                        {
                            audioCodec ??= codec;
                        }
                    }
                }

                currentOffset += boxSize;

                continue;
            }

            if (quality is not null
                && durationSeconds.HasValue
                && videoCodec is not null
                && audioCodec is not null)
            {
                break;
            }

            currentOffset += boxSize;
        }

        return (quality ?? "Auto", width, height, videoCodec, audioCodec, durationSeconds);

        async Task<(long Offset, long Size)?> FindChildAsync(long parentOffset, long parentEnd, string type)
        {
            var offset = parentOffset + 8;
            var guard  = 0;

            while (offset < parentEnd - 8 && guard < MaxChildScan)
            {
                guard++;

                var header = await GetBytesAtOffsetAsync(offset, 8);
                if (header.Length < 8)
                {
                    return null;
                }

                long size = (uint) ((header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3]);

                if (size == 1)
                {
                    var extra = await GetBytesAtOffsetAsync(offset + 8, 8);
                    if (extra.Length < 8)
                    {
                        return null;
                    }

                    size = 0;
                    for (var i = 0; i < 8; i++)
                    {
                        size = (size << 8) | extra[i];
                    }
                }

                if (size <= 0)
                {
                    return null;
                }

                if (Encoding.ASCII.GetString(header, 4, 4) == type)
                {
                    return (offset, size);
                }

                offset += size;
            }

            return null;
        }

        async Task<string?> ParseTrakCodecAsync(long trakOffset, long trakEnd)
        {
            var mdia = await FindChildAsync(trakOffset, trakEnd, "mdia");
            if (mdia is null)
            {
                return null;
            }

            var minf = await FindChildAsync(mdia.Value.Offset, mdia.Value.Offset + mdia.Value.Size, "minf");
            if (minf is null)
            {
                return null;
            }

            var stbl = await FindChildAsync(minf.Value.Offset, minf.Value.Offset + minf.Value.Size, "stbl");
            if (stbl is null)
            {
                return null;
            }

            var stsd = await FindChildAsync(stbl.Value.Offset, stbl.Value.Offset + stbl.Value.Size, "stsd");
            if (stsd is null || stsd.Value.Size < 20)
            {
                return null;
            }

            var bytes = await GetBytesAtOffsetAsync(stsd.Value.Offset, 28);
            if (bytes.Length < 24)
            {
                return null;
            }

            return M3U8PlaylistExtractor.MapCodecTag(Encoding.ASCII.GetString(bytes, 20, 4));
        }

        async Task<byte[]> GetBytesAtOffsetAsync(long offset, int size)
        {
            if (bufferStartOffset >= 0
                && offset >= bufferStartOffset
                && offset + size <= bufferStartOffset + buffer.Length)
            {
                var result = new byte[size];
                Buffer.BlockCopy(buffer, (int) (offset - bufferStartOffset), result, 0, size);

                return result;
            }

            var fetchSize = Math.Max(size, ChunkFetchBytes);
            if (offset + fetchSize > moovEndOffset)
            {
                fetchSize = (int) (moovEndOffset - offset);
            }

            if (fetchSize < size)
            {
                fetchSize = size;
            }

            var fetchedBytes =
                (await FetchBytesAbsoluteAsync(videoUrl, offset, offset + fetchSize - 1, headers, cancellationToken))
                .Bytes;
            buffer            = fetchedBytes;
            bufferStartOffset = offset;

            if (buffer.Length < size)
            {
                return [];
            }

            var res = new byte[size];
            Buffer.BlockCopy(buffer, 0, res, 0, size);

            return res;
        }
    }

    public static (int? Width, int? Height) ParseResolutionFromTkhd(byte[] bytes)
    {
        if (bytes.Length < 90)
        {
            return (null, null);
        }

        var version = bytes[8];
        int widthOffset;
        int heightOffset;

        switch (version)
        {
            case 0:
                widthOffset  = 84;
                heightOffset = 88;

                break;
            case 1:
                widthOffset  = 96;
                heightOffset = 100;

                break;
            default:
                return (null, null);
        }

        if (heightOffset + 2 > bytes.Length)
        {
            return (null, null);
        }

        var width  = (ushort) ((bytes[widthOffset] << 8) | bytes[widthOffset + 1]);
        var height = (ushort) ((bytes[heightOffset] << 8) | bytes[heightOffset + 1]);

        if (width > 0 && height > 0 && width < 10000 && height < 10000)
        {
            return (width, height);
        }

        return (null, null);
    }

    private async Task<(byte[] Bytes, long? TotalSize)> FetchBytesAbsoluteAsync(
        string                      videoUrl,
        long                        start,
        long                        end,
        Dictionary<string, string>? headers,
        CancellationToken           cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, videoUrl);
            request.WithSkipCertVerify();
            request.Headers.TryAddWithoutValidation("Range", $"bytes={start}-{end}");

            if (headers != null && headers.Any())
            {
                foreach (var keyValuePair in headers)
                {
                    request.Headers.TryAddWithoutValidation(keyValuePair.Key, keyValuePair.Value);
                }
            }

            if (!request.Headers.UserAgent.Any())
            {
                request.Headers.TryAddWithoutValidation("User-Agent",
                                                        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            }

            using var response =
                await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                            .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.PartialContent)
            {
                return ([], null);
            }

            string? contentRange = null;
            if (response.Headers.TryGetValues("Content-Range", out var values))
            {
                contentRange = values.FirstOrDefault();
            }
            else if (response.Content.Headers.TryGetValues("Content-Range", out var cValues))
            {
                contentRange = cValues.FirstOrDefault();
            }

            var wanted = end - start + 1;
            if (wanted is <= 0 or > 1_048_576)
            {
                return ([], ParseTotalSize(contentRange));
            }

            if (response.StatusCode == HttpStatusCode.OK && start > 0)
            {
                var contentLength = response.Content.Headers.ContentLength;
                return ([], contentLength is > 0 ? contentLength : ParseTotalSize(contentRange));
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                                                          .ConfigureAwait(false);
            var buffer = new byte[wanted];
            var read   = 0;
            while (read < wanted)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(read, (int) (wanted - read)), cancellationToken)
                                        .ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                read += count;
            }

            if (read < wanted)
            {
                Array.Resize(ref buffer, read);
            }

            if (response.StatusCode == HttpStatusCode.OK)
            {
                var contentLength = response.Content.Headers.ContentLength;
                return (buffer, contentLength is > 0 ? contentLength : ParseTotalSize(contentRange));
            }

            return (buffer, ParseTotalSize(contentRange));
        }
        catch
        {
            return ([], null);
        }
    }
}
