using Migurdex.Cli.Services.Downloads;
using Migurdex.Core.Database;
using Xunit;

namespace Migurdex.Tests;

public sealed class DownloadJobStoreTests : IDisposable
{
    private readonly string _dir;

    public DownloadJobStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "migurdex-jobtest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); }
        catch
        {
            // ignored
        }
    }

    private static StoredDownloadJob Sample(Guid? id = null)
    {
        return new StoredDownloadJob(
            id ?? Guid.NewGuid(),
            "Stub",
            "anime1",
            "Test Anime",
            "ep1",
            "Bölüm 1",
            1,
            1,
            ["Fansub"],
            ["FastHost"],
            ["1080p"],
            ["Mp4"],
            "/tmp/dl",
            true,
            "queued",
            null,
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
    }

    [Fact]
    public void Upsert_ThenLoad_RoundTripsAllFields()
    {
        var store = new DownloadJobStore(new MigurdexDatabase(_dir));
        var job   = Sample();
        store.Upsert(job);

        var loaded = Assert.Single(store.LoadAll());
        Assert.Equal(job.Id, loaded.Id);
        Assert.Equal("Stub", loaded.Provider);
        Assert.Equal("ep1", loaded.EpisodeId);
        Assert.Equal(["Fansub"], loaded.Groups);
        Assert.Equal(["FastHost"], loaded.Hosters);
        Assert.Equal("queued", loaded.State);
        Assert.True(loaded.DownloadSubtitles);
    }

    [Fact]
    public void Upsert_SameId_UpdatesState()
    {
        var store = new DownloadJobStore(new MigurdexDatabase(_dir));
        var job   = Sample();
        store.Upsert(job);
        store.Upsert(job with
        {
            State = "paused",
            Error = "duraklatıldı"
        });

        var loaded = Assert.Single(store.LoadAll());
        Assert.Equal("paused", loaded.State);
        Assert.Equal("duraklatıldı", loaded.Error);
    }

    [Fact]
    public void Delete_RemovesRow()
    {
        var store = new DownloadJobStore(new MigurdexDatabase(_dir));
        var job   = Sample();
        store.Upsert(job);
        store.Delete(job.Id);

        Assert.Empty(store.LoadAll());
    }

    [Fact]
    public async Task Tracker_PersistAndRestore_RoundTripsAsPaused()
    {
        var store = new DownloadJobStore(new MigurdexDatabase(_dir));
        using var tracker = new BackgroundDownloadTracker(maxEpisodes: 1, maxSocketsPerHoster: 8, connectionsPerFile: 4)
        {
            Store = store
        };

        var started = new TaskCompletionSource();
        var id = tracker.Enqueue("Test Anime",
                                 "Bölüm 1",
                                 async control =>
                                 {
                                     started.TrySetResult();
#pragma warning disable xUnit1051 // İş token'ı kasıtlı kullanılıyor
                                     await Task.Delay(5000, control.Token);
#pragma warning restore xUnit1051
                                 },
                                 jobId => Sample(jobId));

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Single(store.LoadAll());
        Assert.True(tracker.Pause(id));

        using var tracker2 =
            new BackgroundDownloadTracker(maxEpisodes: 1, maxSocketsPerHoster: 8, connectionsPerFile: 4)
            {
                Store = store
            };
        var restored = tracker2.RestorePending(_ => control =>
        {
            control.Job.MediaPath = "/tmp/restored.mp4";
            return Task.CompletedTask;
        });

        Assert.Equal(1, restored);
        Assert.Equal(1, tracker2.TakePendingRestore());
        var job = Assert.Single(tracker2.Snapshot());
        Assert.Equal(BackgroundJobState.Paused, job.State);

        Assert.True(tracker2.Resume(job.Id));
        await WaitForStateAsync(tracker2, job.Id, BackgroundJobState.Done);
        Assert.Equal("/tmp/restored.mp4", tracker2.Snapshot().First(j => j.Id == job.Id).MediaPath);
        Assert.Empty(store.LoadAll());
    }

    private static async Task WaitForStateAsync(
        BackgroundDownloadTracker tracker,
        Guid                      id,
        BackgroundJobState        state,
        int                       timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (tracker.Snapshot().First(j => j.Id == id).State != state)
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
            {
                Assert.Fail("Timed out waiting for restored job state.");
            }

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }
}
