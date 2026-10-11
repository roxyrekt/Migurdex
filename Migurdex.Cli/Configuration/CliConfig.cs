using System.Text.RegularExpressions;

namespace Migurdex.Cli.Configuration;

public class CliConfig
{
    private static readonly Regex _environmentVariablePattern = new(
        "%(?<percent>[^%]+)%|\\$(?:\\{(?<braced>[A-Za-z_][A-Za-z0-9_]*)\\}|(?<plain>[A-Za-z_][A-Za-z0-9_]*))",
        RegexOptions.Compiled
        | RegexOptions.CultureInvariant);

    private string _downloadDirectory = DefaultDownloadDirectory;
    private string _ytDlpPath         = "yt-dlp";

    public string       ApiBaseUrl               { get; set; } = "http://127.0.0.1:7045";
    public string       PreferredPlayer          { get; set; } = "mpv";
    public bool         EnableDiscordRpc         { get; set; } = true;
    public string       DiscordRpcTitleMode      { get; set; } = "İçerik";
    public bool         EnableIncognitoMode      { get; set; } = false;
    public bool         ShowPlayerLogs           { get; set; } = false;
    public bool         AutoSelectBestSource     { get; set; } = false;
    public double       AutoSelectTimeoutSeconds { get; set; } = 5;
    public List<string> DisabledProviders        { get; set; } = [];

    public string DownloadDirectory
    {
        get => string.IsNullOrWhiteSpace(_downloadDirectory) ? DefaultDownloadDirectory : _downloadDirectory;
        set => _downloadDirectory = NormalizeDownloadDirectory(value);
    }

    public string YtDlpPath
    {
        get => string.IsNullOrWhiteSpace(_ytDlpPath) ? "yt-dlp" : _ytDlpPath;
        set => _ytDlpPath = string.IsNullOrWhiteSpace(value) ? "yt-dlp" : value.Trim();
    }

    public bool DownloadSubtitles { get; set; } = true;
    public bool DownloadResume    { get; set; } = true;
    public bool DownloadOverwrite { get; set; } = false;

    public bool   AutoDownloadBestSource           { get; set; } = false;
    public double DownloadAutoSelectTimeoutSeconds { get; set; } = 5;

    public int    MaxConcurrentEpisodes         { get; set; } = 2;
    public int    ConnectionsPerFile            { get; set; } = 4;
    public int    MaxSocketsPerHoster           { get; set; } = 8;
    public int    DownloadMaxRetries            { get; set; } = 3;
    public double DownloadRetryBaseDelaySeconds { get; set; } = 2;
    /// <summary>Global indirme hız limiti (KB/sn). 0 = sınırsız.</summary>
    public long   MaxDownloadSpeedKbps          { get; set; } = 0;

    public string DefaultEpisodeAction { get; set; } = "Ask";
    public string BatchSourceSelection { get; set; } = "Ask";

    public List<string> PreferredGroupOrder { get; set; } = [];

    public bool    UpdateCheckEnabled { get; set; } = true;
    public string  UpdateChannel      { get; set; } = "stable";
    public string? SkippedVersion     { get; set; }

    public TurnstileConfig Turnstile { get; set; } = new();

    public List<string> AutoNeverHosters   { get; set; } = [];
    public List<string> AutoOnlyHosters    { get; set; } = [];
    public List<string> AutoNeverQualities { get; set; } = [];
    public List<string> AutoOnlyQualities  { get; set; } = [];
    public List<string> AutoNeverTypes     { get; set; } = [];
    public List<string> AutoOnlyTypes      { get; set; } = [];

    public List<string> SourceSortPriority { get; set; } =
    [
        "Quality",
        "Format",
        "Hoster",
        "Group"
    ];

    public List<string> PreferredQualityOrder { get; set; } =
    [
        "2160p",
        "1440p",
        "1080p",
        "720p",
        "480p",
        "360p",
        "Auto"
    ];

    public List<string> PreferredFormatOrder { get; set; } =
    [
        "Mp4",
        "M3U8"
    ];

    public List<string> PreferredHosterOrder { get; set; } =
    [
        "Streamcash",
        "Streamain",
        "Tau Video",
        "Turkanime",
        "GoogleDrive",
        "HdVid",
        "Uqload",
        "Flyfile",
        "VidsSt",
        "Byse",
        "AitrVip",
        "DoodStream",
        "AnizmPlayer",
        "Voe",
        "YandexDisk",
        "HexUpload",
        "Cyberfile",
        "Videa",
        "Sibnet",
        "Sistenn",
        "YourUpload",
        "Vidsonic",
        "MailRu",
        "Puffy",
        "Dailymotion",
        "OkRu",
        "StreamWish",
        "MixDrop",
        "VK",
        "Gofile",
        "Sendvid",
        "Mp4Upload",
        "Streamtape",
        "Firestream",
        "Vidmoly",
        "Abyss",
        "Rumble"
    ];

    public static string DefaultDownloadDirectory
    {
        get
        {
            if (!OperatingSystem.IsWindows())
            {
                var xdgDownloadDirectory = Environment.GetEnvironmentVariable("XDG_DOWNLOAD_DIR");
                if (!string.IsNullOrWhiteSpace(xdgDownloadDirectory))
                {
                    var expanded = Environment.ExpandEnvironmentVariables(xdgDownloadDirectory.Trim());
                    if (Path.IsPathRooted(expanded))
                    {
                        return Path.Combine(expanded, "Migurdex");
                    }
                }
            }

            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(userProfile))
            {
                userProfile = Directory.GetCurrentDirectory();
            }

            return Path.Combine(userProfile, "Downloads", "Migurdex");
        }
    }

    public static string NormalizeDownloadDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return DefaultDownloadDirectory;
        }

        var trimmed = path.Trim().Trim('"');
        if (trimmed.Length == 0)
        {
            return DefaultDownloadDirectory;
        }

        if (trimmed.StartsWith('~')
            && (trimmed.Length == 1 || trimmed[1] == Path.DirectorySeparatorChar || trimmed[1] == '/'))
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(userProfile))
            {
                trimmed = userProfile + trimmed[1..];
            }
        }

        var expanded = _environmentVariablePattern.Replace(trimmed,
                                                           match =>
                                                           {
                                                               var name = match.Groups["percent"].Success
                                                                              ? match.Groups["percent"].Value
                                                                              : match.Groups["braced"].Success
                                                                                  ? match.Groups["braced"].Value
                                                                                  : match.Groups["plain"].Value;
                                                               return Environment.GetEnvironmentVariable(name)
                                                                      ?? match.Value;
                                                           });

        return string.IsNullOrWhiteSpace(expanded) ? DefaultDownloadDirectory : expanded;
    }

    public sealed class TurnstileConfig
    {
        public bool   Enabled        { get; set; } = true;
        public bool   AllowDownload  { get; set; } = true;
        public double TimeoutSeconds { get; set; } = 45;
    }
}
