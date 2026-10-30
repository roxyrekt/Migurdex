using Migurdex.Cli.Dependencies;
using System.IO.Compression;
using Xunit;

namespace Migurdex.Tests;

public sealed class DependencyArchiveTests
{
    [Fact]
    public void ExtractToDirectory_ExtractsNestedFiles()
    {
        var root = NewTempDir();
        try
        {
            var archivePath = Path.Combine(root, "sample.zip");
            CreateZip(archivePath, ("bin/tool.exe", "payload"), ("readme.txt", "hello"));

            var destination = Path.Combine(root, "out");
            DependencyArchive.ExtractToDirectory(archivePath,
                                                 destination,
                                                 cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal("payload", File.ReadAllText(Path.Combine(destination, "bin", "tool.exe")));
            Assert.Equal("hello", File.ReadAllText(Path.Combine(destination, "readme.txt")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ExtractToDirectory_RejectsZipSlipEntries()
    {
        var root = NewTempDir();
        try
        {
            var archivePath = Path.Combine(root, "slip.zip");
            CreateZip(archivePath, ("../escaped.txt", "nope"), ("ok.txt", "yes"));

            var destination = Path.Combine(root, "out");
            DependencyArchive.ExtractToDirectory(archivePath,
                                                 destination,
                                                 cancellationToken: TestContext.Current.CancellationToken);

            Assert.False(File.Exists(Path.Combine(root, "escaped.txt")));
            Assert.False(File.Exists(Path.Combine(destination, "..", "escaped.txt")));
            Assert.True(File.Exists(Path.Combine(destination, "ok.txt")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void TryExtractFile_MatchesByFileNameRegardlessOfDirectory()
    {
        var root = NewTempDir();
        try
        {
            var archivePath = Path.Combine(root, "ffmpeg.zip");
            CreateZip(archivePath,
                      ("ffmpeg-9.0.2-essentials_build/bin/ffmpeg.exe", "binary"),
                      ("ffmpeg-9.0.2-essentials_build/bin/ffprobe.exe", "probe"));

            var destination = Path.Combine(root, "ffmpeg.exe");
            Assert.True(DependencyArchive.TryExtractFile(archivePath,
                                                         "ffmpeg.exe",
                                                         destination,
                                                         cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal("binary", File.ReadAllText(destination));

            Assert.True(DependencyArchive.ContainsFile(archivePath, "FFPROBE.EXE"));
            Assert.False(DependencyArchive.ContainsFile(archivePath, "ffplay.exe"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ExtractToDirectory_HandlesRealSevenZipArchive()
    {
        var root = NewTempDir();
        try
        {
            var archivePath = Path.Combine(AppContext.BaseDirectory, "sevenzip-fixture.7z");
            Assert.True(File.Exists(archivePath), $"fixture bulunamadı: {archivePath}");

            var destination = Path.Combine(root, "out");
            DependencyArchive.ExtractToDirectory(archivePath,
                                                 destination,
                                                 cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal("fake-exe-binary", File.ReadAllText(Path.Combine(destination, "bin", "tool.exe")));
            Assert.Equal("readme-content", File.ReadAllText(Path.Combine(destination, "readme.txt")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void TryExtractFile_ReadsFromRealSevenZipArchive()
    {
        var root = NewTempDir();
        try
        {
            var archivePath = Path.Combine(AppContext.BaseDirectory, "sevenzip-fixture.7z");
            Assert.True(File.Exists(archivePath), $"fixture bulunamadı: {archivePath}");

            var destination = Path.Combine(root, "tool.exe");
            Assert.True(DependencyArchive.TryExtractFile(archivePath,
                                                         "tool.exe",
                                                         destination,
                                                         cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal("fake-exe-binary", File.ReadAllText(destination));

            Assert.True(DependencyArchive.ContainsFile(archivePath, "TOOL.EXE"));
            Assert.False(DependencyArchive.ContainsFile(archivePath, "missing.exe"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void TryExtractFile_ReturnsFalseWhenEntryMissing()
    {
        var root = NewTempDir();
        try
        {
            var archivePath = Path.Combine(root, "empty.zip");
            CreateZip(archivePath, ("readme.txt", "hello"));

            Assert.False(DependencyArchive.TryExtractFile(archivePath,
                                                          "ffmpeg.exe",
                                                          Path.Combine(root, "ffmpeg.exe"),
                                                          cancellationToken: TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void CreateZip(string path, params (string Name, string Content)[] entries)
    {
        using var stream  = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        foreach (var (name, content) in entries)
        {
            var       entry  = archive.CreateEntry(name);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }
    }

    private static string NewTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "migurdex-archive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);

        return path;
    }
}

public sealed class DependencyArchiveProgressTests
{
    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _report;

        public SyncProgress(Action<T> report)
        {
            _report = report;
        }

        public void Report(T value)
        {
            _report(value);
        }
    }

    [Fact]
    public void ExtractToDirectory_ReportsProgressAndCompletes()
    {
        var root = NewTempDir();
        try
        {
            var archivePath = Path.Combine(root, "sample.zip");
            CreateZip(archivePath, ("bin/tool.exe", new string('x', 4096)), ("readme.txt", "hello"));

            var reports = new List<string>();
            var progress = new SyncProgress<string>(reports.Add);

            DependencyArchive.ExtractToDirectory(archivePath,
                                                 Path.Combine(root, "out"),
                                                 progress,
                                                 TestContext.Current.CancellationToken);

            Assert.NotEmpty(reports);
            Assert.Contains(reports, r => r.Contains('%', StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ExtractToDirectory_ThrowsWhenCancelled()
    {
        var root = NewTempDir();
        try
        {
            var archivePath = Path.Combine(root, "sample.zip");
            CreateZip(archivePath, ("a.bin", new string('a', 1024)), ("b.bin", new string('b', 1024)));

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.Throws<OperationCanceledException>(() =>
                                                          DependencyArchive.ExtractToDirectory(archivePath,
                                                              Path.Combine(root, "out"),
                                                              cancellationToken: cts.Token));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void TryExtractFile_ThrowsWhenCancelled()
    {
        var root = NewTempDir();
        try
        {
            var archivePath = Path.Combine(root, "sample.zip");
            CreateZip(archivePath, ("tool.exe", "payload"));

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.Throws<OperationCanceledException>(() =>
                                                          DependencyArchive.TryExtractFile(archivePath,
                                                              "tool.exe",
                                                              Path.Combine(root, "tool.exe"),
                                                              cancellationToken: cts.Token));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void CreateZip(string path, params (string Name, string Content)[] entries)
    {
        using var stream  = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        foreach (var (name, content) in entries)
        {
            var       entry  = archive.CreateEntry(name);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }
    }

    private static string NewTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "migurdex-aprog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);

        return path;
    }
}
