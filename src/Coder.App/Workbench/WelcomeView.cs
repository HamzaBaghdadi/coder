using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Coder.App.Controls;
using Coder.App.Mvvm;
using Coder.App.Theme;
using Coder.Presentation.Commands;
using Coder.Presentation.Mvvm;
using Coder.Presentation.Workbench;

namespace Coder.App.Workbench;

/// <summary>
/// The empty editor state: the brand, and the handful of actions that get you started, each with its
/// shortcut so the keyboard path is learned from day one.
/// </summary>
public sealed class WelcomeView : Border
{
    public WelcomeView(WorkbenchViewModel workbench)
    {
        Background = Palette.EditorBackground;

        var actions = new StackPanel { Spacing = 2, Width = 380 };
        foreach (var id in new[] { CommandIds.OpenFolder, CommandIds.OpenFile, CommandIds.NewFile, CommandIds.QuickOpen, CommandIds.CommandPalette })
        {
            actions.Children.Add(ActionRow(workbench, workbench.Commands.Get(id)));
        }

        var content = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = Spacing.XL,
            Children =
            {
                new StackPanel
                {
                    Spacing = Spacing.XS,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Children =
                    {
                        GlowingLogo(88),
                        new TextBlock
                        {
                            Text = "Coder",
                            FontSize = Typography.DisplaySize,
                            FontWeight = Typography.Strong,
                            Foreground = Palette.TextPrimary,
                            Margin = new Thickness(0, Spacing.L, 0, 0),
                            HorizontalAlignment = HorizontalAlignment.Center,
                        },
                        new TextBlock
                        {
                            Text = "Fast, focused code editing.",
                            Classes = { "secondary" },
                            HorizontalAlignment = HorizontalAlignment.Center,
                        },
                    },
                },
                actions,
                RecentFolders(workbench),
            },
        };

        // The logo tile's faint diagonal light bands, layered behind the content. Dark themes only: the bands
        // are navy light, which on a light surface reads as gray smudges.
        var sheen = new Border { Background = Brand.DiagonalSheen, IsVisible = Palette.Current.IsDark };
        EventHandler follow = (_, _) => sheen.IsVisible = Palette.Current.IsDark;
        AttachedToVisualTree += (_, _) => { ThemeManager.Changed += follow; follow(null, EventArgs.Empty); };
        DetachedFromVisualTree += (_, _) => ThemeManager.Changed -= follow;
        Child = new Panel { Children = { sheen, content } };
    }

    /// <summary>The logo with a soft blue glow, echoing the lit edge of its tile.</summary>
    private static Border GlowingLogo(double size) => new()
    {
        Width = size,
        Height = size,
        HorizontalAlignment = HorizontalAlignment.Center,
        CornerRadius = new CornerRadius(size * 0.19),
        BoxShadow = BoxShadows.Parse("0 10 40 0 #592F7BFF"),
        Child = new LogoView(size),
    };

    /// <summary>Recently opened folders, one click to reopen. Hidden until there is history.</summary>
    private static StackPanel RecentFolders(WorkbenchViewModel workbench)
    {
        var list = new ItemsControl
        {
            ItemsSource = workbench.RecentFolders,
            ItemTemplate = Templates.Row<string>(path => RecentFolderRow(workbench, path)),
        };

        var section = new StackPanel
        {
            Width = 400,
            Spacing = Spacing.XS,
            Children =
            {
                new TextBlock
                {
                    Text = "Recent",
                    Classes = { "section-title" },
                    Margin = new Thickness(Spacing.M, 0, Spacing.M, Spacing.XXS),
                },
                list,
            },
        };

        void Update() => section.IsVisible = workbench.RecentFolders.Count > 0;
        Update();
        workbench.RecentFolders.CollectionChanged += (_, _) => Update();
        return section;
    }

    private static FlatButton RecentFolderRow(WorkbenchViewModel workbench, string path)
    {
        var button = new FlatButton
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(Spacing.M, 6),
            Foreground = Palette.TextPrimary,
            Command = new RelayCommand(() => workbench.OpenFolder(path)),
            Content = new DockPanel
            {
                Children =
                {
                    new Icon(Icons.Folder, 15) { Foreground = Palette.TextSecondary, Margin = new Thickness(0, 0, Spacing.S, 0) }.Dock(Dock.Left),
                    new TextBlock
                    {
                        Text = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)),
                        Foreground = Palette.TextPrimary,
                        VerticalAlignment = VerticalAlignment.Center,
                    }.Dock(Dock.Left),
                    new TextBlock
                    {
                        Text = Path.GetDirectoryName(path.TrimEnd(Path.DirectorySeparatorChar)) ?? string.Empty,
                        FontSize = Typography.SmallSize,
                        Foreground = Palette.TextSecondary,
                        Margin = new Thickness(Spacing.S, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        TextTrimming = TextTrimming.LeadingCharacterEllipsis,
                    },
                },
            },
        };
        AutomationProperties.SetName(button, $"Open recent folder {path}");
        ToolTip.SetTip(button, path);
        return button;
    }

    private static FlatButton ActionRow(WorkbenchViewModel workbench, CommandDefinition command)
    {
        var button = new FlatButton
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(Spacing.M, Spacing.S),
            Foreground = Palette.TextPrimary,
            Command = workbench.Commands.ToCommand(command.Id),
            Content = new DockPanel
            {
                Children =
                {
                    KeyHint(command.KeyBinding).Dock(Dock.Right),
                    new TextBlock { Text = command.Title.TrimEnd('…'), VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        AutomationProperties.SetName(button, command.Title);
        return button;
    }

    /// <summary>Renders "Ctrl+Shift+P" as separate key caps (the shared <c>kbd</c> class).</summary>
    internal static Control KeyHint(string? gesture)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Spacing.XS, VerticalAlignment = VerticalAlignment.Center };
        foreach (var key in (gesture ?? string.Empty).Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            panel.Children.Add(new Border
            {
                Classes = { "kbd" },
                MinWidth = 20,
                Height = 20,
                Child = new TextBlock
                {
                    Text = KeyLabel(key),
                    FontSize = Typography.CaptionSize,
                    Foreground = Palette.TextSecondary,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            });
        }

        return panel;
    }

    private static string KeyLabel(string key) => key switch
    {
        "OemPlus" => "+",
        "OemMinus" => "-",
        "D0" => "0",
        "OemTilde" => "`",
        "OemPeriod" => ".",
        "OemComma" => ",",
        _ => key,
    };
}
