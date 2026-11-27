using Coder.Core.Editing;
using Coder.Presentation.Editors;
using Coder.Presentation.Mvvm;

namespace Coder.Presentation.LanguageFeatures;

/// <summary>
/// Sticky scroll ("editor.stickyScroll.enabled"): the headers of the blocks around the top of the editor
/// stay pinned there. The editor finds blocks from brackets or indentation by itself; this service adds the
/// language server's document symbols (namespaces, classes, methods) for the active document, asked for
/// when it is shown and a moment after typing stops.
/// </summary>
public sealed class StickyScrollService : ObservableObject, IDisposable
{
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(600);

    private readonly LanguageServices _languages;
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private readonly Timer _timer;
    private EditorTabViewModel? _tab;
    private bool _isEnabled = true;
    private int _generation;

    public StickyScrollService(LanguageServices languages, EditorGroupViewModel editors)
    {
        _languages = languages;
        _timer = new Timer(_ => Post(() => _ = RefreshAsync()));
        editors.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EditorGroupViewModel.ActiveTab))
            {
                Follow(editors.ActiveEditor);
            }
        };
        languages.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LanguageServices.StatusText))
            {
                Schedule(); // a server became ready
            }
        };
        Follow(editors.ActiveEditor);
    }

    /// <summary>Whether block headers stay pinned at the top of the editor ("Toggle Sticky Scroll").</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (SetProperty(ref _isEnabled, value))
            {
                Schedule();
            }
        }
    }

    /// <summary>Asks for the active document's symbols now (tests, and after toggling).</summary>
    public async Task RefreshAsync()
    {
        var generation = ++_generation;
        if (!IsEnabled || _tab is not { } tab || !_languages.Supports(tab, c => c.HasDocumentSymbols))
        {
            return;
        }

        var version = tab.Document.Version;
        var symbols = await _languages.DocumentSymbolsAsync(tab, CancellationToken.None);

        // Only for the text they were computed for: if the user typed meanwhile, the next request follows.
        if (symbols is not null && generation == _generation && tab.Document.Version == version)
        {
            tab.SetSymbolScopes(StickyScopes.FromSymbols(symbols, tab.Document.LineCount, tab.Document.GetLineText));
        }
    }

    public void Dispose() => _timer.Dispose();

    private void Follow(EditorTabViewModel? tab)
    {
        if (ReferenceEquals(tab, _tab))
        {
            return;
        }

        _tab?.Document.Changed -= OnDocumentChanged;
        _tab = tab;
        _tab?.Document.Changed += OnDocumentChanged;
        Schedule();
    }

    private void OnDocumentChanged(object? sender, Coder.Core.Text.TextChange e) => Schedule();

    private void Schedule() => _timer.Change(Delay, Timeout.InfiniteTimeSpan);

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
