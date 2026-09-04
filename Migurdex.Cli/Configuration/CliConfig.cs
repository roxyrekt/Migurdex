namespace Migurdex.Cli.Configuration;

public class CliConfig
{
    public string       ApiBaseUrl               { get; set; } = "http://localhost:7045";
    public string       PreferredPlayer          { get; set; } = "mpv";
    public bool         EnableDiscordRpc         { get; set; } = true;
    public bool         EnableIncognitoMode      { get; set; } = false;
    public bool         AutoSelectBestSource     { get; set; } = false;
    public double       AutoSelectTimeoutSeconds { get; set; } = 5;
    public List<string> DisabledProviders        { get; set; } = [];

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
        "M3U8",
        "Mp4"
    ];

    public List<string> PreferredHosterOrder { get; set; } =
    [
        "GoogleDrive",
        "Yandex",
        "AnizmPlayer",
        "Sibnet",
        "Vidmoly",
        "Streamwish"
    ];
}
