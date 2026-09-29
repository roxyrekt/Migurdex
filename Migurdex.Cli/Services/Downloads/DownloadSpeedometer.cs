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
    private readonly double _alpha;
    private DateTimeOffset? _lastTime;
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
            _lastTime  = now;
            _lastBytes = bytes;
            return;
        }

        var elapsed = (now - _lastTime.Value).TotalSeconds;
        _lastTime = now;
        if (elapsed <= 0)
        {
            return;
        }

        var instant = Math.Max(0, (bytes - _lastBytes) / elapsed);
        _lastBytes  = bytes;
        _bytesPerSecond = _hasEstimate
                              ? _alpha * instant + (1 - _alpha) * _bytesPerSecond
                              : instant;
        _hasEstimate = true;
    }

    public double BytesPerSecond => _hasEstimate ? _bytesPerSecond : 0;

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
