using Migurdex.Cli.Services.Downloads;
using Migurdex.Cli.Tui.Views;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Models;
using Xunit;

namespace Migurdex.Tests;

/// <summary>
/// Zaten indirilmiş bölümün <b>atlanmasını</b> ölçer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden bu test var:</b> kullanıcı 8 bölüm seçti, 7 indirildi, 1'i şu
/// hata ile başarısız göründü: <c>Video hedefi zaten var; overwrite kapalı.</c>
/// Oysa dosya diskte duruyordu — indirme <b>başarısız</b> değil, <b>gerekmiyordu</b>.
/// </para>
/// <para>
/// <b>Ölçülen tuzak:</b> <c>DownloadPath.GetMediaPath(null)</c> uzantı
/// bilinmediğinde dosya adını <b>uzantısız</b> üretir
/// (<c>DownloadModels.cs:233</c>). Bu yüzden <c>MediaPath</c>'e bakmak HLS
/// bölümlerini <b>kaçırır</b>. Test her iki yolu da doğruluyor.
/// </para>
/// </remarks>
public sealed class BulkDownloadSkipTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
                                                "migurdex-skip-" + Guid.NewGuid().ToString("N"));

    public BulkDownloadSkipTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Geçici dizin temizlenemezse testi düşürme.
        }
    }

    private DownloadPath Mp4Path(string animeDir, string stem) =>
        new(_root, animeDir, stem, ".mp4");

    [Fact]
    public void ExistingMp4_IsDetected()
    {
        var dir = Path.Combine(_root, "Anime");
        Directory.CreateDirectory(dir);
        var stem = "S01E02 - Test";
        File.WriteAllText(Path.Combine(dir, stem + ".mp4"), "x");

        var bulunan = DownloadPresence.FindExistingVideo(Mp4Path(dir, stem));

        Assert.NotNull(bulunan);
        Assert.EndsWith(".mp4", bulunan, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingFile_ReturnsNull()
    {
        var dir = Path.Combine(_root, "Anime");
        Directory.CreateDirectory(dir);

        Assert.Null(DownloadPresence.FindExistingVideo(Mp4Path(dir, "S01E09 - Yok")));
    }

    [Fact]
    public void HlsStemWithoutExtension_FindsOtherContainer()
    {
        // ⭐ HLS: uzantı bilinmiyor, MediaPath uzantisiz.
        var dir = Path.Combine(_root, "Anime");
        Directory.CreateDirectory(dir);
        var stem = "S01E05 - Test";

        var path = new DownloadPath(_root, dir, stem, null);
        Assert.Equal(stem, Path.GetFileName(path.MediaPath));

        File.WriteAllText(Path.Combine(dir, stem + ".mkv"), "x");

        var bulunan = DownloadPresence.FindExistingVideo(path);

        Assert.NotNull(bulunan);
        Assert.EndsWith(".mkv", bulunan, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PartialFile_IsNotTreatedAsDownloaded()
    {
        // Yarım kalmis indirme "zaten var" sayilmamali; resume kullanilabilir.
        var dir = Path.Combine(_root, "Anime");
        Directory.CreateDirectory(dir);
        var stem = "S01E06 - Test";
        File.WriteAllText(Path.Combine(dir, stem + ".mp4.part"), "x");

        var path = new DownloadPath(_root, dir, stem, null);

        Assert.Null(DownloadPresence.FindExistingVideo(path));
    }

    [Fact]
    public void SubtitleOnly_IsNotTreatedAsDownloaded()
    {
        var dir = Path.Combine(_root, "Anime");
        Directory.CreateDirectory(dir);
        var stem = "S01E07 - Test";
        File.WriteAllText(Path.Combine(dir, stem + ".tr.vtt"), "x");

        var path = new DownloadPath(_root, dir, stem, null);

        Assert.Null(DownloadPresence.FindExistingVideo(path));
    }

    [Fact]
    public void StemContainingDot_IsNotTruncated()
    {
        // ⭐ Olcum: Onceki taslak GetFileNameWithoutExtension kullaniyordu.
        // Bolum adi "Bölüm 1.5" oldugunda stem kirpilir ve eslesme bulunamaz.
        var dir = Path.Combine(_root, "Anime");
        Directory.CreateDirectory(dir);
        var stem = "S01E08 - Bölüm 1.5";
        File.WriteAllText(Path.Combine(dir, stem + ".mp4"), "x");

        var path = new DownloadPath(_root, dir, stem, null);
        var bulunan = DownloadPresence.FindExistingVideo(path);

        Assert.NotNull(bulunan);
        Assert.Equal(stem + ".mp4", Path.GetFileName(bulunan));
    }

    [Fact]
    public void MissingDirectory_ReturnsNull_NotException()
    {
        var dir = Path.Combine(_root, "YokBoyleBirDizin");
        var path = new DownloadPath(_root, dir, "S01E10", ".mp4");

        Assert.Null(DownloadPresence.FindExistingVideo(path));
    }

    [Fact]
    public void LoadExistingStems_FindsOnlyVideoContainers()
    {
        var dir = Path.Combine(_root, "Anime");
        Directory.CreateDirectory(dir);

        foreach (var ad in new[]
                 {
                     "S01E01 - Bir", "S01E02 - Iki.mp4", "S01E03 - Uc.mkv",
                     "S01E04 - Dort.en.vtt", "S01E05 - Bes.mp4.part"
                 })
        {
            File.WriteAllText(Path.Combine(dir, ad), "x");
        }

        var stemler = DownloadPresence.LoadExistingStems(dir);

        // UZANTISIZ dosya sayilmaz: gercek bir HLS ciktisi her zaman bir
        // kaplayiciya sahiptir; uzantisiz dosya yarim kalmis bir is kalintisidir.
        // Ilk yazimda 3 bekleniyordu ve test dustu - KOD dogruydu.
        Assert.Equal(2, stemler.Count);
        Assert.Contains("S01E02 - Iki", stemler);
        Assert.Contains("S01E03 - Uc", stemler);
        Assert.DoesNotContain("S01E01 - Bir", stemler);
        Assert.DoesNotContain("S01E04 - Dort.en", stemler);
        Assert.DoesNotContain("S01E05 - Bes", stemler);
    }

    [Fact]
    public void LoadExistingStems_MissingDirectory_ReturnsEmpty()
    {
        var stemler = DownloadPresence.LoadExistingStems(Path.Combine(_root, "YokBoyle"));
        Assert.Empty(stemler);
    }

    [Fact]
    public void LoadExistingStems_NullOrBlank_ReturnsEmpty()
    {
        Assert.Empty(DownloadPresence.LoadExistingStems(null));
        Assert.Empty(DownloadPresence.LoadExistingStems("   "));
    }
}
