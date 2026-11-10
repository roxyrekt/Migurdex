using Spectre.Console;
using System.Text;

namespace Migurdex.Cli.Tui;

public sealed class LiveMenuItem
{
    public string  Id            { get; init; } = string.Empty;
    public string  Display       { get; init; } = string.Empty;
    public string  DisplayActive { get; init; } = string.Empty;
    public object? Tag           { get; init; }
    public bool    IsAction      { get; init; }
}

public sealed class LiveMenuFrame
{
    public required string                      Title        { get; init; }
    public          string?                     Subtitle     { get; init; }
    public          IReadOnlyList<string>       HeaderLines  { get; init; } = [];
    public          IReadOnlyList<LiveMenuItem> Items        { get; init; } = [];
    public          string?                     EmptyMarkup  { get; init; }
    public          string                      FooterHelp   { get; init; } = "↑↓ gez • Enter seç • Esc geri";
    public          string?                     PromptMarkup { get; init; }
}

public enum LiveKeyResult
{
    Unhandled,
    Handled,
    Exit
}

public delegate LiveKeyResult LiveMenuKeyHandler(
    ConsoleKeyInfo keyInfo,
    LiveMenuFrame  frame,
    ref int        cursorIndex);

public static class LiveMenu
{
    private const int InputPollSliceMs     = 15;
    private const int DefaultRefreshTickMs = 120;

    public static async Task RunAsync(
        Func<LiveMenuFrame> buildFrame,
        LiveMenuKeyHandler  onKey,
        int                 initialCursorIndex = 0,
        int                 refreshIntervalMs  = DefaultRefreshTickMs)
    {
        ArgumentNullException.ThrowIfNull(buildFrame);
        ArgumentNullException.ThrowIfNull(onKey);

        var cursorIndex     = Math.Max(0, initialCursorIndex);
        var lastFingerprint = string.Empty;
        var tickMs          = Math.Clamp(refreshIntervalMs, InputPollSliceMs, 1000);

        AnsiConsole.Clear();
        Console.Write("\x1b[?25l");

        try
        {
            while (!TuiConsole.IsAppExitRequested)
            {
                var frame = buildFrame();
                if (frame.Items.Count > 0)
                {
                    cursorIndex = Math.Clamp(cursorIndex, 0, frame.Items.Count - 1);
                }
                else
                {
                    cursorIndex = 0;
                }

                var winHeight = TuiConsole.WindowHeight;
                var winWidth  = TuiConsole.WindowWidth;
                var (winStart, winEnd) = ComputeVisibleWindow(frame, cursorIndex, winHeight);

                var fingerprint = ComputeFingerprint(frame, cursorIndex, winStart, winEnd, winWidth, winHeight);
                if (!string.Equals(fingerprint, lastFingerprint, StringComparison.Ordinal))
                {
                    RenderFrame(frame, cursorIndex, winStart, winEnd, winWidth);
                    lastFingerprint = fingerprint;
                }

                var waitedMs = 0;
                while (waitedMs < tickMs && !TuiConsole.IsAppExitRequested)
                {
                    if (Console.KeyAvailable)
                    {
                        var key = Console.ReadKey(true);
                        var res = onKey(key, frame, ref cursorIndex);
                        if (res == LiveKeyResult.Exit)
                        {
                            return;
                        }

                        if (res == LiveKeyResult.Unhandled)
                        {
                            if (HandleDefaultNavigation(key, frame.Items.Count, ref cursorIndex))
                            {
                                break;
                            }
                        }
                        else
                        {
                            break;
                        }
                    }
                    else
                    {
                        await Task.Delay(InputPollSliceMs).ConfigureAwait(false);
                        waitedMs += InputPollSliceMs;
                    }
                }
            }
        }
        finally
        {
            Console.Write("\x1b[?25h");
        }
    }

    internal static (int Start, int End) ComputeVisibleWindow(
        LiveMenuFrame frame,
        int           cursorIndex,
        int           windowHeight)
    {
        var count = frame.Items.Count;
        if (count <= 0)
        {
            return (0, 0);
        }

        var chromeRows = 5 + frame.HeaderLines.Count + (string.IsNullOrWhiteSpace(frame.PromptMarkup) ? 0 : 2);
        var budget     = ListWindow.Budget(windowHeight, chromeRows, count);
        return ListWindow.Compute(count, cursorIndex, budget, _ => false);
    }

