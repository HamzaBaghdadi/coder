using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Coder.App.Controls;
using Coder.App.Mvvm;
using Coder.App.Terminal;
using Coder.App.Theme;
using Coder.Core.Terminal;
using Coder.Presentation.Mvvm;
using Coder.Presentation.Workbench;

namespace Coder.App.Workbench;

/// <summary>
/// The bottom panel with the integrated terminals, like VS Code's. Each terminal is its own shell with a
/// row in the list at the right. Terminals live in groups: a new terminal starts a group, and Split
/// Terminal adds a shell beside the active one, in the same group, shown side by side with a splitter
/// between them. Each terminal has a scroll bar for its history.
/// <para>
/// The first shell starts when the panel is first shown (in the open folder); shells keep running while
/// the panel is hidden. Killing the last terminal hides the panel, and Enter in a terminal whose shell
/// exited starts a new shell in its place.
/// </para>
/// </summary>
public sealed class TerminalPanel : DockPanel
{
    private const double ListWidth = 180;

    private readonly WorkbenchViewModel _workbench;
    private readonly TextBlock _message;
    private readonly StackPanel _messageView;
    private readonly Grid _panesHost = new();
    private readonly StackPanel _tabs = new() { Spacing = 1, Margin = new Thickness(4) };
    private readonly Border _list;
    private readonly List<TerminalInstance> _terminals = [];

    /// <summary>Reused for whichever group is shown: pane <c>i</c> shows the group's <c>i</c>-th terminal.</summary>
    private readonly List<TerminalPane> _panes = [];
    private TerminalGroup? _shownGroup;
    private int _shownCount;
    private TerminalInstance? _active;

