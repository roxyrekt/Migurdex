using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Migurdex.Core.Database;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Models;

namespace Migurdex.Core.Services;

public sealed class WatchSyncService
{
    public const string AniListProviderName = "anilist";
    public const string MalProviderName     = "mal";
    public const string ProviderName        = AniListProviderName;
    public const int    MaxAttempts         = 7;
    public const int    MaxQueueSize        = 200;

    private readonly AniListListClient _aniListClient;

    private readonly Func<string, string, int, double, string?, CancellationToken, Task<EpisodeMappingResult>>
        _episodeMapper;

    private readonly MigurdexDatabase          _db;
    private readonly Lock                      _lock = new();
    private readonly ILogger<WatchSyncService> _logger;
    private readonly MalListClient?            _malClient;
    private readonly OAuthTokenStore           _tokenStore;

    public WatchSyncService(AniListListClient                                                     aniListClient,
        OAuthTokenStore                                                                           tokenStore,
        Func<string, string, int, double, string?, CancellationToken, Task<EpisodeMappingResult>> episodeMapper,
        MigurdexDatabase                                                                          database,
        ILogger<WatchSyncService>?                                                                logger    = null,
        MalListClient?                                                                            malClient = null)
    {
        _aniListClient = aniListClient;
        _malClient     = malClient;
        _tokenStore    = tokenStore;
        _episodeMapper = episodeMapper;
        _db            = database;
        _logger        = logger ?? NullLogger<WatchSyncService>.Instance;
    }

    public WatchSyncService(AniListListClient                                                     aniListClient,
        OAuthTokenStore                                                                           tokenStore,
        Func<string, string, int, double, string?, CancellationToken, Task<EpisodeMappingResult>> episodeMapper,
        string?                                                                                   queueDirectory = null,
        ILogger<WatchSyncService>?                                                                logger         = null,
        MalListClient?                                                                            malClient      = null)
        : this(aniListClient, tokenStore, episodeMapper, new MigurdexDatabase(queueDirectory), logger, malClient)
    {
    }

    public int QueuedCount
    {
        get
        {
            lock (_lock)
            {
                return _db.GetSyncQueueCount();
            }
        }
    }

    public async Task<SyncOutcome> SyncAsync(SyncWatchEntry entry, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!IsLoggedIn())
            {
                Enqueue(entry);
                return new SyncOutcome
                {
                    Kind  = SyncOutcomeKind.SkippedNoToken,
                    Entry = entry
                };
            }

            var mapped = await MapAsync(entry, cancellationToken);
            if (mapped.Mapping is not null)
            {
                var syncedTo = new List<string>();
                var progress = (int) Math.Floor(mapped.Mapping.Episode);
                if (await TryPushMappingAsync(entry, mapped.Mapping, cancellationToken, syncedTo))
                {
                    lock (_lock)
                    {
                        _db.RemoveSyncItem(entry.Provider, entry.AnimeId, entry.Season);
                    }

                    return new SyncOutcome
                    {
                        Kind     = SyncOutcomeKind.Pushed,
                        Entry    = entry,
                        Progress = progress,
                        SyncedTo = syncedTo
                    };
                }

                Enqueue(entry);
                return new SyncOutcome
                {
                    Kind     = SyncOutcomeKind.Queued,
                    Entry    = entry,
                    Progress = progress,
                    SyncedTo = syncedTo
                };
            }

