using Migurdex.Cli.Dependencies;
using Migurdex.Cli.Services.Downloads;
using System.Diagnostics;
using Xunit;

namespace Migurdex.Tests;

public sealed class DependencyResolverTests
{
    [Theory]
    [InlineData("mpv", DependencyKind.Mpv)]
    [InlineData("MPV", DependencyKind.Mpv)]
    [InlineData("yt-dlp", DependencyKind.YtDlp)]
    [InlineData("ytdlp", DependencyKind.YtDlp)]
    [InlineData("ffmpeg", DependencyKind.Ffmpeg)]
    public void Catalog_ParsesKnownToolNames(string value, DependencyKind expected)
    {
        Assert.True(DependencyCatalog.TryParse(value, out var kind));
        Assert.Equal(expected, kind);
    }

    [Fact]
    public void Catalog_RejectsUnknownToolNames()
    {
        Assert.False(DependencyCatalog.TryParse("vlc", out _));
        Assert.False(DependencyCatalog.TryParse(null, out _));
        Assert.False(DependencyCatalog.TryParse("  ", out _));
    }

    [Fact]
    public void Catalog_DefaultToolsDirectoryEndsWithToolsFolder()
    {
        Assert.Equal("tools", Path.GetFileName(Cli.Configuration.CliConfig.DefaultToolsDirectory));
    }

