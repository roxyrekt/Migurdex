using Migurdex.Cli.Services;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Models;
using System.Diagnostics;
using Xunit;

namespace Migurdex.Tests;

public sealed class AnimeciXBatchSourceResolverTests
{
    [Fact]
    public async Task ResolveAsync_SpacesConcurrentLookups()
    {
        var resolver  = new AnimeciXBatchSourceResolver(TimeSpan.FromMilliseconds(40), TimeSpan.Zero);
        var starts    = new List<long>();
        var startLock = new object();

        async Task<DownloadCandidateResult> Resolve(CancellationToken cancellationToken)
        {
            lock (startLock)
            {
                starts.Add(Stopwatch.GetTimestamp());
            }

            await Task.Yield();
            return DirectResult();
        }

        await Task.WhenAll(Enumerable.Range(0, 3)
                                     .Select(_ => resolver.ResolveAsync(Resolve,
                                                                        TestContext.Current.CancellationToken)));

        long[] observed;
        lock (startLock)
        {
            observed = starts.Order().ToArray();
        }

        Assert.Equal(3, observed.Length);
        for (var index = 1; index < observed.Length; index++)
        {
            var elapsed = Stopwatch.GetElapsedTime(observed[index - 1], observed[index]);
            Assert.True(elapsed >= TimeSpan.FromMilliseconds(25),
                        $"AnimeciX lookups began only {elapsed.TotalMilliseconds:F1} ms apart.");
        }
    }

    [Fact]
    public async Task ResolveAsync_RetriesOnceAndUsesTheSecondDirectSource()
    {
        var resolver = new AnimeciXBatchSourceResolver(TimeSpan.Zero, TimeSpan.Zero);
        var calls    = 0;

        var result = await resolver.ResolveAsync(_ =>
        {
            calls++;
            return Task.FromResult(calls == 1 ? EmptyResult() : DirectResult());
        }, TestContext.Current.CancellationToken);

        Assert.Equal(2, calls);
        Assert.False(result.TimedOut);
        Assert.Null(result.Error);
        Assert.Single(result.Candidates);
    }

    [Fact]
    public async Task ResolveAsync_ReportsProviderSpecificFailureAfterTwoEmptyResults()
    {
        var resolver = new AnimeciXBatchSourceResolver(TimeSpan.Zero, TimeSpan.Zero);
        var calls    = 0;

        var result = await resolver.ResolveAsync(_ =>
        {
            calls++;
            return Task.FromResult(EmptyResult());
        }, TestContext.Current.CancellationToken);

        Assert.Equal(2, calls);
        Assert.Contains("AnimeciX", result.Error, StringComparison.Ordinal);
        Assert.Contains("bu denemede doğrudan MP4/HLS kaynak gelmedi", result.Error, StringComparison.Ordinal);
        Assert.Contains("ikinci denemede", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveAsync_CancellationDuringRetryDelayStopsBeforeSecondLookup()
    {
        var resolver = new AnimeciXBatchSourceResolver(TimeSpan.Zero, TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        var calls = 0;

        var lookup = resolver.ResolveAsync(_ =>
        {
            calls++;
            cancellation.CancelAfter(TimeSpan.FromMilliseconds(20));
            return Task.FromResult(EmptyResult());
        }, cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lookup);
        Assert.Equal(1, calls);
    }

    private static DownloadCandidateResult EmptyResult()
    {
        return new DownloadCandidateResult
        {
            Sources    = [],
            Candidates = [],
            TimedOut   = false
        };
    }

    private static DownloadCandidateResult DirectResult()
    {
        var source = new VideoSource { Url = "https://media.example/episode.mp4", Type = VideoType.Mp4 };
        return new DownloadCandidateResult
        {
            Sources    = [source],
            Candidates = [source],
            TimedOut   = false
        };
    }
}
