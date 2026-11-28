using System.Text.Json;

namespace Coder.Core.Extensions;

/// <summary>
/// The content of an extension's panel (<c>panel/show</c>): a tree of nodes that Coder draws with its own
/// controls, in its own design. Nodes with an <see cref="PanelNode.Id"/> report what the user does with them
/// (<c>panel/event</c>) and keep their state (focus, text being typed) when the content is shown again.
/// </summary>
public abstract record PanelNode
{
    /// <summary>Names the node for its events, and keeps its control (and focus, caret, scroll) across updates.</summary>
    public string? Id { get; init; }

    /// <summary>In a row or column, takes the space left over.</summary>
    public bool Grow { get; init; }
}

/// <summary>Children stacked top to bottom (<c>column</c>) or left to right (<c>row</c>).</summary>
public sealed record PanelStack(bool Horizontal, IReadOnlyList<PanelNode> Children) : PanelNode
{
    public double Gap { get; init; } = 8;

    public double Padding { get; init; }

    /// <summary>Cross-axis alignment: <c>start</c>, <c>center</c>, <c>end</c> or <c>stretch</c>.</summary>
    public string Align { get; init; } = "stretch";

    /// <summary>A column that scrolls when its content is taller than the panel.</summary>
    public bool Scroll { get; init; }
}

/// <summary>Two panes with a divider the user can drag.</summary>
public sealed record PanelSplit(bool Horizontal, PanelNode First, PanelNode Second) : PanelNode
{
    /// <summary>The first pane's share of the space, 0.1 to 0.9.</summary>
    public double Ratio { get; init; } = 0.5;
}

/// <summary>Text: <c>title</c>, <c>heading</c>, <c>body</c>, <c>muted</c>, <c>code</c>, <c>success</c>, <c>warning</c> or <c>error</c>.</summary>
public sealed record PanelText(string Text, string Style) : PanelNode
{
    /// <summary>Lets the user select (and copy) the text.</summary>
    public bool Selectable { get; init; }
}

/// <summary>A small colored label (an HTTP method, a status): <c>green</c>, <c>blue</c>, <c>orange</c>, <c>red</c>, <c>purple</c> or <c>gray</c>.</summary>
public sealed record PanelBadge(string Text, string Color) : PanelNode;

/// <summary>A button: <c>primary</c>, <c>secondary</c> or <c>ghost</c>. Reports <c>click</c>.</summary>
public sealed record PanelButton(string Label, string Style) : PanelNode
{
    public bool Disabled { get; init; }
}

/// <summary>A one-line text box. Reports <c>change</c> (the text) and <c>submit</c> (Enter).</summary>
public sealed record PanelInput(string Value) : PanelNode
{
    public string? Placeholder { get; init; }

    public bool Monospace { get; init; }
}

/// <summary>A dropdown. Reports <c>change</c> (the chosen option's value).</summary>
public sealed record PanelSelect(string Value, IReadOnlyList<PanelOption> Options) : PanelNode;

/// <param name="Color">Optional: tints the option's label like a badge color.</param>
public sealed record PanelOption(string Value, string Label, string? Color);

/// <summary>A check box. Reports <c>change</c> (true or false).</summary>
public sealed record PanelCheckbox(bool Checked, string? Label) : PanelNode;

/// <summary>Tabs over content. Reports <c>select</c> (the tab's id).</summary>
public sealed record PanelTabs(string Selected, IReadOnlyList<PanelTab> Items) : PanelNode;

/// <param name="Badge">Optional text after the label (a count).</param>
public sealed record PanelTab(string Id, string Label, string? Badge, PanelNode Content);

/// <summary>
/// An editable table (headers, query parameters): text and check-box columns. Reports <c>change</c>
/// (<c>{row, column, value}</c>), <c>add</c> (<c>{column, value}</c>, typing in the empty last row) and <c>remove</c>
/// (<c>{row}</c>).
/// </summary>
public sealed record PanelTable(IReadOnlyList<PanelColumn> Columns, IReadOnlyList<PanelRow> Rows) : PanelNode
{
    /// <summary>An empty last row: typing in it adds a row.</summary>
    public bool CanAdd { get; init; }

    /// <summary>Each row has a remove button.</summary>
    public bool CanRemove { get; init; }
}

/// <param name="Kind"><c>text</c> or <c>checkbox</c>.</param>
public sealed record PanelColumn(string Id, string Label, string Kind, bool Editable, double? Width);

/// <param name="Cells">Column id to value: a string, or a bool for a check-box column.</param>
public sealed record PanelRow(string Id, IReadOnlyDictionary<string, JsonElement> Cells);

