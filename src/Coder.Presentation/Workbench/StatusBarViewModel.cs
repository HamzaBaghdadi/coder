using Coder.Core.Text;
using Coder.Presentation.Editors;
using Coder.Presentation.Mvvm;

namespace Coder.Presentation.Workbench;

/// <summary>Follows the active editor and exposes the status bar texts.</summary>
public sealed class StatusBarViewModel : ObservableObject
{
    private EditorTabViewModel? _tab;
    private string _message = string.Empty;

    public StatusBarViewModel(EditorGroupViewModel editors)
    {
        editors.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EditorGroupViewModel.ActiveEditor))
            {
                Track(editors.ActiveEditor);
            }
        };
    }

    public bool HasEditor => _tab is not null;

    public string CursorText
    {
        get
        {
            if (_tab is null)
            {
                return string.Empty;
            }

            var editor = _tab.Editor;
            var cursors = editor.Selections.Count;
            if (cursors > 1)
            {
                var selected = editor.Selections.All.Sum(s => s.Length);
                return selected > 0 ? $"{cursors} selections ({selected} characters selected)" : $"{cursors} cursors";
            }

            var caret = editor.CaretPosition;
            var length = editor.Selection.Length;
            return length > 0 ? $"Ln {caret.Line + 1}, Col {caret.Column + 1} ({length} selected)" : $"Ln {caret.Line + 1}, Col {caret.Column + 1}";
        }
    }

    public string IndentText => _tab is null ? string.Empty
        : _tab.Editor.Options.InsertSpaces ? $"Spaces: {_tab.Editor.Options.TabSize}" : $"Tab Size: {_tab.Editor.Options.TabSize}";

    public string EncodingText => _tab?.EncodingName ?? string.Empty;

    public string LineEndingText => _tab?.Document.LineEnding.DisplayName() ?? string.Empty;

    public string LanguageText => _tab?.Language.DisplayName ?? string.Empty;

    /// <summary>Transient feedback such as "Saved" or an error.</summary>
    public string Message
    {
        get => _message;
        set => SetProperty(ref _message, value);
    }

    private void Track(EditorTabViewModel? tab)
    {
        if (_tab is not null)
        {
            _tab.Editor.SelectionChanged -= OnSelectionChanged;
        }

        _tab = tab;
        if (_tab is not null)
        {
            _tab.Editor.SelectionChanged += OnSelectionChanged;
        }

        OnPropertyChanged(string.Empty); // Every text depends on the tab.
    }

    private void OnSelectionChanged(object? sender, EventArgs e) => OnPropertyChanged(nameof(CursorText));
}
