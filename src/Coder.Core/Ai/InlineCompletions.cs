using System.Text;

namespace Coder.Core.Ai;

/// <summary>
/// Where an inline completion is asked for: the file (its path and language) and the text around the
/// caret, already cut to what is sent (see <see cref="InlineCompletionPrompt.Context"/>).
/// </summary>
public sealed record InlineCompletionContext(string FilePath, string LanguageId, string Prefix, string Suffix)
{
    /// <summary>The caret line's text before the caret.</summary>
    public string LinePrefix => Prefix[(Prefix.LastIndexOf('\n') + 1)..];

    /// <summary>The caret line's text after the caret.</summary>
    public string LineSuffix => Suffix.IndexOf('\n') is var end and >= 0 ? Suffix[..end] : Suffix;
}

/// <summary>
/// The fill-in-the-middle prompt for chat models: the file with a marker at the caret, and the rules
/// for answering (only the code to insert, no fences, nothing repeated from around the caret).
/// </summary>
public static class InlineCompletionPrompt
{
    /// <summary>The marker put where the caret is.</summary>
    public const string Marker = "<CURSOR>";

    /// <summary>Text before the caret that is sent at most (the file's start when shorter).</summary>
    public const int MaxPrefixChars = 6_000;

    /// <summary>Text after the caret that is sent at most.</summary>
    public const int MaxSuffixChars = 2_000;

    /// <summary>The longest answer asked for: a dozen lines or so, with room for a model that thinks briefly first.</summary>
    public const int MaxTokens = 512;

    public const string SystemPrompt =
        "You are a code completion engine inside a code editor, like GitHub Copilot. You get one file with the marker "
        + Marker + " where the user's caret is. Reply with ONLY the text to insert at the marker so the code continues naturally: "
        + "finish the current line, and add more lines only when they clearly belong (the rest of a statement, block or function). "
        + "Never repeat text that is already before or after the marker, never explain, never use Markdown or code fences. "
        + "Keep the file's indentation and style. If nothing should be inserted, reply with an empty message.";

    /// <summary>
    /// The context for a caret at <paramref name="caret"/> in <paramref name="text"/>: at most
    /// <see cref="MaxPrefixChars"/> before it and <see cref="MaxSuffixChars"/> after it, cut at whole lines.
    /// </summary>
    public static InlineCompletionContext Context(string filePath, string languageId, string text, int caret)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        var prefixStart = Math.Max(0, caret - MaxPrefixChars);
        if (prefixStart > 0 && text.IndexOf('\n', prefixStart, caret - prefixStart) is var lineBreak and >= 0)
        {
            prefixStart = lineBreak + 1; // whole lines only
        }

        var suffixEnd = Math.Min(text.Length, caret + MaxSuffixChars);
        if (suffixEnd < text.Length && text.LastIndexOf('\n', suffixEnd - 1, suffixEnd - caret) is var lastBreak and > 0 && lastBreak > caret)
        {
            suffixEnd = lastBreak;
        }

        return new InlineCompletionContext(filePath, languageId, text[prefixStart..caret], text[caret..suffixEnd]);
    }

    /// <summary>The chat request for <paramref name="context"/>.</summary>
    public static ChatRequest Build(string model, InlineCompletionContext context)
    {
        var user = new StringBuilder()
            .Append("File: ").Append(context.FilePath.Length > 0 ? context.FilePath : "untitled")
            .Append(" (language: ").Append(context.LanguageId.Length > 0 ? context.LanguageId : "plain text").Append(")\n\n")
            .Append(context.Prefix).Append(Marker).Append(context.Suffix)
            .Append("\n\nReply with only the text that goes at ").Append(Marker).Append('.')
            .ToString();
        return new ChatRequest(model, [ChatMessage.System(SystemPrompt), ChatMessage.User(user)])
        {
            Temperature = 0.2,
            MaxTokens = MaxTokens,
        };
    }
}

/// <summary>Turns a model's reply into the text to show after the caret, and splits it for partial accepts.</summary>
public static class InlineCompletionText
{
    /// <summary>Suggestions are cut to this many lines.</summary>
    public const int MaxLines = 16;

    /// <summary>
    /// The text to insert, from a model's raw reply: reasoning and code fences removed, text the model
    /// repeated from before or after the caret dropped, cut to <see cref="MaxLines"/>. Null when nothing is left.
    /// </summary>
    public static string? Clean(string reply, InlineCompletionContext context)
    {
        var text = reply.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        text = StripReasoning(text);
        if (text is null)
        {
            return null;
        }

        text = Unfence(text).Replace(InlineCompletionPrompt.Marker, string.Empty, StringComparison.Ordinal);
        text = DropEchoedPrefix(text, context);
        text = DropRepeatedSuffix(text, context);
        text = CutLines(text, MaxLines).TrimEnd();

        // Only whitespace, or the start of a line that only re-indents: nothing worth showing.
        return text.Trim().Length == 0 ? null : text;
    }

