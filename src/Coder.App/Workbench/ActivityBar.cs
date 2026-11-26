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
    private const double CompactIcon = 18;
    private const double UprightIcon = 22;

    private readonly StackPanel _items;
    private readonly List<Border> _indicators = [];
    private readonly List<FlatButton> _buttons = [];
    private Border? _badge;

    public ActivityBar(WorkbenchViewModel workbench)
    {
        Background = Palette.Chrome;
        BorderBrush = Palette.Border;

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
        BorderThickness = horizontal
            ? new Thickness(0, atBottom ? 1 : 0, 0, atBottom ? 0 : 1)
            : new Thickness(onRight ? 1 : 0, 0, onRight ? 0 : 1, 0);

        var size = horizontal ? CompactSize : Spacing.ActivityBarWidth;
        foreach (var button in _buttons)
        {
            button.Width = size;
            button.Height = size;
            if (button.Content is Icon glyph)
            {
                glyph.Width = glyph.Height = horizontal ? CompactIcon : UprightIcon;
            }
        }

        // The indicator marks the edge facing the side bar's content: the inner side, or under the item.
        foreach (var indicator in _indicators)
        {
            indicator.Width = horizontal ? double.NaN : 2;
            indicator.Height = horizontal ? 2 : double.NaN;
            indicator.HorizontalAlignment = horizontal ? HorizontalAlignment.Stretch : onRight ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            indicator.VerticalAlignment = horizontal ? (atBottom ? VerticalAlignment.Top : VerticalAlignment.Bottom) : VerticalAlignment.Stretch;
            indicator.Margin = horizontal ? new Thickness(9, 0) : new Thickness(0, 10);
            indicator.CornerRadius = new CornerRadius(2);
        }

        _badge?.Margin = horizontal ? new Thickness(0, 1, 0, 0) : new Thickness(0, 0, 7, 8);
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
            Background = Brand.Gradient,
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
            Background = Brand.ButtonGradient,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                FontSize = 10,
                FontWeight = FontWeight.SemiBold,
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
        var button = new FlatButton(icon, name, iconSize: 22)
        {
            Chamfer = 0, // full-size tiles in the bar, like the window buttons
            Command = command,
        };
        _buttons.Add(button);
        return button;
    }
}
