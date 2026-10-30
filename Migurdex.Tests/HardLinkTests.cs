using Migurdex.Cli.Services;
using System.Diagnostics;
using Xunit;

namespace Migurdex.Tests;

public sealed class HardLinkTests : IDisposable
{
    private readonly string _root;

    public HardLinkTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"migurdex-hl-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_root, "cli"));
        Directory.CreateDirectory(Path.Combine(_root, "api"));
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
    public void TryCreate_SameFileContent_ProducesSharedInode()
    {
        // paket zip olduğundan anlamı yok
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var target = Path.Combine(_root, "cli", "shared.dll");
        var link   = Path.Combine(_root, "api", "shared.dll");

        File.WriteAllBytes(target, [1, 2, 3, 4, 5]);

        Assert.True(HardLink.TryCreate(target, link));
        Assert.True(File.Exists(link));
        Assert.Equal(new FileInfo(target).Length, new FileInfo(link).Length);
    }

    [Fact]
    public void Run_IdenticalFiles_LinksThemAndReportsSavings()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var cliDir = Path.Combine(_root, "cli");
        var apiDir = Path.Combine(_root, "api");

        var payload = new byte[64 * 1024];
        Random.Shared.NextBytes(payload);

        File.WriteAllBytes(Path.Combine(cliDir, "same.dll"), payload);
        File.WriteAllBytes(Path.Combine(apiDir, "same.dll"), payload);

        File.WriteAllBytes(Path.Combine(cliDir, "diff.dll"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(apiDir, "diff.dll"), [9, 9, 9]);

        File.WriteAllBytes(Path.Combine(cliDir, "only-cli.dll"), [7, 7]);

        var (linked, saved) = HardLinkDeduplicator.Run(cliDir, apiDir);

        Assert.Equal(1, linked);
        Assert.Equal(payload.Length, saved);

        Assert.Equal(payload, File.ReadAllBytes(Path.Combine(cliDir, "same.dll")));
        Assert.Equal(payload, File.ReadAllBytes(Path.Combine(apiDir, "same.dll")));

        Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(cliDir, "diff.dll")));
        Assert.Equal([9, 9, 9], File.ReadAllBytes(Path.Combine(apiDir, "diff.dll")));

        Assert.True(File.Exists(Path.Combine(cliDir, "only-cli.dll")));
    }

    [Fact]
    public void Run_AfterCopyBreaksLinks_ReLinksThem()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var cliDir = Path.Combine(_root, "cli");
        var apiDir = Path.Combine(_root, "api");

        var cliFile = Path.Combine(cliDir, "runtime.dll");
        var apiFile = Path.Combine(apiDir, "runtime.dll");

        var payload = new byte[32 * 1024];
        Random.Shared.NextBytes(payload);

        File.WriteAllBytes(cliFile, payload);
        File.WriteAllBytes(apiFile, payload);
        Assert.True(HardLink.TryCreate(apiFile, cliFile));

        File.Copy(apiFile, cliFile + ".tmp", true);
        File.Move(cliFile + ".tmp", cliFile, true);

        var first = HardLinkDeduplicator.Run(cliDir, apiDir);
        Assert.Equal(1, first.Linked);
        Assert.Equal(payload.Length, first.SavedBytes);

        var second = HardLinkDeduplicator.Run(cliDir, apiDir);
        Assert.Equal(payload, File.ReadAllBytes(cliFile));
        Assert.Equal(payload, File.ReadAllBytes(apiFile));
        Assert.True(second.Linked is 0 or 1);

        // geçici .hltmp kalmamalı posix rename() aynı inode'a denk
        // geldiginde no-op olur, temizlenmezse her dedupe da birikir
        // ve link count 2 yerine 3 olur
        Assert.Empty(Directory.GetFiles(cliDir, "*.hltmp", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(apiDir, "*.hltmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void TryCreate_WhenAlreadyLinked_LeavesNoTempFile()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var target = Path.Combine(_root, "cli", "a.dll");
        var link   = Path.Combine(_root, "api", "a.dll");
        File.WriteAllBytes(target, [1, 2, 3, 4]);

        Assert.True(HardLink.TryCreate(target, link));
        Assert.True(HardLink.TryCreate(target, link));
        Assert.True(HardLink.TryCreate(target, link));

        Assert.Empty(Directory.GetFiles(_root, "*.hltmp", SearchOption.AllDirectories));

        if (!OperatingSystem.IsWindows())
        {
            var psi = new ProcessStartInfo("stat", $"-c %h \"{link}\"")
            {
                RedirectStandardOutput = true,
                UseShellExecute        = false
            };
            using var p     = Process.Start(psi)!;
            var       count = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            Assert.Equal("2", count);
        }
    }

    [Fact]
    public void Run_MissingDirectories_ReturnsZero()
    {
        var (linked, saved) = HardLinkDeduplicator.Run(Path.Combine(_root, "yok"),
                                                       Path.Combine(_root, "yok2"));

        Assert.Equal(0, linked);
        Assert.Equal(0, saved);
    }
}
