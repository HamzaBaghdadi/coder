using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
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
    internal const double IndentPerLevel = 12;
    private const double DragThreshold = 6;

    private readonly WorkbenchViewModel _workbench;
    private readonly ExplorerViewModel _explorer;
    private readonly ListBox _list;
    private (ExplorerItemViewModel Item, Point Start, IPointer Pointer)? _pressed;
    private ExplorerItemViewModel? _dragged;
    private bool _justDragged;
    private readonly Canvas _dragLayer = new() { IsHitTestVisible = false };
    private readonly ChamferBorder _dragLabel;
    private readonly TextBlock _dragText = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = Typography.SmallSize, MaxWidth = 320, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Icon _dragIcon = new(Icons.FolderOpen, 14) { Margin = new Thickness(0, 0, 6, 0) };
    private readonly PathDrag _pathDrag = new();
    private readonly ExplorerStickyRows _sticky;

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
        {
            AutomationProperties.SetName(e.Container, e.Container.DataContext is ExplorerItemViewModel row ? row.Name : string.Empty);
            MarkDrop(e.Container, e.Index); // a row scrolled into view during a drag lights up too
        };

        // The label that follows the pointer while dragging.
        _dragLabel = new ChamferBorder
        {
            IsVisible = false,
            Background = Palette.Elevated,
            BorderBrush = Palette.BorderStrong,
            BorderThickness = 1,
            Radius = Spacing.RadiusMedium.TopLeft,
            Padding = new Thickness(Spacing.S, 4),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { _dragIcon, _dragText } },
        };
        _dragLayer.Children.Add(_dragLabel);

        // Sticky scroll: the folders the top row is in stay pinned over the tree. Right-click and drops work on them too.
        _sticky = new ExplorerStickyRows(_list, _explorer);
        _sticky.ContextRequested += OnContextRequested;
        DragDrop.SetAllowDrop(_sticky, true);
        _sticky.AddHandler(DragDrop.DragEnterEvent, OnExternalDragOver);
        _sticky.AddHandler(DragDrop.DragOverEvent, OnExternalDragOver);
        _sticky.AddHandler(DragDrop.DragLeaveEvent, OnExternalDragLeave);
        _sticky.AddHandler(DragDrop.DropEvent, OnExternalDrop);

        // Files from outside Coder (File Explorer, a browser) are copied in where they are dropped.
        DragDrop.SetAllowDrop(_list, true);
        _list.AddHandler(DragDrop.DragEnterEvent, OnExternalDragOver);
        _list.AddHandler(DragDrop.DragOverEvent, OnExternalDragOver);
        _list.AddHandler(DragDrop.DragLeaveEvent, OnExternalDragLeave);
        _list.AddHandler(DragDrop.DropEvent, OnExternalDrop);

        // Esc, or losing the pointer to another window, cancels a drag.
        _list.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape && _dragged is not null)
            {
                CancelDrag();
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
        _list.PointerCaptureLost += (_, _) => CancelDrag();

        // A second click soon after the first arrives as a double-tap, not a tap: both toggle, so a
        // folder can be opened and closed again as fast as the user clicks.
        EventHandler<TappedEventArgs> activate = (_, e) =>
        {
            if (_justDragged)
            {
                return; // the release that ended a drag is not a click (nor a double-click); the next press clears it
            }

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
        _explorer.ScrollRequested += (_, item) => ScrollToRow(item);
        _list.SizeChanged += (_, e) =>
        {
            if (e.PreviousSize.Height <= 0 && e.NewSize.Height > 0 && _explorer.SelectedItem is { } selected)
            {
                ScrollToRow(selected, retry: false); // revealed while the side bar was hidden: shown now
            }
        };

        Children.Add(Header().Dock(Dock.Top));
        Children.Add(RootHeader().Dock(Dock.Top));
        Children.Add(new Panel { Children = { EmptyState(workbench), _list, _sticky, _dragLayer } });
    }

    /// <summary>
    /// Brings a row into view at once, near the middle when it was off screen (like VS Code). Rows all
    /// have the same height, so the offset is computed rather than found by scrolling through the list,
    /// which in a folder of thousands of rows (node_modules) both froze the window and looked like an animation.
    /// </summary>
    private void ScrollToRow(ExplorerItemViewModel item, bool retry = true)
    {
        var index = _explorer.Rows.IndexOf(item);
        if (index < 0 || _list.Scroll is not ScrollViewer scroller)
        {
            return;
        }

        _list.UpdateLayout(); // the rows just added count in the scroll range (only visible rows are built)
        var top = index * Spacing.ListRowHeight;
        var viewport = scroller.Viewport.Height;
        if (viewport <= 0)
        {
            if (retry && IsEffectivelyVisible)
            {
                Dispatcher.UIThread.Post(() => ScrollToRow(item, retry: false), DispatcherPriority.Loaded); // once, after the first layout
            }

            return;
        }

        var offset = scroller.Offset.Y;
        if (top < offset + _sticky.CoveredHeight || top + Spacing.ListRowHeight > offset + viewport) // under the pinned folders counts as hidden
        {
            var max = Math.Max(0, (_explorer.Rows.Count * Spacing.ListRowHeight) - viewport);
            scroller.Offset = new Vector(scroller.Offset.X, Math.Clamp(top - ((viewport - Spacing.ListRowHeight) / 2), 0, max));
        }
    }

    private Control Header() => SideBarHeader.Create(
        "Explorer",
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
        Padding = new Thickness(Spacing.M + Spacing.XXS, 0),
        Child = new TextBlock { Classes = { "section-title" }, VerticalAlignment = VerticalAlignment.Center }
            .Bind(TextBlock.TextProperty, _explorer, x => x.RootName),
    }.Bind(IsVisibleProperty, _explorer, x => x.HasFolder);

    private Control EmptyState(WorkbenchViewModel workbench) => new StackPanel
    {
        Margin = new Thickness(Spacing.M + Spacing.XXS, Spacing.S),
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
        var letter = new TextBlock { FontSize = Typography.SmallSize, FontWeight = Typography.Strong, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) }
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
        var item = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as ExplorerItemViewModel
            ?? (ReferenceEquals(sender, _sticky) ? (e.Source as Visual)?.DataContext as ExplorerItemViewModel : null); // a pinned folder
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
    //
    // Inside the tree: press a row and move a few pixels. A label follows the pointer saying what will
    // happen ("Move app.ts into src"); the folder it would land in lights up with everything shown under
    // it. Near the top or bottom edge the tree scrolls; resting on a closed folder opens it. Esc cancels.
    // From outside Coder (File Explorer, a browser): files, folders and images are copied in.

    private const double EdgeZone = 28;
    private static readonly TimeSpan ExpandDelay = TimeSpan.FromMilliseconds(650);

    private (int Start, int End)? _dropRange;
    private bool _dropOnRoot;
    private Point _dragPosition;
    private DispatcherTimer? _edgeScroll;
    private DispatcherTimer? _hoverExpand;
    private ExplorerItemViewModel? _hoverFolder;

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _justDragged = false;
        var point = e.GetCurrentPoint(_list);
        _pressed = point.Properties.IsLeftButtonPressed && RowAt(e.Source) is { DataContext: ExplorerItemViewModel { IsEditing: false, IsPending: false } item }
            ? (item, point.Position, e.Pointer)
            : null;
    }

    /// <summary>Past a few pixels, a press becomes a drag.</summary>
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
            _list.ContainerFromItem(_dragged)?.Classes.Add("dragging"); // the row being carried fades
        }

        e.Handled = true;

        // Over Coder Agent's chat: the file is attached there instead of moved.
        if (_pathDrag.Move(_list, e) is not null)
        {
            ShowDrop(null, root: false);
            ShowLabel($"Attach {_dragged.Name} to the chat", ok: true, e.GetPosition(_dragLayer));
            _list.Cursor = new Cursor(StandardCursorType.DragCopy);
            StopEdgeScroll();
            return;
        }

        _dragPosition = position;
        UpdateDrag();
    }

    /// <summary>Lights up the drop folder and words the label for the pointer's position (also as the tree scrolls under it).</summary>
    private void UpdateDrag()
    {
        if (_dragged is not { } dragged)
        {
            return;
        }

        var inside = IsInsideList(_dragPosition);
        var target = inside ? ItemAt(_dragPosition) : null;
        var ok = inside && _explorer.CanMove(dragged, target);
        _list.Cursor = new Cursor(ok ? StandardCursorType.DragMove : StandardCursorType.No);
        var folder = ok ? _explorer.DropFolderRow(target) : null;
        ShowDrop(folder, root: ok && folder is null);
        ShowLabel(inside ? _explorer.DescribeMove(dragged, target) : $"Drop {dragged.Name} on a folder", ok, _list.TranslatePoint(_dragPosition, _dragLayer) ?? default);
        FollowEdges();
        FollowHover(inside ? target : null);
    }

    private bool IsInsideList(Point position) =>
        position.Y >= 0 && position.Y <= _list.Bounds.Height && position.X >= 0 && position.X <= _list.Bounds.Width;

    private async void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var dragged = _dragged;
        _pressed = null;
        if (dragged is null)
        {
            return;
        }

        e.Handled = true;
        var position = e.GetPosition(_list);
        var inside = IsInsideList(position);
        var target = inside ? ItemAt(position) : null;
        EndDrag(e.Pointer);
        if (_pathDrag.Drop(_list, e, [dragged.Path]))
        {
            return;
        }

        if (inside)
        {
            await _explorer.MoveAsync(dragged, target); // over empty space: the top of the tree
        }
    }

    /// <summary>Esc, or the pointer taken away (another window): the drag ends with nothing moved.</summary>
    private void CancelDrag()
    {
        if (_dragged is not null)
        {
            EndDrag(null);
            _pathDrag.Cancel();
        }
    }

    private void EndDrag(IPointer? pointer)
    {
        if (_dragged is not null)
        {
            _list.ContainerFromItem(_dragged)?.Classes.Remove("dragging");
        }

        _justDragged = true; // the release ending (or after cancelling) a drag must not also open the row
        _dragged = null;
        _pressed = null;
        pointer?.Capture(null);
        _list.Cursor = null;
        ShowDrop(null, root: false);
        _dragLabel.IsVisible = false;
        StopEdgeScroll();
        FollowHover(null);
    }

    /// <summary>The label beside the pointer: what a drop does here, or (muted) why it can't.</summary>
    private void ShowLabel(string text, bool ok, Point at)
    {
        _dragText.Text = text;
        _dragText.Foreground = ok ? Palette.TextPrimary : Palette.TextMuted;
        _dragIcon.Foreground = ok ? Palette.Accent : Palette.TextMuted;
        _dragIcon.Data = ok ? Icons.FolderOpen : Icons.Close;
        _dragLabel.IsVisible = true;
        _dragLabel.Measure(Size.Infinity);
        var x = Math.Clamp(at.X + 14, 0, Math.Max(0, _dragLayer.Bounds.Width - _dragLabel.DesiredSize.Width - 4));
        var y = Math.Clamp(at.Y + 16, 0, Math.Max(0, _dragLayer.Bounds.Height - _dragLabel.DesiredSize.Height - 4));
        Canvas.SetLeft(_dragLabel, x);
        Canvas.SetTop(_dragLabel, y);
    }

    /// <summary>
    /// Highlights where a drop lands: the folder's row (outlined) and every row shown under it (tinted),
    /// or the whole tree for its top. Rows scrolled into view later pick it up when they are prepared.
    /// </summary>
    private void ShowDrop(ExplorerItemViewModel? folder, bool root)
    {
        (int Start, int End)? range = null;
        if (folder is not null && _explorer.Rows.IndexOf(folder) is var start and >= 0)
        {
            var end = start + 1;
            while (end < _explorer.Rows.Count && _explorer.Rows[end].Depth > folder.Depth)
            {
                end++;
            }

            range = (start, end);
        }

        if (range == _dropRange && root == _dropOnRoot)
        {
            return;
        }

        _dropRange = range;
        _dropOnRoot = root;
        _sticky.MarkDrop(folder);
        _list.Background = root ? Palette.Hover : Brushes.Transparent; // the top of the tree: all of it
        foreach (var container in _list.GetRealizedContainers())
        {
            MarkDrop(container, _list.IndexFromContainer(container));
        }
    }

    private void MarkDrop(Control container, int index)
    {
        var range = _dropRange;
        container.Classes.Set("drop-target", range is { } r && index == r.Start);
        container.Classes.Set("drop-inside", range is { } s && index > s.Start && index < s.End);
        container.Classes.Set("dragging", _dragged is not null && ReferenceEquals(container.DataContext, _dragged));
    }

    /// <summary>How fast to scroll for the pointer's distance into the top or bottom edge zone (0: not in one).</summary>
    private double EdgeSpeed()
    {
        var height = _list.Bounds.Height;
        var into = _dragPosition.Y < EdgeZone ? -(EdgeZone - _dragPosition.Y)
            : _dragPosition.Y > height - EdgeZone ? _dragPosition.Y - (height - EdgeZone)
            : 0;
        return Math.Clamp(into, -EdgeZone * 2, EdgeZone * 2) / 2;
    }

    /// <summary>Scrolls while the pointer rests near the top or bottom edge, faster the closer it is.</summary>
    private void FollowEdges()
    {
        if (EdgeSpeed() == 0 || _dragged is null)
        {
            StopEdgeScroll();
            return;
        }

        _edgeScroll ??= new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Input, (_, _) =>
        {
            var step = EdgeSpeed();
            if (step == 0 || _dragged is null || _list.Scroll is not ScrollViewer scroller)
            {
                StopEdgeScroll();
                return;
            }

            scroller.Offset = new Vector(scroller.Offset.X, Math.Max(0, scroller.Offset.Y + step));
            UpdateDrag(); // the row under the pointer changed
        });
        _edgeScroll.Start();
    }

    private void StopEdgeScroll() => _edgeScroll?.Stop();

    /// <summary>Resting on a closed folder for a moment opens it, so a drop can go deeper.</summary>
    private void FollowHover(ExplorerItemViewModel? target)
    {
        var folder = target is { IsDirectory: true, IsExpanded: false } && _dragged is not null && !ReferenceEquals(target, _dragged) ? target : null;
        if (ReferenceEquals(folder, _hoverFolder))
        {
            return;
        }

        _hoverFolder = folder;
        _hoverExpand?.Stop();
        if (folder is null)
        {
            return;
        }

        _hoverExpand = new DispatcherTimer(ExpandDelay, DispatcherPriority.Input, (_, _) =>
        {
            _hoverExpand?.Stop();
            if (_dragged is not null && ReferenceEquals(_hoverFolder, folder))
            {
                _explorer.SetExpanded(folder, true);
                _hoverFolder = null;
                UpdateDrag();
            }
        });
        _hoverExpand.Start();
    }

    // From outside Coder --------------------------------------------------------------------------

    private void OnExternalDragOver(object? sender, DragEventArgs e)
    {
        var data = e.DataTransfer;
        var accepts = _explorer.HasFolder && (data.Contains(DataFormat.File) || data.Contains(DataFormat.Bitmap));
        e.DragEffects = accepts ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        if (!accepts)
        {
            OnExternalDragLeave(sender, e);
            return;
        }

        var target = ItemAt(e.GetPosition(_list));
        var folder = _explorer.DropFolderRow(target);
        ShowDrop(folder, root: folder is null);
        var files = data.TryGetFiles();
        var what = files is { Length: > 1 } ? $"{files.Length} items" : files is { Length: 1 } ? files[0].Name : "the image";
        ShowLabel($"Copy {what} into {_explorer.FolderName(_explorer.DropFolder(target))}", ok: true, e.GetPosition(_dragLayer));
    }

    private void OnExternalDragLeave(object? sender, DragEventArgs e)
    {
        ShowDrop(null, root: false);
        _dragLabel.IsVisible = false;
    }

    private async void OnExternalDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        OnExternalDragLeave(sender, e);
        var target = ItemAt(e.GetPosition(_list));
        var data = e.DataTransfer;
        if (data.TryGetFiles() is { Length: > 0 } files)
        {
            await DropFilesAsync(files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList(), target);
        }
        else if (data.TryGetBitmap() is { } bitmap)
        {
            // An image with no file behind it (dragged from a web page): saved as image.png.
            var folder = Directory.CreateTempSubdirectory("coder-drop-").FullName;
            try
            {
                var file = Path.Combine(folder, "image.png");
                using (bitmap)
                {
                    bitmap.Save(file);
                }

                await DropFilesAsync([file], target);
            }
            finally
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    /// <summary>Files from outside Coder dropped on a row (null: the top of the tree): copied in. Internal for tests.</summary>
    internal Task<IReadOnlyList<string>> DropFilesAsync(IReadOnlyList<string> paths, ExplorerItemViewModel? target) => _explorer.CopyInAsync(paths, target);

    private static ListBoxItem? RowAt(object? source) => (source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true);

    /// <summary>
    /// The row at a point of the list (null: the empty space below the rows). Found from where the rows on
    /// screen are, not by hit testing, which finds nothing while the pointer is captured for the drag.
    /// </summary>
    private ExplorerItemViewModel? ItemAt(Point position)
    {
        if (_sticky.ItemAt(position) is { } pinned)
        {
            return pinned; // over a pinned folder: that folder, not the row scrolled under it
        }

        foreach (var container in _list.GetRealizedContainers())
        {
            if (container.IsVisible && container.TranslatePoint(default, _list) is { } top && position.Y >= top.Y && position.Y < top.Y + container.Bounds.Height)
            {
                return container.DataContext as ExplorerItemViewModel;
            }
        }

        return null;
    }

    /// <summary>For tests: drags a row onto another (null: the empty space below the tree).</summary>
    internal Task DropAsync(ExplorerItemViewModel item, ExplorerItemViewModel? target) => _explorer.MoveAsync(item, target);

    /// <summary>For tests: the drag label's text while it shows, else null.</summary>
    internal string? DragLabel => _dragLabel.IsVisible ? _dragText.Text : null;

    /// <summary>For tests: the rows a drop would land in (the folder's first), and whether it is the top of the tree.</summary>
    internal ((int Start, int End)? Range, bool Root) DropHighlight => (_dropRange, _dropOnRoot);

    /// <summary>For tests: the sticky-scroll rows pinned over the tree.</summary>
    internal ExplorerStickyRows StickyRows => _sticky;

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
