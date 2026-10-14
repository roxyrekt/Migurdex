namespace Migurdex.Cli.Tui;

/// <summary>
/// Pure windowing math that keeps long lists inside the visible terminal height.
/// </summary>
internal static class ListWindow
{
    /// <summary>
    /// Reserves fixed screen rows and overflow slack before choosing the visible list size.
    /// </summary>
    public static int Budget(int windowHeight, int chromeRows, int maxRows)
    {
        // Keep one row available for a section heading and one so the final newline does not scroll.
        return Math.Min(Math.Max(1, maxRows), Math.Max(1, windowHeight - chromeRows - 2));
    }

    /// <summary>
    /// Returns a [Start, End) range containing the cursor and, when needed, its section heading.
    /// </summary>
    public static (int Start, int End) Compute(
        int count,
        int cursorIndex,
        int budget,
        Func<int, bool>? isSection = null)
    {
        if (count <= 0)
        {
            return (0, 0);
        }

        var span = Math.Clamp(budget, 1, count);
        cursorIndex = Math.Clamp(cursorIndex, 0, count - 1);

        var start = Math.Max(0, cursorIndex - (span / 2));
        var end = Math.Min(count, start + span);

        if (end - start < span)
        {
            start = Math.Max(0, end - span);
        }

        if (isSection is not null && start > 0 && !isSection(start) && isSection(start - 1))
        {
            // Pull the section header into view; Budget reserves room for this additional row.
            start--;
            end = Math.Min(count, Math.Max(start + span, cursorIndex + 1));
        }

        return (start, end);
    }
}