    /// <summary>
    /// How much of <paramref name="text"/> Ctrl+Right accepts: leading spaces (or one line break and the
    /// next line's indentation), then a word, or else a run of punctuation.
    /// </summary>
    public static int NextWordLength(string text)
    {
        var i = 0;
        if (i < text.Length && text[i] == '\n')
        {
            i++;
        }

        while (i < text.Length && text[i] is ' ' or '\t')
        {
            i++;
        }

        if (i < text.Length && IsWord(text[i]))
        {
            while (i < text.Length && IsWord(text[i]))
            {
                i++;
            }
        }
        else
        {
            while (i < text.Length && !IsWord(text[i]) && !char.IsWhiteSpace(text[i]))
            {
                i++;
            }
        }

        return Math.Max(i, Math.Min(1, text.Length));
    }

    private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>Drops <c>&lt;think&gt;…&lt;/think&gt;</c>; null while the reasoning never ended (nothing usable).</summary>
    private static string? StripReasoning(string text)
    {
        while (text.IndexOf("<think>", StringComparison.Ordinal) is var start and >= 0)
        {
            var end = text.IndexOf("</think>", start, StringComparison.Ordinal);
            if (end < 0)
            {
                return null;
            }

            text = text[..start] + text[(end + "</think>".Length)..].TrimStart('\n');
        }

        return text;
    }

    /// <summary>The inside of the first fenced block, when the reply has one (models like Markdown even when told not to).</summary>
    private static string Unfence(string text)
    {
        var open = text.IndexOf("```", StringComparison.Ordinal);
        if (open < 0)
        {
            return text;
        }

        var bodyStart = text.IndexOf('\n', open);
        if (bodyStart < 0)
        {
            return string.Empty; // a fence and nothing else
        }

        var close = text.IndexOf("```", bodyStart + 1, StringComparison.Ordinal);
        var body = close < 0 ? text[(bodyStart + 1)..] : text[(bodyStart + 1)..close];
        return body.EndsWith('\n') ? body[..^1] : body;
    }

    /// <summary>
    /// Models often restate the caret's line ("  int x" → "int x = 5;") or its indentation: the part
    /// already typed is removed so only what is new remains.
    /// </summary>
    private static string DropEchoedPrefix(string text, InlineCompletionContext context)
    {
        var line = context.LinePrefix;
        var typed = line.TrimStart();

        // The line before the caret's repeated (the model started over from it): keep what follows it.
        var before = context.Prefix[..^line.Length].TrimEnd().Split('\n')[^1].Trim();
        if (typed.Length == 0 && before.Length >= 4)
        {
            var lines = text.Split('\n');
            var match = Array.FindIndex(lines, 0, Math.Min(3, lines.Length), l => l.Trim() == before);
            if (match >= 0 && match < lines.Length - 1)
            {
                text = string.Join('\n', lines[(match + 1)..]);
            }
        }

        if (line.Length == 0)
        {
            return text;
        }

        if (text.StartsWith(line, StringComparison.Ordinal))
        {
            return text[line.Length..];
        }

        var trimmed = text.TrimStart(' ', '\t');
        if (typed.Length > 0 && trimmed.StartsWith(typed, StringComparison.Ordinal))
        {
            return trimmed[typed.Length..];
        }

        // The word being typed repeated whole ("<li>Ap" → "Apples</li>"): keep the rest of the word.
        var word = 0;
        while (word < line.Length && (char.IsLetterOrDigit(line[^(word + 1)]) || line[^(word + 1)] == '_'))
        {
            word++;
        }

        return word >= 2 && text.StartsWith(line[^word..], StringComparison.Ordinal) ? text[word..] : text;
    }

