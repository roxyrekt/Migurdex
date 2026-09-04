using DiscordRPC;

namespace Migurdex.Cli.Services;

public class DiscordRpcService : IDiscordRpcService, IDisposable
{
    private const string DefaultClientId = "1406319525124768007";
    private const string DefaultCover    = "https://cdn.rcd.gg/PreMiD/websites/A/AniList/assets/logo.png";
    private const string PauseIcon       = "https://cdn.rcd.gg/PreMiD/resources/pause.png";

    private static readonly string[] _globalCdns =
    [
        "tmdb.org", "anilist.co", "myanimelist.net", "media.kitsu.io", "cdn.rcd.gg"
    ];

    private readonly IApiClientService     _apiService;
    private readonly IConfigurationService _configService;
    private          List<ProviderInfo>?   _cachedProviders;
    private          DiscordRpcClient?     _client;

    public DiscordRpcService(IConfigurationService configService, IApiClientService apiService)
    {
        _configService = configService;
        _apiService    = apiService;
        Initialize();
    }

    public void UpdatePresence(string title, string details, double? remainingSeconds = null)
    {
        UpdatePlaybackPresence(title, details, null, false, null, remainingSeconds.HasValue ? remainingSeconds : null);
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
            var detailsText = Truncate($"{animeTitle}");

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

            var presence = new RichPresence
            {
                Details    = detailsText,
                State      = stateText,
                Type       = ActivityType.Watching,
                DetailsUrl = "https://github.com/roxy/Migurdex"
            }.WithName(providerName ?? "Migurdex");

            presence.Buttons =
            [
                new Button
                {
                    Label = "GitHub'da İncele",
                    Url   = "https://github.com/roxy/Migurdex"
                }
            ];

            presence.Assets = new Assets();

            var coverUrl = GetCoverUrl(posterUrl);
            presence.Assets.LargeImageKey = coverUrl;

            var hoverParts = new List<string>();
            if (season is > 0)
            {
                hoverParts.Add($"Sezon {season.Value}");
            }

            if (episodeNumber.HasValue)
            {
                hoverParts.Add($"Bölüm {episodeNumber.Value}");
            }

            presence.Assets.LargeImageText = hoverParts.Count > 0 ? string.Join(", ", hoverParts) : animeTitle;

            if (isPaused)
            {
                presence.Assets.SmallImageKey  = PauseIcon;
                presence.Assets.SmallImageText = "Duraklatıldı";
            }
            else if (!string.IsNullOrWhiteSpace(providerName))
            {
                var domain = GetProviderDomain(providerName);
                if (!string.IsNullOrWhiteSpace(domain))
                {
                    presence.Assets.SmallImageKey  = $"https://www.google.com/s2/favicons?domain={domain}&sz=128";
                    presence.Assets.SmallImageText = $"Oynatılıyor ({providerName})";
                }
            }

            if (!isPaused)
            {
                if (totalDurationSeconds is > 0)
                {
                    var currentPos = currentPositionSeconds ?? 0;
                    var remaining  = Math.Max(0, totalDurationSeconds.Value - currentPos);
                    presence.Timestamps = new Timestamps
                    {
                        Start = DateTime.UtcNow.AddSeconds(-currentPos),
                        End   = DateTime.UtcNow.AddSeconds(remaining)
                    };
                }
                else
                {
                    presence.Timestamps = Timestamps.Now;
                }
            }

            _client.SetPresence(presence);
        }
        catch
        {
            // ignored
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
            var presence = new RichPresence
            {
                Details    = Truncate(details ?? "Ana Menü"),
                State      = Truncate(state),
                Type       = ActivityType.Watching,
                Timestamps = Timestamps.Now
            }.WithName("Migurdex");

            _client.SetPresence(presence);
        }
        catch
        {
            // ignored
        }
    }

    public void ClearPresence()
    {
        try
        {
            _client?.ClearPresence();
        }
        catch
        {
            // ignored
        }
    }

    public void Dispose()
    {
        try
        {
            _client?.Dispose();
        }
        catch
        {
            // ignored
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
            _client = new DiscordRpcClient(DefaultClientId);
            _client.Initialize();

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
        catch
        {
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

        if (_client is { IsInitialized: false })
        {
            try
            {
                _client.Dispose();
            }
            catch
            {
                // ignored
            }

            _client = null;
        }

        if (_client == null)
        {
            Initialize();
        }
    }
}
