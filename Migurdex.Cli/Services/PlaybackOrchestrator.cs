using Migurdex.Cli.Tui;
using Migurdex.Shared.Models;

namespace Migurdex.Cli.Services;

public sealed class PlaybackOrchestrator
{
    private readonly IHistoryService   _historyService;
    private readonly IMpvPlayerService _playerService;
    private readonly IServiceProvider  _serviceProvider;

    public PlaybackOrchestrator(
        IHistoryService   historyService,
        IMpvPlayerService playerService,
        IServiceProvider  serviceProvider)
    {
        _historyService  = historyService;
        _playerService   = playerService;
        _serviceProvider = serviceProvider;
    }

    public WatchHistoryEntry BuildEntry(
        string  provider,
        string  animeId,
        string  animeTitle,
        Episode episode,
        string? posterUrl)
    {
        var entry = new WatchHistoryEntry
        {
            AnimeId       = animeId,
            AnimeTitle    = animeTitle,
            ProviderName  = provider,
            EpisodeId     = episode.Id,
            EpisodeTitle  = episode.Title ?? $"Bölüm {episode.Number}",
            PosterUrl     = posterUrl ?? string.Empty,
            Season        = episode.Season ?? 1,
            EpisodeNumber = episode.Number
        };

        var existing = _historyService.GetWatchHistory()
                                      .FirstOrDefault(h => h.AnimeId == animeId
                                                           && h.EpisodeId == episode.Id
                                                           && h.ProviderName == provider);
        if (existing is not null)
        {
            entry.LastPositionSeconds  = existing.LastPositionSeconds;
            entry.TotalDurationSeconds = existing.TotalDurationSeconds;
        }

        return entry;
    }

    public async Task<SyncOutcome> PlayAndNotifyAsync(
        VideoSource       source,
        WatchHistoryEntry entry,
        bool              showToast         = true,
        CancellationToken cancellationToken = default)
    {
        var outcome = await _playerService.PlayAsync(
                          source.Url,
                          entry,
                          source.Headers,
                          source.Subtitles,
                          cancellationToken);

        await SyncAmbiguityPrompt.HandleAfterPlaybackAsync(_serviceProvider, outcome);
        if (showToast)
        {
            SyncAmbiguityPrompt.ShowPlaybackNotification(outcome);
        }

        return outcome;
    }
}
