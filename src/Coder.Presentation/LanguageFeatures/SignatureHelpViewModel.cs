using Coder.Core.Lsp;
using Coder.Presentation.Editors;
using Coder.Presentation.Mvvm;

namespace Coder.Presentation.LanguageFeatures;

/// <summary>
/// Parameter hints while typing a call: opened by the server's trigger characters ("(" and "," in
/// most languages) or Ctrl+Shift+Space, refreshed as the caret moves within the line (the active
/// parameter changes), and closed when the server says the caret left the call, when the caret
/// leaves the line, or on Escape.
/// </summary>
public sealed class SignatureHelpViewModel : ObservableObject
{
    private readonly LanguageServices _languages;
    private readonly EditorGroupViewModel _editors;
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private EditorTabViewModel? _tab;
    private SignatureHelp? _help;
    private int _line;
    private int _generation;
    private bool _refreshPosted;

    public SignatureHelpViewModel(LanguageServices languages, EditorGroupViewModel editors)
    {
        _languages = languages;
        _editors = editors;
        editors.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EditorGroupViewModel.ActiveEditor))
            {
                Close();
            }
        };
    }

    /// <summary>Raised when the help appears, changes or closes.</summary>
    public event EventHandler? Changed;

    public bool IsOpen => _help is not null;

    public SignatureHelp? Help => _help;

    /// <summary>The caret when the help was last updated; the card sits above it.</summary>
    public int AnchorOffset { get; private set; }

    /// <summary>The signature to show and which parameter (by index into its ranges) is active.</summary>
    public (SignatureInfo Signature, int Parameter, int Index, int Count)? Current
    {
        get
        {
            if (_help is not { Signatures.Count: > 0 } help)
            {
                return null;
            }

            var index = Math.Clamp(help.ActiveSignature, 0, help.Signatures.Count - 1);
            var signature = help.Signatures[index];
            return (signature, signature.ActiveParameter ?? help.ActiveParameter, index, help.Signatures.Count);
        }
    }

    /// <summary>Called after typed text went into the active editor.</summary>
    public void OnTextTyped(string text)
    {
        if (_editors.ActiveEditor is not { } tab || text.Length == 0)
        {
            return;
        }

        var trigger = _languages.SignatureTriggerCharacters(tab).FirstOrDefault(t => text.EndsWith(t, StringComparison.Ordinal));
        if (trigger is not null || (IsOpen && text.Contains(')', StringComparison.Ordinal)))
        {
            _ = RequestAsync(tab, trigger);
        }
    }

    /// <summary>Ctrl+Shift+Space.</summary>
    public void Trigger()
    {
        if (_editors.ActiveEditor is { } tab)
        {
            _ = RequestAsync(tab, triggerCharacter: null);
        }
    }

    public void Close()
    {
        _generation++;
        if (_tab is not null)
        {
            _tab.Editor.SelectionChanged -= OnSelectionChanged;
            _tab = null;
        }

        if (_help is not null)
        {
            _help = null;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task RequestAsync(EditorTabViewModel tab, string? triggerCharacter)
    {
        var generation = ++_generation;
        var caret = tab.Editor.Selection.Active;
        var help = await _languages.SignatureHelpAsync(tab, caret, triggerCharacter, CancellationToken.None);
        if (generation != _generation || !ReferenceEquals(tab, _editors.ActiveEditor))
        {
            return; // a newer request (or a close) came in meanwhile
        }

        if (help is null)
        {
            Close();
            return;
        }

        if (!ReferenceEquals(_tab, tab))
        {
            _tab?.Editor.SelectionChanged -= OnSelectionChanged;
            _tab = tab;
            tab.Editor.SelectionChanged += OnSelectionChanged;
        }

        _help = help;
        _line = tab.Document.GetLineOfOffset(caret);
        AnchorOffset = caret;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Moving within the line can change the active parameter; leaving the line closes the help.</summary>
    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        if (_tab is not { } tab)
        {
            return;
        }

        if (tab.Document.GetLineOfOffset(tab.Editor.Selection.Active) != _line)
        {
            Close();
            return;
        }

        // Typing changes the selection once per keystroke: ask again once, after the edit settles.
        if (!_refreshPosted)
        {
            _refreshPosted = true;
            Post(() =>
            {
                _refreshPosted = false;
                if (_tab is { } current)
                {
                    _ = RequestAsync(current, triggerCharacter: null);
                }
            });
        }
    }

    private void Post(Action action)
    {
        if (_context is null)
        {
            action();
        }
        else
        {
            _context.Post(_ => action(), null);
        }
    }
}
