using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Coder.App.Mvvm;

namespace Coder.App.Terminal;

/// <summary>
/// One terminal on screen: the <see cref="TerminalView"/> and a scroll bar for its history, like VS
/// Code's. The bar shows where the view is in the scrollback, follows the wheel and new output, and
/// dragging it or clicking its track scrolls the terminal. It hides while there is no history (and
/// while a full-screen program such as vim uses the alternate screen).
/// </summary>
public sealed class TerminalPane : DockPanel
{
    private readonly ScrollBar _scrollBar;
    private bool _syncing;

    public TerminalPane()
    {
        _scrollBar = new ScrollBar
        {
            Orientation = Orientation.Vertical,
            SmallChange = 1,
            Minimum = 0,
            IsVisible = false,
        };
        AutomationProperties.SetName(_scrollBar, "Terminal scroll bar");
        Children.Add(_scrollBar.Dock(Dock.Right));
        FindBar = new TerminalFindBar(View);
        Children.Add(new Panel { Children = { View, FindBar } });
        View.FindRequested += (_, _) => FindBar.Open();

        View.ScrollChanged += (_, _) => Sync();

        // The bar's value is the top line (0 = the oldest history); the view counts up from the bottom.
        _scrollBar.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty && !_syncing)
            {
                View.ScrollOffset = (int)Math.Round(_scrollBar.Maximum - _scrollBar.Value);
            }
        };
    }

    public TerminalView View { get; } = new();

    /// <summary>Find in this terminal (Ctrl+F).</summary>
    public TerminalFindBar FindBar { get; }

    /// <summary>For tests: the scroll bar.</summary>
    internal ScrollBar ScrollBar => _scrollBar;

    private void Sync()
    {
        _syncing = true;
        try
        {
            var history = View.ScrollbackLines;
            _scrollBar.IsVisible = history > 0;
            _scrollBar.Maximum = history;
            _scrollBar.ViewportSize = Math.Max(1, View.VisibleRows);
            _scrollBar.LargeChange = Math.Max(1, View.VisibleRows - 1);
            _scrollBar.Value = history - View.ScrollOffset;
        }
        finally
        {
            _syncing = false;
        }
    }
}
