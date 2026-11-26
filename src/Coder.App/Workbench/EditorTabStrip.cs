using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Coder.App.Controls;
using Coder.App.Mvvm;
using Coder.App.Theme;
using Coder.Presentation.Editors;

namespace Coder.App.Workbench;

/// <summary>
/// Horizontally scrolling tabs: a slim scroll bar shows when they overflow, the wheel scrolls them
/// sideways, and the active tab is kept in view. The active tab blends into the editor and carries an accent top line;
/// a dirty tab shows a dot that turns into a close button on hover. Middle-click closes a tab.
/// </summary>
public sealed class EditorTabStrip : Border
{
    /// <summary>Pixels the tabs move per wheel notch.</summary>
    private const double WheelStep = 60;

    private readonly EditorGroupViewModel _editors;
    private readonly ItemsControl _tabs;
    private readonly ScrollViewer _scroller;

    public EditorTabStrip(EditorGroupViewModel editors)
    {
        _editors = editors;
        Height = Spacing.TabHeight;
        Background = Palette.SideBar;
        BorderBrush = Palette.Border;
        BorderThickness = new Thickness(0, 0, 0, 1);

        _tabs = new ItemsControl
        {
            ItemsSource = editors.Tabs,
            ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel { Orientation = Orientation.Horizontal }),
            ItemTemplate = Templates.Row<TabViewModel>(CreateTab),
        };

        // When the tabs overflow, a slim scroll bar appears over their bottom edge.
        _scroller = new ScrollViewer
        {
            Classes = { "slim" },
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _tabs,
        };
        Child = _scroller;

        // The mouse wheel scrolls the tabs sideways, like VS Code (a tab strip has nothing to scroll vertically).
        _scroller.AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            var delta = Math.Abs(e.Delta.Y) > Math.Abs(e.Delta.X) ? e.Delta.Y : e.Delta.X;
            var max = Math.Max(0, _scroller.Extent.Width - _scroller.Viewport.Width);
            _scroller.Offset = _scroller.Offset.WithX(Math.Clamp(_scroller.Offset.X - delta * WheelStep, 0, max));
            e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        // The active tab is always visible, also when it was opened or chosen from elsewhere.
        editors.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EditorGroupViewModel.ActiveTab))
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(RevealActiveTab, Avalonia.Threading.DispatcherPriority.Loaded);
            }
        };
    }

    private void RevealActiveTab()
    {
        if (_editors.ActiveTab is { } tab && _tabs.ContainerFromItem(tab) is { } container)
        {
            container.BringIntoView();
        }
    }

    private Control CreateTab(TabViewModel tab)
    {
        var close = new FlatButton(Icons.Close, $"Close {tab.Title}", iconSize: 14)
        {
            Width = 20,
            Height = 20,
            Padding = new Thickness(0),
            Chamfer = Spacing.ChamferSmall,
            Command = new Presentation.Mvvm.RelayCommand(() => _ = _editors.CloseAsync(tab)),
        };

        var dirtyDot = new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = Palette.Modified,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var title = new TextBlock { Text = tab.Title, VerticalAlignment = VerticalAlignment.Center };
        // The tab surface has the logo's cut corner at the top right, like a folder tab.
        var root = new ChamferBorder
        {
            Padding = new Thickness(Spacing.M, 0, Spacing.M, 0),
            Chamfer = Spacing.ChamferLarge,
            Corners = ChamferCorners.TopRight,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    tab is DiffTabViewModel ? new Icon(Icons.Diff, 14) { Foreground = Palette.TextSecondary }
                        : tab is Presentation.Settings.SettingsPageViewModel ? new Icon(Icons.Settings, 14) { Foreground = Palette.TextSecondary }
                        : FileIcons.File(tab.FilePath ?? tab.Title, 15),
                    title,
                    new Panel { Width = 20, Height = 20, Children = { dirtyDot, close } },
                },
            },
        };
        ToolTip.SetTip(root, tab.Description);
        AutomationProperties.SetName(root, tab.Title);

        // Visual state is derived from the view model in one place.
        void Update()
        {
            var hovered = root.IsPointerOver;
            title.Text = tab.Title;
            title.Foreground = tab.IsActive ? Palette.TextPrimary : Palette.TextSecondary;
            root.Background = tab.IsActive ? Palette.EditorBackground : hovered ? Palette.Hover : Palette.HoverClear;
            root.TopEdgeBrush = tab.IsActive ? Brand.Gradient : null; // the accent follows the cut corner

            // A dirty tab shows a dot; hovering swaps it for the close button.
            dirtyDot.IsVisible = tab.IsDirty && !hovered;
            close.IsVisible = !dirtyDot.IsVisible;
            close.Opacity = tab.IsActive || hovered ? 1 : 0;
        }

        // Follow the view model only while the tab is on screen, so removed tabs leave no handlers behind.
        void OnTabChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Update();
        root.AttachedToVisualTree += (_, _) =>
        {
            tab.PropertyChanged += OnTabChanged;
            Update();
        };
        root.DetachedFromVisualTree += (_, _) => tab.PropertyChanged -= OnTabChanged;
        root.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsPointerOverProperty)
            {
                Update();
            }
        };
        root.PointerPressed += (_, e) =>
        {
            var point = e.GetCurrentPoint(root).Properties;
            if (point.IsMiddleButtonPressed)
            {
                _ = _editors.CloseAsync(tab);
                e.Handled = true;
            }
            else if (point.IsLeftButtonPressed)
            {
                _editors.ActiveTab = tab;
            }
        };
        Update();

        // A hairline between tabs.
        var separator = new Border
        {
            Width = 1,
            Background = Palette.Border,
            HorizontalAlignment = HorizontalAlignment.Right,
            IsHitTestVisible = false,
        };
        return new Panel { Children = { root, separator } };
    }
}
