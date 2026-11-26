using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Coder.App.Controls;
using Coder.App.Mvvm;
using Coder.App.Theme;
using Coder.Core.Git;
using Coder.Presentation.Mvvm;
using Coder.Presentation.SourceControl;

namespace Coder.App.Workbench;

/// <summary>
/// The GRAPH section under the changes in Source Control: the commit history with its branch graph,
/// like VS Code's Source Control Graph. Clicking a commit lists the files it changed; clicking a file
/// opens that commit's change as a diff. The header collapses the section.
/// </summary>
public sealed class HistoryView : DockPanel
{
    private readonly HistoryViewModel _history;

    public HistoryView(HistoryViewModel history)
    {
        _history = history;

        var chevron = new Icon(Icons.ChevronDown, 14) { Margin = new Thickness(0, 0, 4, 0) }
            .Bind(Icon.DataProperty, history, x => x.IsExpanded, open => open ? Icons.ChevronDown : Icons.ChevronRight);
        var header = new FlatButton
        {
            Height = 26,
            Padding = new Thickness(Spacing.S, 0),
            Chamfer = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Command = new RelayCommand(() => history.IsExpanded = !history.IsExpanded),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    chevron,
                    new TextBlock
                    {
                        Text = "GRAPH",
                        FontSize = Typography.CaptionSize,
                        FontWeight = FontWeight.SemiBold,
                        LetterSpacing = 0.4,
                        Foreground = Palette.TextSecondary,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                },
            },
        };
        AutomationProperties.SetName(header, "Graph");

        var list = new ListBox
        {
            ItemsSource = history.Rows,
            ItemContainerTheme = AppStyles.ListRowTheme,
            ItemTemplate = Templates.Row<HistoryRow>(CreateRow),
        }.Bind(IsVisibleProperty, history, x => x.IsExpanded);
        AutomationProperties.SetName(list, "Commits");
        ScrollViewer.SetHorizontalScrollBarVisibility(list, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        list.ContainerPrepared += (_, e) => AutomationProperties.SetName(e.Container, e.Container.DataContext switch
        {
            HistoryCommitRow commit => $"{commit.Commit.Subject}, {commit.Commit.Author}, {commit.When}",
            HistoryFileRow file => $"{file.Change.RelativePath}, {file.Change.Kind}",
            HistoryMoreRow => "Load More Commits",
            _ => string.Empty,
        });
        list.Tapped += async (_, e) =>
        {
            if (e.Source is Visual { DataContext: HistoryRow row } source && source.FindAncestorOfType<Button>() is null)
            {
                await ActivateAsync(row);
            }
        };
        list.OnRowKeyDown(async (_, e) =>
        {
            if (e.Key == Key.Enter && list.SelectedItem is HistoryRow row)
            {
                e.Handled = true;
                await ActivateAsync(row);
            }
        });

        var message = new TextBlock
        {
            Margin = new Thickness(Spacing.L, Spacing.XS),
            Foreground = Palette.TextSecondary,
            TextWrapping = TextWrapping.Wrap,
        }
        .Bind(TextBlock.TextProperty, history, x => x.Message)
        .Bind(IsVisibleProperty, history, x => x.Message, text => text.Length > 0);

        Children.Add(new Border { BorderBrush = Palette.Border, BorderThickness = new Thickness(0, 1, 0, 0), Child = header }.Dock(Dock.Top));
        Children.Add(message.Dock(Dock.Top));
        Children.Add(list);
    }

    private Task ActivateAsync(HistoryRow row) => row switch
    {
        HistoryCommitRow commit => _history.ToggleAsync(commit),
        HistoryFileRow file => _history.OpenAsync(file),
        HistoryMoreRow => _history.LoadMoreAsync(),
        _ => Task.CompletedTask,
    };

    private static Control CreateRow(HistoryRow row) => row switch
    {
        HistoryCommitRow commit => CreateCommitRow(commit),
        HistoryFileRow file => CreateFileRow(file),
        _ => new TextBlock
        {
            Text = "Load more commits…",
            Margin = new Thickness(Spacing.L, 0),
            Foreground = Palette.Accent,
            VerticalAlignment = VerticalAlignment.Center,
        },
    };

