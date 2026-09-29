using Migurdex.Shared.Models;

namespace Migurdex.Cli.Services.Downloads;

public enum DownloadStage
{
    Preparing,
    Requesting,
    Downloading,
    Finalizing,
    Subtitle,
    Completed,
    Failed,
    Cancelled
}

public sealed class DownloadProgress
{
    public DownloadProgress()
    {
    }

    public DownloadProgress(DownloadStage stage, long bytesDownloaded, long? totalBytes = null)
    {
        Stage          = stage;
        BytesDownloaded = bytesDownloaded;
        TotalBytes      = totalBytes;
    }

    public DownloadStage Stage          { get; init; }
    public long          BytesDownloaded { get; init; }
    public long?         TotalBytes      { get; init; }

    public long  Bytes => BytesDownloaded;
    public long? Total => TotalBytes;
}

public sealed class DownloadRequest
{
    public DownloadRequest()
    {
    }

    public DownloadRequest(
        VideoSource                  source,
        string                       outputDirectory,
        string                       animeTitle,
        string                       episodeTitle,
        int                          season          = 1,
        double                       episodeNumber   = 1,
        bool                         overwrite       = false,
        bool                         resume          = true,
        IProgress<DownloadProgress>? progress        = null)
    {
        Source          = source;
        OutputDirectory = outputDirectory;
        AnimeTitle      = animeTitle;
        EpisodeTitle    = episodeTitle;
        Season          = season;
        EpisodeNumber   = episodeNumber;
        Overwrite       = overwrite;
        Resume          = resume;
        Progress        = progress;
    }

    public VideoSource                 Source          { get; set; } = new();
    public string                      OutputDirectory { get; set; } = string.Empty;
    public string                      AnimeTitle      { get; set; } = string.Empty;
    public string                      EpisodeTitle    { get; set; } = string.Empty;
    public int                         Season          { get; set; } = 1;
    public double                      EpisodeNumber   { get; set; } = 1;
    public bool                        Overwrite       { get; set; }
    public bool                        Resume          { get; set; } = true;
    public bool                        DownloadSubtitles { get; set; } = true;
    public IProgress<DownloadProgress>? Progress        { get; set; }

    public bool IncludeSubtitles
    {
        get => DownloadSubtitles;
        set => DownloadSubtitles = value;
    }

    public string RootDirectory
    {
        get => OutputDirectory;
        set => OutputDirectory = value;
    }

    public string DestinationDirectory
    {
        get => OutputDirectory;
        set => OutputDirectory = value;
    }

    public bool ResumeEnabled
    {
        get => Resume;
        set => Resume = value;
    }

    public bool NoResume
    {
        get => !Resume;
        set => Resume = !value;
    }
}

public sealed class DownloadResult
{
    public static DownloadResult Successful(
        string             mediaPath,
        IEnumerable<string>? subtitlePaths = null,
        IEnumerable<string>? warnings     = null)
    {
        return new DownloadResult
        {
            Success       = true,
            MediaPath     = mediaPath,
            SubtitlePaths = subtitlePaths?.ToArray() ?? [],
            Warnings      = warnings?.ToArray() ?? []
        };
    }

    public static DownloadResult Failed(string error)
    {
        return new DownloadResult
        {
            Success = false,
            Error   = error
        };
    }

    public bool             Success       { get; init; }
    public string?          MediaPath     { get; init; }
    public IReadOnlyList<string> SubtitlePaths { get; init; } = [];
    public IReadOnlyList<string> Warnings      { get; init; } = [];

    public string? Error       { get; init; }
    public bool    IsCancelled { get; init; }

    public bool   IsSuccess => Success;
    public string? ErrorMessage => Error;
    public string? VideoPath => MediaPath;
    public string? OutputPath => MediaPath;
    public bool   Cancelled => IsCancelled;
}

public sealed class MediaDownloadResult
{
    public MediaDownloadResult(string outputPath, long bytes = 0, long? totalBytes = null, bool resumed = false)
    {
        OutputPath  = outputPath;
        Bytes       = bytes;
        TotalBytes  = totalBytes;
        WasResumed  = resumed;
    }

    public string OutputPath { get; }
    public long   Bytes      { get; }
    public long?  TotalBytes { get; }
    public bool   WasResumed { get; }

    public string Path => OutputPath;
}

public sealed class SubtitleDownloadResult
{
    public SubtitleDownloadResult(string outputPath)
    {
        OutputPath = outputPath;
    }

    public string OutputPath { get; }
    public string Path       => OutputPath;
}

public sealed class HlsDownloadOptions
{
    public string Executable   { get; set; } = "yt-dlp";
    public int    MaxAttempts { get; set; } = 3;
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);
}

public sealed class DownloadPath
{
    public DownloadPath(
        string       rootDirectory,
        string       animeDirectory,
        string       fileStem,
        string?      extension = null,
        string?      mediaPath = null)
    {
        RootDirectory  = rootDirectory;
        AnimeDirectory = animeDirectory;
        FileStem       = fileStem;
        Extension      = extension;
        MediaPath      = mediaPath ?? GetMediaPath(extension);
    }

    public string       RootDirectory  { get; }
    public string       AnimeDirectory { get; }
    public string       FileStem       { get; }
    public string?      Extension      { get; }
    public string       MediaPath      { get; }

    public string AnimeFolder => AnimeDirectory;
    public string Stem       => FileStem;
    public string Directory  => AnimeDirectory;
    public string FullPath   => MediaPath;
    public string Path       => MediaPath;

    public string GetMediaPath(string? extension = null)
    {
        var selectedExtension = string.IsNullOrWhiteSpace(extension)
                                    ? Extension
                                    : DownloadPathBuilder.NormalizeExtension(extension);
        var fileName          = selectedExtension is null
                                    ? FileStem
                                    : FileStem + selectedExtension;
        var path = System.IO.Path.Combine(AnimeDirectory, fileName);
        DownloadPathBuilder.EnsureFullPathBudget(path, DownloadPathBuilder.TemporarySuffixUtf8Bytes);
        return path;
    }

    public string GetPartPath(string? extension = null)
    {
        return GetMediaPath(extension) + ".part";
    }
}
