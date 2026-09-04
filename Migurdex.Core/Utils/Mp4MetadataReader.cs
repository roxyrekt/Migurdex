using Microsoft.Extensions.DependencyInjection;
using Migurdex.Shared.Infrastructure;
using Migurdex.Shared.Interfaces;
using System.Net;
using System.Text;

namespace Migurdex.Core.Utils;

public class Mp4MetadataReader : IMp4MetadataReader
{
    private readonly Lazy<ISharedBridge> _bridge;
    private          HttpClient?         _client;

    public Mp4MetadataReader(IServiceProvider serviceProvider)
    {
        _bridge = new Lazy<ISharedBridge>(serviceProvider.GetRequiredService<ISharedBridge>);
    }

    private HttpClient Client =>
        _client ??= _bridge.Value.CreateHttpClient(o =>
        {
            o.AllowAutoRedirect = true;
            o.SkipCertVerify    = true;
        });

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
        try
        {
            long totalSize = -1;

            var (bytes, contentRange) = await FetchBytesAbsoluteAsync(videoUrl, 0, 131071, headers, cancellationToken);

            if (bytes.Length == 0)
            {
                return "Auto";
            }

            if (!string.IsNullOrEmpty(contentRange))
            {
                var slashIndex = contentRange.LastIndexOf('/');
                if (slashIndex != -1 && long.TryParse(contentRange[(slashIndex + 1)..], out var size))
                {
                    totalSize = size;
                }
            }

            long currentOffset = 0;
            var  step          = 0;

            while (currentOffset < bytes.Length - 8 && step < 15)
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
                    var resolution =
                        await ParseMoovBoxAsync(videoUrl, currentOffset, boxSize, headers, cancellationToken);

                    return resolution;
                }

                if (boxType == "mdat")
                {
                    var moovOffset = currentOffset + boxSize;

                    if (totalSize > 0 && moovOffset < totalSize)
                    {
                        var moovSize = totalSize - moovOffset;

                        var resolution =
                            await ParseMoovBoxAsync(videoUrl, moovOffset, moovSize, headers, cancellationToken);

                        if (resolution != "Auto")
                        {
                            return resolution;
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
        }
        catch
        {
            // ignored
        }

        return "Auto";
    }

    private async Task<string> ParseMoovBoxAsync(string videoUrl,
        long                                            moovOffset,
        long                                            moovSize,
        Dictionary<string, string>                      headers,
        CancellationToken                               cancellationToken)
    {
        var currentOffset = moovOffset + 8;
        var moovEndOffset = moovOffset + moovSize;

        byte[] buffer            = [];
        long   bufferStartOffset = -1;

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

            if (boxType == "trak")
            {
                var trakContentOffset = currentOffset + 8;
                var trakEndOffset     = currentOffset + boxSize;
                var isAudioTrack      = false;

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
                            var resolution = ParseResolutionFromTkhd(tkhdBytes);
                            if (resolution != "Auto")
                            {
                                return resolution;
                            }

                            isAudioTrack = true;

                            break;
                        }
                    }

                    trakContentOffset += subBoxSize;
                }

                if (isAudioTrack)
                {
                    currentOffset += boxSize;

                    continue;
                }
            }

            currentOffset += boxSize;
        }

        return "Auto";

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

            var fetchSize = Math.Max(size, 16384);
            if (offset + fetchSize > moovEndOffset)
            {
                fetchSize = (int) (moovEndOffset - offset);
            }

            if (fetchSize < size)
            {
                fetchSize = size;
            }

            var (fetchedBytes, _) =
                await FetchBytesAbsoluteAsync(videoUrl, offset, offset + fetchSize - 1, headers, cancellationToken);
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

    private static string ParseResolutionFromTkhd(byte[] bytes)
    {
        if (bytes.Length < 90)
        {
            return "Auto";
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
                return "Auto";
        }

        if (heightOffset + 2 > bytes.Length)
        {
            return "Auto";
        }

        var width  = (ushort) ((bytes[widthOffset] << 8) | bytes[widthOffset + 1]);
        var height = (ushort) ((bytes[heightOffset] << 8) | bytes[heightOffset + 1]);

        if (width > 0 && height > 0 && width < 10000 && height < 10000)
        {
            var result = $"{height}p";

            return result;
        }

        return "Auto";
    }

    private async Task<(byte[] Bytes, string? ContentRange)> FetchBytesAbsoluteAsync(
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

            using var response = await Client.SendAsync(request, cancellationToken).ConfigureAwait(false);

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

            var responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false)
                                ?? [];

            return (responseBytes, contentRange);
        }
        catch
        {
            return ([], null);
        }
    }
}
