using Migurdex.Cli.Services.Discord;
using Migurdex.Shared.Models;

namespace Migurdex.Cli.Services;

public class DiscordRpcService : IDiscordRpcService, IDisposable
{
    private const string DefaultClientId = "1406319525124768007";
    private const string DefaultCover    = "large_image";
    private const string PauseIcon       = "https://cdn.rcd.gg/PreMiD/resources/pause.png";
    private const string GitHubUrl       = "https://github.com/roxyrekt/Migurdex";

    private static readonly string[] _globalCdns =
    [
        "tmdb.org", "anilist.co", "myanimelist.net", "media.kitsu.io", "cdn.rcd.gg", "raw.githubusercontent.com",
        "githubusercontent.com"
    ];

    private static readonly Lock _rpcLogLock = new();

    private readonly IApiClientService     _apiService;
    private readonly IConfigurationService _configService;
    private          List<ProviderInfo>?   _cachedProviders;
    private          DiscordIpcClient?     _client;

    public DiscordRpcService(IConfigurationService configService, IApiClientService apiService)
    {
        _configService = configService;
        _apiService    = apiService;
        Initialize();
    }

    public void UpdatePresence(string title, string details, double? remainingSeconds = null)
    {
        UpdatePlaybackPresence(title, details, null, false, null, remainingSeconds);
    }