    /// <summary>
    /// Drops the end of the reply when it repeats what already follows the caret: the rest of the line
    /// (a closing ")" or ";") or the next lines (a closing brace).
    /// </summary>
    private static string DropRepeatedSuffix(string text, InlineCompletionContext context)
    {
        var rest = context.LineSuffix.Trim();
        if (rest.Length > 0)
        {
            var firstLineEnd = text.IndexOf('\n') is var nl and >= 0 ? nl : text.Length;
            var firstLine = text[..firstLineEnd].TrimEnd();
            if (firstLine.EndsWith(rest, StringComparison.Ordinal))
            {
                // A single-line answer that closes the line itself: keep only what goes before the existing rest.
                return firstLineEnd == text.Length ? firstLine[..^rest.Length] : text;
            }
        }

        var following = context.Suffix.Split('\n').Skip(1).Select(l => l.Trim()).Where(l => l.Length > 0).Take(8).ToList();
        if (following.Count == 0)
        {
            return text;
        }

        var lines = text.TrimEnd().Split('\n').ToList();
        for (var count = Math.Min(following.Count, lines.Count - 1); count > 0; count--)
        {
            var tail = lines.TakeLast(count).Select(l => l.Trim()).ToList();
            if (tail.SequenceEqual(following.Take(count)))
            {
                lines.RemoveRange(lines.Count - count, count);
                return string.Join('\n', lines);
            }
        }

        return text;
    }

    private static string CutLines(string text, int maxLines)
    {
        var index = -1;
        for (var line = 0; line < maxLines; line++)
        {
            index = text.IndexOf('\n', index + 1);
            if (index < 0)
            {
                return text;
            }
        }

        return text[..index];
    }
}

/// <summary>Asks a chat model for the text to insert at the caret.</summary>
public sealed class InlineCompletionEngine
{
    /// <summary>A request taking longer than this fails (the caret has likely moved on anyway).</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The suggestion for <paramref name="context"/>, or null when the model had nothing to add. Reading
    /// stops (and the request is aborted) once enough lines have arrived.
    /// </summary>
    /// <exception cref="AiProviderException">The provider failed, refused, or took longer than <see cref="Timeout"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled (the user typed on).</exception>
    public async Task<string?> CompleteAsync(IChatProvider provider, string model, InlineCompletionContext context, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        var reply = new StringBuilder();
        var reasoned = false;
        string? finish = null;
        try
        {
            await foreach (var item in provider.StreamAsync(InlineCompletionPrompt.Build(model, context), timeout.Token).ConfigureAwait(false))
            {
                if (item is TextDelta delta)
                {
                    reply.Append(delta.Text);
                    if (CountLines(reply) > InlineCompletionText.MaxLines + 8)
                    {
                        break; // more than will be shown (fences and echoes included)
                    }
                }
                else if (item is ReasoningDelta)
                {
                    reasoned = true;
                }
                else if (item is ChatCompleted completed)
                {
                    finish = completed.Reason;
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AiProviderException($"{provider.Config.Name} took longer than {Timeout.TotalSeconds:0} s to suggest a completion.");
        }

        // A thinking model can use the whole (short) answer on its reasoning and say nothing: that is not
        // "nothing to suggest", and the user needs to know to pick another model.
        var text = reply.ToString();
        if (text.Trim().Length == 0 && (reasoned || finish == "length" || text.Contains("<think>", StringComparison.Ordinal)))
        {
            throw new AiProviderException($"{model} spent its answer thinking and suggested nothing. Pick a model that answers directly (not a reasoning model) for inline suggestions.");
        }

        if (text.Contains("<think>", StringComparison.Ordinal) && !text.Contains("</think>", StringComparison.Ordinal))
        {
            throw new AiProviderException($"{model} was still thinking when its answer ran out. Pick a model that answers directly (not a reasoning model) for inline suggestions.");
        }

        return InlineCompletionText.Clean(text, context);
    }

    private static int CountLines(StringBuilder text)
    {
        var count = 1;
        foreach (var chunk in text.GetChunks())
        {
            count += chunk.Span.Count('\n');
        }

        return count;
    }
}

/// <summary>Recent suggestions by position, so coming back to the same text needs no new request.</summary>
public sealed class InlineCompletionCache(int capacity = 32)
{
    private readonly LinkedList<(InlineCompletionContext Context, string? Text)> _entries = new();

    public int Count => _entries.Count;

    public bool TryGet(InlineCompletionContext context, out string? text)
    {
        for (var node = _entries.First; node is not null; node = node.Next)
        {
            if (node.Value.Context == context)
            {
                _entries.Remove(node);
                _entries.AddFirst(node); // most recently used first
                text = node.Value.Text;
                return true;
            }
        }

        text = null;
        return false;
    }

    /// <summary>Remembers the answer for <paramref name="context"/> (null: the model had nothing to add).</summary>
    public void Add(InlineCompletionContext context, string? text)
    {
        for (var node = _entries.First; node is not null; node = node.Next)
        {
            if (node.Value.Context == context)
            {
                _entries.Remove(node);
                break;
            }
        }

        _entries.AddFirst((context, text));
        while (_entries.Count > capacity)
        {
            _entries.RemoveLast();
        }
    }

    public void Clear() => _entries.Clear();
}
