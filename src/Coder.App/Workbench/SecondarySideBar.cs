using System.Collections.Specialized;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Coder.App.Controls;
using Coder.App.Mvvm;
using Coder.App.Theme;
using Coder.App.Workbench.Agent;
using Coder.Presentation.Mvvm;
using Coder.Presentation.Workbench;

namespace Coder.App.Workbench;

/// <summary>
/// The side bar on the other side of the editors from the primary one. It holds Coder Agent, the chat (a
/// workbench without the agent, in tests, gets a calm placeholder instead), and the views extensions add
/// (Claude Code), each a tab in a row above them once there is more than one.
/// </summary>
public sealed class SecondarySideBar : Border
{
    private readonly WorkbenchViewModel _workbench;
    private readonly Border _agentHost = new();
    private readonly Panel _content = new();
    private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal, Spacing = Spacing.XXS, Margin = new Thickness(Spacing.S, 0) };
    private readonly Border _tabRow;
    private readonly Dictionary<string, ExtensionTerminalView> _views = [];

    public SecondarySideBar(WorkbenchViewModel workbench)
    {
        _workbench = workbench;
        Classes.Add("panel"); // an inset, rounded panel on the shell, like the editors
        _tabRow = new Border
        {
            Height = 34,
            BorderBrush = Palette.Border,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = _tabs,
            IsVisible = false,
        };
        _content.Children.Add(_agentHost);
        Child = new DockPanel { Children = { _tabRow.Dock(Dock.Top), _content } };

        ShowAgent();
        workbench.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WorkbenchViewModel.Agent))
            {
                ShowAgent();
            }
            else if (e.PropertyName is nameof(WorkbenchViewModel.ActiveSecondaryView) or nameof(WorkbenchViewModel.IsSecondarySideBarVisible))
            {
                Select();
            }
        };
        workbench.SecondaryViews.CollectionChanged += (_, e) => OnViewsChanged(e);
        OnViewsChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    /// <summary>The chat, when the agent is on.</summary>
    public AgentView? Agent => _agentHost.Child as AgentView;

    /// <summary>An extension's view by id, once it exists (tests).</summary>
    internal ExtensionTerminalView? ExtensionView(string id) => _views.GetValueOrDefault(id);

    private void OnViewsChanged(NotifyCollectionChangedEventArgs e)
    {
        foreach (var id in _views.Keys.Where(id => _workbench.SecondaryViews.All(v => v.Id != id)).ToList())
        {
            _views[id].Close(); // its extension was uninstalled
            _content.Children.Remove(_views[id]);
            _views.Remove(id);
        }

        foreach (var view in _workbench.SecondaryViews.Where(v => !_views.ContainsKey(v.Id)))
        {
            var control = new ExtensionTerminalView(view, _workbench) { IsVisible = false };
            _views[view.Id] = control;
            _content.Children.Add(control);
        }

        RebuildTabs();
        Select();
    }

    /// <summary>Shows the active tab's content; an extension's view shown for the first time starts its terminal.</summary>
    private void Select()
    {
        var active = _workbench.ActiveSecondaryView;
        _agentHost.IsVisible = active is null;
        foreach (var (id, control) in _views)
        {
            control.IsVisible = id == active;
            if (id == active && _workbench.IsSecondarySideBarVisible)
            {
                control.OnShown();
            }
        }

        RebuildTabs();
    }

    private void RebuildTabs()
    {
        _tabRow.IsVisible = _workbench.SecondaryViews.Count > 0;
        _tabs.Children.Clear();
        if (!_tabRow.IsVisible)
        {
            return;
        }

        _tabs.Children.Add(Tab("Coder Agent", null));
        foreach (var view in _workbench.SecondaryViews)
        {
            _tabs.Children.Add(Tab(view.Title, view.Id));
        }
    }

    private FlatButton Tab(string title, string? id)
    {
        var selected = _workbench.ActiveSecondaryView == id;
        var tab = new FlatButton
        {
            Padding = new Thickness(Spacing.M, 0),
            VerticalAlignment = VerticalAlignment.Stretch,
            Radius = 0,
            Command = new RelayCommand(() => _workbench.ShowSecondaryView(id)),
            Content = new Border
            {
                BorderBrush = selected ? Palette.Accent : Brushes.Transparent,
                BorderThickness = new Thickness(0, 0, 0, 2),
                Child = new TextBlock
                {
                    Text = title,
                    FontSize = Typography.SmallSize,
                    FontWeight = selected ? Typography.Strong : FontWeight.Normal,
                    Foreground = selected ? Palette.TextPrimary : Palette.TextSecondary,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 8, 0, 6),
                },
            },
        };
        AutomationProperties.SetName(tab, title);
        return tab;
    }

    private void ShowAgent()
    {
        if (_workbench.Agent is { } agent)
        {
            if (_agentHost.Child is not AgentView)
            {
                _agentHost.Child = new AgentView(agent);
            }

            return;
        }

        _agentHost.Child = new DockPanel
        {
            Children =
            {
                SideBarHeader.Create("Secondary side bar").Dock(Dock.Top),
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
