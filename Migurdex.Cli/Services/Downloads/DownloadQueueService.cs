using Migurdex.Cli.Configuration;
using Migurdex.Core.Database;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Models;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Migurdex.Cli.Services.Downloads;

public sealed class DownloadJobRequest
{
    public string               ProviderName      { get; set; } = string.Empty;
    public string               AnimeId           { get; set; } = string.Empty;
    public string               AnimeTitle        { get; set; } = string.Empty;
    public string               EpisodeId         { get; set; } = string.Empty;
    public string               EpisodeTitle      { get; set; } = string.Empty;
    public int                  Season            { get; set; } = 1;
    public double               EpisodeNumber     { get; set; } = 1;
    public string?              PreferredGroup    { get; set; }
    public DownloadSourceFormat PreferredFormat   { get; set; } = DownloadSourceFormat.Auto;
    public VideoSource?         Source            { get; set; }
    public string?              OutputDirectory   { get; set; }
    public bool?                Overwrite         { get; set; }
    public bool?                Resume            { get; set; }
    public bool?                DownloadSubtitles { get; set; }
    public int                  Priority          { get; set; }
}

public sealed class DownloadJobSnapshot
{
    public required DownloadJobEntry Job                 { get; init; }
    public          DownloadStage?   Stage               { get; init; }
    public          double           SpeedBytesPerSecond { get; init; }
    public          TimeSpan?        Eta                 { get; init; }
    public          string?          CurrentTrack        { get; init; }
    public          bool             IsAudioTrack        { get; init; }
}

public sealed class DownloadQueueSummary
{
    public int    ActiveCount              { get; init; }
    public int    QueuedCount              { get; init; }
    public int    PausedCount              { get; init; }
    public int    CompletedCount           { get; init; }
    public int    FailedCount              { get; init; }
    public int    CancelledCount           { get; init; }
    public double TotalSpeedBytesPerSecond { get; init; }

    public int  TotalCount => ActiveCount + QueuedCount + PausedCount + CompletedCount + FailedCount + CancelledCount;
    public bool HasActiveOrQueued => ActiveCount > 0 || QueuedCount > 0;
}

public interface IDownloadQueueService : IAsyncDisposable, IDisposable
{
    DownloadJobEntry Enqueue(DownloadJobRequest request, out bool alreadyExisted);

    IReadOnlyList<DownloadJobEntry> EnqueueBatch(
        IEnumerable<DownloadJobRequest> requests,
        out int                         skippedExistingCount);

    IReadOnlyList<DownloadJobSnapshot> GetSnapshots();

    DownloadJobSnapshot? GetSnapshot(string jobId);

    DownloadQueueSummary GetSummary();

    bool Pause(string jobId);

    int PauseAll();

    bool Resume(string jobId, bool refreshSource = false);

    int ResumeAll();

    bool Retry(string jobId, bool refreshSource = true);

    bool BoostPriority(string jobId);

    bool Cancel(string jobId, bool deletePartialFiles = true);

    bool Remove(string jobId, bool deletePartialFiles = false);

    int ClearCompleted();

    int ClearFailed();

    int ClearAll(bool deletePartialFiles = false);

    Task WaitForIdleAsync(CancellationToken cancellationToken = default);

    Task StopAsync();
}

public sealed class DownloadQueueService : IDownloadQueueService
{
    private const long DbProgressFlushIntervalMs = 400;

    private readonly MigurdexDatabase      _database;
    private readonly IDownloadService      _downloadService;
    private readonly IApiClientService?    _apiClient;
    private readonly IConfigurationService _configService;
    private readonly IDownloadPathBuilder  _pathBuilder;

    private readonly Lock                                    _stateLock     = new();
    private readonly Dictionary<string, DownloadJobEntry>    _jobs          = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, JobRunner> _activeRunners = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim                           _schedulerWake = new(0);
    private readonly CancellationTokenSource                 _shutdownCts   = new();
    private readonly Task                                    _schedulerTask;

    private bool _disposed;

    public DownloadQueueService(
        MigurdexDatabase      database,
        IDownloadService      downloadService,
        IConfigurationService configService,
        IApiClientService?    apiClient   = null,
        IDownloadPathBuilder? pathBuilder = null)
    {
        _database        = database ?? throw new ArgumentNullException(nameof(database));
        _downloadService = downloadService ?? throw new ArgumentNullException(nameof(downloadService));
        _configService   = configService ?? throw new ArgumentNullException(nameof(configService));
        _apiClient       = apiClient;
        _pathBuilder     = pathBuilder ?? new DownloadPathBuilder();

        var startupTargetStatus = _configService.Config.AutoResumeDownloadsOnStartup
                                      ? DownloadJobStatus.Queued
                                      : DownloadJobStatus.Paused;
        _database.RecoverInterruptedDownloadJobs(startupTargetStatus);
        foreach (var existing in _database.GetDownloadJobs())
        {
            if (!_configService.Config.AutoResumeDownloadsOnStartup && existing.Status == DownloadJobStatus.Queued)
            {
                existing.Status       = DownloadJobStatus.Paused;
                existing.UpdatedAtUtc = DateTime.UtcNow;
                _database.UpsertDownloadJob(existing);
            }

            _jobs[existing.Id] = existing;
        }

        _schedulerTask = Task.Run(RunSchedulerLoopAsync);
        if (_configService.Config.AutoResumeDownloadsOnStartup)
        {
            SignalScheduler();
        }
    }

