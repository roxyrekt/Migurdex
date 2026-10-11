using Migurdex.Cli.Services;
using System.Collections.Concurrent;

namespace Migurdex.Cli.Services.Downloads;

public enum BackgroundJobState
{
    Queued,
    Active,
    Paused,
    Done,
    Failed,
    Cancelled
}

public sealed class BackgroundJob
{
    public          Guid                    Id                  { get; init; } = Guid.NewGuid();
    public required string                  Title               { get; init; }
    public required string                  Label               { get; init; }
    public          BackgroundJobState      State               { get; set; } = BackgroundJobState.Queued;
    public          double                  Percent             { get; set; }
    public          long                    DownloadedBytes     { get; set; }
    public          long?                   TotalBytes          { get; set; }
    public          long                    ResumeBaselineBytes { get; set; }
    public          string                  Detail              { get; set; } = "sırada";
    public          string?                 MediaPath           { get; set; }
    public          string?                 Error               { get; set; }
    public          DateTimeOffset          EnqueuedAt          { get; } = DateTimeOffset.UtcNow;
    public          DateTimeOffset?         FinishedAt          { get; set; }
    public          bool                    Seen                { get; set; }
    public          CancellationTokenSource Cts                 { get; private set; } = new();

    public Func<JobControl, Task>? WorkFactory { get; set; }

    public void ResetForResume()
    {
        try { Cts.Dispose(); }
        catch
        {
            // ignored
        }

        Cts        = new CancellationTokenSource();
        State      = BackgroundJobState.Queued;
        FinishedAt = null;
        Seen       = false;
        Error      = null;
    }
}

public static class JobProgressText
{
    public static (double Percent, string Detail) For(DownloadProgress value, double currentPercent = 0)
    {
        var percent = value.Stage switch
        {
            DownloadStage.Completed => 100,
            DownloadStage.Downloading => value.Percent
                                         ?? (value.TotalBytes is > 0
                                                 ? Math.Min(100, value.BytesDownloaded * 100.0 / value.TotalBytes.Value)
                                                 : currentPercent),
            _ => currentPercent
        };

        var stage = value.Stage switch
        {
            DownloadStage.Preparing   => "Hazırlanıyor",
            DownloadStage.Requesting  => "Bağlanıyor",
            DownloadStage.Downloading => "İndiriliyor",
            DownloadStage.Finalizing  => "Tamamlanıyor",
            DownloadStage.Subtitle    => "Altyazı",
            DownloadStage.Completed   => "Tamamlandı",
            DownloadStage.Failed      => "Hata",
            DownloadStage.Cancelled   => "İptal",
            _                         => value.Stage.ToString()
        };

        var detail = stage;
        if (value.Stage == DownloadStage.Downloading)
        {
            if (value.SpeedBytesPerSecond is > 0)
            {
                detail += $" • {FormatBytes((long) value.SpeedBytesPerSecond.Value)}/s";
            }

            if (value.Eta is not null)
            {
                detail += $" • {DownloadSpeedometer.FormatEta(value.Eta.Value)}";
            }
        }

        return (percent, detail);
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double   value = Math.Max(0, bytes);
        var      unit  = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} {units[unit]}" : $"{value:0.##} {units[unit]}";
    }
}

public sealed class JobControl
{
    private readonly BackgroundJob _job;

    public JobControl(BackgroundJob job)
    {
        _job     = job;
        Progress = new JobProgressSink(job);
    }

    public CancellationToken Token => _job.Cts.Token;

    public BackgroundJob Job => _job;

    public IProgress<DownloadProgress> Progress { get; }

    private sealed class JobProgressSink(BackgroundJob job) : IProgress<DownloadProgress>
    {
        private readonly DownloadSpeedometer _speed = new();

        public void Report(DownloadProgress value)
        {
            _speed.Sample(value.BytesDownloaded, DateTimeOffset.UtcNow);
            var enriched = value;
            if (value.SpeedBytesPerSecond is null or <= 0 && _speed.BytesPerSecond > 0)
            {
                enriched = new DownloadProgress(
                    value.Stage,
                    value.BytesDownloaded,
                    value.TotalBytes,
                    _speed.BytesPerSecond,
                    value.FragmentsDone,
                    value.FragmentsTotal,
                    value.Eta
                    ?? _speed.EstimateRemaining(value.BytesDownloaded, value.TotalBytes, _speed.BytesPerSecond),
                    value.Percent,
                    value.Track,
                    value.IsAudioTrack);
            }

            var (percent, detail) = JobProgressText.For(enriched, job.Percent);
            job.Percent           = percent;
            job.Detail            = detail;

            if (value.Stage == DownloadStage.Downloading || value.TotalBytes is > 0)
            {
                job.DownloadedBytes = value.BytesDownloaded;
                if (value.TotalBytes is > 0)
                {
                    job.TotalBytes = value.TotalBytes;
                }
            }
        }
    }
}

