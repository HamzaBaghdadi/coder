using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Coder.App.Controls;
using Coder.App.Mvvm;
using Coder.App.Theme;
using Coder.Presentation.Mvvm;
using Coder.Presentation.Workbench;

namespace Coder.App.Workbench;

/// <summary>
/// The panel under the editors. Its top bar switches between PROBLEMS, TERMINAL and REFERENCES, shows the active
/// view's own buttons (the terminal's new and kill) at the right, and closes the panel.
/// </summary>
public sealed class BottomPanel : DockPanel
{
    public BottomPanel(WorkbenchViewModel workbench)
    {
        Background = TerminalPalette.Background;
        var terminal = new TerminalPanel(workbench);
        var problems = new ProblemsView(workbench.Problems);

        bool IsActive(WorkbenchViewModel w, PanelView view) => w.ActivePanel == view;
        terminal.Bind(IsVisibleProperty, workbench, x => x.ActivePanel, view => view == PanelView.Terminal);
        terminal.Toolbar.Bind(IsVisibleProperty, workbench, x => x.ActivePanel, view => view == PanelView.Terminal);
        problems.Bind(IsVisibleProperty, workbench, x => x.ActivePanel, view => view == PanelView.Problems);

        var problemsTab = ViewTab("PROBLEMS", () => workbench.ShowPanel(PanelView.Problems))
            .BindClass("active", workbench, w => IsActive(w, PanelView.Problems));
        var countBadge = new TextBlock { FontSize = Typography.CaptionSize, Foreground = Palette.TextSecondary, VerticalAlignment = VerticalAlignment.Center }
            .Bind(TextBlock.TextProperty, workbench.Problems, x => x.Count, count => count == 0 ? string.Empty : count.ToString(CultureInfo.InvariantCulture));
        ((StackPanel)problemsTab.Content!).Children.Add(countBadge);
        var terminalTab = ViewTab("TERMINAL", () => workbench.ShowPanel(PanelView.Terminal))
            .BindClass("active", workbench, w => IsActive(w, PanelView.Terminal));
        var referencesTab = ViewTab("REFERENCES", () => workbench.ShowPanel(PanelView.References))
            .BindClass("active", workbench, w => IsActive(w, PanelView.References));
        var references = new ReferencesView(workbench.References);
        references.Bind(IsVisibleProperty, workbench, x => x.ActivePanel, view => view == PanelView.References);

        var header = new Border
        {
            Height = 34,
            Padding = new Thickness(Spacing.S, 0, Spacing.S, 0),
            BorderBrush = Palette.Border,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Background = Palette.SideBar,
            Child = new DockPanel
            {
                Children =
                {
                    new FlatButton(Icons.Close, "Hide Panel") { Command = new RelayCommand(() => workbench.IsPanelVisible = false) }.Dock(Dock.Right),
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(0, 0, Spacing.M, 0), Children = { problemsTab, terminalTab, referencesTab } }.Dock(Dock.Left),
                    terminal.Toolbar,
                },
            },
        };

        Children.Add(header.Dock(Dock.Top));
        Children.Add(new Panel { Children = { terminal, problems, references } });
    }

    /// <summary>A caption-style tab; the active one is bright and underlined with the accent.</summary>
    private static FlatButton ViewTab(string title, Action show)
    {
        var tab = new FlatButton
        {
            Height = 26,
            Padding = new Thickness(Spacing.S, 0),
            Command = new RelayCommand(show),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new TextBlock
                    {
                        Text = title,
                        FontSize = Typography.CaptionSize,
                        FontWeight = FontWeight.SemiBold,
                        LetterSpacing = 0.6,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                },
            },
        };
        AutomationProperties.SetName(tab, title[0] + title[1..].ToLowerInvariant());
        return tab;
    }
}
