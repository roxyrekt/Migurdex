using Migurdex.Cli.Services.Downloads;
using Xunit;

namespace Migurdex.Tests;

public sealed class BackgroundDownloadTrackerTests
{
    [Fact]
    public async Task Enqueue_RunsWork_AndMarksDone()
    {
        using var tracker =
            new BackgroundDownloadTracker(maxEpisodes: 2, maxSocketsPerHoster: 8, connectionsPerFile: 4);
        var id = tracker.Enqueue("Anime",
                                 "Bölüm 1",
                                 async control =>
                                 {
                                     control.Progress.Report(new DownloadProgress(DownloadStage.Downloading, 50, 100));
                                     await Task.Delay(10, TestContext.Current.CancellationToken);
                                     control.Job.MediaPath = "/tmp/x.mp4";
                                 });

        await WaitForAsync(() => tracker.Snapshot().First(j => j.Id == id).State == BackgroundJobState.Done);

        var job = tracker.Snapshot().First(j => j.Id == id);
        Assert.Equal(BackgroundJobState.Done, job.State);
        Assert.Equal("/tmp/x.mp4", job.MediaPath);
        Assert.Equal(0, tracker.ActiveCount);
    }

    [Fact]
    public async Task Enqueue_Failure_MarksFailedWithError()
    {
        using var tracker =
            new BackgroundDownloadTracker(maxEpisodes: 2, maxSocketsPerHoster: 8, connectionsPerFile: 4);
        var id = tracker.Enqueue("Anime", "Bölüm 2", _ => throw new DownloadException("upstream down"));

        await WaitForAsync(() => tracker.Snapshot().First(j => j.Id == id).State == BackgroundJobState.Failed);

        var job = tracker.Snapshot().First(j => j.Id == id);
        Assert.Equal("upstream down", job.Error);
    }

    [Fact]
    public async Task Cancel_ActiveJob_MarksCancelled()
    {
        using var tracker =
            new BackgroundDownloadTracker(maxEpisodes: 2, maxSocketsPerHoster: 8, connectionsPerFile: 4);
        var started = new TaskCompletionSource();
        var id = tracker.Enqueue("Anime",
                                 "Bölüm 3",
                                 async control =>
                                 {
                                     started.TrySetResult();
#pragma warning disable xUnit1051 // İptal senaryosu iş token'ını gerektirir
                                     await Task.Delay(5000, control.Token);
#pragma warning restore xUnit1051
                                 });

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(tracker.Cancel(id));
        await WaitForAsync(() => tracker.Snapshot().First(j => j.Id == id).State == BackgroundJobState.Cancelled);
    }

    [Fact]
    public async Task TakeUnseenFinished_ReturnsOnce()
    {
        using var tracker =
            new BackgroundDownloadTracker(maxEpisodes: 2, maxSocketsPerHoster: 8, connectionsPerFile: 4);
        tracker.Enqueue("Anime", "Bölüm 4", _ => Task.CompletedTask);

        BackgroundJob? finished = null;
        await WaitForAsync(() =>
        {
            var n = tracker.TakeUnseenFinished(out var list);
            if (n > 0)
            {
                finished = list[0];
                return true;
            }

            return false;
        });

        Assert.NotNull(finished);
        Assert.Equal(0, tracker.TakeUnseenFinished(out _));
    }

    [Fact]
    public async Task Pause_ActiveJob_KeepsJobAsPaused()
    {
        using var tracker =
            new BackgroundDownloadTracker(maxEpisodes: 2, maxSocketsPerHoster: 8, connectionsPerFile: 4);
        var started = new TaskCompletionSource();
        var runs    = 0;
        var id = tracker.Enqueue("Anime",
                                 "Bölüm 5",
                                 async control =>
                                 {
                                     Interlocked.Increment(ref runs);
                                     started.TrySetResult();
#pragma warning disable xUnit1051 // İptal senaryosu iş token'ını gerektirir
                                     await Task.Delay(5000, control.Token);
#pragma warning restore xUnit1051
                                 });

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(tracker.Pause(id));
        await WaitForAsync(() => tracker.Snapshot().First(j => j.Id == id).State == BackgroundJobState.Paused);

        var job = tracker.Snapshot().First(j => j.Id == id);
        Assert.Equal(BackgroundJobState.Paused, job.State);
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task Resume_PausedJob_RerunsWork_ToCompletion()
    {
        using var tracker =
            new BackgroundDownloadTracker(maxEpisodes: 2, maxSocketsPerHoster: 8, connectionsPerFile: 4);
        var started = new TaskCompletionSource();
        var runs    = 0;
        var id = tracker.Enqueue("Anime",
                                 "Bölüm 6",
                                 async control =>
                                 {
                                     if (Interlocked.Increment(ref runs) == 1)
                                     {
                                         started.TrySetResult();
#pragma warning disable xUnit1051 // İptal senaryosu iş token'ını gerektirir
                                         await Task.Delay(5000, control.Token);
#pragma warning restore xUnit1051
                                     }

                                     control.Job.MediaPath = "/tmp/resumed.mp4";
                                 });

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(tracker.Pause(id));
        await WaitForAsync(() => tracker.Snapshot().First(j => j.Id == id).State == BackgroundJobState.Paused);

        Assert.True(tracker.Resume(id));
        await WaitForAsync(() => tracker.Snapshot().First(j => j.Id == id).State == BackgroundJobState.Done);

        var job = tracker.Snapshot().First(j => j.Id == id);
        Assert.Equal(2, runs);
        Assert.Equal("/tmp/resumed.mp4", job.MediaPath);
    }

    [Fact]
    public void Pause_UnknownJob_ReturnsFalse()
    {
        using var tracker =
            new BackgroundDownloadTracker(maxEpisodes: 2, maxSocketsPerHoster: 8, connectionsPerFile: 4);
        Assert.False(tracker.Pause(Guid.NewGuid()));
        Assert.False(tracker.Resume(Guid.NewGuid()));
    }

    [Fact]
    public async Task RemoveWhenSettled_RemovesActiveJobAfterCancel()
    {
        using var tracker =
            new BackgroundDownloadTracker(maxEpisodes: 2, maxSocketsPerHoster: 8, connectionsPerFile: 4);
        var started = new TaskCompletionSource();
        var id = tracker.Enqueue("Anime", "Bölüm 7", async control =>
        {
            started.TrySetResult();
#pragma warning disable xUnit1051 // İptal senaryosu iş token'ını gerektirir
            await Task.Delay(5000, control.Token);
#pragma warning restore xUnit1051
        });

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        tracker.RemoveWhenSettled(id);
        Assert.True(tracker.Cancel(id));
        await WaitForAsync(() => tracker.Snapshot().All(j => j.Id != id));
        Assert.Empty(tracker.Snapshot());
    }

    [Fact]
    public void JobProgressText_FormatsDownloadingDetail()
    {
        var (percent, detail) = JobProgressText.For(
            new DownloadProgress(DownloadStage.Downloading,
                                 50,
                                 100,
                                 1024 * 1024,
                                 null,
                                 null,
                                 TimeSpan.FromSeconds(50)));

        Assert.Equal(50, percent);
        Assert.Contains("İndiriliyor", detail, StringComparison.Ordinal);
        Assert.Contains("/s", detail, StringComparison.Ordinal);
        Assert.Contains("ETA", detail, StringComparison.Ordinal);
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
            {
                Assert.Fail("Timed out waiting for background job state.");
            }

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }
}
