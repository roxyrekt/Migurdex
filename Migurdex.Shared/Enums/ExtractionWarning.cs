namespace Migurdex.Shared.Enums;

public enum ExtractionWarning
{
    NoExtractorMatched,

    EmptyResult,

    QuotaExceeded,

    RateLimited,

    Private,

    Deleted,

    NotFound,

    EmbedBlocked,

    CopyrightBlocked,

    UpstreamChanged,

    CloudflareBlocked,

    Processing
}
