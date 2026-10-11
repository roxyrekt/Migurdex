namespace Migurdex.Cli.Services.Downloads;

public static class DownloadRetryPolicy
{
    private static readonly string[] _permanentMarkers =
    [
        "404", "not found", "bulunamadı",
        "forbidden", "403", "unauthorized", "401",
        "no streams", "kaynak bulunamadı",
        "indirilebilir değil", "not supported", "desteklenmiyor",
        "invalid", "geçersiz"
    ];

    private static readonly string[] _transientMarkers =
    [
        "timed out", "timeout", "zaman aşımı", "zaman aşımına",
        "429", "too many", "rate limit", "rate-limit",
        "500", "502", "503", "504", "bad gateway", "unavailable",
        "connection", "bağlantı", "network", "ağ hatası",
        "reset", "refused", "unreachable", "socket",
        "temporar", "geçici", "try again", "yeniden dene"
    ];

    public static bool IsTransient(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return true;
        }

        foreach (var marker in _permanentMarkers)
        {
            if (error.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    public static TimeSpan BackoffDelay(int attempt, TimeSpan baseDelay)
    {
        var clampedAttempt = Math.Max(1, attempt);
        var baseSeconds    = Math.Max(0, baseDelay.TotalSeconds);
        var grown          = baseSeconds * Math.Pow(2, clampedAttempt - 1);
        var capped         = Math.Min(grown, 60);
        var jitter         = Random.Shared.NextDouble() * Math.Min(1, capped * 0.25);

        return TimeSpan.FromSeconds(capped + jitter);
    }
}
