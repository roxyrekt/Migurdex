using Spectre.Console;

namespace Migurdex.Cli.Tui;

public static class Toast
{
    public static void Show(string markup, int millis = 800)
    {
        AnsiConsole.MarkupLine(markup);
        Thread.Sleep(Math.Max(0, millis));
    }
}
