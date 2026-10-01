using Migurdex.Cli.Services.Downloads;

namespace Migurdex.Cli.Tui;

/// <summary>
/// İndirme ilerlemesini izleyen, iş parçacığı güvenli (<c>IProgress</c>) sayaç.
/// </summary>
/// <remarks>
/// <para>
/// ⭐ <b>Neden ayrı dosya?</b> Bu sınıf <c>EpisodeSourcesView</c> içinde gömülüydü
/// ve <c>private</c> idi; bu yüzden toplu indirme ekranı bayt/hız/ETA bilgisini
/// <b>kullanamıyordu</b> (<c>BulkDownloadView</c> yalnız <c>Status</c> yazıyordu).
/// Sınıf buraya taşındı ve <c>internal</c> yapıldı; <c>EpisodeSourcesView</c>
/// hâlâ aynı örneği kullanır, davranış değişmedi.
/// </para>
/// <para>
/// Aynı sebeplerle <c>FormatBytes</c> ve <c>DownloadProgressLine</c> de buraya
/// alındı. Depoda <c>FormatBytes</c> üç ayrı yerde kopyalanmıştı
/// (<c>EpisodeSourcesView:723</c>, <c>DownloadCommand:785</c> ve buraya
/// taşınan kopya) — bu, birinin düzeltmesinin diğerlerine ulaşmaması demektir.
/// </para>
/// <para>
/// <b>Kilit</b> yalnız okuma/yazma için; hesaplama kilit dışında yapılır, böylece
/// indirme iş parçacığı bekletilmez.
/// </para>
/// </remarks>
internal sealed class DownloadProgressTracker : IProgress<DownloadProgress>
{
    private readonly Lock                      _sync     = new();
    private readonly DownloadSpeedometer       _speed    = new();
    private          DownloadProgress?         _current;

    /// <summary>Son raporlanan ilerleme kaydı.</summary>
    public DownloadProgress? Current
    {
        get
        {
            lock (_sync)
            {
                return _current;
            }
        }
    }

    /// <summary>Bayt/saniye cinsinden anlık hız.</summary>
    public double CurrentSpeed
    {
        get
        {
            lock (_sync)
            {
                return _current?.SpeedBytesPerSecond ?? _speed.BytesPerSecond;
            }
        }
    }

    /// <summary>Kalan süre tahmini; bilinmiyorsa <see langword="null"/>.</summary>
    public TimeSpan? CurrentEta
    {
        get
        {
            DownloadProgress? snapshot;
            lock (_sync)
            {
                snapshot = _current;
            }

            if (snapshot is null)
            {
                return null;
            }

            if (snapshot.Eta is not null)
            {
                return snapshot.Eta;
            }

            return _speed.EstimateRemaining(snapshot.BytesDownloaded,
                                            snapshot.TotalBytes,
                                            snapshot.SpeedBytesPerSecond);
        }
    }

    /// <inheritdoc/>
    public void Report(DownloadProgress value)
    {
        lock (_sync)
        {
            _speed.Sample(value.BytesDownloaded, DateTimeOffset.UtcNow);
            _current = value;
        }
    }
}

/// <summary>Bayt sayısını okunabilir birim metnine çevirir.</summary>
/// <remarks>
/// Ölçülen ayrım: <c>1000</c> tabanlı (KB/MB/GB) kullanılır; indirme hızı için
/// ikili taban daha doğru olsa da <b>mevcut ekranlarla aynı biçim</b> şart —
/// aksi hâlde tekli ve toplu indirme farklı sayılar gösterirdi.
/// </remarks>
internal static class DownloadProgressFormatter
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    /// <summary>Bayt sayısını <c>58.2 MB</c> biçimine çevirir.</summary>
    /// <remarks>
    /// ⭐ İki overload var: <see cref="long"/> (bayt) ve <see cref="double"/>
    /// (hız). Tek parametreli olsaydı <c>TotalBytes</c> (<c>long?</c>) ve
    /// <c>CurrentSpeed</c> (<c>double</c>) çağrıları derlenmezdi.
    /// </remarks>
    public static string FormatBytes(long bytes)
        => Format((double)Math.Max(0, bytes));

    /// <inheritdoc cref="FormatBytes(long)"/>
    public static string FormatBytes(double bytes)
        => Format(bytes);

    private static string Format(double bytes)
    {
        if (bytes < 0 || double.IsNaN(bytes) || double.IsInfinity(bytes))
        {
            return "-";
        }

        var unit = 0;
        while (bytes >= 1024 && unit < Units.Length - 1)
        {
            bytes /= 1024;
            unit++;
        }

        return $"{bytes:0.#} {Units[unit]}";
    }

    /// <summary>
    /// Tek satırlık ilerleme özeti:
    /// <c>58.2 MB / 120.4 MB • 3.4 MB/s • ETA 00:18</c>
    /// </summary>
    /// <remarks>
    /// ⭐ <b>Toplam bilinmiyorsa yalnız indirilen bayt yazılır.</b> Boyut yokken
    /// <c>0 B / -</c> gibi sahte bir gösterim yapmak, kullanıcının indirme
    /// takıldı sanmasına yol açar.
    /// </remarks>
    public static string Describe(DownloadProgressTracker tracker)
    {
        ArgumentNullException.ThrowIfNull(tracker);

        var current = tracker.Current;
        if (current is null)
        {
            return "Hazırlanıyor...";
        }

        var parts = new List<string>(4);

        if (current.TotalBytes > 0)
        {
            parts.Add($"{FormatBytes(current.BytesDownloaded)} / {FormatBytes(current.TotalBytes!.Value)}");
        }
        else if (current.BytesDownloaded > 0)
        {
            parts.Add(FormatBytes(current.BytesDownloaded));
        }

        var speed = tracker.CurrentSpeed;
        if (speed > 0)
        {
            parts.Add($"{FormatBytes(speed)}/s");
        }

        var eta = tracker.CurrentEta;
        if (eta is { } remaining && remaining > TimeSpan.Zero)
        {
            parts.Add(DownloadSpeedometer.FormatEta(remaining));
        }

        return parts.Count == 0 ? "Hazırlanıyor..." : string.Join(" • ", parts);
    }
}