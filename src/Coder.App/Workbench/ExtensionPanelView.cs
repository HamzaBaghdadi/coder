using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Coder.App.Controls;
using Coder.App.Mvvm;
using Coder.App.Editor;
using Coder.App.Theme;
using Coder.Core.Extensions;
using Coder.Presentation.Editors;
using Coder.Presentation.Extensions;

namespace Coder.App.Workbench;

/// <summary>
/// Draws an extension's panel (<see cref="PanelSurface"/>) with Coder's own controls: stacks, splits, text,
/// badges, buttons, text boxes, dropdowns, check boxes, tabs, editable tables, trees and code editors, and
/// reports what the user does. When the program sends new content, controls with the same id are kept (so focus,
/// the caret, text being typed, a split's position and a tree's open folders survive) and updated in place.
/// </summary>
public sealed class ExtensionPanelView : Border
{
    private static readonly TimeSpan TypingDelay = TimeSpan.FromMilliseconds(150);

    private readonly Dictionary<string, double> _splitRatios = [];
    private readonly Dictionary<string, bool> _treeExpanded = [];
    private Dictionary<string, Control> _controls = [];
    private Dictionary<string, Control> _previous = [];
    private PanelSurface? _surface;
    private (string Table, string Column)? _focusAfterAdd;

    public ExtensionPanelView()
    {
        Background = Palette.EditorBackground;
        ClipToBounds = true;
    }

    public PanelSurface? Surface
    {
        get => _surface;
        set
        {
            if (ReferenceEquals(_surface, value))
            {
                return;
            }

            if (_surface is not null)
            {
                _surface.PropertyChanged -= OnContentChanged;
            }

            _surface = value;
            _controls = [];
            if (value is not null)
            {
                value.PropertyChanged += OnContentChanged;
            }

            Render();
        }
    }

    /// <summary>For tests: the control drawn for a node id.</summary>
    internal Control? ControlFor(string id) => _controls.GetValueOrDefault(id);

    private void OnContentChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
        Dispatcher.UIThread.Post(Render);

    private void Render()
    {
        _previous = _controls;
        _controls = [];
        var content = _surface?.Content;
        var child = content is null ? null : Build(content);
        if (!ReferenceEquals(Child, child))
        {
            Detach(child);
            Child = child;
        }

        _previous = [];
        if (_focusAfterAdd is { } pending)
        {
            _focusAfterAdd = null;
            FocusNewRow(pending.Table, pending.Column);
        }
    }

    private void Send(string id, string type, JsonNode? value = null) => _surface?.Send(new PanelEvent(id, type, value));

    // Building ---------------------------------------------------------------------------------------

    private Control Build(PanelNode node)
    {
        var control = node switch
        {
            PanelStack stack => Stack(stack),
            PanelSplit split => Split(split),
            PanelText text => Text(text),
            PanelBadge badge => Badge(badge),
            PanelButton button => Button(button),
            PanelInput input => Input(input),
            PanelSelect select => Select(select),
            PanelCheckbox checkbox => Checkbox(checkbox),
            PanelTabs tabs => Tabs(tabs),
            PanelTable table => Table(table),
            PanelTree tree => Tree(tree),
            PanelCode code => Code(code),
            PanelDivider => new Border { Height = 1, Background = Palette.Border, Margin = new Thickness(0, Spacing.XS) },
            _ => new Panel(),
        };
        return control;
    }

    /// <summary>The control kept for an id (detached from where it was), or a new one.</summary>
    private T Keep<T>(string? id, Func<T> create)
        where T : Control
    {
        if (id is not null && _previous.TryGetValue(id, out var existing) && existing is T kept)
        {
            Detach(kept);
            _controls[id] = kept;
            return kept;
        }

        var made = create();
        if (id is not null)
        {
            _controls[id] = made;
        }

        return made;
    }

    private static void Detach(Control? control)
    {
        switch (control?.Parent)
        {
            case Panel panel:
                panel.Children.Remove(control);
                break;
            case Decorator decorator:
                decorator.Child = null;
                break;
            case ContentControl content:
                content.Content = null;
                break;
        }
    }

