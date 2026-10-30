using Migurdex.Cli.Services;
using Xunit;

namespace Migurdex.Tests;

public sealed class InstallLayoutTests : IDisposable
{
    private readonly string _root;

    public InstallLayoutTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"migurdex-layout-{Guid.NewGuid():N}");
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

    [Fact]
    public void ResolveCliDir_SplitLayout_ReturnsCliSubdirectory()
    {
        Directory.CreateDirectory(Path.Combine(_root, "cli"));
        Directory.CreateDirectory(Path.Combine(_root, "api"));

        Assert.Equal(Path.Combine(_root, "cli"), InstallLayout.ResolveCliDir(_root));
    }

    [Fact]
    public void ResolveCliDir_FlatLayout_ReturnsRoot()
    {
        Directory.CreateDirectory(Path.Combine(_root, "api"));

        Assert.Equal(_root, InstallLayout.ResolveCliDir(_root));
    }

    [Fact]
    public void ResolveApiDir_AlwaysApiSubdirectory()
    {
        Assert.Equal(Path.Combine(_root, "api"), InstallLayout.ResolveApiDir(_root));
    }

    [Fact]
    public void IsSplitLayout_TrueOnlyWhenBothDirectoriesExist()
    {
        Assert.False(InstallLayout.IsSplitLayout(_root));

        Directory.CreateDirectory(Path.Combine(_root, "api"));
        Assert.False(InstallLayout.IsSplitLayout(_root));

        Directory.CreateDirectory(Path.Combine(_root, "cli"));
        Assert.True(InstallLayout.IsSplitLayout(_root));
    }

    [Fact]
    public void CanShareRuntime_TrueForBothFlatAndSplit()
    {
        Assert.False(InstallLayout.CanShareRuntime(_root));

        Directory.CreateDirectory(Path.Combine(_root, "api"));
        Assert.True(InstallLayout.CanShareRuntime(_root));

        Directory.CreateDirectory(Path.Combine(_root, "cli"));
        Assert.True(InstallLayout.CanShareRuntime(_root));
    }

    [Fact]
    public void EnsureRootLauncher_FlatLayout_DoesNothing()
    {
        Directory.CreateDirectory(Path.Combine(_root, "api"));
        var launcher = Path.Combine(_root, "migurdex");
        File.WriteAllText(launcher, "mevcut-icerik");

        InstallLayout.EnsureRootLauncher(_root);

        Assert.Equal("mevcut-icerik", File.ReadAllText(launcher));
        Assert.Null(new FileInfo(launcher).LinkTarget);
    }

    [Fact]
    public void EnsureRootLauncher_SplitLayout_WritesScriptNotSymlink()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateDirectory(Path.Combine(_root, "cli"));
        Directory.CreateDirectory(Path.Combine(_root, "api"));
        File.WriteAllText(Path.Combine(_root, "cli", "migurdex"), "cli-binary");

        InstallLayout.EnsureRootLauncher(_root);

        var launcher = new FileInfo(Path.Combine(_root, "migurdex"));

        Assert.Null(launcher.LinkTarget);
        Assert.True(launcher.Exists);

        var content = File.ReadAllText(launcher.FullName);
        Assert.StartsWith("#!/bin/sh", content);
        Assert.Contains("cli/migurdex", content);

        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(launcher.FullName);
            Assert.True(mode.HasFlag(UnixFileMode.UserExecute));
            Assert.True(mode.HasFlag(UnixFileMode.GroupExecute));
            Assert.True(mode.HasFlag(UnixFileMode.OtherExecute));
        }
    }

    [Fact]
    public void EnsureRootLauncher_ReplacesOldSymlinkWithScript()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateDirectory(Path.Combine(_root, "cli"));
        Directory.CreateDirectory(Path.Combine(_root, "api"));
        File.WriteAllText(Path.Combine(_root, "cli", "migurdex"), "cli-binary");

        File.CreateSymbolicLink(Path.Combine(_root, "migurdex"), Path.Combine("cli", "migurdex"));
        Assert.NotNull(new FileInfo(Path.Combine(_root, "migurdex")).LinkTarget);

        InstallLayout.EnsureRootLauncher(_root);

        var launcher = new FileInfo(Path.Combine(_root, "migurdex"));
        Assert.Null(launcher.LinkTarget);
        Assert.StartsWith("#!/bin/sh", File.ReadAllText(launcher.FullName));
    }

    [Fact]
    public void EnsureRootLauncher_Idempotent()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateDirectory(Path.Combine(_root, "cli"));
        Directory.CreateDirectory(Path.Combine(_root, "api"));
        File.WriteAllText(Path.Combine(_root, "cli", "migurdex"), "cli-binary");

        InstallLayout.EnsureRootLauncher(_root);
        var first = File.ReadAllText(Path.Combine(_root, "migurdex"));

        InstallLayout.EnsureRootLauncher(_root);
        var second = File.ReadAllText(Path.Combine(_root, "migurdex"));

        Assert.Equal(first, second);
    }
}
