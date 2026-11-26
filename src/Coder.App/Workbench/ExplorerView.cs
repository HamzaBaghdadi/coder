using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Coder.App.Controls;
using Coder.App.Mvvm;
using Coder.App.Theme;
using Coder.Core.Git;
using Coder.Presentation.Commands;
using Coder.Presentation.Explorer;
using Coder.Presentation.Mvvm;
using Coder.Presentation.Workbench;

namespace Coder.App.Workbench;

/// <summary>
/// The side bar's file explorer, like VS Code's: a header with New File, New Folder, Refresh and
/// Collapse All, and a virtualized, keyboard-navigable tree. Right-click for the file actions; F2 renames,
/// Delete moves to the Recycle Bin, and rows can be dragged onto folders to move them. New names are
/// typed in place. With no folder open it shows a clear empty state with the next step to take.
/// </summary>
public sealed class ExplorerView : DockPanel
{
    private const double IndentPerLevel = 12;
    private const double DragThreshold = 6;

    private readonly WorkbenchViewModel _workbench;
    private readonly ExplorerViewModel _explorer;
    private readonly ListBox _list;
    private (ExplorerItemViewModel Item, Point Start, IPointer Pointer)? _pressed;
    private ExplorerItemViewModel? _dragged;
    private Control? _dropTarget;
    private readonly PathDrag _pathDrag = new();

    public ExplorerView(WorkbenchViewModel workbench)
    {
        _workbench = workbench;
        _explorer = workbench.Explorer;
        Background = Palette.SideBar;

        _list = new ListBox
        {
            ItemsSource = _explorer.Rows,
            ItemContainerTheme = AppStyles.ListRowTheme,
            ItemTemplate = Templates.Row<ExplorerItemViewModel>(CreateRow),
            Background = Brushes.Transparent,
        }.Bind(ListBox.SelectedItemProperty, _explorer, x => x.SelectedItem, Avalonia.Data.BindingMode.TwoWay)
         .Bind(IsVisibleProperty, _explorer, x => x.HasFolder);
        AutomationProperties.SetName(_list, "Files");
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        _list.OnRowKeyDown(OnListKeyDown);
        _list.ContainerPrepared += (_, e) =>
            AutomationProperties.SetName(e.Container, e.Container.DataContext is ExplorerItemViewModel row ? row.Name : string.Empty);

        // A second click soon after the first arrives as a double-tap, not a tap: both toggle, so a
        // folder can be opened and closed again as fast as the user clicks.
        EventHandler<TappedEventArgs> activate = (_, e) =>
        {
            if (e.Source is Visual { DataContext: ExplorerItemViewModel item } && _dragged is null)
            {
                _explorer.Activate(item);
            }
        };
        _list.Tapped += activate;
        _list.DoubleTapped += activate;
        _list.ContextRequested += OnContextRequested;
        _list.AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        _list.AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        _list.AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        _explorer.ScrollRequested += (_, item) => Dispatcher.UIThread.Post(() => _list.ScrollIntoView(item), DispatcherPriority.Loaded);

        Children.Add(Header().Dock(Dock.Top));
        Children.Add(RootHeader().Dock(Dock.Top));
        Children.Add(EmptyState(workbench));
        Children.Add(_list);
    }

    private Control Header() => SideBarHeader.Create(
        "EXPLORER",
        Action(Icons.NewFile, "New File…", () => _explorer.StartNew(directory: false)),
        Action(Icons.NewFolder, "New Folder…", () => _explorer.StartNew(directory: true)),
        Action(Icons.Refresh, "Refresh Explorer", _explorer.Refresh),
        Action(Icons.CollapseAll, "Collapse Folders in Explorer", _explorer.CollapseAll));

    private FlatButton Action(Geometry icon, string name, Action action)
    {
        var button = new FlatButton(icon, name) { Command = new RelayCommand(action) };
        button.Bind(IsEnabledProperty, _explorer, x => x.HasFolder);
        return button;
    }

