using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Coder.App.Controls;
using Coder.App.Mvvm;
using Coder.App.Theme;
using Coder.Presentation.Explorer;

namespace Coder.App.Workbench;

/// <summary>
/// The explorer's sticky scroll, like VS Code's: while the tree scrolls, the folders the top row sits in
/// stay pinned above it, one row per nesting level (at most <see cref="MaxRows"/>). A pinned folder slides
/// up out of the way as the last of its rows scrolls past. Clicking one scrolls back to the folder and
/// selects it; the pinned rows never take keyboard focus, so the tree keeps it.
/// </summary>
internal sealed class ExplorerStickyRows : Border
{
    /// <summary>Deeper nesting than this pins only the outermost folders.</summary>
    public const int MaxRows = 5;

    private const double RowHeight = Spacing.ListRowHeight;
    private const double WheelStep = 50; // what a ScrollViewer scrolls for one notch

    private readonly ListBox _list;
    private readonly ExplorerViewModel _explorer;
    private readonly Canvas _canvas = new() { ClipToBounds = true };
    private readonly List<StickyRow> _shown = [];
    private bool _updatePosted;

    public ExplorerStickyRows(ListBox list, ExplorerViewModel explorer)
    {
        _list = list;
        _explorer = explorer;
        VerticalAlignment = VerticalAlignment.Top;
        HorizontalAlignment = HorizontalAlignment.Left;
        Background = Palette.SideBar;
        BorderBrush = Palette.Border;
        BorderThickness = new Thickness(0, 0, 0, 1);
        ThemeManager.BindShadow(this, "0 3 6 -3");
        IsVisible = false;
        Child = _canvas;
        AutomationProperties.SetName(this, "Pinned folders");

        // Wheeling over the pinned rows scrolls the tree under them.
        PointerWheelChanged += (_, e) =>
        {
            if (_list.Scroll is ScrollViewer scroller)
            {
                ScrollTo(scroller.Offset.Y - (e.Delta.Y * WheelStep));
                e.Handled = true;
            }
        };

        _list.AddHandler(ScrollViewer.ScrollChangedEvent, (_, _) => Update());
        _list.SizeChanged += (_, _) => Update();
        _explorer.Rows.CollectionChanged += (_, _) => PostUpdate();
        _list.SelectionChanged += (_, _) => Dispatcher.UIThread.Post(UncoverSelection, DispatcherPriority.Loaded);
    }

    /// <summary>For tests and hit testing: the folders pinned now, outermost first.</summary>
    internal IReadOnlyList<ExplorerItemViewModel> Items => _shown.Select(r => r.Item).ToList();

    /// <summary>The pinned folder at a point of the list (in the list's coordinates), else null.</summary>
    internal ExplorerItemViewModel? ItemAt(Point inList)
    {
        if (!IsVisible || this.TranslatePoint(default, _list) is not { } origin)
        {
            return null;
        }

        var y = inList.Y - origin.Y;
        if (inList.X < origin.X || inList.X > origin.X + Bounds.Width || y < 0 || y >= Bounds.Height)
        {
            return null;
        }

        // Earlier rows are drawn over deeper ones sliding under them.
        return _shown.FirstOrDefault(r => y >= Canvas.GetTop(r) && y < Canvas.GetTop(r) + RowHeight)?.Item;
    }

    /// <summary>For tests: clicks a pinned row, as the pointer would.</summary>
    internal void Click(ExplorerItemViewModel folder) => Reveal(folder);

    /// <summary>
    /// The folders to pin over a tree scrolled to <paramref name="offset"/>, outermost first, each with
    /// its top in the viewport (negative while it slides up). Slot k pins the depth-k ancestor of the
    /// row under it; it stops at the first slot whose row is itself at that depth (shown in place).
    /// </summary>
    internal static List<(ExplorerItemViewModel Item, double Top)> Compute(IReadOnlyList<ExplorerItemViewModel> rows, double offset, int max = MaxRows)
    {
        var pinned = new List<(ExplorerItemViewModel, double)>();
        var shift = 0.0;
        for (var k = 0; k < max; k++)
        {
            var under = (int)Math.Floor((offset + (k * RowHeight)) / RowHeight);
            if (under < 0 || under >= rows.Count || rows[under].Depth <= k)
            {
                break;
            }

            var folder = under - 1;
            while (folder >= 0 && rows[folder].Depth != k)
            {
                folder--;
            }

            if (folder < 0)
            {
                break;
            }

            // Pushed up by the end of its rows: the slot's bottom never goes past the folder's last row.
            var end = under + 1;
            while (end < rows.Count && rows[end].Depth > k)
            {
                end++;
            }

            shift = Math.Min(shift, (end * RowHeight) - offset - ((k + 1) * RowHeight));
            pinned.Add((rows[folder], (k * RowHeight) + shift));
        }

        return pinned;
    }

