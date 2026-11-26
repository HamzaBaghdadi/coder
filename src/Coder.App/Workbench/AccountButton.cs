using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Coder.App.Controls;
using Coder.App.Mvvm;
using Coder.App.Theme;
using Coder.Presentation.Commands;
using Coder.Presentation.Mvvm;
using Coder.Presentation.Settings;
using Coder.Presentation.Workbench;

namespace Coder.App.Workbench;

/// <summary>
/// The account, in the title bar beside the cog (like VS Code's Accounts): a person icon while signed out,
/// the GitHub picture once signed in. It opens a small panel: sign in with GitHub, or who is signed in,
/// how Settings Sync is doing, and Sync Now / Turn Off Sync / Sign Out.
/// </summary>
public sealed class AccountButton : Panel
{
    private const double PanelWidth = 300;
    private const double ButtonAvatarSize = 20;

    private readonly WorkbenchViewModel _workbench;
    private readonly SettingsSyncViewModel _sync;
    private readonly FlatButton _button;
    private readonly Popup _popup;
    private readonly ChamferBorder _panel;
    private (string Path, DateTime Written, Bitmap Image)? _avatar;

    public AccountButton(WorkbenchViewModel workbench, SettingsSyncViewModel sync)
    {
        _workbench = workbench;
        _sync = sync;
        VerticalAlignment = VerticalAlignment.Center;
        _button = new FlatButton(Icons.Account, "Accounts") { Command = new RelayCommand(Toggle) };
        WindowDecorationProperties.SetElementRole(_button, WindowDecorationsElementRole.User);

        _panel = new ChamferBorder
        {
            Width = PanelWidth,
            Background = Palette.Elevated,
            BorderBrush = Palette.BorderStrong,
            BorderThickness = 1,
            Chamfer = Spacing.ChamferSmall,
            Padding = new Thickness(Spacing.XS),
        };
        _popup = new Popup
        {
            PlacementTarget = _button,
            Placement = PlacementMode.BottomEdgeAlignedRight,
            VerticalOffset = 4,
            IsLightDismissEnabled = true,
            Child = _panel,
        };

        Children.Add(_button);
        Children.Add(_popup);
        UpdateButton();
        sync.PropertyChanged += (_, _) =>
        {
            UpdateButton();
            if (_popup.IsOpen)
            {
                _panel.Child = BuildPanel(); // the code arrived, sign-in finished, a sync ran…
            }
        };
    }

    public bool IsOpen => _popup.IsOpen;

    /// <summary>What the title bar shows: the person icon, or the round picture (tests).</summary>
    internal object? Face => _button.Content;

    /// <summary>Opens or closes the account panel (tests, and the button).</summary>
    public void Toggle()
    {
        if (_popup.IsOpen)
        {
            _popup.IsOpen = false;
            return;
        }

        _panel.Child = BuildPanel();
        _popup.IsOpen = true;
    }

    private void UpdateButton()
    {
        var name = _sync.IsSignedIn ? $"Accounts: signed in as {_sync.Account}" : _sync.IsSigningIn ? "Accounts: signing in…" : "Accounts: sign in to sync settings";
        AutomationProperties.SetName(_button, "Accounts");
        AutomationProperties.SetHelpText(_button, name);
        ToolTip.SetTip(_button, name);
        _button.Content = _sync.IsSignedIn ? Avatar(ButtonAvatarSize) : new Icon(Icons.Account, 16);
    }

