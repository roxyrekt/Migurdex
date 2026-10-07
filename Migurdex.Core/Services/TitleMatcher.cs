namespace Migurdex.Core.Services;

internal static class TitleMatcher
{
    public const double MinSimilarity = 0.80;
    public const double AmbiguityGap = 0.05;
    public const double YearBonus = 0.05;
    public const double YearMismatchPenalty = -0.10;
    public const double FormatBonus = 0.03;
    public const double SeasonBonus = 0.05;
    public const double SeasonMismatchPenalty = -0.10;
    public const double ContainmentFloor = 0.90;
    public const double CoveragePenaltyPerToken = 0.08;
    public const double MaxCoveragePenalty = 0.30;

    public static double ScorePair(string a, string b)
    {
        var (aBase, _) = TitleNormalizer.SplitSeason(a);
        var (bBase, _) = TitleNormalizer.SplitSeason(b);
        return Math.Max(
            TitleNormalizer.Ratio(a, b),
            TitleNormalizer.Ratio(
                string.IsNullOrEmpty(aBase) ? a : aBase,
                string.IsNullOrEmpty(bBase) ? b : bBase));
    }

    public static double ApplyCoveragePenalty(double score, string otherTitle, ISet<string> referenceTokens)
    {
        var uncovered = TitleNormalizer.Normalize(otherTitle)
                                       .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                                       .Count(t => !referenceTokens.Contains(t));
        return score - Math.Min(MaxCoveragePenalty, CoveragePenaltyPerToken * uncovered);
    }

    public static bool VariantContained(string variant, string candBase, string[] otherTitles)
    {
        var variantTokens = TitleNormalizer.Normalize(variant)
                                           .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var baseTokens = TitleNormalizer.Normalize(candBase)
                                        .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        foreach (var other in otherTitles)
        {
            var otherTokens = TitleNormalizer.Normalize(other)
                                             .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var (otherBase, _) = TitleNormalizer.SplitSeason(other);
            var otherBaseTokens = otherBase.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (variantTokens.Length >= 2 && IsSubset(variantTokens, otherTokens))
            {
                return true;
            }

            if (baseTokens.Length >= 2 && IsSubset(baseTokens, otherBaseTokens))
            {
                return true;
            }

            if (IsValidQuerySubset(otherTokens, variantTokens))
            {
                return true;
            }

            if (IsValidQuerySubset(otherBaseTokens, baseTokens))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsValidQuerySubset(string[] queryTokens, string[] candidateTokens)
    {
        if (queryTokens.Length == 0 || candidateTokens.Length == 0 || queryTokens.Length > candidateTokens.Length)
        {
            return false;
        }

        if (queryTokens.Length == 1 && queryTokens[0].Length < 5)
        {
            return false;
        }

        return IsSubset(queryTokens, candidateTokens);
    }

    private static bool IsSubset(string[] small, string[] big)
    {
        if (small.Length == 0 || big.Length == 0 || small.Length > big.Length)
        {
            return false;
        }

        var set = new HashSet<string>(big, StringComparer.Ordinal);
        return small.All(set.Contains);
    }
}
