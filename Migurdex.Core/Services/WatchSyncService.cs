using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Models;
using System.Text.Json;

namespace Migurdex.Core.Services;

public sealed class SyncQueueItem
{
    public string   Provider         { get; set; } = string.Empty;
    public string   AnimeTitle       { get; set; } = string.Empty;
    public string   AnimeId          { get; set; } = string.Empty;
    public int      Season           { get; set; } = 1;
    public double   Episode          { get; set; }
    public bool     IsCompleted      { get; set; }
    public int      Attempts         { get; set; }
    public DateTime NextAttemptAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime EnqueuedAtUtc    { get; set; } = DateTime.UtcNow;
}

public sealed class WatchSyncService
{
    public const string AniListProviderName = "anilist";
    public const string MalProviderName     = "mal";
    public const string ProviderName        = AniListProviderName;
    public const int    MaxAttempts         = 7;
    public const int    MaxQueueSize        = 200;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true
    };

    private readonly AniListListClient _aniListClient;

    private readonly Func<string, string, int, double, string?, CancellationToken, Task<EpisodeMappingResult>>
        _episodeMapper;

    private readonly string                    _filePath;
    private readonly Lock                      _lock = new();
    private readonly ILogger<WatchSyncService> _logger;
    private readonly MalListClient?            _malClient;
    private readonly OAuthTokenStore           _tokenStore;
    private          List<SyncQueueItem>       _queue = [];

    public WatchSyncService(AniListListClient                                                     aniListClient,
        OAuthTokenStore                                                                           tokenStore,
        Func<string, string, int, double, string?, CancellationToken, Task<EpisodeMappingResult>> episodeMapper,
        string?                                                                                   queueDirectory = null,
        ILogger<WatchSyncService>?                                                                logger         = null,
        MalListClient?                                                                            malClient      = null)
    {
        _aniListClient = aniListClient;
        _malClient     = malClient;
        _tokenStore    = tokenStore;
        _episodeMapper = episodeMapper;

        var dir = queueDirectory
                  ?? Path.Combine(
                      Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                      ".config",
                      "migurdex");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "sync_queue.json");
        _logger   = logger ?? NullLogger<WatchSyncService>.Instance;
        Load();
    }

    public int QueuedCount
    {
        get
        {
            lock (_lock)
            {
                return _queue.Count;
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
                    RemoveKey(Key(entry.Provider, entry.AnimeId, entry.Season));
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
                due = _queue.Where(i => i.NextAttemptAtUtc <= DateTime.UtcNow).ToList();
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
                    RemoveKey(Key(item.Provider, item.AnimeId, item.Season));
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

    private static string Key(string provider, string animeId, int season)
    {
        return $"{provider.Trim().ToLowerInvariant()}:{animeId.Trim().ToLowerInvariant()}:s{season}";
    }

    private void Enqueue(SyncWatchEntry entry)
    {
        lock (_lock)
        {
            var key      = Key(entry.Provider, entry.AnimeId, entry.Season);
            var existing = _queue.FirstOrDefault(i => Key(i.Provider, i.AnimeId, i.Season) == key);
            if (existing is not null)
            {
                if (entry.Episode > existing.Episode)
                {
                    existing.Episode = entry.Episode;
                }

                existing.IsCompleted      = existing.IsCompleted || entry.IsCompleted;
                existing.NextAttemptAtUtc = DateTime.UtcNow;
                existing.EnqueuedAtUtc    = DateTime.UtcNow;
            }
            else
            {
                _queue.Add(new SyncQueueItem
                {
                    Provider    = entry.Provider,
                    AnimeTitle  = entry.AnimeTitle,
                    AnimeId     = entry.AnimeId,
                    Season      = entry.Season,
                    Episode     = entry.Episode,
                    IsCompleted = entry.IsCompleted
                });

                while (_queue.Count > MaxQueueSize)
                {
                    _queue.RemoveAt(0);
                }
            }

            Save();
        }
    }

    private void MarkFailed(SyncQueueItem item)
    {
        lock (_lock)
        {
            item.Attempts++;
            if (item.Attempts >= MaxAttempts)
            {
                _queue.Remove(item);
                _logger.LogWarning("Sync denemesi {Count} kez başarısız; kayıt düşürüldü", MaxAttempts);
            }
            else
            {
                var delayMinutes = Math.Min(1 << item.Attempts, 60);
                item.NextAttemptAtUtc = DateTime.UtcNow.AddMinutes(delayMinutes);
            }

            Save();
        }
    }

    private void RemoveKey(string key)
    {
        lock (_lock)
        {
            if (_queue.RemoveAll(i => Key(i.Provider, i.AnimeId, i.Season) == key) > 0)
            {
                Save();
            }
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return;
            }

            var json   = File.ReadAllText(_filePath);
            var loaded = JsonSerializer.Deserialize<List<SyncQueueItem>>(json, JsonOpts);
            if (loaded is not null)
            {
                _queue = loaded;
            }
        }
        catch
        {
            _queue = [];
        }
    }

    private void Save()
    {
        try
        {
            var                 tmp = _filePath + ".tmp";
            List<SyncQueueItem> snapshot;
            lock (_lock)
            {
                snapshot = [.. _queue];
            }

            File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot, JsonOpts));
            File.Move(tmp, _filePath, true);
        }
        catch
        {
            // ignored
        }
    }
}
