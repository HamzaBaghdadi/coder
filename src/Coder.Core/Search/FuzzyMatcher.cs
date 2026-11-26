namespace Coder.Core.Search;

/// <summary>
/// Subsequence matching for quick-open and the command palette ("wbvm" finds "WorkbenchViewModel").
/// Scores reward consecutive characters and matches at word starts, so the best candidates rise first.
/// </summary>
public static class FuzzyMatcher
{
    /// <summary>Returns a score (higher is better) or null when <paramref name="query"/> does not match.</summary>
    public static int? Score(ReadOnlySpan<char> query, ReadOnlySpan<char> candidate)
    {
        if (query.IsEmpty)
        {
            return 0;
        }

        var score = 0;
        var queryIndex = 0;
        var previousMatch = -2;
        for (var i = 0; i < candidate.Length && queryIndex < query.Length; i++)
        {
            if (char.ToLowerInvariant(candidate[i]) != char.ToLowerInvariant(query[queryIndex]))
            {
                continue;
            }

            score += 1;
            if (i == previousMatch + 1)
            {
                score += 5; // consecutive
            }

            if (IsWordStart(candidate, i))
            {
                score += 8;
            }

            if (candidate[i] == query[queryIndex])
            {
                score += 1; // exact case
            }

            previousMatch = i;
            queryIndex++;
        }

        if (queryIndex < query.Length)
        {
            return null;
        }

        // Prefer shorter candidates when everything else is equal.
        return score * 100 - candidate.Length;
    }

    /// <summary>The candidate indices <see cref="Score"/> matched, for highlighting; null when it does not match.</summary>
    public static int[]? MatchedIndices(ReadOnlySpan<char> query, ReadOnlySpan<char> candidate)
    {
        var indices = new int[query.Length];
        var queryIndex = 0;
        for (var i = 0; i < candidate.Length && queryIndex < query.Length; i++)
        {
            if (char.ToLowerInvariant(candidate[i]) == char.ToLowerInvariant(query[queryIndex]))
            {
                indices[queryIndex++] = i;
            }
        }

        return queryIndex == query.Length ? indices : null;
    }

    private static bool IsWordStart(ReadOnlySpan<char> text, int index)
    {
        if (index == 0)
        {
            return true;
        }

        var previous = text[index - 1];
        return previous is ' ' or '_' or '-' or '.' or '/' or '\\' or ':'
               || (char.IsUpper(text[index]) && char.IsLower(previous));
    }
}
