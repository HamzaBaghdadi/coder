using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Coder.App.Controls;
using Coder.App.Mvvm;
using Coder.App.Theme;
using Coder.Presentation.Commands;
using Coder.Presentation.Mvvm;
using Coder.Presentation.Workbench;

namespace Coder.App.Workbench;

/// <summary>
/// The project name (with a chevron) in the title bar, and the dropdown it opens (like a select, as in Zed):
/// recent projects, the current one checked, then "Open Folder…". Hovering another project, or moving the
/// keyboard onto it, shows "Open in This Window" and "Open in New Window". Arrow keys move, Enter opens here,
/// Ctrl+Enter opens in a new window, Esc or a click elsewhere closes. Ctrl+R opens it too.
/// </summary>
public sealed class ProjectSwitcher : Panel
{
    private const double MenuWidth = 340;

    private readonly WorkbenchViewModel _workbench;
    private readonly Popup _popup;
    private readonly ListBox _list;

    public ProjectSwitcher(WorkbenchViewModel workbench)
    {
        _workbench = workbench;
        VerticalAlignment = VerticalAlignment.Center;

        var button = new FlatButton
        {
            Height = 28,
            Padding = new Thickness(Spacing.S, 0, Spacing.XS + Spacing.XXS, 0),
            Command = new RelayCommand(Toggle),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = Spacing.XS,
                Children =
                {
                    new TextBlock { FontWeight = Typography.Strong, VerticalAlignment = VerticalAlignment.Center }
                        .Bind(TextBlock.TextProperty, workbench, x => x.ProjectName),
                    // The chevron says "this opens a list", like a select.
                    new Icon(Icons.ChevronDown, 14) { Foreground = Palette.TextSecondary, VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        AutomationProperties.SetName(button, "Recent projects");
        ToolTip.SetTip(button, "Switch project (Ctrl+R)");
        WindowDecorationProperties.SetElementRole(button, WindowDecorationsElementRole.User);

        _list = new ListBox
        {
            ItemContainerTheme = AppStyles.ListRowTheme,
            ItemTemplate = Templates.Row<Entry>(Row),
            MaxHeight = 360,
            Background = Brushes.Transparent,
        };
        AutomationProperties.SetName(_list, "Recent projects list");
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        _list.ContainerPrepared += (_, e) => AutomationProperties.SetName(e.Container, e.Container.DataContext is Entry entry ? entry.Name : string.Empty);
        _list.Tapped += async (_, e) =>
        {
            // The row's own "open here / in a new window" buttons handle their clicks.
            if ((e.Source as Visual)?.FindAncestorOfType<FlatButton>(includeSelf: true) is not null)
            {
                return;
            }

            if (e.Source is Visual { DataContext: Entry entry })
            {
                await ChooseAsync(entry);
            }
        };
        _list.OnRowKeyDown(async (_, e) =>
        {
            if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.Control && _list.SelectedItem is Entry { Folder: { } folder } chosen && !chosen.IsCurrent)
            {
                e.Handled = true;
                _popup!.IsOpen = false;
                OpenInNewWindow(folder);
            }
            else if (e.Key == Key.Enter && _list.SelectedItem is Entry entry)
            {
                e.Handled = true;
                await ChooseAsync(entry);
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                _popup!.IsOpen = false;
            }
        });

        _popup = new Popup
        {
            PlacementTarget = button,
            Placement = PlacementMode.BottomEdgeAlignedLeft,
            VerticalOffset = 4,
            IsLightDismissEnabled = true,
            Child = new ChamferBorder
            {
                Width = MenuWidth,
                Background = Palette.Elevated,
                BorderBrush = Palette.BorderStrong,
                BorderThickness = 1,
                Radius = Spacing.RadiusLarge.TopLeft,
                Padding = new Thickness(0, Spacing.XS),
                Child = _list,
            },
        };

        Children.Add(button);
        Children.Add(_popup);
        workbench.RecentProjectsRequested += (_, _) => Open();
    }

    public bool IsOpen => _popup.IsOpen;

    private void Toggle()
    {
        if (_popup.IsOpen)
        {
            _popup.IsOpen = false;
        }
        else
        {
            Open();
        }
    }

    /// <summary>Fills the menu from the current recent folders and puts the keyboard on the current project.</summary>
    private void Open()
    {
        var entries = _workbench.RecentFolders
            .Select(folder => new Entry(folder, Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar)),
                Path.GetDirectoryName(folder.TrimEnd(Path.DirectorySeparatorChar)) ?? string.Empty, _workbench.IsCurrentProject(folder)))
            .Append(new Entry(null, "Open Folder…", "Ctrl+Alt+O", IsCurrent: false))
            .ToList();
        _list.ItemsSource = entries;
        _list.SelectedItem = entries.FirstOrDefault(e => e.IsCurrent) ?? entries[0];
        _popup.IsOpen = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_list.ContainerFromItem(_list.SelectedItem!) is { } container)
            {
                container.Focus();
            }
        });
    }

    private async Task ChooseAsync(Entry entry)
    {
        _popup.IsOpen = false;
        if (entry.Folder is { } folder)
        {
            await _workbench.SwitchProjectAsync(folder);
        }
        else
        {
            await _workbench.Commands.Get(CommandIds.OpenFolder).Execute();
        }
    }

    /// <summary>
    /// Opens <paramref name="folder"/> in a second Coder window (its own process, like VS Code), with the same
    /// data folder as this one, so the current project stays open and untouched.
    /// </summary>
    private static void OpenInNewWindow(string folder)
    {
        if (Environment.ProcessPath is not { } exe)
        {
            return;
        }

        var start = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false };
        var args = Environment.GetCommandLineArgs();
        var dataDir = Array.IndexOf(args, "--user-data-dir");
        if (dataDir >= 0 && dataDir + 1 < args.Length)
        {
            start.ArgumentList.Add("--user-data-dir");
            start.ArgumentList.Add(args[dataDir + 1]);
        }

        start.ArgumentList.Add(folder);
        System.Diagnostics.Process.Start(start)?.Dispose();
    }

    private Control Row(Entry entry)
    {
        var isAction = entry.Folder is null;

        // Other projects offer both ways to open on hover (or when the keyboard is on them), in place of the path.
        Control trailing = new TextBlock
        {
            Text = entry.Detail,
            FontSize = Typography.SmallSize,
            Foreground = Palette.TextSecondary,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.LeadingCharacterEllipsis,
            MaxWidth = 170,
            Margin = new Thickness(Spacing.S, 0, 0, 0),
        };
        if (entry.Folder is { } folder && !entry.IsCurrent)
        {
            trailing.Classes.Add("row-detail"); // gives way to the actions below
            var here = new FlatButton(Icons.OpenHere, "Open in This Window (Enter)", iconSize: 14)
            {
                Command = new RelayCommand(() => _ = ChooseAsync(entry)),
            };
            var newWindow = new FlatButton(Icons.NewWindow, "Open in New Window (Ctrl+Enter)", iconSize: 14)
            {
                Command = new RelayCommand(() =>
                {
                    _popup.IsOpen = false;
                    OpenInNewWindow(folder);
                }),
            };
            trailing = new Panel
            {
                Margin = new Thickness(Spacing.S, 0, 0, 0),
                Children =
                {
                    trailing,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = Spacing.XXS, Classes = { "row-actions" }, Children = { here, newWindow } },
                },
            };
            ((TextBlock)((Panel)trailing).Children[0]).Margin = default;
        }

        return new DockPanel
        {
            Margin = new Thickness(Spacing.M, 0, Spacing.XS, 0),
            MinHeight = 30,
            Background = Brushes.Transparent,
            Children =
            {
                new Icon(isAction ? Icons.FolderOpen : Icons.Check, 14)
                {
                    Foreground = isAction ? Palette.TextSecondary : Palette.Accent,
                    Opacity = isAction || entry.IsCurrent ? 1 : 0, // the check marks the current project
                    Margin = new Thickness(0, 0, Spacing.S, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                }.Dock(Dock.Left),
                trailing.Dock(Dock.Right),
                new TextBlock
                {
                    Text = entry.Name,
                    FontWeight = entry.IsCurrent ? Typography.Strong : FontWeight.Normal,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
            },
        };
    }

    /// <summary>A recent project (or, with no folder, the "Open Folder…" action).</summary>
    private sealed record Entry(string? Folder, string Name, string Detail, bool IsCurrent);
}
