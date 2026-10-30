using Migurdex.Cli.Dependencies;
using Xunit;

namespace Migurdex.Tests;

public sealed class DependencyUpdateTests
{
    [Theory]
    [InlineData("2026.10.26", "2026.10.26", true)]
    [InlineData("v2026.10.26", "2026.10.26", true)]
    [InlineData("20261021", "v20261021", true)]
    [InlineData("2026.10.21", "2026.10.26", false)]
    [InlineData("9.0.2", "9.0.1", false)]
    public void IsSameVersion_NormalizesPrefix(string installed, string latest, bool expected)
    {
        Assert.Equal(expected, DependencyUpdateChecker.IsSameVersion(installed, latest));
    }

    [Theory]
    [InlineData(null, "1.0")]
    [InlineData("1.0", null)]
    [InlineData("", "")]
    public void IsSameVersion_FalseForMissingValues(string? installed, string? latest)
    {
        Assert.False(DependencyUpdateChecker.IsSameVersion(installed, latest));
    }

    [Fact]
    public void VersionStore_WriteReadClear()
    {
        var root = NewTempDir();
        try
        {
            var store = new DependencyVersionStore(root);

            Assert.Null(store.Read(DependencyKind.YtDlp));

            store.Write(DependencyKind.YtDlp, "2026.10.26");

            Assert.Equal("2026.10.26", store.Read(DependencyKind.YtDlp));
            Assert.True(File.Exists(Path.Combine(root, ".migurdex-version-yt-dlp")));

            store.Clear(DependencyKind.YtDlp);

            Assert.Null(store.Read(DependencyKind.YtDlp));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void VersionStore_MpvLivesInSubdirectory()
    {
        var root = NewTempDir();
        try
        {
            var store = new DependencyVersionStore(root);
            store.Write(DependencyKind.Mpv, "20261021");

            Assert.True(File.Exists(Path.Combine(root, "mpv", ".migurdex-version-mpv")));
            Assert.Equal("20261021", store.Read(DependencyKind.Mpv));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void UpdateCache_RoundTrips()
    {
        var root = NewTempDir();
        try
        {
            var path = Path.Combine(root, "deps-check.json");

            var cache = DependencyUpdateCache.Load(path);
            Assert.Null(cache.Get(DependencyKind.Ffmpeg));

            cache.Set(DependencyKind.Ffmpeg, "9.0.2");
            cache.CheckedAt = DateTimeOffset.UtcNow;
            cache.Save(path);

            var reloaded = DependencyUpdateCache.Load(path);

            Assert.Equal("9.0.2", reloaded.Get(DependencyKind.Ffmpeg));
            Assert.True(reloaded.CheckedAt > DateTimeOffset.UtcNow.AddMinutes(-1));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void UpdateCache_MissingFileYieldsEmpty()
    {
        var cache = DependencyUpdateCache.Load(Path.Combine(Path.GetTempPath(),
                                                            "migurdex-missing-" + Guid.NewGuid().ToString("N"),
                                                            "none.json"));

        Assert.Null(cache.Get(DependencyKind.Mpv));
        Assert.Equal(default, cache.CheckedAt);
    }

    [Fact]
    public void SkippedVersions_AreCaseInsensitive()
    {
        var config = new Cli.Configuration.CliConfig();
        config.SkippedDependencyVersions["YT-DLP"] = "2026.10.21";

        Assert.True(config.SkippedDependencyVersions.TryGetValue("yt-dlp", out var value));
        Assert.Equal("2026.10.21", value);
    }

    private static string NewTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "migurdex-upd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);

        return path;
    }
}

public sealed class DependencyUpdateCheckerCacheTests
{
    [Fact]
    public async Task FreshCache_DoesNotHitNetwork()
    {
        var root = NewTempDir();
        try
        {
            var cachePath = Path.Combine(root, "deps-check.json");
            var cache     = DependencyUpdateCache.Load(cachePath);
            cache.Set(DependencyKind.YtDlp, "2026.10.26");
            cache.Set(DependencyKind.Ffmpeg, "9.0.2");
            cache.Set(DependencyKind.Mpv, "20261021");
            cache.CheckedAt = DateTimeOffset.UtcNow;
            cache.Save(cachePath);

            var handler = new CountingHandler();
            using var http = new HttpClient(handler);

            var resolver = new DependencyResolver(new ManualDependencyInstaller(),
                                                  _ => null,
                                                  Path.Combine(root, "tools"),
                                                  pathEnvironment: () => string.Empty);
            var versions = new DependencyVersionStore(Path.Combine(root, "tools"));
            versions.Write(DependencyKind.YtDlp, "2026.10.26");

            var checker = new DependencyUpdateChecker(resolver, http, versions, cachePath);
            var updates = await checker.CheckAsync(cancellationToken: TestContext.Current.CancellationToken);

            Assert.Empty(updates);
            Assert.Equal(0, handler.RequestCount);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task FailedRefresh_DoesNotPoisonCache()
    {
        var root = NewTempDir();
        try
        {
            var cachePath = Path.Combine(root, "deps-check.json");
            var cache     = DependencyUpdateCache.Load(cachePath);
            cache.CheckedAt = DateTimeOffset.UtcNow.AddDays(-3);
            cache.Set(DependencyKind.YtDlp, "2026.10.26");
            cache.Save(cachePath);

            var handler = new CountingHandler();
            using var http = new HttpClient(handler);

            var resolver = new DependencyResolver(new ManualDependencyInstaller(),
                                                  _ => null,
                                                  Path.Combine(root, "tools"),
                                                  pathEnvironment: () => string.Empty);
            var versions = new DependencyVersionStore(Path.Combine(root, "tools"));
            versions.Write(DependencyKind.YtDlp, "2026.10.01");

            handler.Fail = true;

            var checker = new DependencyUpdateChecker(resolver, http, versions, cachePath);
            var updates = await checker.CheckAsync(cancellationToken: TestContext.Current.CancellationToken);

            var update = Assert.Single(updates);
            Assert.Equal(DependencyKind.YtDlp, update.Kind);
            Assert.Equal("2026.10.26", update.LatestVersion);

            var reloaded = DependencyUpdateCache.Load(cachePath);
            Assert.Equal("2026.10.26", reloaded.Get(DependencyKind.YtDlp));
            Assert.Equal(DateTimeOffset.UtcNow.AddDays(-3).Date, reloaded.CheckedAt.Date);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task StaleCache_RefreshesOnce()
    {
        var root = NewTempDir();
        try
        {
            var cachePath = Path.Combine(root, "deps-check.json");
            var cache     = DependencyUpdateCache.Load(cachePath);
            cache.CheckedAt = DateTimeOffset.UtcNow.AddDays(-3);
            cache.Save(cachePath);

            var handler = new CountingHandler();
            using var http = new HttpClient(handler);

            var resolver = new DependencyResolver(new ManualDependencyInstaller(),
                                                  _ => null,
                                                  Path.Combine(root, "tools"),
                                                  pathEnvironment: () => string.Empty);
            var versions = new DependencyVersionStore(Path.Combine(root, "tools"));
            versions.Write(DependencyKind.YtDlp, "2026.10.01");

            var checker = new DependencyUpdateChecker(resolver, http, versions, cachePath);
            await checker.CheckAsync(cancellationToken: TestContext.Current.CancellationToken);

            Assert.True(handler.RequestCount > 0);

            var reloaded = DependencyUpdateCache.Load(cachePath);
            Assert.True(reloaded.CheckedAt > DateTimeOffset.UtcNow.AddMinutes(-1));

            var before = handler.RequestCount;
            await checker.CheckAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(before, handler.RequestCount);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static string NewTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "migurdex-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);

        return path;
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        private int _count;

        public bool Fail { get; set; }

        public int RequestCount => Volatile.Read(ref _count);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _count);

            if (Fail)
            {
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
            }

            var json = ReleaseJson(request.RequestUri?.AbsoluteUri ?? string.Empty);

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
        }

        private static string ReleaseJson(string url)
        {
            if (url.Contains("yt-dlp", StringComparison.OrdinalIgnoreCase))
            {
                return "{\"tag_name\":\"2026.10.26\",\"assets\":[{" +
                       "\"name\":\"yt-dlp.exe\",\"size\":1," +
                       "\"browser_download_url\":\"https://example.test/yt-dlp.exe\"," +
                       "\"digest\":\"sha256:" + new string('a', 64) + "\"}]}";
            }

            if (url.Contains("codexffmpeg", StringComparison.OrdinalIgnoreCase))
            {
                return "{\"tag_name\":\"9.0.2\",\"assets\":[{" +
                       "\"name\":\"ffmpeg-9.0.2-essentials_build.7z\",\"size\":1," +
                       "\"browser_download_url\":\"https://example.test/ffmpeg.7z\"," +
                       "\"digest\":\"sha256:" + new string('b', 64) + "\"}]}";
            }

            return "{\"tag_name\":\"20261021\",\"assets\":[{" +
                   "\"name\":\"mpv-x86_64-20261021-git-e471496c56.7z\",\"size\":1," +
                   "\"browser_download_url\":\"https://example.test/mpv.7z\"," +
                   "\"digest\":\"sha256:" + new string('c', 64) + "\"}]}";
        }
    }
}