    public DownloadJobEntry Enqueue(DownloadJobRequest request, out bool alreadyExisted)
    {
        ArgumentNullException.ThrowIfNull(request);

        lock (_stateLock)
        {
            var existing = FindDuplicateLocked(request.ProviderName, request.AnimeId, request.EpisodeId);
            if (existing is not null)
            {
                if (existing.Status == DownloadJobStatus.Paused)
                {
                    if (request.Source is not null)
                    {
                        existing.Source = CloneSource(request.Source);
                    }

                    existing.Status       = DownloadJobStatus.Queued;
                    existing.ErrorMessage = null;
                    existing.UpdatedAtUtc = DateTime.UtcNow;
                    _database.UpsertDownloadJob(existing);
                    alreadyExisted = false;
                    SignalScheduler();
                    return CloneJob(existing);
                }

                alreadyExisted = true;
                return CloneJob(existing);
            }

            var job = CreateJobEntry(request);
            _jobs[job.Id] = job;
            _database.UpsertDownloadJob(job);
            alreadyExisted = false;
            SignalScheduler();
            return CloneJob(job);
        }
    }

    public IReadOnlyList<DownloadJobEntry> EnqueueBatch(
        IEnumerable<DownloadJobRequest> requests,
        out int                         skippedExistingCount)
    {
        ArgumentNullException.ThrowIfNull(requests);

        var added   = new List<DownloadJobEntry>();
        var skipped = 0;

        lock (_stateLock)
        {
            foreach (var request in requests)
            {
                if (request is null)
                {
                    continue;
                }

                var existing = FindDuplicateLocked(request.ProviderName, request.AnimeId, request.EpisodeId);
                if (existing is not null)
                {
                    if (existing.Status == DownloadJobStatus.Paused)
                    {
                        existing.Status       = DownloadJobStatus.Queued;
                        existing.ErrorMessage = null;
                        existing.UpdatedAtUtc = DateTime.UtcNow;
                        _database.UpsertDownloadJob(existing);
                        added.Add(CloneJob(existing));
                    }
                    else
                    {
                        skipped++;
                    }

                    continue;
                }

                var job = CreateJobEntry(request);
                _jobs[job.Id] = job;
                _database.UpsertDownloadJob(job);
                added.Add(CloneJob(job));
            }
        }

        if (added.Count > 0)
        {
            SignalScheduler();
        }

        skippedExistingCount = skipped;
        return added;
    }

    public IReadOnlyList<DownloadJobSnapshot> GetSnapshots()
    {
        List<DownloadJobEntry> snapshotJobs;
        lock (_stateLock)
        {
            snapshotJobs = _jobs.Values
                                .OrderBy(GetStatusSortOrder)
                                .ThenByDescending(j => j.Priority)
                                .ThenBy(j => j.CreatedAtUtc)
                                .Select(CloneJob)
                                .ToList();
        }

        var result = new List<DownloadJobSnapshot>(snapshotJobs.Count);
        foreach (var job in snapshotJobs)
        {
            result.Add(BuildSnapshot(job));
        }

        return result;
    }

    public DownloadJobSnapshot? GetSnapshot(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return null;
        }

        DownloadJobEntry? job;
        lock (_stateLock)
        {
            if (!_jobs.TryGetValue(jobId, out var found))
            {
                return null;
            }

            job = CloneJob(found);
        }