    private static DockPanel CreateCommitRow(HistoryCommitRow row)
    {
        var commit = row.Commit;
        var refs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, Margin = new Thickness(4, 0, 0, 0) };
        foreach (var name in commit.Refs)
        {
            refs.Children.Add(RefBadge(name, row.Graph.Color));
        }

        var subject = new TextBlock
        {
            Text = commit.Subject,
            FontWeight = row.IsHead ? FontWeight.SemiBold : FontWeight.Normal,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var author = new TextBlock
        {
            Text = commit.Author,
            Margin = new Thickness(6, 0, 0, 0),
            FontSize = Typography.SmallSize,
            Foreground = Palette.TextMuted,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var panel = new DockPanel
        {
            Height = Spacing.ListRowHeight - 2,
            Margin = new Thickness(Spacing.XS, 0, Spacing.S, 0),
            Background = Brushes.Transparent,
            Children =
            {
                new GraphCell { Row = row.Graph, IsHead = row.IsHead, IsMerge = commit.IsMerge }.Dock(Dock.Left),
                refs.Dock(Dock.Right),
                author.Dock(Dock.Right),
                subject,
            },
        };
        ToolTip.SetTip(panel, Details(row));
        return panel;
    }

    private static DockPanel CreateFileRow(HistoryFileRow row)
    {
        var change = row.Change;
        var color = GitColors.For(change.Kind);
        var panel = new DockPanel
        {
            Height = Spacing.ListRowHeight - 2,
            Margin = new Thickness(Spacing.XS, 0, Spacing.S, 0),
            Background = Brushes.Transparent,
            Children =
            {
                new GraphCell { Through = row.Owner.LanesBelow, Width = GraphCell.WidthFor(row.Owner.Graph) }.Dock(Dock.Left),
                new TextBlock
                {
                    Text = change.Letter,
                    Foreground = color,
                    FontWeight = FontWeight.SemiBold,
                    Width = 16,
                    TextAlignment = TextAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                }.Dock(Dock.Right),
                FileIcons.File(change.Path, margin: new Thickness(4, 0, 6, 0)).Dock(Dock.Left),
                new TextBlock { Text = change.FileName, Foreground = color, VerticalAlignment = VerticalAlignment.Center }.Dock(Dock.Left),
                new TextBlock
                {
                    Text = Path.GetDirectoryName(change.RelativePath)?.Replace('\\', '/') ?? string.Empty,
                    Margin = new Thickness(6, 0, 0, 0),
                    FontSize = Typography.SmallSize,
                    Foreground = Palette.TextMuted,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.LeadingCharacterEllipsis,
                },
            },
        };
        ToolTip.SetTip(panel, change.OriginalPath is null ? change.RelativePath : $"{change.RelativePath} (renamed from {Path.GetFileName(change.OriginalPath)})");
        return panel;
    }

    /// <summary>
    /// A branch or tag on a commit: the checked-out branch ("HEAD -> main") filled with the lane's color,
    /// other local branches outlined in it, remote branches and tags quieter.
    /// </summary>
    private static Border RefBadge(string name, int laneColor)
    {
        var lane = Palette.GraphLanes[laneColor % Palette.GraphLanes.Count];
        var isHead = name.StartsWith("HEAD -> ", StringComparison.Ordinal);
        var isTag = name.StartsWith("tag: ", StringComparison.Ordinal);
        var text = isHead ? name["HEAD -> ".Length..] : isTag ? name["tag: ".Length..] : name;
        var isRemote = !isHead && !isTag && text.Contains('/', StringComparison.Ordinal);
        return new Border
        {
            Height = 16,
            Padding = new Thickness(5, 0),
            CornerRadius = new CornerRadius(3),
            VerticalAlignment = VerticalAlignment.Center,
            Background = isHead ? lane : Brushes.Transparent,
            BorderBrush = isRemote || isTag ? Palette.BorderStrong : lane,
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = isTag ? $"◆ {text}" : text,
                FontSize = Typography.CaptionSize,
                Foreground = isHead ? Palette.EditorBackground : isRemote || isTag ? Palette.TextSecondary : Palette.TextPrimary,
                FontWeight = isHead ? FontWeight.SemiBold : FontWeight.Normal,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

    private static string Details(HistoryCommitRow row)
    {
        var commit = row.Commit;
        var lines = new List<string>
        {
            commit.Subject,
            $"{commit.Author} <{commit.Email}>, {row.When} ({RelativeTime.Exact(commit.Date)})",
            $"Commit {commit.ShortHash}{(commit.IsMerge ? " (merge)" : string.Empty)}",
        };
        if (commit.Body.Length > 0)
        {
            lines.Insert(1, string.Empty);
            lines.Insert(2, commit.Body);
            lines.Insert(3, string.Empty);
        }

        return string.Join('\n', lines);
    }

    /// <summary>
    /// Draws one row of the commit graph: the lanes passing through, the lines joining and leaving the
    /// commit, and its dot (a ring for the checked-out commit, hollow for merges). File rows under an
    /// expanded commit only draw the lanes that continue.
    /// </summary>
    private sealed class GraphCell : Control
    {
        private const double LaneWidth = 12;
        private const double Inset = 9;

        public GraphRow? Row { get; init; }

        public IReadOnlyList<(int Lane, int Color)> Through { get; init; } = [];

        public bool IsHead { get; init; }

        public bool IsMerge { get; init; }

        public static double WidthFor(GraphRow row) => Inset * 2 + (row.Width - 1) * LaneWidth;

        protected override Size MeasureOverride(Size availableSize) =>
            new(Row is { } row ? WidthFor(row) : Width, 0);

        public override void Render(DrawingContext context)
        {
            // Rows sit 1px apart (the list item border), so lines run 1px past the edges to stay unbroken.
            var top = -1.0;
            var bottom = Bounds.Height + 1;
            var middle = Bounds.Height / 2;

            foreach (var (lane, color) in Through)
            {
                context.DrawLine(PenFor(color), new Point(X(lane), top), new Point(X(lane), bottom));
            }

            if (Row is not { } row)
            {
                return;
            }

            foreach (var segment in row.Segments)
            {
                var (y1, y2) = segment.IsTop ? (top, middle) : (middle, bottom);
                var (x1, x2) = (X(segment.From), X(segment.To));
                if (x1 == x2)
                {
                    context.DrawLine(PenFor(segment.Color), new Point(x1, y1), new Point(x2, y2));
                    continue;
                }

                var curve = new StreamGeometry();
                using (var g = curve.Open())
                {
                    g.BeginFigure(new Point(x1, y1), isFilled: false);
                    var bend = (y2 - y1) / 2;
                    g.CubicBezierTo(new Point(x1, y1 + bend), new Point(x2, y2 - bend), new Point(x2, y2));
                    g.EndFigure(isClosed: false);
                }

                context.DrawGeometry(null, PenFor(segment.Color), curve);
            }

            var dot = Palette.GraphLanes[row.Color % Palette.GraphLanes.Count];
            var center = new Point(X(row.Lane), middle);
            if (IsHead)
            {
                context.DrawEllipse(Palette.SideBar, new Pen(dot, 2), center, 5.5, 5.5);
                context.DrawEllipse(dot, null, center, 2.5, 2.5);
            }
            else if (IsMerge)
            {
                context.DrawEllipse(Palette.SideBar, new Pen(dot, 2), center, 4, 4);
            }
            else
            {
                context.DrawEllipse(dot, new Pen(Palette.SideBar, 1.5), center, 4.5, 4.5);
            }
        }

        private static double X(int lane) => Inset + lane * LaneWidth;

        private static Pen PenFor(int color) => new(Palette.GraphLanes[color % Palette.GraphLanes.Count], 1.5);
    }
}
