using Migurdex.Core.Interop;

namespace Migurdex.Core.Utils;

public static class FuzzyMatcher
{
    public static double CalculateSimilarity(string source, string target)
    {
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(target))
        {
            return 0;
        }

        if (RustBridge.IsInitialized)
        {
            var rustResult = RustBridge.CalculateFuzzySimilarity(source, target);
            if (rustResult.HasValue)
            {
                return rustResult.Value;
            }
        }

        source = source.ToLowerInvariant().Trim();
        target = target.ToLowerInvariant().Trim();

        if (source == target)
        {
            return 1.0;
        }

        var n = source.Length;
        var m = target.Length;
        var d = new int[n + 1, m + 1];

        for (var i = 0; i <= n; d[i, 0] = i++)
        {
            ;
        }

        for (var j = 0; j <= m; d[0, j] = j++)
        {
            ;
        }

        for (var i = 1; i <= n; i++)
        {
            for (var j = 1; j <= m; j++)
            {
                var cost = target[j - 1] == source[i - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            }
        }

        double maxLen = Math.Max(n, m);

        return 1.0 - (d[n, m] / maxLen);
    }
}
