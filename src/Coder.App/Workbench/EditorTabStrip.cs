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
/// sideways, and the active tab is kept in view. Tabs are rounded pills on the editor panel; the active one is filled
/// and underlined with the accent;
/// a dirty tab shows a dot that turns into a close button on hover. Middle-click closes a tab, and a
/// file's tab can be dragged onto Coder Agent's chat to attach the file.
/// </summary>
public sealed class EditorTabStrip : Border
{
    /// <summary>Pixels the tabs move per wheel notch.</summary>
    private const double WheelStep = 60;

    /// <summary>Pixels a press moves before it becomes a drag (onto Coder Agent's chat).</summary>
    private const double DragThreshold = 6;

    private readonly EditorGroupViewModel _editors;
    private readonly ItemsControl _tabs;
    private readonly ScrollViewer _scroller;

    public EditorTabStrip(EditorGroupViewModel editors)
    {
        _editors = editors;
        Height = Spacing.TabHeight;
        Background = Palette.EditorBackground; // the tabs sit on the editor panel itself
        BorderBrush = Palette.Border;
        BorderThickness = new Thickness(0, 0, 0, 1);
        Padding = new Thickness(Spacing.XS, 0);

        _tabs = new ItemsControl
        {
            ItemsSource = editors.Tabs,
            ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel { Orientation = Orientation.Horizontal, Spacing = Spacing.XXS }),
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
            Radius = Spacing.RadiusSmall.TopLeft,
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
        var underline = new Border
        {
            Height = 2,
            Margin = new Thickness(Spacing.S, 0),
            CornerRadius = new CornerRadius(1),
            Background = Palette.Accent,
            VerticalAlignment = VerticalAlignment.Bottom,
            IsHitTestVisible = false,
        };

        // The tab: a rounded pill, vertically centered in the strip.
        var root = new ChamferBorder
        {
            Height = Spacing.TabHeight - 8,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(Spacing.S + Spacing.XXS, 0, Spacing.XS, 0),
            Radius = Spacing.RadiusMedium.TopLeft,
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
            root.Background = tab.IsActive ? Palette.Pressed : hovered ? Palette.Hover : Palette.HoverClear;
            underline.IsVisible = tab.IsActive; // a shape cue besides the fill, so the active tab never relies on color alone

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
        // A file's tab can be dragged onto Coder Agent's chat to attach the file.
        Point? pressedAt = null;
        var dragging = false;
        var drag = new PathDrag();
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
                pressedAt = tab.FilePath is null ? null : e.GetPosition(root);
            }
        };

        root.PointerMoved += (_, e) =>
        {
            if (pressedAt is not { } start)
            {
                return;
            }

            var position = e.GetPosition(root);
            if (!dragging && Math.Abs(position.X - start.X) < DragThreshold && Math.Abs(position.Y - start.Y) < DragThreshold)
            {
                return;
            }

            if (!dragging)
            {
                dragging = true;
                e.Pointer.Capture(root);
            }

            root.Cursor = new Cursor(drag.Move(root, e) is null ? StandardCursorType.No : StandardCursorType.DragCopy);
            e.Handled = true;
        };
        root.PointerReleased += (_, e) =>
        {
            pressedAt = null;
            if (!dragging)
            {
                return;
            }

            dragging = false;
            e.Pointer.Capture(null);
            root.Cursor = null;
            if (tab.FilePath is { } path)
            {
                drag.Drop(root, e, [path]);
            }
            else
            {
                drag.Cancel();
            }

            e.Handled = true;
        };
        root.PointerCaptureLost += (_, _) =>
        {
            pressedAt = null;
            dragging = false;
            root.Cursor = null;
            drag.Cancel();
        };
        Update();

        return new Panel { Children = { root, underline } };
    }
}