    public TerminalPanel(WorkbenchViewModel workbench)
    {
        _workbench = workbench;
        workbench.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(WorkbenchViewModel.TerminalFontFamily) or nameof(WorkbenchViewModel.TerminalFontSize))
            {
                foreach (var pane in _panes)
                {
                    pane.View.SetFont(workbench.TerminalFontFamily, workbench.TerminalFontSize);
                }
            }
        };
        Background = TerminalPalette.Background;
        OpenUrl = async uri =>
        {
            if (TopLevel.GetTopLevel(this) is { } top)
            {
                await top.Launcher.LaunchUriAsync(uri);
            }
        };

        _message = new TextBlock
        {
            Classes = { "secondary" },
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
        };

        // Shown instead of the terminals when no shell can start: a quiet icon over one line of text.
        _messageView = new StackPanel
        {
            Margin = new Thickness(Spacing.L, Spacing.XL),
            Spacing = Spacing.S,
            MaxWidth = 480,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            IsVisible = false,
            Children =
            {
                new Icon(Icons.Terminal, 20) { Foreground = Palette.TextSecondary, HorizontalAlignment = HorizontalAlignment.Center },
                _message,
            },
        };

        Toolbar = CreateToolbar(() => AddTerminal(), workbench.ShowNewTerminalWithProfilePicker, () => Split(), KillActive);
        _list = CreateList();
        _list.IsVisible = false;
        Children.Add(_list.Dock(Dock.Right));
        Children.Add(new Panel { Children = { _panesHost, _messageView } });
        Pane(0); // one pane from the start, so the first shell knows its size

        workbench.NewTerminalRequested += (_, profile) => Dispatcher.UIThread.Post(() => AddTerminal(profile));
        workbench.TerminalAtFolderRequested += (_, folder) => Dispatcher.UIThread.Post(() => AddTerminal(folder: folder));
        workbench.TerminalRequested += (_, request) => Dispatcher.UIThread.Post(() => AddTerminal(request));
        workbench.SplitTerminalRequested += (_, _) => Dispatcher.UIThread.Post(() => Split());
        workbench.FindInTerminalRequested += (_, _) => Dispatcher.UIThread.Post(() => PaneOf(_active)?.FindBar.Open(), DispatcherPriority.Background);
        workbench.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WorkbenchViewModel.IsTerminalVisible) && workbench.IsTerminalVisible)
            {
                // After layout, so the shell starts with the panel's real size.
                Dispatcher.UIThread.Post(() =>
                {
                    if (_terminals.Count == 0)
                    {
                        AddTerminal();
                    }

                    PaneOf(_active)?.View.Focus();
                }, DispatcherPriority.Background);
            }
        };
    }

    /// <summary>The running terminals, in list order.</summary>
    internal IReadOnlyList<TerminalSession> Sessions => _terminals.Select(t => t.Session).ToList();

    internal TerminalSession? ActiveSession => _active?.Session;

    /// <summary>For tests: the panes on screen now (the active group's terminals, left to right).</summary>
    internal IReadOnlyList<TerminalPane> VisiblePanes => _panes.Take(_shownCount).ToList();

    /// <summary>The terminal's buttons (new, split, kill), shown in the bottom panel's top bar while the terminal is the active view.</summary>
    public Control Toolbar { get; }

    private static StackPanel CreateToolbar(Action add, Action pickProfile, Action split, Action kill) => new()
    {
        Orientation = Orientation.Horizontal,
        HorizontalAlignment = HorizontalAlignment.Right,
        Spacing = 2,
        Children =
        {
            new FlatButton(Icons.Plus, "New Terminal (Ctrl+Shift+`)") { Command = new RelayCommand(add) },
            new FlatButton(Icons.ChevronDown, "Launch Profile…", 13) { Width = 18, Command = new RelayCommand(pickProfile) },
            new FlatButton(Icons.SplitTerminal, "Split Terminal (Ctrl+Shift+5)") { Command = new RelayCommand(split) },
            new FlatButton(Icons.Trash, "Kill Terminal") { Command = new RelayCommand(kill) },
        },
    };

    /// <summary>The list of terminals at the right, one row per shell, like VS Code's terminal tabs.</summary>
    private Border CreateList() => new()
    {
        Width = ListWidth,
        BorderBrush = Palette.Border,
        BorderThickness = new Thickness(1, 0, 0, 0),
        Child = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = _tabs,
        },
    };

    // Terminals ---------------------------------------------------------------------------------

    /// <summary>Starts a new shell (the default one, or <paramref name="profile"/>) in the workspace folder, in a group of its own, and shows it.</summary>
    private void AddTerminal(ShellProfile? profile = null, string? folder = null)
    {
        if (CreateTerminal(new TerminalGroup(), profile ?? _workbench.DefaultShellProfile, folder) is { } terminal)
        {
            _terminals.Add(terminal);
            Activate(terminal);
        }
    }

    /// <summary>A terminal an extension asked for: named, with its variables, its command typed in once the shell starts.</summary>
    private void AddTerminal(TerminalRequest request)
    {
        if (CreateTerminal(new TerminalGroup(), _workbench.DefaultShellProfile, request.WorkingDirectory, request.Environment) is not { } terminal)
        {
            return;
        }

        terminal.Name = request.Name;
        _terminals.Add(terminal);
        Activate(terminal);
        if (request.CommandLine is { Length: > 0 } command)
        {
            terminal.Session.Write(command + "\r"); // the console keeps it until the shell reads input
        }
    }

    /// <summary>Starts a new shell beside the active one (Ctrl+Shift+5); with no terminal yet, just a new one.</summary>
    private void Split()
    {
        if (_active is not { } active)
        {
            AddTerminal();
            return;
        }

        var group = active.Group;
        if (CreateTerminal(group, active.Profile) is not { } terminal) // a split runs the same shell
        {
            return;
        }

        group.Terminals.Remove(terminal);
        group.Terminals.Insert(group.Terminals.IndexOf(active) + 1, terminal);
        _terminals.Insert(_terminals.IndexOf(active) + 1, terminal);
        Activate(terminal);
    }

    private TerminalInstance? CreateTerminal(TerminalGroup group, ShellProfile? profile, string? startFolder = null, IReadOnlyDictionary<string, string>? environment = null)
    {
        if (!PseudoTerminal.IsSupported)
        {
            ShowMessage("The integrated terminal needs Windows 10 (version 1809) or later for now. Support for macOS and Linux is planned.");
            return null;
        }

        // A split starts where the terminal it splits is (when that shell reports its folder).
        var folder = startFolder ?? (group.Terminals.Count > 0 ? _active?.Session.Emulator.CurrentDirectory : null);
        if (StartSession(PaneOf(_active) ?? Pane(0), profile, folder, environment) is not { } session)
        {
            return null;
        }

        var terminal = new TerminalInstance(session, group, profile) { Environment = environment };
        group.Terminals.Add(terminal);
        Watch(terminal);
        return terminal;
    }

    /// <summary>Enter after a shell exited: a new shell takes its place.</summary>
    private void Restart(TerminalInstance terminal)
    {
        if (StartSession(PaneOf(terminal) ?? Pane(0), terminal.Profile, environment: terminal.Environment) is not { } session)
        {
            return;
        }

        terminal.Session.Dispose();
        terminal.Session = session;
        terminal.Title = string.Empty;
        Watch(terminal);
        Activate(terminal);
    }

    private void KillActive()
    {
        if (_active is { } terminal)
        {
            Kill(terminal);
        }
    }

    /// <summary>Ends a shell. The next one shown is a neighbor in its group, else a neighbor in the list.</summary>
    private void Kill(TerminalInstance terminal)
    {
        var index = _terminals.IndexOf(terminal);
        var group = terminal.Group;
        var indexInGroup = group.Terminals.IndexOf(terminal);
        _terminals.Remove(terminal);
        group.Terminals.Remove(terminal);
        terminal.Session.Dispose();
        if (_terminals.Count == 0)
        {
            _active = null;
            ShowGroup(null);
            RebuildTabs();
            _workbench.IsTerminalVisible = false; // nothing left to show
            return;
        }

        if (!ReferenceEquals(terminal, _active))
        {
            ShowGroup(_active!.Group);
            RebuildTabs();
            return;
        }

        Activate(group.Terminals.Count > 0
            ? group.Terminals[Math.Min(indexInGroup, group.Terminals.Count - 1)]
            : _terminals[Math.Min(index, _terminals.Count - 1)]);
    }

    private void Activate(TerminalInstance terminal)
    {
        _active = terminal;
        _messageView.IsVisible = false;
        _panesHost.IsVisible = true;
        ShowGroup(terminal.Group);
        RebuildTabs();
        PaneOf(terminal)?.View.Focus();
    }

    /// <summary>Lays out the group's terminals side by side (the panes are reused), each with its shell.</summary>
    private void ShowGroup(TerminalGroup? group)
    {
        var terminals = group?.Terminals ?? [];
        var count = Math.Max(terminals.Count, group is null ? 0 : 1);
        if (!ReferenceEquals(group, _shownGroup) || count != _shownCount)
        {
            _panesHost.Children.Clear();
            _panesHost.ColumnDefinitions.Clear();
            for (var i = 0; i < count; i++)
            {
                if (i > 0)
                {
                    // A splitter between neighbors: a hairline, with a wider invisible grip over it.
                    _panesHost.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
                    var column = _panesHost.ColumnDefinitions.Count - 1;
                    var hairline = new Border { Width = 1, Background = Palette.Border, HorizontalAlignment = HorizontalAlignment.Center, IsHitTestVisible = false };
                    var splitter = new GridSplitter { Width = 5, Background = Brushes.Transparent, ResizeDirection = GridResizeDirection.Columns };
                    Grid.SetColumn(hairline, column);
                    Grid.SetColumn(splitter, column);
                    _panesHost.Children.Add(hairline);
                    _panesHost.Children.Add(splitter);
                }

                _panesHost.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star) { MinWidth = 120 });
                var pane = Pane(i);
                Grid.SetColumn(pane, _panesHost.ColumnDefinitions.Count - 1);
                _panesHost.Children.Add(pane);
            }

            _shownGroup = group;
            _shownCount = count;
        }

        for (var i = 0; i < _panes.Count; i++)
        {
            var session = i < terminals.Count ? terminals[i].Session : null;
            if (!ReferenceEquals(_panes[i].View.Session, session))
            {
                _panes[i].View.Session = session;
            }
        }
    }

    /// <summary>The pane at a position, created on first use.</summary>
    private TerminalPane Pane(int index)
    {
        while (_panes.Count <= index)
        {
            var pane = new TerminalPane();
            pane.View.SetFont(_workbench.TerminalFontFamily, _workbench.TerminalFontSize);
            var position = _panes.Count;
            pane.View.RestartRequested += (_, _) =>
            {
                if (TerminalAt(position) is { } terminal)
                {
                    Restart(terminal);
                }
            };

            // Links in the output: files open in the editor at the line they name, web addresses in the browser.
            pane.View.ResolveFile = path => ResolveFile(path, pane.View.Session?.Emulator.CurrentDirectory);
            pane.View.LinkActivated += async (_, link) => await OpenLinkAsync(link);

            // Clicking into a split terminal makes it the active one (for Kill, Split and the list).
            pane.View.GotFocus += (_, _) =>
            {
                if (TerminalAt(position) is { } terminal && !ReferenceEquals(terminal, _active))
                {
                    _active = terminal;
                    RebuildTabs();
                }
            };
            _panes.Add(pane);
        }

        return _panes[index];
    }

    /// <summary>Opens a web address (replaceable in tests, which must not start a browser).</summary>
    internal Func<Uri, Task> OpenUrl { get; set; }

    /// <summary>A printed path as an existing file: absolute, or relative to the shell's current folder (when it reports it), else the folder terminals start in.</summary>
    private string? ResolveFile(string path, string? shellFolder)
    {
        try
        {
            var folder = shellFolder is not null && Directory.Exists(shellFolder) ? shellFolder : _workbench.TerminalWorkingDirectory;
            var full = Path.IsPathFullyQualified(path) ? path : Path.GetFullPath(Path.Combine(folder, path));
            return File.Exists(full) ? full : null;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private async Task OpenLinkAsync(TerminalLinkTarget link)
    {
        if (link.Kind == TerminalLinkKind.Url)
        {
            if (Uri.TryCreate(link.Target, UriKind.Absolute, out var uri))
            {
                await OpenUrl(uri);
            }

            return;
        }

        // Printed positions count from 1; the editor's from 0. Without a line, the file just opens.
        if (link.Line is { } line)
        {
            var position = new Coder.Core.Text.TextPosition(Math.Max(0, line - 1), Math.Max(0, (link.Column ?? 1) - 1));
            await _workbench.RevealAsync(link.Target, new Coder.Core.Lsp.TextRange(position, position));
        }
        else
        {
            await _workbench.OpenFileAsync(link.Target);
        }
    }

    private TerminalInstance? TerminalAt(int position) =>
        _shownGroup is { } group && position < group.Terminals.Count ? group.Terminals[position] : null;

    private TerminalPane? PaneOf(TerminalInstance? terminal)
    {
        if (terminal is null || !ReferenceEquals(terminal.Group, _shownGroup))
        {
            return null;
        }

        var position = terminal.Group.Terminals.IndexOf(terminal);
        return position >= 0 && position < _panes.Count ? _panes[position] : null;
    }

    private TerminalSession? StartSession(TerminalPane sizeFrom, ShellProfile? profile, string? folder = null, IReadOnlyDictionary<string, string>? environment = null)
    {
        try
        {
            var (columns, rows) = sizeFrom.View.GridSize; // a new split pane is resized once laid out
            var start = folder is not null && Directory.Exists(folder) ? folder : _workbench.TerminalWorkingDirectory;
            return TerminalSession.Start(start, columns, rows, profile is null ? null : ShellIntegration.CommandLine(profile), environment);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException or PlatformNotSupportedException)
        {
            ShowMessage($"Couldn't start a shell: {exception.Message}");
            return null;
        }
    }

    private void Watch(TerminalInstance terminal)
    {
        var session = terminal.Session;
        session.TitleChanged += (_, title) =>
        {
            terminal.Title = DisplayTitle(title);
            RebuildTabs();
        };
        session.Exited += (_, code) =>
            session.Emulator.Feed($"\r\n\x1b[2m[The shell exited with code {code}. Press Enter to start a new one.]\x1b[0m");
    }

    private void ShowMessage(string text)
    {
        _message.Text = text;
        _messageView.IsVisible = true;
        _panesHost.IsVisible = false;
    }

    // Terminal list ---------------------------------------------------------------------------------

    private void RebuildTabs()
    {
        _tabs.Children.Clear();
        for (var i = 0; i < _terminals.Count; i++)
        {
            _tabs.Children.Add(Row(_terminals[i], number: i + 1));
        }

        _list.IsVisible = _terminals.Count > 0;
    }

    /// <summary>
    /// One terminal in the list: its icon and "number: title". Terminals split together are joined by
    /// tree lines (┌ ├ └). The selected row is highlighted; the kill button shows on it and on hover.
    /// </summary>
    private FlatButton Row(TerminalInstance terminal, int number)
    {
        var isActive = ReferenceEquals(terminal, _active);
        var label = $"{number.ToString(CultureInfo.InvariantCulture)}: {terminal.Name ?? (terminal.Title.Length > 0 ? terminal.Title : terminal.Profile?.Name ?? "shell")}";
        var kill = new FlatButton(Icons.Trash, $"Kill Terminal {number}", 13)
        {
            Width = 20,
            Height = 20,
            Padding = new Thickness(0),
            Radius = Spacing.RadiusSmall.TopLeft,
            Command = new RelayCommand(() => Kill(terminal)),
            Opacity = isActive ? 1 : 0,
        };

        var content = new DockPanel { Children = { kill.Dock(Dock.Right) } };
        if (TreeLine(terminal) is { } line)
        {
            content.Children.Add(new TextBlock
            {
                Text = line,
                FontFamily = Typography.Code,
                Foreground = Palette.TextMuted,
                Margin = new Thickness(0, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
            }.Dock(Dock.Left));
        }

        content.Children.Add(new Icon(terminal.Group.Terminals.Count > 1 ? Icons.SplitTerminal : Icons.Terminal, 14)
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        }.Dock(Dock.Left));
        content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });

        var row = new FlatButton
        {
            Height = 28,
            Padding = new Thickness(Spacing.S, 0, Spacing.XS, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Command = new RelayCommand(() => Activate(terminal)),
            Content = content,
        };
        if (isActive)
        {
            row.Classes.Add("active");
            row.Background = Palette.Selected;
        }

        row.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsPointerOverProperty)
            {
                kill.Opacity = isActive || row.IsPointerOver ? 1 : 0;
            }
        };
        ToolTip.SetTip(row, terminal.Group.Terminals.Count > 1 ? $"{label} (split)" : label);
        AutomationProperties.SetName(row, $"Terminal {label}");
        return row;
    }

    /// <summary>The tree line before a terminal in a split group: ┌ first, ├ middle, └ last; null when alone.</summary>
    private static string? TreeLine(TerminalInstance terminal)
    {
        var members = terminal.Group.Terminals;
        if (members.Count < 2)
        {
            return null;
        }

        var position = members.IndexOf(terminal);
        return position == 0 ? "┌" : position == members.Count - 1 ? "└" : "├";
    }

    /// <summary>Shells often title themselves with their full executable path; the program name is enough.</summary>
    private static string DisplayTitle(string title) =>
        Path.IsPathFullyQualified(title) && File.Exists(title) ? Path.GetFileNameWithoutExtension(title) : title;

    /// <summary>Terminals shown together, side by side (one, until split).</summary>
    private sealed class TerminalGroup
    {
        public List<TerminalInstance> Terminals { get; } = [];
    }

    /// <summary>One terminal: its current shell, its group, and the title the shell gave itself. Terminals are numbered by position.</summary>
    private sealed class TerminalInstance(TerminalSession session, TerminalGroup group, ShellProfile? profile)
    {
        public TerminalSession Session { get; set; } = session;

        public TerminalGroup Group { get; } = group;

        /// <summary>The shell it runs (null: the default when it started).</summary>
        public ShellProfile? Profile { get; } = profile;

        public string Title { get; set; } = string.Empty;

        /// <summary>A name given when it was opened (an extension's terminal), shown instead of the shell's title.</summary>
        public string? Name { get; set; }

        /// <summary>Extra variables it was started with, kept when the shell restarts.</summary>
        public IReadOnlyDictionary<string, string>? Environment { get; init; }
    }
}
