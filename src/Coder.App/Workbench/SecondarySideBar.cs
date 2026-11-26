using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Coder.App.Controls;
using Coder.App.Mvvm;
using Coder.App.Theme;
using Coder.App.Workbench.Agent;
using Coder.Presentation.Workbench;

namespace Coder.App.Workbench;

/// <summary>
/// The side bar on the other side of the editors from the primary one. It holds Coder Agent, the chat;
/// a workbench without the agent (tests) gets a calm placeholder instead.
/// </summary>
public sealed class SecondarySideBar : Border
{
    public SecondarySideBar(WorkbenchViewModel workbench)
    {
        Background = Palette.SideBar;
        BorderBrush = Palette.Border;
        BorderThickness = new Thickness(1, 0, 0, 0);
        Show(workbench);
        workbench.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WorkbenchViewModel.Agent))
            {
                Show(workbench);
            }
        };
    }

    /// <summary>The chat, when the agent is on.</summary>
    public AgentView? Agent => Child as AgentView;

    private void Show(WorkbenchViewModel workbench)
    {
        if (workbench.Agent is { } agent)
        {
            if (Child is not AgentView)
            {
                Child = new AgentView(agent);
            }

            return;
        }

        Child = new DockPanel
        {
            Children =
            {
                SideBarHeader.Create("SECONDARY SIDE BAR").Dock(Dock.Top),
                new StackPanel
                {
                    Spacing = Spacing.S,
                    Margin = new Thickness(Spacing.L, Spacing.XL),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Children =
                    {
                        new Icon(Icons.LayoutSecondaryShown, 32) { Foreground = Palette.TextMuted, HorizontalAlignment = HorizontalAlignment.Center },
                        new TextBlock
                        {
                            Text = "Agents and other views will appear here.",
                            Foreground = Palette.TextSecondary,
                            TextWrapping = TextWrapping.Wrap,
                            TextAlignment = TextAlignment.Center,
                        },
                    },
                },
            },
        };
    }
}
