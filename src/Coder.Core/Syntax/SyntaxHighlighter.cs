using Coder.Core.Text;

namespace Coder.Core.Syntax;

/// <summary>
/// Incremental highlighting for one document. It remembers the lexer state at the end of every line
/// it has seen, so asking for a line only lexes from the last line still known to be valid. An edit
/// invalidates states from the edited line onwards; nothing is lexed until a line is actually shown.
/// </summary>
public sealed class SyntaxHighlighter
{
    private readonly TextDocument _document;
    private readonly LineLexer _lexer;

    // _endStates[n] is the state after line n. Only the first _endStates.Count lines are known.
    private readonly List<LexState> _endStates = [];
    private readonly List<Token> _scratch = [];

    public SyntaxHighlighter(TextDocument document, LanguageDefinition language)
    {
        _document = document;
        Language = language;
        _lexer = new LineLexer(language);
        _document.Changed += OnDocumentChanged;
    }

    /// <summary>Raised when lines from the given index onward may have new colors.</summary>
    public event EventHandler<int>? Invalidated;

    public LanguageDefinition Language { get; }

    /// <summary>Fills <paramref name="tokens"/> with the tokens of <paramref name="line"/>.</summary>
    public void GetTokens(int line, List<Token> tokens)
    {
        tokens.Clear();
        var state = StateBefore(line);
        var endState = _lexer.Tokenize(_document.GetLineText(line), state, tokens);
        if (line == _endStates.Count)
        {
            _endStates.Add(endState);
        }
    }

    public void Detach() => _document.Changed -= OnDocumentChanged;

    private LexState StateBefore(int line)
    {
        if (line == 0)
        {
            return LexState.Normal;
        }

        // Catch up on the lines between the last known state and the requested one.
        for (var known = _endStates.Count; known < line; known++)
        {
            _scratch.Clear();
            var start = known == 0 ? LexState.Normal : _endStates[known - 1];
            _endStates.Add(_lexer.Tokenize(_document.GetLineText(known), start, _scratch));
        }

        return _endStates[line - 1];
    }

    private void OnDocumentChanged(object? sender, TextChange change)
    {
        if (change.StartLine < _endStates.Count)
        {
            _endStates.RemoveRange(change.StartLine, _endStates.Count - change.StartLine);
        }

        Invalidated?.Invoke(this, change.StartLine);
    }
}
