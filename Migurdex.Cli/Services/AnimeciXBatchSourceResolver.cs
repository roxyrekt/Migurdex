using Migurdex.Shared.Models;
using System.Diagnostics;

namespace Migurdex.Cli.Services;

/// <summary>
/// AnimeciX batch source lookups are paced independently from media downloads. A missing direct
/// source gets one delayed retry; provider errors, timeouts and cancellation keep their existing
/// behavior.
/// </summary>
public sealed class AnimeciXBatchSourceResolver
{
    private static readonly AnimeciXBatchSourceResolver SharedResolver = new(TimeSpan.FromSeconds(5),
                                                                            TimeSpan.FromSeconds(15));

    private readonly SemaphoreSlim _scheduleLock = new(1, 1);
    private readonly TimeSpan      _minimumInterval;
    private readonly TimeSpan      _retryDelay;
    private          long?           _lastRequestStarted;

    public AnimeciXBatchSourceResolver(TimeSpan minimumInterval, TimeSpan retryDelay)
    {
        if (minimumInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumInterval));
        }

        if (retryDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retryDelay));
        }

        _minimumInterval = minimumInterval;
        _retryDelay      = retryDelay;
    }

    internal static AnimeciXBatchSourceResolver Shared => SharedResolver;

    public async Task<DownloadCandidateResult> ResolveAsync(
        Func<CancellationToken, Task<DownloadCandidateResult>> resolve,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resolve);

        var first = await ResolveAtPacedStartAsync(resolve, cancellationToken).ConfigureAwait(false);
        if (first.TimedOut || first.Error is not null || HasDirectSource(first))
        {
            return first;
        }

        await Task.Delay(_retryDelay, cancellationToken).ConfigureAwait(false);
        var second = await ResolveAtPacedStartAsync(resolve, cancellationToken).ConfigureAwait(false);
        if (second.TimedOut || second.Error is not null || HasDirectSource(second))
        {
            return second;
        }

        return new DownloadCandidateResult
        {
            Sources    = second.Sources,
            Candidates = second.Candidates,
            TimedOut   = false,
            Error      = "AnimeciX sağlayıcısından bu denemede doğrudan MP4/HLS kaynak gelmedi; "
                         + "ikinci denemede de doğrudan kaynak bulunamadı."
        };
    }

    private async Task<DownloadCandidateResult> ResolveAtPacedStartAsync(
        Func<CancellationToken, Task<DownloadCandidateResult>> resolve,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            await _scheduleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            TimeSpan wait;
            Task<DownloadCandidateResult>? pending = null;
            try
            {
                var now = Stopwatch.GetTimestamp();
                wait = _lastRequestStarted is { } last
                           ? _minimumInterval - Stopwatch.GetElapsedTime(last, now)
                           : TimeSpan.Zero;

                if (wait <= TimeSpan.Zero)
                {
                    _lastRequestStarted = now;
                    pending = resolve(cancellationToken);
                }
            }
            finally
            {
                _scheduleLock.Release();
            }

            if (pending is not null)
            {
                return await pending.ConfigureAwait(false);
            }

            await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool HasDirectSource(DownloadCandidateResult result)
    {
        return result.Candidates.Count > 0 || result.Sources.Any(DownloadSourceResolver.IsDirectDownloadable);
    }
}
