namespace Migurdex.Cli.Configuration;

public class CliConfig
{
    /// <summary>Toplu indirmede aynı anda çalışabilecek en az iş sayısı.</summary>
    public const int MinDownloadConcurrency = 1;

    /// <summary>Toplu indirmede aynı anda çalışabilecek en fazla iş sayısı.</summary>
    public const int MaxDownloadConcurrency = 8;

    /// <summary>Ayarlarda hiçbir şey seçilmediyse kullanılan eşzamanlılık.</summary>
    public const int DefaultDownloadConcurrency = 2;

    private string _downloadDirectory   = DefaultDownloadDirectory;
    private int    _downloadConcurrency = DefaultDownloadConcurrency;
    private string _ytDlpPath           = "yt-dlp";

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

    /// <summary>
    /// Toplu indirmede birden fazla bölümün aynı anda indirilip indirilmeyeceği.
    /// Kapalı olduğunda toplu indirme de tek tek (sıralı) çalışır.
    /// </summary>
    public bool DownloadParallelEnabled { get; set; } = true;

    /// <summary>
    /// Paralel toplu indirmede eşzamanlı iş sayısı. Okuma/yazma sırasında
    /// <see cref="MinDownloadConcurrency"/> - <see cref="MaxDownloadConcurrency"/> aralığına sıkıştırılır,
    /// böylece elle bozulmuş bir config dosyası geçersiz değer üretemez.
    /// </summary>
    public int DownloadConcurrency
    {
        get => _downloadConcurrency;
        set => _downloadConcurrency = ClampConcurrency(value);
    }

    public bool   AutoDownloadBestSource           { get; set; } = false;
    public double DownloadAutoSelectTimeoutSeconds { get; set; } = 5;

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

    public static int ClampConcurrency(int value)
    {
        if (value < MinDownloadConcurrency)
        {
            return MinDownloadConcurrency;
        }

        return value > MaxDownloadConcurrency ? MaxDownloadConcurrency : value;
    }

    /// <summary>
    /// İndirme dizinini kalıcı olarak kullanılacak biçime getirir: baştaki/sondaki boşluk ve
    /// tırnaklar atılır, <c>~</c> kullanıcı profiline açılır, <c>%DEĞİŞKEN%</c> / <c>$DEĞİŞKEN</c>
    /// ortam değişkenleri genişletilir. Boş sonuç varsayılan dizine düşer.
    /// </summary>
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

        var expanded = Environment.ExpandEnvironmentVariables(trimmed);
        return string.IsNullOrWhiteSpace(expanded) ? DefaultDownloadDirectory : expanded;
    }

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
