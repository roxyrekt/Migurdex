using Migurdex.Cli.Services;
using System.Diagnostics;
using System.Formats.Tar;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
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

    private (string TarPath, BlobManifest Manifest) BuildStore(string srcDir)
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

        var tarPath = Path.Combine(_root, $"migurdex-{Runtime}.blobs.tar");

        using (var fs = File.Create(tarPath))
        using (var tw = new TarWriter(fs, TarEntryFormat.Gnu, leaveOpen: false))
        {
            foreach (var (sha, full) in shaToPath.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                var entry = new GnuTarEntry(TarEntryType.RegularFile, sha + ".xz")
                {
                    DataStream       = new MemoryStream(XzCompress(File.ReadAllBytes(full))),
                    ModificationTime = DateTimeOffset.UnixEpoch
                };
                entry.Mode = UnixFileMode.UserRead
                             | UnixFileMode.UserWrite
                             | UnixFileMode.GroupRead
                             | UnixFileMode.OtherRead;

                tw.WriteEntry(entry);
            }
        }

        var blobs = new List<BlobEntry>();

        using (var fs = File.OpenRead(tarPath))
        {
            var header = new byte[512];

            while (fs.Position + 512 <= fs.Length)
            {
                var headerPos = fs.Position;
                if (fs.Read(header, 0, 512) != 512)
                {
                    break;
                }

                if (header.All(b => b == 0))
                {
                    break;
                }

                var name      = Encoding.ASCII.GetString(header, 0, 100).TrimEnd('\0');
                var sizeField = Encoding.ASCII.GetString(header, 124, 12).Trim('\0', ' ');
                var size      = sizeField.Length == 0 ? 0 : Convert.ToInt64(sizeField, 8);

                if (name.EndsWith(".xz", StringComparison.Ordinal))
                {
                    blobs.Add(new BlobEntry(name[..^3], headerPos + 512, (int) size));
                }

                var padded = (size + 511) / 512 * 512;
                fs.Seek(headerPos + 512 + padded, SeekOrigin.Begin);
            }
        }

        var manifest = new BlobManifest
        {
            Version = 1,
            Runtime = Runtime,
            Blobs   = blobs,
            Paths   = paths
        };

        return (tarPath, manifest);
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

        var (tarPath, manifest) = BuildStore(source);
        var install = Path.Combine(_root, "install");
        Directory.CreateDirectory(install);

        using var server = new StoreServer(tarPath, supportRange: true);

        var result = await BlobStoreUpdater.ApplyAsync(server.Url, install, manifest, _root, null, ct);

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

        var (v2Tar, v2Manifest) = BuildStore(v2);

        using var server = new StoreServer(v2Tar, supportRange: true);

        var result = await BlobStoreUpdater.ApplyAsync(server.Url, install, v2Manifest, _root, null, ct);

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

        var (tarPath, manifest) = BuildStore(source);
        var install = Path.Combine(_root, "install");
        Directory.CreateDirectory(install);

        using var server = new StoreServer(tarPath, supportRange: true);

        var first = await BlobStoreUpdater.ApplyAsync(server.Url, install, manifest, _root, null, ct);
        Assert.Equal(2, first.Written);

        var second = await BlobStoreUpdater.ApplyAsync(server.Url, install, manifest, _root, null, ct);

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

        var (tarPath, manifest) = BuildStore(source);

        Assert.Equal(2, manifest.Blobs.Count);
        Assert.Equal(3, manifest.Paths.Count);

        var install = Path.Combine(_root, "install");
        Directory.CreateDirectory(install);

        using var server = new StoreServer(tarPath, supportRange: true);

        var result = await BlobStoreUpdater.ApplyAsync(server.Url, install, manifest, _root, null, ct);

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

        var (tarPath, manifest) = BuildStore(source);
        var install = Path.Combine(_root, "install");
        Directory.CreateDirectory(install);

        using var server = new StoreServer(tarPath, supportRange: false);

        var result = await BlobStoreUpdater.ApplyAsync(server.Url, install, manifest, _root, null, ct);

        Assert.Equal(2, result.Written);
        Assert.Empty(result.Deferred);
        Assert.Equal(Index(source), Index(install));

        Assert.Equal(new FileInfo(tarPath).Length, result.BytesDownloaded);
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

        var (v2Tar, v2Manifest) = BuildStore(v2);

        using var server = new StoreServer(v2Tar, supportRange: true);

        var result = await BlobStoreUpdater.ApplyAsync(server.Url, install, v2Manifest, _root, null, ct);

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
        var (tarPath, manifest) = BuildStore(source);

        var bytes = File.ReadAllBytes(tarPath);
        var blob  = manifest.Blobs[0];
        for (var i = 0; i < 8; i++)
        {
            bytes[blob.Offset + i] ^= 0xFF;
        }

        File.WriteAllBytes(tarPath, bytes);

        var install = Path.Combine(_root, "install");
        Directory.CreateDirectory(install);

        using var server = new StoreServer(tarPath, supportRange: true);

        await Assert.ThrowsAnyAsync<Exception>(() =>
                                                   BlobStoreUpdater.ApplyAsync(
                                                       server.Url,
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
        var (tarPath, manifest) = BuildStore(source);

        var existingSha = manifest.Paths.Values.First();
        manifest.Paths["../../escape.txt"] = existingSha;

        var install = Path.Combine(_root, "install");
        Directory.CreateDirectory(install);

        using var server = new StoreServer(tarPath, supportRange: true);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
                                                                    BlobStoreUpdater.ApplyAsync(
                                                                        server.Url,
                                                                        install,
                                                                        manifest,
                                                                        _root,
                                                                        null,
                                                                        ct));

        Assert.Contains("kokun disinda", ex.Message);
        Assert.False(File.Exists(Path.Combine(_root, "escape.txt")));
        Assert.False(File.Exists(Path.Combine(Path.GetTempPath(), "escape.txt")));
    }
}