            Enqueue(entry);
            return new SyncOutcome
            {
                Kind       = mapped.Ambiguous ? SyncOutcomeKind.Ambiguous : SyncOutcomeKind.Queued,
                Entry      = entry,
                Candidates = mapped.Candidates
            };
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Watch sync hata verdi; kuyruğa alındı");
            Enqueue(entry);
            return new SyncOutcome
            {
                Kind  = SyncOutcomeKind.Queued,
                Entry = entry
            };
        }
    }

    public async Task<int> FlushQueueAsync(CancellationToken cancellationToken = default)
    {
        var pushed = 0;

        try
        {
            if (!IsLoggedIn())
            {
                return 0;
            }

            List<SyncQueueItem> due;
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                due = _db.GetSyncQueue()
                         .Where(i => i.NextAttemptAtUtc <= now)
                         .ToList();
            }

            foreach (var item in due)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                var entry = new SyncWatchEntry
                {
                    Provider    = item.Provider,
                    AnimeTitle  = item.AnimeTitle,
                    AnimeId     = item.AnimeId,
                    Season      = item.Season,
                    Episode     = item.Episode,
                    IsCompleted = item.IsCompleted
                };

                if (await TryPushAsync(entry, cancellationToken))
                {
                    lock (_lock)
                    {
                        _db.RemoveSyncItemById(item.Id);
                    }

                    pushed++;
                }
                else
                {
                    MarkFailed(item);
                }
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Sync kuyruğu boşaltılamadı");
        }

        return pushed;
    }

    private bool IsLoggedIn()
    {
        return IsAniListLoggedIn() || IsMalLoggedIn();
    }

    public bool IsAniListLoggedIn()
    {
        return _tokenStore.TryGet(AniListProviderName, out var token)
               && token is not null
               && !string.IsNullOrEmpty(token.AccessToken);
    }

    public bool IsMalLoggedIn()
    {
        return _tokenStore.TryGet(MalProviderName, out var token)
               && token is not null
               && !string.IsNullOrEmpty(token.AccessToken);
    }

    private async Task<bool> TryPushAsync(SyncWatchEntry entry, CancellationToken cancellationToken)
    {
        var mapped = await MapAsync(entry, cancellationToken);
        return mapped.Mapping is not null
               && await TryPushMappingAsync(entry, mapped.Mapping, cancellationToken);
    }

    private async Task<EpisodeMappingResult> MapAsync(SyncWatchEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            return await _episodeMapper(entry.Provider,
                                        entry.AnimeId,
                                        entry.Season,
                                        entry.Episode,
                                        entry.AnimeTitle,
                                        cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Bölüm eşlenemedi; kuyrukta tekrar denenecek");
            return new EpisodeMappingResult();
        }
    }

    private async Task<bool> TryPushMappingAsync(SyncWatchEntry entry,
        TrackerEpisodeMapping                                   mapping,
        CancellationToken                                       cancellationToken,
        List<string>?                                           pushedTrackers = null)
    {
        var progress = (int) Math.Floor(mapping.Episode);
        if (progress < 1)
        {
            return false;
        }

        var isFinale     = IsSeasonFinale(entry, mapping, progress);
        var anyPushed    = false;
        var anyFailed    = false;
        var anyAttempted = false;

        if (IsAniListLoggedIn())
        {
            if (int.TryParse(mapping.AniListId, out var mediaId) && mediaId > 0)
            {
                anyAttempted = true;
                var status = isFinale ? AniListListStatus.Completed : AniListListStatus.Current;
                if (await _aniListClient.UpdateProgressAsync(mediaId, progress, status, cancellationToken))
                {
                    anyPushed = true;
                    pushedTrackers?.Add("AniList");
                }
                else
                {
                    anyFailed = true;
                }
            }
            else
            {
                _logger.LogInformation("AniList sync atlandı: bu içerik için AniList ID bulunamadı");
            }
        }

        if (IsMalLoggedIn() && _malClient is not null)
        {
            if (!string.IsNullOrWhiteSpace(mapping.MyAnimeListId)
                && int.TryParse(mapping.MyAnimeListId, out var malId)
                && malId > 0)
            {
                anyAttempted = true;
                if (await _malClient.UpdateProgressAsync(malId, progress, isFinale, cancellationToken))
                {
                    anyPushed = true;
                    pushedTrackers?.Add("MyAnimeList");
                }
                else
                {
                    anyFailed = true;
                }
            }
            else
            {
                _logger.LogInformation("MyAnimeList sync atlandı: bu içerik için MAL ID bulunamadı");
            }
        }

        return anyAttempted && anyPushed && !anyFailed;
    }

    private static bool IsSeasonFinale(SyncWatchEntry entry, TrackerEpisodeMapping mapping, int progress)
    {
        return entry.IsCompleted
               && !mapping.IsOverflow
               && mapping.TotalEpisodes.HasValue
               && progress >= mapping.TotalEpisodes.Value;
    }

    private void Enqueue(SyncWatchEntry entry)
    {
        lock (_lock)
        {
            _db.EnqueueSyncItem(new SyncQueueItem
            {
                Provider         = entry.Provider,
                AnimeTitle       = entry.AnimeTitle,
                AnimeId          = entry.AnimeId,
                Season           = entry.Season,
                Episode          = entry.Episode,
                IsCompleted      = entry.IsCompleted,
                Attempts         = 0,
                NextAttemptAtUtc = DateTime.UtcNow,
                EnqueuedAtUtc    = DateTime.UtcNow
            });
        }
    }

    private void MarkFailed(SyncQueueItem item)
    {
        lock (_lock)
        {
            item.Attempts++;
            if (item.Attempts >= MaxAttempts)
            {
                _db.RemoveSyncItemById(item.Id);
                _logger.LogWarning("Sync denemesi {Count} kez başarısız; kayıt düşürüldü", MaxAttempts);
            }
            else
            {
                var delayMinutes = Math.Min(1 << item.Attempts, 60);
                item.NextAttemptAtUtc = DateTime.UtcNow.AddMinutes(delayMinutes);
                _db.UpdateSyncItem(item);
            }
        }
    }
}
