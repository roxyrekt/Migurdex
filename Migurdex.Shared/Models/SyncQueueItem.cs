namespace Migurdex.Shared.Models;

public sealed class SyncQueueItem
{
    public long     Id               { get; set; }
    public string   Provider         { get; set; } = string.Empty;
    public string   AnimeTitle       { get; set; } = string.Empty;
    public string   AnimeId          { get; set; } = string.Empty;
    public int      Season           { get; set; } = 1;
    public double   Episode          { get; set; }
    public bool     IsCompleted      { get; set; }
    public int      Attempts         { get; set; }
    public DateTime NextAttemptAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime EnqueuedAtUtc    { get; set; } = DateTime.UtcNow;
}
