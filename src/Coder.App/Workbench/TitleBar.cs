using System.Linq.Expressions;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Input;
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
/// Custom title bar drawn into the window frame. Left: the logo and the project name, which opens
/// the recent-projects switcher (like Zed). Center: the command center (quick open). Right: layout
/// toggles for the side bar, the bottom panel and the secondary side bar, then the window buttons.
/// The empty area drags the window.
/// </summary>
public sealed class TitleBar : Border
{
    public TitleBar(WorkbenchViewModel workbench, Window window)
    {
        Height = Spacing.TitleBarHeight;
        Background = Palette.Chrome;
        BorderBrush = Palette.Border;
        BorderThickness = new Thickness(0, 0, 0, 1);
        WindowDecorationProperties.SetElementRole(this, WindowDecorationsElementRole.TitleBar);

        var left = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Spacing.XS,
            Margin = new Thickness(Spacing.M, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Children = { new LogoView(18) { Margin = new Thickness(0, 0, Spacing.XS, 0) }, new ProjectSwitcher(workbench) },
        };

        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children =
            {
                Manage(workbench),
                new Border { Width = Spacing.XS },
                SideToggle(workbench, left: true),
                LayoutToggle(workbench, x => x.IsPanelVisible, Icons.LayoutPanel, Icons.LayoutPanelShown, "Toggle Panel (Ctrl+J)",
                    () => workbench.IsPanelVisible = !workbench.IsPanelVisible),
                SideToggle(workbench, left: false),
                new Border { Width = Spacing.S },
                new CaptionButtons(window),
            },
        };

        // Equal side columns keep the command center centered on the window.
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,*") };
        grid.Children.Add(left);
        grid.Children.Add(CommandCenter(workbench).Column(1));
        grid.Children.Add(right.Column(2));
        Child = grid;
    }

    /// <summary>
    /// The cog, like VS Code'"'"'s Manage menu: the command palette, settings, extensions, color theme and
    /// updates. A dot on it says a new version is ready; the menu then leads with "Restart to Update".
    /// </summary>
    private static Control Manage(WorkbenchViewModel workbench)
    {
        var button = new FlatButton(Icons.Settings, "Manage");
        var dot = new Border
        {
            Width = 7,
            Height = 7,
            CornerRadius = new CornerRadius(3.5),
            Background = Palette.Accent,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 5, 5, 0),
            IsHitTestVisible = false,
        }.Bind(IsVisibleProperty, workbench.Updates, x => x.IsReady);

        button.Command = new RelayCommand(() =>
        {
            var updates = workbench.Updates;
            MenuItem Item(string header, string? keys, string command)
            {
                var item = new MenuItem { Header = header, InputGesture = keys is null ? null : KeyGesture.Parse(keys), Command = workbench.Commands.ToCommand(command) };
                AutomationProperties.SetName(item, header);
                return item;
            }

            var update = updates.IsReady
                ? Item($"Restart to Update ({updates.Version})", null, CommandIds.RestartToUpdate)
                : Item(updates.State == Presentation.Updates.UpdateState.Downloading ? updates.StatusText : "Check for Updates…", null, CommandIds.CheckForUpdates);
            if (updates.IsReady)
            {
                update.Classes.Add("accent");
            }

            var menu = new ContextMenu
            {
                ItemsSource = new List<Control>
                {
                    Item("Command Palette…", "Ctrl+Shift+P", CommandIds.CommandPalette),
                    new Separator(),
                    Item("Settings", "Ctrl+OemComma", CommandIds.OpenSettingsPage),
                    Item("Extensions", "Ctrl+Shift+X", CommandIds.ShowExtensions),
                    Item("Color Theme", null, CommandIds.ColorTheme),
                    new Separator(),
                    update,
                    new MenuItem { Header = $"Coder {updates.CurrentVersion}", IsEnabled = false },
                },
                Placement = PlacementMode.BottomEdgeAlignedRight,
            };
            menu.Open(button);
        });
        WindowDecorationProperties.SetElementRole(button, WindowDecorationsElementRole.User);
        return new Panel { Children = { button, dot } };
    }

    /// <summary>
    /// The toggle for the bar on the window's left or right, like VS Code: with the primary side bar moved to
    /// the right, the right-hand button (right-hand icon) toggles it, and the left one the secondary side bar.
    /// </summary>
    private static FlatButton SideToggle(WorkbenchViewModel workbench, bool left)
    {
        var icon = new Icon(left ? Icons.LayoutSideBar : Icons.LayoutSecondary, 16);
        var button = new FlatButton(Icons.Close, string.Empty) { Content = icon };
        bool IsPrimary() => left != workbench.IsSideBarOnRight;
        void Update()
        {
            var shown = IsPrimary() ? workbench.IsSideBarVisible : workbench.IsSecondarySideBarVisible;
            icon.Data = left ? (shown ? Icons.LayoutSideBarShown : Icons.LayoutSideBar) : (shown ? Icons.LayoutSecondaryShown : Icons.LayoutSecondary);
            var name = IsPrimary() ? "Toggle Primary Side Bar (Ctrl+B)" : "Toggle Secondary Side Bar (Ctrl+Alt+B)";
            AutomationProperties.SetName(button, name);
            ToolTip.SetTip(button, name);
        }

        button.Command = new RelayCommand(() =>
        {
            if (IsPrimary())
            {
                workbench.IsSideBarVisible = !workbench.IsSideBarVisible;
            }
            else
            {
                workbench.IsSecondarySideBarVisible = !workbench.IsSecondarySideBarVisible;
            }
        });
        Update();
        workbench.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(WorkbenchViewModel.IsSideBarOnRight) or nameof(WorkbenchViewModel.IsSideBarVisible) or nameof(WorkbenchViewModel.IsSecondarySideBarVisible))
            {
                Update();
            }
        };
        WindowDecorationProperties.SetElementRole(button, WindowDecorationsElementRole.User);
        return button;
    }

    private static FlatButton LayoutToggle(WorkbenchViewModel workbench, Expression<Func<WorkbenchViewModel, bool>> visible,
        Geometry hidden, Geometry shown, string name, Action toggle)
    {
        var icon = new Icon(hidden, 16).Bind(Icon.DataProperty, workbench, visible, isVisible => isVisible ? shown : hidden);
        var button = new FlatButton(Icons.Close, name) { Content = icon, Command = new RelayCommand(toggle) };
        WindowDecorationProperties.SetElementRole(button, WindowDecorationsElementRole.User);
        return button;
    }

    private static FlatButton CommandCenter(WorkbenchViewModel workbench)
    {
        var button = new FlatButton
        {
            Classes = { "command-center" },
            Width = 440,
            Height = 26,
            Padding = new Thickness(Spacing.S, 0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = new DockPanel
            {
                Children =
                {
                    new Icon(Icons.Search, 14) { Margin = new Thickness(0, 0, Spacing.S, 0) }.Dock(Dock.Left),
                    new TextBlock { Text = "Ctrl+P", FontSize = Typography.CaptionSize, Foreground = Palette.TextMuted, VerticalAlignment = VerticalAlignment.Center }.Dock(Dock.Right),
                    new TextBlock
                    {
                        Text = "Search files and commands",
                        Foreground = Palette.TextSecondary,
                        VerticalAlignment = VerticalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    },
                },
            },
            Command = workbench.Commands.ToCommand(CommandIds.QuickOpen),
        };
        AutomationProperties.SetName(button, "Search files and commands");
        WindowDecorationProperties.SetElementRole(button, WindowDecorationsElementRole.User);
        return button;
    }
}