    [Fact]
    public void Resolve_PrefersExplicitConfiguredPath()
    {
        var root = NewTempDir();
        try
        {
            var configured = Path.Combine(root, "custom-mpv");
            File.WriteAllText(configured, "x");

            var resolver = CreateResolver(root, _ => configured);

            Assert.Equal(Path.GetFullPath(configured), resolver.Resolve(DependencyKind.Mpv));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Resolve_DoesNotFallBackToPathWhenExplicitPathIsMissing()
    {
        var root = NewTempDir();
        try
        {
            var missing = Path.Combine(root, "does-not-exist");

            var resolver = CreateResolver(root,
                                          _ => missing,
                                          pathEnvironment: () => root);

            Assert.Null(resolver.Resolve(DependencyKind.Mpv));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Resolve_UsesToolsDirectoryBeforePath()
    {
        var root = NewTempDir();
        var toolsDirectory = Path.Combine(root, "tools");
        Directory.CreateDirectory(toolsDirectory);
        try
        {
            var toolName = DependencyCatalog.FileName(DependencyKind.YtDlp);
            var toolPath = Path.Combine(toolsDirectory, toolName);
            File.WriteAllText(toolPath, "x");

            var resolver = CreateResolver(root,
                                          _ => "yt-dlp",
                                          toolsDirectory: toolsDirectory,
                                          pathEnvironment: () => string.Empty);

            Assert.Equal(toolPath, resolver.Resolve(DependencyKind.YtDlp));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Resolve_FallsBackToPath()
    {
        var root = NewTempDir();
        var binDirectory = Path.Combine(root, "bin");
        Directory.CreateDirectory(binDirectory);
        try
        {
            var toolPath = Path.Combine(binDirectory, DependencyCatalog.FileName(DependencyKind.Ffmpeg));
            File.WriteAllText(toolPath, "x");

            var resolver = CreateResolver(root,
                                          _ => "ffmpeg",
                                          pathEnvironment: () => binDirectory);

            Assert.Equal(toolPath, resolver.Resolve(DependencyKind.Ffmpeg));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Resolve_CachesUntilInvalidated()
    {
        var root = NewTempDir();
        try
        {
            var resolver = CreateResolver(root,
                                          _ => "mpv",
                                          toolsDirectory: root,
                                          pathEnvironment: () => string.Empty);

            Assert.Null(resolver.Resolve(DependencyKind.Mpv));

            var toolPath = Path.Combine(root, DependencyCatalog.FileName(DependencyKind.Mpv));
            File.WriteAllText(toolPath, "x");

            Assert.Null(resolver.Resolve(DependencyKind.Mpv));

            resolver.InvalidateCache();

            Assert.Equal(toolPath, resolver.Resolve(DependencyKind.Mpv));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task CheckAsync_ReportsMissingWhenNotResolved()
    {
        var root = NewTempDir();
        try
        {
            var resolver = CreateResolver(root,
                                          _ => "mpv",
                                          pathEnvironment: () => string.Empty);

            var status = await resolver.CheckAsync(DependencyKind.Mpv,
                                                   includeVersion: true,
                                                   TestContext.Current.CancellationToken);

            Assert.False(status.IsInstalled);
            Assert.Equal(DependencyState.Missing, status.State);
            Assert.Null(status.ResolvedPath);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task CheckAsync_ReadsVersionFromExecutable()
    {
        var root = NewTempDir();
        try
        {
            var toolPath = Path.Combine(root, DependencyCatalog.FileName(DependencyKind.Mpv));
            File.WriteAllText(toolPath, "x");

            var processRunner = new FakeProcessRunner((startInfo, _) =>
            {
                Assert.Equal(toolPath, startInfo.FileName);
                Assert.Contains("--version", startInfo.ArgumentList);

                return Task.FromResult(new ExternalProcessResult(0, "mpv v0.41.0\nsecond line"));
            });

            var resolver = new DependencyResolver(new ManualDependencyInstaller(),
                                                  _ => toolPath,
                                                  root,
                                                  pathEnvironment: () => string.Empty,
                                                  processRunner: processRunner);

            var status = await resolver.CheckAsync(DependencyKind.Mpv,
                                                   includeVersion: true,
                                                   TestContext.Current.CancellationToken);

            Assert.True(status.IsInstalled);
            Assert.Equal("mpv v0.41.0", status.Version);
            Assert.Equal(toolPath, status.ResolvedPath);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task CheckAsync_IgnoresFailingVersionProbe()
    {
        var root = NewTempDir();
        try
        {
            var toolPath = Path.Combine(root, DependencyCatalog.FileName(DependencyKind.Mpv));
            File.WriteAllText(toolPath, "x");

            var processRunner = new FakeProcessRunner((_, _) =>
                throw new ExternalProcessStartException("boom"));

            var resolver = new DependencyResolver(new ManualDependencyInstaller(),
                                                  _ => toolPath,
                                                  root,
                                                  pathEnvironment: () => string.Empty,
                                                  processRunner: processRunner);

            var status = await resolver.CheckAsync(DependencyKind.Mpv,
                                                   includeVersion: true,
                                                   TestContext.Current.CancellationToken);

            Assert.True(status.IsInstalled);
            Assert.Null(status.Version);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ManualInstaller_DoesNotSupportAutoInstall()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var installer = new ManualDependencyInstaller();

        Assert.False(installer.CanAutoInstall);
        Assert.Empty(installer.SupportedTools);
        Assert.Contains("mpv", installer.GetManualInstructions(DependencyKind.Mpv));
    }

    [Fact]
    public async Task SetupRunner_ReturnsManualInstructionsWhenUnsupported()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var root = NewTempDir();
        try
        {
            var resolver = CreateResolver(root,
                                          _ => "mpv",
                                          pathEnvironment: () => string.Empty);

            var results = await DependencySetupRunner.InstallAsync(resolver,
                                                                    [DependencyKind.Mpv],
                                                                    cancellationToken: TestContext.Current
                                                                                           .CancellationToken);

            var result = Assert.Single(results);
            Assert.False(result.Success);
            Assert.Contains("mpv", result.Message);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void SetupRunner_FindMissingListsOnlyUnresolvedTools()
    {
        var root = NewTempDir();
        try
        {
            var toolPath = Path.Combine(root, DependencyCatalog.FileName(DependencyKind.Mpv));
            File.WriteAllText(toolPath, "x");

            var resolver = CreateResolver(root,
                                          _ => null,
                                          pathEnvironment: () => string.Empty);

            var missing = DependencySetupRunner.FindMissing(resolver);

            Assert.Contains(DependencyKind.YtDlp, missing);
            Assert.Contains(DependencyKind.Ffmpeg, missing);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void WindowsInstaller_AdvertisesSupportedToolsAndInstructions()
    {
        var installer = new WindowsDependencyInstaller(Path.GetTempPath());

        Assert.True(installer.CanAutoInstall);
        Assert.Contains(DependencyKind.Mpv, installer.SupportedTools);
        Assert.Contains(DependencyKind.YtDlp, installer.SupportedTools);
        Assert.Contains(DependencyKind.Ffmpeg, installer.SupportedTools);
        Assert.Contains("winget", installer.GetManualInstructions(DependencyKind.Mpv));
    }

    private static DependencyResolver CreateResolver(
        string                        tempRoot,
        Func<DependencyKind, string?> configuredPath,
        string?                       toolsDirectory   = null,
        Func<string?>?                pathEnvironment  = null)
    {
        return new DependencyResolver(new ManualDependencyInstaller(),
                                      configuredPath,
                                      toolsDirectory ?? Path.Combine(tempRoot, "missing-tools"),
                                      pathEnvironment ?? (() => string.Empty));
    }

    private static string NewTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "migurdex-deps-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);

        return path;
    }

    private sealed class FakeProcessRunner : IExternalProcessRunner
    {
        private readonly Func<ProcessStartInfo, CancellationToken, Task<ExternalProcessResult>> _handler;

        public FakeProcessRunner(
            Func<ProcessStartInfo, CancellationToken, Task<ExternalProcessResult>> handler)
        {
            _handler = handler;
        }

        public Task<ExternalProcessResult> RunAsync(
            ProcessStartInfo  startInfo,
            CancellationToken cancellationToken = default)
        {
            return _handler(startInfo, cancellationToken);
        }

        public Task<ExternalProcessResult> RunAsync(
            ProcessStartInfo  startInfo,
            Action<string>?   onStandardOutput,
            CancellationToken cancellationToken = default)
        {
            return _handler(startInfo, cancellationToken);
        }
    }
}