    private Control RootHeader() => new Border
    {
        Height = Spacing.ListRowHeight,
        Padding = new Thickness(Spacing.L, 0),
        Child = new TextBlock { FontSize = Typography.CaptionSize, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center }
            .Bind(TextBlock.TextProperty, _explorer, x => x.RootName),
    }.Bind(IsVisibleProperty, _explorer, x => x.HasFolder);

    private Control EmptyState(WorkbenchViewModel workbench) => new StackPanel
    {
        Margin = new Thickness(Spacing.L, Spacing.S),
        Spacing = Spacing.M,
        Children =
        {
            new TextBlock
            {
                Text = "You have not opened a folder yet. Open one to browse and edit its files.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Palette.TextSecondary,
                LineHeight = 20,
            },
            new FlatButton
            {
                Classes = { "primary" },
                Content = "Open Folder",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Command = workbench.Commands.ToCommand(CommandIds.OpenFolder),
            },
        },
    }.Bind(IsVisibleProperty, _explorer, x => x.HasFolder, hasFolder => !hasFolder);

    private Control CreateRow(ExplorerItemViewModel item)
    {
        var chevron = Chevron.For(item, x => x.IsExpanded);
        chevron.Opacity = item.IsDirectory ? 1 : 0;

        // Material icons: by file name and type; folders switch to their open icon when expanded.
        var icon = item.IsDirectory
            ? new Image { Width = 16, Height = 16 }.Bind(Image.SourceProperty, item, x => x.IsExpanded, expanded => FileIcons.ForFolder(item.Path, expanded))
            : FileIcons.File(item.Path);

        // Git state: changed files take the change's color and letter; folders with changes inside get a dot.
        var name = new TextBlock { Text = item.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis }
            .Bind(TextBlock.ForegroundProperty, item, x => x.GitChange, GitColors.For);
        var letter = new TextBlock { FontSize = Typography.SmallSize, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) }
            .Bind(TextBlock.TextProperty, item, x => x.GitChange, kind => kind is { } k ? new GitChange(item.Path, item.Name, k).Letter : string.Empty)
            .Bind(TextBlock.ForegroundProperty, item, x => x.GitChange, GitColors.For);
        var dot = new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), Background = Palette.GitModified, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 2, 0) }
            .Bind(IsVisibleProperty, item, x => x.ContainsGitChanges);

        // The name, or a box to type it in (new file or folder, rename). The box is made only when needed.
        var nameSlot = new Panel { Children = { name } };
        void FollowEditing()
        {
            if (item.IsEditing && nameSlot.Children.Count == 1)
            {
                var box = NameBox(item);
                nameSlot.Children.Add(box);
                name.IsVisible = false;
                Dispatcher.UIThread.Post(() =>
                {
                    box.Focus();
                    var dot = item.IsDirectory ? -1 : item.Name.LastIndexOf('.');
                    box.SelectionStart = 0;
                    box.SelectionEnd = dot > 0 ? dot : item.Name.Length; // the name, not its extension
                }, DispatcherPriority.Loaded);
            }
            else if (!item.IsEditing && nameSlot.Children.Count == 2)
            {
                nameSlot.Children.RemoveAt(1);
                name.IsVisible = true;
            }
        }

        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ExplorerItemViewModel.IsEditing))
            {
                FollowEditing();
            }
        };
        FollowEditing();

        return new DockPanel
        {
            Margin = new Thickness(Spacing.S + item.Depth * IndentPerLevel, 0, Spacing.S, 0),
            Background = Brushes.Transparent,
            Children =
            {
                letter.Dock(Dock.Right),
                dot.Dock(Dock.Right),
                chevron.Dock(Dock.Left),
                new Border { Width = 6 }.Dock(Dock.Left),
                icon.Dock(Dock.Left),
                new Border { Width = 6 }.Dock(Dock.Left),
                nameSlot,
            },
        };
    }

    /// <summary>The in-place name box: Enter keeps the name, Esc cancels, leaving it keeps it too (like VS Code).</summary>
    private TextBox NameBox(ExplorerItemViewModel item)
    {
        var box = new TextBox
        {
            Text = item.Name,
            Padding = new Thickness(4, 0),
            MinHeight = 0,
            Height = Spacing.ListRowHeight - 4,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(box, item.IsPending ? (item.IsDirectory ? "New folder name" : "New file name") : "New name");
        var done = false;
        void Commit()
        {
            if (!done && item.IsEditing)
            {
                done = _explorer.CommitEdit(item, box.Text ?? string.Empty);
            }
        }

        box.AddHandler(KeyDownEvent, (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Enter:
                    Commit();
                    e.Handled = true;
                    break;
                case Key.Escape:
                    done = true;
                    _explorer.CancelEditing();
                    _list.Focus();
                    e.Handled = true;
                    break;
            }
        }, RoutingStrategies.Tunnel);
        box.LostFocus += (_, _) =>
        {
            Commit();
            if (!done)
            {
                _explorer.CancelEditing(); // a name that couldn't be used: give up rather than trap the focus
            }
        };
        return box;
    }

    // Context menu --------------------------------------------------------------------------------

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        var item = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as ExplorerItemViewModel;
        if (!_explorer.HasFolder || item is { IsPending: true })
        {
            return;
        }

        if (item is not null)
        {
            _explorer.SelectedItem = item;
        }

        var menu = new ContextMenu { ItemsSource = MenuItems(item) };
        menu.Open(_list);
        e.Handled = true;
    }

    /// <summary>VS Code's explorer menu: create, reveal, open a terminal there, copy paths, rename, delete.</summary>
    private List<Control> MenuItems(ExplorerItemViewModel? item)
    {
        var path = item?.Path ?? _explorer.RootPath!;
        var folder = item is null ? path : item.IsDirectory ? item.Path : Path.GetDirectoryName(item.Path)!;
        var items = new List<Control>
        {
            Item("New File…", null, () => _explorer.StartNew(directory: false, item)),
            Item("New Folder…", null, () => _explorer.StartNew(directory: true, item)),
            new Separator(),
            Item("Reveal in File Explorer", "Shift+Alt+R", () => RevealInSystem(path)),
            Item("Open in Integrated Terminal", null, () => _workbench.OpenTerminalAt(folder)),
            new Separator(),
            Item("Copy Path", "Shift+Alt+C", () => _ = CopyAsync(path)),
            Item("Copy Relative Path", null, () => _ = CopyAsync(Path.GetRelativePath(_explorer.RootPath!, path).Replace('\\', '/'))),
        };

        if (item is not null && _workbench.Agent is { } agent)
        {
            items.Add(new Separator());
            items.Add(Item("Add to Coder Agent Chat", null, () =>
            {
                _workbench.ShowAgent();
                _ = agent.AddPathsAsync([item.Path]);
            }));
        }

        if (item is not null)
        {
            items.Add(new Separator());
            items.Add(Item("Rename…", "F2", () => _explorer.StartRename(item)));
            items.Add(Item("Delete", "Delete", () => _ = _explorer.DeleteAsync(item)));
        }
        else
        {
            items.Add(new Separator());
            items.Add(Item("Refresh", null, _explorer.Refresh));
            items.Add(Item("Collapse Folders", null, _explorer.CollapseAll));
        }

        return items;
    }

    private static MenuItem Item(string header, string? keys, Action action)
    {
        var item = new MenuItem { Header = header, InputGesture = keys is null ? null : KeyGesture.Parse(keys) };
        item.Click += (_, _) => action();
        AutomationProperties.SetName(item, header);
        return item;
    }

    private async Task CopyAsync(string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    /// <summary>Opens the system file manager with the file selected.</summary>
    private static void RevealInSystem(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        else
        {
            Process.Start(new ProcessStartInfo(Directory.Exists(path) ? path : Path.GetDirectoryName(path)!) { UseShellExecute = true });
        }
    }

    // Drag and drop -------------------------------------------------------------------------------

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(_list);
        _pressed = point.Properties.IsLeftButtonPressed && RowAt(e.Source) is { DataContext: ExplorerItemViewModel { IsEditing: false } item }
            ? (item, point.Position, e.Pointer)
            : null;
    }

    /// <summary>Past a few pixels, a press becomes a drag: the folder under the pointer lights up as the target.</summary>
    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressed is not { } pressed)
        {
            return;
        }

        var position = e.GetPosition(_list);
        if (_dragged is null)
        {
            if (Math.Abs(position.Y - pressed.Start.Y) < DragThreshold && Math.Abs(position.X - pressed.Start.X) < DragThreshold)
            {
                return;
            }

            _dragged = pressed.Item;
            pressed.Pointer.Capture(_list);
            _list.Cursor = new Cursor(StandardCursorType.DragMove);
        }

        // Over Coder Agent's chat: the file is attached there instead of moved.
        if (_pathDrag.Move(_list, e) is not null)
        {
            SetDropTarget(null);
            _list.Cursor = new Cursor(StandardCursorType.DragCopy);
            e.Handled = true;
            return;
        }

        _list.Cursor = new Cursor(StandardCursorType.DragMove);
        var target = RowAt(_list.InputHitTest(position) as Visual);
        var targetItem = target?.DataContext as ExplorerItemViewModel;
        var highlight = targetItem is { IsDirectory: false } ? ParentRow(targetItem) : target; // over a file: its folder
        SetDropTarget(_explorer.CanMove(_dragged, targetItem) ? highlight : null);
        e.Handled = true;
    }

    private async void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var dragged = _dragged;
        _pressed = null;
        if (dragged is null)
        {
            return;
        }

        e.Pointer.Capture(null);
        _list.Cursor = null;
        SetDropTarget(null);
        e.Handled = true;
        if (_pathDrag.Drop(_list, e, [dragged.Path]))
        {
            _dragged = null;
            return;
        }

        var target = RowAt(_list.InputHitTest(e.GetPosition(_list)) as Visual)?.DataContext as ExplorerItemViewModel;
        try
        {
            await _explorer.MoveAsync(dragged, target); // over empty space: the top of the tree
        }
        finally
        {
            _dragged = null;
        }
    }

    private void SetDropTarget(Control? row)
    {
        if (ReferenceEquals(row, _dropTarget))
        {
            return;
        }

        _dropTarget?.Classes.Remove("drop-target");
        _dropTarget = row;
        _dropTarget?.Classes.Add("drop-target");
    }

    private ListBoxItem? ParentRow(ExplorerItemViewModel item)
    {
        var index = _explorer.Rows.IndexOf(item);
        for (var i = index - 1; i >= 0; i--)
        {
            if (_explorer.Rows[i].Depth < item.Depth)
            {
                return _list.ContainerFromItem(_explorer.Rows[i]) as ListBoxItem;
            }
        }

        return null;
    }

    private static ListBoxItem? RowAt(object? source) => (source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true);

    /// <summary>For tests: drags a row onto another (null: the empty space below the tree).</summary>
    internal Task DropAsync(ExplorerItemViewModel item, ExplorerItemViewModel? target) => _explorer.MoveAsync(item, target);

    // Keys ----------------------------------------------------------------------------------------

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (_explorer.SelectedItem is not { IsEditing: false } item)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter:
            case Key.Space:
                _explorer.Activate(item);
                break;
            case Key.Right when item.IsDirectory:
                _explorer.SetExpanded(item, true);
                break;
            case Key.Left when item.IsDirectory && item.IsExpanded:
                _explorer.SetExpanded(item, false);
                break;
            case Key.Left:
                _explorer.SelectParent(item);
                break;
            case Key.F2:
                _explorer.StartRename(item);
                break;
            case Key.Delete:
                _ = _explorer.DeleteAsync(item);
                break;
            default:
                return;
        }

        e.Handled = true;
    }
}
