using Migurdex.Cli.Services.Downloads;
using System.Text;
using Xunit;

namespace Migurdex.Tests;

public sealed class DownloadPathBuilderTests
{
    [Fact]
    public void Build_KeepsTraversalAndReservedNamesInsideRoot()
    {
        var root = NewTempDir();
        try
        {
            var builder = new DownloadPathBuilder();
            var path = builder.Build(root,
                                     "../CON /...",
                                     "..\\NUL. ",
                                     2,
                                     3,
                                     ".mp4");

            Assert.Equal(Path.GetFullPath(root), path.RootDirectory);
            Assert.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar,
                              path.MediaPath,
                              StringComparison.OrdinalIgnoreCase);
            Assert.NotEqual(".", Path.GetFileName(path.AnimeDirectory));
            Assert.NotEqual("..", Path.GetFileName(path.AnimeDirectory));
            Assert.DoesNotContain("/", Path.GetFileName(path.AnimeDirectory), StringComparison.Ordinal);
            Assert.DoesNotContain("\\", Path.GetFileName(path.AnimeDirectory), StringComparison.Ordinal);
            Assert.NotEqual("CON", Path.GetFileName(path.AnimeDirectory).Split('.')[0]);
            Assert.EndsWith(".mp4", path.MediaPath, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("S02E03", path.FileStem, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Build_TruncatesLongUtf8ComponentsWithoutExceedingLimit()
    {
        var root = NewTempDir();
        try
        {
            var builder = new DownloadPathBuilder();
            var longTitle = string.Concat(Enumerable.Repeat("\U0001F600", 300));
            var path = builder.Build(root, longTitle, longTitle, 1, 1, ".mkv");

            Assert.True(Encoding.UTF8.GetByteCount(Path.GetFileName(path.AnimeDirectory))
                        <= DownloadPathBuilder.MaxComponentUtf8Bytes);
            Assert.True(Encoding.UTF8.GetByteCount(Path.GetFileNameWithoutExtension(path.MediaPath))
                        <= DownloadPathBuilder.MaxComponentUtf8Bytes);
            Assert.DoesNotContain("\uFFFD", path.MediaPath, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Build_RejectsRootThatCannotFitTemporarySuffixes()
    {
        var root = NewTempDir();
        try
        {
            var tooLongRoot = Path.Combine(root, new string('r', DownloadPathBuilder.MaxFullPathUtf8Bytes));
            Assert.Throws<ArgumentException>(() => new DownloadPathBuilder().Build(tooLongRoot,
                                                                                           "Anime",
                                                                                           "Episode",
                                                                                           1,
                                                                                           1,
                                                                                           ".mp4"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Build_DoesNotUseSourceUrlAsFileName()
    {
        var root = NewTempDir();
        try
        {
            var path = new DownloadPathBuilder().Build(root,
                                                        "Anime",
                                                        "Episode",
                                                        1,
                                                        1,
                                                        ".mp4");

            Assert.DoesNotContain("http", path.FileStem, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret-token", path.FileStem, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static string NewTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "migurdex-path-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
