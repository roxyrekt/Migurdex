namespace Migurdex.Shared.Models;

public sealed class TrackerEpisodeMapping
{
    public string  AniListId     { get; set; } = string.Empty;
    public string? MyAnimeListId { get; set; }
    public int     Season        { get; set; }
    public double  Episode       { get; set; }
    public int?    TotalEpisodes { get; set; }
    public bool    IsOverflow    { get; set; }
}

public sealed class SyncWatchEntry
{
    public string Provider    { get; set; } = string.Empty;
    public string AnimeTitle  { get; set; } = string.Empty;
    public string AnimeId     { get; set; } = string.Empty;
    public int    Season      { get; set; } = 1;
    public double Episode     { get; set; }
    public bool   IsCompleted { get; set; }
}

public sealed class EpisodeMappingResult
{
    public TrackerEpisodeMapping?          Mapping    { get; set; }
    public bool                            Ambiguous  { get; set; }
    public IReadOnlyList<TrackerCandidate> Candidates { get; set; } = [];
}

public enum SyncOutcomeKind
{
    None,
    Pushed,
    Queued,
    SkippedNoToken,
    Ambiguous
}

public sealed class SyncOutcome
{
    public SyncOutcomeKind                 Kind       { get; set; }
    public SyncWatchEntry?                 Entry      { get; set; }
    public int?                            Progress   { get; set; }
    public List<string>                    SyncedTo   { get; set; } = [];
    public IReadOnlyList<TrackerCandidate> Candidates { get; set; } = [];
}
