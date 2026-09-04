using Spectre.Console;

namespace Migurdex.Cli.Tui;

public static class Toast
{
    public static void Show(string markup, int millis = 800)
    {
        AnsiConsole.MarkupLine(markup);

        var waited = 0;
        while (waited < millis)
        {
            if (Console.KeyAvailable)
            {
                Console.ReadKey(true);
                return;
            }

            Thread.Sleep(25);
            waited += 25;
        }
    }
}
