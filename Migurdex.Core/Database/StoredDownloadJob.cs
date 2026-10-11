namespace Migurdex.Core.Database;

public sealed record StoredDownloadJob(
    Guid           Id,
    string         Provider,
    string         AnimeId,
    string         AnimeTitle,
    string         EpisodeId,
    string         EpisodeTitle,
    int            Season,
    double         Number,
    List<string>   Groups,
    List<string>   Hosters,
    List<string>   Qualities,
    List<string>   Formats,
    string         OutputDirectory,
    bool           DownloadSubtitles,
    string         State,
    string?        Error,
    string?        MediaPath,
    DateTimeOffset EnqueuedAtUtc,
    DateTimeOffset UpdatedAtUtc);
