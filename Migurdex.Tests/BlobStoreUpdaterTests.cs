using Migurdex.Cli.Services;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Migurdex.Tests;

public sealed class BlobStoreUpdaterTests : IDisposable
{
    private const string Runtime = "linux-x64";

    private readonly string _root;

    public BlobStoreUpdaterTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"migurdex-blob-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }
        catch
        {
            // ignored
        }
    }

    private string MakeTree(string name, params (string Path, string Content)[] files)
    {
        var root = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(root, "cli"));
        Directory.CreateDirectory(Path.Combine(root, "api"));

        foreach (var (path, content) in files)
        {
            var full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        return root;
    }

    private static byte[] XzCompress(byte[] data)
    {
        var psi = new ProcessStartInfo("xz")
        {
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false
        };
        psi.ArgumentList.Add("-9");
        psi.ArgumentList.Add("-T0");
        psi.ArgumentList.Add("-c");

        using var proc   = Process.Start(psi)!;
        var       stdout = new MemoryStream();

        var copy = proc.StandardOutput.BaseStream.CopyToAsync(stdout);
        proc.StandardInput.BaseStream.Write(data);
        proc.StandardInput.Close();
        copy.GetAwaiter().GetResult();
        proc.WaitForExit();

        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException($"xz failed: {proc.StandardError.ReadToEnd()}");
        }

        return stdout.ToArray();
    }

    private (DeltaLocation Location, BlobManifest Manifest) BuildStore(string srcDir)
    {
        var paths     = new Dictionary<string, string>(StringComparer.Ordinal);
        var shaToPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var full in Directory.EnumerateFiles(srcDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(srcDir, full).Replace('\\', '/');
            var sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(full))).ToLowerInvariant();

            paths[rel] = sha;
            shaToPath.TryAdd(sha, full);
        }

        var compressed = shaToPath
                         .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                         .ToDictionary(kv => kv.Key,
                                       kv => XzCompress(File.ReadAllBytes(kv.Value)),
                                       StringComparer.Ordinal);

        var blobs   = new List<BlobEntry>();
        var running = 0L;
        foreach (var (sha, data) in compressed)
        {
            blobs.Add(new BlobEntry(sha, running + 512, data.Length));
            running += 512 + Align512(data.Length);
        }

        var manifest = new BlobManifest
        {
            Version = 1,
            Runtime = Runtime,
            Blobs   = blobs,
            Paths   = paths
        };

        var raw = JsonSerializer.SerializeToUtf8Bytes(manifest);
        var gz  = new MemoryStream();
        using (var gzip = new GZipStream(gz, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(raw);
        }

        var manifestGz = gz.ToArray();
        var tarPath    = Path.Combine(_root, $"migurdex-{Runtime}.delta.tar");

        using (var fs = File.Create(tarPath))
        using (var tw = new TarWriter(fs, TarEntryFormat.Gnu, leaveOpen: false))
        {
            WriteEntry(tw, "manifest.json.gz", manifestGz);

            foreach (var (sha, data) in compressed)
            {
                WriteEntry(tw, sha + ".xz", data);
            }
        }

        var payloadStart = 512 + Align512(manifestGz.Length);

        return (new DeltaLocation("", false, tarPath, new FileInfo(tarPath).Length, payloadStart), manifest);
    }

    private static long Align512(long size) => (size + 511) / 512 * 512;

    private static void WriteEntry(TarWriter tw, string name, byte[] data)
    {
        var entry = new GnuTarEntry(TarEntryType.RegularFile, name)
        {
            DataStream       = new MemoryStream(data),
            ModificationTime = DateTimeOffset.UnixEpoch
        };
        entry.Mode = UnixFileMode.UserRead
                     | UnixFileMode.UserWrite
                     | UnixFileMode.GroupRead
                     | UnixFileMode.OtherRead;

        tw.WriteEntry(entry);
    }

    private static bool XzAvailable()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("xz", "--version")
            {
                RedirectStandardOutput = true,
                UseShellExecute        = false
            });
            p!.WaitForExit(5000);

            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint) listener.LocalEndpoint).Port;
        listener.Stop();

        return port;
    }

    private sealed class StoreServer : IDisposable
    {
        private readonly HttpListener            _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly string                  _path;
        private readonly bool                    _supportRange;

        public string Url { get; }

        public StoreServer(string path, bool supportRange)
        {
            _path         = path;
            _supportRange = supportRange;

            Exception? last = null;
            for (var attempt = 0; attempt < 10; attempt++)
            {
                var port = FreePort();
                var url  = $"http://127.0.0.1:{port}/";
                var l    = new HttpListener();
                l.Prefixes.Add(url);

                try
                {
                    l.Start();
                }
                catch (Exception ex) when (ex is HttpListenerException or System.Net.Sockets.SocketException)
                {
                    last = ex;
                    try { l.Close(); }
                    catch
                    {
                        // ignored
                    }

                    continue;
                }

                _listener = l;
                Url       = $"http://127.0.0.1:{port}/store";
                _         = Task.Run(LoopAsync);

                return;
            }

            throw new InvalidOperationException("test sunucusu başlatılamadı", last);
        }

        private async Task LoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch
                {
                    return;
                }

                _ = Task.Run(() => Handle(ctx));
            }
        }

        private void Handle(HttpListenerContext ctx)
        {
            try
            {
                var bytes = File.ReadAllBytes(_path);
                var range = ctx.Request.Headers["Range"];

                if (_supportRange && range is not null && TryParse(range, bytes.Length, out var start, out var end))
                {
                    var len = end - start + 1;

                    ctx.Response.StatusCode               = 206;
                    ctx.Response.Headers["Content-Range"] = $"bytes {start}-{end}/{bytes.Length}";
                    ctx.Response.Headers["Accept-Ranges"] = "bytes";
                    ctx.Response.ContentLength64          = len;
                    ctx.Response.OutputStream.Write(bytes, (int) start, (int) len);
                }
                else
                {
                    ctx.Response.StatusCode      = 200;
                    ctx.Response.ContentLength64 = bytes.Length;
                    ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                }
            }
            catch
            {
                // ignored
            }
            finally
            {
                try { ctx.Response.Close(); }
                catch
                {
                    // ignored
                }
            }
        }

        private static bool TryParse(string header, long total, out long start, out long end)
        {
            start = end = 0;

            if (!header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var spec = header[6..].Split(',')[0].Trim();
            var dash = spec.IndexOf('-');
            if (dash < 0)
            {
                return false;
            }

            if (!long.TryParse(spec[..dash], out start))
            {
                return false;
            }

            if (spec[(dash + 1)..] is { Length: > 0 } endText && long.TryParse(endText, out var e))
            {
                end = Math.Min(e, total - 1);
            }
            else
            {
                end = total - 1;
            }

            return start >= 0 && start <= end && start < total;
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); }
            catch
            {
                // ignored
            }

            _listener.Close();
            _cts.Dispose();
        }
    }

    private static Dictionary<string, string> Index(string root)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var full in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, full).Replace('\\', '/');
            map[rel] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(full))).ToLowerInvariant();
        }

        return map;
    }

    private static string? RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "assets", "packaging", "build-blobs.py")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }

    [Fact]
    public async Task PythonProducer_IsReadableByClient()
    {
        if (!XzAvailable())
        {
            return;
        }

        var root = RepoRoot();
        if (root is null)
        {
            return;
        }

        var source = MakeTree("payload",
                              ("cli/migurdex", "cli-v1"),
                              ("api/Migurdex.Api", new string('q', 8192)));

        var py = new ProcessStartInfo("python3")
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            WorkingDirectory       = root
        };
        foreach (var a in new[]
                 {
                     Path.Combine(root, "assets", "packaging", "build-blobs.py"),
                     "--input", source,
                     "--output-dir", Path.Combine(_root, "out"),
                     "--runtime", Runtime
                 })
        {
            py.ArgumentList.Add(a);
        }

        using var proc = Process.Start(py)!;
        var       err  = await proc.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        proc.WaitForExit();

        Assert.True(proc.ExitCode == 0, err);

        var tar = Path.Combine(_root, "out", $"migurdex-{Runtime}.delta.tar");
        Assert.True(File.Exists(tar), $"uretilen paket yok: {tar}");

        using var server = new StoreServer(tar, supportRange: true);
        using var http   = new HttpClient();

        var (location, manifest) =
            await DeltaArchive.OpenAsync(http, server.Url, _root, TestContext.Current.CancellationToken);

        using var _ = location;

        Assert.Equal(Runtime, manifest.Runtime);
        Assert.Equal(Index(source), manifest.Paths);

        var install = Path.Combine(_root, "install");
        Directory.CreateDirectory(install);

        var result = await BlobStoreUpdater.ApplyAsync(server.Url,
                                                       location.PayloadStart,
                                                       null,
                                                       0,
                                                       install,
                                                       manifest,
                                                       _root,
                                                       null,
                                                       TestContext.Current.CancellationToken);

        Assert.Equal(manifest.Paths.Count, result.Written);
        Assert.Equal(Index(source), Index(install));
    }

    [Fact]
    public async Task DeltaArchive_ReadsManifestFromFirstEntry()
    {
        if (!XzAvailable())
        {
            return;
        }

        var ct = TestContext.Current.CancellationToken;

        var source = MakeTree("v1",
                              ("cli/migurdex", "cli-v1"),
                              ("api/Migurdex.Api", new string('z', 4096)));

        var (location, expected) = BuildStore(source);

        using var server = new StoreServer(location.LocalPath!, supportRange: true);
        using var http   = new HttpClient();

        var (opened, manifest) = await DeltaArchive.OpenAsync(http, server.Url, _root, ct);

        using var _ = opened;

        Assert.True(opened.SupportsRange);
        Assert.Equal(location.PayloadStart, opened.PayloadStart);
        Assert.Equal(expected.Paths, manifest.Paths);
        Assert.Equal(expected.Blobs.Count, manifest.Blobs.Count);

        Assert.Equal(512, manifest.Blobs[0].Offset);
    }

    [Fact]
    public async Task DeltaArchive_FallsBackWhenServerIgnoresRange()
    {
        if (!XzAvailable())
        {
            return;
        }

        var ct = TestContext.Current.CancellationToken;

        var source = MakeTree("v1", ("cli/migurdex", "cli-v1"));
        var (location, expected) = BuildStore(source);

        using var server = new StoreServer(location.LocalPath!, supportRange: false);
        using var http   = new HttpClient();

        var (opened, manifest) = await DeltaArchive.OpenAsync(http, server.Url, _root, ct);

        using var _ = opened;

        Assert.False(opened.SupportsRange);
        Assert.NotNull(opened.LocalPath);
        Assert.True(opened.TotalBytes > 0);
        Assert.Equal(expected.Paths, manifest.Paths);
    }

    [Fact]
    public async Task DeltaArchive_RejectsArchiveWithoutManifest()
    {
        var ct = TestContext.Current.CancellationToken;

        var bogus = Path.Combine(_root, "bogus.delta.tar");
        await using (var fs = File.Create(bogus))
        await using (var tw = new TarWriter(fs, TarEntryFormat.Gnu, leaveOpen: false))
        {
            WriteEntry(tw, "not-a-manifest.txt", "hello"u8.ToArray());
        }

        using var server = new StoreServer(bogus, supportRange: true);
        using var http   = new HttpClient();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
                                                           DeltaArchive.OpenAsync(http, server.Url, _root, ct));
    }

    [Fact]
    public async Task Apply_FreshInstall_WritesEveryFile()
    {
        if (!XzAvailable())
        {
            return;
        }

        var ct = TestContext.Current.CancellationToken;

        var source = MakeTree("v1",
                              ("cli/migurdex", "cli-v1"),
                              ("cli/migurdex.dll", new string('a', 2048)),
                              ("api/Migurdex.Api", "api-v1"));

        var (location, manifest) = BuildStore(source);
        var install = Path.Combine(_root, "install");
        Directory.CreateDirectory(install);

        using var server = new StoreServer(location.LocalPath!, supportRange: true);

        var result = await BlobStoreUpdater.ApplyAsync(server.Url, location.PayloadStart, null, 0, install, manifest, _root, null, ct);

        Assert.Equal(0, result.Reused);
        Assert.Equal(manifest.Paths.Count, result.Written);
        Assert.Equal(0, result.Deleted);
        Assert.Empty(result.Deferred);
        Assert.Equal(Index(source), Index(install));
    }

    [Fact]
    public async Task Apply_OnlyChangedFileDownloaded()
    {
        if (!XzAvailable())
        {
            return;
        }

        var ct = TestContext.Current.CancellationToken;

        var v1 = MakeTree("v1",
                          ("cli/migurdex", "cli"),
                          ("cli/big.dll", new string('x', 100_000)),
                          ("api/Migurdex.Api", "api"));

        var (_, v1Manifest) = BuildStore(v1);

        var install = Path.Combine(_root, "install");
        Directory.CreateDirectory(install);

        foreach (var (rel, sha) in v1Manifest.Paths)
        {
            var full = Path.Combine(install, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.Copy(Path.Combine(v1, rel.Replace('/', Path.DirectorySeparatorChar)), full, true);
            Assert.Equal(sha, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(full))).ToLowerInvariant());
        }

        var v2 = MakeTree("v2",
                          ("cli/migurdex", "cli"),
                          ("cli/big.dll", new string('x', 100_000)),
                          ("api/Migurdex.Api", "api-v2-changed"));

        var (v2Location, v2Manifest) = BuildStore(v2);

        using var server = new StoreServer(v2Location.LocalPath!, supportRange: true);

        var result = await BlobStoreUpdater.ApplyAsync(server.Url, v2Location.PayloadStart, null, 0, install, v2Manifest, _root, null, ct);

        Assert.Equal(1, result.Written);
        Assert.Equal(2, result.Reused);
        Assert.Equal(Index(v2), Index(install));

        Assert.InRange(result.BytesDownloaded, 1, 500);
    }

    [Fact]
    public async Task Apply_NoChanges_DownloadsNothing()
    {
        if (!XzAvailable())
        {
            return;
        }

        var ct = TestContext.Current.CancellationToken;

        var source = MakeTree("v1",
                              ("cli/migurdex", "cli"),
                              ("api/Migurdex.Api", "api"));

        var (location, manifest) = BuildStore(source);
        var install = Path.Combine(_root, "install");
        Directory.CreateDirectory(install);

        using var server = new StoreServer(location.LocalPath!, supportRange: true);

        var first = await BlobStoreUpdater.ApplyAsync(server.Url, location.PayloadStart, null, 0, install, manifest, _root, null, ct);
        Assert.Equal(2, first.Written);

        var second = await BlobStoreUpdater.ApplyAsync(server.Url, location.PayloadStart, null, 0, install, manifest, _root, null, ct);

        Assert.Equal(0, second.Written);
        Assert.Equal(2, second.Reused);
        Assert.Equal(0, second.BytesDownloaded);
    }

    [Fact]
    public async Task Apply_SharedContent_FetchedOnceForBothPaths()
    {
        if (!XzAvailable())
        {
            return;
        }

        var ct = TestContext.Current.CancellationToken;

        var payload = new string('r', 50_000);

        var source = MakeTree("v1",
                              ("cli/runtime.dll", payload),
                              ("api/runtime.dll", payload),
                              ("cli/migurdex", "cli"));

        var (location, manifest) = BuildStore(source);

        Assert.Equal(2, manifest.Blobs.Count);
        Assert.Equal(3, manifest.Paths.Count);

        var install = Path.Combine(_root, "install");
        Directory.CreateDirectory(install);

        using var server = new StoreServer(location.LocalPath!, supportRange: true);

        var result = await BlobStoreUpdater.ApplyAsync(server.Url, location.PayloadStart, null, 0, install, manifest, _root, null, ct);

        Assert.Equal(3, result.Written);
        Assert.Equal(Index(source), Index(install));

        Assert.InRange(result.BytesDownloaded, 1, manifest.Blobs.Sum(b => (long) b.Length) + 64);
    }

    [Fact]
    public async Task Apply_ServerWithoutRange_StillCorrect()
    {
        if (!XzAvailable())
        {
            return;
        }

        var ct = TestContext.Current.CancellationToken;

        var source = MakeTree("v1",
                              ("cli/migurdex", "cli"),
                              ("api/Migurdex.Api", "api"));

        var (location, manifest) = BuildStore(source);
        var install = Path.Combine(_root, "install");
        Directory.CreateDirectory(install);

        using var server = new StoreServer(location.LocalPath!, supportRange: false);

        var result = await BlobStoreUpdater.ApplyAsync(server.Url, location.PayloadStart, null, 0, install, manifest, _root, null, ct);

        Assert.Equal(2, result.Written);
        Assert.Empty(result.Deferred);
        Assert.Equal(Index(source), Index(install));

        Assert.Equal(new FileInfo(location.LocalPath!).Length, result.BytesDownloaded);
    }

    [Fact]
    public async Task Apply_RemovedFilesArePruned()
    {
        if (!XzAvailable())
        {
            return;
        }

        var ct = TestContext.Current.CancellationToken;

        var v1 = MakeTree("v1",
                          ("cli/migurdex", "cli"),
                          ("cli/old.dll", "olduzantı"),
                          ("api/Migurdex.Api", "api"));

        var (_, v1Manifest) = BuildStore(v1);

        var install = Path.Combine(_root, "install");
        Directory.CreateDirectory(install);
        foreach (var (rel, _) in v1Manifest.Paths)
        {
            var full = Path.Combine(install, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.Copy(Path.Combine(v1, rel.Replace('/', Path.DirectorySeparatorChar)), full, true);
        }

        var v2 = MakeTree("v2",
                          ("cli/migurdex", "cli"),
                          ("api/Migurdex.Api", "api"));

        var (v2Location, v2Manifest) = BuildStore(v2);

        using var server = new StoreServer(v2Location.LocalPath!, supportRange: true);

        var result = await BlobStoreUpdater.ApplyAsync(server.Url, v2Location.PayloadStart, null, 0, install, v2Manifest, _root, null, ct);

        Assert.Equal(1, result.Deleted);
        Assert.Equal(Index(v2), Index(install));
        Assert.False(File.Exists(Path.Combine(install, "cli", "old.dll")));
    }

    [Fact]
    public async Task Apply_CorruptBlob_Throws()
    {
        if (!XzAvailable())
        {
            return;
        }

        var ct = TestContext.Current.CancellationToken;

        var source = MakeTree("v1", ("cli/migurdex", "cli"));
        var (location, manifest) = BuildStore(source);

        var bytes = File.ReadAllBytes(location.LocalPath!);
        var blob  = manifest.Blobs[0];
        var start = location.PayloadStart + blob.Offset;
        for (var i = 0; i < 8; i++)
        {
            bytes[start + i] ^= 0xFF;
        }

        File.WriteAllBytes(location.LocalPath!, bytes);

        var install = Path.Combine(_root, "install");
        Directory.CreateDirectory(install);

        using var server = new StoreServer(location.LocalPath!, supportRange: true);

        await Assert.ThrowsAnyAsync<Exception>(() =>
                                                   BlobStoreUpdater.ApplyAsync(
                                                       server.Url,
                                                       location.PayloadStart,
                                                       null,
                                                       0,
                                                       install,
                                                       manifest,
                                                       _root,
                                                       null,
                                                       ct));
    }

    [Fact]
    public async Task Apply_RejectsPathTraversal()
    {
        if (!XzAvailable())
        {
            return;
        }

        var ct = TestContext.Current.CancellationToken;

        var source = MakeTree("v1", ("cli/migurdex", "cli"));
        var (location, manifest) = BuildStore(source);

        var existingSha = manifest.Paths.Values.First();
        manifest.Paths["../../escape.txt"] = existingSha;

        var install = Path.Combine(_root, "install");
        Directory.CreateDirectory(install);

        using var server = new StoreServer(location.LocalPath!, supportRange: true);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
                                                                    BlobStoreUpdater.ApplyAsync(
                                                                        server.Url,
                                                                        location.PayloadStart,
                                                                        null,
                                                                        0,
                                                                        install,
                                                                        manifest,
                                                                        _root,
                                                                        null,
                                                                        ct));

        Assert.Contains("kokun disinda", ex.Message);
        Assert.False(File.Exists(Path.Combine(_root, "escape.txt")));
        Assert.False(File.Exists(Path.Combine(Path.GetTempPath(), "escape.txt")));
    }

    [Fact]
    public async Task Apply_PrunesStaleOldBackup()
    {
        if (!XzAvailable())
        {
            return;
        }

        var ct = TestContext.Current.CancellationToken;

        var source = MakeTree("v1",
                              ("migurdex", "launcher"),
                              ("cli/migurdex", "cli"));

        var (location, manifest) = BuildStore(source);

        var install = Path.Combine(_root, "install");
        Directory.CreateDirectory(Path.Combine(install, "cli"));

        using var firstServer = new StoreServer(location.LocalPath!, supportRange: true);

        var first = await BlobStoreUpdater.ApplyAsync(
                        firstServer.Url,
                        location.PayloadStart,
                        null,
                        0,
                        install,
                        manifest,
                        _root,
                        null,
                        ct);
        Assert.Equal(2, first.Written);

        var backup = Path.Combine(install, "migurdex.old");
        File.WriteAllText(backup, "eski surum");

        using var server = new StoreServer(location.LocalPath!, supportRange: true);

        var second = await BlobStoreUpdater.ApplyAsync(server.Url, location.PayloadStart, null, 0, install, manifest, _root, null, ct);

        Assert.Equal(0, second.Written);
        Assert.Equal(1, second.Deleted);
        Assert.False(File.Exists(backup));
        Assert.True(File.Exists(Path.Combine(install, "migurdex")));
    }

    [Fact]
    public async Task Apply_PrunesStaleRootFile()
    {
        if (!XzAvailable())
        {
            return;
        }

        var ct = TestContext.Current.CancellationToken;

        var source = MakeTree("v1", ("cli/migurdex", "cli"));
        var (location, manifest) = BuildStore(source);

        var install = Path.Combine(_root, "install");
        Directory.CreateDirectory(install);

        using var server = new StoreServer(location.LocalPath!, supportRange: true);

        var first = await BlobStoreUpdater.ApplyAsync(server.Url, location.PayloadStart, null, 0, install, manifest, _root, null, ct);
        Assert.Equal(1, first.Written);

        var stale = Path.Combine(install, "migurdex");
        File.WriteAllText(stale, "eski single-file binary");

        var second = await BlobStoreUpdater.ApplyAsync(server.Url, location.PayloadStart, null, 0, install, manifest, _root, null, ct);

        Assert.Equal(1, second.Deleted);
        Assert.False(File.Exists(stale));
    }
}
