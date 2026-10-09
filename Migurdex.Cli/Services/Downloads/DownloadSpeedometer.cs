namespace Migurdex.Cli.Services.Downloads;

internal static class DownloadStageLabel
{
    private static readonly string[] AudioMarkers =
    [
        "aud", "audio", "m4a", "mp3", "opus", "ogg", "aac", "vorbis", "sound", "ac3", "dts"
    ];

    public static string For(DownloadStage stage, string? track, bool isAudioTrack = false)
    {
        if (stage == DownloadStage.Downloading && (isAudioTrack || LooksLikeAudio(track)))
        {
            return "Ses indiriliyor";
        }

        return stage switch
        {
            DownloadStage.Preparing   => "Hazırlanıyor",
            DownloadStage.Requesting  => "Bağlanıyor",
            DownloadStage.Downloading => "Video indiriliyor",
            DownloadStage.Finalizing  => "Tamamlanıyor",
            DownloadStage.Subtitle    => "Altyazı indiriliyor",
            DownloadStage.Completed   => "Tamamlandı",
            DownloadStage.Failed      => "Başarısız",
            DownloadStage.Cancelled   => "İptal edildi",
            _                         => "İşleniyor"
        };
    }

    private static bool LooksLikeAudio(string? track)
    {
        return !string.IsNullOrWhiteSpace(track)
               && AudioMarkers.Any(marker => track.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }
}

internal sealed class DownloadSpeedometer
{
    /// <summary>Bu süreden uzun sessizlikten sonra ölçüm sıfırdan başlar (eski hız yapışmasın).</summary>
    private const double IdleResetSeconds = 1.5;

    private readonly double _alpha;
    private DateTimeOffset? _lastTime;
    private DateTimeOffset? _lastFlowTime;
    private long _lastBytes;
    private double _bytesPerSecond;
    private bool _hasEstimate;

    public DownloadSpeedometer(double alpha = 0.3)
    {
        _alpha = double.IsFinite(alpha) ? Math.Clamp(alpha, 0.05, 1) : 0.3;
    }

    public void Sample(long bytes, DateTimeOffset now)
    {
        if (_lastTime is null)
        {
            _lastTime     = now;
            _lastFlowTime = now;
            _lastBytes    = bytes;
            return;
        }

        // Payda: son örnekten bu yana geçen süre. İndirme döngüsü bayt akmadığında hiç örnek
        // göndermez; bu yüzden uzun boşluk ancak dosya durakladığında oluşur ve orada ekran
        // uydurma hız yerine "duraklı" yazar (bkz. BatchDownloadView.DescribeProgress).
        var elapsed = (now - _lastTime.Value).TotalSeconds;
        _lastTime = now;
        if (bytes <= _lastBytes)
        {
            // Bayt akmadı: geçen süre hız değildir. Ölçüm düşürülmez, yoksa birleştirme ya da
            // duraklama aralarında ekran KB/s gösterir ve indirme bitmiş gibi görünür.
            return;
        }

        var delta = bytes - _lastBytes;
        _lastBytes    = bytes;
        _lastFlowTime = now;
        if (elapsed <= 0)
        {
            return;
        }

        var instant = delta / elapsed;
        if (!_hasEstimate || elapsed >= IdleResetSeconds)
        {
            // Boşluktan sonraki ilk veri: yeni akış kendi hızını kursun, eski tahmin taşınmasın.
            _bytesPerSecond = instant;
        }
        else
        {
            _bytesPerSecond = _alpha * instant + (1 - _alpha) * _bytesPerSecond;
        }

        _hasEstimate = true;
    }

    public double BytesPerSecond => _hasEstimate ? _bytesPerSecond : 0;

    /// <summary>Son bayt hareketinden bu yana geçen süre; henüz hiç bayt gelmediyse null.</summary>
    public TimeSpan? IdleFor(DateTimeOffset now)
    {
        return _lastFlowTime is null ? null : now - _lastFlowTime.Value;
    }

    public TimeSpan? EstimateRemaining(long downloaded, long? total, double? speedOverride = null)
    {
        var speed = speedOverride ?? BytesPerSecond;
        if (total is null || total <= downloaded || speed <= 0)
        {
            return null;
        }

        return TimeSpan.FromSeconds((total.Value - downloaded) / speed);
    }

    public static string FormatEta(TimeSpan remaining)
    {
        if (remaining.TotalHours >= 1)
        {
            return $"ETA {(int)remaining.TotalHours}:{remaining.Minutes:00}:{remaining.Seconds:00}";
        }

        return $"ETA {remaining.Minutes:00}:{remaining.Seconds:00}";
    }
}
