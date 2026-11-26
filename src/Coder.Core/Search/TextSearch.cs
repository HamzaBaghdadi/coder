using System.Text.RegularExpressions;
using Coder.Core.Editing;

namespace Coder.Core.Search;

public sealed record SearchOptions(bool MatchCase = false, bool WholeWord = false, bool UseRegex = false);

/// <summary>A found occurrence: document offset and length.</summary>
public readonly record struct SearchMatch(int Start, int Length)
{
    public int End => Start + Length;
}

/// <summary>The outcome of a search: matches in document order, or why the query is invalid.</summary>
public sealed record SearchResult(IReadOnlyList<SearchMatch> Matches, bool IsTruncated, string? Error)
{
    public static readonly SearchResult Empty = new([], false, null);
}

/// <summary>
/// Finds every occurrence of a query in a text. Plain queries use a fast ordinal scan; regular
/// expressions run with a timeout so a pathological pattern can never freeze the editor.
/// </summary>
public static class TextSearch
{
    /// <summary>Upper bound on reported matches, keeping highlight memory and drawing cost bounded.</summary>
    public const int MaxMatches = 20_000;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(500);

    /// <param name="maxMatches">Stop after this many; highlighting uses the default, replacing passes no limit.</param>
    public static SearchResult FindAll(string text, string query, SearchOptions options, int maxMatches = MaxMatches)
    {
        if (query.Length == 0)
        {
            return SearchResult.Empty;
        }

        return options.UseRegex ? FindRegex(text, query, options, maxMatches) : FindPlain(text, query, options, maxMatches);
    }

    /// <summary>
    /// Returns a function giving the replacement text for each match of <paramref name="text"/>. For
    /// regex searches, <c>$1</c>-style group references are expanded. The regex is built only once.
    /// </summary>
    public static Func<SearchMatch, string> CreateReplacer(string text, string query, string replacement, SearchOptions options)
    {
        if (!options.UseRegex)
        {
            return _ => replacement;
        }

        var regex = CreateRegex(query, options);
        return match =>
        {
            var found = regex.Match(text, match.Start);
            return found.Success && found.Index == match.Start ? found.Result(replacement) : replacement;
        };
    }

    private static SearchResult FindPlain(string text, string query, SearchOptions options, int maxMatches)
    {
        var comparison = options.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var matches = new List<SearchMatch>();
        for (var index = text.IndexOf(query, comparison); index >= 0; index = text.IndexOf(query, index + query.Length, comparison))
        {
            if (options.WholeWord && !IsWholeWord(text, index, query.Length))
            {
                continue;
            }

            matches.Add(new SearchMatch(index, query.Length));
            if (matches.Count == maxMatches)
            {
                return new SearchResult(matches, IsTruncated: true, null);
            }
        }

        return new SearchResult(matches, false, null);
    }

    private static SearchResult FindRegex(string text, string query, SearchOptions options, int maxMatches)
    {
        Regex regex;
        try
        {
            regex = CreateRegex(query, options);
        }
        catch (ArgumentException exception)
        {
            return new SearchResult([], false, exception.Message);
        }

        var matches = new List<SearchMatch>();
        try
        {
            for (var match = regex.Match(text); match.Success; match = match.NextMatch())
            {
                // Empty matches (e.g. "^") are skipped: they cannot be selected or highlighted.
                if (match.Length == 0)
                {
                    continue;
                }

                matches.Add(new SearchMatch(match.Index, match.Length));
                if (matches.Count == maxMatches)
                {
                    return new SearchResult(matches, IsTruncated: true, null);
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return new SearchResult(matches, IsTruncated: true, "The expression took too long to evaluate.");
        }

        return new SearchResult(matches, false, null);
    }

    private static Regex CreateRegex(string query, SearchOptions options)
    {
        var pattern = options.WholeWord ? $@"\b(?:{query})\b" : query;
        var flags = RegexOptions.Multiline | RegexOptions.CultureInvariant;
        if (!options.MatchCase)
        {
            flags |= RegexOptions.IgnoreCase;
        }

        return new Regex(pattern, flags, RegexTimeout);
    }

    private static bool IsWholeWord(string text, int start, int length)
    {
        var before = start == 0 || !CharClassifier.IsWordChar(text[start - 1]);
        var after = start + length >= text.Length || !CharClassifier.IsWordChar(text[start + length]);
        return before && after;
    }
}
