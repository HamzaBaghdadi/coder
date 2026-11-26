using Coder.Presentation.Editors;
using Coder.Presentation.Mvvm;

namespace Coder.Presentation.LanguageFeatures;

/// <summary>
/// Inlay hints for the active document: parameter names and inferred types the editor shows faintly
/// inside the code ("Add(count: 2)", "x := 3 int"). Asked of the language server when a document is shown,
/// a moment after typing stops, and when the server says its hints changed.
/// </summary>
public sealed class InlayHintsService : ObservableObject, IDisposable
{
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(350);

    /// <summary>Hints are asked for this many lines at most (the whole of nearly every file).</summary>
    private const int MaxLines = 20_000;

    private readonly LanguageServices _languages;
    private readonly EditorGroupViewModel _editors;
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private readonly Timer _timer;
    private EditorTabViewModel? _tab;
    private bool _isEnabled = true;
    private int _generation;

    public InlayHintsService(LanguageServices languages, EditorGroupViewModel editors)
    {
        _languages = languages;
        _editors = editors;
        _timer = new Timer(_ => Post(() => _ = RefreshAsync()));
        editors.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EditorGroupViewModel.ActiveTab))
            {
                Follow(editors.ActiveEditor);
            }
        };
        languages.InlayHintsChanged += (_, _) => Schedule();
        languages.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LanguageServices.StatusText))
            {
                Schedule(); // a server became ready
            }
        };
        Follow(editors.ActiveEditor);
    }

    /// <summary>Whether hints are shown ("Toggle Inlay Hints").</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (SetProperty(ref _isEnabled, value))
            {
                if (!value)
                {
                    foreach (var tab in _editors.Tabs.OfType<EditorTabViewModel>())
                    {
                        tab.SetInlayHints([]);
                    }
                }

                Schedule();
            }
        }
    }

    /// <summary>Asks for the active document's hints now (tests, and after toggling).</summary>
    public async Task RefreshAsync()
    {
        var generation = ++_generation;
        if (!IsEnabled || _tab is not { } tab || !_languages.Supports(tab, c => c.HasInlayHints))
        {
            return;
        }

        var version = tab.Document.Version;
        var hints = await _languages.InlayHintsAsync(tab, 0, Math.Min(tab.Document.LineCount, MaxLines) - 1, CancellationToken.None);

        // Only for the text they were computed for: if the user typed meanwhile, the next request follows.
        if (hints is not null && generation == _generation && tab.Document.Version == version && IsEnabled)
        {
            tab.SetInlayHints(hints);
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
