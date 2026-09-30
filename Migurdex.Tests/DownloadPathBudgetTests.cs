using Migurdex.Cli.Services.Downloads;
using System.Text;
using Xunit;

namespace Migurdex.Tests;

/// <summary>
/// C5: <c>DownloadPathBuilder</c> ayraç bütçesini iki kez düşüyordu.
///
/// Tam hedef yolu İKİ ayraç içeriyor (root → anime dizini → dosya adı) ama
/// <c>availableBytes</c> hesabında <c>separatorBytes</c> yalnızca BİR kez
/// düşülüyordu:
/// <code>
/// tam yol = root + ayraç + anime + ayraç + stem + uzantı
///        = root + uzantı + 2*ayraç + availableBytes
///        = Max + ayraç - TemporarySuffix
/// </code>
/// Yani bütçe tam olarak bir ayraç (1 UTF-8 baytı) eksik hesaplanıyor ve tam
/// dolu bir yolda <c>EnsureFullPathBudget</c> yanlışlıkla patlıyordu
/// ("Tam çıktı yolu güvenli dosya adı bütçesini aşıyor").
///
/// Düzeltme: ikinci <c>separatorBytes</c> de bütçeden düşülüyor; bütçe artık
/// tam olarak tükeniyor ve <c>EnsureFullPathBudget</c> tutarlı hale geliyor.
/// </summary>
public sealed class DownloadPathBudgetTests
{
    [Fact]
    public void Build_UsesTheWholeBudgetWithoutTrippingTheFinalCheck()
    {
        // Tam bütçe senaryosu: anime ve bölüm adları bütçeyi tamamen dolduracak
        // kadar uzun olmalı ki kesinlik aracı devreye girsin.
        var root = RootOfExactByteLength(90);
        try
        {
            var path = new DownloadPathBuilder().Build(root, LongAscii(400), LongAscii(400), 1, 1, ".mp4");

            // Patlamadı: bütçe artık yeterli.
            Assert.NotNull(path);
        }
        finally
        {
            SafeDelete(root);
        }
    }

    [Fact]
    public void Build_NeverExceedsTheDocumentedFullPathBudget()
    {
        // Her büyüklük kombinasyonunda sonuç bütçe içinde kalmalı.
        foreach (var animeLength in new[] { 1, 20, 60, 200, 400 })
        {
            foreach (var episodeLength in new[] { 0, 1, 20, 60, 200, 400 })
            {
                var root = RootOfExactByteLength(90);
                try
                {
                    var path = new DownloadPathBuilder().Build(root,
                                                                LongAscii(animeLength),
                                                                LongAscii(episodeLength),
                                                                1,
                                                                1,
                                                                ".mp4");
                    var bytes = Encoding.UTF8.GetByteCount(Path.GetFullPath(path.MediaPath));
                    Assert.True(bytes <= DownloadPathBuilder.MaxFullPathUtf8Bytes
                                         - DownloadPathBuilder.TemporarySuffixUtf8Bytes,
                                $"anime={animeLength} episode={episodeLength} -> {bytes} bayt");
                }
                finally
                {
                    SafeDelete(root);
                }
            }
        }
    }

    [Fact]
    public void Build_RejectsRootThatLeavesNoRoomForTheTwoSeparators()
    {
        // availableBytes = Max - root - 2*ayraç - uzantı - TemporarySuffix
        // 48 baytın altına düşünce reddedilmeli.
        var separatorBytes = Encoding.UTF8.GetByteCount(Path.DirectorySeparatorChar.ToString());
        var extensionBytes = Encoding.UTF8.GetByteCount(".mp4");
        var rootBytes = DownloadPathBuilder.MaxFullPathUtf8Bytes
                        - 2 * separatorBytes
                        - extensionBytes
                        - DownloadPathBuilder.TemporarySuffixUtf8Bytes
                        - 48;
        var root = RootOfExactByteLength(rootBytes + 1);
        try
        {
            Assert.Throws<ArgumentException>(() => new DownloadPathBuilder().Build(root,
                                                                                   "Anime",
                                                                                   "Episode",
                                                                                   1,
                                                                                   1,
                                                                                   ".mp4"));
        }
        finally
        {
            SafeDelete(root);
        }
    }

