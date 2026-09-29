namespace Migurdex.Cli.Configuration;

public class CliConfig
{
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
        set => _downloadDirectory = string.IsNullOrWhiteSpace(value) ? DefaultDownloadDirectory : value.Trim();
    }

    public string YtDlpPath
    {
        get => string.IsNullOrWhiteSpace(_ytDlpPath) ? "yt-dlp" : _ytDlpPath;
        set => _ytDlpPath = string.IsNullOrWhiteSpace(value) ? "yt-dlp" : value.Trim();
    }

    public bool DownloadSubtitles { get; set; } = true;
    public bool DownloadResume    { get; set; } = true;
    public bool DownloadOverwrite { get; set; } = false;

    public bool    UpdateCheckEnabled { get; set; } = true;
    public string  UpdateChannel      { get; set; } = "stable";
    public string? SkippedVersion     { get; set; }

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
}
