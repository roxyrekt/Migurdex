using Spectre.Console;
using System.Diagnostics;

namespace Migurdex.Cli.Services;

public static class BrowserHelper
{
    public static void OpenBrowser(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo(url)
                {
                    UseShellExecute = true
                });
            }
            else if (OperatingSystem.IsMacOS())
            {
                Process.Start("open", url);
            }
            else
            {
                Process.Start("xdg-open", url);
            }
        }
        catch
        {
            AnsiConsole.MarkupLine("[yellow]Tarayıcı açılamadı; kodu manuel girin.[/]");
            AnsiConsole.WriteLine(url);
        }
    }
}
