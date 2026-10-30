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
        File.WriteAllText(Path.Combine(_root, "migurdex"), "x");

        InstallLayout.EnsureRootLauncher(_root);

        Assert.Null(new FileInfo(Path.Combine(_root, "migurdex")).LinkTarget);
    }
}
