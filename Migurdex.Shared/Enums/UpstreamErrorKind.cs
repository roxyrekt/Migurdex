namespace Migurdex.Shared.Enums;

public enum UpstreamErrorKind
{
    Unknown,

    NotFound,

    Deleted,

    Private,

    QuotaExceeded,

    RateLimited,

    EmbedBlocked,

    CopyrightBlocked,

    UpstreamChanged,

    CloudflareBlocked,

    Processing
}
