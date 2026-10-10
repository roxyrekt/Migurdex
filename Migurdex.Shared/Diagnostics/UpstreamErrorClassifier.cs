using Migurdex.Shared.Enums;

namespace Migurdex.Shared.Diagnostics;

public static class UpstreamErrorClassifier
{
    private const int MaxScanLength = 65536;

    private static readonly string[] QuotaMarkers =
    [
        "quota exceeded", "quota_exceeded", "quotaexceeded", "exceeds the quota",
        "download quota", "playback quota", "kota"
    ];

    private static readonly string[] CloudflareMarkers =
    [
        "just a moment", "checking your browser", "__cf_bm", "cf-chl",
        "attention required", "cloudflare"
    ];

    private static readonly string[] CopyrightMarkers =
    [
        "copyright claim", "copyrightsrestricted", "авторских прав",
        "at the request of the copyright holder", "telif hakkı"
    ];

    private static readonly string[] EmbedMarkers =
    [
        "blocklist", "embedding disabled", "embed disabled", "not embeddable",
        "\"embeddable\":false"
    ];

    private static readonly string[] ProcessingMarkers =
    [
        "publishing in progress", "publication of this video", "dm006",
        "video is being processed", "processing"
    ];

    private static readonly string[] PrivateMarkers =
    [
        "\"private\":", "private content", "this video is private", "video is private",
        "özel içerik", "özel olarak", "login required", "sign in to watch", "giriş yap"
    ];

    private static readonly string[] DeletedMarkers =
    [
        "deleted", "has been removed", "no longer available", "video unavailable",
        "file not found", "not found", "does not exist", "doesn't exist",
        "silindi", "silinmiş", "kaldırıldı", "kaldırılmış", "bulunamadı", "mevcut değil",
        "удалено", "удален", "не найдено",
        "has not been found", "usernotfound"
    ];

    private static readonly string[] RateMarkers =
    [
        "too many requests", "rate limit", "rate-limit", "ratelimit", "slow down"
    ];

    public static UpstreamErrorKind Detect(int statusCode, string? body)
    {
        var text = string.IsNullOrEmpty(body)
            ? string.Empty
            : body.Length > MaxScanLength
                ? body[..MaxScanLength].ToLowerInvariant()
                : body.ToLowerInvariant();

        if (ContainsAny(text, QuotaMarkers))
        {
            return UpstreamErrorKind.QuotaExceeded;
        }

        if (ContainsAny(text, CopyrightMarkers))
        {
            return UpstreamErrorKind.CopyrightBlocked;
        }

        if (ContainsAny(text, PrivateMarkers))
        {
            return UpstreamErrorKind.Private;
        }

        if (ContainsAny(text, ProcessingMarkers))
        {
            return UpstreamErrorKind.Processing;
        }

        if (ContainsAny(text, DeletedMarkers))
        {
            return UpstreamErrorKind.Deleted;
        }

        if (ContainsAny(text, EmbedMarkers))
        {
            return UpstreamErrorKind.EmbedBlocked;
        }

        if (ContainsAny(text, CloudflareMarkers))
        {
            return UpstreamErrorKind.CloudflareBlocked;
        }

        if (ContainsAny(text, RateMarkers))
        {
            return UpstreamErrorKind.RateLimited;
        }

        return statusCode switch
        {
            429 => UpstreamErrorKind.RateLimited,
            404 => UpstreamErrorKind.NotFound,
            410 => UpstreamErrorKind.Deleted,
            _   => UpstreamErrorKind.Unknown
        };
    }

    public static string Summarize(string? text, int maxLength = 200)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var single = string.Join(' ', text.Split((char[]?) null, StringSplitOptions.RemoveEmptyEntries));

        return single.Length > maxLength ? single[..maxLength] + "…" : single;
    }

    private static bool ContainsAny(string text, string[] markers)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (var marker in markers)
        {
            if (text.Contains(marker, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