        return BuildSnapshot(job);
    }

    public DownloadQueueSummary GetSummary()
    {
        var active    = 0;
        var queued    = 0;
        var paused    = 0;
        var completed = 0;
        var failed    = 0;
        var cancelled = 0;

        lock (_stateLock)
        {
            foreach (var job in _jobs.Values)
            {
                switch (job.Status)
                {
                    case DownloadJobStatus.Queued:
                        queued++;
                        break;
                    case DownloadJobStatus.Resolving:
                    case DownloadJobStatus.Downloading:
                    case DownloadJobStatus.Subtitle:
                    case DownloadJobStatus.Finalizing:
                        active++;
                        break;
                    case DownloadJobStatus.Paused:
                        paused++;
                        break;
                    case DownloadJobStatus.Completed:
                        completed++;
                        break;
                    case DownloadJobStatus.Failed:
                        failed++;
                        break;
                    case DownloadJobStatus.Cancelled:
                        cancelled++;
                        break;
                }
            }
        }

        double totalSpeed = 0;
        foreach (var runner in _activeRunners.Values)
        {
            totalSpeed += runner.CurrentSpeed;
        }

        return new DownloadQueueSummary
        {
            ActiveCount              = active,
            QueuedCount              = queued,
            PausedCount              = paused,
            CompletedCount           = completed,
            FailedCount              = failed,
            CancelledCount           = cancelled,
            TotalSpeedBytesPerSecond = totalSpeed
        };
    }

    public bool Pause(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return false;
        }

        JobRunner? runnerToPause = null;
        lock (_stateLock)
        {
            if (!_jobs.TryGetValue(jobId, out var job))
            {
                return false;
            }

            if (!job.Status.IsActiveOrPending())
            {
                return false;
            }

            job.Status       = DownloadJobStatus.Paused;
            job.UpdatedAtUtc = DateTime.UtcNow;
            _database.UpsertDownloadJob(job);

            _activeRunners.TryGetValue(jobId, out runnerToPause);
        }

        runnerToPause?.RequestPause();
        SignalScheduler();
        return true;
    }

    public int PauseAll()
    {
        var runners = new List<JobRunner>();
        var count   = 0;

        lock (_stateLock)
        {
            foreach (var job in _jobs.Values)
            {
                if (!job.Status.IsActiveOrPending())
                {
                    continue;
                }

                job.Status       = DownloadJobStatus.Paused;
                job.UpdatedAtUtc = DateTime.UtcNow;
                _database.UpsertDownloadJob(job);
                count++;

                if (_activeRunners.TryGetValue(job.Id, out var runner))
                {
                    runners.Add(runner);
                }
            }
        }

        foreach (var runner in runners)
        {
            runner.RequestPause();
        }

        return count;
    }

    public bool Resume(string jobId, bool refreshSource = false)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return false;
        }

        lock (_stateLock)
        {
            if (!_jobs.TryGetValue(jobId, out var job))
            {
                return false;
            }

            if (job.Status is not (DownloadJobStatus.Paused or DownloadJobStatus.Failed or DownloadJobStatus.Cancelled))
            {
                return false;
            }

            if (refreshSource)
            {
                job.Source = null;
            }

            job.Status       = DownloadJobStatus.Queued;
            job.ErrorMessage = null;
            job.UpdatedAtUtc = DateTime.UtcNow;
            _database.UpsertDownloadJob(job);
        }

        SignalScheduler();
        return true;
    }

    public int ResumeAll()
    {
        var count = 0;
        lock (_stateLock)
        {
            foreach (var job in _jobs.Values)
            {
                if (job.Status is not (DownloadJobStatus.Paused or DownloadJobStatus.Failed))
                {
                    continue;
                }

                job.Status       = DownloadJobStatus.Queued;
                job.ErrorMessage = null;
                job.UpdatedAtUtc = DateTime.UtcNow;
                _database.UpsertDownloadJob(job);
                count++;
            }
        }

        if (count > 0)
        {
            SignalScheduler();
        }

        return count;
    }

    public bool Retry(string jobId, bool refreshSource = true)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return false;
        }

        JobRunner? activeRunner = null;
        lock (_stateLock)
        {
            if (!_jobs.TryGetValue(jobId, out var job))
            {
                return false;
            }

            if (_activeRunners.TryGetValue(jobId, out activeRunner))
            {
                return false;
            }

            if (refreshSource
                && !string.IsNullOrWhiteSpace(job.ProviderName)
                && !string.IsNullOrWhiteSpace(job.EpisodeId))
            {
                job.Source = null;
            }

            job.Status       = DownloadJobStatus.Queued;
            job.ErrorMessage = null;
            job.Warnings.Clear();
            job.UpdatedAtUtc = DateTime.UtcNow;
            _database.UpsertDownloadJob(job);
        }

        SignalScheduler();
        return true;
    }

    public bool Cancel(string jobId, bool deletePartialFiles = true)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return false;
        }

        JobRunner?        runnerToCancel = null;
        DownloadJobEntry? snapshot       = null;

        lock (_stateLock)
        {
            if (!_jobs.TryGetValue(jobId, out var job))
            {
                return false;
            }

            if (job.Status == DownloadJobStatus.Completed)
            {
                return false;
            }

            job.Status       = DownloadJobStatus.Cancelled;
            job.ErrorMessage = "İndirme iptal edildi.";
            job.UpdatedAtUtc = DateTime.UtcNow;
            _database.UpsertDownloadJob(job);
            snapshot = CloneJob(job);

            _activeRunners.TryGetValue(jobId, out runnerToCancel);
        }

        if (runnerToCancel is not null)
        {
            runnerToCancel.RequestCancel(deletePartialFiles);
        }
        else if (deletePartialFiles && snapshot is not null)
        {
            TryCleanPartialArtifacts(snapshot);
        }

        SignalScheduler();
        return true;
    }

    public bool Remove(string jobId, bool deletePartialFiles = false)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return false;
        }

        JobRunner?        runnerToCancel = null;
        DownloadJobEntry? removedJob     = null;

        lock (_stateLock)
        {
            if (!_jobs.TryGetValue(jobId, out var job))
            {
                return false;
            }

            _jobs.Remove(jobId);
            _database.DeleteDownloadJob(jobId);
            removedJob = CloneJob(job);
            _activeRunners.TryGetValue(jobId, out runnerToCancel);
        }

        if (runnerToCancel is not null)
        {
            runnerToCancel.RequestCancel(deletePartialFiles);
        }
        else if (deletePartialFiles && removedJob is not null)
        {
            TryCleanPartialArtifacts(removedJob);
        }

        SignalScheduler();
        return true;
    }

    public bool BoostPriority(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return false;
        }

        lock (_stateLock)
        {
            if (!_jobs.TryGetValue(jobId, out var job))
            {
                return false;
            }

            var maxPriority = _jobs.Values.Count > 0 ? _jobs.Values.Max(j => j.Priority) : 0;
            job.Priority     = Math.Max(1, maxPriority + 1);
            job.UpdatedAtUtc = DateTime.UtcNow;
            _database.UpsertDownloadJob(job);
        }

        SignalScheduler();
        return true;
    }

    public int ClearCompleted()
    {
        lock (_stateLock)
        {
            var toRemove = _jobs.Values
                                .Where(j => j.Status is DownloadJobStatus.Completed or DownloadJobStatus.Cancelled)
                                .Select(j => j.Id)
                                .ToList();

            foreach (var id in toRemove)
            {
                _jobs.Remove(id);
            }

            _database.ClearCompletedDownloadJobs();
            return toRemove.Count;
        }
    }

    public int ClearFailed()
    {
        lock (_stateLock)
        {
            var toRemove = _jobs.Values
                                .Where(j => j.Status is DownloadJobStatus.Failed or DownloadJobStatus.Cancelled)
                                .Select(j => j.Id)
                                .ToList();

            foreach (var id in toRemove)
            {
                _jobs.Remove(id);
            }

            _database.ClearFailedDownloadJobs();
            return toRemove.Count;
        }
    }

    public int ClearAll(bool deletePartialFiles = false)
    {
        var runners  = new List<JobRunner>();
        var jobsList = new List<DownloadJobEntry>();

        lock (_stateLock)
        {
            foreach (var job in _jobs.Values)
            {
                jobsList.Add(CloneJob(job));
                if (_activeRunners.TryGetValue(job.Id, out var runner))
                {
                    runners.Add(runner);
                }
            }

            _jobs.Clear();
            _database.ClearAllDownloadJobs();
        }

        foreach (var runner in runners)
        {
            runner.RequestCancel(deletePartialFiles);
        }

        if (deletePartialFiles)
        {
            foreach (var job in jobsList)
            {
                TryCleanPartialArtifacts(job);
            }
        }

        SignalScheduler();
        return jobsList.Count;
    }

    public async Task WaitForIdleAsync(CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var summary = GetSummary();
            if (!summary.HasActiveOrQueued && _activeRunners.IsEmpty)
            {
                return;
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task StopAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            await _shutdownCts.CancelAsync().ConfigureAwait(false);
        }
        catch
        {
            // ignored
        }

        SignalScheduler();

        foreach (var runner in _activeRunners.Values)
        {
            runner.RequestPause();
        }

        try
        {
            await _schedulerTask.ConfigureAwait(false);
        }
        catch
        {
            // ignored
        }

        var activeTasks = _activeRunners.Values.Select(r => r.ExecutionTask)
                                        .Where(t => t is not null)
                                        .Cast<Task>()
                                        .ToArray();
        if (activeTasks.Length > 0)
        {
            try
            {
                await Task.WhenAll(activeTasks).ConfigureAwait(false);
            }
            catch
            {
                // ignored
            }
        }
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
        _shutdownCts.Dispose();
        _schedulerWake.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _shutdownCts.Dispose();
        _schedulerWake.Dispose();
    }

    private async Task RunSchedulerLoopAsync()
    {
        var token = _shutdownCts.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                DispatchAvailableJobs();
                await _schedulerWake.WaitAsync(TimeSpan.FromMilliseconds(250), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // ignored
            }
        }
    }

    private void DispatchAvailableJobs()
    {
        if (_shutdownCts.IsCancellationRequested)
        {
            return;
        }

        var maxConcurrency = Math.Clamp(_configService.Config.MaxConcurrentDownloads, 1, 8);

        while (_activeRunners.Count < maxConcurrency)
        {
            DownloadJobEntry? nextJob = null;
            JobRunner?        runner  = null;

            lock (_stateLock)
            {
                if (_activeRunners.Count >= maxConcurrency)
                {
                    break;
                }

                nextJob = _jobs.Values
                               .Where(j => j.Status == DownloadJobStatus.Queued
                                           && !_activeRunners.ContainsKey(j.Id))
                               .OrderByDescending(j => j.Priority)
                               .ThenBy(j => j.CreatedAtUtc)
                               .FirstOrDefault();

                if (nextJob is null)
                {
                    break;
                }

                nextJob.Status = nextJob.Source is null
                                     ? DownloadJobStatus.Resolving
                                     : DownloadJobStatus.Downloading;
                nextJob.ErrorMessage = null;
                nextJob.UpdatedAtUtc = DateTime.UtcNow;
                _database.UpsertDownloadJob(nextJob);

                runner                     = new JobRunner(nextJob.Id, _shutdownCts.Token);
                _activeRunners[nextJob.Id] = runner;
            }

            var capturedJob    = nextJob;
            var capturedRunner = runner;
            capturedRunner.ExecutionTask = Task.Run(async () =>
            {
                try
                {
                    await ExecuteJobAsync(capturedJob.Id, capturedRunner).ConfigureAwait(false);
                }
                finally
                {
                    _activeRunners.TryRemove(capturedJob.Id, out _);
                    capturedRunner.Dispose();
                    SignalScheduler();
                }
            });
        }
    }

    private async Task ExecuteJobAsync(string jobId, JobRunner runner)
    {
        DownloadJobEntry? snapshot;
        lock (_stateLock)
        {
            if (!_jobs.TryGetValue(jobId, out var found))
            {
                return;
            }

            snapshot = CloneJob(found);
        }

        var ct = runner.Token;

        try
        {
            var candidates = new List<VideoSource>();
            if (snapshot.Source is not null && DownloadSourceResolver.IsDirectDownloadable(snapshot.Source))
            {
                candidates.Add(SanitizeSource(snapshot.Source));
            }

            if (candidates.Count == 0)
            {
                var resolved = await ResolveFreshCandidatesAsync(snapshot, ct).ConfigureAwait(false);
                if (resolved.Error is not null && resolved.Candidates.Count == 0)
                {
                    MarkJobFailed(jobId, runner, resolved.Error);
                    return;
                }

                candidates.AddRange(resolved.Candidates.Select(SanitizeSource));
            }

            if (candidates.Count == 0)
            {
                MarkJobFailed(jobId, runner, "İndirilebilir doğrudan kaynak bulunamadı.");
                return;
            }

            var             triedUrls             = new HashSet<string>(StringComparer.Ordinal);
            var             refreshedFromUpstream = snapshot.Source is null;
            DownloadResult? finalResult           = null;
            string?         lastError             = null;

            for (var index = 0; index < candidates.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                var candidate = candidates[index];
                triedUrls.Add(candidate.Url);

                UpdateJobSource(jobId, candidate);

                var progress = new JobProgressReporter(this, jobId, runner);
                try
                {
                    finalResult = await _downloadService.DownloadAsync(
                                                            new DownloadRequest
                                                            {
                                                                Source            = candidate,
                                                                OutputDirectory   = snapshot.OutputDirectory,
                                                                AnimeTitle        = snapshot.AnimeTitle,
                                                                EpisodeTitle      = snapshot.EpisodeTitle,
                                                                Season            = snapshot.Season,
                                                                EpisodeNumber     = snapshot.EpisodeNumber,
                                                                Overwrite         = snapshot.Overwrite,
                                                                Resume            = snapshot.ResumeEnabled,
                                                                DownloadSubtitles = snapshot.DownloadSubtitles,
                                                                Progress          = progress
                                                            },
                                                            ct)
                                                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    HandleJobCancellation(jobId, runner, snapshot);
                    return;
                }
                catch (Exception ex)
                {
                    finalResult = DownloadResult.Failed(ex.Message);
                }

                if (ct.IsCancellationRequested || runner.PauseRequested || runner.CancelRequested)
                {
                    HandleJobCancellation(jobId, runner, snapshot);
                    return;
                }

                if (finalResult.Success)
                {
                    MarkJobCompleted(jobId, finalResult);
                    return;
                }

                if (finalResult.Error?.Contains("Video hedefi zaten var", StringComparison.OrdinalIgnoreCase) == true
                    && !snapshot.Overwrite)
                {
                    var dest = _pathBuilder.Build(snapshot.OutputDirectory,
                                                  snapshot.AnimeTitle,
                                                  snapshot.EpisodeTitle,
                                                  snapshot.Season,
                                                  snapshot.EpisodeNumber,
                                                  candidate.Type == VideoType.Mp4 ? ".mp4" : null);
                    if (File.Exists(dest.MediaPath) && new FileInfo(dest.MediaPath).Length > 0)
                    {
                        MarkJobCompleted(jobId, DownloadResult.Successful(dest.MediaPath));
                        return;
                    }
                }

                lastError = finalResult.Error;

                if (!refreshedFromUpstream
                    && _apiClient is not null
                    && !string.IsNullOrWhiteSpace(snapshot.ProviderName)
                    && !string.IsNullOrWhiteSpace(snapshot.EpisodeId))
                {
                    refreshedFromUpstream = true;
                    SetJobStatus(jobId, DownloadJobStatus.Resolving);
                    var fresh = await ResolveFreshCandidatesAsync(snapshot, ct).ConfigureAwait(false);
                    foreach (var freshCandidate in fresh.Candidates)
                    {
                        if (!triedUrls.Contains(freshCandidate.Url))
                        {
                            candidates.Add(SanitizeSource(freshCandidate));
                        }
                    }
                }
            }

            MarkJobFailed(jobId, runner, lastError ?? "Video indirilemedi.");
        }
        catch (OperationCanceledException)
        {
            HandleJobCancellation(jobId, runner, snapshot);
        }
        catch (Exception ex)
        {
            if (runner.PauseRequested || runner.CancelRequested || _shutdownCts.IsCancellationRequested)
            {
                HandleJobCancellation(jobId, runner, snapshot);
                return;
            }

            MarkJobFailed(jobId, runner, ex.Message);
        }
    }

    private async Task<(List<VideoSource> Candidates, string? Error)> ResolveFreshCandidatesAsync(
        DownloadJobEntry  job,
        CancellationToken cancellationToken)
    {
        if (_apiClient is null
            || string.IsNullOrWhiteSpace(job.ProviderName)
            || string.IsNullOrWhiteSpace(job.EpisodeId))
        {
            return ([], "Kaynak çözümü için API bağlantısı veya bölüm kimliği eksik.");
        }

        SetJobStatus(job.Id, DownloadJobStatus.Resolving);

        if (!Enum.TryParse<DownloadSourceFormat>(job.PreferredFormat, ignoreCase: true, out var format))
        {
            format = DownloadSourceFormat.Auto;
        }

        var config  = _configService.Config;
        var timeout = Math.Max(5, config.DownloadAutoSelectTimeoutSeconds);
        var resolved = await DownloadCandidateResolver.ResolveAsync(
                                                          _apiClient,
                                                          job.ProviderName,
                                                          job.EpisodeId,
                                                          job.PreferredGroup,
                                                          format,
                                                          config,
                                                          timeout,
                                                          cancellationToken)
                                                      .ConfigureAwait(false);

        if (resolved.Candidates.Count > 0)
        {
            return (resolved.Candidates, null);
        }

        if (resolved.TimedOut)
        {
            return ([], "Kaynak çözümleme zaman aşımına uğradı.");
        }

        return ([], resolved.Error ?? "Uygun doğrudan MP4/HLS kaynağı bulunamadı.");
    }

    private void ReportJobProgress(string jobId, JobRunner runner, DownloadProgress progress)
    {
        runner.UpdateProgress(progress);

        lock (_stateLock)
        {
            if (!_jobs.TryGetValue(jobId, out var job))
            {
                return;
            }

            if (runner.PauseRequested
                || runner.CancelRequested
                || job.Status is DownloadJobStatus.Paused or DownloadJobStatus.Cancelled)
            {
                return;
            }

            var previousStatus = job.Status;
            job.Status = progress.Stage switch
            {
                DownloadStage.Subtitle   => DownloadJobStatus.Subtitle,
                DownloadStage.Finalizing => DownloadJobStatus.Finalizing,
                _                        => DownloadJobStatus.Downloading
            };

            if (progress.BytesDownloaded > 0 || job.BytesDownloaded == 0)
            {
                job.BytesDownloaded = progress.BytesDownloaded;
            }

            if (progress.TotalBytes is > 0)
            {
                job.TotalBytes = progress.TotalBytes;
            }

            if (progress.FragmentsDone is not null)
            {
                job.FragmentsDone = progress.FragmentsDone;
            }

            if (progress.FragmentsTotal is not null)
            {
                job.FragmentsTotal = progress.FragmentsTotal;
            }

            if (progress.Percent is not null)
            {
                job.Percent = progress.Percent;
            }
            else if (job.TotalBytes is > 0 && job.BytesDownloaded >= 0)
            {
                job.Percent = Math.Clamp((double) job.BytesDownloaded / job.TotalBytes.Value * 100.0, 0.0, 100.0);
            }
            else if (job.FragmentsTotal is > 0 && job.FragmentsDone is >= 0)
            {
                job.Percent = Math.Clamp((double) job.FragmentsDone.Value / job.FragmentsTotal.Value * 100.0,
                                         0.0,
                                         100.0);
            }

            job.UpdatedAtUtc = DateTime.UtcNow;

            if (previousStatus != job.Status || runner.ShouldFlushToDatabase())
            {
                _database.UpsertDownloadJob(job);
            }
        }
    }

    private void HandleJobCancellation(string jobId, JobRunner runner, DownloadJobEntry snapshot)
    {
        var deletePartial = false;
        lock (_stateLock)
        {
            if (_jobs.TryGetValue(jobId, out var job))
            {
                if (runner.CancelRequested || job.Status == DownloadJobStatus.Cancelled)
                {
                    job.Status       = DownloadJobStatus.Cancelled;
                    job.ErrorMessage = "İndirme iptal edildi.";
                    job.UpdatedAtUtc = DateTime.UtcNow;
                    _database.UpsertDownloadJob(job);
                    deletePartial = runner.DeletePartialOnCancel;
                }
                else if (_shutdownCts.IsCancellationRequested && !runner.PauseRequested)
                {
                    job.Status       = DownloadJobStatus.Queued;
                    job.UpdatedAtUtc = DateTime.UtcNow;
                    _database.UpsertDownloadJob(job);
                }
                else
                {
                    job.Status       = DownloadJobStatus.Paused;
                    job.UpdatedAtUtc = DateTime.UtcNow;
                    _database.UpsertDownloadJob(job);
                }
            }
            else
            {
                deletePartial = runner.DeletePartialOnCancel;
            }
        }

        if (deletePartial)
        {
            TryCleanPartialArtifacts(snapshot);
        }
    }

    private void MarkJobCompleted(string jobId, DownloadResult result)
    {
        lock (_stateLock)
        {
            if (!_jobs.TryGetValue(jobId, out var job))
            {
                return;
            }

            if (_configService.Config.AutoClearCompletedDownloads)
            {
                _jobs.Remove(jobId);
                _database.DeleteDownloadJob(jobId);
                return;
            }

            job.Status        = DownloadJobStatus.Completed;
            job.MediaPath     = result.MediaPath;
            job.SubtitlePaths = [.. result.SubtitlePaths];
            job.Warnings      = [.. result.Warnings];
            job.Percent       = 100.0;
            job.ErrorMessage  = null;
            if (!string.IsNullOrWhiteSpace(result.MediaPath) && File.Exists(result.MediaPath))
            {
                try
                {
                    var len = new FileInfo(result.MediaPath).Length;
                    job.BytesDownloaded = len;
                    job.TotalBytes      = len;
                }
                catch
                {
                    // ignored
                }
            }

            job.UpdatedAtUtc = DateTime.UtcNow;
            _database.UpsertDownloadJob(job);
        }
    }

    private void MarkJobFailed(string jobId, JobRunner runner, string error)
    {
        if (runner.PauseRequested || runner.CancelRequested || _shutdownCts.IsCancellationRequested)
        {
            return;
        }

        lock (_stateLock)
        {
            if (!_jobs.TryGetValue(jobId, out var job))
            {
                return;
            }

            if (job.Status is DownloadJobStatus.Paused or DownloadJobStatus.Cancelled)
            {
                return;
            }

            job.Status       = DownloadJobStatus.Failed;
            job.ErrorMessage = string.IsNullOrWhiteSpace(error) ? "Video indirilemedi." : error;
            job.UpdatedAtUtc = DateTime.UtcNow;
            _database.UpsertDownloadJob(job);
        }
    }

    private void SetJobStatus(string jobId, DownloadJobStatus status)
    {
        lock (_stateLock)
        {
            if (!_jobs.TryGetValue(jobId, out var job))
            {
                return;
            }

            if (job.Status is DownloadJobStatus.Paused or DownloadJobStatus.Cancelled)
            {
                return;
            }

            job.Status       = status;
            job.UpdatedAtUtc = DateTime.UtcNow;
            _database.UpsertDownloadJob(job);
        }
    }

    private void UpdateJobSource(string jobId, VideoSource source)
    {
        lock (_stateLock)
        {
            if (!_jobs.TryGetValue(jobId, out var job))
            {
                return;
            }

            job.Source = CloneSource(source);
            if (job.Status == DownloadJobStatus.Resolving)
            {
                job.Status = DownloadJobStatus.Downloading;
            }

            job.UpdatedAtUtc = DateTime.UtcNow;
            _database.UpsertDownloadJob(job);
        }
    }

    private DownloadJobSnapshot BuildSnapshot(DownloadJobEntry job)
    {
        if (_activeRunners.TryGetValue(job.Id, out var runner))
        {
            var progress = runner.CurrentProgress;
            return new DownloadJobSnapshot
            {
                Job                 = job,
                Stage               = progress?.Stage,
                SpeedBytesPerSecond = runner.CurrentSpeed,
                Eta                 = runner.CurrentEta,
                CurrentTrack        = progress?.Track,
                IsAudioTrack        = progress?.IsAudioTrack ?? false
            };
        }

        return new DownloadJobSnapshot
        {
            Job = job
        };
    }

    private DownloadJobEntry? FindDuplicateLocked(string providerName, string animeId, string episodeId)
    {
        if (string.IsNullOrWhiteSpace(providerName)
            || string.IsNullOrWhiteSpace(animeId)
            || string.IsNullOrWhiteSpace(episodeId))
        {
            return null;
        }

        return _jobs.Values.FirstOrDefault(j =>
                                               string.Equals(j.ProviderName,
                                                             providerName.Trim(),
                                                             StringComparison.OrdinalIgnoreCase)
                                               && string.Equals(j.AnimeId,
                                                                animeId.Trim(),
                                                                StringComparison.OrdinalIgnoreCase)
                                               && string.Equals(j.EpisodeId,
                                                                episodeId.Trim(),
                                                                StringComparison.OrdinalIgnoreCase)
                                               && j.Status is DownloadJobStatus.Queued
                                                              or DownloadJobStatus.Resolving
                                                              or DownloadJobStatus.Downloading
                                                              or DownloadJobStatus.Subtitle
                                                              or DownloadJobStatus.Finalizing
                                                              or DownloadJobStatus.Paused);
    }

    private DownloadJobEntry CreateJobEntry(DownloadJobRequest request)
    {
        var config = _configService.Config;
        var now    = DateTime.UtcNow;

        return new DownloadJobEntry
        {
            Id              = Guid.NewGuid().ToString("N"),
            ProviderName    = (request.ProviderName ?? string.Empty).Trim(),
            AnimeId         = (request.AnimeId ?? string.Empty).Trim(),
            AnimeTitle      = string.IsNullOrWhiteSpace(request.AnimeTitle) ? "Anime" : request.AnimeTitle.Trim(),
            EpisodeId       = (request.EpisodeId ?? string.Empty).Trim(),
            EpisodeTitle    = (request.EpisodeTitle ?? string.Empty).Trim(),
            Season          = Math.Max(1, request.Season),
            EpisodeNumber   = request.EpisodeNumber > 0 ? request.EpisodeNumber : 1,
            PreferredGroup  = string.IsNullOrWhiteSpace(request.PreferredGroup) ? null : request.PreferredGroup.Trim(),
            PreferredFormat = request.PreferredFormat.ToString(),
            Source          = request.Source is null ? null : CloneSource(request.Source),
            OutputDirectory = string.IsNullOrWhiteSpace(request.OutputDirectory)
                                  ? config.DownloadDirectory
                                  : request.OutputDirectory.Trim(),
            Status            = DownloadJobStatus.Queued,
            Overwrite         = request.Overwrite ?? config.DownloadOverwrite,
            ResumeEnabled     = request.Resume ?? config.DownloadResume,
            DownloadSubtitles = request.DownloadSubtitles ?? config.DownloadSubtitles,
            Priority          = request.Priority,
            CreatedAtUtc      = now,
            UpdatedAtUtc      = now
        };
    }

    private void TryCleanPartialArtifacts(DownloadJobEntry job)
    {
        try
        {
            var dest = _pathBuilder.Build(job.OutputDirectory,
                                          job.AnimeTitle,
                                          job.EpisodeTitle,
                                          job.Season,
                                          job.EpisodeNumber,
                                          ".mp4");
            if (!Directory.Exists(dest.AnimeDirectory))
            {
                return;
            }

            foreach (var file in Directory.GetFiles(dest.AnimeDirectory, dest.FileStem + "*"))
            {
                if (file.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
                    || file.EndsWith(".part.meta", StringComparison.OrdinalIgnoreCase)
                    || file.Contains(".part.seg", StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(file); }
                    catch
                    {
                        // ignored
                    }
                }
            }
        }
        catch
        {
            // ignored
        }
    }

    private void SignalScheduler()
    {
        try
        {
            if (_schedulerWake.CurrentCount == 0)
            {
                _schedulerWake.Release();
            }
        }
        catch
        {
            // ignored
        }
    }

    private static int GetStatusSortOrder(DownloadJobEntry job)
    {
        return job.Status switch
        {
            DownloadJobStatus.Downloading => 0,
            DownloadJobStatus.Resolving   => 1,
            DownloadJobStatus.Subtitle    => 2,
            DownloadJobStatus.Finalizing  => 3,
            DownloadJobStatus.Queued      => 4,
            DownloadJobStatus.Paused      => 5,
            DownloadJobStatus.Failed      => 6,
            DownloadJobStatus.Completed   => 7,
            DownloadJobStatus.Cancelled   => 8,
            _                             => 9
        };
    }

    private static VideoSource SanitizeSource(VideoSource source)
    {
        var clone = CloneSource(source);
        if (clone.Subtitles is { Count: > 0 })
        {
            clone.Subtitles = clone.Subtitles
                                   .Where(s => s is not null
                                               && !string.IsNullOrWhiteSpace(s.Url)
                                               && !s.Url.StartsWith("migurdex://openanime_sub_",
                                                                    StringComparison.OrdinalIgnoreCase))
                                   .ToList();
            if (clone.Subtitles.Count == 0)
            {
                clone.Subtitles = null;
            }
        }

        return clone;
    }

    private static VideoSource CloneSource(VideoSource source)
    {
        return new VideoSource
        {
            Url             = source.Url,
            Quality         = source.Quality,
            Type            = source.Type,
            Hoster          = source.Hoster,
            Group           = source.Group,
            Language        = source.Language,
            Bitrate         = source.Bitrate,
            Width           = source.Width,
            Height          = source.Height,
            VideoCodec      = source.VideoCodec,
            AudioCodec      = source.AudioCodec,
            SizeBytes       = source.SizeBytes,
            DurationSeconds = source.DurationSeconds,
            Headers = source.Headers is null
                          ? null
                          : new Dictionary<string, string>(source.Headers, StringComparer.OrdinalIgnoreCase),
            Subtitles = source.Subtitles?
                              .Select(s => new Subtitle
                              {
                                  Url      = s.Url,
                                  Language = s.Language,
                                  Label    = s.Label,
                                  Format   = s.Format,
                                  Headers = s.Headers is null
                                                ? null
                                                : new Dictionary<string, string>(s.Headers,
                                                    StringComparer.OrdinalIgnoreCase)
                              })
                              .ToList()
        };
    }

    private static DownloadJobEntry CloneJob(DownloadJobEntry job)
    {
        return new DownloadJobEntry
        {
            Id                = job.Id,
            ProviderName      = job.ProviderName,
            AnimeId           = job.AnimeId,
            AnimeTitle        = job.AnimeTitle,
            EpisodeId         = job.EpisodeId,
            EpisodeTitle      = job.EpisodeTitle,
            Season            = job.Season,
            EpisodeNumber     = job.EpisodeNumber,
            PreferredGroup    = job.PreferredGroup,
            PreferredFormat   = job.PreferredFormat,
            Source            = job.Source is null ? null : CloneSource(job.Source),
            OutputDirectory   = job.OutputDirectory,
            MediaPath         = job.MediaPath,
            SubtitlePaths     = [.. job.SubtitlePaths],
            Warnings          = [.. job.Warnings],
            Status            = job.Status,
            BytesDownloaded   = job.BytesDownloaded,
            TotalBytes        = job.TotalBytes,
            FragmentsDone     = job.FragmentsDone,
            FragmentsTotal    = job.FragmentsTotal,
            Percent           = job.Percent,
            ErrorMessage      = job.ErrorMessage,
            Overwrite         = job.Overwrite,
            ResumeEnabled     = job.ResumeEnabled,
            DownloadSubtitles = job.DownloadSubtitles,
            Priority          = job.Priority,
            CreatedAtUtc      = job.CreatedAtUtc,
            UpdatedAtUtc      = job.UpdatedAtUtc
        };
    }

    private sealed class JobProgressReporter(DownloadQueueService owner, string jobId, JobRunner runner)
        : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value)
        {
            owner.ReportJobProgress(jobId, runner, value);
        }
    }

    private sealed class JobRunner : IDisposable
    {
        private readonly CancellationTokenSource _cts;
        private readonly Lock                    _sync  = new();
        private readonly DownloadSpeedometer     _speed = new();
        private          DownloadProgress?       _currentProgress;
        private          long                    _lastDbFlushTimestamp = Stopwatch.GetTimestamp();

        public JobRunner(string jobId, CancellationToken shutdownToken)
        {
            JobId = jobId;
            _cts  = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
        }

        public          string            JobId         { get; }
        public          CancellationToken Token         => _cts.Token;
        public          Task?             ExecutionTask { get; set; }
        public volatile bool              PauseRequested;
        public volatile bool              CancelRequested;
        public volatile bool              DeletePartialOnCancel;

        public DownloadProgress? CurrentProgress
        {
            get
            {
                lock (_sync)
                {
                    return _currentProgress;
                }
            }
        }

        public double CurrentSpeed
        {
            get
            {
                lock (_sync)
                {
                    return _currentProgress?.SpeedBytesPerSecond ?? _speed.BytesPerSecond;
                }
            }
        }

        public TimeSpan? CurrentEta
        {
            get
            {
                lock (_sync)
                {
                    if (_currentProgress is null)
                    {
                        return null;
                    }

                    if (_currentProgress.Eta is not null)
                    {
                        return _currentProgress.Eta;
                    }

                    return _speed.EstimateRemaining(_currentProgress.BytesDownloaded,
                                                    _currentProgress.TotalBytes,
                                                    _currentProgress.SpeedBytesPerSecond);
                }
            }
        }

        public void UpdateProgress(DownloadProgress progress)
        {
            lock (_sync)
            {
                _speed.Sample(progress.BytesDownloaded, DateTimeOffset.UtcNow);
                _currentProgress = progress;
            }
        }

        public bool ShouldFlushToDatabase()
        {
            lock (_sync)
            {
                if (Stopwatch.GetElapsedTime(_lastDbFlushTimestamp).TotalMilliseconds >= DbProgressFlushIntervalMs)
                {
                    _lastDbFlushTimestamp = Stopwatch.GetTimestamp();
                    return true;
                }

                return false;
            }
        }

        public void RequestPause()
        {
            PauseRequested = true;
            try { _cts.Cancel(); }
            catch
            {
                // ignored
            }
        }

        public void RequestCancel(bool deletePartial)
        {
            CancelRequested       = true;
            DeletePartialOnCancel = deletePartial;
            try { _cts.Cancel(); }
            catch
            {
                // ignored
            }
        }

        public void Dispose()
        {
            _cts.Dispose();
        }
    }
}