    public void UpdatePlaybackPresence(
        string  animeTitle,
        string  episodeTitle,
        string? posterUrl              = null,
        bool    isPaused               = false,
        double? currentPositionSeconds = null,
        double? totalDurationSeconds   = null,
        string? providerName           = null,
        int?    season                 = null,
        double? episodeNumber          = null)
    {
        EnsureInitialized();
        if (_client == null || !_configService.Config.EnableDiscordRpc)
        {
            return;
        }

        try
        {
            var detailsText = Truncate(animeTitle);

            string stateText;
            var    stateParts = new List<string>();
            if (season is > 0)
            {
                stateParts.Add($"S{season.Value}");
            }

            if (episodeNumber.HasValue)
            {
                stateParts.Add($"E{episodeNumber.Value}");
            }

            var statePrefix = stateParts.Count > 0 ? string.Join(" ", stateParts) : "";

            if (!string.IsNullOrWhiteSpace(episodeTitle))
            {
                var cleanTitle = episodeTitle
                                 .Replace($"Bölüm: {episodeNumber}", "", StringComparison.OrdinalIgnoreCase)
                                 .Replace($"Bölüm {episodeNumber}", "", StringComparison.OrdinalIgnoreCase)
                                 .Trim();

                if (cleanTitle.StartsWith("-"))
                {
                    cleanTitle = cleanTitle.TrimStart('-', ' ').Trim();
                }

                if (!string.IsNullOrWhiteSpace(cleanTitle) && cleanTitle != episodeNumber?.ToString())
                {
                    stateText = !string.IsNullOrEmpty(statePrefix) ? $"{statePrefix} - {cleanTitle}" : cleanTitle;
                }
                else
                {
                    stateText = !string.IsNullOrEmpty(statePrefix) ? statePrefix : $"Bölüm {episodeNumber}";
                }
            }
            else
            {
                stateText = !string.IsNullOrEmpty(statePrefix) ? statePrefix : "Film / Özel";
            }

            stateText = Truncate(stateText);

            var hoverParts = new List<string>();
            if (season is > 0)
            {
                hoverParts.Add($"Sezon {season.Value}");
            }

            if (episodeNumber.HasValue)
            {
                hoverParts.Add($"Bölüm {episodeNumber.Value}");
            }

            var largeText = hoverParts.Count > 0 ? string.Join(", ", hoverParts) : animeTitle;
            var coverUrl  = GetCoverUrl(posterUrl);

            var activity = new DiscordActivity
            {
                Details = detailsText,
                State   = stateText,
                Type    = 3, // watching
                Buttons =
                [
                    new DiscordButton
                    {
                        Label = "GitHub'da İncele",
                        Url   = GitHubUrl
                    }
                ],
                Assets = new DiscordAssets
                {
                    LargeImage = coverUrl,
                    LargeText  = Truncate(largeText)
                }
            };

            if (isPaused)
            {
                activity.Assets.SmallImage = PauseIcon;
                activity.Assets.SmallText  = "Duraklatıldı";
            }
            else if (!string.IsNullOrWhiteSpace(providerName))
            {
                var domain = GetProviderDomain(providerName);
                if (!string.IsNullOrWhiteSpace(domain))
                {
                    activity.Assets.SmallImage = $"https://www.google.com/s2/favicons?domain={domain}&sz=128";
                    activity.Assets.SmallText  = Truncate($"Oynatılıyor ({providerName})");
                }
            }

            if (!isPaused)
            {
                if (totalDurationSeconds is > 0)
                {
                    var currentPos = currentPositionSeconds ?? 0;
                    var remaining  = Math.Max(0, totalDurationSeconds.Value - currentPos);
                    activity.Timestamps = new DiscordTimestamps
                    {
                        Start = DateTimeOffset.UtcNow.AddSeconds(-currentPos).ToUnixTimeSeconds(),
                        End   = DateTimeOffset.UtcNow.AddSeconds(remaining).ToUnixTimeSeconds()
                    };
                }
                else
                {
                    activity.Timestamps = new DiscordTimestamps
                    {
                        Start = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    };
                }
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await _client.SetActivityAsync(activity);
                }
                catch (Exception ex)
                {
                    Log($"SetActivity (Playback) hatası: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            Log($"UpdatePlaybackPresence hatası: {ex.Message}");
        }
    }

    public void UpdateNavigationPresence(string state, string? details = null)
    {
        EnsureInitialized();
        if (_client == null || !_configService.Config.EnableDiscordRpc)
        {
            return;
        }

        try
        {
            var activity = new DiscordActivity
            {
                Details = Truncate(details ?? "Ana Menü"),
                State   = Truncate(state),
                Type    = 3, // watching
                Timestamps = new DiscordTimestamps
                {
                    Start = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                },
                Assets = new DiscordAssets
                {
                    LargeImage = DefaultCover,
                    LargeText  = "Migurdex"
                },
                Buttons =
                [
                    new DiscordButton
                    {
                        Label = "GitHub'da İncele",
                        Url   = GitHubUrl
                    }
                ]
            };

            _ = Task.Run(async () =>
            {
                try
                {
                    await _client.SetActivityAsync(activity);
                }
                catch (Exception ex)
                {
                    Log($"SetActivity (Navigation) hatası: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            Log($"UpdateNavigationPresence hatası: {ex.Message}");
        }
    }

    public void ClearPresence()
    {
        try
        {
            if (_client != null)
            {
                try
                {
                    _client.ClearActivityAsync().Wait(TimeSpan.FromMilliseconds(500));
                }
                catch
                {
                    // ignored
                }
            }
        }
        catch (Exception ex)
        {
            Log($"ClearPresence hatası: {ex.Message}");
        }
    }

    public void Dispose()
    {
        try
        {
            ClearPresence();
            _client?.Dispose();
            _client = null;
        }
        catch (Exception ex)
        {
            Log($"Dispose hatası: {ex.Message}");
        }
    }

    private void Initialize()
    {
        if (!_configService.Config.EnableDiscordRpc)
        {
            return;
        }

        try
        {
            _client = new DiscordIpcClient(DefaultClientId, Log);

            _ = Task.Run(async () =>
            {
                try
                {
                    await _client.EnsureConnectedAsync();
                }
                catch (Exception ex)
                {
                    Log($"Başlangıç bağlantı denemesi hatası: {ex.Message}");
                }
            });

            _ = Task.Run(async () =>
            {
                try
                {
                    var providersResult = await _apiService.GetProvidersAsync();
                    _cachedProviders = [.. providersResult.Data];
                }
                catch
                {
                    // ignored
                }
            });
        }
        catch (Exception ex)
        {
            Log($"Initialize hatası: {ex.Message}");
            _client = null;
        }
    }

    private static string GetCoverUrl(string? posterUrl)
    {
        if (string.IsNullOrWhiteSpace(posterUrl) || !Uri.TryCreate(posterUrl, UriKind.Absolute, out var uri))
        {
            return DefaultCover;
        }

        if (IsGlobalCdnImage(posterUrl))
        {
            return posterUrl;
        }

        var proxyHost = uri.Host.Replace(".", "-") + ".translate.goog";
        var separator = string.IsNullOrEmpty(uri.Query) ? "?" : "&";
        var translateUrl =
            $"https://{proxyHost}{uri.PathAndQuery}{separator}_x_tr_sl=tr&_x_tr_tl=ja&_x_tr_hl=tr&_x_tr_pto=wapp";

        return $"https://wsrv.nl/?url={translateUrl}";
    }

    private string? GetProviderDomain(string? providerName)
    {
        if (string.IsNullOrWhiteSpace(providerName))
        {
            return null;
        }

        if (_cachedProviders != null)
        {
            var clean = providerName.ToLowerInvariant().Replace(" ", "");
            var provider = _cachedProviders.FirstOrDefault(p =>
                                                               p.Name.Equals(
                                                                   providerName,
                                                                   StringComparison.OrdinalIgnoreCase)
                                                               || p.Name.ToLowerInvariant()
                                                                   .Replace(" ", "")
                                                                   .Equals(clean));

            if (provider != null && !string.IsNullOrWhiteSpace(provider.BaseUrl))
            {
                if (Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out var uri))
                {
                    return uri.Host;
                }
            }
        }

        return null;
    }

    private static bool IsGlobalCdnImage(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !IsValidHttpUrl(url))
        {
            return false;
        }

        var lower = url.ToLowerInvariant();
        return _globalCdns.Any(lower.Contains);
    }

    private static string Truncate(string? text, int maxLength = 128)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var trimmed = text.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..(maxLength - 3)] + "...";
    }

    private static bool IsValidHttpUrl(string? url)
    {
        return !string.IsNullOrWhiteSpace(url)
               && (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                   || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
    }

    private void EnsureInitialized()
    {
        if (!_configService.Config.EnableDiscordRpc)
        {
            if (_client != null)
            {
                ClearPresence();
                _client.Dispose();
                _client = null;
            }

            return;
        }

        if (_client == null)
        {
            Initialize();
        }
    }

    private void Log(string message)
    {
        try
        {
            var logDir = Path.Combine(_configService.ConfigDirectory, "logs");
            Directory.CreateDirectory(logDir);
            var logPath = Path.Combine(logDir, "rpc.log");

            lock (_rpcLogLock)
            {
                if (File.Exists(logPath) && new FileInfo(logPath).Length > 2 * 1024 * 1024)
                {
                    var oldLog = logPath + ".old";
                    if (File.Exists(oldLog))
                    {
                        File.Delete(oldLog);
                    }

                    File.Move(logPath, oldLog);
                }

                var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}";
                File.AppendAllText(logPath, line);
            }
        }
        catch
        {
            // ignored
        }
    }
}
