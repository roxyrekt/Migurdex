using Migurdex.Cli.Services.Downloads;
using Spectre.Console;

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

/// <summary>
/// İndirme ilerlemesini okunabilir metne ve Spectre görev satırına çevirir.
/// </summary>
/// <remarks>
/// <para>
/// Burada iki şey yaşar: bayt/hız metni (<see cref="FormatBytes(long)"/>) ve
/// <c>EpisodeSourcesView</c> ile paylaşılan görev satırı güncellemesi
/// (<see cref="UpdateDownloadTask"/>).
/// </para>
/// <para>
/// ⭐ Görev satırı güncellemesi <b>tekli akışta yazılmış, toplu akışta
/// kopyalanmamıştı</b>; iki ekran farklı gösteri üretiyordu. Toplu indirme
/// ekranı da aynı fonksiyonu kullanır, böylece "Tamamlanıyor" gibi aşama
/// metinleri her iki ekranda da görünür.
/// </para>
/// </remarks>
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


    internal static void UpdateDownloadTask(ProgressTask task, DownloadProgressTracker tracker)
    {
        var progress = tracker.Current;
        if (progress is null)
        {
            task.IsIndeterminate = true;
            task.Description      = "Hazırlanıyor • Esc: iptal";
            return;
        }

        var stage = DownloadStageLabel.For(progress.Stage, progress.Track, progress.IsAudioTrack);

        if (progress is { Stage: DownloadStage.Downloading })
        {
            var detail = stage;
            if (!string.IsNullOrWhiteSpace(progress.Track))
            {
                detail += $" • {Markup.Escape(progress.Track)}";
            }

            if (progress is { FragmentsTotal: > 0, FragmentsDone: not null })
            {
                detail += $" • frag {progress.FragmentsDone.Value}/{progress.FragmentsTotal.Value}";
            }
            else if (progress.TotalBytes is > 0)
            {
                detail += $" • {DownloadProgressFormatter.FormatBytes(progress.BytesDownloaded)} / {DownloadProgressFormatter.FormatBytes(progress.TotalBytes.Value)}";
            }

            if (progress.Percent is not null)
            {
                task.IsIndeterminate = false;
                task.MaxValue         = 100;
                task.Value            = Math.Clamp(progress.Percent.Value, 0, 100);
            }
            else if (progress is { FragmentsTotal: > 0, FragmentsDone: not null })
            {
                task.IsIndeterminate = false;
                task.MaxValue         = progress.FragmentsTotal.Value;
                task.Value            = Math.Min(progress.FragmentsDone.Value, progress.FragmentsTotal.Value);
            }
            else if (progress.TotalBytes is > 0)
            {
                task.IsIndeterminate = false;
                task.MaxValue         = progress.TotalBytes.Value;
                task.Value            = Math.Min(progress.BytesDownloaded, progress.TotalBytes.Value);
            }
            else
            {
                task.IsIndeterminate = true;
            }

            var speed = tracker.CurrentSpeed;
            if (speed > 0)
            {
                detail += $" • {DownloadProgressFormatter.FormatBytes((long)speed)}/s";
                var eta = tracker.CurrentEta;
                if (eta is not null)
                {
                    detail += $" • {DownloadSpeedometer.FormatEta(eta.Value)}";
                }
            }

            task.Description = detail + " • Esc: iptal";
            return;
        }

        task.IsIndeterminate = true;
        task.Description      = $"{stage} • Esc: iptal";
    }
}