public sealed class BackgroundDownloadTracker : IDisposable
{
    private readonly ConcurrentDictionary<Guid, BackgroundJob> _jobs = new();
    private readonly ConcurrentDictionary<Guid, Migurdex.Core.Database.StoredDownloadJob> _specs = new();
    private readonly HashSet<Guid> _removeWhenSettled = new();
    private readonly SemaphoreSlim _episodeGate;
    private readonly Dictionary<string, SemaphoreSlim> _hosterGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();
    private readonly int _perHosterCap;
    private          bool _disposed;

    public BackgroundDownloadTracker(IConfigurationService configService)
        : this(Math.Clamp(configService.Config.MaxConcurrentEpisodes, 1, 5),
               Math.Clamp(configService.Config.MaxSocketsPerHoster, 1, 32),
               Math.Clamp(configService.Config.ConnectionsPerFile, 1, 8))
    {
    }

    public BackgroundDownloadTracker(int maxEpisodes, int maxSocketsPerHoster, int connectionsPerFile)
    {
        _episodeGate  = new SemaphoreSlim(Math.Clamp(maxEpisodes, 1, 5), 5);
        _perHosterCap = Math.Max(1, Math.Clamp(maxSocketsPerHoster, 1, 32) / Math.Max(1, connectionsPerFile));
    }

    public int PerHosterCap => _perHosterCap;

    public IDownloadJobStore? Store { get; set; }

    public int PendingRestore { get; private set; }

    public SemaphoreSlim HosterGate(string hoster)
    {
        lock (_gate)
        {
            if (!_hosterGates.TryGetValue(hoster, out var semaphore))
            {
                semaphore            = new SemaphoreSlim(_perHosterCap, _perHosterCap);
                _hosterGates[hoster] = semaphore;
            }

            return semaphore;
        }
    }

    public Guid Enqueue(string                                title,
        string                                                label,
        Func<JobControl, Task>                                work,
        Func<Guid, Migurdex.Core.Database.StoredDownloadJob>? describe = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        var job = new BackgroundJob
        {
            Title       = title,
            Label       = label,
            WorkFactory = work
        };
        _jobs[job.Id] = job;
        if (describe is not null)
        {
            try
            {
                _specs[job.Id] = describe(job.Id);
                Persist(job);
            }
            catch
            {
                // ignored
            }
        }

        RunJob(job);

        return job.Id;
    }

