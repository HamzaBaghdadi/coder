using Coder.Core.Ai;
using Coder.Core.Text;
using Coder.Presentation.Editors;
using Coder.Presentation.LanguageFeatures;
using Coder.Presentation.Mvvm;

namespace Coder.Presentation.Agent;

/// <summary>Gray text after the caret that Tab would insert: where it starts and what it says.</summary>
public sealed record InlineSuggestion(int Offset, string Text);

/// <summary>The model suggestions come from, resolved when a request is made.</summary>
/// <param name="Label">How the status bar names it ("qwen3 · Ollama").</param>
public sealed record InlineSuggestionModel(IChatProvider Provider, string Model, string Label);

/// <summary>Where inline suggestions stand, for the status bar.</summary>
public enum InlineSuggestionStatus
{
    /// <summary>Turned off by the user.</summary>
    Off,

    /// <summary>On, but no provider is ready (no key) or no model is chosen.</summary>
    NoModel,

    /// <summary>On and waiting for typing.</summary>
    Ready,

    /// <summary>A request is out.</summary>
    Loading,

    /// <summary>The last request failed (<see cref="InlineSuggestionsViewModel.Error"/> says why).</summary>
    Failed,
}

/// <summary>
/// AI inline suggestions ("ghost text") for the active editor, like GitHub Copilot: a moment after
/// typing stops, the text around the caret goes to a chat model as a fill-in-the-middle prompt and the
/// answer shows after the caret. Typing on cancels the request; typing what the suggestion says keeps
/// the rest of it; moving the caret, Esc or typing something else drops it. Answers are cached by
/// position, so coming back to the same text asks nothing new.
/// </summary>
public sealed class InlineSuggestionsViewModel : ObservableObject, IDisposable
{
    public static readonly TimeSpan DefaultDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>Text after the caret that still allows a suggestion: closing brackets, quotes and separators.</summary>
    private const string ClosingCharacters = ")]}>\"'`;,: \t";

    /// <summary>Back-off after repeated failures, so a broken provider isn't asked on every keystroke.</summary>
    private static readonly TimeSpan MaxBackOff = TimeSpan.FromSeconds(30);

    private readonly EditorGroupViewModel _editors;
    private readonly CompletionViewModel _completion;
    private readonly Func<InlineSuggestionModel?> _resolve;
    private readonly Func<string?> _root;
    private readonly InlineCompletionEngine _engine;
    private readonly InlineCompletionCache _cache = new();
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private Func<TimeSpan, Action, IDisposable> _schedule;
    private IDisposable? _pending;
    private EditorTabViewModel? _tab;
    private InlineSuggestion? _suggestion;
    private CancellationTokenSource? _request;
    private bool _isEnabled = true;
    private bool _applying;
    private bool _editMovesCaret;
    private bool _caretMovedSinceEdit;
    private int _generation;
    private int _failures;
    private DateTime _retryAt;
    private InlineSuggestionStatus _status;
    private string? _error;
    private bool _wasListOpen;

