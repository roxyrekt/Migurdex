namespace Migurdex.Cli.Services.Downloads;

/// <summary>
/// Parça (range) bağlantıları için toplam bütçe.
///
/// Neden gerekli: bir mp4 dosyası 8 MiB üzerindeyse <see cref="Mp4Downloader"/> onu en çok
/// 4 parçaya böler. Toplu indirmede 8 dosya paralel çalıştığında bu 8 × 4 = 32 eşzamanlı
/// bağlantı demektir; sunucular bunu genelde bağlantı başına kısıtlar, bu yüzden toplam hız
/// düşer ve sona kalan dosyalar KB/s seviyesine iner. Burada dosya başına parça sayısı,
/// eşzamanlı dosya sayısına bölünür; toplam bağlantı <see cref="TotalBudget"/>'i aşmaz.
///
/// Tek indirme akışı bütçeyi hiç değiştirmez: varsayılan 4 parça aynen kalır.
/// </summary>
internal static class DownloadConnectionBudget
{
    /// <summary>Toplu indirmede kabul edilen en fazla eşzamanlı parça bağlantısı.</summary>
    public const int TotalBudget = 8;

    /// <summary>Tek dosya için üst sınır (<see cref="Mp4Downloader"/> ile aynı).</summary>
    public const int DefaultSegmentsPerFile = 4;

    private static int _segmentsPerFile = DefaultSegmentsPerFile;

    /// <summary>Şu an dosya başına izin verilen parça sayısı.</summary>
    public static int SegmentsPerFile => _segmentsPerFile;

    /// <summary>
    /// Verilen eşzamanlılık için dosya başına parça sayısı. Ekran metni de bunu kullanır;
    /// böylece çizilen sayı ile gerçekten açılan bağlantı sayısı aynı olur.
    /// </summary>
    public static int SegmentsFor(int parallelFiles)
    {
        var files = Math.Max(1, parallelFiles);
        return Math.Clamp(TotalBudget / files, 1, DefaultSegmentsPerFile);
    }

    /// <summary>Kuyruk başlarken çağrılır; bütçeyi eşzamanlı dosya sayısına böler.</summary>
    public static void Apply(int parallelFiles)
    {
        _segmentsPerFile = SegmentsFor(parallelFiles);
    }

    /// <summary>Kuyruk bittiğinde varsayılana döner.</summary>
    public static void Reset()
    {
        _segmentsPerFile = DefaultSegmentsPerFile;
    }
}
