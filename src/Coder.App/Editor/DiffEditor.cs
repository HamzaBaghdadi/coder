using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Coder.App.Controls;
using Coder.App.Mvvm;
using Coder.App.Theme;
using Coder.Presentation.Editors;
using Coder.Presentation.Mvvm;

namespace Coder.App.Editor;

/// <summary>
/// A diff tab's content: a header naming both sides with the change count and buttons for the
/// previous / next change (also F7 / Shift+F7) and for opening the file, above the side-by-side view.
/// </summary>
public sealed class DiffEditor : DockPanel
{
    private readonly DiffView _view = new();
    private readonly TextBlock _leftLabel = Label();
    private readonly TextBlock _rightLabel = Label();
    private readonly TextBlock _summary = new() { FontSize = Typography.SmallSize, Foreground = Palette.TextMuted, VerticalAlignment = VerticalAlignment.Center };

    public DiffEditor(Func<string, Task> openFile)
    {
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            Children =
            {
                _summary,
                new Border { Width = Spacing.S },
                new FlatButton(Icons.ArrowUp, "Previous Change (Shift+F7)") { Command = new RelayCommand(() => GoToChange(-1)) },
                new FlatButton(Icons.ArrowDown, "Next Change (F7)") { Command = new RelayCommand(() => GoToChange(1)) },
                new FlatButton(Icons.GoToFile, "Open File") { Command = new RelayCommand(() => _ = Diff?.FilePath is { } path ? openFile(path) : Task.CompletedTask) },
            },
        };

        var header = new Border
        {
            Height = 32,
            Background = Palette.SideBar,
            BorderBrush = Palette.Border,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new DockPanel
            {
                Margin = new Thickness(0, 0, Spacing.S, 0),
                Children =
                {
                    actions.Dock(Dock.Right),
                    new UniformGrid { Columns = 2, Children = { _leftLabel, _rightLabel } },
                },
            },
        };

        Children.Add(header.Dock(Dock.Top));
        Children.Add(new ScrollViewer
        {
            Content = _view,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });

        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.F7 && e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift)
            {
                GoToChange(e.KeyModifiers == KeyModifiers.Shift ? -1 : 1);
                e.Handled = true;
            }
        });
    }

    public DiffTabViewModel? Diff
    {
        get => _view.Diff;
        set
        {
            _view.Diff = value;
            _leftLabel.Text = value?.OriginalLabel ?? string.Empty;
            _rightLabel.Text = value?.ModifiedLabel ?? string.Empty;
            var count = value?.Hunks.Count ?? 0;
            _summary.Text = count switch
            {
                0 => "No changes",
                1 => "1 change",
                _ => $"{count.ToString(CultureInfo.InvariantCulture)} changes",
            };

            // Open at the first change, like VS Code.
            if (value?.HunkStartRows.FirstOrDefault() is { } first and > 3)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => _view.RevealRow(first), Avalonia.Threading.DispatcherPriority.Background);
            }
        }
    }

    public void FocusView() => _view.Focus();

    /// <summary>Jumps to the next (or previous) change after the top visible row, wrapping around.</summary>
    private void GoToChange(int direction)
    {
        if (Diff is not { } diff)
        {
            return;
        }

        var starts = diff.HunkStartRows.ToList();
        if (starts.Count == 0)
        {
            return;
        }

        var current = _view.FirstVisibleRow + 3; // RevealRow keeps three rows of context above
        var target = direction > 0
            ? starts.FirstOrDefault(row => row > current, starts[0])
            : starts.LastOrDefault(row => row < current, starts[^1]);
        _view.RevealRow(target);
    }

    private static TextBlock Label() => new()
    {
        FontSize = Typography.SmallSize,
        Foreground = Palette.TextSecondary,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(Spacing.L, 0, 0, 0),
        TextTrimming = TextTrimming.CharacterEllipsis,
    };
}
