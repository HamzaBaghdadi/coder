using System.Text;
using Coder.Core.Files;
using Coder.Core.Text;

namespace Coder.Core.Search;

/// <summary>Replaces every match of a query in a string.</summary>
public static class TextReplacer
{
    /// <summary>Returns the new text and how many matches were replaced (0 means unchanged).</summary>
    public static (string Text, int Count) ReplaceAll(string text, string query, string replacement, SearchOptions options)
    {
        var result = TextSearch.FindAll(text, query, options, maxMatches: int.MaxValue);
        if (result.Error is not null || result.Matches.Count == 0)
        {
            return (text, 0);
        }

        var replacer = TextSearch.CreateReplacer(text, query, replacement, options);
        var builder = new StringBuilder(text.Length);
        var copied = 0;
        foreach (var match in result.Matches)
        {
            builder.Append(text, copied, match.Start - copied).Append(replacer(match));
            copied = match.End;
        }

        builder.Append(text, copied, text.Length - copied);
        return (builder.ToString(), result.Matches.Count);
    }
}

/// <summary>
/// Replaces matches in a file on disk, keeping its encoding (including any BOM) and its dominant line
/// ending style. The file is searched again at replace time, so edits made since the search are respected.
/// </summary>
public sealed class FileReplacer(IFileSystem fileSystem)
{
    /// <summary>Returns the number of replacements written (0 leaves the file untouched).</summary>
    public async Task<int> ReplaceAsync(string path, string query, string replacement, SearchOptions options, CancellationToken cancellationToken = default)
    {
        var content = await fileSystem.ReadTextAsync(path, cancellationToken).ConfigureAwait(false);
        var lineEnding = LineEndings.Detect(content.Text);
        var (text, count) = TextReplacer.ReplaceAll(LineEndings.Normalize(content.Text), query, replacement, options);
        if (count > 0)
        {
            await fileSystem.WriteTextAsync(path, LineEndings.Apply(text, lineEnding), content.Encoding, cancellationToken).ConfigureAwait(false);
        }

        return count;
    }
}