    public InlineSuggestionsViewModel(EditorGroupViewModel editors, CompletionViewModel completion, Func<InlineSuggestionModel?> resolve, Func<string?> root, InlineCompletionEngine? engine = null)
    {
        _editors = editors;
        _completion = completion;
        _resolve = resolve;
        _root = root;
        _engine = engine ?? new InlineCompletionEngine();
        _schedule = (delay, action) => new Timer(_ => Post(action), null, delay, Timeout.InfiniteTimeSpan);
        editors.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EditorGroupViewModel.ActiveTab))
            {
                Follow(editors.ActiveEditor);
            }
        };
        completion.Changed += (_, _) =>
        {
            // The list hides the suggestion while it is open (Tab is the list's); it comes back when the list closes.
            if (completion.IsOpen != _wasListOpen)
            {
                _wasListOpen = completion.IsOpen;
                Changed?.Invoke(this, EventArgs.Empty);
            }
        };
        Follow(editors.ActiveEditor);
        RefreshStatus();
    }

    /// <summary>The suggestion appeared, changed or went away (repaint).</summary>
    public event EventHandler? Changed;

    /// <summary>"Inline Suggestions" in settings, toggled from the status bar.</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (SetProperty(ref _isEnabled, value))
            {
                if (!value)
                {
                    Clear();
                }

                _failures = 0;
                RefreshStatus();
            }
        }
    }

    /// <summary>How long typing must pause before a suggestion is asked for.</summary>
    public TimeSpan Delay { get; set; } = DefaultDelay;

    public InlineSuggestionStatus Status
    {
        get => _status;
        private set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusTip));
                OnPropertyChanged(nameof(IsAvailable));
            }
        }
    }

    /// <summary>Why the last request failed; null after a success.</summary>
    public string? Error
    {
        get => _error;
        private set
        {
            if (SetProperty(ref _error, value))
            {
                OnPropertyChanged(nameof(StatusTip));
            }
        }
    }

    /// <summary>Whether the status bar shows the indicator: suggestions are set up (or were turned off).</summary>
    public bool IsAvailable => Status != InlineSuggestionStatus.NoModel || !IsEnabled;

    /// <summary>The status bar indicator's tooltip.</summary>
    public string StatusTip => Status switch
    {
        InlineSuggestionStatus.Off => "Inline suggestions are off. Click to turn them on.",
        InlineSuggestionStatus.NoModel => "Inline suggestions need a model: add an API key in Coder Agent's providers.",
        InlineSuggestionStatus.Loading => $"Getting a suggestion from {ModelLabel}… Click to turn inline suggestions off.",
        InlineSuggestionStatus.Failed => $"Inline suggestion failed: {Error} Click to turn inline suggestions off.",
        _ => $"Inline suggestions from {ModelLabel}. Tab accepts, Ctrl+Right accepts a word, Esc dismisses. Click to turn them off.",
    };

    /// <summary>The model suggestions come from now, for the status bar and settings ("none" when none).</summary>
    public string ModelLabel => _resolve()?.Label ?? "no model";

    /// <summary>The suggestion to draw in <paramref name="tab"/>, if any (none while the completion list is open).</summary>
    public InlineSuggestion? SuggestionFor(EditorTabViewModel? tab) =>
        tab is not null && ReferenceEquals(tab, _tab) && !_completion.IsOpen ? _suggestion : null;

    /// <summary>The suggestion shown in the active editor now.</summary>
    public InlineSuggestion? Current => SuggestionFor(_tab);

    /// <summary>Whether <see cref="Current"/> is shown (Tab, Ctrl+Right and Esc act on it).</summary>
    public bool IsShowing => Current is not null;

    /// <summary>Requests made (cache hits excluded), for tests.</summary>
    public int RequestCount { get; private set; }

    public void Toggle() => IsEnabled = !IsEnabled;

    /// <summary>Asks now, without waiting for typing to pause ("Trigger Inline Suggestion").</summary>
    public Task TriggerAsync()
    {
        CancelPending();
        _caretMovedSinceEdit = false;
        return RequestAsync(automatic: false);
    }

    /// <summary>Tab: inserts the whole suggestion. False when none is shown.</summary>
    public bool Accept()
    {
        if (Current is not { } suggestion || _tab is not { } tab)
        {
            return false;
        }

        Insert(tab, suggestion.Text);
        Clear();
        Schedule(); // what comes next
        return true;
    }

    /// <summary>Ctrl+Right: inserts the suggestion's next word and keeps the rest. False when none is shown.</summary>
    public bool AcceptNextWord()
    {
        if (Current is not { } suggestion || _tab is not { } tab)
        {
            return false;
        }

        var length = InlineCompletionText.NextWordLength(suggestion.Text);
        Insert(tab, suggestion.Text[..length]);
        var rest = suggestion.Text[length..];
        SetSuggestion(rest.Length > 0 ? new InlineSuggestion(tab.Editor.Selection.Active, rest) : null);
        return true;
    }

    /// <summary>Esc: drops the suggestion. False when none is shown.</summary>
    public bool Dismiss()
    {
        if (!IsShowing)
        {
            return false;
        }

        Clear();
        return true;
    }

    /// <summary>The provider or model may have changed (settings, keys): show where things stand.</summary>
    public void RefreshStatus()
    {
        OnPropertyChanged(nameof(ModelLabel));
        if (Status != InlineSuggestionStatus.Loading)
        {
            Status = SettledStatus();
        }

        OnPropertyChanged(nameof(StatusTip));
    }

    private InlineSuggestionStatus SettledStatus() =>
        !IsEnabled ? InlineSuggestionStatus.Off
        : _resolve() is null ? InlineSuggestionStatus.NoModel
        : Error is not null ? InlineSuggestionStatus.Failed
        : InlineSuggestionStatus.Ready;

    public void Dispose()
    {
        CancelPending();
        _request?.Cancel();
        _request?.Dispose();
    }

    private void Follow(EditorTabViewModel? tab)
    {
        if (ReferenceEquals(tab, _tab))
        {
            return;
        }

        if (_tab is { } old)
        {
            old.Document.Changed -= OnDocumentChanged;
            old.Editor.SelectionChanged -= OnSelectionChanged;
        }

        Clear();
        _tab = tab;
        if (tab is not null)
        {
            tab.Document.Changed += OnDocumentChanged;
            tab.Editor.SelectionChanged += OnSelectionChanged;
        }
    }

    private void OnDocumentChanged(object? sender, TextChange change)
    {
        if (_applying)
        {
            return;
        }

        _editMovesCaret = true;
        _caretMovedSinceEdit = false;

        // Typed what the suggestion says: keep the rest of it, no new request.
        if (_suggestion is { } suggestion && change.RemovedText.Length == 0 && change.Offset == suggestion.Offset
            && change.InsertedText.Length > 0 && change.InsertedText.Length < suggestion.Text.Length
            && suggestion.Text.StartsWith(change.InsertedText, StringComparison.Ordinal))
        {
            SetSuggestion(new InlineSuggestion(suggestion.Offset + change.InsertedText.Length, suggestion.Text[change.InsertedText.Length..]));
            return;
        }

        Clear();
        Schedule();
    }

    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        if (_applying || _tab is not { } tab)
        {
            return;
        }

        if (_editMovesCaret)
        {
            _editMovesCaret = false; // the caret moving with the text just typed
            return;
        }

        // The caret moved by itself (a click, an arrow key): what was asked for no longer applies.
        _caretMovedSinceEdit = true;
        if (_suggestion is { } suggestion && (tab.Editor.Selection.Active != suggestion.Offset || !tab.Editor.Selection.IsEmpty))
        {
            Clear();
        }
        else if (_suggestion is null)
        {
            CancelRequest();
        }
    }

    private void Schedule()
    {
        if (IsEnabled)
        {
            CancelPending();
            _pending = _schedule(Delay, () =>
            {
                _pending = null;
                _ = RequestAsync(automatic: true);
            });
        }
    }

    private async Task RequestAsync(bool automatic)
    {
        if (!IsEnabled || _tab is not { } tab || (automatic && _caretMovedSinceEdit))
        {
            return;
        }

        var editor = tab.Editor;
        var document = tab.Document;
        if (editor.Selections.Count != 1 || !editor.Selection.IsEmpty || editor.IsInSnippet)
        {
            return;
        }

        var caret = editor.Selection.Active;
        var line = document.GetLineOfOffset(caret);
        var restOfLine = document.GetText(caret, document.GetLineEnd(line) - caret);
        if (automatic && restOfLine.Any(c => !ClosingCharacters.Contains(c)))
        {
            return; // in the middle of code: a suggestion would only get in the way
        }

        if (_resolve() is not { } model)
        {
            RefreshStatus();
            return;
        }

        var context = ContextAt(tab, caret);
        if (_cache.TryGet(context, out var cached))
        {
            CancelRequest();
            SetSuggestion(cached is null ? null : new InlineSuggestion(caret, cached));
            return;
        }

        if (automatic && DateTime.UtcNow < _retryAt)
        {
            return; // backing off after failures
        }

        CancelRequest();
        var request = _request = new CancellationTokenSource();
        var generation = ++_generation;
        var version = document.Version;
        Status = InlineSuggestionStatus.Loading;
        RequestCount++;
        try
        {
            var text = await _engine.CompleteAsync(model.Provider, model.Model, context, request.Token);
            _cache.Add(context, text);
            _failures = 0;
            Error = null;
            if (generation == _generation && ReferenceEquals(tab, _tab) && document.Version == version && editor.Selection.Active == caret && editor.Selection.IsEmpty)
            {
                SetSuggestion(text is null ? null : new InlineSuggestion(caret, text));
            }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
            // The user typed on; the next request (if any) takes over.
        }
        catch (Exception exception) when (exception is AiProviderException or HttpRequestException or IOException or InvalidOperationException)
        {
            if (generation == _generation)
            {
                _failures++;
                _retryAt = DateTime.UtcNow + TimeSpan.FromSeconds(Math.Min(MaxBackOff.TotalSeconds, Math.Pow(2, _failures - 1) - 1));
                Error = exception is AiProviderException ? exception.Message : $"{model.Label}: {exception.Message}";
            }
        }
        finally
        {
            if (generation == _generation)
            {
                Status = SettledStatus();
            }

            if (ReferenceEquals(_request, request))
            {
                _request = null;
            }

            request.Dispose();
        }
    }

    private InlineCompletionContext ContextAt(EditorTabViewModel tab, int caret)
    {
        // A window a little larger than what is sent, so the prompt can cut it at whole lines.
        var document = tab.Document;
        var start = Math.Max(0, caret - InlineCompletionPrompt.MaxPrefixChars - 400);
        var end = Math.Min(document.Length, caret + InlineCompletionPrompt.MaxSuffixChars + 400);
        var path = tab.FilePath is { } file
            ? _root() is { } root && file.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? Path.GetRelativePath(root, file).Replace('\\', '/') : Path.GetFileName(file)
            : tab.Title;
        return InlineCompletionPrompt.Context(path, tab.Language.Id, document.GetText(start, end - start), caret - start);
    }

    private void Insert(EditorTabViewModel tab, string text)
    {
        _applying = true;
        try
        {
            tab.Editor.Paste(text);
        }
        finally
        {
            _applying = false;
        }
    }

    private void SetSuggestion(InlineSuggestion? suggestion)
    {
        if (_suggestion == suggestion)
        {
            return;
        }

        _suggestion = suggestion;
        OnPropertyChanged(nameof(Current));
        OnPropertyChanged(nameof(IsShowing));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Clear()
    {
        CancelPending();
        CancelRequest();
        SetSuggestion(null);
    }

    /// <summary>
    /// Runs the debounce on another timer: the app passes one on its UI dispatcher, so nothing is posted
    /// to the UI thread from a background thread.
    /// </summary>
    /// <param name="schedule">Calls the action once after the delay, unless the returned handle is disposed first.</param>
    public void UseScheduler(Func<TimeSpan, Action, IDisposable> schedule)
    {
        CancelPending();
        _schedule = schedule;
    }

    private void CancelPending()
    {
        _pending?.Dispose();
        _pending = null;
    }

    private void CancelRequest()
    {
        if (_request is { } request)
        {
            _generation++;
            _request = null;
            request.Cancel();
            if (Status == InlineSuggestionStatus.Loading)
            {
                Status = SettledStatus();
            }
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