/// <summary>A tree (collections, folders). Reports <c>select</c> (<c>{item}</c>) and <c>activate</c> (double-click).</summary>
public sealed record PanelTree(IReadOnlyList<PanelTreeItem> Items) : PanelNode
{
    public string? Selected { get; init; }
}

public sealed record PanelTreeItem(string Id, string Label, IReadOnlyList<PanelTreeItem> Children)
{
    public string? Detail { get; init; }

    public PanelBadge? Badge { get; init; }

    public bool Expanded { get; init; }
}

/// <summary>Code in Coder's own editor (highlighting, find, folding). Reports <c>change</c> (the text) unless read-only.</summary>
public sealed record PanelCode(string Value, string Language) : PanelNode
{
    public bool ReadOnly { get; init; }

    public double MinHeight { get; init; } = 120;
}

/// <summary>A thin line between sections.</summary>
public sealed record PanelDivider : PanelNode;

/// <summary>Empty space that grows (pushes what follows to the end of a row).</summary>
public sealed record PanelSpacer : PanelNode;

/// <summary>Reads panel content from JSON, with limits; a malformed node is an error with its path, for the program.</summary>
public static class PanelContent
{
    public const int MaxDepth = 32;
    public const int MaxNodes = 20_000;
    public const int MaxText = 2 * 1024 * 1024;

    public static readonly string[] TextStyles = ["title", "heading", "body", "muted", "code", "success", "warning", "error"];
    public static readonly string[] Colors = ["green", "blue", "orange", "red", "purple", "gray"];

    public static PanelNode Parse(JsonElement content)
    {
        var count = 0;
        return Node(content, "content", 0, ref count);
    }

    private static PanelNode Node(JsonElement e, string path, int depth, ref int count)
    {
        if (++count > MaxNodes)
        {
            throw new ArgumentException($"Panel content has more than {MaxNodes} nodes.");
        }

        if (depth > MaxDepth)
        {
            throw new ArgumentException($"{path}: nested deeper than {MaxDepth}.");
        }

        if (e.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException($"{path}: a node must be an object with a \"type\".");
        }

        var type = Str(e, "type") ?? throw new ArgumentException($"{path}: a node needs a \"type\".");
        PanelNode node = type switch
        {
            "column" or "row" => new PanelStack(type == "row", Children(e, path, depth, ref count))
            {
                Gap = Num(e, "gap") ?? 8,
                Padding = Num(e, "padding") ?? 0,
                Align = OneOf(e, "align", ["start", "center", "end", "stretch"], path) ?? "stretch",
                Scroll = Bool(e, "scroll"),
            },
            "split" => new PanelSplit(Str(e, "orientation") != "vertical", Child(e, "first", path, depth, ref count), Child(e, "second", path, depth, ref count))
            {
                Ratio = Math.Clamp(Num(e, "ratio") ?? 0.5, 0.1, 0.9),
            },
            "text" => new PanelText(Text(e, "text", path), OneOf(e, "style", TextStyles, path) ?? "body") { Selectable = Bool(e, "selectable") },
            "badge" => Badge(e, path),
            "button" => new PanelButton(Text(e, "label", path), OneOf(e, "style", ["primary", "secondary", "ghost"], path) ?? "secondary") { Disabled = Bool(e, "disabled") },
            "input" => new PanelInput(Str(e, "value") ?? string.Empty) { Placeholder = Str(e, "placeholder"), Monospace = Bool(e, "monospace") },
            "select" => new PanelSelect(Str(e, "value") ?? string.Empty, Options(e, path)),
            "checkbox" => new PanelCheckbox(Bool(e, "checked"), Str(e, "label")),
            "tabs" => Tabs(e, path, depth, ref count),
            "table" => Table(e, path),
            "tree" => new PanelTree(TreeItems(e, path, depth, ref count)) { Selected = Str(e, "selected") },
            "code" => new PanelCode(Str(e, "value") ?? string.Empty, Str(e, "language") ?? "plaintext") { ReadOnly = Bool(e, "readOnly"), MinHeight = Num(e, "minHeight") ?? 120 },
            "divider" => new PanelDivider(),
            "spacer" => new PanelSpacer(),
            _ => throw new ArgumentException($"{path}: unknown node type \"{type}\"."),
        };

        var id = Str(e, "id");
        if (id is null && node is PanelButton or PanelInput or PanelSelect or PanelCheckbox or PanelTabs or PanelTable or PanelTree || id is null && node is PanelCode { ReadOnly: false })
        {
            throw new ArgumentException($"{path}: a {type} needs an \"id\" (its events name it).");
        }

        return node with { Id = id, Grow = Bool(e, "grow") || node is PanelSpacer };
    }

    private static PanelBadge Badge(JsonElement e, string path) =>
        new(Text(e, "text", path), OneOf(e, "color", Colors, path) ?? "gray");

