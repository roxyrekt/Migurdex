namespace Migurdex.Cli.Services;

public interface IDiscordRpcService
{
    void UpdatePresence(string title, string details, double? remainingSeconds = null);

    void UpdatePlaybackPresence(
        string  animeTitle,
        string  episodeTitle,
        string? posterUrl              = null,
        bool    isPaused               = false,
        double? currentPositionSeconds = null,
        double? totalDurationSeconds   = null,
        string? providerName           = null,
        int?    season                 = null,
        double? episodeNumber          = null);

    void UpdateNavigationPresence(string state, string? details = null);
    void ClearPresence();
}