    public int RestorePending(Func<Migurdex.Core.Database.StoredDownloadJob, Func<JobControl, Task>?> rehydrate)
    {
        ArgumentNullException.ThrowIfNull(rehydrate);
        if (Store is null)
        {
            return 0;
        }

        List<Migurdex.Core.Database.StoredDownloadJob> rows;
        try
        {
            rows = Store.LoadAll();
        }
        catch
        {
            return 0;
        }

        var restored = 0;
        foreach (var row in rows)
        {
            if (_jobs.ContainsKey(row.Id))
            {
                continue;
            }

            Func<JobControl, Task>? work;
            try
            {
                work = rehydrate(row);
            }
            catch
            {
                continue;
            }

            if (work is null)
            {
                continue;
            }

            var job = new BackgroundJob
            {
                Id          = row.Id,
                Title       = row.AnimeTitle,
                Label       = $"Bölüm {row.Number:0.##}",
                WorkFactory = work,
                State       = BackgroundJobState.Paused,
                Detail      = "duraklatıldı",
                Error       = row.Error,
                MediaPath   = row.MediaPath,
                Seen        = true
            };

            var (onDisk, total) = ResumeProgressProbe.Probe(
                row.OutputDirectory,
                row.AnimeTitle,
                row.EpisodeTitle,
                row.Season,
                row.Number);
            job.DownloadedBytes = onDisk;
            job.TotalBytes      = total;
            if (total is > 0 && onDisk > 0)
            {
                job.Percent = Math.Min(100, onDisk * 100.0 / total.Value);
            }

            _jobs[job.Id] = job;
            _specs[job.Id] = row with
            {
                State = "paused",
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            Persist(job);
            restored++;
        }

        PendingRestore += restored;

        return restored;
    }

    public int TakePendingRestore()
    {
        var count = PendingRestore;
        PendingRestore = 0;

        return count;
    }

    public bool Pause(Guid id)
    {
        if (!_jobs.TryGetValue(id, out var job))
        {
            return false;
        }

        lock (job)
        {
            if (job.State is not (BackgroundJobState.Queued or BackgroundJobState.Active))
            {
                return false;
            }

            job.State  = BackgroundJobState.Paused;
            job.Detail = "duraklatıldı";
        }

        Persist(job);
        try { job.Cts.Cancel(); }
        catch
        {
            // ignored
        }

        return true;
    }

    public bool Resume(Guid id)
    {
        if (!_jobs.TryGetValue(id, out var job))
        {
            return false;
        }

        lock (job)
        {
            if (job.State != BackgroundJobState.Paused || job.WorkFactory is null)
            {
                return false;
            }

            job.ResetForResume();
        }

        Persist(job);
        RunJob(job);

        return true;
    }

    public bool Retry(Guid id)
    {
        if (!_jobs.TryGetValue(id, out var job))
        {
            return false;
        }

        lock (job)
        {
            if (job.State != BackgroundJobState.Failed || job.WorkFactory is null)
            {
                return false;
            }

            job.ResetForResume();
        }

        Persist(job);
        RunJob(job);

        return true;
    }

    public int ResumeAllPaused()
    {
        var resumed = 0;
        foreach (var job in _jobs.Values)
        {
            if (Resume(job.Id))
            {
                resumed++;
            }
        }

        return resumed;
    }

    private void RunJob(BackgroundJob job)
    {
        ArgumentNullException.ThrowIfNull(job.WorkFactory);
        var control = new JobControl(job);
        _ = Task.Run(async () =>
        {
            await _episodeGate.WaitAsync(control.Token).ConfigureAwait(false);
            try
            {
                lock (job)
                {
                    if (job.State == BackgroundJobState.Queued)
                    {
                        job.State  = BackgroundJobState.Active;
                        job.Detail = "başlıyor…";
                    }
                }

                Persist(job);
                await job.WorkFactory(control).ConfigureAwait(false);
                var completed = false;
                lock (job)
                {
                    if (job.State == BackgroundJobState.Active)
                    {
                        completed = true;
                    }
                }

                if (completed)
                {
                    Forget(job.Id);
                    lock (job)
                    {
                        if (job.State == BackgroundJobState.Active)
                        {
                            job.State = BackgroundJobState.Done;
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (control.Token.IsCancellationRequested)
            {
                var cancelled = false;
                lock (job)
                {
                    if (job.State != BackgroundJobState.Paused)
                    {
                        cancelled = true;
                    }
                }

                if (cancelled)
                {
                    Forget(job.Id);
                    lock (job)
                    {
                        if (job.State != BackgroundJobState.Paused)
                        {
                            job.State  = BackgroundJobState.Cancelled;
                            job.Detail = "iptal edildi";
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                lock (job)
                {
                    job.State  =   BackgroundJobState.Failed;
                    job.Error  ??= ex.Message;
                    job.Detail =   "hata";
                }

                Persist(job);
            }
            finally
            {
                lock (job)
                {
                    if (job.State != BackgroundJobState.Paused)
                    {
                        job.FinishedAt = DateTimeOffset.UtcNow;
                    }

                    job.Seen = false;
                }

                TryAutoRemove(job.Id);
                _episodeGate.Release();
            }
        });
    }

    private void Persist(BackgroundJob job)
    {
        var store = Store;
        if (store is null || !_specs.TryGetValue(job.Id, out var spec))
        {
            return;
        }

        try
        {
            store.Upsert(spec with
            {
                State = StateName(job.State),
                Error = job.Error,
                MediaPath = job.MediaPath,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            });
        }
        catch
        {
            // ignored
        }
    }

    public void RemoveWhenSettled(Guid id)
    {
        if (!_jobs.TryGetValue(id, out var job))
        {
            return;
        }

        bool settled;
        lock (job)
        {
            settled = job.State
                          is not (BackgroundJobState.Queued or BackgroundJobState.Active);
        }

        if (settled)
        {
            Remove(id);
            return;
        }

        lock (_gate)
        {
            _removeWhenSettled.Add(id);
        }
    }

    private void TryAutoRemove(Guid id)
    {
        lock (_gate)
        {
            if (!_removeWhenSettled.Remove(id))
            {
                return;
            }
        }

        if (!_jobs.TryGetValue(id, out var job))
        {
            return;
        }

        lock (job)
        {
            if (job.State is not (BackgroundJobState.Done or BackgroundJobState.Failed or BackgroundJobState.Cancelled))
            {
                return;
            }
        }

        Remove(id);
    }

    private void Forget(Guid id)
    {
        _specs.TryRemove(id, out _);
        try
        {
            Store?.Delete(id);
        }
        catch
        {
            // ignored
        }
    }

    private static string StateName(BackgroundJobState state)
    {
        return state switch
        {
            BackgroundJobState.Queued    => "queued",
            BackgroundJobState.Active    => "active",
            BackgroundJobState.Paused    => "paused",
            BackgroundJobState.Done      => "done",
            BackgroundJobState.Failed    => "failed",
            BackgroundJobState.Cancelled => "cancelled",
            _                            => "queued"
        };
    }

    public IReadOnlyList<BackgroundJob> Snapshot()
    {
        return _jobs.Values
                    .OrderBy(j => j.State is BackgroundJobState.Queued or BackgroundJobState.Active
                                      ? 0
                                      : j.State == BackgroundJobState.Paused
                                          ? 1
                                          : 2)
                    .ThenBy(j => j.EnqueuedAt)
                    .ToList();
    }

    public int ActiveCount =>
        _jobs.Values.Count(j => j.State is BackgroundJobState.Queued or BackgroundJobState.Active);

    public int TakeUnseenFinished(out List<BackgroundJob> finished)
    {
        finished = [];
        foreach (var job in _jobs.Values)
        {
            if (job is
                {
                    Seen : false,
                    State: BackgroundJobState.Done or BackgroundJobState.Failed or BackgroundJobState.Cancelled
                })
            {
                job.Seen = true;
                finished.Add(job);
            }
        }

        return finished.Count;
    }

    public bool Cancel(Guid id)
    {
        if (!_jobs.TryGetValue(id, out var job))
        {
            return false;
        }

        bool directCancel;
        lock (job)
        {
            if (job.State == BackgroundJobState.Paused)
            {
                directCancel = true;
            }
            else if (job.State is (BackgroundJobState.Queued or BackgroundJobState.Active))
            {
                directCancel = false;
            }
            else
            {
                return false;
            }
        }

        if (directCancel)
        {
            Forget(job.Id);
            lock (job)
            {
                if (job.State == BackgroundJobState.Paused)
                {
                    job.State      = BackgroundJobState.Cancelled;
                    job.Detail     = "iptal edildi";
                    job.FinishedAt = DateTimeOffset.UtcNow;
                    job.Seen       = false;
                }
            }

            return true;
        }

        try { job.Cts.Cancel(); }
        catch
        {
            // ignored
        }

        lock (job)
        {
            if (job.State is BackgroundJobState.Queued or BackgroundJobState.Active)
            {
                job.Detail = "iptal ediliyor";
            }
        }

        return true;
    }

    public bool Remove(Guid id)
    {
        if (_jobs.TryGetValue(id, out var job)
            && job.State is not (BackgroundJobState.Queued or BackgroundJobState.Active))
        {
            job.Cts.Dispose();
            _specs.TryRemove(id, out var spec);
            var removed = _jobs.TryRemove(id, out _);
            if (removed)
            {
                Forget(id);
                CleanupOrphanedParts(spec);
            }

            return removed;
        }

        return false;
    }

    public void ClearFinished()
    {
        foreach (var pair in _jobs)
        {
            if (pair.Value.State is not (BackgroundJobState.Queued or BackgroundJobState.Active))
            {
                if (_jobs.TryRemove(pair.Key, out var removed))
                {
                    _specs.TryRemove(pair.Key, out var spec);
                    Forget(pair.Key);
                    removed.Cts.Dispose();
                    CleanupOrphanedParts(spec);
                }
            }
        }
    }

    private void CleanupOrphanedParts(Migurdex.Core.Database.StoredDownloadJob? spec)
    {
        if (spec is null)
        {
            return;
        }

        try
        {
            foreach (var other in _specs.Values)
            {
                if (string.Equals(other.OutputDirectory, spec.OutputDirectory, StringComparison.Ordinal)
                    && string.Equals(other.AnimeTitle, spec.AnimeTitle, StringComparison.Ordinal)
                    && string.Equals(other.EpisodeTitle, spec.EpisodeTitle, StringComparison.Ordinal)
                    && other.Season == spec.Season
                    && other.Number.Equals(spec.Number))
                {
                    return;
                }
            }

            ResumeProgressProbe.Cleanup(
                spec.OutputDirectory,
                spec.AnimeTitle,
                spec.EpisodeTitle,
                spec.Season,
                spec.Number);
        }
        catch
        {
            // ignored
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _episodeGate.Dispose();
        lock (_gate)
        {
            foreach (var gate in _hosterGates.Values)
            {
                gate.Dispose();
            }

            _hosterGates.Clear();
        }

        foreach (var job in _jobs.Values)
        {
            job.Cts.Dispose();
        }
    }
}
