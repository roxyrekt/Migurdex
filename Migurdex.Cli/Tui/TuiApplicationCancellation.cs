namespace Migurdex.Cli.Tui;

public static class TuiApplicationCancellation
{
    private static CancellationTokenSource _source = new();
    private static CancellationTokenSource? _modalCts;
    private static int _modalDepth;

    public static CancellationToken Token => _source.Token;

    public static bool IsCancellationRequested => _source.Token.IsCancellationRequested;

    public static bool IsModalActive => Volatile.Read(ref _modalDepth) > 0;

    public static void Reset()
    {
        var old = Interlocked.Exchange(ref _source, new CancellationTokenSource());
        old.Dispose();
        Interlocked.Exchange(ref _modalCts, null);
        Interlocked.Exchange(ref _modalDepth, 0);
    }

    public static void RequestCancellation()
    {
        try
        {
            _source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // ignored
        }
    }

    public static IDisposable BeginModal(CancellationTokenSource modalCts)
    {
        ArgumentNullException.ThrowIfNull(modalCts);
        Interlocked.Exchange(ref _modalCts, modalCts);
        Interlocked.Increment(ref _modalDepth);
        return new ModalScope(modalCts);
    }

    public static bool CancelModal()
    {
        var modal = Volatile.Read(ref _modalCts);
        if (modal is null || modal.IsCancellationRequested)
        {
            return false;
        }

        try
        {
            modal.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private sealed class ModalScope : IDisposable
    {
        private readonly CancellationTokenSource _ownedCts;
        private int _disposed;

        public ModalScope(CancellationTokenSource modalCts)
        {
            _ownedCts = modalCts;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Interlocked.Decrement(ref _modalDepth);
                Interlocked.CompareExchange(ref _modalCts, null, _ownedCts);
            }
        }
    }
}
