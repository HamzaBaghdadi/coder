using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Controls.Primitives;
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
/// The project name in the title bar, and the dropdown it opens (like a select, as in Zed): recent
/// projects, the current one checked, then "Open Folder…". Arrow keys move, Enter opens, Esc or a
/// click elsewhere closes. Ctrl+R opens it too.
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
            Height = 26,
            Padding = new Thickness(Spacing.S, 0),
            Command = new RelayCommand(Toggle),
            Content = new TextBlock { FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center }
                .Bind(TextBlock.TextProperty, workbench, x => x.ProjectName),
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
            if (e.Source is Visual { DataContext: Entry entry })
            {
                await ChooseAsync(entry);
            }
        };
        _list.OnRowKeyDown(async (_, e) =>
        {
            if (e.Key == Key.Enter && _list.SelectedItem is Entry entry)
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
                Chamfer = Spacing.ChamferSmall,
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

    private static Control Row(Entry entry)
    {
        var isAction = entry.Folder is null;
        return new DockPanel
        {
            Margin = new Thickness(Spacing.M, 0),
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
                new TextBlock
                {
                    Text = entry.Detail,
                    FontSize = Typography.SmallSize,
                    Foreground = Palette.TextMuted,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.LeadingCharacterEllipsis,
                    MaxWidth = 170,
                    Margin = new Thickness(Spacing.S, 0, 0, 0),
                }.Dock(Dock.Right),
                new TextBlock
                {
                    Text = entry.Name,
                    FontWeight = entry.IsCurrent ? FontWeight.SemiBold : FontWeight.Normal,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
            },
        };
    }

    /// <summary>A recent project (or, with no folder, the "Open Folder…" action).</summary>
    private sealed record Entry(string? Folder, string Name, string Detail, bool IsCurrent);
}
