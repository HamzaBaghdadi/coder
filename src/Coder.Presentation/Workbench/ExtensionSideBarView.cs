using Coder.Presentation.Mvvm;

namespace Coder.Presentation.Workbench;

/// <summary>
/// An extension's tab in the secondary side bar (Claude Code): it shows a terminal the extension's program
/// opens there (<see cref="TerminalRequest.View"/>). Showing the tab before that asks the program to open
/// it (<see cref="ActivateAsync"/> runs the view's command).
/// </summary>
public sealed class ExtensionSideBarView(string id, string title, string extensionId, Func<Task> activate) : ObservableObject
{
    private bool _hasTerminal;

    public string Id { get; } = id;

    public string Title { get; } = title;

    public string ExtensionId { get; } = extensionId;

    /// <summary>For a view that shows a panel (<c>"type": "panel"</c>) rather than a terminal: its content and events.</summary>
    public Coder.Presentation.Extensions.PanelSurface? Panel { get; init; }

    /// <summary>True while a terminal runs in the view (the view sets it).</summary>
    public bool HasTerminal
    {
        get => _hasTerminal;
        set => SetProperty(ref _hasTerminal, value);
    }

    /// <summary>The view should start (or replace) its terminal.</summary>
    public event EventHandler<TerminalRequest>? TerminalRequested;

    /// <summary>The view should take the keyboard focus (shown again while its terminal runs).</summary>
    public event EventHandler? FocusRequested;

    /// <summary>Asks the extension to open the view's terminal (its program starts first if needed).</summary>
    public Task ActivateAsync() => activate();

    internal void Request(TerminalRequest request) => TerminalRequested?.Invoke(this, request);

    internal void Focus() => FocusRequested?.Invoke(this, EventArgs.Empty);
}
