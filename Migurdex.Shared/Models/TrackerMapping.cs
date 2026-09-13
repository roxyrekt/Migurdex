namespace Migurdex.Shared.Models;

public sealed class TrackerMappingEntry
{
    public string   ProviderName  { get; set; } = string.Empty;
    public string   ProviderId    { get; set; } = string.Empty;
    public string?  AniListId     { get; set; }
    public string?  MyAnimeListId { get; set; }
    public string   MatchedTitle  { get; set; } = string.Empty;
    public double   Score         { get; set; }
    public DateTime UpdatedAt     { get; set; } = DateTime.UtcNow;
}

public sealed class TrackerCandidate
{
    public MediaMetadata Metadata { get; set; } = new();
    public double        Score    { get; set; }
}

public sealed class TrackerResolveResult
{
    public TrackerMappingEntry?            Entry      { get; set; }
    public bool                            Ambiguous  { get; set; }
    public bool                            FromCache  { get; set; }
    public IReadOnlyList<TrackerCandidate> Candidates { get; set; } = [];
}