    private void PostUpdate()
    {
        if (!_updatePosted)
        {
            _updatePosted = true;
            Dispatcher.UIThread.Post(() =>
            {
                _updatePosted = false;
                Update();
            }, DispatcherPriority.Loaded);
        }
    }

    private void Update()
    {
        if (_list.Scroll is not ScrollViewer scroller || !_explorer.HasFolder || scroller.Viewport.Height <= 0)
        {
            Show([], 0, default);
            return;
        }

        var pinned = Compute(_explorer.Rows, scroller.Offset.Y);
        var origin = (scroller as Visual).TranslatePoint(default, _list) ?? default;
        Show(pinned, scroller.Viewport.Width, origin);
    }

    private void Show(List<(ExplorerItemViewModel Item, double Top)> pinned, double width, Point origin)
    {
        if (pinned.Count == 0)
        {
            IsVisible = false;
            _canvas.Children.Clear();
            _shown.Clear();
            return;
        }

        // Rows are rebuilt only when the pinned folders change; scrolling just moves them.
        if (!pinned.Select(p => p.Item).SequenceEqual(_shown.Select(r => r.Item)))
        {
            _canvas.Children.Clear();
            _shown.Clear();
            foreach (var (item, _) in pinned)
            {
                var row = new StickyRow(item, Reveal);
                row.MarkDrop(ReferenceEquals(item, _dropFolder));
                _shown.Add(row);
                _canvas.Children.Add(row);
            }
        }

        var height = 0.0;
        for (var k = 0; k < pinned.Count; k++)
        {
            var row = _shown[k];
            row.Width = width;
            row.ZIndex = MaxRows - k; // a deeper row slides under the one above it
            Canvas.SetTop(row, pinned[k].Top);
            height = Math.Max(height, pinned[k].Top + RowHeight);
        }

        Margin = new Thickness(origin.X, origin.Y, 0, 0);
        Width = width;
        _canvas.Height = Math.Max(0, height - BorderThickness.Bottom); // the bottom line sits on the last row, not over the next
        IsVisible = true;
    }

    /// <summary>How much of the list's top the pinned rows cover now (0 when none are pinned).</summary>
    internal double CoveredHeight => IsVisible ? Bounds.Height : 0;

    private ExplorerItemViewModel? _dropFolder;

    /// <summary>During a drag: the folder a drop lands in lights up on its pinned row too (it may be under one).</summary>
    internal void MarkDrop(ExplorerItemViewModel? folder)
    {
        _dropFolder = folder;
        foreach (var row in _shown)
        {
            row.MarkDrop(ReferenceEquals(row.Item, folder));
        }
    }

