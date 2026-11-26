using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Coder.App.Mvvm;
using Coder.App.Theme;
using Coder.Presentation.Settings;
using Coder.Presentation.Workbench;

namespace Coder.App.Workbench;

/// <summary>
/// The workbench window. Layout, top to bottom: title bar, body (activity bar | side bar | editors),
/// status bar, with the command palette floating above everything. It restores and saves its own
/// placement and the side bar width.
/// </summary>
public sealed class MainWindow : Window
{
    private const double SideBarMinWidth = 170;
    private const double TerminalPanelHeight = 280;
    private const double SecondarySideBarWidth = 380;

    private readonly WorkbenchViewModel _workbench;
    private readonly KeyBindingRouter _keyBindings;
    private readonly EditorArea _editorArea;
    private readonly ColumnDefinition _sideBarColumn;
    private double _sideBarWidth;
    private PixelPoint _normalPosition;
    private Size _normalSize;
    private bool _closeConfirmed;

    public MainWindow(WorkbenchViewModel workbench)
    {
        _workbench = workbench;
        _keyBindings = new KeyBindingRouter(workbench.Commands, message => workbench.StatusBar.Message = message);
        _editorArea = new EditorArea(workbench);
        _sideBarWidth = Math.Max(SideBarMinWidth, workbench.Settings.SideBarWidth);
        _sideBarColumn = new ColumnDefinition(_sideBarWidth, GridUnitType.Pixel) { MinWidth = SideBarMinWidth };

        MinWidth = 640;
        MinHeight = 400;
        RestorePlacement(workbench.Settings.Window);

        // The native frame (so Windows animates minimize and maximize, and snaps the window), with the
        // app's own title bar and caption buttons drawn over its caption area.
        WindowDecorations = WindowDecorations.Full;
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaTitleBarHeightHint = Spacing.TitleBarHeight;
        Icon = new WindowIcon(Brand.Logo);
        this.Bind(TitleProperty, workbench, x => x.Title);

        Content = new Panel
        {
            Children =
            {
                new DockPanel
                {
                    Children =
                    {
                        new TitleBar(workbench, this).Dock(Dock.Top),
                        new StatusBarView(workbench).Dock(Dock.Bottom),
                        Body(),
                    },
                },
                new CommandPaletteView(workbench.Palette),
            },
        };

        workbench.Palette.Closed += (_, _) => _editorArea.FocusEditor();

        // Custom-drawn views (editor, diff, terminal, lists) paint with the theme's brushes when they
        // render, so a theme switch repaints them.
        EventHandler repaint = (_, _) => RepaintAll(this);
        ThemeManager.Changed += repaint;
        Closed += (_, _) => ThemeManager.Changed -= repaint;

        // "Restart to Update": unsaved files are dealt with and the session saved first, like a normal close.
        workbench.Updates.ConfirmRestart = async () =>
        {
            if (!await _workbench.ConfirmCloseAsync())
            {
                return false;
            }

            _closeConfirmed = true;
            SaveSession();
            await _workbench.DisposeAsync(); // stops language servers before the updater takes over
            return true;
        };

        // Tunnel so global shortcuts win over focused controls (e.g. Ctrl+P inside the editor).
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>
    /// Activity bar, primary side bar, editors and panel, secondary side bar. The primary side bar is on
    /// the left or the right (the secondary takes the other side), and the activity bar is upright on the
    /// window's edge or lies across the top or bottom of the side bar, or is hidden. Changes apply live.
    /// </summary>
    private Grid Body()
    {
        var splitter = new GridSplitter { Width = 2, ResizeDirection = GridResizeDirection.Columns };
        var sideBar = new SideBar(_workbench); // hidden, not just zero-width: a collapsed column does not clip its content
        var activityBar = new ActivityBar(_workbench);
        var primary = new DockPanel { Children = { sideBar } };
        var secondaryColumn = new ColumnDefinition(0, GridUnitType.Pixel);
        var secondarySplitter = new GridSplitter { Width = 2, ResizeDirection = GridResizeDirection.Columns, IsVisible = false };
        var secondary = new SecondarySideBar(_workbench) { IsVisible = false };
        var outerLeft = new ColumnDefinition(GridLength.Auto);
        var outerRight = new ColumnDefinition(GridLength.Auto);
        var grid = new Grid
        {
            Children =
            {
                EditorAndPanel().Column(3),
                primary,
                splitter,
                secondarySplitter,
                secondary,
            },
        };
        AutomationProperties.SetName(splitter, "Resize Side Bar");

        void Arrange()
        {
            var right = _workbench.IsSideBarOnRight;
            var location = _workbench.ActivityBarLocation;

            // Columns, left to right: outer edge, side bar, divider, editors, divider, side bar, outer edge.
            grid.ColumnDefinitions.Clear();
            grid.ColumnDefinitions.AddRange(
            [
                outerLeft, right ? secondaryColumn : _sideBarColumn, new ColumnDefinition(GridLength.Auto), new ColumnDefinition(1, GridUnitType.Star),
                new ColumnDefinition(GridLength.Auto), right ? _sideBarColumn : secondaryColumn, outerRight,
            ]);
            Grid.SetColumn(primary, right ? 5 : 1);
            Grid.SetColumn(splitter, right ? 4 : 2);
            Grid.SetColumn(secondary, right ? 1 : 5);
            Grid.SetColumn(secondarySplitter, right ? 2 : 4);
            sideBar.BorderThickness = right ? new Thickness(1, 0, 0, 0) : new Thickness(0, 0, 1, 0);
            secondary.BorderThickness = right ? new Thickness(0, 0, 1, 0) : new Thickness(1, 0, 0, 0);

            // The activity bar: on the outer edge beside the side bar, or across its top or bottom.
            (activityBar.Parent as Panel)?.Children.Remove(activityBar);
            activityBar.Place(horizontal: location is "top" or "bottom", onRight: right, atBottom: location == "bottom");
            switch (location)
            {
                case "top" or "bottom":
                    DockPanel.SetDock(activityBar, location == "top" ? Dock.Top : Dock.Bottom);
                    primary.Children.Insert(0, activityBar);
                    break;
                case "side":
                    grid.Children.Add(activityBar.Column(right ? 6 : 0));
                    break;
            }
        }

        Arrange();

        // The secondary side bar, hidden until toggled, keeping the user's width.
        var secondaryWidth = SecondarySideBarWidth;
        void ApplySecondaryVisibility()
        {
            var visible = _workbench.IsSecondarySideBarVisible;
            if (!visible && secondaryColumn.ActualWidth > 0)
            {
                secondaryWidth = secondaryColumn.ActualWidth;
            }

            secondaryColumn.MinWidth = visible ? SideBarMinWidth : 0;
            secondaryColumn.Width = new GridLength(visible ? secondaryWidth : 0);
            secondarySplitter.IsVisible = visible;
            secondary.IsVisible = visible;
        }

        ApplySecondaryVisibility();

        void ApplySideBarVisibility()
        {
            var visible = _workbench.IsSideBarVisible;
            if (!visible && _sideBarColumn.ActualWidth > 0)
            {
                _sideBarWidth = _sideBarColumn.ActualWidth; // remember the user's width
            }

            _sideBarColumn.MinWidth = visible ? SideBarMinWidth : 0;
            _sideBarColumn.Width = new GridLength(visible ? _sideBarWidth : 0);
            splitter.IsVisible = visible;
            primary.IsVisible = visible;
        }

        ApplySideBarVisibility();

        // Double-clicking a divider puts it back where it started (the side bar: its width from settings).
        splitter.DoubleTapped += (_, _) => _sideBarColumn.Width = new GridLength(_sideBarWidth = Math.Max(SideBarMinWidth, _workbench.Settings.SideBarDefaultWidth));
        secondarySplitter.DoubleTapped += (_, _) => secondaryColumn.Width = new GridLength(secondaryWidth = SecondarySideBarWidth);

        _workbench.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(WorkbenchViewModel.IsSideBarVisible):
                    ApplySideBarVisibility();
                    break;
                case nameof(WorkbenchViewModel.IsSecondarySideBarVisible):
                    ApplySecondaryVisibility();
                    break;
                case nameof(WorkbenchViewModel.IsSideBarOnRight) or nameof(WorkbenchViewModel.ActivityBarLocation):
                    Arrange();
                    break;
            }
        };

