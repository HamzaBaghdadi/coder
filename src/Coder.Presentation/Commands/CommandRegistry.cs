namespace Coder.Presentation.Commands;

/// <summary>
/// A user-facing action. Every action lives here once and is reachable from the command palette, a
/// key binding and any button, so behavior never diverges between entry points.
/// </summary>
/// <param name="KeyBinding">Gesture text such as <c>Ctrl+Shift+P</c>; parsed by the UI layer.</param>
public sealed record CommandDefinition(string Id, string Category, string Title, string? KeyBinding, Func<Task> Execute)
{
    public CommandDefinition(string id, string category, string title, string? keyBinding, Action execute)
        : this(id, category, title, keyBinding, () =>
        {
            execute();
            return Task.CompletedTask;
        })
    {
    }

    public string FullTitle => $"{Category}: {Title}";
}

public sealed class CommandRegistry
{
    private readonly Dictionary<string, CommandDefinition> _commands = new(StringComparer.Ordinal);

    public IEnumerable<CommandDefinition> All => _commands.Values;

    public void Register(CommandDefinition command) => _commands.Add(command.Id, command);

    public CommandDefinition Get(string id) => _commands[id];

    public Task ExecuteAsync(string id) => _commands[id].Execute();
}

/// <summary>Command ids, so call sites never repeat magic strings.</summary>
public static class CommandIds
{
    public const string OpenFolder = "workbench.openFolder";
    public const string OpenFile = "workbench.openFile";
    public const string NewFile = "workbench.newFile";
    public const string Save = "workbench.save";
    public const string SaveAs = "workbench.saveAs";
    public const string CloseEditor = "workbench.closeEditor";
    public const string NextEditor = "workbench.nextEditor";
    public const string PreviousEditor = "workbench.previousEditor";
    public const string ToggleSideBar = "workbench.toggleSideBar";
    public const string ToggleTerminal = "terminal.toggle";
    public const string NewTerminal = "terminal.new";
    public const string SplitTerminal = "terminal.split";
    public const string SelectDefaultTerminalProfile = "terminal.selectDefaultProfile";
    public const string NewTerminalWithProfile = "terminal.newWithProfile";
    public const string FindInTerminal = "terminal.find";
    public const string CommandPalette = "workbench.commandPalette";
    public const string QuickOpen = "workbench.quickOpen";
    public const string ShowExplorer = "workbench.showExplorer";
    public const string SearchInFiles = "workbench.searchInFiles";
    public const string ReplaceInFiles = "workbench.replaceInFiles";
    public const string GoToLine = "editor.goToLine";
    public const string Find = "editor.find";
    public const string Replace = "editor.replace";
    public const string FindNext = "editor.findNext";
    public const string FindPrevious = "editor.findPrevious";
    public const string ToggleGpuAcceleration = "preferences.toggleGpuAcceleration";
    public const string ZoomIn = "view.zoomIn";
    public const string ZoomOut = "view.zoomOut";
    public const string ZoomReset = "view.zoomReset";
    public const string TriggerSuggest = "editor.triggerSuggest";
    public const string GoToDefinition = "editor.goToDefinition";
    public const string RestartLanguageServers = "languages.restart";
    public const string ShowSourceControl = "view.showSourceControl";
    public const string ShowExtensions = "view.showExtensions";
    public const string CheckForUpdates = "help.checkForUpdates";
    public const string RestartToUpdate = "help.restartToUpdate";
    public const string SettingsSyncSignIn = "settingsSync.signIn";
    public const string SettingsSyncCancelSignIn = "settingsSync.cancelSignIn";
    public const string SettingsSyncNow = "settingsSync.syncNow";
    public const string SettingsSyncTurnOn = "settingsSync.turnOn";
    public const string SettingsSyncTurnOff = "settingsSync.turnOff";
    public const string SettingsSyncSignOut = "settingsSync.signOut";
    public const string GitCommit = "git.commit";
    public const string GitRefresh = "git.refresh";
    public const string GitStageAll = "git.stageAll";
    public const string GitCheckout = "git.checkout";
    public const string GitPull = "git.pull";
    public const string GitPush = "git.push";
    public const string GitFetch = "git.fetch";
    public const string GitSync = "git.sync";
    public const string GitStash = "git.stash";
    public const string GitPopLatestStash = "git.stashPopLatest";
    public const string GitApplyStash = "git.stashApply";
    public const string GitPopStash = "git.stashPop";
    public const string GitDropStash = "git.stashDrop";
    public const string MergeAcceptAllCurrent = "merge-conflict.accept.all-current";
    public const string MergeAcceptAllIncoming = "merge-conflict.accept.all-incoming";
    public const string MergeAcceptAllBoth = "merge-conflict.accept.all-both";
    public const string MergeNextConflict = "merge-conflict.next";
    public const string MergePreviousConflict = "merge-conflict.previous";
    public const string GitAbortMerge = "git.mergeAbort";
    public const string GitFileHistory = "git.fileHistory";
    public const string GitToggleBlame = "git.toggleBlame";
    public const string GoToSymbol = "workbench.gotoSymbol";
    public const string GoToWorkspaceSymbol = "workbench.showAllSymbols";
    public const string NextReference = "references.next";
    public const string PreviousReference = "references.previous";
    public const string ToggleInlayHints = "editor.toggleInlayHints";
    public const string ShowProblems = "view.problems";
    public const string FindReferences = "editor.findReferences";
    public const string RenameSymbol = "editor.rename";
    public const string QuickFix = "editor.quickFix";
    public const string FormatDocument = "editor.formatDocument";
    public const string TriggerParameterHints = "editor.parameterHints";
    public const string OpenSettings = "preferences.openSettings";
    public const string ColorTheme = "preferences.colorTheme";
    public const string OpenRecent = "file.openRecent";
    public const string TogglePanel = "view.togglePanel";
    public const string ToggleSecondarySideBar = "view.toggleSecondarySideBar";
    public const string TogglePrimarySideBarPosition = "view.togglePrimarySideBarPosition";
    public const string ActivityBarPosition = "view.activityBarPosition";
    public const string ToggleMinimap = "editor.toggleMinimap";
    public const string OpenSettingsPage = "preferences.openSettingsPage";
    public const string ShowMemoryUsage = "developer.showMemoryUsage";
    public const string AgentFocus = "agent.focus";
    public const string AgentNewChat = "agent.newChat";
    public const string AgentPickModel = "agent.pickModel";
    public const string AgentManageProviders = "agent.manageProviders";
    public const string AgentAddActiveFile = "agent.addActiveFile";
}
