namespace Migurdex.Cli.Configuration;

public class WatchHistoryEntry
{
    public string AnimeId { get; set; } = string.Empty;
    public string AnimeTitle { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
    public string EpisodeId { get; set; } = string.Empty;
    public string EpisodeTitle { get; set; } = string.Empty;
    public string PosterUrl { get; set; } = string.Empty;
    public int Season { get; set; } = 1;
    public double EpisodeNumber { get; set; }
    public double LastPositionSeconds { get; set; }
    public double TotalDurationSeconds { get; set; }
    public double ProgressPercentage => TotalDurationSeconds > 0 ? LastPositionSeconds / TotalDurationSeconds * 100 : 0;
    public bool IsCompleted { get; set; }
    public DateTime LastWatchedAt { get; set; } = DateTime.UtcNow;
}
