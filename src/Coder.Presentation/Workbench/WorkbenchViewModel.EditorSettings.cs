using System.Collections.Specialized;
using Coder.Core.Editing;
using Coder.Presentation.Editors;

namespace Coder.Presentation.Workbench;

/// <summary>
/// The editor's text settings: indentation defaults and detection, save clean-ups, format on save, auto save,
/// the caret's look, and the terminal's font. Values live in <see cref="Settings"/>; the properties here are the
/// ones views bind to.
/// </summary>
public sealed partial class WorkbenchViewModel
{
    private readonly Dictionary<EditorTabViewModel, CancellationTokenSource> _autoSaveTimers = [];

    /// <summary>The editor caret: "line", "block" or "underline".</summary>
    public string CursorStyle
    {
        get => Settings.CursorStyle;
        set => SetSetting(Settings.CursorStyle, value, v => Settings.CursorStyle = v);
    }

    public bool CursorBlinking
    {
        get => Settings.CursorBlinking;
        set => SetSetting(Settings.CursorBlinking, value, v => Settings.CursorBlinking = v);
    }

    public double TerminalFontSize
    {
        get => Settings.TerminalFontSize;
        set => SetSetting(Settings.TerminalFontSize, Math.Clamp(value, 8, 40), v => Settings.TerminalFontSize = v);
    }

    public string TerminalFontFamily
    {
        get => Settings.TerminalFontFamily;
        set => SetSetting(Settings.TerminalFontFamily, value, v => Settings.TerminalFontFamily = v);
    }

    /// <summary>The window lost focus: with "onFocusChange" auto save, unsaved files are written.</summary>
    public Task OnWindowDeactivatedAsync() => Settings.AutoSave == "onFocusChange" ? SaveAllAutomaticallyAsync() : Task.CompletedTask;

    private void WireEditorSettings()
    {
        Editors.DefaultOptions = () => new EditorOptions { TabSize = Math.Clamp(Settings.TabSize, 1, 8), InsertSpaces = Settings.InsertSpaces };
        Editors.DetectIndentation = () => Settings.DetectIndentation;
        Editors.BeforeSave = BeforeSaveAsync;

        // Auto save: "afterDelay" restarts a timer on every change; "onFocusChange" saves the tab being left.
        Editors.Tabs.CollectionChanged += (_, e) =>
        {
            foreach (var tab in e.NewItems?.OfType<EditorTabViewModel>() ?? [])
            {
                tab.Document.Changed += (_, _) => ScheduleAutoSave(tab);
            }

            if (e.Action is NotifyCollectionChangedAction.Remove or NotifyCollectionChangedAction.Reset)
            {
                foreach (var tab in _autoSaveTimers.Keys.Where(t => !Editors.Tabs.Contains(t)).ToList())
                {
                    _autoSaveTimers.Remove(tab, out var timer);
                    timer?.Cancel();
                }
            }
        };

        EditorTabViewModel? previous = null;
        Editors.PropertyChanged += async (_, e) =>
        {
            if (e.PropertyName != nameof(EditorGroupViewModel.ActiveEditor))
            {
                return;
            }

            var left = previous;
            previous = Editors.ActiveEditor;
            if (Settings.AutoSave == "onFocusChange" && left is { IsDirty: true, FilePath: not null } && Editors.Tabs.Contains(left))
            {
                await SaveAutomaticallyAsync(left);
            }
        };
    }

    /// <summary>Save clean-ups (trim, final newline) as one undo step, then format when saving by hand.</summary>
    private async Task BeforeSaveAsync(EditorTabViewModel tab, bool automatic)
    {
        if (!automatic && Settings.FormatOnSave && ReferenceEquals(tab, Editors.ActiveEditor))
        {
            await LanguageCommands.FormatDocumentAsync();
        }

        var edits = new List<(int Start, int End, string Text)>();
        if (Settings.TrimTrailingWhitespace)
        {
            edits.AddRange(Indentation.TrimTrailingWhitespace(tab.Document));
        }

        if (Settings.InsertFinalNewline)
        {
            edits.AddRange(Indentation.InsertFinalNewline(tab.Document));
        }

        tab.Editor.ApplyEdits(edits);
    }

    private void ScheduleAutoSave(EditorTabViewModel tab)
    {
        if (Settings.AutoSave != "afterDelay" || tab.FilePath is null)
        {
            return;
        }

        if (_autoSaveTimers.Remove(tab, out var old))
        {
            old.Cancel();
        }

        var timer = new CancellationTokenSource();
        _autoSaveTimers[tab] = timer;
        _ = SaveAfterDelayAsync(tab, timer.Token);
    }

    private async Task SaveAfterDelayAsync(EditorTabViewModel tab, CancellationToken cancellation)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp(Settings.AutoSaveDelay, 100, 60_000)), cancellation);
        }
        catch (OperationCanceledException)
        {
            return; // typed again: a newer timer took over
        }

        if (_autoSaveTimers.TryGetValue(tab, out var current) && current.Token == cancellation)
        {
            _autoSaveTimers.Remove(tab);
        }

        if (tab.IsDirty && Editors.Tabs.Contains(tab))
        {
            await SaveAutomaticallyAsync(tab);
        }
    }

    private async Task SaveAllAutomaticallyAsync()
    {
        foreach (var tab in Editors.Tabs.OfType<EditorTabViewModel>().Where(t => t is { IsDirty: true, FilePath: not null }).ToList())
        {
            await SaveAutomaticallyAsync(tab);
        }
    }

    /// <summary>Auto save writes only files that exist on disk (never asks where), and a failure is just a message.</summary>
    private async Task SaveAutomaticallyAsync(EditorTabViewModel tab)
    {
        try
        {
            await Editors.SaveAsync(tab, automatic: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusBar.Message = $"Auto save: {exception.Message}";
        }
    }

    private void SetSetting<T>(T current, T value, Action<T> store, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (!EqualityComparer<T>.Default.Equals(current, value))
        {
            store(value);
            OnPropertyChanged(name);
        }
    }
}
