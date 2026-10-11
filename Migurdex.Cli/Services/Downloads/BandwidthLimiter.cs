namespace Migurdex.Cli.Services.Downloads;

public sealed class BandwidthLimiter
{
    private static readonly AsyncLocal<BandwidthLimiter?> _current = new();

    public static BandwidthLimiter? Current
    {
        get => _current.Value;
        private set => _current.Value = value;
    }

    private readonly Func<long>     _limitBytesPerSecondProvider;
    private readonly Lock           _gate = new();
    private          double         _tokens;
    private          DateTimeOffset _lastRefill = DateTimeOffset.UtcNow;

    public BandwidthLimiter(Func<long> limitBytesPerSecondProvider)
    {
        _limitBytesPerSecondProvider = limitBytesPerSecondProvider
                                       ?? throw new ArgumentNullException(nameof(limitBytesPerSecondProvider));
    }

    public long LimitBytesPerSecond => Math.Max(0, _limitBytesPerSecondProvider());

    public static IDisposable Activate(BandwidthLimiter? limiter)
    {
        var previous = Current;
        Current = limiter is not null && limiter.LimitBytesPerSecond > 0 ? limiter : null;

        return new Scope(previous);
    }

    private sealed class Scope(BandwidthLimiter? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Current   = previous;
        }
    }

    public async Task ConsumeAsync(int bytes, CancellationToken cancellationToken = default)
    {
        if (bytes <= 0)
        {
            return;
        }

        var rate = LimitBytesPerSecond;
        if (rate <= 0)
        {
            return;
        }

        var      capacity = Math.Max(64 * 1024, (double) rate);
        TimeSpan wait;
        lock (_gate)
        {
            var now     = DateTimeOffset.UtcNow;
            var elapsed = (now - _lastRefill).TotalSeconds;
            if (elapsed > 0)
            {
                _tokens     = Math.Min(capacity, _tokens + elapsed * rate);
                _lastRefill = now;
            }

            if (_tokens >= bytes)
            {
                _tokens -= bytes;
                return;
            }

            wait        = TimeSpan.FromSeconds((bytes - _tokens) / rate);
            _tokens     = 0;
            _lastRefill = DateTimeOffset.UtcNow;
        }

        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
        }
    }
}
