using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Coder.App.Controls;
using Coder.App.Mvvm;
using Coder.App.Theme;
using Coder.Core.Diff;
using Coder.Presentation.Editors;
using Coder.Presentation.Mvvm;
using Coder.Presentation.SourceControl;
using Coder.Presentation.Workbench;

namespace Coder.App.Editor;

/// <summary>
/// Opens when a change marker in the gutter is clicked (like VS Code's dirty-diff peek): a card under
/// the change showing what the lines were before (the staged version), with Stage Change, Revert
/// Change and previous / next change. Esc, scrolling or editing closes it.
/// </summary>
public sealed class ChangePeek : Canvas
{
    private const int MaxPreviewLines = 12;

    private readonly CodeEditor _editor;
    private readonly WorkbenchViewModel _workbench;
    private readonly Border _card;
    private readonly TextBlock _title = new() { FontSize = Typography.SmallSize, Foreground = Palette.TextSecondary, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _before = new();
    private EditorTabViewModel? _tab;
    private DiffHunk _hunk;

    public ChangePeek(CodeEditor editor, WorkbenchViewModel workbench)
    {
        _editor = editor;
        _workbench = workbench;
        ClipToBounds = true;

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            Children =
            {
                Action(Icons.ArrowUp, "Previous Change", () => Step(-1)),
                Action(Icons.ArrowDown, "Next Change", () => Step(1)),
                TextAction(Icons.Plus, "Stage Change", async () =>
                {
                    var (tab, hunk) = (_tab!, _hunk);
                    Close();
                    await workbench.SourceControl.StageChangeAsync(tab, hunk);
                }),
                TextAction(Icons.Discard, "Revert Change", () =>
                {
                    var (tab, hunk) = (_tab!, _hunk);
                    Close();
                    SourceControlViewModel.RevertChange(tab, hunk);
                    return Task.CompletedTask;
                }),
                Action(Icons.Close, "Close (Esc)", Close),
            },
        };

        _card = new Border
        {
            Background = Palette.Elevated,
            BorderBrush = Palette.ChangeModified,
            BorderThickness = new Thickness(0, 1, 0, 1),
            IsVisible = false,
            Child = new DockPanel
            {
                Children =
                {
                    new DockPanel
                    {
                        Margin = new Thickness(Spacing.M, 4, Spacing.S, 4),
                        Children = { actions.Dock(Dock.Right), _title },
                    }.Dock(Dock.Top),
                    new ScrollViewer { MaxHeight = MaxPreviewLines * 19 + 8, Content = _before, Padding = new Thickness(0, 0, 0, 4) },
                },
            },
        };
        AutomationProperties.SetName(_card, "Change");
        Children.Add(_card);

        editor.ChangeMarkerClicked += (_, hunk) => Show(hunk);
        editor.Scrolled += (_, _) => Place();
        workbench.Editors.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EditorGroupViewModel.ActiveTab))
            {
                Close();
            }
        };
        editor.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape && _card.IsVisible)
            {
                Close();
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
    }

    public bool IsOpen => _card.IsVisible;

    private void Show(DiffHunk hunk)
    {
        if (_workbench.Editors.ActiveEditor is not { } tab)
        {
            return;
        }

        if (!ReferenceEquals(_tab, tab))
        {
            _tab?.Document.Changed -= OnDocumentChanged;
            _tab = tab;
            tab.Document.Changed += OnDocumentChanged;
        }

        _hunk = hunk;
        var changes = tab.LineChanges;
        var index = changes.ToList().IndexOf(hunk);
        _title.Text = $"{(index + 1).ToString(CultureInfo.InvariantCulture)} of {changes.Count.ToString(CultureInfo.InvariantCulture)} changes · {Describe(hunk)}";
        _card.BorderBrush = hunk.Kind switch
        {
            DiffHunkKind.Added => Palette.ChangeAdded,
            DiffHunkKind.Deleted => Palette.ChangeDeleted,
            _ => Palette.ChangeModified,
        };

        _before.Children.Clear();
        var lines = tab.ChangeBase.Skip(hunk.OldStart).Take(hunk.OldCount).ToList();
        if (lines.Count == 0)
        {
            _before.Children.Add(new TextBlock { Text = "New lines: there was nothing here before.", Foreground = Palette.TextMuted, Margin = new Thickness(Spacing.M, 2) });
        }

        foreach (var line in lines)
        {
            _before.Children.Add(new Border
            {
                Background = Palette.DiffRemovedLine,
                Padding = new Thickness(Spacing.M, 1),
                Child = new TextBlock { Text = line.Length == 0 ? " " : line.Replace("\t", "    ", StringComparison.Ordinal), FontFamily = Typography.Code, FontSize = 13 },
            });
        }

        _card.IsVisible = true;
        Place();
    }

    /// <summary>Right under the change's last line, across the text area.</summary>
    private void Place()
    {
        if (!_card.IsVisible || _tab is null)
        {
            return;
        }

        var document = _tab.Document;
        var lastLine = Math.Clamp(_hunk.NewStart + Math.Max(_hunk.NewCount, 1) - 1, 0, document.LineCount - 1);
        if (_editor.CharacterBounds(document.GetLineStart(lastLine), this) is not { } line || _editor.TextArea(this) is not { } area)
        {
            return;
        }

        _card.Width = Math.Max(200, area.Right - line.Left + 4);
        SetLeft(_card, Math.Max(0, line.Left - 4));
        SetTop(_card, line.Bottom + 2);
    }

    private void Step(int direction)
    {
        if (_tab is not { LineChanges.Count: > 0 } tab)
        {
            return;
        }

        var changes = tab.LineChanges.ToList();
        var index = (changes.IndexOf(_hunk) + direction + changes.Count) % changes.Count;
        Show(changes[index]);
    }

    private void Close()
    {
        _card.IsVisible = false;
        _tab?.Document.Changed -= OnDocumentChanged;
        _tab = null;
    }

    private void OnDocumentChanged(object? sender, Coder.Core.Text.TextChange e) => Close(); // the change moved or is gone

    private static string Describe(DiffHunk hunk) => hunk.Kind switch
    {
        DiffHunkKind.Added => $"{hunk.NewCount} added",
        DiffHunkKind.Deleted => $"{hunk.OldCount} deleted",
        _ => $"{hunk.OldCount} → {hunk.NewCount} lines",
    };

    private static FlatButton Action(Geometry icon, string name, Action action) =>
        new(icon, name, 14) { Width = 24, Height = 24, Command = new RelayCommand(action) };

    private static FlatButton TextAction(Geometry icon, string name, Func<Task> action)
    {
        var button = new FlatButton
        {
            Height = 24,
            Padding = new Thickness(Spacing.S, 0),
            Command = new RelayCommand(() => _ = action()),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Children = { new Icon(icon, 13), new TextBlock { Text = name, FontSize = Typography.SmallSize, VerticalAlignment = VerticalAlignment.Center } },
            },
        };
        AutomationProperties.SetName(button, name);
        return button;
    }
}