    internal static Grid BuildGrid(
        LiveMenuFrame frame,
        int           cursorIndex,
        int           winStart,
        int           winEnd,
        int           windowWidth)
    {
        var colWidth = Math.Max(20, windowWidth - 2);
        var grid     = new Grid();
        grid.AddColumn(new GridColumn().Width(colWidth));

        grid.AddRow(new Markup(Theme.Header(frame.Title, frame.Subtitle)));
        grid.AddRow(new Text(string.Empty));

        foreach (var headerLine in frame.HeaderLines)
        {
            if (string.IsNullOrWhiteSpace(headerLine))
            {
                grid.AddRow(new Text(string.Empty));
            }
            else
            {
                grid.AddRow(new Markup(headerLine));
            }
        }

        if (frame.HeaderLines.Count > 0)
        {
            grid.AddRow(new Text(string.Empty));
        }

        var hasNonActionItems = frame.Items.Any(i => !i.IsAction);
        if (!hasNonActionItems && !string.IsNullOrWhiteSpace(frame.EmptyMarkup))
        {
            grid.AddRow(new Markup(frame.EmptyMarkup));
            grid.AddRow(new Text(string.Empty));
        }

        for (var i = winStart; i < winEnd; i++)
        {
            var item       = frame.Items[i];
            var isSelected = i == cursorIndex;
            grid.AddRow(new Markup(isSelected ? item.DisplayActive : item.Display));
        }

        grid.AddRow(new Text(string.Empty));

        var pos  = frame.Items.Count > 1 ? $" • {cursorIndex + 1}/{frame.Items.Count}" : string.Empty;
        var help = $"[grey]{Markup.Escape(frame.FooterHelp)}{Markup.Escape(pos)}[/]";
        grid.AddRow(new Markup(help));

        if (!string.IsNullOrWhiteSpace(frame.PromptMarkup))
        {
            grid.AddRow(new Text(string.Empty));
            grid.AddRow(new Markup(frame.PromptMarkup));
        }

        return grid;
    }

    internal static string ComputeFingerprint(
        LiveMenuFrame frame,
        int           cursorIndex,
        int           winStart,
        int           winEnd,
        int           windowWidth,
        int           windowHeight)
    {
        var sb = new StringBuilder(512);
        sb.Append(windowWidth).Append('x').Append(windowHeight).Append('|');
        sb.Append(cursorIndex).Append('|').Append(winStart).Append('-').Append(winEnd).Append('|');
        sb.Append(frame.Title).Append('|').Append(frame.Subtitle).Append('|');

        foreach (var h in frame.HeaderLines)
        {
            sb.Append(h).Append('\n');
        }

        sb.Append('|').Append(frame.EmptyMarkup).Append('|');
        for (var i = winStart; i < winEnd; i++)
        {
            var item = frame.Items[i];
            sb.Append(i == cursorIndex ? item.DisplayActive : item.Display).Append('\n');
        }

        sb.Append('|').Append(frame.FooterHelp).Append('|').Append(frame.PromptMarkup);
        return sb.ToString();
    }

    private static void RenderFrame(
        LiveMenuFrame frame,
        int           cursorIndex,
        int           winStart,
        int           winEnd,
        int           windowWidth)
    {
        var grid = BuildGrid(frame, cursorIndex, winStart, winEnd, windowWidth);

        try
        {
            Console.SetCursorPosition(0, 0);
        }
        catch
        {
            // ignored
        }

        Console.Write("\x1b[H");
        AnsiConsole.Write(grid);
        Console.Write("\x1b[J");
    }

    private static bool HandleDefaultNavigation(ConsoleKeyInfo key, int count, ref int cursorIndex)
    {
        if (count <= 0)
        {
            return false;
        }

        switch (key.Key)
        {
            case ConsoleKey.UpArrow:
                cursorIndex = (cursorIndex - 1 + count) % count;
                return true;
            case ConsoleKey.DownArrow:
                cursorIndex = (cursorIndex + 1) % count;
                return true;
            case ConsoleKey.Home:
                cursorIndex = 0;
                return true;
            case ConsoleKey.End:
                cursorIndex = count - 1;
                return true;
            case ConsoleKey.PageUp:
                cursorIndex = Math.Max(0, cursorIndex - 5);
                return true;
            case ConsoleKey.PageDown:
                cursorIndex = Math.Min(count - 1, cursorIndex + 5);
                return true;
            default:
                return false;
        }
    }
}
