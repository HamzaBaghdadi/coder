using Avalonia;
using Avalonia.Controls;
using Coder.App.Mvvm;
using Coder.App.Theme;
using Coder.Presentation.Workbench;

namespace Coder.App.Workbench;

/// <summary>Hosts the side bar views and shows the active one. Hidden views keep their state.</summary>
public sealed class SideBar : Border
{
    public SideBar(WorkbenchViewModel workbench)
    {
        Background = Palette.SideBar;
        BorderBrush = Palette.Border;
        BorderThickness = new Thickness(0, 0, 1, 0);
        Child = new Panel
        {
            Children =
            {
                new ExplorerView(workbench).Bind(IsVisibleProperty, workbench, x => x.ActiveSideBarView, view => view == SideBarView.Explorer),
                new SearchView(workbench.Search).Bind(IsVisibleProperty, workbench, x => x.ActiveSideBarView, view => view == SideBarView.Search),
                new SourceControlView(workbench.SourceControl).Bind(IsVisibleProperty, workbench, x => x.ActiveSideBarView, view => view == SideBarView.SourceControl),
                new ExtensionsView(workbench.Extensions).Bind(IsVisibleProperty, workbench, x => x.ActiveSideBarView, view => view == SideBarView.Extensions),
            },
        };
    }
}
