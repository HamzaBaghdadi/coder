using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Coder.App.Controls;
using Coder.App.Theme;
using Coder.Core.Terminal;
using Coder.Presentation.Mvvm;

namespace Coder.App.Terminal;

/// <summary>
/// Find in the terminal (Ctrl+F while it has focus), floating over its top-right corner like the
/// editor's find card. Matches in the history and on screen are highlighted; searching starts at the
/// newest one (nearest the prompt) and Enter goes up to older ones, Shift+Enter back down, as in
/// VS Code. "Aa" matches case. Esc closes it and returns to the terminal.
/// </summary>
public sealed class TerminalFindBar : Border
{
    private readonly TerminalView _view;
    private readonly TextBox _query;
    private readonly TextBlock _status;
    private readonly FlatButton _matchCase;
    private IReadOnlyList<TerminalMatch> _matches = [];
    private int _current = -1;

    public TerminalFindBar(TerminalView view)
    {
        _view = view;
        IsVisible = false;
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Top;
        Margin = new Thickness(0, 0, 8, 0);
        Padding = new Thickness(6);
        Background = Palette.Elevated;
        BorderBrush = Palette.BorderStrong;
        BorderThickness = new Thickness(1, 0, 1, 1);
        CornerRadius = new CornerRadius(0, 0, 8, 8);
        ThemeManager.BindShadow(this, "0 8 24 0");

        _query = new TextBox { Width = 200, PlaceholderText = "Find", VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(_query, "Find in terminal");
        _query.TextChanged += (_, _) => Search(fromNewest: true);
        _query.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);

        _matchCase = new FlatButton { Content = new TextBlock { Text = "Aa", FontSize = Typography.SmallSize }, Width = 24, Height = 22, Padding = new Thickness(0) };
        AutomationProperties.SetName(_matchCase, "Match Case");
        ToolTip.SetTip(_matchCase, "Match Case");
        _matchCase.Command = new RelayCommand(() =>
        {
            MatchCase = !MatchCase;
            _matchCase.Classes.Set("active", MatchCase);
            Search(fromNewest: true);
        });
        _query.InnerRightContent = _matchCase;

        _status = new TextBlock
        {
            Width = 74,
            FontSize = Typography.SmallSize,
            Foreground = Palette.TextSecondary,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(Spacing.S, 0, Spacing.XS, 0),
        };
        AutomationProperties.SetName(_status, "Terminal find results");

        Child = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            Children =
            {
                _query,
                _status,
                Action(Icons.ArrowUp, "Previous Match (Enter)", () => Step(-1)),
                Action(Icons.ArrowDown, "Next Match (Shift+Enter)", () => Step(1)),
                Action(Icons.Close, "Close (Escape)", Close),
            },
        };
    }

    public bool MatchCase { get; private set; }

    /// <summary>For tests: the matches and the shown one.</summary>
    internal (IReadOnlyList<TerminalMatch> Matches, int Current) State => (_matches, _current);

    /// <summary>Shows the bar with the query selected, ready to type (Ctrl+F).</summary>
    public void Open()
    {
        IsVisible = true;
        Search(fromNewest: true); // the terminal may have new output since the last search
        Dispatcher.UIThread.Post(() =>
        {
            _query.Focus();
            _query.SelectAll();
        });
    }

    public void Close()
    {
        IsVisible = false;
        _view.SetMatches([], -1);
        _view.Focus();
    }

    /// <summary>Sets the query (tests and the command palette's "Find" use it too).</summary>
    public void SetQuery(string text) => _query.Text = text;

    /// <summary>Finds every match again; the shown match is the newest (or keeps its place among them).</summary>
    private void Search(bool fromNewest)
    {
        var buffer = _view.Session?.Emulator.Buffer;
        _matches = buffer is null ? [] : TerminalSearch.FindAll(buffer, _query.Text ?? string.Empty, MatchCase);
        _current = _matches.Count == 0 ? -1 : fromNewest ? _matches.Count - 1 : Math.Min(_current, _matches.Count - 1);
        Show();
    }

    /// <summary>Up (-1, older) or down (+1, newer) to the next match, wrapping around.</summary>
    private void Step(int direction)
    {
        Search(fromNewest: _current < 0); // new output may have arrived
        if (_matches.Count == 0)
        {
            return;
        }

        _current = ((_current + direction) % _matches.Count + _matches.Count) % _matches.Count;
        Show();
    }

    private void Show()
    {
        var hasQuery = !string.IsNullOrEmpty(_query.Text);
        _status.Text = !hasQuery ? string.Empty
            : _matches.Count == 0 ? "No results"
            : $"{(_current + 1).ToString(CultureInfo.InvariantCulture)} of {_matches.Count.ToString(CultureInfo.InvariantCulture)}";
        _status.Foreground = hasQuery && _matches.Count == 0 ? Palette.Danger : Palette.TextSecondary;
        _view.SetMatches(_matches, _current);
        if (_current >= 0)
        {
            _view.Reveal(_matches[_current].Line);
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                Step(e.KeyModifiers == KeyModifiers.Shift ? 1 : -1);
                e.Handled = true;
                break;
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
            case Key.F when e.KeyModifiers == KeyModifiers.Control:
                _query.SelectAll(); // Ctrl+F again: start a new search
                e.Handled = true;
                break;
        }
    }

    private static FlatButton Action(Geometry icon, string name, Action action) =>
        new(icon, name, 14) { Width = 24, Height = 24, Command = new RelayCommand(action) };
}
