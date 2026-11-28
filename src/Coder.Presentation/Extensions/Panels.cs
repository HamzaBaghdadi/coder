using System.Text.Json.Nodes;
using Coder.Core.Extensions;
using Coder.Presentation.Editors;
using Coder.Presentation.Mvvm;

namespace Coder.Presentation.Extensions;

/// <summary>Something the user did in a panel: which node (<see cref="PanelNode.Id"/>), what (click, change…), with what value.</summary>
public sealed record PanelEvent(string Id, string Type, JsonNode? Value);

/// <summary>
/// Where an extension's panel content is shown (an editor tab or a side-bar view): the content its program sent
/// (<c>panel/show</c>), and where the user's actions go (<c>panel/event</c>).
/// </summary>
public sealed class PanelSurface(Action<PanelEvent> send) : ObservableObject
{
    private PanelNode? _content;
    private int _version;

    /// <summary>The nodes to draw; null until the program sends them.</summary>
    public PanelNode? Content
    {
        get => _content;
        set
        {
            _content = value;
            _version++;
            OnPropertyChanged(); // always: the program sent it again (it may hold the same text, but new values)
        }
    }

    /// <summary>Counts updates (for tests and views that skip redundant work).</summary>
    public int Version => _version;

    /// <summary>Tells the program what the user did.</summary>
    public void Send(PanelEvent e) => send(e);
}

/// <summary>An extension's panel open as an editor tab (<c>coder.panels</c>): one per key, its content drawn by Coder.</summary>
public sealed class PanelTabViewModel : TabViewModel
{
    private readonly Action<PanelTabViewModel> _closed;
    private string _title;

    public PanelTabViewModel(string extensionId, string extensionName, string panelId, string key, string title, Action<PanelEvent> send, Action<PanelTabViewModel> closed)
    {
        ExtensionId = extensionId;
        ExtensionName = extensionName;
        PanelId = panelId;
        Key = key;
        _title = title;
        _closed = closed;
        Surface = new PanelSurface(send);
    }

    public string ExtensionId { get; }

    public string ExtensionName { get; }

    public string PanelId { get; }

    /// <summary>Which instance of the panel this is (a request's id); empty for a panel with one instance.</summary>
    public string Key { get; }

    public PanelSurface Surface { get; }

    public override string Title => _title;

    public override string Description => ExtensionName;

    public override string? FilePath
    {
        get => null;
        set { }
    }

    public void SetTitle(string title)
    {
        if (_title != title)
        {
            _title = title;
            OnPropertyChanged(nameof(Title));
        }
    }

    /// <summary>The user closed it: the program hears <c>panel/closed</c>.</summary>
    public override void Close() => _closed(this);
}
