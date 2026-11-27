using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Coder.App.Controls;
using Coder.App.Mvvm;
using Coder.App.Theme;
using Coder.Presentation.Mvvm;
using Coder.Presentation.Workbench;

namespace Coder.App.Workbench;

/// <summary>
/// The panel under the editors. Its top bar switches between Problems, Terminal and References, shows the active
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

        var problemsTab = ViewTab("Problems", () => workbench.ShowPanel(PanelView.Problems))
            .BindClass("active", workbench, w => IsActive(w, PanelView.Problems));
        var countBadge = new Border
        {
            Background = Palette.Pressed,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(6, 0),
            MinWidth = 18,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { FontSize = Typography.CaptionSize, Foreground = Palette.TextSecondary, HorizontalAlignment = HorizontalAlignment.Center }
                .Bind(TextBlock.TextProperty, workbench.Problems, x => x.Count, count => count.ToString(CultureInfo.InvariantCulture)),
        }.Bind(IsVisibleProperty, workbench.Problems, x => x.Count, count => count > 0);
        ((StackPanel)problemsTab.Content!).Children.Add(countBadge);
        var terminalTab = ViewTab("Terminal", () => workbench.ShowPanel(PanelView.Terminal))
            .BindClass("active", workbench, w => IsActive(w, PanelView.Terminal));
        var referencesTab = ViewTab("References", () => workbench.ShowPanel(PanelView.References))
            .BindClass("active", workbench, w => IsActive(w, PanelView.References));
        var references = new ReferencesView(workbench.References);
        references.Bind(IsVisibleProperty, workbench, x => x.ActivePanel, view => view == PanelView.References);

        var header = new Border
        {
            Height = 36,
            Padding = new Thickness(Spacing.XS + Spacing.XXS, 0, Spacing.XS, 0),
            BorderBrush = Palette.Border,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new DockPanel
            {
                Children =
                {
                    new FlatButton(Icons.Close, "Hide Panel") { Command = new RelayCommand(() => workbench.IsPanelVisible = false) }.Dock(Dock.Right),
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = Spacing.XXS, Margin = new Thickness(0, 0, Spacing.M, 0), Children = { problemsTab, terminalTab, referencesTab } }.Dock(Dock.Left),
                    terminal.Toolbar,
                },
            },
        };

        Children.Add(header.Dock(Dock.Top));
        Children.Add(new Panel { Children = { terminal, problems, references } });
    }

    /// <summary>A text tab; the active one is a filled pill in primary text (see the "panel-tab" styles).</summary>
    private static FlatButton ViewTab(string title, Action show)
    {
        var tab = new FlatButton
        {
            Height = 26,
            Padding = new Thickness(Spacing.S + Spacing.XXS, 0),
            Command = new RelayCommand(show),
            Classes = { "panel-tab" },
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        AutomationProperties.SetName(tab, title);
        return tab;
    }
}
