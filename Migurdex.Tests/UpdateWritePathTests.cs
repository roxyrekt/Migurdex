using Migurdex.Cli.Services;
using System.IO.MemoryMappedFiles;
using Xunit;

namespace Migurdex.Tests;

public sealed class UpdateWritePathTests : IDisposable
{
    private readonly string _root;

    public UpdateWritePathTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"migurdex-write-{Guid.NewGuid():N}");
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
    public void CopyReplacing_DoesNotTruncateMappedTarget()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var dest = Path.Combine(_root, "libcoreclr.so");
        var src  = Path.Combine(_root, "new-libcoreclr.so");

        File.WriteAllBytes(dest, Enumerable.Repeat((byte)0x41, 64 * 1024).ToArray());
        File.WriteAllBytes(src,  Enumerable.Repeat((byte)0x42, 64 * 1024).ToArray());

        using var mm   = MemoryMappedFile.CreateFromFile(dest, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var view = mm.CreateViewAccessor(0, 64 * 1024, MemoryMappedFileAccess.Read);

        Assert.Equal(0x41, view.ReadByte(0));

        UpdateCommand.CopyReplacing(src, dest);

        Assert.Equal(0x41, view.ReadByte(0));

        Assert.Equal(0x42, File.ReadAllBytes(dest)[0]);
        Assert.Equal(64 * 1024, new FileInfo(dest).Length);

        Assert.False(File.Exists(dest + ".tmp"));
    }

    [Fact]
    public void CopyReplacing_ReplacesContentAndLeavesNoTemp()
    {
        var dest = Path.Combine(_root, "a.dll");
        var src  = Path.Combine(_root, "b.dll");

        File.WriteAllText(dest, "old");
        File.WriteAllText(src,  "new-content");

        UpdateCommand.CopyReplacing(src, dest);

        Assert.Equal("new-content", File.ReadAllText(dest));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public void CopyReplacing_OverwritesShorterContent()
    {
        var dest = Path.Combine(_root, "a.dll");
        var src  = Path.Combine(_root, "b.dll");

        File.WriteAllText(dest, new string('x', 4096));
        File.WriteAllText(src,  "short");

        UpdateCommand.CopyReplacing(src, dest);

        Assert.Equal(5, new FileInfo(dest).Length);
    }
}
