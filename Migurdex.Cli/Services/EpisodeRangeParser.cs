using System.Globalization;

namespace Migurdex.Cli.Services;

public static class EpisodeRangeParser
{
    public const int MaxEpisodes = 500;

    public static bool TryParse(string? value, out List<double> episodes, out bool selectAll)
    {
        episodes  = [];
        selectAll = false;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            selectAll = true;
            return true;
        }

        foreach (var part in trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryParsePart(part, episodes))
            {
                episodes  = [];
                selectAll = false;
                return false;
            }

            if (episodes.Count > MaxEpisodes)
            {
                episodes  = [];
                selectAll = false;
                return false;
            }
        }

        episodes.Sort();
        return episodes.Count > 0 && episodes.Count <= MaxEpisodes;
    }

    public static bool TryParse(string? value, out List<double> episodes)
    {
        return TryParse(value, out episodes, out _);
    }

    private static bool TryParsePart(string part, List<double> episodes)
    {
        if (string.IsNullOrWhiteSpace(part))
        {
            return false;
        }

        var rangeSeparator = FindRangeSeparator(part);
        if (rangeSeparator is not null)
        {
            var (a, b) = rangeSeparator.Value;
            var fromText = part[..a].Trim();
            var toText   = part[(a + b)..].Trim();
            if (!double.TryParse(fromText, NumberStyles.Float, CultureInfo.InvariantCulture, out var from)
                || !double.TryParse(toText, NumberStyles.Float, CultureInfo.InvariantCulture, out var to)
                || !double.IsFinite(from)
                || !double.IsFinite(to)
                || from <= 0
                || to < from
                || to - from > MaxEpisodes)
            {
                return false;
            }

            var step = from % 1 == 0 && to % 1 == 0 ? 1.0 : 0.5;
            for (var e = from; e <= to; e += step)
            {
                if (!episodes.Contains(e))
                {
                    episodes.Add(e);
                }

                if (episodes.Count > MaxEpisodes)
                {
                    return false;
                }
            }

            return true;
        }

        if (double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var single)
            && double.IsFinite(single)
            && single > 0)
        {
            if (!episodes.Contains(single))
            {
                episodes.Add(single);
            }

            return true;
        }

        return false;
    }

    private static (int Index, int Length)? FindRangeSeparator(string part)
    {
        var doubleDot = part.IndexOf("..", StringComparison.Ordinal);
        if (doubleDot > 0)
        {
            return (doubleDot, 2);
        }

        var dash = part.IndexOf('-');
        if (dash > 0)
        {
            return (dash, 1);
        }

        return null;
    }
}
