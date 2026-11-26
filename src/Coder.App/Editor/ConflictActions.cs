using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Coder.App.Controls;
using Coder.App.Theme;
using Coder.Core.Git;
using Coder.Presentation.Editors;
using Coder.Presentation.Mvvm;
using Coder.Presentation.Workbench;

namespace Coder.App.Editor;

/// <summary>
/// The actions of each merge conflict, on its <c>&lt;&lt;&lt;&lt;&lt;&lt;&lt;</c> line after the marker text, like VS Code's:
/// Accept Current Change, Accept Incoming Change and Accept Both Changes. Each is one undo step.
/// Rows are reused while scrolling, so only the visible conflicts cost anything.
/// </summary>
public sealed class ConflictActions : Canvas
{
    private readonly CodeEditor _editor;
    private readonly EditorGroupViewModel _editors;
    private readonly List<Row> _rows = [];
    private EditorTabViewModel? _tab;
    private bool _refreshPosted;

    public ConflictActions(CodeEditor editor, WorkbenchViewModel workbench)
    {
        _editor = editor;
        _editors = workbench.Editors;
        ClipToBounds = true;

        editor.Scrolled += (_, _) => Refresh();
        editor.SizeChanged += (_, _) => Refresh();
        editor.PropertyChanged += (_, e) =>
        {
            if (e.Property == CodeEditor.EditorFontSizeProperty || e.Property == CodeEditor.TabProperty)
            {
                Follow(editor.Tab);
            }
        };
        Follow(editor.Tab);
    }

    /// <summary>For tests: the conflicts that currently show buttons.</summary>
    internal int VisibleCount => _rows.Count(row => row.IsVisible);

    private void Follow(EditorTabViewModel? tab)
    {
        if (!ReferenceEquals(_tab, tab))
        {
            _tab?.Document.Changed -= OnDocumentChanged;
            _tab = tab;
            tab?.Document.Changed += OnDocumentChanged;
        }

        Refresh();
    }

    private void OnDocumentChanged(object? sender, Coder.Core.Text.TextChange e) => Refresh();

    /// <summary>Repositions after the editor's next layout, at most once per frame.</summary>
    private void Refresh()
    {
        if (_refreshPosted)
        {
            return;
        }

        _refreshPosted = true;
        Dispatcher.UIThread.Post(() =>
        {
            _refreshPosted = false;
            Place();
        }, DispatcherPriority.Loaded);
    }

    private void Place()
    {
        var shown = 0;
        if (_tab is { } tab && tab.Conflicts is { Count: > 0 } conflicts && _editor.TextArea(this) is { } area)
        {
            var document = tab.Document;
            foreach (var conflict in conflicts)
            {
                var (top, height) = _editor.View.LineBand(conflict.Start);
                if (top + height < 0 || top > area.Height)
                {
                    continue; // the marker line is scrolled out of view
                }

                if (_editor.CharacterBounds(document.GetLineEnd(conflict.Start), this) is not { } end)
                {
                    continue;
                }

                if (shown == _rows.Count)
                {
                    var created = new Row(Resolve);
                    _rows.Add(created);
                    Children.Add(created);
                }

                var row = _rows[shown++];
                row.Conflict = conflict;
                row.Height = height;
                row.IsVisible = true;
                SetLeft(row, end.Left + 24);
                SetTop(row, end.Top);
            }
        }

        for (var i = shown; i < _rows.Count; i++)
        {
            _rows[i].IsVisible = false;
        }
    }

    private void Resolve(MergeConflict conflict, ConflictResolution resolution)
    {
        if (_editors.ActiveEditor is not { } tab || !tab.Conflicts.Contains(conflict))
        {
            return;
        }

        MergeConflicts.Resolve(tab.Editor, conflict, resolution);
        _editor.FocusText();
    }

    /// <summary>The three actions of one conflict, as quiet text links.</summary>
    private sealed class Row : StackPanel
    {
        public Row(Action<MergeConflict, ConflictResolution> resolve)
        {
            Orientation = Orientation.Horizontal;
            Spacing = 2;
            Children.Add(Link("Accept Current Change", () => resolve(Conflict, ConflictResolution.Current)));
            Children.Add(Divider());
            Children.Add(Link("Accept Incoming Change", () => resolve(Conflict, ConflictResolution.Incoming)));
            Children.Add(Divider());
            Children.Add(Link("Accept Both Changes", () => resolve(Conflict, ConflictResolution.Both)));
        }

        public MergeConflict Conflict { get; set; }

        private static FlatButton Link(string text, Action action)
        {
            var button = new FlatButton
            {
                Padding = new Thickness(4, 0),
                VerticalAlignment = VerticalAlignment.Stretch,
                Chamfer = 0,
                Content = new TextBlock { Text = text, FontSize = Typography.SmallSize, VerticalAlignment = VerticalAlignment.Center },
                Command = new RelayCommand(action),
            };
            AutomationProperties.SetName(button, text);
            return button;
        }

        private static TextBlock Divider() => new()
        {
            Text = "|",
            FontSize = Typography.SmallSize,
            Foreground = Palette.TextMuted,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }
}
