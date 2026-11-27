using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Coder.App.Controls;
using Coder.App.Mvvm;
using Coder.App.Theme;
using Coder.Presentation.Commands;
using Coder.Presentation.Mvvm;
using Coder.Presentation.Workbench;

namespace Coder.App.Workbench;

/// <summary>
/// The bar of views (Explorer, Search, Source Control). Upright on the window's edge beside the side bar,
/// or lying across the top or bottom of the side bar. Each view item shows an accent indicator while
/// its view is open; clicking the open view's item hides the side bar.
/// </summary>
public sealed class ActivityBar : Border
{
    private const double CompactSize = 32; // lying across the side bar: a slim strip, like VS Code's
    private const double CompactTile = 28;
    private const double UprightTile = 36; // a rounded tile centered in the 48px bar
    private const double CompactIcon = 17;
    private const double UprightIcon = 20;

    private readonly StackPanel _items;
    private readonly List<Border> _indicators = [];
    private readonly List<FlatButton> _buttons = [];
    private Border? _badge;

    public ActivityBar(WorkbenchViewModel workbench)
    {
        Background = Palette.Chrome; // part of the shell: no divider line

        _items = new StackPanel
        {
            Spacing = Spacing.XS,
            Children =
            {
                ViewItem(workbench, SideBarView.Explorer, Icons.Files, "Explorer (Ctrl+Shift+E)"),
                ViewItem(workbench, SideBarView.Search, Icons.Search, "Search (Ctrl+Shift+F)"),
                WithBadge(ViewItem(workbench, SideBarView.SourceControl, Icons.SourceControl, "Source Control (Ctrl+Shift+G)"), workbench),
                ViewItem(workbench, SideBarView.Extensions, Icons.Extensions, "Extensions (Ctrl+Shift+X)"),
                Item(Icons.Command, "Command Palette (Ctrl+Shift+P)", workbench.Commands.ToCommand(CommandIds.CommandPalette)),
            },
        };
        Child = _items;
        Place(horizontal: false, onRight: false, atBottom: false);
    }

    public bool IsHorizontal => _items.Orientation == Orientation.Horizontal;

    /// <summary>Upright on the left or right window edge, or lying across the top or bottom of the side bar.</summary>
    public void Place(bool horizontal, bool onRight, bool atBottom)
    {
        _items.Orientation = horizontal ? Orientation.Horizontal : Orientation.Vertical;
        _items.Margin = horizontal ? new Thickness(Spacing.S, 0) : new Thickness(0, Spacing.XS);
        _items.Spacing = horizontal ? 2 : Spacing.XS;
        Width = horizontal ? double.NaN : Spacing.ActivityBarWidth;
        Height = horizontal ? CompactSize : double.NaN;
        var size = horizontal ? CompactTile : UprightTile;
        foreach (var button in _buttons)
        {
            button.Width = size;
            button.Height = size;
            if (button.Content is Icon glyph)
            {
                glyph.Width = glyph.Height = horizontal ? CompactIcon : UprightIcon;
            }
        }

        // The indicator is a short pill on the bar's outer edge (upright), or along the strip's edge (lying).
        foreach (var indicator in _indicators)
        {
            indicator.Width = horizontal ? 14 : 3;
            indicator.Height = horizontal ? 3 : 16;
            indicator.HorizontalAlignment = horizontal ? HorizontalAlignment.Center : onRight ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            indicator.VerticalAlignment = horizontal ? (atBottom ? VerticalAlignment.Top : VerticalAlignment.Bottom) : VerticalAlignment.Center;
            indicator.Margin = new Thickness(0);
            indicator.CornerRadius = new CornerRadius(2);
        }

        _badge?.Margin = horizontal ? new Thickness(0, 1, 0, 0) : new Thickness(0, 0, 4, 0);
        if (_badge is not null)
        {
            _badge.VerticalAlignment = horizontal ? VerticalAlignment.Top : VerticalAlignment.Bottom; // in a slim strip, up by the corner
            _badge.MinWidth = _badge.Height = horizontal ? 14 : 16;
        }
    }

    private Panel ViewItem(WorkbenchViewModel workbench, SideBarView view, Geometry icon, string name)
    {
        bool IsActive(WorkbenchViewModel w) => w.IsSideBarVisible && w.ActiveSideBarView == view;

        var button = Item(icon, name, new RelayCommand(() => workbench.ToggleSideBarView(view)))
            .BindClass("active", workbench, IsActive);
        var indicator = new Border
        {
            Background = Palette.Accent,
            IsHitTestVisible = false,
        };
        _indicators.Add(indicator);
        void Update() => indicator.IsVisible = IsActive(workbench);
        Update();
        workbench.PropertyChanged += (_, _) => Update();

        return new Panel { Children = { button, indicator } };
    }

    /// <summary>The number of changed files, in a small pill on the Source Control item.</summary>
    private Panel WithBadge(Panel item, WorkbenchViewModel workbench)
    {
        var badge = new Border
        {
            MinWidth = 16,
            Height = 16,
            Padding = new Thickness(4, 0),
            CornerRadius = new CornerRadius(8),
            Background = Palette.AccentFill,
            BorderBrush = Palette.Chrome, // a ring in the bar's color lifts the badge off the icon
            BorderThickness = new Thickness(1.5),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                FontSize = 10,
                FontWeight = Typography.Strong,
                Foreground = Palette.TextOnAccent,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            }.Bind(TextBlock.TextProperty, workbench.SourceControl, x => x.ChangeCount, count => count > 999 ? "999+" : count.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        }.Bind(IsVisibleProperty, workbench.SourceControl, x => x.ChangeCount, count => count > 0);
        _badge = badge;
        item.Children.Add(badge);
        return item;
    }

    private FlatButton Item(Geometry icon, string name, System.Windows.Input.ICommand command)
    {
        var button = new FlatButton(icon, name, iconSize: UprightIcon)
        {
            Radius = Spacing.RadiusPanel.TopLeft,
            HorizontalAlignment = HorizontalAlignment.Center,
            Classes = { "activity" },
            Command = command,
        };
        _buttons.Add(button);
        return button;
    }
}