    /// <summary>The panel as it would show now. Internal for tests.</summary>
    internal Control BuildPanel()
    {
        var content = new StackPanel { Spacing = 2 };
        if (_sync.IsSigningIn)
        {
            content.Children.Add(Header(new Icon(Icons.Account, 28) { Foreground = Palette.TextSecondary }, "Signing in to GitHub", "Finish in your browser"));
            if (_sync.UserCode is { } code)
            {
                content.Children.Add(Text("Enter this code on github.com/login/device:", Palette.TextSecondary));
                var codeText = new SelectableTextBlock
                {
                    Text = code,
                    FontFamily = Typography.Code,
                    FontSize = 22,
                    FontWeight = FontWeight.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, Spacing.S),
                };
                AutomationProperties.SetName(codeText, "Sign-in code");
                content.Children.Add(codeText);
            }

            content.Children.Add(Separator());
            content.Children.Add(Row(Icons.Close, "Cancel Sign-in", CommandIds.SettingsSyncCancelSignIn));
        }
        else if (!_sync.IsSignedIn)
        {
            content.Children.Add(Header(new Icon(Icons.Account, 28) { Foreground = Palette.TextSecondary }, "Not signed in", "Accounts"));
            content.Children.Add(Text("Sign in with GitHub and your settings and extensions follow you to every machine you use Coder on.", Palette.TextSecondary));
            var signIn = new FlatButton
            {
                Classes = { "primary" },
                Content = _sync.IsAvailable ? "Sign in with GitHub" : "Not available in this build",
                IsEnabled = _sync.IsAvailable,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Padding = new Thickness(Spacing.M, 6),
                Margin = new Thickness(Spacing.S, Spacing.S, Spacing.S, Spacing.S),
                Command = new RelayCommand(() => Run(CommandIds.SettingsSyncSignIn)),
            };
            AutomationProperties.SetName(signIn, "Sign in with GitHub to Sync Settings");
            content.Children.Add(signIn);
        }
        else
        {
            content.Children.Add(Header(Avatar(40), _sync.DisplayName ?? _sync.Account!, $"{_sync.Account} · GitHub"));
            content.Children.Add(Separator());
            var on = _sync.IsSyncOn;
            var status = new DockPanel { Margin = new Thickness(Spacing.S, Spacing.XS, Spacing.S, 0) };
            status.Children.Add(new Border
            {
                Width = 8,
                Height = 8,
                CornerRadius = new CornerRadius(4),
                Background = on ? Palette.Accent : Palette.TextMuted,
                Margin = new Thickness(0, 0, Spacing.S, 0),
                VerticalAlignment = VerticalAlignment.Center,
            }.Dock(Dock.Left));
            status.Children.Add(new TextBlock { Text = _sync.IsSyncing ? "Syncing…" : on ? "Settings Sync is on" : "Settings Sync is off", FontWeight = FontWeight.SemiBold });
            content.Children.Add(status);
            content.Children.Add(Text(_sync.StatusText, Palette.TextMuted, Typography.CaptionSize));
            content.Children.Add(Separator());
            if (on)
            {
                content.Children.Add(Row(Icons.Sync, "Sync Now", CommandIds.SettingsSyncNow, enabled: !_sync.IsSyncing));
                content.Children.Add(Row(Icons.Close, "Turn Off Sync", CommandIds.SettingsSyncTurnOff));
            }
            else
            {
                content.Children.Add(Row(Icons.Sync, "Turn On Sync", CommandIds.SettingsSyncTurnOn));
            }

            content.Children.Add(Row(Icons.SignOut, "Sign Out", CommandIds.SettingsSyncSignOut));
        }

        return content;
    }

    /// <summary>The picture, round; the login's initial on the accent color until it is downloaded.</summary>
    private Control Avatar(double size)
    {
        var circle = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2),
            ClipToBounds = true,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (LoadAvatar() is { } image)
        {
            circle.Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
        }
        else
        {
            circle.Background = Palette.Accent;
            circle.Child = new TextBlock
            {
                Text = _sync.Initial,
                Foreground = Palette.TextOnAccent,
                FontWeight = FontWeight.SemiBold,
                FontSize = size * 0.5,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        return circle;
    }

    /// <summary>The downloaded picture, read once per change of the file.</summary>
    private Bitmap? LoadAvatar()
    {
        if (_sync.AvatarPath is not { } path)
        {
            return null;
        }

        try
        {
            var written = File.GetLastWriteTimeUtc(path);
            if (_avatar is not { } cached || cached.Path != path || cached.Written != written)
            {
                _avatar?.Image.Dispose();
                _avatar = (path, written, new Bitmap(path));
            }

            return _avatar.Value.Image;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return null; // unreadable picture: the initial stands in
        }
    }

    private static Control Header(Control picture, string title, string subtitle)
    {
        picture.Margin = new Thickness(0, 0, Spacing.M, 0);
        var header = new DockPanel { Margin = new Thickness(Spacing.S, Spacing.S, Spacing.S, Spacing.S) };
        header.Children.Add(picture.Dock(Dock.Left));
        header.Children.Add(new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock { Text = subtitle, Foreground = Palette.TextSecondary, FontSize = Typography.SmallSize, TextTrimming = TextTrimming.CharacterEllipsis },
            },
        });
        return header;
    }

    private static TextBlock Text(string text, IBrush foreground, double size = Typography.SmallSize) => new()
    {
        Text = text,
        Foreground = foreground,
        FontSize = size,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(Spacing.S, 0, Spacing.S, Spacing.XS),
    };

    private static Border Separator() => new() { Height = 1, Background = Palette.Border, Margin = new Thickness(0, Spacing.XS) };

    /// <summary>A menu-like row: icon and words; it closes the panel and runs the command.</summary>
    private FlatButton Row(Geometry icon, string text, string command, bool enabled = true)
    {
        var row = new FlatButton
        {
            Height = 28,
            IsEnabled = enabled,
            Padding = new Thickness(Spacing.S, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = new DockPanel
            {
                Children =
                {
                    new Icon(icon, 14) { Margin = new Thickness(0, 0, Spacing.S, 0), Foreground = Palette.TextSecondary }.Dock(Dock.Left),
                    new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center },
                },
            },
            Command = new RelayCommand(() => Run(command)),
        };
        AutomationProperties.SetName(row, text);
        return row;
    }

    private void Run(string command)
    {
        // Signing in keeps the panel open: it shows the code while GitHub waits.
        if (command != CommandIds.SettingsSyncSignIn)
        {
            _popup.IsOpen = false;
        }

        _ = _workbench.Commands.ExecuteAsync(command);
    }
}
