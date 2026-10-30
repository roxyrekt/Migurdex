using Migurdex.Cli.Services;
using Xunit;

namespace Migurdex.Tests;

public sealed class FileSwapTests : IDisposable
{
    private readonly string _root;

    public FileSwapTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"migurdex-fileswap-{Guid.NewGuid():N}");
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
    public void TryReplace_CreatesMissingTarget()
    {
        var src  = Path.Combine(_root, "a.dll.new");
        var dest = Path.Combine(_root, "a.dll");

        File.WriteAllText(src, "content");

        Assert.True(FileSwap.TryReplace(src, dest));
        Assert.False(File.Exists(src));
        Assert.Equal("content", File.ReadAllText(dest));
    }

    [Fact]
    public void TryReplace_OverwritesExistingTarget()
    {
        var src  = Path.Combine(_root, "a.dll.new");
        var dest = Path.Combine(_root, "a.dll");

        File.WriteAllText(src,  "new");
        File.WriteAllText(dest, "old");

        Assert.True(FileSwap.TryReplace(src, dest));
        Assert.Equal("new", File.ReadAllText(dest));
        Assert.False(File.Exists(src));
    }

    [Fact]
    public void TryReplace_MissingSourceFails()
    {
        Assert.False(FileSwap.TryReplace(Path.Combine(_root, "yok.new"), Path.Combine(_root, "yok")));
    }

    [Fact]
    public void RecoverLeftovers_AppliesStagedFiles()
    {
        var dest = Path.Combine(_root, "migurdex.dll");
        var staged = dest + ".new";

        File.WriteAllText(dest,   "old");
        File.WriteAllText(staged, "new");

        Assert.Equal(1, FileSwap.RecoverLeftovers(_root));
        Assert.Equal("new", File.ReadAllText(dest));
        Assert.False(File.Exists(staged));
    }

    [Fact]
    public void RecoverLeftovers_SkipsWhenTargetIsNewer()
    {
        var dest   = Path.Combine(_root, "migurdex.dll");
        var staged = dest + ".new";

        File.WriteAllText(staged, "stale");
        File.WriteAllText(dest,   "current");
        File.SetLastWriteTimeUtc(staged, DateTime.UtcNow.AddHours(-2));
        File.SetLastWriteTimeUtc(dest,   DateTime.UtcNow);

        Assert.Equal(0, FileSwap.RecoverLeftovers(_root));
        Assert.Equal("current", File.ReadAllText(dest));
        Assert.True(File.Exists(staged));
    }

    [Fact]
    public void RecoverLeftovers_ScansCliAndApiDirectories()
    {
        var cliDir = Path.Combine(_root, "cli");
        var apiDir = Path.Combine(_root, "api");
        Directory.CreateDirectory(cliDir);
        Directory.CreateDirectory(apiDir);

        var cliDest = Path.Combine(cliDir, "x.dll");
        var apiDest = Path.Combine(apiDir, "y.dll");

        File.WriteAllText(cliDest, "a");
        File.WriteAllText(apiDest, "b");
        File.WriteAllText(cliDest + ".new", "A");
        File.WriteAllText(apiDest + ".new", "B");

        Assert.Equal(2, FileSwap.RecoverLeftovers(_root));
        Assert.Equal("A", File.ReadAllText(cliDest));
        Assert.Equal("B", File.ReadAllText(apiDest));
    }

    [Fact]
    public void RemoveBackups_DeletesOnlyOrphans()
    {
        var live = Path.Combine(_root, "live.dll");
        File.WriteAllText(live, "x");
        File.WriteAllText(live + ".old", "x");

        var orphan = Path.Combine(_root, "gone.dll.old");
        File.WriteAllText(orphan, "x");

        var removed = FileSwap.RemoveBackups(_root);

        Assert.Equal(1, removed);
        Assert.False(File.Exists(live + ".old"));
        Assert.True(File.Exists(orphan));
    }

    [Fact]
    public void RemoveBackups_IgnoresUnrelatedExtensions()
    {
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "x");

        Assert.Equal(0, FileSwap.RemoveBackups(_root));
        Assert.True(File.Exists(Path.Combine(_root, "notes.txt")));
    }
}
