namespace Migurdex.Cli.Tui;

public static class FuzzyMatcher
{
    private const int StartBonus      = 50;
    private const int WordStartBonus  = 30;
    private const int AdjacentBonus   = 20;
    private const int StreakStep      = 5;
    private const int FirstGapPenalty = 3;
    private const int GapPenalty      = 2;

    private static bool IsWordSeparator(char c)
    {
        return c is ' ' or '-'
               || c == '('
               || c == ')'
               || c == '|'
               || c == '#'
               || c == '/'
               || c == '_'
               || c == '.'
               || c == ':'
               || c == ',';
    }

    private static int? ScoreToken(string foldedText, string foldedToken)
    {
        if (foldedToken.Length == 0)
        {
            return 0;
        }

        var score      = 0;
        var textIdx    = 0;
        var prevMatch  = -2;
        var streak     = 0;
        var firstMatch = true;

        foreach (var qc in foldedToken)
        {
            var found = false;
            while (textIdx < foldedText.Length)
            {
                var tc = foldedText[textIdx];
                textIdx++;

                if (tc != qc)
                {
                    continue;
                }

                var matchIdx = textIdx - 1;

                if (firstMatch)
                {
                    score      -= matchIdx * FirstGapPenalty;
                    firstMatch =  false;
                }
                else
                {
                    score -= (matchIdx - prevMatch - 1) * GapPenalty;
                }

                if (matchIdx == 0 || IsWordSeparator(foldedText[matchIdx - 1]))
                {
                    score += matchIdx == 0 ? StartBonus : WordStartBonus;
                }

                if (matchIdx == prevMatch + 1)
                {
                    streak += 1;
                    score  += AdjacentBonus + (streak * StreakStep);
                }
                else
                {
                    streak = 0;
                }

                prevMatch = matchIdx;
                found     = true;
                break;
            }

            if (!found)
            {
                return null;
            }
        }

        return score;
    }

    private static int? Score(string text, string query)
    {
        if (string.IsNullOrEmpty(query))
        {
            return 0;
        }

        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var foldedText = text.ToUpperInvariant();
        var total      = 0;

        var tokens = query.Split([' '], StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return 0;
        }

        foreach (var token in tokens)
        {
            var tokenScore = ScoreToken(foldedText, token.ToUpperInvariant());
            if (tokenScore is null)
            {
                return null;
            }

            total += tokenScore.Value;
        }

        return total;
    }

    public static List<FuzzyChoice> Rank(IReadOnlyList<FuzzyChoice> choices, string query)
    {
        if (string.IsNullOrEmpty(query))
        {
            return [.. choices];
        }

        return choices.Select((c, idx) => (choice: c, index: idx, score: Score(c.Searchable, query)))
                      .Where(x => x.score is not null)
                      .OrderByDescending(x => x.score!.Value)
                      .ThenBy(x => x.index)
                      .Select(x => x.choice)
                      .ToList();
    }
}
