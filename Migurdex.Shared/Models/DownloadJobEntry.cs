using Migurdex.Shared.Enums;

namespace Migurdex.Shared.Models;

public sealed class DownloadJobEntry
{
    public string            Id                { get; set; } = Guid.NewGuid().ToString("N");
    public string            ProviderName      { get; set; } = string.Empty;
    public string            AnimeId           { get; set; } = string.Empty;
    public string            AnimeTitle        { get; set; } = string.Empty;
    public string            EpisodeId         { get; set; } = string.Empty;
    public string            EpisodeTitle      { get; set; } = string.Empty;
    public int               Season            { get; set; } = 1;
    public double            EpisodeNumber     { get; set; } = 1;
    public string?           PreferredGroup    { get; set; }
    public string            PreferredFormat   { get; set; } = "Auto";
    public VideoSource?      Source            { get; set; }
    public string            OutputDirectory   { get; set; } = string.Empty;
    public string?           MediaPath         { get; set; }
    public List<string>      SubtitlePaths     { get; set; } = [];
    public List<string>      Warnings          { get; set; } = [];
    public DownloadJobStatus Status            { get; set; } = DownloadJobStatus.Queued;
    public long              BytesDownloaded   { get; set; }
    public long?             TotalBytes        { get; set; }
    public int?              FragmentsDone     { get; set; }
    public int?              FragmentsTotal    { get; set; }
    public double?           Percent           { get; set; }
    public string?           ErrorMessage      { get; set; }
    public bool              Overwrite         { get; set; }
    public bool              ResumeEnabled     { get; set; } = true;
    public bool              DownloadSubtitles { get; set; } = true;
    public int               Priority          { get; set; }
    public DateTime          CreatedAtUtc      { get; set; } = DateTime.UtcNow;
    public DateTime          UpdatedAtUtc      { get; set; } = DateTime.UtcNow;
}
