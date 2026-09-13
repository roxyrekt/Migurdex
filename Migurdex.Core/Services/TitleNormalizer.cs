using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Migurdex.Core.Services;

public static partial class TitleNormalizer
{
    public static string Normalize(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var s = title.Trim().ToLowerInvariant();

        if (s.EndsWith(" izle", StringComparison.Ordinal))
        {
            s = s[..^5].TrimEnd();
        }

        var formD = s.Normalize(NormalizationForm.FormD);
        var sb    = new StringBuilder(formD.Length);
        foreach (var c in formD)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(c);
            }
        }

        s = PunctuationRegex().Replace(sb.ToString(), " ");
        s = WhitespaceRegex().Replace(s, " ").Trim();
        return s.Normalize(NormalizationForm.FormC);
    }

    public static (string BaseTitle, int Season) SplitSeason(string? title)
    {
        var normalized = Normalize(title);
        var season     = ParseSeason(normalized);
        var baseTitle  = SeasonSuffixRegex().Replace(normalized, " ").Trim();
        baseTitle = WhitespaceRegex().Replace(baseTitle, " ").Trim();
        return (baseTitle, season);
    }

    private static int ParseSeason(string normalized)
    {
        // "season 2", "2nd season", "sezon 2", "2. sezon", "part 2", "s2"
        var m = Regex.Match(normalized, @"\b(?:season|sezon|part|k[ıi]s[ıi]m)\s*(\d{1,2})(?:st|nd|rd|th)?\b");
        if (m.Success && int.TryParse(m.Groups[1].Value, out var n))
        {
            return n;
        }

        m = Regex.Match(normalized, @"\b(\d{1,2})(?:st|nd|rd|th)?\s*\.?\s*(?:season|sezon|part|k[ıi]s[ıi]m)\b");
        if (m.Success && int.TryParse(m.Groups[1].Value, out var n2))
        {
            return n2;
        }

        m = Regex.Match(normalized, @"\bs(\d{1,2})\b");
        if (m.Success && int.TryParse(m.Groups[1].Value, out var s))
        {
            return s;
        }

        return 1;
    }

    public static double Ratio(string a, string b)
    {
        var x = Normalize(a);
        var y = Normalize(b);

        if (x.Length == 0 && y.Length == 0)
        {
            return 1.0;
        }

        if (x.Length == 0 || y.Length == 0)
        {
            return 0.0;
        }

        if (x == y)
        {
            return 1.0;
        }

        var dist = Distance(x, y);
        return Math.Max(0.0, (x.Length + y.Length - 2 * dist) / (double)(x.Length + y.Length));
    }

    private static int Distance(string x, string y)
    {
        if (x.Length < y.Length)
        {
            (x, y) = (y, x);
        }

        var prev = new int[y.Length + 1];
        var curr = new int[y.Length + 1];

        for (var j = 0; j <= y.Length; j++)
        {
            prev[j] = j;
        }

        for (var i = 1; i <= x.Length; i++)
        {
            curr[0] = i;
            for (var j = 1; j <= y.Length; j++)
            {
                var cost = x[i - 1] == y[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }

            (prev, curr) = (curr, prev);
        }

        return prev[y.Length];
    }

    [GeneratedRegex(@"[^\p{L}\p{N}\s]")]
    private static partial Regex PunctuationRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(
        @"\b\d{1,2}(?:st|nd|rd|th)?\s*\.?\s*(?:season|sezon|part|k[ıi]s[ıi]m)\b|\b(?:season|sezon|part|k[ıi]s[ıi]m)\s*\d{1,2}(?:st|nd|rd|th)?\b|\bs\d{1,2}\b")]
    private static partial Regex SeasonSuffixRegex();
}