    private Control Stack(PanelStack stack)
    {
        var grid = new Grid { Margin = new Thickness(stack.Padding) };
        for (var i = 0; i < stack.Children.Count; i++)
        {
            var node = stack.Children[i];
            var child = Build(node);
            var size = node.Grow ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
            var gap = i == 0 ? 0 : stack.Gap;
            if (stack.Horizontal)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition(size));
                child.Margin = new Thickness(gap, 0, 0, 0);
                child.VerticalAlignment = Align(stack.Align, VerticalAlignment.Stretch);
                Grid.SetColumn(child, i);
            }
            else
            {
                grid.RowDefinitions.Add(new RowDefinition(size));
                child.Margin = new Thickness(0, gap, 0, 0);
                if (child is not (TextBox or ComboBox))
                {
                    child.HorizontalAlignment = AlignX(stack.Align);
                }

                Grid.SetRow(child, i);
            }

            Detach(child);
            grid.Children.Add(child);
        }

        return stack.Scroll ? new ScrollViewer { Content = grid, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } : grid;
    }

    private static VerticalAlignment Align(string align, VerticalAlignment stretch) => align switch
    {
        "start" => VerticalAlignment.Top,
        "center" => VerticalAlignment.Center,
        "end" => VerticalAlignment.Bottom,
        _ => stretch,
    };

    private static HorizontalAlignment AlignX(string align) => align switch
    {
        "start" => HorizontalAlignment.Left,
        "center" => HorizontalAlignment.Center,
        "end" => HorizontalAlignment.Right,
        _ => HorizontalAlignment.Stretch,
    };

    private Control Split(PanelSplit split)
    {
        var ratio = split.Id is not null && _splitRatios.TryGetValue(split.Id, out var dragged) ? dragged : split.Ratio;
        var grid = new Grid();
        var first = Build(split.First);
        var second = Build(split.Second);
        Detach(first);
        Detach(second);
        var splitter = new GridSplitter { Background = Palette.Border, ResizeDirection = split.Horizontal ? GridResizeDirection.Columns : GridResizeDirection.Rows };
        if (split.Horizontal)
        {
            grid.ColumnDefinitions = new ColumnDefinitions { new(ratio, GridUnitType.Star), new(1, GridUnitType.Pixel), new(1 - ratio, GridUnitType.Star) };
            splitter.Width = 1;
            Grid.SetColumn(splitter, 1);
            Grid.SetColumn(second, 2);
        }
        else
        {
            grid.RowDefinitions = new RowDefinitions { new(ratio, GridUnitType.Star), new(1, GridUnitType.Pixel), new(1 - ratio, GridUnitType.Star) };
            splitter.Height = 1;
            Grid.SetRow(splitter, 1);
            Grid.SetRow(second, 2);
        }

        // Remember where the user dragged it, so a new render keeps it there.
        splitter.DragCompleted += (_, _) =>
        {
            if (split.Id is not null)
            {
                var a = split.Horizontal ? grid.ColumnDefinitions[0].ActualWidth : grid.RowDefinitions[0].ActualHeight;
                var b = split.Horizontal ? grid.ColumnDefinitions[2].ActualWidth : grid.RowDefinitions[2].ActualHeight;
                _splitRatios[split.Id] = a + b > 0 ? Math.Clamp(a / (a + b), 0.05, 0.95) : ratio;
            }
        };
        grid.Children.Add(first);
        grid.Children.Add(splitter);
        grid.Children.Add(second);
        return grid;
    }

    private static Control Text(PanelText text)
    {
        TextBlock block = text.Selectable ? new SelectableTextBlock() : new TextBlock();
        block.Text = text.Text;
        block.TextWrapping = TextWrapping.Wrap;
        block.VerticalAlignment = VerticalAlignment.Center;
        switch (text.Style)
        {
            case "title":
                block.FontSize = Typography.TitleSize;
                block.FontWeight = FontWeight.SemiBold;
                break;
            case "heading":
                block.FontSize = Typography.HeadingSize;
                block.FontWeight = FontWeight.SemiBold;
                break;
            case "muted":
                block.Foreground = Palette.TextMuted;
                block.FontSize = Typography.SmallSize;
                break;
            case "code":
                block.FontFamily = Typography.Code;
                block.FontSize = Typography.SmallSize;
                break;
            case "success":
                block.Foreground = Palette.GitAdded;
                block.FontWeight = FontWeight.SemiBold;
                break;
            case "warning":
                block.Foreground = Palette.Warning;
                block.FontWeight = FontWeight.SemiBold;
                break;
            case "error":
                block.Foreground = Palette.Danger;
                block.FontWeight = FontWeight.SemiBold;
                break;
        }

        return block;
    }

    internal static IBrush ColorOf(string color) => color switch
    {
        "green" => Palette.GitAdded,
        "blue" => Palette.AccentBright,
        "orange" => Palette.Warning,
        "red" => Palette.Danger,
        "purple" => Palette.KindClass,
        _ => Palette.TextMuted,
    };

    private static Control Badge(PanelBadge badge)
    {
        var color = ColorOf(badge.Color);
        return new Border
        {
            CornerRadius = Spacing.RadiusSmall,
            Padding = new Thickness(6, 1),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = color is ISolidColorBrush solid ? new SolidColorBrush(solid.Color, 0.16) : Palette.Hover,
            Child = new TextBlock { Text = badge.Text, Foreground = color, FontSize = Typography.CaptionSize, FontWeight = FontWeight.Bold, FontFamily = Typography.Code },
        };
    }

    private Control Button(PanelButton node)
    {
        var button = Keep(node.Id, () =>
        {
            var made = new FlatButton { Padding = new Thickness(Spacing.M, 5), VerticalAlignment = VerticalAlignment.Center };
            made.Click += (_, _) => Send(node.Id!, "click");
            return made;
        });
        button.Content = node.Label;
        button.IsEnabled = !node.Disabled;
        button.Classes.Set("primary", node.Style == "primary");
        button.Classes.Set("secondary", node.Style == "secondary");
        AutomationProperties.SetName(button, node.Label);
        return button;
    }

    private Control Input(PanelInput node)
    {
        var box = Keep(node.Id, () => NewTextBox(node.Id!, node.Placeholder, node.Monospace, text => Send(node.Id!, "change", text), () => Send(node.Id!, "submit")));
        box.PlaceholderText = node.Placeholder;
        SetProgramText(box, node.Value);
        return box;
    }

    /// <summary>A text box that reports changes (after a short pause in typing) and Enter.</summary>
    private static TextBox NewTextBox(string id, string? placeholder, bool monospace, Action<string> changed, Action? submitted)
    {
        var box = new TextBox
        {
            PlaceholderText = placeholder,
            VerticalContentAlignment = VerticalAlignment.Center,
            MinHeight = 30,
            FontFamily = monospace ? Typography.Code : Typography.Ui,
        };
        AutomationProperties.SetName(box, placeholder ?? id);
        DispatcherTimer? timer = null;
        void Flush()
        {
            timer?.Stop();
            if (box.Tag is TextState state && state.Pending)
            {
                state.Pending = false;
                changed(box.Text ?? string.Empty);
            }
        }

        box.TextChanged += (_, _) =>
        {
            var state = box.Tag as TextState ?? new TextState();
            box.Tag = state;
            if (state.Applying || (box.Text ?? string.Empty) == (state.ProgramValue ?? string.Empty) && !state.Pending)
            {
                return; // the program set it (the event can come after the assignment)
            }

            state.Pending = true;
            timer ??= new DispatcherTimer(TypingDelay, DispatcherPriority.Background, (_, _) => Flush());
            timer.Stop();
            timer.Start();
        };
        box.LostFocus += (_, _) => Flush();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && submitted is not null)
            {
                Flush();
                submitted();
                e.Handled = true;
            }
        };
        return box;
    }

    /// <summary>
    /// Sets a text box's text from the program, unless the user is typing in it: what they typed wins until the
    /// program sends a different value than it sent before.
    /// </summary>
    private static void SetProgramText(TextBox box, string value)
    {
        var state = box.Tag as TextState ?? new TextState();
        box.Tag = state;
        if (state.ProgramValue == value && box.Text is not null)
        {
            return;
        }

        state.ProgramValue = value;
        if (box.Text != value && !(box.IsFocused && state.Pending))
        {
            state.Applying = true;
            var caret = box.CaretIndex;
            box.Text = value;
            box.CaretIndex = Math.Min(caret, value.Length);
            state.Applying = false;
        }
    }

    private sealed class TextState
    {
        public string? ProgramValue { get; set; }

        public bool Pending { get; set; }

        public bool Applying { get; set; }
    }

    private Control Select(PanelSelect node)
    {
        var combo = Keep(node.Id, () =>
        {
            var made = new ComboBox { MinWidth = 90, VerticalAlignment = VerticalAlignment.Center };
            made.SelectionChanged += (_, _) =>
            {
                if (made.Tag is not true && made.SelectedItem is ComboBoxItem { Tag: string value })
                {
                    Send(node.Id!, "change", value);
                }
            };
            return made;
        });
        combo.Tag = true; // filling it isn't the user choosing
        var items = node.Options.Select(o => new ComboBoxItem
        {
            Tag = o.Value,
            Content = new TextBlock { Text = o.Label, Foreground = o.Color is { } c ? ColorOf(c) : Palette.TextPrimary, FontWeight = o.Color is null ? FontWeight.Normal : FontWeight.SemiBold },
        }).ToList();
        combo.ItemsSource = items;
        combo.SelectedItem = items.FirstOrDefault(i => (string)i.Tag! == node.Value);
        combo.Tag = null;
        AutomationProperties.SetName(combo, node.Id);
        return combo;
    }

    private Control Checkbox(PanelCheckbox node)
    {
        var box = Keep(node.Id, () =>
        {
            var made = new CheckBox { VerticalAlignment = VerticalAlignment.Center };
            made.IsCheckedChanged += (_, _) =>
            {
                if (made.Tag is not true)
                {
                    Send(node.Id!, "change", made.IsChecked == true);
                }
            };
            return made;
        });
        box.Tag = true;
        box.IsChecked = node.Checked;
        box.Content = node.Label;
        box.Tag = null;
        return box;
    }

    // Tabs -------------------------------------------------------------------------------------------

    private Control Tabs(PanelTabs node)
    {
        var headers = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Spacing.XXS };
        var selected = node.Items.FirstOrDefault(t => t.Id == node.Selected) ?? (node.Items.Count > 0 ? node.Items[0] : null);
        foreach (var tab in node.Items)
        {
            var isSelected = ReferenceEquals(tab, selected);
            var label = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 5,
                Children = { new TextBlock { Text = tab.Label, Foreground = isSelected ? Palette.TextPrimary : Palette.TextSecondary, FontWeight = isSelected ? FontWeight.SemiBold : FontWeight.Normal } },
            };
            if (tab.Badge is { Length: > 0 } count)
            {
                label.Children.Add(new TextBlock { Text = count, Foreground = Palette.Accent, FontSize = Typography.CaptionSize, VerticalAlignment = VerticalAlignment.Center });
            }

            var header = new FlatButton
            {
                Padding = new Thickness(Spacing.S, 6),
                Content = new StackPanel
                {
                    Children =
                    {
                        label,
                        new Border { Height = 2, Margin = new Thickness(0, 5, 0, -6), CornerRadius = new CornerRadius(1), Background = isSelected ? Palette.Accent : Brushes.Transparent },
                    },
                },
            };
            AutomationProperties.SetName(header, $"{tab.Label} tab");
            header.Click += (_, _) => Send(node.Id!, "select", tab.Id);
            headers.Children.Add(header);
        }

        var body = selected is null ? new Panel() : Build(selected.Content);
        Detach(body);
        _controls[node.Id!] = headers;
        return new DockPanel
        {
            Children =
            {
                new Border { BorderBrush = Palette.Border, BorderThickness = new Thickness(0, 0, 0, 1), Child = headers, Margin = new Thickness(0, 0, 0, Spacing.S) }.Dock(Dock.Top),
                body,
            },
        };
    }

    // Tables -----------------------------------------------------------------------------------------

    private Control Table(PanelTable node)
    {
        var tableId = node.Id!;
        var grid = new Grid();
        foreach (var column in node.Columns)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(column.Width is { } width ? new GridLength(width) : new GridLength(1, GridUnitType.Star)));
        }

        if (node.CanRemove)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(28)));
        }

        var row = 0;
        if (node.Columns.Any(c => c.Label.Length > 0))
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (var c = 0; c < node.Columns.Count; c++)
            {
                var header = new TextBlock { Text = node.Columns[c].Label, Foreground = Palette.TextMuted, FontSize = Typography.SmallSize, FontWeight = FontWeight.SemiBold, Margin = new Thickness(6, 0, 0, 4) };
                Grid.SetColumn(header, c);
                grid.Children.Add(header);
            }

            row++;
        }

        foreach (var data in node.Rows)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (var c = 0; c < node.Columns.Count; c++)
            {
                var column = node.Columns[c];
                var value = data.Cells.TryGetValue(column.Id, out var cell) ? cell : default;
                var rowId = data.Id;
                var control = Cell(tableId, rowId, column, value, v => Send(tableId, "change", new JsonObject { ["row"] = rowId, ["column"] = column.Id, ["value"] = v }));
                Place(grid, control, row, c);
            }

            if (node.CanRemove)
            {
                var rowId = data.Id;
                var remove = new FlatButton { Content = "×", Padding = new Thickness(6, 2), Foreground = Palette.TextMuted, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
                AutomationProperties.SetName(remove, $"Remove row {rowId}");
                remove.Click += (_, _) => Send(tableId, "remove", new JsonObject { ["row"] = rowId });
                Place(grid, remove, row, node.Columns.Count);
            }

            row++;
        }

        if (node.CanAdd)
        {
            // The empty last row: typing in it adds a row (the program sends it back with an id).
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (var c = 0; c < node.Columns.Count; c++)
            {
                var column = node.Columns[c];
                if (column.Kind != "text" || !column.Editable)
                {
                    continue;
                }

                var box = new TextBox { PlaceholderText = column.Label.Length > 0 ? column.Label : "Add", MinHeight = 28, FontFamily = Typography.Code, FontSize = Typography.SmallSize, Margin = new Thickness(0, 1, 4, 1) };
                AutomationProperties.SetName(box, $"New {column.Label}");
                box.TextChanged += (_, _) =>
                {
                    if (box.Text is { Length: > 0 } text)
                    {
                        _focusAfterAdd = (tableId, column.Id);
                        Send(tableId, "add", new JsonObject { ["column"] = column.Id, ["value"] = text });
                    }
                };
                Place(grid, box, row, c);
            }
        }

        _controls[tableId] = grid;
        return grid;
    }

    private Control Cell(string tableId, string rowId, PanelColumn column, JsonElement value, Action<JsonNode> changed)
    {
        var id = $"{tableId}/{rowId}/{column.Id}";
        if (column.Kind == "checkbox")
        {
            var check = Keep(id, () =>
            {
                var made = new CheckBox { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                made.IsCheckedChanged += (_, _) =>
                {
                    if (made.Tag is not true)
                    {
                        changed(made.IsChecked == true);
                    }
                };
                return made;
            });
            check.Tag = true;
            check.IsChecked = value.ValueKind == JsonValueKind.True;
            check.IsEnabled = column.Editable;
            check.Tag = null;
            return check;
        }

        var text = value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? string.Empty : value.GetRawText();
        if (!column.Editable)
        {
            return new SelectableTextBlock { Text = text, FontFamily = Typography.Code, FontSize = Typography.SmallSize, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 4), TextWrapping = TextWrapping.Wrap };
        }

        var box = Keep(id, () =>
        {
            var made = NewTextBox(id, column.Label, monospace: true, v => changed(v), null);
            made.MinHeight = 28;
            made.FontSize = Typography.SmallSize;
            made.Margin = new Thickness(0, 1, 4, 1);
            return made;
        });
        SetProgramText(box, text);
        return box;
    }

    private static void Place(Grid grid, Control control, int row, int column)
    {
        Grid.SetRow(control, row);
        Grid.SetColumn(control, column);
        Detach(control);
        grid.Children.Add(control);
    }

    /// <summary>After typing in the add row, the program sent the new row back: carry on typing in it.</summary>
    private void FocusNewRow(string tableId, string column)
    {
        var cells = _controls.Where(c => c.Key.StartsWith(tableId + "/", StringComparison.Ordinal) && c.Key.EndsWith("/" + column, StringComparison.Ordinal)).Select(c => c.Value).OfType<TextBox>().ToList();
        if (cells.LastOrDefault() is { } last)
        {
            Dispatcher.UIThread.Post(() =>
            {
                last.Focus();
                last.CaretIndex = last.Text?.Length ?? 0;
            }, DispatcherPriority.Input);
        }
    }

    // Trees ------------------------------------------------------------------------------------------

    private Control Tree(PanelTree node)
    {
        var list = new StackPanel();
        AddTreeItems(node, list, node.Items, 0);
        _controls[node.Id!] = list;
        return list;
    }

    private void AddTreeItems(PanelTree tree, StackPanel list, IReadOnlyList<PanelTreeItem> items, int depth)
    {
        foreach (var item in items)
        {
            var hasChildren = item.Children.Count > 0;
            if (!_treeExpanded.TryGetValue(item.Id, out var expanded))
            {
                expanded = _treeExpanded[item.Id] = item.Expanded;
            }

            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(depth * 14 + 4, 0, 0, 0) };
            content.Children.Add(new TextBlock
            {
                Text = hasChildren ? (expanded ? "▾" : "▸") : string.Empty,
                Width = 10,
                Foreground = Palette.TextMuted,
                VerticalAlignment = VerticalAlignment.Center,
            });
            if (item.Badge is { } badge)
            {
                content.Children.Add(new TextBlock { Text = badge.Text, Foreground = ColorOf(badge.Color), FontSize = Typography.CaptionSize, FontWeight = FontWeight.Bold, FontFamily = Typography.Code, MinWidth = 34, VerticalAlignment = VerticalAlignment.Center });
            }

            content.Children.Add(new TextBlock { Text = item.Label, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            if (item.Detail is { Length: > 0 } detail)
            {
                content.Children.Add(new TextBlock { Text = detail, Foreground = Palette.TextMuted, FontSize = Typography.SmallSize, VerticalAlignment = VerticalAlignment.Center });
            }

            var row = new FlatButton
            {
                Content = content,
                Padding = new Thickness(Spacing.XS, 4),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = item.Id == tree.Selected ? Palette.Selected : Brushes.Transparent,
            };
            AutomationProperties.SetName(row, item.Label);
            var id = item.Id;
            row.Click += (_, _) =>
            {
                if (hasChildren)
                {
                    _treeExpanded[id] = !_treeExpanded[id];
                    Render(); // folding a folder is the view's own business
                }

                Send(tree.Id!, "select", new JsonObject { ["item"] = id });
            };
            row.DoubleTapped += (_, _) => Send(tree.Id!, "activate", new JsonObject { ["item"] = id });
            list.Children.Add(row);
            if (hasChildren && expanded)
            {
                AddTreeItems(tree, list, item.Children, depth + 1);
            }
        }
    }

    // Code -------------------------------------------------------------------------------------------

    private Control Code(PanelCode node)
    {
        var editor = Keep(node.Id, () => NewCodeEditor(node));
        var tab = editor.Tab!;
        if (editor.Tag is not string programValue || programValue != node.Value)
        {
            editor.Tag = node.Value;
            if (tab.Document.GetText() != node.Value)
            {
                tab.Document.IsReadOnly = false; // lifted for the program's own text
                tab.Editor.ApplyEdits([(0, tab.Document.Length, node.Value)]);
                tab.Editor.SetCaret(0);
            }
        }

        tab.Document.IsReadOnly = node.ReadOnly;
        editor.MinHeight = node.MinHeight;
        return editor;
    }

    private CodeEditor NewCodeEditor(PanelCode node)
    {
        // A tab model that's never in the editor area: it gives the editor its language (by a file name).
        var language = Coder.Core.Syntax.Languages.All.FirstOrDefault(l => l.Id == node.Language);
        var name = "panel" + (language is { FileExtensions.Count: > 0 } ? language.FileExtensions[0] : ".txt");
        var tab = new EditorTabViewModel(name, node.Value, new UTF8Encoding(false));
        tab.Document.IsReadOnly = node.ReadOnly;
        var editor = new CodeEditor { Tab = tab, ShowMinimap = false, StickyScroll = false, Tag = node.Value, BorderBrush = Palette.Border, BorderThickness = new Thickness(1) };
        if (node.Id is { } id)
        {
            DispatcherTimer? timer = null;
            tab.Document.Changed += (_, _) =>
            {
                if (tab.Document.IsReadOnly || editor.Tag is string sent && sent == tab.Document.GetText())
                {
                    return; // the program's own text
                }

                timer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (_, _) =>
                {
                    timer!.Stop();
                    var text = tab.Document.GetText();
                    editor.Tag = text; // the program will send it back: no need to set it again
                    Send(id, "change", text);
                });
                timer.Stop();
                timer.Start();
            };
        }

        return editor;
    }
}
