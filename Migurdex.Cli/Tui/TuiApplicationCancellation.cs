namespace Migurdex.Cli.Tui;

public static class TuiApplicationCancellation
{
    private static CancellationTokenSource _source = new();

    public static CancellationToken Token => _source.Token;

    public static void Reset()
    {
        var old = Interlocked.Exchange(ref _source, new CancellationTokenSource());
        old.Dispose();
    }

    public static void RequestCancellation()
    {
        try
        {
            _source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Program sonlanırken gelen son Ctrl+C yoksayılabilir.
        }
    }
}
