namespace Migurdex.Cli.Dependencies;

public enum DependencyKind
{
    Mpv,
    YtDlp,
    Ffmpeg
}

public static class DependencyCatalog
{
    public static IReadOnlyList<DependencyKind> All { get; } =
        [DependencyKind.Mpv, DependencyKind.YtDlp, DependencyKind.Ffmpeg];

    public static string CommandName(DependencyKind kind)
    {
        return kind switch
        {
            DependencyKind.Mpv    => "mpv",
            DependencyKind.YtDlp  => "yt-dlp",
            DependencyKind.Ffmpeg => "ffmpeg",
            _                     => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
    }

    public static string DisplayName(DependencyKind kind)
    {
        return kind switch
        {
            DependencyKind.Mpv    => "MPV",
            DependencyKind.YtDlp  => "yt-dlp",
            DependencyKind.Ffmpeg => "ffmpeg",
            _                     => kind.ToString()
        };
    }

    public static string Description(DependencyKind kind)
    {
        return kind switch
        {
            DependencyKind.Mpv    => "Video oynatıcı",
            DependencyKind.YtDlp  => "HLS indirme aracı",
            DependencyKind.Ffmpeg => "HLS birleştirme/aktarma aracı",
            _                     => string.Empty
        };
    }

    public static IReadOnlyList<string> ExecutableCandidates(DependencyKind kind)
    {
        var name = CommandName(kind);
        return OperatingSystem.IsWindows()
                   ? [name + ".exe", name]
                   : [name];
    }

    public static string FileName(DependencyKind kind)
    {
        var name = CommandName(kind);
        return OperatingSystem.IsWindows() ? name + ".exe" : name;
    }

    public static string? ToolSubdirectory(DependencyKind kind)
    {
        return kind == DependencyKind.Mpv ? "mpv" : null;
    }

    public static string VersionArgument(DependencyKind kind)
    {
        return kind == DependencyKind.Ffmpeg ? "-version" : "--version";
    }

    public static bool TryParse(string? value, out DependencyKind kind)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "mpv":
                kind = DependencyKind.Mpv;

                return true;
            case "yt-dlp":
            case "ytdlp":
                kind = DependencyKind.YtDlp;

                return true;
            case "ffmpeg":
                kind = DependencyKind.Ffmpeg;

                return true;
            default:
                kind = default;

                return false;
        }
    }
}
