using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Coder.App.Mvvm;
using Coder.App.Terminal;
using Coder.App.Theme;
using Coder.Core.Terminal;
using Coder.Presentation.Workbench;

namespace Coder.App.Workbench;

/// <summary>
/// An extension's tab in the secondary side bar: the terminal its program opened there (Claude Code's own
/// interface), filling the tab. Until then a short "starting" line; shown for the first time, it asks the
/// extension to open it. When the program inside exits, Enter asks again.
/// </summary>
public sealed class ExtensionTerminalView : Panel
{
    private readonly ExtensionSideBarView _view;
    private readonly WorkbenchViewModel _workbench;
    private readonly TerminalPane _pane = new();
    private readonly TextBlock _status;

    /// <summary>The terminal, with the request's message above it (the console clears the screen when the shell starts, so it can't go inside).</summary>
    private readonly DockPanel _terminalArea;
    private readonly Border _banner;
    private readonly SelectableTextBlock _bannerText = new()
    {
        TextWrapping = TextWrapping.Wrap,
        FontSize = Typography.SmallSize,
        Foreground = Palette.TextPrimary,
    };

    private TerminalSession? _session;
    private bool _activated;
    private bool _requested;

    public ExtensionTerminalView(ExtensionSideBarView view, WorkbenchViewModel workbench)
    {
        _view = view;
        _workbench = workbench;

        _pane.View.SetFont(workbench.TerminalFontFamily, workbench.TerminalFontSize);
        _pane.View.RestartRequested += (_, _) => _ = view.ActivateAsync(); // the extension starts it again, its way
        _status = new TextBlock
        {
            Text = $"Starting {view.Title}…",
            Foreground = Palette.TextSecondary,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(Spacing.L),
        };
        _banner = new Border
        {
            IsVisible = false,
            Background = Palette.SideBar,
            BorderBrush = Palette.Border,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(Spacing.M, Spacing.S),
            Child = _bannerText,
        };
        _terminalArea = new DockPanel { IsVisible = false, Children = { _banner.Dock(Dock.Top), _pane } };
        Children.Add(_status);
        Children.Add(_terminalArea);
        view.TerminalRequested += (_, request) => Dispatcher.UIThread.Post(() => Start(request));
        view.FocusRequested += (_, _) => Dispatcher.UIThread.Post(() => _pane.View.Focus());
        workbench.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(WorkbenchViewModel.TerminalFontFamily) or nameof(WorkbenchViewModel.TerminalFontSize))
            {
                _pane.View.SetFont(workbench.TerminalFontFamily, workbench.TerminalFontSize);
            }
        };
    }

    /// <summary>The running session, for tests.</summary>
    internal TerminalSession? Session => _session;

    /// <summary>Called when the tab is shown: the first time, the extension is asked to open its terminal.</summary>
    public void OnShown()
    {
        if (_session is not null)
        {
            Dispatcher.UIThread.Post(() => _pane.View.Focus(), DispatcherPriority.Background);
            return;
        }

        if (!_activated)
        {
            _activated = true;
            _ = ActivateAsync();
        }
    }

    private async Task ActivateAsync()
    {
        try
        {
            await _view.ActivateAsync();
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            ShowStatus(exception.Message);
        }

        // The program asks for the terminal in its own time (its answer and the request may cross): give it a moment.
        await Task.Delay(TimeSpan.FromSeconds(3));
        if (_session is null && !_requested)
        {
            _activated = false; // nothing opened (refused, or failed): showing the tab again retries
            ShowStatus($"{_view.Title} didn't start. Show this tab again to retry.");
        }
    }

    private void ShowStatus(string text)
    {
        _status.Text = text;
        _status.IsVisible = _session is null;
        _terminalArea.IsVisible = _session is not null;
    }

    /// <summary>Shows the terminal area first, so the shell starts at the size it will have (a program inside sees the right width).</summary>
    private void Start(TerminalRequest request)
    {
        _requested = true;
        _terminalArea.IsVisible = true;
        _status.IsVisible = false;
        Dispatcher.UIThread.Post(() => StartSized(request), DispatcherPriority.Background); // after layout
    }

    private void StartSized(TerminalRequest request)
    {
        if (!PseudoTerminal.IsSupported)
        {
            ShowStatus("The integrated terminal needs Windows 10 (version 1809) or later for now.");
            return;
        }

        TerminalSession session;
        try
        {
            var (columns, rows) = Bounds.Width > 0 ? _pane.View.GridSize : (80, 24);
            var folder = request.WorkingDirectory is { } cwd && Directory.Exists(cwd) ? cwd : _workbench.TerminalWorkingDirectory;
            var profile = _workbench.DefaultShellProfile;
            var commandLine = request.ProgramCommandLine ?? (profile is null ? null : ShellIntegration.CommandLine(profile));
            session = TerminalSession.Start(folder, columns, rows, commandLine, request.Environment);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException or PlatformNotSupportedException)
        {
            ShowStatus($"Couldn't start a shell: {exception.Message}");
            return;
        }

        var previous = _session;
        _session = session;
        _view.HasTerminal = true;
        session.Exited += (_, code) => Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(_session, session))
            {
                _view.HasTerminal = false;
                session.Emulator.Feed($"\r\n\x1b[2m[{request.Name} ended (code {code}). Press Enter to start it again.]\x1b[0m");
            }
        });
        _bannerText.Text = request.Message;
        _banner.IsVisible = request.Message is { Length: > 0 };

        _pane.View.Session = session;
        _terminalArea.IsVisible = true;
        _status.IsVisible = false;
        previous?.Dispose();
        if (request.Program is null && request.CommandLine is { Length: > 0 } command)
        {
            session.Write(command + "\r");
        }

        Dispatcher.UIThread.Post(() => _pane.View.Focus(), DispatcherPriority.Background);
    }

    /// <summary>Ends the terminal (the extension was uninstalled, or Coder closes).</summary>
    public void Close()
    {
        _session?.Dispose();
        _session = null;
        _view.HasTerminal = false;
    }
}
