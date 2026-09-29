namespace Migurdex.Cli.Tui;

internal static class TuiConsole
{
    public static CancellationToken AppToken => TuiApplicationCancellation.Token;

    public static bool IsAppExitRequested => TuiApplicationCancellation.Token.IsCancellationRequested;

    public static bool WaitForKey()
    {
        while (!Console.KeyAvailable)
        {
            if (TuiApplicationCancellation.Token.IsCancellationRequested)
            {
                return false;
            }

            Thread.Sleep(50);
        }

        Console.ReadKey(true);
        return true;
    }
}