        return grid;
    }

    /// <summary>The editors on top and the terminal panel below them, split by a draggable divider.</summary>
    private Grid EditorAndPanel()
    {
        var panelRow = new RowDefinition(0, GridUnitType.Pixel);
        var splitter = new GridSplitter { Height = 2, ResizeDirection = GridResizeDirection.Rows, IsVisible = false };
        var panel = new BottomPanel(_workbench) { IsVisible = false }; // a zero-height row does not clip, so hide it
        var grid = new Grid
        {
            RowDefinitions = { new RowDefinition(1, GridUnitType.Star) { MinHeight = 120 }, new RowDefinition(GridLength.Auto), panelRow },
            Children =
            {
                _editorArea.Row(0),
                splitter.Row(1),
                panel.Row(2),
            },
        };

        var panelHeight = TerminalPanelHeight;
        splitter.DoubleTapped += (_, _) => panelRow.Height = new GridLength(panelHeight = TerminalPanelHeight);
        _workbench.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(WorkbenchViewModel.IsPanelVisible))
            {
                return;
            }

            var visible = _workbench.IsPanelVisible;
            if (!visible && panelRow.ActualHeight > 0)
            {
                panelHeight = panelRow.ActualHeight; // remember the user's height
            }

            panelRow.MinHeight = visible ? 100 : 0;
            panelRow.Height = new GridLength(visible ? panelHeight : 0);
            splitter.IsVisible = visible;
            panel.IsVisible = visible;
            if (!visible)
            {
                _editorArea.FocusEditor();
            }
        };

        return grid;
    }

    private static void RepaintAll(Visual root)
    {
        root.InvalidateVisual();
        foreach (var visual in root.GetVisualDescendants())
        {
            visual.InvalidateVisual();
        }
    }

    private void RestorePlacement(WindowPlacement? placement)
    {
        if (placement is null)
        {
            Width = 1280;
            Height = 800;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }

        Width = Math.Max(MinWidth, placement.Width);
        Height = Math.Max(MinHeight, placement.Height);
        Position = new PixelPoint(placement.X, placement.Y);
        WindowStartupLocation = WindowStartupLocation.Manual;
        if (placement.IsMaximized)
        {
            WindowState = WindowState.Maximized;
        }

        // A monitor may have been disconnected since last time: never open off-screen.
        Opened += (_, _) =>
        {
            if (Screens.ScreenFromWindow(this) is null && Screens.Primary is { } primary)
            {
                var area = primary.WorkingArea;
                Position = new PixelPoint(area.X + (area.Width - (int)Width) / 2, area.Y + (area.Height - (int)Height) / 2);
            }
        };
    }

    /// <summary>Remembers the non-maximized bounds, which are what should be restored next time.</summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty && WindowState == WindowState.Minimized)
        {
            Services.MemoryTrimmer.TrimSoon();
        }

        if (WindowState == WindowState.Normal && (change.Property == ClientSizeProperty || change.Property == WindowStateProperty))
        {
            _normalSize = ClientSize;
            _normalPosition = Position;
        }
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        // Inside a terminal (its view or its find bar), keys belong to the terminal except a few app shortcuts.
        var focused = FocusManager?.GetFocusedElement() as Visual;
        var terminalHasFocus = focused?.FindAncestorOfType<Terminal.TerminalPane>(includeSelf: true) is not null;

        // In the explorer, F2 renames the file (not the symbol at the editor's caret) and Delete deletes it.
        if (e.Key is Key.F2 or Key.Delete && e.KeyModifiers == KeyModifiers.None && focused?.FindAncestorOfType<ExplorerView>(includeSelf: true) is not null)
        {
            return;
        }

        if (!e.Handled && _keyBindings.TryExecute(e, terminalHasFocus))
        {
            e.Handled = true;
        }
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_closeConfirmed)
        {
            return;
        }

        // Ask about unsaved files first; close for real only if the user did not cancel.
        e.Cancel = true;
        if (await _workbench.ConfirmCloseAsync())
        {
            _closeConfirmed = true;
            SaveSession();
            await _workbench.DisposeAsync(); // stops language servers
            Close();
        }
    }

    private void SaveSession()
    {
        if (WindowState == WindowState.Normal)
        {
            _normalSize = ClientSize;
            _normalPosition = Position;
        }

        if (_workbench.IsSideBarVisible)
        {
            _sideBarWidth = _sideBarColumn.ActualWidth;
        }

        var placement = new WindowPlacement(_normalPosition.X, _normalPosition.Y, _normalSize.Width, _normalSize.Height,
            WindowState == WindowState.Maximized);
        _workbench.SaveSettings(placement, _sideBarWidth);
    }
}