    private void ScrollTo(double y)
    {
        if (_list.Scroll is ScrollViewer scroller)
        {
            var max = Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height);
            scroller.Offset = new Vector(scroller.Offset.X, Math.Clamp(y, 0, max));
        }
    }

    /// <summary>Where a row sits just below the folders it would pin, so nothing covers it.</summary>
    private static double OffsetShowing(int index, ExplorerItemViewModel row) =>
        (index - Math.Min(row.Depth, MaxRows)) * RowHeight;

    /// <summary>A pinned row clicked: the tree scrolls back to the folder, which is selected and focused (VS Code).</summary>
    private void Reveal(ExplorerItemViewModel folder)
    {
        var index = _explorer.Rows.IndexOf(folder);
        if (index < 0)
        {
            return;
        }

        ScrollTo(OffsetShowing(index, folder));
        _explorer.SelectedItem = folder;
        Dispatcher.UIThread.Post(() =>
        {
            Update();
            (_list.ContainerFromItem(folder) ?? (Control)_list).Focus(NavigationMethod.Pointer);
        }, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// The keyboard moved the selection onto a row the pinned folders cover (going up, the list scrolls
    /// it only to the very top): scroll a little more so it shows below them.
    /// </summary>
    private void UncoverSelection()
    {
        if (_explorer.SelectedItem is not { } selected || _list.Scroll is not ScrollViewer scroller)
        {
            return;
        }

        var index = _explorer.Rows.IndexOf(selected);
        var top = (index * RowHeight) - scroller.Offset.Y;
        if (index < 0 || top + RowHeight <= 0 || top >= scroller.Viewport.Height)
        {
            return; // not on screen: nothing to uncover
        }

        var pinned = Compute(_explorer.Rows, scroller.Offset.Y);
        var covered = pinned.Count > 0 && top < pinned.Max(p => p.Top + RowHeight) && !pinned.Any(p => ReferenceEquals(p.Item, selected));
        if (covered)
        {
            ScrollTo(OffsetShowing(index, selected));
        }
    }

    /// <summary>One pinned folder: drawn like its row in the tree, on the side bar surface. Not focusable.</summary>
    private sealed class StickyRow : Border
    {
        private readonly Border _surface;
        private readonly Action<ExplorerItemViewModel> _invoke;

        public StickyRow(ExplorerItemViewModel item, Action<ExplorerItemViewModel> invoke)
        {
            Item = item;
            _invoke = invoke;
            DataContext = item;
            Height = RowHeight;
            Background = Palette.SideBar;
            Focusable = false;
            Cursor = new Cursor(StandardCursorType.Hand);
            AutomationProperties.SetName(this, item.Name);
            AutomationProperties.SetHelpText(this, $"Pinned folder {item.Path}. Click to scroll to it.");

            var chevron = Chevron.For(item, x => x.IsExpanded);
            var icon = new Image { Width = 16, Height = 16 }
                .Bind(Image.SourceProperty, item, x => x.IsExpanded, expanded => FileIcons.ForFolder(item.Path, expanded));
            var name = new TextBlock { Text = item.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis }
                .Bind(TextBlock.ForegroundProperty, item, x => x.GitChange, GitColors.For);
            var dot = new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), Background = Palette.GitModified, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 2, 0) }
                .Bind(IsVisibleProperty, item, x => x.ContainsGitChanges);

            // The same insets as the tree's rows (ListRowTheme's margin and corners, the row's indent).
            _surface = new Border
            {
                Margin = new Thickness(Spacing.XS, 0),
                CornerRadius = Spacing.RadiusMedium,
                Background = Brushes.Transparent,
                Child = new DockPanel
                {
                    Margin = new Thickness(Spacing.S + (item.Depth * ExplorerView.IndentPerLevel), 0, Spacing.S, 0),
                    Children =
                    {
                        dot.Dock(Dock.Right),
                        chevron.Dock(Dock.Left),
                        new Border { Width = 6 }.Dock(Dock.Left),
                        icon.Dock(Dock.Left),
                        new Border { Width = 6 }.Dock(Dock.Left),
                        name,
                    },
                },
            };
            Child = _surface;

            _surface.BorderThickness = new Thickness(1);
            _surface.BorderBrush = Brushes.Transparent;
            PointerEntered += (_, _) => { _hovered = true; Paint(); };
            PointerExited += (_, _) => { _hovered = false; Paint(); };
            PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                {
                    e.Handled = true; // not a focus change: the tree keeps the keyboard
                }
            };
            PointerReleased += (_, e) =>
            {
                if (e.InitialPressMouseButton == MouseButton.Left)
                {
                    e.Handled = true;
                    Invoke();
                }
            };
        }

        public ExplorerItemViewModel Item { get; }

        public void Invoke() => _invoke(Item);

        private bool _hovered;
        private bool _dropTarget;

        /// <summary>The drop-target look of the tree's rows: tinted, outlined in the accent.</summary>
        public void MarkDrop(bool target)
        {
            _dropTarget = target;
            Paint();
        }

        private void Paint()
        {
            _surface.Background = _hovered || _dropTarget ? Palette.Hover : Brushes.Transparent;
            _surface.BorderBrush = _dropTarget ? Palette.Accent : Brushes.Transparent;
        }

        protected override AutomationPeer OnCreateAutomationPeer() => new StickyRowPeer(this);
    }

    /// <summary>Screen readers see each pinned folder as a button that scrolls to it.</summary>
    private sealed class StickyRowPeer(StickyRow owner) : ControlAutomationPeer(owner), IInvokeProvider
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;

        protected override bool IsContentElementCore() => true;

        protected override bool IsControlElementCore() => true;

        public void Invoke() => owner.Invoke();
    }
}
