using System.Collections.ObjectModel;
using Coder.Core.Files;
using Coder.Core.Git;
using Coder.Core.Lsp;
using Coder.Core.Search;
using Coder.Presentation.Commands;
using Coder.Presentation.Editors;
using Coder.Presentation.Explorer;
using Coder.Presentation.LanguageFeatures;
using Coder.Presentation.Mvvm;
using Coder.Presentation.Palette;
using Coder.Presentation.Search;
using Coder.Presentation.Services;
using Coder.Presentation.Settings;
using Coder.Presentation.SourceControl;

namespace Coder.Presentation.Workbench;

/// <summary>The views that can occupy the primary side bar.</summary>
public enum SideBarView
{
    Explorer,
    Search,
    SourceControl,
    Extensions,
}

/// <summary>The views of the bottom panel.</summary>
public enum PanelView
{
    Problems,
    Terminal,
    References,
}

/// <summary>Root view model: owns the workbench parts, the commands that connect them, and the session state.</summary>
public sealed partial class WorkbenchViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IDialogService _dialogs;
    private readonly ISettingsStore _settingsStore;
    private readonly IFileSystem _fileSystem;
    private readonly IColorThemeCatalog? _themes;
    private readonly DocumentReloader _reloader;
    private bool _isSideBarVisible;
    private bool _isPanelVisible;
    private bool _isSecondarySideBarVisible;
    private PanelView _activePanel = PanelView.Terminal;
    private SideBarView _activeSideBarView = SideBarView.Explorer;
    private double _editorFontSize;
    private bool _gpuAcceleration;
    private bool _isSideBarOnRight;
    private bool _isMinimapVisible;
    private bool _minimapRendersCharacters;
    private string _activityBarLocation = "side";
    private string _colorTheme;
    private string _terminalProfile;
    private IReadOnlyList<Coder.Core.Terminal.ShellProfile>? _shellProfiles;
    private readonly Coder.Core.Extensions.ExtensionStore _extensionStore;
    private readonly IReadOnlyList<Coder.Core.Extensions.IExtensionSource> _extensionSources;

    /// <summary>The workbench properties that are settings: a change to one is pushed by Settings Sync.</summary>
    private static readonly HashSet<string> SyncedProperties =
    [
        nameof(EditorFontSize), nameof(ColorTheme), nameof(IsSideBarOnRight), nameof(ActivityBarLocation),
        nameof(IsMinimapVisible), nameof(MinimapRendersCharacters), nameof(TerminalProfileId),
        nameof(EditorFontFamily), nameof(EditorLineHeight), nameof(SecondarySideBarDefaultWidth),
        nameof(CursorStyle), nameof(CursorBlinking), nameof(TerminalFontSize), nameof(TerminalFontFamily),
    ];

    /// <param name="languageServers">Starts language servers; null turns language features off (tests).</param>
    /// <param name="managed">Tools Coder ships (Prettier); null turns them off (tests).</param>
    public WorkbenchViewModel(IFileSystem fileSystem, IDialogService dialogs, ISettingsStore settingsStore, ILanguageServerLauncher? languageServers = null, IColorThemeCatalog? themes = null, ManagedServers? managed = null,
        Coder.Core.Extensions.ExtensionStore? extensionStore = null, IReadOnlyList<Coder.Core.Extensions.IExtensionSource>? extensionSources = null,
        Coder.Presentation.Updates.IUpdateService? updates = null)
    {
        _dialogs = dialogs;
        _settingsStore = settingsStore;
        _fileSystem = fileSystem;
        _themes = themes;
        Settings = settingsStore.Load();
        _isSideBarVisible = Settings.IsSideBarVisible;
        _editorFontSize = Settings.EditorFontSize;
        _gpuAcceleration = Settings.GpuAcceleration;
        _colorTheme = Settings.ColorTheme;
        _terminalProfile = Settings.TerminalProfile;
        _isSideBarOnRight = Settings.SideBarLocation == "right";
        _isMinimapVisible = Settings.ShowMinimap;
        _minimapRendersCharacters = Settings.MinimapRenderCharacters;
        _activityBarLocation = Settings.ActivityBarLocation is "top" or "bottom" or "hidden" ? Settings.ActivityBarLocation : "side";
        RecentFolders = new ObservableCollection<string>(Settings.RecentFolders.Where(Directory.Exists));

        Explorer = new ExplorerViewModel(fileSystem);
        Updates = new Coder.Presentation.Updates.UpdatesViewModel(updates ?? new Coder.Presentation.Updates.NoUpdates());
        _extensionStore = extensionStore ?? new Coder.Core.Extensions.ExtensionStore(Path.Combine(Path.GetTempPath(), "coder-extensions-none"), SharedHttp.Value);
        _extensionSources = extensionSources ?? [];
        Extensions = new Coder.Presentation.Extensions.ExtensionsViewModel(_extensionStore, _extensionSources, ShowThemePickerAsync);
        Extensions.DetailsRequested += (_, item) => ShowExtensionDetails(item);
        Editors = new EditorGroupViewModel(fileSystem, dialogs);
        StatusBar = new StatusBarViewModel(Editors);
        Find = new FindViewModel(Editors);
        Languages = new LanguageServices(languageServers, Editors, () => Explorer.RootPath, CustomLanguageServers);
        Languages.Notice += (_, message) => StatusBar.Message = message;
        Completion = new CompletionViewModel(Languages, Editors, new Coder.Core.Snippets.SnippetCatalog(_extensionStore), () => Explorer.RootPath);
        SignatureHelp = new SignatureHelpViewModel(Languages, Editors);
        SourceControl = new SourceControlViewModel(Explorer, Editors, dialogs, OpenFileAsync);
        SourceControl.Blame.IsInlineEnabled = Settings.InlineBlame;
        _reloader = new DocumentReloader(fileSystem, Editors);
        _reloader.Watch(Explorer.RootPath);
        _reloader.FilesChanged += (_, files) =>
        {
            Languages.NotifyFilesChanged(files);
            if (files.Any(f => f.Type != Coder.Core.Lsp.FileChangeType.Changed))
            {
                Explorer.Refresh(); // files appeared or went away (git, a build, another program)
            }
        };
        SourceControl.StatusChanged += (_, _) => _ = _reloader.ReloadAllAsync(); // git may have rewritten open files
        Search = new SearchViewModel(
            new WorkspaceSearch(fileSystem),
            new ReplaceInFiles(Editors, new FileReplacer(fileSystem)),
            Explorer,
            Editors,
            dialogs,
            OpenFileAsync);
        Commands = new CommandRegistry();
        Palette = new CommandPaletteViewModel(
        [
            new CommandPaletteProvider(Commands),
            new FilePaletteProvider(fileSystem, Explorer, OpenFileAsync),
            new GoToLinePaletteProvider(Editors),
            new DocumentSymbolPaletteProvider(Languages, Editors, RevealAsync),
            new WorkspaceSymbolPaletteProvider(Languages, () => Explorer.RootPath, RevealAsync),
        ]);
        Problems = new ProblemsViewModel(Languages, () => Explorer.RootPath, RevealAsync);
        References = new ReferencesViewModel(Languages, Editors, () => Explorer.RootPath, RevealAsync);
        InlayHints = new InlayHintsService(Languages, Editors) { IsEnabled = Settings.InlayHints };
        StickyScroll = new StickyScrollService(Languages, Editors) { IsEnabled = Settings.StickyScroll };
        InlineSuggestions = new Coder.Presentation.Agent.InlineSuggestionsViewModel(Editors, Completion, ResolveInlineSuggestionsModel, () => Explorer.RootPath)
        {
            IsEnabled = Settings.InlineSuggestions,
            Delay = TimeSpan.FromMilliseconds(Math.Clamp(Settings.InlineSuggestionsDelay, 0, 5000)),
        };
        var edits = new WorkspaceEditApplier(fileSystem, Editors, Languages);
        LanguageCommands = new LanguageCommands(Languages, Editors, Palette, edits, message => StatusBar.Message = message, managed is null ? null : new Coder.Core.Formatting.PrettierFormatter(managed));
        Languages.EditRequested += async (_, edit) =>
        {
            try
            {
                await edits.ApplyAsync(edit);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                StatusBar.Message = $"Some files could not be changed: {exception.Message}";
            }
        };

        Explorer.FileActivated += async (_, path) => await OpenFileAsync(path);
        Explorer.Dialogs = dialogs;
        Explorer.Notice += (_, message) => StatusBar.Message = message;
        Explorer.PathMoved += (_, move) => Editors.FollowMove(move.From, move.To);
        Explorer.PathDeleted += async (_, path) => await Editors.CloseDeletedAsync(path);
        Editors.Saved += async (_, tab) => await OnSavedAsync(tab);
        WireEditorSettings();
        Explorer.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ExplorerViewModel.RootPath) or "" or null)
            {
                _reloader.Watch(Explorer.RootPath);
            }

            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(ProjectName));
        };
        Editors.PropertyChanged += (_, e) =>
        {
            OnPropertyChanged(nameof(Title));

            // The explorer shows the file being edited (opened from Ctrl+P, a definition, a tab…), like VS Code.
            if (e.PropertyName == nameof(EditorGroupViewModel.ActiveTab) && Settings.ExplorerAutoReveal && Editors.ActiveTab?.FilePath is { } shown)
            {
                Explorer.Reveal(shown);
            }
        };
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is { } name && SyncedProperties.Contains(name))
            {
                Sync?.NotifyLocalChange();
            }
        };
        InlayHints.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(InlayHintsService.IsEnabled))
            {
                Sync?.NotifyLocalChange();
            }
        };
        StickyScroll.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(StickyScrollService.IsEnabled))
            {
                Sync?.NotifyLocalChange();
            }
        };
        InlineSuggestions.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Coder.Presentation.Agent.InlineSuggestionsViewModel.IsEnabled))
            {
                Sync?.NotifyLocalChange();
            }
            else if (e.PropertyName == nameof(Coder.Presentation.Agent.InlineSuggestionsViewModel.Error) && InlineSuggestions.Error is { } error)
            {
                StatusBar.Message = $"Inline suggestions: {error}"; // not only in the indicator's tooltip
            }
        };
        SourceControl.Blame.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SourceControl.Blame.IsInlineEnabled))
            {
                Sync?.NotifyLocalChange();
            }
        };
        RegisterCommands();
    }

    /// <summary>
    /// "Shell Command: Install 'coder' Command in PATH" (and "Open with Coder" in File Explorer), like VS
    /// Code's; the installer does it too. Only where the app provides it (Windows).
    /// </summary>
    public void EnableShellIntegration(IShellIntegration shell)
    {
        Register(CommandIds.InstallShellCommand, "Shell Command", "Install 'coder' Command in PATH and 'Open with Coder'", null, () => StatusBar.Message = shell.Install());
        Register(CommandIds.UninstallShellCommand, "Shell Command", "Uninstall 'coder' Command from PATH and 'Open with Coder'", null, () => StatusBar.Message = shell.Uninstall());
    }

    /// <summary>Settings Sync (sign in with GitHub); null until <see cref="EnableSettingsSync"/> (tests leave it off).</summary>
    public SettingsSyncViewModel? Sync { get; private set; }

    /// <summary>Turns on Settings Sync: its commands, its row on the settings page and its items in the Manage menu.</summary>
    /// <param name="openUrl">Opens a page in the browser (GitHub's sign-in page).</param>
    /// <param name="copy">Puts text on the clipboard (the sign-in code).</param>
    public SettingsSyncViewModel EnableSettingsSync(Coder.Core.Sync.SettingsSyncService service, Action<Uri> openUrl, Func<string, Task> copy, TimeSpan? debounce = null)
    {
        if (Sync is not null)
        {
            return Sync;
        }

        Sync = new SettingsSyncViewModel(service, _extensionStore, _extensionSources, _dialogs, CurrentSettings, ApplySyncedSettings,
            message => StatusBar.Message = message, openUrl, copy, debounce);
        Register(CommandIds.SettingsSyncSignIn, "Settings Sync", "Sign in with GitHub to Sync Settings…", null, Sync.SignInAsync);
        Register(CommandIds.SettingsSyncCancelSignIn, "Settings Sync", "Cancel Sign-in", null, Sync.CancelSignIn);
        Register(CommandIds.SettingsSyncNow, "Settings Sync", "Sync Now", null, Sync.SyncNowAsync);
        Register(CommandIds.SettingsSyncTurnOn, "Settings Sync", "Turn On", null, Sync.TurnOnAsync);
        Register(CommandIds.SettingsSyncTurnOff, "Settings Sync", "Turn Off", null, Sync.TurnOff);
        Register(CommandIds.SettingsSyncSignOut, "Settings Sync", "Sign Out", null, Sync.SignOut);
        OnPropertyChanged(nameof(Sync));
        return Sync;
    }

    /// <summary>Coder Agent, the chat in the secondary side bar; null until <see cref="EnableAgent"/> (tests may leave it off).</summary>
    public Coder.Presentation.Agent.AgentViewModel? Agent { get; private set; }

    /// <summary>Turns on Coder Agent: the chat, its commands and its providers.</summary>
    /// <summary>Runs extension programs (null until <see cref="EnableExtensionHost"/>).</summary>
    public Coder.Presentation.Extensions.ExtensionHost? ExtensionHost { get; private set; }

    /// <summary>
    /// Lets installed extensions run their programs, with the <see cref="Coder.Presentation.Extensions.EditorApi"/>.
    /// Their commands are registered at once; call <see cref="Coder.Presentation.Extensions.ExtensionHost.StartAsync"/>
    /// once the window is up (it may ask the user's consent).
    /// </summary>
    public Coder.Presentation.Extensions.ExtensionHost EnableExtensionHost(Coder.Presentation.Extensions.IExtensionLauncher? launcher = null)
    {
        if (ExtensionHost is not null)
        {
            return ExtensionHost;
        }

        var trust = new Coder.Core.Extensions.ExtensionTrust(Path.Combine(_extensionStore.Folder, ".trust.json"));
        ExtensionHost = new Coder.Presentation.Extensions.ExtensionHost(_extensionStore, trust, _dialogs, Commands,
            new Coder.Presentation.Extensions.EditorApi(this, _fileSystem, _dialogs), launcher, message => StatusBar.Message = message, SecondaryViews, AgentServers);
        SecondaryViews.CollectionChanged += (_, _) =>
        {
            if (ActiveSecondaryView is { } id && SecondaryViews.All(v => v.Id != id))
            {
                ActiveSecondaryView = null; // its extension was uninstalled
            }
        };
        return ExtensionHost;
    }

    public Coder.Presentation.Agent.AgentViewModel EnableAgent(Coder.Presentation.Agent.AgentServices services)
    {
        if (Agent is not null)
        {
            return Agent;
        }

        var workspace = new Coder.Presentation.Agent.AgentWorkspace(
            () => Explorer.RootPath,
            _fileSystem,
            Editors,
            files => Languages.NotifyFilesChanged(files),
            OpenFileAsync,
            Palette)
        {
            AgentServers = AgentServers,
        };
        Agent = new Coder.Presentation.Agent.AgentViewModel(services, workspace, Settings);
        Register(CommandIds.AgentFocus, "Coder Agent", "Open Chat", "Ctrl+Alt+I", ShowAgent);
        Register(CommandIds.AgentNewChat, "Coder Agent", "New Chat", null, () =>
        {
            ShowAgent();
            Agent.NewChat();
        });
        Register(CommandIds.AgentPickModel, "Coder Agent", "Select Model…", null, () =>
        {
            ShowAgent();
            return Agent.ShowModelPickerAsync();
        });
        Register(CommandIds.AgentManageProviders, "Coder Agent", "Manage Providers and API Keys", null, () =>
        {
            ShowAgent();
            Agent.IsProvidersOpen = true;
        });
        Register(CommandIds.AgentAddActiveFile, "Coder Agent", "Add Active File to Chat", null, async () =>
        {
            if (Editors.ActiveTab?.FilePath is { } path)
            {
                ShowAgent();
                await Agent.AddPathsAsync([path]);
            }
        });
        Register(CommandIds.PickInlineSuggestionsModel, "Coder Agent", "Select Inline Suggestions Model…", null, PickInlineSuggestionsModelAsync);

        // Keys, providers and the agent's model decide where suggestions come from.
        void Refresh()
        {
            InlineSuggestions.HasProviderKey = Agent.Providers.Any(p => p.HasKey);
            InlineSuggestions.RefreshStatus();
        }

        void Watch(Coder.Presentation.Agent.AgentProviderViewModel provider) => provider.PropertyChanged += (_, _) => Refresh();
        foreach (var provider in Agent.Providers)
        {
            Watch(provider);
        }

        Agent.Providers.CollectionChanged += (_, e) =>
        {
            foreach (var provider in e.NewItems?.OfType<Coder.Presentation.Agent.AgentProviderViewModel>() ?? [])
            {
                Watch(provider);
            }

            Refresh();
        };
        Agent.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(Agent.Model) or nameof(Agent.ProviderId))
            {
                Refresh();
            }
        };
        Refresh();
        OnPropertyChanged(nameof(Agent));
        return Agent;
    }

    /// <summary>AI inline suggestions ("ghost text") in the editor.</summary>
    public Coder.Presentation.Agent.InlineSuggestionsViewModel InlineSuggestions { get; }

    /// <summary>
    /// Where inline suggestions come from: the provider and model chosen for them, else Coder Agent's.
    /// Null when that provider is missing, has no key, or no model is known.
    /// </summary>
    private Coder.Presentation.Agent.InlineSuggestionModel? ResolveInlineSuggestionsModel()
    {
        if (Agent is not { } agent)
        {
            return null;
        }

        var own = Settings.InlineSuggestionsProvider.Length > 0;
        var providerId = own ? Settings.InlineSuggestionsProvider : agent.ProviderId;
        if (agent.Providers.FirstOrDefault(p => p.Id == providerId) is not { IsReady: true } provider)
        {
            return null;
        }

        var model = own ? Settings.InlineSuggestionsModel : agent.Model;
        if (model.Length == 0)
        {
            model = Coder.Core.Ai.AiProviders.DefaultModel(provider.Kind) ?? string.Empty;
        }

        return model.Length == 0 ? null : new(provider.CreateClient(), model, $"{provider.FindModel(model)?.DisplayName ?? model} · {provider.Config.Name}");
    }

    /// <summary>Picks the model for inline suggestions in the palette ("Same as Coder Agent" first).</summary>
    public Task PickInlineSuggestionsModelAsync()
    {
        if (Agent is not { } agent)
        {
            return Task.CompletedTask;
        }

        void Select(string provider, string model)
        {
            Settings.InlineSuggestionsProvider = provider;
            Settings.InlineSuggestionsModel = model;
            InlineSuggestions.RefreshStatus();
            _settingsPage?.RefreshAll();
            Sync?.NotifyLocalChange();
        }

        var same = new PaletteItem("Same as Coder Agent", agent.ModelLabel, Settings.InlineSuggestionsProvider.Length == 0 ? "current" : null, () =>
        {
            Select(string.Empty, string.Empty);
            return Task.CompletedTask;
        });
        return agent.ShowModelPickerAsync("Pick a model for inline suggestions (a small, fast one works best)", Settings.InlineSuggestionsProvider, Settings.InlineSuggestionsModel, Select, [same]);
    }

    /// <summary>Shows the secondary side bar with the chat and puts the caret in its input.</summary>
    public void ShowAgent()
    {
        IsSecondarySideBarVisible = true;
        ActiveSecondaryView = null; // the Coder Agent tab
        Agent?.Focus();
    }

    /// <summary>The settings as they are now (what closing would save), without what only the view knows (window, side bar width).</summary>
    public UserSettings CurrentSettings() => Settings with
    {
        AgentProviders = Agent?.ProviderSettings ?? Settings.AgentProviders,
        AgentProvider = Agent?.ProviderId ?? Settings.AgentProvider,
        AgentModel = Agent?.Model ?? Settings.AgentModel,
        RecentFolders = RecentFolders.ToList(),
        EditorFontSize = EditorFontSize,
        IsSideBarVisible = IsSideBarVisible,
        GpuAcceleration = _gpuAcceleration,
        ColorTheme = ColorTheme,
        TerminalProfile = _terminalProfile,
        InlineBlame = SourceControl.Blame.IsInlineEnabled,
        InlayHints = InlayHints.IsEnabled,
        StickyScroll = StickyScroll.IsEnabled,
        InlineSuggestions = InlineSuggestions.IsEnabled,
        InlineSuggestionsDelay = InlineSuggestions.Delay.TotalMilliseconds,
        SideBarLocation = IsSideBarOnRight ? "right" : "left",
        ShowMinimap = IsMinimapVisible,
        MinimapRenderCharacters = MinimapRendersCharacters,
        ActivityBarLocation = ActivityBarLocation,
    };

    /// <summary>Settings that came from another machine: applied at once, like changing them here.</summary>
    public void ApplySyncedSettings(UserSettings synced)
    {
        EditorFontSize = synced.EditorFontSize;
        EditorFontFamily = synced.EditorFontFamily ?? string.Empty;
        EditorLineHeight = synced.EditorLineHeight;
        SecondarySideBarDefaultWidth = synced.SecondarySideBarDefaultWidth;
        CursorStyle = synced.CursorStyle ?? "line";
        CursorBlinking = synced.CursorBlinking;
        TerminalFontSize = synced.TerminalFontSize;
        TerminalFontFamily = synced.TerminalFontFamily ?? string.Empty;
        Settings.TabSize = synced.TabSize;
        Settings.InsertSpaces = synced.InsertSpaces;
        Settings.DetectIndentation = synced.DetectIndentation;
        Settings.AutoSave = synced.AutoSave ?? "off";
        Settings.AutoSaveDelay = synced.AutoSaveDelay;
        Settings.FormatOnSave = synced.FormatOnSave;
        Settings.TrimTrailingWhitespace = synced.TrimTrailingWhitespace;
        Settings.InsertFinalNewline = synced.InsertFinalNewline;
        Editors.ReapplyOptions();
        ColorTheme = synced.ColorTheme ?? ColorTheme;
        IsSideBarOnRight = synced.SideBarLocation == "right";
        ActivityBarLocation = synced.ActivityBarLocation ?? "side";
        IsMinimapVisible = synced.ShowMinimap;
        MinimapRendersCharacters = synced.MinimapRenderCharacters;
        TerminalProfileId = synced.TerminalProfile ?? string.Empty;
        SourceControl.Blame.IsInlineEnabled = synced.InlineBlame;
        InlayHints.IsEnabled = synced.InlayHints;
        StickyScroll.IsEnabled = synced.StickyScroll;
        InlineSuggestions.IsEnabled = synced.InlineSuggestions;
        InlineSuggestions.Delay = TimeSpan.FromMilliseconds(Math.Clamp(synced.InlineSuggestionsDelay, 0, 5000));
        var previousServers = Settings.LanguageServers;
        Settings = Settings with
        {
            InlineSuggestionsProvider = synced.InlineSuggestionsProvider ?? string.Empty,
            InlineSuggestionsModel = synced.InlineSuggestionsModel ?? string.Empty,
            SideBarDefaultWidth = synced.SideBarDefaultWidth,
            ExplorerAutoReveal = synced.ExplorerAutoReveal,
            ExtensionRegistry = synced.ExtensionRegistry ?? string.Empty,
            OpenVsx = synced.OpenVsx,
            LanguageServers = synced.LanguageServers ?? [],
        };
        _settingsPage?.RefreshAll();
        InlineSuggestions.RefreshStatus();
        if (!previousServers.SequenceEqual(Settings.LanguageServers, LanguageServerSettingComparer.Instance))
        {
            _ = Languages.RestartAsync();
        }
    }

    public ExplorerViewModel Explorer { get; }

    /// <summary>New versions of Coder: checked and downloaded in the background, applied on restart.</summary>
    public Coder.Presentation.Updates.UpdatesViewModel Updates { get; }

    /// <summary>The Extensions view: installed extensions and the marketplace.</summary>
    public Coder.Presentation.Extensions.ExtensionsViewModel Extensions { get; }

    /// <summary>Only for the placeholder store a test workbench gets (it never downloads).</summary>
    private static readonly Lazy<HttpClient> SharedHttp = new(() => new HttpClient());

    public EditorGroupViewModel Editors { get; }

    public StatusBarViewModel StatusBar { get; }

    public FindViewModel Find { get; }

    /// <summary>Language servers: diagnostics, completion, hover, go to definition.</summary>
    public LanguageServices Languages { get; }

    public CompletionViewModel Completion { get; }

    public SignatureHelpViewModel SignatureHelp { get; }

    public SourceControlViewModel SourceControl { get; }

    public SearchViewModel Search { get; }

    public CommandPaletteViewModel Palette { get; }

    public CommandRegistry Commands { get; }

    /// <summary>Settings as loaded at startup; the view reads window placement and side bar width from it.</summary>
    public UserSettings Settings { get; private set; }

    /// <summary>Recently opened folders, most recent first.</summary>
    public ObservableCollection<string> RecentFolders { get; }

    public string Title
    {
        get
        {
            var parts = new[] { Editors.ActiveTab?.Title, Explorer.HasFolder ? Path.GetFileName(Explorer.RootPath) : null, "Coder" };
            return string.Join(" — ", parts.Where(part => !string.IsNullOrEmpty(part)));
        }
    }

    public bool IsSideBarVisible
    {
        get => _isSideBarVisible;
        set => SetProperty(ref _isSideBarVisible, value);
    }

    public SideBarView ActiveSideBarView
    {
        get => _activeSideBarView;
        private set => SetProperty(ref _activeSideBarView, value);
    }

    /// <summary>The bottom panel with the integrated terminal.</summary>
    public bool IsTerminalVisible
    {
        get => IsPanelVisible && ActivePanel == PanelView.Terminal;
        set
        {
            if (value)
            {
                ShowPanel(PanelView.Terminal);
            }
            else if (IsTerminalVisible)
            {
                IsPanelVisible = false;
            }
        }
    }

    /// <summary>The side bar on the right; empty for now, it will host views such as agents.</summary>
    public bool IsSecondarySideBarVisible
    {
        get => _isSecondarySideBarVisible;
        set => SetProperty(ref _isSecondarySideBarVisible, value);
    }

    /// <summary>The code in miniature on the right of the editor.</summary>
    public bool IsMinimapVisible
    {
        get => _isMinimapVisible;
        set => SetProperty(ref _isMinimapVisible, value);
    }

    /// <summary>The minimap draws words in their colors (otherwise one bar per line).</summary>
    public bool MinimapRendersCharacters
    {
        get => _minimapRendersCharacters;
        set => SetProperty(ref _minimapRendersCharacters, value);
    }

    /// <summary>True when the primary side bar (and the activity bar beside it) is on the right; the secondary side bar is then on the left.</summary>
    public bool IsSideBarOnRight
    {
        get => _isSideBarOnRight;
        set => SetProperty(ref _isSideBarOnRight, value);
    }

    /// <summary>Where the activity bar is: "side" (the outer edge, beside the side bar), "top", "bottom" (of the side bar) or "hidden".</summary>
    public string ActivityBarLocation
    {
        get => _activityBarLocation;
        set => SetProperty(ref _activityBarLocation, value is "top" or "bottom" or "hidden" ? value : "side");
    }

    /// <summary>"View: Activity Bar Position…": side, top, bottom or hidden.</summary>
    public void ShowActivityBarPositionPicker() =>
        Palette.Pick(PickProvider.FromList("Where the activity bar should be", new (string Id, string Label, string Detail)[]
        {
            ("side", "Side", "Beside the primary side bar, on the window's edge"),
            ("top", "Top", "Across the top of the primary side bar"),
            ("bottom", "Bottom", "Across the bottom of the primary side bar"),
            ("hidden", "Hidden", "Views stay reachable with their shortcuts and the command palette"),
        }.Select(option => new PaletteItem(option.Label, option.Detail, option.Id == ActivityBarLocation ? "current" : null, () =>
        {
            ActivityBarLocation = option.Id;
            return Task.CompletedTask;
        })).ToList()));

    /// <summary>"Developer: Show Memory Usage": what Coder itself uses (language servers run as their own processes).</summary>
    public static string MemoryReport()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var gc = GC.GetGCMemoryInfo();
        return string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "Memory: {0:N0} MB private ({1:N0} MB in use by .NET objects, {2:N0} MB reserved for them), {3} threads.",
            process.PrivateMemorySize64 / 1048576.0, GC.GetTotalMemory(false) / 1048576.0, gc.HeapSizeBytes / 1048576.0, process.Threads.Count);
    }

    /// <summary>GPU rendering (smoother) or software rendering (less memory). Applies on restart.</summary>
    public bool GpuAcceleration
    {
        get => _gpuAcceleration;
        set => SetProperty(ref _gpuAcceleration, value);
    }

    /// <summary>The shell new terminals start (a profile id; empty for the default).</summary>
    public string TerminalProfileId
    {
        get => DefaultShellProfile?.Id ?? _terminalProfile;
        set => SetProperty(ref _terminalProfile, value ?? string.Empty);
    }

    private SettingsPageViewModel? _settingsPage;

    /// <summary>"Preferences: Open Settings" (Ctrl+,): the settings page, in a tab.</summary>
    public void OpenSettingsPage()
    {
        if (_settingsPage is null)
        {
            _settingsPage = new SettingsPageViewModel([.. SyncSettingItems(), .. CreateSettingItems()], OpenSettingsFileAsync);
            PropertyChanged += (_, _) => _settingsPage.RefreshAll(); // a command or key changed a value
            if (Sync is not null)
            {
                Sync.PropertyChanged += (_, _) => _settingsPage.RefreshAll();
            }
        }

        Editors.Show(_settingsPage);
    }

    /// <summary>The "Settings Sync" row: who is signed in, and the one thing to do next.</summary>
    private IEnumerable<SettingItem> SyncSettingItems()
    {
        if (Sync is not { } sync)
        {
            yield break;
        }

        yield return new ActionSetting("Settings Sync", "Settings Sync",
            "Sign in with GitHub and your settings and extensions follow you to every machine you sign in on. They are kept in a secret gist in your GitHub account; the window's position, recent folders and GPU acceleration stay per machine.",
            () => sync.StatusText,
            () => sync.IsSigningIn ? "Cancel Sign-in" : !sync.IsSignedIn ? "Sign in with GitHub…" : sync.IsSyncOn ? "Sync Now" : "Turn On Sync",
            () =>
            {
                if (sync.IsSigningIn)
                {
                    sync.CancelSignIn();
                    return Task.CompletedTask;
                }

                return sync.IsSignedIn ? sync.TurnOnAsync() : sync.SignInAsync();
            })
        { Keywords = "github account sync gist cloud backup sign in" };
    }

    private List<SettingItem> CreateSettingItems() =>
    [
        new ChoiceSetting("Appearance", "Color Theme", "The colors of the whole window. \"Follow system\" switches with Windows' light or dark mode.",
            [new("dark", "Obsidian (dark)"), new("light", "Daylight (light)"), new("hc-dark", "High Contrast Dark"), new("hc-light", "High Contrast Light"), new("system", "Follow system")],
            () => ColorTheme, value => ColorTheme = value) { Keywords = "dark light theme colour vs code monokai" }
            .WithOptionsFrom(FindInstalledThemeOptionsAsync),
        new ChoiceSetting("Appearance", "Primary Side Bar Position", "Which side the Explorer, Search and Source Control are on. The secondary side bar takes the other side.",
            [new("left", "Left"), new("right", "Right")],
            () => IsSideBarOnRight ? "right" : "left", value => IsSideBarOnRight = value == "right") { Keywords = "layout sidebar" },
        new ChoiceSetting("Appearance", "Activity Bar Position", "Where the bar of view icons is: on the window's edge, across the top or bottom of the side bar, or hidden.",
            [new("side", "Side"), new("top", "Top"), new("bottom", "Bottom"), new("hidden", "Hidden")],
            () => ActivityBarLocation, value => ActivityBarLocation = value) { Keywords = "layout icons" },
        new NumberSetting("Appearance", "Side Bar Default Width", "The width, in pixels, that double-clicking the side bar's edge returns it to.",
            () => Settings.SideBarDefaultWidth, value => { Settings.SideBarDefaultWidth = value; Sync?.NotifyLocalChange(); }, 170, 800) { Keywords = "layout sidebar size" },
        new NumberSetting("Appearance", "Secondary Side Bar Default Width", "The width, in pixels, Coder Agent's side bar opens at, and what double-clicking its edge returns it to.",
            () => SecondarySideBarDefaultWidth, value => SecondarySideBarDefaultWidth = value, 240, 900) { Keywords = "layout agent chat size secondary" },

        new ChoiceSetting("Editor", "Font Family", "The font code is shown in, in the editor. Default uses the first installed of JetBrains Mono, Cascadia Code, Fira Code, Consolas.",
            [new(string.Empty, "Default"), .. CodeFonts.Select(font => new SettingOption(font, font))],
            () => EditorFontFamily, value => EditorFontFamily = value) { Keywords = "font typeface monospace text" },
        new NumberSetting("Editor", "Font Size", "The size of code in the editor. Ctrl+= and Ctrl+- change it too.",
            () => EditorFontSize, value => EditorFontSize = value, 8, 40) { Keywords = "zoom text" },
        new ChoiceSetting("Editor", "Line Height", "The space each line of code takes, as a multiple of the font size.",
            [.. ((double[])[1.2, 1.3, 1.4, 1.5, 1.6, 1.8, 2.0]).Select(h => new SettingOption(h.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture), h == 1.5 ? "1.5 (default)" : h.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)))],
            () => EditorLineHeight.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
            value => EditorLineHeight = double.Parse(value, System.Globalization.CultureInfo.InvariantCulture)) { Keywords = "spacing leading density" },
        new ChoiceSetting("Editor", "Cursor Style", "The shape of the caret.",
            [new("line", "Line"), new("block", "Block"), new("underline", "Underline")],
            () => CursorStyle, value => CursorStyle = value) { Keywords = "caret cursor block" },
        new ToggleSetting("Editor", "Cursor Blinking", "The caret blinks. Off keeps it steady.",
            () => CursorBlinking, value => CursorBlinking = value) { Keywords = "caret cursor blink" },
        new ChoiceSetting("Editor", "Tab Size", "Spaces per indent level, and how wide a tab character shows, for files that don't show their own.",
            [new("2", "2"), new("4", "4"), new("8", "8")],
            () => Settings.TabSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
            value => { Settings.TabSize = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture); Editors.ReapplyOptions(); Sync?.NotifyLocalChange(); }) { Keywords = "indent indentation width spaces" },
        new ToggleSetting("Editor", "Insert Spaces", "Tab inserts spaces. Off inserts a tab character.",
            () => Settings.InsertSpaces, value => { Settings.InsertSpaces = value; Editors.ReapplyOptions(); Sync?.NotifyLocalChange(); }) { Keywords = "indent indentation tabs" },
        new ToggleSetting("Editor", "Detect Indentation", "A file's own indentation (tabs, 2 or 4 spaces…) wins over Tab Size and Insert Spaces when it opens.",
            () => Settings.DetectIndentation, value => { Settings.DetectIndentation = value; Editors.ReapplyOptions(); Sync?.NotifyLocalChange(); }) { Keywords = "indent indentation auto guess" },
        new ToggleSetting("Editor", "Minimap", "The code in miniature on the editor's right edge, to see where you are and jump around.",
            () => IsMinimapVisible, value => IsMinimapVisible = value) { Keywords = "overview scroll" },
        new ToggleSetting("Editor", "Minimap: Render Characters", "Draw every word in the minimap as its own mark. Off draws each line as a few solid blocks in the code's colors.",
            () => MinimapRendersCharacters, value => MinimapRendersCharacters = value) { Keywords = "minimap blocks" },
        new ToggleSetting("Editor", "Inlay Hints", "Parameter names and inferred types shown inside the code, from the language server.",
            () => InlayHints.IsEnabled, value => InlayHints.IsEnabled = value) { Keywords = "types parameters" },
        new ToggleSetting("Editor", "Sticky Scroll", "Keep the headers of the blocks you are scrolling through (class, method, if…) pinned at the top of the editor. Click one to go to it.",
            () => StickyScroll.IsEnabled, value => StickyScroll.IsEnabled = value) { Keywords = "scope header pinned outline" },
        new ToggleSetting("Editor", "Inline Blame", "Who last changed the current line, and when, shown at the end of it.",
            () => SourceControl.Blame.IsInlineEnabled, value => SourceControl.Blame.IsInlineEnabled = value) { Keywords = "git author" },
        new ToggleSetting("Editor", "Inline Suggestions", "AI code suggestions in gray after the caret while you type, from Coder Agent's providers. Tab accepts, Ctrl+Right accepts the next word, Esc dismisses.",
            () => InlineSuggestions.IsEnabled, value => InlineSuggestions.IsEnabled = value) { Keywords = "ai copilot autocomplete ghost text completion agent" },
        new NumberSetting("Editor", "Inline Suggestions: Delay", "How long typing must pause, in milliseconds, before a suggestion is asked for.",
            () => InlineSuggestions.Delay.TotalMilliseconds, value => { InlineSuggestions.Delay = TimeSpan.FromMilliseconds(value); Sync?.NotifyLocalChange(); }, 0, 5000) { Keywords = "ai copilot debounce" },
        new ActionSetting("Editor", "Inline Suggestions: Model", "The model suggestions come from. A small, fast model answers sooner; by default it is Coder Agent's.",
            () => Settings.InlineSuggestionsProvider.Length == 0 ? $"Same as Coder Agent ({InlineSuggestions.ModelLabel})" : InlineSuggestions.ModelLabel,
            () => "Change Model…",
            PickInlineSuggestionsModelAsync) { Keywords = "ai copilot provider openrouter ollama freellmapi" },

        new ToggleSetting("Explorer", "Auto Reveal", "The explorer selects the file you are editing, opening its folders.",
            () => Settings.ExplorerAutoReveal, value => { Settings.ExplorerAutoReveal = value; Sync?.NotifyLocalChange(); }) { Keywords = "files tree follow" },

        new ChoiceSetting("Files", "Auto Save", "Save files without pressing Ctrl+S: after you stop typing, or when you switch to another tab or window.",
            [new("off", "Off"), new("afterDelay", "After a delay"), new("onFocusChange", "On focus change")],
            () => Settings.AutoSave, value => { Settings.AutoSave = value; Sync?.NotifyLocalChange(); }) { Keywords = "autosave save automatically" },
        new NumberSetting("Files", "Auto Save Delay", "With \"After a delay\", how long typing must pause, in milliseconds, before the file is saved.",
            () => Settings.AutoSaveDelay, value => { Settings.AutoSaveDelay = value; Sync?.NotifyLocalChange(); }, 100, 60000) { Keywords = "autosave milliseconds" },
        new ToggleSetting("Files", "Format On Save", "Format the document (with its language server, or Prettier for web files) when you save it with Ctrl+S.",
            () => Settings.FormatOnSave, value => { Settings.FormatOnSave = value; Sync?.NotifyLocalChange(); }) { Keywords = "format prettier beautify save" },
        new ToggleSetting("Files", "Trim Trailing Whitespace", "Remove spaces and tabs at the ends of lines when saving.",
            () => Settings.TrimTrailingWhitespace, value => { Settings.TrimTrailingWhitespace = value; Sync?.NotifyLocalChange(); }) { Keywords = "whitespace spaces save clean" },
        new ToggleSetting("Files", "Insert Final Newline", "End files with a newline when saving.",
            () => Settings.InsertFinalNewline, value => { Settings.InsertFinalNewline = value; Sync?.NotifyLocalChange(); }) { Keywords = "newline eol end of file save" },

        new ChoiceSetting("Terminal", "Default Shell", "The shell new terminals start.",
            ShellProfiles.Select(p => new SettingOption(p.Id, p.Name)).ToList(),
            () => TerminalProfileId, value => TerminalProfileId = value) { Keywords = "powershell cmd bash wsl profile" },
        new ChoiceSetting("Terminal", "Terminal Font Family", "The terminal's font. Default uses the editor's list (JetBrains Mono, Cascadia Code, …, the first installed).",
            [new(string.Empty, "Default"), .. CodeFonts.Select(font => new SettingOption(font, font))],
            () => TerminalFontFamily, value => TerminalFontFamily = value) { Keywords = "font typeface monospace terminal" },
        new NumberSetting("Terminal", "Terminal Font Size", "The size of text in the terminal.",
            () => TerminalFontSize, value => TerminalFontSize = value, 8, 40) { Keywords = "font text terminal zoom" },

        new ToggleSetting("Performance", "GPU Acceleration", "Draw with the graphics card: smoother scrolling with half the processor work. Off saves about 100 MB of memory (the graphics driver's share).",
            () => GpuAcceleration, value => GpuAcceleration = value) { Keywords = "memory ram rendering", Note = "Applies after a restart." },
    ];

    /// <summary>The open folder's name for the title bar, or a prompt to open one.</summary>
    public string ProjectName => Explorer.RootPath is { } root ? Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar)) : "Open Folder…";

    /// <summary>The bottom panel (Problems, Terminal).</summary>
    public bool IsPanelVisible
    {
        get => _isPanelVisible;
        set
        {
            if (SetProperty(ref _isPanelVisible, value))
            {
                OnPropertyChanged(nameof(IsTerminalVisible));
            }
        }
    }

    public PanelView ActivePanel
    {
        get => _activePanel;
        private set
        {
            if (SetProperty(ref _activePanel, value))
            {
                OnPropertyChanged(nameof(IsTerminalVisible));
            }
        }
    }

    public void ShowPanel(PanelView view)
    {
        ActivePanel = view;
        IsPanelVisible = true;
    }

    /// <summary>Shows a panel view, or hides the panel when that view is already showing (Ctrl+`, Ctrl+Shift+M).</summary>
    public void TogglePanel(PanelView view)
    {
        if (IsPanelVisible && ActivePanel == view)
        {
            IsPanelVisible = false;
        }
        else
        {
            ShowPanel(view);
        }
    }

    public ProblemsViewModel Problems { get; }

    /// <summary>Parameter names and inferred types shown inside the code.</summary>
    public InlayHintsService InlayHints { get; }

    /// <summary>Block headers pinned at the top of the editor while scrolling.</summary>
    public StickyScrollService StickyScroll { get; }

    /// <summary>The References panel (Find All References).</summary>
    public ReferencesViewModel References { get; }

    public LanguageCommands LanguageCommands { get; }

    /// <summary>Shift+F12: lists every use of the symbol at the caret in the References panel.</summary>
    public async Task FindReferencesAsync()
    {
        if (Editors.ActiveEditor is not { } tab)
        {
            return;
        }

        ShowPanel(PanelView.References);
        if (!await References.FindAsync(tab, tab.Editor.Selection.Active))
        {
            StatusBar.Message = Languages.StatusText.Length == 0
                ? $"No language server for {tab.Language.DisplayName} files, so finding references is not available."
                : $"The {tab.Language.DisplayName} language server does not support finding references.";
        }
    }

    /// <summary>Asks the terminal panel for a new terminal, with this shell (null: the default one).</summary>
    public event EventHandler<Coder.Core.Terminal.ShellProfile?>? NewTerminalRequested;

    /// <summary>Asks the terminal panel for a new terminal (default shell) starting in a folder.</summary>
    public event EventHandler<string>? TerminalAtFolderRequested;

    /// <summary>The explorer's "Open in Integrated Terminal": a new shell in that folder.</summary>
    public void OpenTerminalAt(string folder)
    {
        IsTerminalVisible = true;
        TerminalAtFolderRequested?.Invoke(this, folder);
    }

    /// <summary>Asks the terminal panel for a named terminal running a command, with extra environment variables (extensions).</summary>
    public event EventHandler<TerminalRequest>? TerminalRequested;

    /// <summary>
    /// Opens a new terminal in the default shell, named, with the request's variables, and types its command
    /// into it: in the terminal panel, or in an extension's side-bar view (<see cref="TerminalRequest.View"/>).
    /// A request for a view that already runs a terminal only shows it, unless <paramref name="replace"/>.
    /// </summary>
    public void OpenTerminal(TerminalRequest request, bool replace = false)
    {
        if (request.View is { } id && SecondaryViews.FirstOrDefault(v => v.Id == id) is { } view)
        {
            ShowSecondaryView(id);
            if (view.HasTerminal && !replace)
            {
                view.Focus();
            }
            else
            {
                view.Request(request);
            }

            return;
        }

        IsTerminalVisible = true;
        TerminalRequested?.Invoke(this, request);
    }

    /// <summary>Text extensions show after line ends (<c>editor/setDecorations</c>), such as Error Lens's messages.</summary>
    public Coder.Presentation.Editors.LineDecorations Decorations { get; } = new();

    /// <summary>Coding agents extensions bring to Coder Agent (<c>coder.agents</c>), offered in its model picker.</summary>
    public System.Collections.ObjectModel.ObservableCollection<Coder.Presentation.Agent.AgentServer> AgentServers { get; } = [];

    /// <summary>Views extensions add to the secondary side bar, as tabs beside Coder Agent.</summary>
    public System.Collections.ObjectModel.ObservableCollection<ExtensionSideBarView> SecondaryViews { get; } = [];

    private string? _activeSecondaryView;

    /// <summary>The secondary side bar's tab: a <see cref="ExtensionSideBarView.Id"/>, or null for Coder Agent.</summary>
    public string? ActiveSecondaryView
    {
        get => _activeSecondaryView;
        set => SetProperty(ref _activeSecondaryView, value is not null && SecondaryViews.All(v => v.Id != value) ? null : value);
    }

    /// <summary>Opens the secondary side bar on a view (null: Coder Agent).</summary>
    public void ShowSecondaryView(string? id)
    {
        IsSecondarySideBarVisible = true;
        ActiveSecondaryView = id;
    }

    /// <summary>Asks the terminal panel to open find in the active terminal.</summary>
    public event EventHandler? FindInTerminalRequested;

    /// <summary>Asks the terminal panel to split the active terminal (a new shell beside it).</summary>
    public event EventHandler? SplitTerminalRequested;

    /// <summary>The shells installed on this machine (found once, on first use).</summary>
    public IReadOnlyList<Coder.Core.Terminal.ShellProfile> ShellProfiles => _shellProfiles ??= Coder.Core.Terminal.ShellProfiles.Detect();

    /// <summary>The shell new terminals start: the chosen default when installed, else PowerShell.</summary>
    public Coder.Core.Terminal.ShellProfile? DefaultShellProfile => Coder.Core.Terminal.ShellProfiles.Resolve(ShellProfiles, _terminalProfile);

    /// <summary>"Terminal: Select Default Profile": which shell new terminals start (remembered).</summary>
    public void ShowDefaultProfilePicker() =>
        Palette.Pick(PickProvider.FromList("Select the default shell for new terminals", ShellProfiles.Select(profile =>
            new PaletteItem(profile.Name, profile.CommandLine, profile == DefaultShellProfile ? "default" : null, () =>
            {
                TerminalProfileId = profile.Id;
                StatusBar.Message = $"New terminals start {profile.Name}.";
                return Task.CompletedTask;
            })).ToList()));

    /// <summary>The terminal's "Launch Profile…" button: a new terminal with the chosen shell.</summary>
    public void ShowNewTerminalWithProfilePicker() =>
        Palette.Pick(PickProvider.FromList("Select a shell for the new terminal", ShellProfiles.Select(profile =>
            new PaletteItem(profile.Name, profile.CommandLine, profile == DefaultShellProfile ? "default" : null, () =>
            {
                IsTerminalVisible = true;
                NewTerminalRequested?.Invoke(this, profile);
                return Task.CompletedTask;
            })).ToList()));

    /// <summary>Where new shells start: the open folder, else the user's home folder.</summary>
    public string TerminalWorkingDirectory =>
        Explorer.RootPath ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>The color theme: "dark", "light" or "system". The view applies it.</summary>
    public string ColorTheme
    {
        get => _colorTheme;
        set => SetProperty(ref _colorTheme, value);
    }

    /// <summary>The code font's family ("" = the built-in preference list). Kept in <see cref="Settings"/>.</summary>
    public string EditorFontFamily
    {
        get => Settings.EditorFontFamily;
        set
        {
            if (Settings.EditorFontFamily != value)
            {
                Settings.EditorFontFamily = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Editor line height as a multiple of the font size (1.2–2.0).</summary>
    public double EditorLineHeight
    {
        get => Settings.EditorLineHeight;
        set
        {
            var clamped = Math.Clamp(value, 1.2, 2.0);
            if (Settings.EditorLineHeight != clamped)
            {
                Settings.EditorLineHeight = clamped;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>The secondary side bar's opening width (and what double-clicking its edge returns it to).</summary>
    public double SecondarySideBarDefaultWidth
    {
        get => Settings.SecondarySideBarDefaultWidth;
        set
        {
            var clamped = Math.Clamp(value, 240, 900);
            if (Settings.SecondarySideBarDefaultWidth != clamped)
            {
                Settings.SecondarySideBarDefaultWidth = clamped;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Coding fonts installed on this machine, for the Font Family setting (the app fills it in).</summary>
    public IReadOnlyList<string> CodeFonts { get; set; } = [];

    /// <summary>Installed color themes (VS Code's and extensions'), for the Color Theme setting.</summary>
    private async Task<IEnumerable<SettingOption>> FindInstalledThemeOptionsAsync() =>
        _themes is null ? [] : (await _themes.FindAsync()).Select(theme => new SettingOption(theme.Id, theme.Label)).ToList();

    public double EditorFontSize
    {
        get => _editorFontSize;
        set => SetProperty(ref _editorFontSize, Math.Clamp(value, 8, 40));
    }

    /// <summary>
    /// Activity bar click: shows a view, or hides the side bar when that view is already showing.
    /// </summary>
    public void ToggleSideBarView(SideBarView view)
    {
        if (IsSideBarVisible && ActiveSideBarView == view)
        {
            IsSideBarVisible = false;
            return;
        }

        ShowSideBarView(view);
    }

    public void ShowSideBarView(SideBarView view)
    {
        ActiveSideBarView = view;
        IsSideBarVisible = true;
        if (view == SideBarView.Search)
        {
            Search.Focus();
        }
        else if (view == SideBarView.Extensions)
        {
            _ = Extensions.LoadAsync(); // the marketplace is asked only once the view is opened
        }
    }

    public async Task OpenFileAsync(string path)
    {
        try
        {
            await Editors.OpenAsync(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusBar.Message = exception.Message;
        }
    }

    public void OpenFolder(string path)
    {
        if (!Directory.Exists(path))
        {
            RecentFolders.Remove(path);
            StatusBar.Message = $"Folder not found: {path}";
            return;
        }

        try
        {
            Explorer.OpenFolder(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusBar.Message = $"Can't open {path}: {exception.Message}";
            return;
        }

        ShowSideBarView(SideBarView.Explorer);

        RecentFolders.Remove(path);
        RecentFolders.Insert(0, path);
        while (RecentFolders.Count > UserSettings.MaxRecentFolders)
        {
            RecentFolders.RemoveAt(RecentFolders.Count - 1);
        }
    }

    /// <summary>Reopens the last folder when Coder starts without arguments.</summary>
    public void RestoreLastFolder()
    {
        if (RecentFolders.Count > 0)
        {
            OpenFolder(RecentFolders[0]);
        }
    }

    /// <summary>Called before the window closes. Returns false when the user cancels.</summary>
    public Task<bool> ConfirmCloseAsync() => Editors.CloseAllAsync();

    /// <summary>Stops the language servers politely before the app exits.</summary>
    public async ValueTask DisposeAsync()
    {
        Extensions.Dispose();
        Updates.OnExit(); // a downloaded update installs after Coder exits
        Updates.Dispose();
        Completion.Dispose();
        InlayHints.Dispose();
        StickyScroll.Dispose();
        InlineSuggestions.Dispose();
        SourceControl.Dispose();
        _reloader.Dispose();
        if (Agent is not null)
        {
            await Agent.StopAgentsAsync(); // coding agents (ACP processes) end with Coder
        }

        if (ExtensionHost is not null)
        {
            await ExtensionHost.DisposeAsync(); // extension programs end with Coder
        }

        await Languages.DisposeAsync();
    }

    /// <summary>Asks the title bar to open its project dropdown (Ctrl+R).</summary>
    public event EventHandler? RecentProjectsRequested;

    public void ShowRecentProjects() => RecentProjectsRequested?.Invoke(this, EventArgs.Empty);

    public bool IsCurrentProject(string folder) => Coder.Core.Lsp.DocumentUri.PathComparer.Equals(folder, Explorer.RootPath);

    /// <summary>
    /// Switches to a recent project from the title bar's dropdown (like Zed). The current project's
    /// editors close first, asking about unsaved ones; cancelling keeps everything as it was.
    /// </summary>
    public async Task SwitchProjectAsync(string folder)
    {
        if (IsCurrentProject(folder))
        {
            return;
        }


        if (await Editors.CloseAllAsync())
        {
            OpenFolder(folder);
        }
    }

    /// <summary>"Git: Stash…": an optional message, then Enter stashes every change (untracked files too).</summary>
    public void ShowStashPrompt() =>
        Palette.Pick(new PickProvider("Stash message (optional), then Enter", query =>
        [
            new PaletteItem(query.Trim().Length == 0 ? "Stash all changes" : $"Stash all changes as \"{query.Trim()}\"", "Untracked files are included", null,
                () => SourceControl.StashAsync(query.Trim())),
        ]));

    /// <summary>Lists the stashes to apply, pop or drop one.</summary>
    public async Task ShowStashPickerAsync(StashAction action)
    {
        var stashes = await SourceControl.ListStashesAsync();
        if (stashes.Count == 0)
        {
            StatusBar.Message = "There are no stashes.";
            return;
        }

        var items = stashes.Select(stash => new PaletteItem(stash.Description, $"{stash.Reference} · {stash.Age}", null, () => action switch
        {
            StashAction.Apply => SourceControl.ApplyStashAsync(stash, drop: false),
            StashAction.Pop => SourceControl.ApplyStashAsync(stash, drop: true),
            _ => DropStashAsync(stash),
        })).ToList();
        Palette.Pick(PickProvider.FromList(action switch
        {
            StashAction.Apply => "Pick a stash to apply (it is kept)",
            StashAction.Pop => "Pick a stash to pop (applied, then removed)",
            _ => "Pick a stash to drop",
        }, items));
    }

    /// <summary>
    /// Lists the commits that changed the active file (following renames); picking one shows what that
    /// commit changed in it.
    /// </summary>
    public async Task ShowFileHistoryAsync()
    {
        if (Editors.ActiveEditor?.FilePath is not { } path)
        {
            StatusBar.Message = "Open a file to see its history.";
            return;
        }

        if (!SourceControl.HasRepository)
        {
            StatusBar.Message = "The file is not in a git repository.";
            return;
        }

        var name = Path.GetFileName(path);
        var commits = await SourceControl.History.FileLogAsync(path);
        if (commits.Count == 0)
        {
            StatusBar.Message = $"No commits have changed {name} yet.";
            return;
        }

        var now = DateTimeOffset.Now;
        var items = commits.Select(commit => new PaletteItem(
            commit.Subject,
            $"{commit.ShortHash} · {commit.Author} · {RelativeTime.Format(commit.Date, now)}",
            null,
            () => SourceControl.History.OpenFileAtAsync(commit, path))).ToList();
        Palette.Pick(PickProvider.FromList($"History of {name}: pick a commit to see its change", items));
    }

    private async Task DropStashAsync(Coder.Core.Git.GitStash stash)
    {
        if (await _dialogs.ConfirmAsync($"Drop \"{stash.Description}\"?", "The stashed changes are deleted. This cannot be undone.", "Drop Stash"))
        {
            await SourceControl.DropStashAsync(stash);
        }
    }

    private async Task PopLatestStashAsync()
    {
        var stashes = await SourceControl.ListStashesAsync();
        if (stashes.Count == 0)
        {
            StatusBar.Message = "There are no stashes.";
            return;
        }

        await SourceControl.ApplyStashAsync(stashes[0], drop: true);
    }

    /// <summary>
    /// "Preferences: Color Theme": the built-in themes, following the operating system, and every VS Code
    /// theme on this machine. Moving through the list shows each theme at once; Esc goes back to the
    /// theme that was in use.
    /// </summary>
    public Task ShowThemePickerAsync() => ShowThemePickerAsync(null);

    /// <summary>The color theme picker; with <paramref name="onlyIdsStartingWith"/>, only those themes (an extension's).</summary>
    public async Task ShowThemePickerAsync(string? onlyIdsStartingWith)
    {
        var original = ColorTheme;
        var ids = new Dictionary<PaletteItem, string>();
        PaletteItem Item(string id, string label, string detail)
        {
            var item = new PaletteItem(label, detail, id == original ? "current" : null, () =>
            {
                ColorTheme = id;
                return Task.CompletedTask;
            });
            ids[item] = id;
            return item;
        }

        List<PaletteItem> items =
        [
            Item("dark", "Obsidian", "Dark: a soft neutral black, easy on the eyes for long sessions"),
            Item("light", "Daylight", "Light: bright and crisp, for well-lit rooms"),
            Item("hc-dark", "High Contrast Dark", "Black with bright outlines and text, for low vision and glare"),
            Item("hc-light", "High Contrast Light", "White with deep outlines and black text"),
            Item("system", "Follow System", "Switches with your operating system's light, dark or contrast mode"),
        ];
        if (_themes is not null)
        {
            items.AddRange((await _themes.FindAsync()).Select(theme => Item(theme.Id, theme.Label, theme.Detail)));
        }

        if (onlyIdsStartingWith is not null)
        {
            items = items.Where(item => ids[item].StartsWith(onlyIdsStartingWith, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        Palette.Pick(new PickProvider("Select a color theme (Up/Down to preview)", query => CommandPaletteViewModel.Rank(items, query))
        {
            Preview = item =>
            {
                if (item is not null && ids.TryGetValue(item, out var id))
                {
                    ColorTheme = id;
                }
            },
            Cancelled = () => ColorTheme = original,
        });
    }

    /// <summary>"Preferences: Open Settings (JSON)": the settings file in an editor (created with the current settings if missing).</summary>
    public async Task OpenSettingsFileAsync()
    {
        if (_settingsStore.FilePath is not { } path)
        {
            StatusBar.Message = "Settings are not stored in a file.";
            return;
        }

        if (!File.Exists(path))
        {
            _settingsStore.Save(Settings);
        }

        await OpenFileAsync(path);
    }

    /// <summary>
    /// Saving settings.json applies it: settings are reloaded (so closing the app does not write the old
    /// ones back) and language servers restart when their configuration changed.
    /// </summary>
    private async Task OnSavedAsync(Editors.EditorTabViewModel tab)
    {
        if (_settingsStore.FilePath is not { } path || !Coder.Core.Lsp.DocumentUri.PathComparer.Equals(tab.FilePath, path))
        {
            return;
        }

        var previous = Settings.LanguageServers;
        Settings = _settingsStore.Load();
        Sync?.NotifyLocalChange();
        if (!previous.SequenceEqual(Settings.LanguageServers, LanguageServerSettingComparer.Instance))
        {
            await Languages.RestartAsync();
            StatusBar.Message = "Settings saved. Language servers restarted.";
        }
    }

    private IReadOnlyList<LanguageServerDefinition> CustomLanguageServers() =>
        Settings.LanguageServers
            .Where(s => s.Command.Length > 0 && s.Languages.Count > 0)
            .Select(s => new LanguageServerDefinition(s.Name.Length > 0 ? s.Name : s.Command, s.Languages, s.Command, s.Arguments, s.RootMarkers, $"check the command in settings.json: {s.Command}"))
            .ToList();

    /// <summary>The branch picker (status bar branch, "Git: Checkout to…").</summary>
    public async Task ShowBranchPickerAsync()
    {
        if (!SourceControl.HasRepository)
        {
            StatusBar.Message = "The open folder is not in a git repository.";
            return;
        }

        Palette.Pick(new BranchPicker(SourceControl, await SourceControl.ListBranchesAsync()));
    }

    /// <summary>Opens the definition of the symbol at the caret (F12, Ctrl+Click).</summary>
    public async Task GoToDefinitionAsync()
    {
        if (Editors.ActiveEditor is not { } tab)
        {
            return;
        }

        var location = await Languages.FindDefinitionAsync(tab, tab.Editor.Selection.Active, CancellationToken.None);
        if (location is null)
        {
            StatusBar.Message = "No definition found";
            return;
        }

        await RevealAsync(location.Path, location.Range);
    }

    /// <summary>Opens a file and selects a range in it (definitions, references, problems).</summary>
    public async Task RevealAsync(string path, TextRange range)
    {
        await OpenFileAsync(path);
        if (Editors.FindTab(path) is { } target)
        {
            var document = target.Document;
            target.Editor.SetSelection(new Coder.Core.Text.Selection(document.GetOffset(range.Start), document.GetOffset(range.End)));
        }
    }

    /// <summary>Resolves every merge conflict in the active file the same way (one undo step).</summary>
    public void AcceptAllConflicts(ConflictResolution resolution)
    {
        var count = Editors.ActiveEditor is { } tab ? MergeConflicts.ResolveAll(tab.Editor, resolution) : 0;
        StatusBar.Message = count switch
        {
            0 => "No merge conflicts in this file",
            1 => "Resolved 1 merge conflict",
            _ => $"Resolved {count} merge conflicts",
        };
    }

    /// <summary>Moves the caret to the next (or previous) merge conflict in the active file, wrapping around.</summary>
    public void GoToConflict(int direction)
    {
        if (Editors.ActiveEditor is not { } tab || tab.Conflicts is not { Count: > 0 } conflicts)
        {
            StatusBar.Message = "No merge conflicts in this file";
            return;
        }

        var document = tab.Document;
        var caretLine = document.GetLineOfOffset(tab.Editor.Selection.Active);
        var target = direction > 0
            ? conflicts.FirstOrDefault(c => c.Start > caretLine, conflicts[0])
            : conflicts.LastOrDefault(c => c.End < caretLine, conflicts[^1]);
        tab.Editor.SetCaret(document.GetLineStart(target.Start));
    }

    /// <summary>Persists the session. The view supplies what only it knows (window and side bar size).</summary>
    public void SaveSettings(WindowPlacement window, double sideBarWidth) =>
        _settingsStore.Save(CurrentSettings() with { SideBarWidth = sideBarWidth, Window = window });

    private void RegisterCommands()
    {
        Register(CommandIds.OpenFolder, "File", "Open Folder…", "Ctrl+Alt+O", async () =>
        {
            if (await _dialogs.PickFolderAsync() is { } folder)
            {
                OpenFolder(folder);
            }
        });
        Register(CommandIds.OpenFile, "File", "Open File…", "Ctrl+O", async () =>
        {
            if (await _dialogs.PickFileAsync() is { } file)
            {
                await OpenFileAsync(file);
            }
        });
        Register(CommandIds.NewFile, "File", "New Text File", "Ctrl+N", Editors.NewUntitled);
        Register(CommandIds.Save, "File", "Save", "Ctrl+S", () => SaveActiveAsync(chooseLocation: false));
        Register(CommandIds.SaveAs, "File", "Save As…", "Ctrl+Shift+S", () => SaveActiveAsync(chooseLocation: true));
        Register(CommandIds.CloseEditor, "View", "Close Editor", "Ctrl+W", async () =>
        {
            if (Editors.ActiveTab is { } tab)
            {
                await Editors.CloseAsync(tab);
            }
        });
        Register(CommandIds.NextEditor, "View", "Open Next Editor", "Ctrl+Tab", () => Editors.Cycle(1));
        Register(CommandIds.PreviousEditor, "View", "Open Previous Editor", "Ctrl+Shift+Tab", () => Editors.Cycle(-1));
        Register(CommandIds.ToggleSideBar, "View", "Toggle Primary Side Bar", "Ctrl+B", () => IsSideBarVisible = !IsSideBarVisible);
        Register(CommandIds.ToggleTerminal, "View", "Toggle Terminal", "Ctrl+OemTilde", () => TogglePanel(PanelView.Terminal));
        Register(CommandIds.ShowProblems, "View", "Toggle Problems", "Ctrl+Shift+M", () => TogglePanel(PanelView.Problems));
        Register(CommandIds.NewTerminal, "Terminal", "Create New Terminal", "Ctrl+Shift+OemTilde", () =>
        {
            IsTerminalVisible = true;
            NewTerminalRequested?.Invoke(this, null);
        });
        Register(CommandIds.SelectDefaultTerminalProfile, "Terminal", "Select Default Profile", null, ShowDefaultProfilePicker);
        Register(CommandIds.FindInTerminal, "Terminal", "Find", null, () =>
        {
            ShowPanel(PanelView.Terminal);
            FindInTerminalRequested?.Invoke(this, EventArgs.Empty);
        });
        Register(CommandIds.NewTerminalWithProfile, "Terminal", "Create New Terminal (With Profile)", null, ShowNewTerminalWithProfilePicker);
        Register(CommandIds.SplitTerminal, "Terminal", "Split Terminal", "Ctrl+Shift+D5", () =>
        {
            IsTerminalVisible = true;
            SplitTerminalRequested?.Invoke(this, EventArgs.Empty);
        });
        Register(CommandIds.OpenRecent, "File", "Open Recent…", "Ctrl+R", ShowRecentProjects);
        Register(CommandIds.TogglePanel, "View", "Toggle Panel", "Ctrl+J", () => IsPanelVisible = !IsPanelVisible);
        Register(CommandIds.ToggleSecondarySideBar, "View", "Toggle Secondary Side Bar", "Ctrl+Alt+B", () => IsSecondarySideBarVisible = !IsSecondarySideBarVisible);
        Register(CommandIds.ToggleMinimap, "View", "Toggle Minimap", null, () => IsMinimapVisible = !IsMinimapVisible);
        Register(CommandIds.TogglePrimarySideBarPosition, "View", "Toggle Primary Side Bar Position (Left/Right)", null, () => IsSideBarOnRight = !IsSideBarOnRight);
        Register(CommandIds.ActivityBarPosition, "View", "Activity Bar Position…", null, ShowActivityBarPositionPicker);
        Register(CommandIds.ShowExplorer, "View", "Show Explorer", "Ctrl+Shift+E", () => ShowSideBarView(SideBarView.Explorer));
        Register(CommandIds.CheckForUpdates, "Help", "Check for Updates…", null, async () =>
        {
            await Updates.CheckNowAsync();
            StatusBar.Message = Updates.StatusText;
        });
        Register(CommandIds.RestartToUpdate, "Help", "Restart to Update", null, Updates.RestartToUpdateAsync);
        Register(CommandIds.ShowExtensions, "View", "Show Extensions", "Ctrl+Shift+X", () => ShowSideBarView(SideBarView.Extensions));
        Register(CommandIds.ShowSourceControl, "View", "Show Source Control", "Ctrl+Shift+G", () => ShowSideBarView(SideBarView.SourceControl));
        Register(CommandIds.GitCommit, "Git", "Commit", null, SourceControl.CommitAsync);
        Register(CommandIds.GitRefresh, "Git", "Refresh", null, SourceControl.RefreshAsync);
        Register(CommandIds.GitStageAll, "Git", "Stage All Changes", null, SourceControl.StageAllAsync);
        Register(CommandIds.GitCheckout, "Git", "Checkout to…", null, ShowBranchPickerAsync);
        Register(CommandIds.GitPull, "Git", "Pull", null, SourceControl.PullAsync);
        Register(CommandIds.GitPush, "Git", "Push", null, SourceControl.PushAsync);
        Register(CommandIds.GitFetch, "Git", "Fetch", null, SourceControl.FetchAsync);
        Register(CommandIds.GitSync, "Git", "Sync", null, SourceControl.SyncAsync);
        Register(CommandIds.GitStash, "Git", "Stash…", null, ShowStashPrompt);
        Register(CommandIds.GitPopLatestStash, "Git", "Pop Latest Stash", null, PopLatestStashAsync);
        Register(CommandIds.GitApplyStash, "Git", "Apply Stash…", null, () => ShowStashPickerAsync(StashAction.Apply));
        Register(CommandIds.GitPopStash, "Git", "Pop Stash…", null, () => ShowStashPickerAsync(StashAction.Pop));
        Register(CommandIds.GitDropStash, "Git", "Drop Stash…", null, () => ShowStashPickerAsync(StashAction.Drop));
        Register(CommandIds.MergeAcceptAllCurrent, "Merge Conflict", "Accept All Current", null, () => AcceptAllConflicts(ConflictResolution.Current));
        Register(CommandIds.MergeAcceptAllIncoming, "Merge Conflict", "Accept All Incoming", null, () => AcceptAllConflicts(ConflictResolution.Incoming));
        Register(CommandIds.MergeAcceptAllBoth, "Merge Conflict", "Accept All Both", null, () => AcceptAllConflicts(ConflictResolution.Both));
        Register(CommandIds.MergeNextConflict, "Merge Conflict", "Next Conflict", null, () => GoToConflict(1));
        Register(CommandIds.MergePreviousConflict, "Merge Conflict", "Previous Conflict", null, () => GoToConflict(-1));
        Register(CommandIds.GitAbortMerge, "Git", "Abort Merge", null, () => SourceControl.AbortMergeAsync());
        Register(CommandIds.GitFileHistory, "Git", "View File History", null, ShowFileHistoryAsync);
        Register(CommandIds.GitToggleBlame, "Git", "Toggle Line Blame", null, () =>
        {
            SourceControl.Blame.IsInlineEnabled = !SourceControl.Blame.IsInlineEnabled;
            StatusBar.Message = SourceControl.Blame.IsInlineEnabled ? "Line blame shown" : "Line blame hidden";
        });
        Register(CommandIds.SearchInFiles, "Search", "Search in Files", "Ctrl+Shift+F", () => ShowSideBarView(SideBarView.Search));
        Register(CommandIds.ReplaceInFiles, "Search", "Replace in Files", "Ctrl+Shift+H", () =>
        {
            Search.IsReplaceVisible = true;
            ShowSideBarView(SideBarView.Search);
        });
        Register(CommandIds.CommandPalette, "View", "Show All Commands", "Ctrl+Shift+P", () => Palette.Open(">"));
        Register(CommandIds.QuickOpen, "Go", "Go to File…", "Ctrl+P", () => Palette.Open(string.Empty));
        Register(CommandIds.GoToLine, "Go", "Go to Line…", "Ctrl+G", () => Palette.Open(":"));
        Register(CommandIds.GoToSymbol, "Go", "Go to Symbol in Editor…", "Ctrl+Shift+O", () => Palette.Open("@"));
        Register(CommandIds.GoToWorkspaceSymbol, "Go", "Go to Symbol in Workspace…", "Ctrl+T", () => Palette.Open("#"));
        Register(CommandIds.Find, "Edit", "Find", "Ctrl+F", () => Find.Open(withReplace: false));
        Register(CommandIds.Replace, "Edit", "Replace", "Ctrl+H", () => Find.Open(withReplace: true));
        Register(CommandIds.FindNext, "Edit", "Find Next", "F3", Find.FindNext);
        Register(CommandIds.FindPrevious, "Edit", "Find Previous", "Shift+F3", Find.FindPrevious);
        Register(CommandIds.ColorTheme, "Preferences", "Color Theme", "Ctrl+K Ctrl+T", ShowThemePickerAsync);
        Register(CommandIds.ShowMemoryUsage, "Developer", "Show Memory Usage", null, () => StatusBar.Message = MemoryReport());
        Register(CommandIds.OpenSettingsPage, "Preferences", "Open Settings", "Ctrl+OemComma", OpenSettingsPage);
        Register(CommandIds.OpenSettings, "Preferences", "Open Settings (JSON)", null, OpenSettingsFileAsync);
        Register(CommandIds.ToggleGpuAcceleration, "Preferences", "Toggle GPU Acceleration", null, () =>
        {
            GpuAcceleration = !GpuAcceleration;
            StatusBar.Message = _gpuAcceleration
                ? "GPU acceleration on (smoother scrolling). Restart Coder to apply."
                : "GPU acceleration off (lower memory use). Restart Coder to apply.";
        });
        Register(CommandIds.ZoomIn, "View", "Zoom In", "Ctrl+OemPlus", () => EditorFontSize += 1);
        Register(CommandIds.ZoomOut, "View", "Zoom Out", "Ctrl+OemMinus", () => EditorFontSize -= 1);
        Register(CommandIds.ZoomReset, "View", "Reset Zoom", "Ctrl+D0", () => EditorFontSize = UserSettings.DefaultEditorFontSize);
        Register(CommandIds.TriggerSuggest, "Editor", "Trigger Suggest", "Ctrl+Space", Completion.Trigger);
        Register(CommandIds.Fold, "Editor", "Fold", "Ctrl+Shift+OemOpenBrackets", () => Editors.ActiveEditor?.FoldAtCaret());
        Register(CommandIds.Unfold, "Editor", "Unfold", "Ctrl+Shift+OemCloseBrackets", () => Editors.ActiveEditor?.UnfoldAtCaret());
        Register(CommandIds.FoldAll, "Editor", "Fold All", "Ctrl+K Ctrl+D0", () => Editors.ActiveEditor?.FoldAll());
        Register(CommandIds.UnfoldAll, "Editor", "Unfold All", "Ctrl+K Ctrl+J", () => Editors.ActiveEditor?.UnfoldAll());
        Register(CommandIds.TriggerParameterHints, "Editor", "Trigger Parameter Hints", "Ctrl+Shift+Space", SignatureHelp.Trigger);
        Register(CommandIds.GoToDefinition, "Go", "Go to Definition", "F12", GoToDefinitionAsync);
        Register(CommandIds.FindReferences, "Go", "Find All References", "Shift+F12", FindReferencesAsync);
        Register(CommandIds.NextReference, "Go", "Next Reference", "F4", () => References.StepAsync(1));
        Register(CommandIds.PreviousReference, "Go", "Previous Reference", "Shift+F4", () => References.StepAsync(-1));
        Register(CommandIds.ToggleInlineSuggestions, "Coder Agent", "Toggle Inline Suggestions", null, () =>
        {
            // On but without a model: the indicator's click picks one instead of turning suggestions off.
            if (InlineSuggestions.IsEnabled && InlineSuggestions.Status == Coder.Presentation.Agent.InlineSuggestionStatus.NoModel && Agent is not null)
            {
                return PickInlineSuggestionsModelAsync();
            }

            InlineSuggestions.Toggle();
            StatusBar.Message = InlineSuggestions.IsEnabled ? "Inline suggestions on" : "Inline suggestions off";
            return Task.CompletedTask;
        });
        Register(CommandIds.TriggerInlineSuggestion, "Coder Agent", "Trigger Inline Suggestion", null,InlineSuggestions.TriggerAsync);
        Register(CommandIds.ToggleInlayHints, "View", "Toggle Inlay Hints", null, () =>
        {
            InlayHints.IsEnabled = !InlayHints.IsEnabled;
            StatusBar.Message = InlayHints.IsEnabled ? "Inlay hints shown" : "Inlay hints hidden";
        });
        Register(CommandIds.ToggleStickyScroll, "View", "Toggle Sticky Scroll", null, () =>
        {
            StickyScroll.IsEnabled = !StickyScroll.IsEnabled;
            StatusBar.Message = StickyScroll.IsEnabled ? "Sticky scroll on" : "Sticky scroll off";
        });
        Register(CommandIds.RenameSymbol, "Edit", "Rename Symbol", "F2", LanguageCommands.Rename);
        Register(CommandIds.QuickFix, "Edit", "Quick Fix…", "Ctrl+OemPeriod", LanguageCommands.QuickFixAsync);
        Register(CommandIds.FormatDocument, "Edit", "Format Document", "Shift+Alt+F", LanguageCommands.FormatDocumentAsync);
        Register(CommandIds.OpenPreview, "View", "Open Preview (Markdown, SVG)", "Ctrl+Shift+V", OpenPreviewAsync);
        Register(CommandIds.OpenPreviewSource, "View", "Open Preview Source", null, OpenPreviewSourceAsync);
        Register(CommandIds.RestartLanguageServers, "Languages", "Restart Language Servers", null, Languages.RestartAsync);
    }

    private void Register(string id, string category, string title, string? keys, Func<Task> execute) =>
        Commands.Register(new CommandDefinition(id, category, title, keys, () => RunSafelyAsync(execute)));

    private void Register(string id, string category, string title, string? keys, Action execute) =>
        Register(id, category, title, keys, () =>
        {
            execute();
            return Task.CompletedTask;
        });

    private async Task SaveActiveAsync(bool chooseLocation)
    {
        if (Editors.ActiveEditor is not { } tab)
        {
            return;
        }

        if (await Editors.SaveAsync(tab, chooseLocation))
        {
            StatusBar.Message = $"Saved {tab.Title}";
        }
    }

    /// <summary>File errors become a status message instead of crashing the app.</summary>
    private async Task RunSafelyAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusBar.Message = exception.Message;
        }
    }
}

/// <summary>What to do with a picked stash.</summary>
public enum StashAction
{
    Apply,
    Pop,
    Drop,
}