    private static PanelTabs Tabs(JsonElement e, string path, int depth, ref int count)
    {
        var items = new List<PanelTab>();
        var i = 0;
        foreach (var tab in Array(e, "items", path))
        {
            var at = $"{path}.items[{i++}]";
            items.Add(new PanelTab(Str(tab, "id") ?? throw new ArgumentException($"{at}: a tab needs an \"id\"."), Text(tab, "label", at), Str(tab, "badge"), Child(tab, "content", at, depth, ref count)));
        }

        return new PanelTabs(Str(e, "selected") ?? items.FirstOrDefault()?.Id ?? string.Empty, items);
    }

    private static PanelTable Table(JsonElement e, string path)
    {
        var columns = new List<PanelColumn>();
        var i = 0;
        foreach (var column in Array(e, "columns", path))
        {
            var at = $"{path}.columns[{i++}]";
            columns.Add(new PanelColumn(
                Str(column, "id") ?? throw new ArgumentException($"{at}: a column needs an \"id\"."),
                Str(column, "label") ?? string.Empty,
                OneOf(column, "kind", ["text", "checkbox"], at) ?? "text",
                !column.TryGetProperty("editable", out var editable) || editable.ValueKind != JsonValueKind.False,
                Num(column, "width")));
        }

        var rows = new List<PanelRow>();
        i = 0;
        foreach (var row in Array(e, "rows", path, required: false))
        {
            var at = $"{path}.rows[{i++}]";
            var cells = row.TryGetProperty("cells", out var c) && c.ValueKind == JsonValueKind.Object
                ? c.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())
                : [];
            rows.Add(new PanelRow(Str(row, "id") ?? throw new ArgumentException($"{at}: a row needs an \"id\"."), cells));
        }

        return new PanelTable(columns, rows) { CanAdd = Bool(e, "canAdd"), CanRemove = Bool(e, "canRemove") };
    }

    private static List<PanelTreeItem> TreeItems(JsonElement e, string path, int depth, ref int count)
    {
        var items = new List<PanelTreeItem>();
        var i = 0;
        foreach (var item in Array(e, e.TryGetProperty("items", out _) ? "items" : "children", path, required: false))
        {
            var at = $"{path}.items[{i++}]";
            if (++count > MaxNodes || depth > MaxDepth)
            {
                throw new ArgumentException($"{at}: the tree is too big or too deep.");
            }

            items.Add(new PanelTreeItem(Str(item, "id") ?? throw new ArgumentException($"{at}: a tree item needs an \"id\"."), Text(item, "label", at), TreeItems(item, at, depth + 1, ref count))
            {
                Detail = Str(item, "detail"),
                Badge = item.TryGetProperty("badge", out var badge) && badge.ValueKind == JsonValueKind.Object ? Badge(badge, $"{at}.badge") : null,
                Expanded = Bool(item, "expanded"),
            });
        }

        return items;
    }

    private static List<PanelOption> Options(JsonElement e, string path)
    {
        var options = new List<PanelOption>();
        var i = 0;
        foreach (var option in Array(e, "options", path))
        {
            var at = $"{path}.options[{i++}]";
            var value = Str(option, "value") ?? throw new ArgumentException($"{at}: an option needs a \"value\".");
            options.Add(new PanelOption(value, Str(option, "label") ?? value, OneOf(option, "color", Colors, at)));
        }

        return options;
    }

    private static List<PanelNode> Children(JsonElement e, string path, int depth, ref int count)
    {
        var children = new List<PanelNode>();
        var i = 0;
        foreach (var child in Array(e, "children", path, required: false))
        {
            children.Add(Node(child, $"{path}.children[{i++}]", depth + 1, ref count));
        }

        return children;
    }

    private static PanelNode Child(JsonElement e, string name, string path, int depth, ref int count) =>
        e.TryGetProperty(name, out var child) ? Node(child, $"{path}.{name}", depth + 1, ref count) : throw new ArgumentException($"{path}: needs \"{name}\".");

    private static IEnumerable<JsonElement> Array(JsonElement e, string name, string path, bool required = true) =>
        e.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray()
            : required ? throw new ArgumentException($"{path}: needs \"{name}\" (an array).") : [];

    private static string Text(JsonElement e, string name, string path)
    {
        var text = Str(e, name) ?? throw new ArgumentException($"{path}: needs \"{name}\".");
        return text.Length > MaxText ? throw new ArgumentException($"{path}.{name}: longer than {MaxText} characters.") : text;
    }

    private static string? OneOf(JsonElement e, string name, string[] allowed, string path) =>
        Str(e, name) is not { } value ? null
        : allowed.Contains(value) ? value
        : throw new ArgumentException($"{path}.{name}: \"{value}\" isn't one of {string.Join(", ", allowed)}.");

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
