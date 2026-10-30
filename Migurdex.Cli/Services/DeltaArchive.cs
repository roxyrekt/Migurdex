using System;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Migurdex.Cli.Services;

public sealed class DeltaLocation : IDisposable
{
    public const string ManifestEntry = "manifest.json.gz";

    public string  Url           { get; }
    public bool    SupportsRange { get; }
    public string? LocalPath     { get; }
    public long    TotalBytes    { get; }
    public long    PayloadStart  { get; }

    public DeltaLocation(string url, bool supportsRange, string? localPath, long totalBytes, long payloadStart)
    {
        Url           = url;
        SupportsRange = supportsRange;
        LocalPath     = localPath;
        TotalBytes    = totalBytes;
        PayloadStart  = payloadStart;
    }

    public void Dispose()
    {
        if (LocalPath is null)
        {
            return;
        }

        try
        {
            File.Delete(LocalPath);
        }
        catch
        {
            // ignored
        }
    }
}

internal static class DeltaArchive
{
    private const int HeaderSize = 512;

    public static async Task<(DeltaLocation Location, BlobManifest Manifest)> OpenAsync(HttpClient http,
        string                                                                                 url,
        string                                                                                 workDir,
        CancellationToken                                                                      cancellationToken)
    {
        using var probe = new HttpRequestMessage(HttpMethod.Get, url);
        probe.Headers.Range = new RangeHeaderValue(0, HeaderSize - 1);

        using var response = await http
                                   .SendAsync(probe, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                                   .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            var header = new byte[HeaderSize];
            await using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            {
                await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
            }

            var (name, size) = ParseHeader(header);
            if (!name.Equals(DeltaLocation.ManifestEntry, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"delta paketinde manifest yok (ilk girdi: {name})");
            }

            var gz      = await FetchRangeAsync(http, url, HeaderSize, size, cancellationToken).ConfigureAwait(false);
            var payload = HeaderSize + RoundUp(size);

            return (new DeltaLocation(url, true, null, 0, payload), await ParseManifestAsync(gz, cancellationToken));
        }

        response.EnsureSuccessStatusCode();

        var local = Path.Combine(workDir, "delta.tar");
        await using (var src = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var dst = File.Create(local))
        {
            await src.CopyToAsync(dst, cancellationToken).ConfigureAwait(false);
        }

        var bytes = await File.ReadAllBytesAsync(local, cancellationToken).ConfigureAwait(false);
        if (bytes.Length < HeaderSize)
        {
            throw new InvalidDataException("delta paketi cok kucuk");
        }

        var firstHeader = new byte[HeaderSize];
        Array.Copy(bytes, firstHeader, HeaderSize);

        var (localName, localSize) = ParseHeader(firstHeader);
        if (!localName.Equals(DeltaLocation.ManifestEntry, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"delta paketinde manifest yok (ilk girdi: {localName})");
        }

        if (HeaderSize + localSize > bytes.Length)
        {
            throw new InvalidDataException("delta paketinde manifest kesik");
        }

        var manifestBytes = new byte[localSize];
        Array.Copy(bytes, HeaderSize, manifestBytes, 0, localSize);

        return (new DeltaLocation(url, false, local, bytes.Length, HeaderSize + RoundUp(localSize)),
                await ParseManifestAsync(manifestBytes, cancellationToken));
    }

    private static async Task<BlobManifest> ParseManifestAsync(byte[] gzip, CancellationToken cancellationToken)
    {
        await using var stream = new MemoryStream(gzip, writable: false);

        return await BlobManifest.LoadGzipAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> FetchRangeAsync(HttpClient http,
        string                                               url,
        long                                                 start,
        long                                                 length,
        CancellationToken                                    cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(start, start + length - 1);

        using var response = await http
                                   .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                                   .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        if (response.StatusCode != HttpStatusCode.PartialContent)
        {
            throw new HttpRequestException($"Range desteklenmiyor ({(int) response.StatusCode})");
        }

        var             buffer = new byte[length];
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);

        return buffer;
    }

    private static (string Name, long Size) ParseHeader(byte[] header)
    {
        if (header.Length < HeaderSize)
        {
            throw new InvalidDataException("tar basligi eksik");
        }

        var name = Encoding.ASCII.GetString(header, 0, 100).TrimEnd('\0');
        if (name.Length == 0)
        {
            throw new InvalidDataException("tar basligi bos");
        }

        if (name.StartsWith("././@LongLink", StringComparison.Ordinal))
        {
            throw new InvalidDataException("beklenmeyen GNU uzun ad girdisi");
        }

        var sizeText = Encoding.ASCII.GetString(header, 124, 12).Trim('\0', ' ');
        var size     = sizeText.Length == 0 ? 0 : Convert.ToInt64(sizeText, 8);

        return (name, size);
    }

    private static long RoundUp(long size)
    {
        return (size + HeaderSize - 1) / HeaderSize * HeaderSize;
    }
}