    [Fact]
    public void Build_AcceptsTheExactMinimumRootLength()
    {
        // Bütçe+1 reddediliyor, tam bütçe kabul ediliyor: sınır tam burada.
        var separatorBytes = Encoding.UTF8.GetByteCount(Path.DirectorySeparatorChar.ToString());
        var extensionBytes = Encoding.UTF8.GetByteCount(".mp4");
        var rootBytes = DownloadPathBuilder.MaxFullPathUtf8Bytes
                        - 2 * separatorBytes
                        - extensionBytes
                        - DownloadPathBuilder.TemporarySuffixUtf8Bytes
                        - 48;
        var root = RootOfExactByteLength(rootBytes);
        try
        {
            var path = new DownloadPathBuilder().Build(root,
                                                       LongAscii(200),
                                                       LongAscii(200),
                                                       1,
                                                       1,
                                                       ".mp4");
            Assert.NotNull(path);
            Assert.StartsWith(Path.GetFullPath(root), path.MediaPath, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            SafeDelete(root);
        }
    }

    [Fact]
    public void Build_ExtensionIsAlsoChargedAgainstTheBudget()
    {
        // Bütçe tam dolu bir kökte 1 bayt daha uzun uzantı reddedilmeli:
        // uzantı da bütçeye dahildir.
        var rootBytes = DownloadPathBuilder.MaxFullPathUtf8Bytes
                        - 2 * Encoding.UTF8.GetByteCount(Path.DirectorySeparatorChar.ToString())
                        - Encoding.UTF8.GetByteCount(".mp4")
                        - DownloadPathBuilder.TemporarySuffixUtf8Bytes
                        - 48;
        var root = RootOfExactByteLength(rootBytes);
        try
        {
            // Tam sınırda ".mp4" (4 bayt) kabul edilir.
            var atLimit = new DownloadPathBuilder().Build(root, "Anime", "Episode", 1, 1, ".mp4");
            Assert.NotNull(atLimit);

            // ".webm" 5 bayt: 1 bayt fazla → bütçe artık yetersiz.
            Assert.Throws<ArgumentException>(() => new DownloadPathBuilder().Build(root,
                                                                                   "Anime",
                                                                                   "Episode",
                                                                                   1,
                                                                                   1,
                                                                                   ".webm"));
        }
        finally
        {
            SafeDelete(root);
        }
    }

    [Fact]
    public void Build_VeryLongAnimeNameIsBoundedAndPathStaysValid()
    {
        // Çok uzun sağlayıcı (anime) adı: dizin adı sınırı aşmamalı, yol bütçede kalmalı.
        var root = RootOfExactByteLength(90);
        try
        {
            var path = new DownloadPathBuilder().Build(root, new string('A', 5000), "Episode", 1, 1, ".mp4");
            Assert.True(Encoding.UTF8.GetByteCount(Path.GetFileName(path.AnimeDirectory))
                        <= DownloadPathBuilder.MaxComponentUtf8Bytes);
            Assert.True(Encoding.UTF8.GetByteCount(Path.GetFullPath(path.MediaPath))
                        <= DownloadPathBuilder.MaxFullPathUtf8Bytes
                           - DownloadPathBuilder.TemporarySuffixUtf8Bytes);
        }
        finally
        {
            SafeDelete(root);
        }
    }

    [Fact]
    public void Build_VeryLongEpisodeNameFillsTheBudgetExactly()
    {
        // Çok uzun bölüm adı: stem fileBudget'a tam oturur ve yol TAM bütçeye
        // oturmalı (1 bayt taşmamalı). Bu, "tam bütçe" sınır durumudur.
        var root = RootOfExactByteLength(90);
        try
        {
            var path = new DownloadPathBuilder().Build(root, "Anime", new string('B', 5000), 1, 1, ".mp4");
            var bytes = Encoding.UTF8.GetByteCount(Path.GetFullPath(path.MediaPath));
            Assert.True(bytes <= DownloadPathBuilder.MaxFullPathUtf8Bytes
                               - DownloadPathBuilder.TemporarySuffixUtf8Bytes,
                        $"{bytes} bayt bütçeyi aşıyor");
        }
        finally
        {
            SafeDelete(root);
        }
    }

    [Fact]
    public void Build_MultiByteTitlesNeverSplitACodePoint()
    {
        var root = RootOfExactByteLength(90);
        try
        {
            var title = string.Concat(Enumerable.Repeat("\U0001F600", 400));
            var path = new DownloadPathBuilder().Build(root, title, title, 1, 1, ".mp4");
            Assert.DoesNotContain("\uFFFD", path.MediaPath, StringComparison.Ordinal);
            Assert.True(Encoding.UTF8.GetByteCount(Path.GetFullPath(path.MediaPath))
                        <= DownloadPathBuilder.MaxFullPathUtf8Bytes
                           - DownloadPathBuilder.TemporarySuffixUtf8Bytes);
        }
        finally
        {
            SafeDelete(root);
        }
    }

    [Fact]
    public void BuildAnimeDirectory_AlsoStaysWithinBudget()
    {
        var root = RootOfExactByteLength(90);
        try
        {
            var directory = new DownloadPathBuilder().BuildAnimeDirectory(root, new string('A', 5000));
            Assert.True(Encoding.UTF8.GetByteCount(Path.GetFullPath(directory))
                        <= DownloadPathBuilder.MaxFullPathUtf8Bytes
                           - DownloadPathBuilder.TemporarySuffixUtf8Bytes);
        }
        finally
        {
            SafeDelete(root);
        }
    }

    /// <summary>
    /// Kök yolun tam olarak <paramref name="byteLength"/> UTF-8 bayt olmasını
    /// sağlar. Dizin oluşturulmaz; sadece uzunluk ölçülür.
    /// </summary>
    private static string RootOfExactByteLength(int byteLength)
    {
        var prefix = Path.Combine(Path.GetTempPath(), "migurdex-budget-" + Guid.NewGuid().ToString("N"));
        var baseBytes = Encoding.UTF8.GetByteCount(Path.GetFullPath(prefix));
        var separatorBytes = Encoding.UTF8.GetByteCount(Path.DirectorySeparatorChar.ToString());
        // Path.Combine eklenen ayraç da bayt harcar.
        var fillerLength = byteLength - baseBytes - separatorBytes;
        if (fillerLength < 1)
        {
            throw new InvalidOperationException(
                $"Test kökü en az {baseBytes + separatorBytes + 1} bayt olmalı, istenen {byteLength}.");
        }

        var candidate = Path.Combine(prefix, new string('r', fillerLength));
        var actual = Encoding.UTF8.GetByteCount(Path.GetFullPath(candidate));
        if (actual != byteLength)
        {
            throw new InvalidOperationException($"Kök {actual} bayt, beklenen {byteLength}.");
        }

        return candidate;
    }

    private static string LongAscii(int length)
    {
        return length <= 0 ? string.Empty : new string('x', length);
    }

    private static void SafeDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch
        {
            // yol hiç oluşmadıysa sorun değil
        }
    }
}
