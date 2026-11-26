using System.Linq.Expressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Coder.App.Controls;
using Coder.App.Mvvm;
using Coder.App.Theme;
using Coder.Presentation.Workbench;

namespace Coder.App.Workbench;

/// <summary>Bottom bar: branch, problem counts and messages on the left; document facts (position, indent, encoding…) on the right.</summary>
public sealed class StatusBarView : Border
{
    public StatusBarView(WorkbenchViewModel workbench)
    {
        var status = workbench.StatusBar;
        Height = Spacing.StatusBarHeight;
        Background = Palette.StatusBar;
        BorderBrush = Palette.Border;
        BorderThickness = new Thickness(0, 1, 0, 0);
        TextElement.SetFontSize(this, Typography.SmallSize);
        TextElement.SetForeground(this, Palette.TextSecondary);

        var languages = workbench.Languages;
        var problems = new FlatButton
        {
            Padding = new Thickness(4, 0),
            Height = 20,
            Chamfer = 0,
            VerticalAlignment = VerticalAlignment.Center,
            Command = workbench.Commands.ToCommand(Presentation.Commands.CommandIds.ShowProblems),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Children =
                {
                    new Icon(Icons.Error, 13),
                    new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) }
                        .Bind(TextBlock.TextProperty, languages, x => x.ErrorCount, count => count.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    new Icon(Icons.Warning, 13),
                    new TextBlock { VerticalAlignment = VerticalAlignment.Center }
                        .Bind(TextBlock.TextProperty, languages, x => x.WarningCount, count => count.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                },
            },
        };
        Avalonia.Automation.AutomationProperties.SetName(problems, "Problems");
        ToolTip.SetTip(problems, "Problems (Ctrl+Shift+M)");

        var message = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = Palette.TextPrimary }
            .Bind(TextBlock.TextProperty, status, x => x.Message);

        var facts = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Spacing.L,
            Children =
            {
                Blame(workbench.SourceControl.Blame),
                new TextBlock { VerticalAlignment = VerticalAlignment.Center }.Bind(TextBlock.TextProperty, languages, x => x.StatusText),
                Fact(status, x => x.CursorText),
                Fact(status, x => x.IndentText),
                Fact(status, x => x.EncodingText),
                Fact(status, x => x.LineEndingText),
                Fact(status, x => x.LanguageText),
            },
        }.Bind(IsVisibleProperty, status, x => x.HasEditor);

        Child = new DockPanel
        {
            Margin = new Thickness(Spacing.M, 0),
            Children =
            {
                UpdatePill(workbench).Dock(Dock.Right),
                facts.Dock(Dock.Right),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = Spacing.L, Children = { Branch(workbench), problems, message } },
            },
        };
    }

    /// <summary>Once a new version is downloaded: "Restart to Update" in the accent color. Nothing before that.</summary>
    private static FlatButton UpdatePill(WorkbenchViewModel workbench)
    {
        var updates = workbench.Updates;
        var button = new FlatButton
        {
            Classes = { "primary" },
            Padding = new Thickness(8, 0),
            Height = 18,
            Margin = new Thickness(Spacing.L, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Command = workbench.Commands.ToCommand(Presentation.Commands.CommandIds.RestartToUpdate),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 5,
                Children =
                {
                    new Icon(Icons.Refresh, 12) { Foreground = Palette.TextOnAccent },
                    new TextBlock { Text = "Restart to Update", VerticalAlignment = VerticalAlignment.Center, Foreground = Palette.TextOnAccent, FontSize = Typography.CaptionSize, FontWeight = FontWeight.SemiBold },
                },
            },
        }.Bind(IsVisibleProperty, updates, x => x.IsReady);
        Avalonia.Automation.AutomationProperties.SetName(button, "Restart to Update");
        ToolTip.SetTip(button, "A new version of Coder is downloaded. Restart to use it now, or it installs when you close Coder.");
        updates.PropertyChanged += (_, _) => ToolTip.SetTip(button, $"Coder {updates.Version} is downloaded. Restart to use it now, or it installs when you close Coder.");
        return button;
    }

    /// <summary>The current branch (with ahead/behind arrows); clicking it opens Source Control.</summary>
    private static FlatButton Branch(WorkbenchViewModel workbench)
    {
        var sourceControl = workbench.SourceControl;
        var button = new FlatButton
        {
            Padding = new Thickness(4, 0),
            Height = 20,
            Chamfer = 0,
            VerticalAlignment = VerticalAlignment.Center,
            Command = workbench.Commands.ToCommand(Presentation.Commands.CommandIds.GitCheckout),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Children =
                {
                    new Icon(Icons.Branch, 13),
                    new TextBlock { VerticalAlignment = VerticalAlignment.Center }.Bind(TextBlock.TextProperty, sourceControl, x => x.BranchText),
                },
            },
        }.Bind(IsVisibleProperty, sourceControl, x => x.BranchText, text => text.Length > 0);
        Avalonia.Automation.AutomationProperties.SetName(button, "Branch");
        ToolTip.SetTip(button, "Switch or create a branch");
        return button;
    }

    /// <summary>Who last changed the caret's line ("You, 2 days ago"); the tooltip has the commit.</summary>
    private static StackPanel Blame(Presentation.SourceControl.BlameViewModel blame)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Background = Brushes.Transparent,
            Children =
            {
                new Icon(Icons.Commit, 13) { VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { VerticalAlignment = VerticalAlignment.Center }.Bind(TextBlock.TextProperty, blame, x => x.StatusText),
            },
        }.Bind(IsVisibleProperty, blame, x => x.StatusText, text => text.Length > 0);
        panel.Bind(ToolTip.TipProperty, blame, x => x.StatusDetail);
        Avalonia.Automation.AutomationProperties.SetName(panel, "Line Blame");
        return panel;
    }

    private static TextBlock Fact(StatusBarViewModel status, Expression<Func<StatusBarViewModel, string>> path) =>
        new TextBlock { VerticalAlignment = VerticalAlignment.Center }.Bind(TextBlock.TextProperty, status, path);
}
