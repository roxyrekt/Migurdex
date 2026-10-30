using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Migurdex.Cli.Services;

public sealed record BlobUpdateResult(
    int          Written,
    int          Reused,
    int          Deleted,
    long         BytesDownloaded,
    List<string> Deferred,
    int          ExecRestored = 0);

internal static class BlobStoreUpdater
{
    private const int MaxParallel = 4;

    public static async Task<BlobUpdateResult> ApplyAsync(string storeUrl,
        string                                                   installRoot,
        BlobManifest                                             manifest,
        string                                                   workDir,
        IProgress<string>?                                       progress          = null,
        CancellationToken                                        cancellationToken = default)
    {
        var bySha = manifest.BySha();

        var local = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rel in manifest.Paths.Keys)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var full = FullPath(installRoot, rel);
            if (!File.Exists(full))
            {
                continue;
            }

            try
            {
                await using var fs = File.OpenRead(full);
                local[rel] = Convert.ToHexString(await SHA256.HashDataAsync(fs, cancellationToken)).ToLowerInvariant();
            }
            catch
            {
                // ignored
            }
        }

        var needed = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var reused = 0;

        foreach (var (rel, sha) in manifest.Paths)
        {
            if (local.TryGetValue(rel, out var have) && have.Equals(sha, StringComparison.OrdinalIgnoreCase))
            {
                reused++;
                continue;
            }

            if (!bySha.ContainsKey(sha))
            {
                throw new InvalidDataException($"manifest bu blob'u içermiyor: {Short(sha)}");
            }

            if (!needed.TryGetValue(sha, out var list))
            {
                needed[sha] = list = [];
            }

            list.Add(rel);
        }

        var  deferred   = new List<string>();
        var  written    = 0;
        long downloaded = 0;

        if (needed.Count > 0)
        {
            progress?.Report($"{needed.Count} dosya indirilecek ({reused} dosya zaten güncel)");

            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromMinutes(30);
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "migurdex");

            var (supportsRange, localStore, storeBytes) =
                await ProbeAsync(http,
                                 storeUrl,
                                 bySha.Values.OrderBy(b => b.Length).First(),
                                 workDir,
                                 cancellationToken)
                    .ConfigureAwait(false);

            if (!supportsRange)
            {
                downloaded += storeBytes;
            }

            try
            {
                using var gate = new SemaphoreSlim(MaxParallel);

                var tasks = needed.Select(async pair =>
                {
                    await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        var blob = bySha[pair.Key];

                        var compressed = supportsRange
                                             ? await FetchRangeAsync(http, storeUrl, blob, cancellationToken)
                                                   .ConfigureAwait(false)
                                             : SliceLocal(localStore!, blob);

                        if (supportsRange)
                        {
                            Interlocked.Add(ref downloaded, compressed.LongLength);
                        }

                        var raw    = Decompress(compressed);
                        var actual = Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant();
                        if (!actual.Equals(pair.Key, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidDataException($"blob bozuk: {Short(pair.Key)}");
                        }

                        foreach (var rel in pair.Value)
                        {
                            if (TryWriteAtomic(FullPath(installRoot, rel), raw, out var lockPath))
                            {
                                Interlocked.Increment(ref written);
                            }
                            else
                            {
                                lock (deferred)
                                {
                                    deferred.Add(lockPath ?? rel);
                                }
                            }
                        }
                    }
                    finally
                    {
                        gate.Release();
                    }
                });

                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            finally
            {
                if (localStore is not null)
                {
                    try { File.Delete(localStore); }
                    catch
                    {
                        // ignored
                    }
                }
            }
        }

        // depo mod biti taşımadığı için yazılan dosyalar 644 kalır; +x geri kur.
        var execRestored = RestoreExecutableBits(installRoot, manifest);

        var deleted = PruneStale(installRoot, manifest);

        return new BlobUpdateResult(written, reused, deleted, downloaded, deferred, execRestored);
    }


    private static async Task<(bool SupportsRange, string? LocalStore, long StoreBytes)> ProbeAsync(
        HttpClient        http,
        string            storeUrl,
        BlobEntry         probe,
        string            workDir,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, storeUrl);
        request.Headers.Range = new RangeHeaderValue(probe.Offset, probe.Offset + probe.Length - 1);

        using var response = await http
                                   .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                                   .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            return (true, null, 0);
        }

        var path = Path.Combine(workDir, "blobstore.bin");
        await using (var src = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var dst = File.Create(path))
        {
            await src.CopyToAsync(dst, cancellationToken).ConfigureAwait(false);
        }

        return (false, path, new FileInfo(path).Length);
    }

    private static async Task<byte[]> FetchRangeAsync(HttpClient http,
        string                                                   storeUrl,
        BlobEntry                                                blob,
        CancellationToken                                        cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, storeUrl);
        request.Headers.Range = new RangeHeaderValue(blob.Offset, blob.Offset + blob.Length - 1);

        using var response = await http
                                   .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                                   .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        if (response.StatusCode != HttpStatusCode.PartialContent)
        {
            throw new HttpRequestException($"Range desteklenmiyor ({(int) response.StatusCode})");
        }

        var             buffer = new byte[blob.Length];
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);

        return buffer;
    }

    private static byte[] SliceLocal(string storePath, BlobEntry blob)
    {
        using var fs = File.OpenRead(storePath);
        fs.Seek(blob.Offset, SeekOrigin.Begin);

        var buffer = new byte[blob.Length];
        fs.ReadExactly(buffer, 0, blob.Length);

        return buffer;
    }

    private static byte[] Decompress(byte[] xz)
    {
        using var input  = new MemoryStream(xz, writable: false);
        using var stream = new SharpCompress.Compressors.Xz.XZStream(input);
        using var output = new MemoryStream();

        stream.CopyTo(output);

        return output.ToArray();
    }


    private static bool TryWriteAtomic(string dest, byte[] content, out string? deferredPath)
    {
        deferredPath = null;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

            var tmp = dest + ".new";
            File.WriteAllBytes(tmp, content);
            File.Move(tmp, dest, true);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            deferredPath = Path.GetRelativePath(Path.GetDirectoryName(dest) ?? dest, dest) + ".new";

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static int RestoreExecutableBits(string installRoot, BlobManifest manifest)
    {
        if (OperatingSystem.IsWindows() || manifest.Exec.Count == 0)
        {
            return 0;
        }

        const UnixFileMode bits = UnixFileMode.UserRead
                                  | UnixFileMode.UserWrite
                                  | UnixFileMode.UserExecute
                                  | UnixFileMode.GroupRead
                                  | UnixFileMode.GroupExecute
                                  | UnixFileMode.OtherRead
                                  | UnixFileMode.OtherExecute;

        var restored = 0;

        foreach (var rel in manifest.Exec)
        {
            var full = FullPath(installRoot, rel);

            try
            {
                if (!File.Exists(full) || File.GetUnixFileMode(full) == bits)
                {
                    continue;
                }

                File.SetUnixFileMode(full, bits);
                restored++;
            }
            catch
            {
                // ignored
            }
        }

        return restored;
    }

    private static int PruneStale(string installRoot, BlobManifest manifest)
    {
        var keep    = new HashSet<string>(manifest.Paths.Keys, StringComparer.Ordinal);
        var deleted = 0;

        foreach (var sub in new[] { "cli", "api" })
        {
            var dir = Path.Combine(installRoot, sub);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                if (file.EndsWith(".new", StringComparison.Ordinal) || file.EndsWith(".old", StringComparison.Ordinal))
                {
                    continue;
                }

                var rel = Path.GetRelativePath(installRoot, file).Replace('\\', '/');
                if (keep.Contains(rel))
                {
                    continue;
                }

                try
                {
                    File.Delete(file);
                    deleted++;
                }
                catch
                {
                    // ignored
                }
            }
        }

        return deleted;
    }


    private static string FullPath(string root, string relative)
    {
        return Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    }

    private static string Short(string sha)
    {
        return sha.Length > 12 ? sha[..12] + "…" : sha;
    }
}
