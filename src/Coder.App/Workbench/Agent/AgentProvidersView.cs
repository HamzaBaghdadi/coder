using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Coder.App.Controls;
using Coder.App.Mvvm;
using Coder.App.Theme;
using Coder.Core.Ai;
using Coder.Presentation.Agent;

namespace Coder.App.Workbench.Agent;

/// <summary>
/// Coder Agent's providers: each with its name, address and API key (saved encrypted for the Windows
/// account, never shown again), a connection test that lists its models, and buttons to add more.
/// </summary>
public sealed class AgentProvidersView : DockPanel
{
    public AgentProvidersView(AgentViewModel agent)
    {
        var back = new FlatButton
        {
            Padding = new Thickness(Spacing.S, 3),
            Command = agent.ToggleProvidersCommand,
            HorizontalAlignment = HorizontalAlignment.Left,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Children = { new Icon(Icons.ChevronRight, 12) { RenderTransform = new RotateTransform(180) }, new TextBlock { Text = "Back to Chat", FontSize = Typography.SmallSize } },
            },
        };
        AutomationProperties.SetName(back, "Back to Chat");

        var adders = new WrapPanel();
        foreach (var kind in new[] { AiProviderKind.FreeLlmApi, AiProviderKind.OpenRouter, AiProviderKind.Ollama, AiProviderKind.OpenAiCompatible })
        {
            var add = new FlatButton
            {
                Padding = new Thickness(Spacing.S, 3),
                Margin = new Thickness(0, 0, Spacing.XS, Spacing.XS),
                Classes = { "secondary" },
                Command = agent.AddProviderCommand,
                CommandParameter = kind,
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 4,
                    Children = { new Icon(Icons.Plus, 12), new TextBlock { Text = AiProviders.KindName(kind), FontSize = Typography.SmallSize } },
                },
            };
            AutomationProperties.SetName(add, $"Add {AiProviders.KindName(kind)} provider");
            adders.Children.Add(add);
        }

        var list = new ItemsControl
        {
            ItemsSource = agent.Providers,
            ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel { Spacing = Spacing.M }),
            ItemTemplate = Templates.Row<AgentProviderViewModel>(Card),
        };

        Children.Add(new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = new StackPanel
            {
                Margin = new Thickness(Spacing.M, Spacing.XS, Spacing.M, Spacing.L),
                Spacing = Spacing.M,
                Children =
                {
                    back,
                    new TextBlock { Text = "Providers", Classes = { "title" } },
                    new TextBlock
                    {
                        Text = "Coder Agent talks to the AI services you choose, with your own keys. Keys are encrypted for your Windows account and kept out of settings.json.",
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = Typography.SmallSize,
                        Foreground = Palette.TextSecondary,
                    },
                    list,
                    new TextBlock { Text = "Add a provider", Classes = { "section-title" } },
                    adders,
                },
            },
        });
    }

    private static Control Card(AgentProviderViewModel provider)
    {
        var name = new TextBox { Height = 28, VerticalContentAlignment = VerticalAlignment.Center, PlaceholderText = provider.KindName }
            .Bind(TextBox.TextProperty, provider, x => x.Name, Avalonia.Data.BindingMode.TwoWay);
        AutomationProperties.SetName(name, $"{provider.KindName} name");
        var url = new TextBox { Height = 28, VerticalContentAlignment = VerticalAlignment.Center, FontFamily = Typography.Code, FontSize = Typography.SmallSize, PlaceholderText = AiProviders.DefaultUrl(provider.Kind) }
            .Bind(TextBox.TextProperty, provider, x => x.BaseUrl, Avalonia.Data.BindingMode.TwoWay);
        AutomationProperties.SetName(url, $"{provider.Config.Name} base URL");

        var key = new TextBox { Height = 28, VerticalContentAlignment = VerticalAlignment.Center, PasswordChar = '•', FontFamily = Typography.Code, FontSize = Typography.SmallSize }
            .Bind(TextBox.TextProperty, provider, x => x.KeyInput, Avalonia.Data.BindingMode.TwoWay)
            .Bind(TextBox.PlaceholderTextProperty, provider, x => x.HasKey, has => has ? "Saved. Type a new key to replace it." : "Paste your API key");
        AutomationProperties.SetName(key, $"{provider.Config.Name} API key");
        key.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter)
            {
                provider.SaveKeyCommand.Execute(null);
                e.Handled = true;
            }
        };
        var saveKey = new FlatButton { Classes = { "secondary" }, Content = "Save", Padding = new Thickness(Spacing.S, 3), Margin = new Thickness(Spacing.XS, 0, 0, 0), Command = provider.SaveKeyCommand };
        AutomationProperties.SetName(saveKey, $"Save {provider.Config.Name} key");
        var clearKey = new FlatButton(Icons.Trash, "Forget the Saved Key", 14) { Margin = new Thickness(2, 0, 0, 0), Command = provider.ClearKeyCommand }
            .Bind(IsVisibleProperty, provider, x => x.HasKey);

        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = Typography.SmallSize }
            .Bind(TextBlock.TextProperty, provider, x => x.StatusText)
            .Bind(TextBlock.ForegroundProperty, provider, x => x.HasError, error => error ? Palette.DiagnosticError : Palette.TextSecondary);

        var test = new FlatButton { Classes = { "secondary" }, Content = "Test Connection", Padding = new Thickness(Spacing.S, 3), Command = provider.TestCommand };
        AutomationProperties.SetName(test, $"Test {provider.Config.Name}");
        var remove = new FlatButton { Content = "Remove", Padding = new Thickness(Spacing.S, 3), Command = provider.RemoveCommand };
        AutomationProperties.SetName(remove, $"Remove {provider.Config.Name}");

        return new Border
        {
            Classes = { "card", "provider-card" },
            Padding = new Thickness(Spacing.M),
            Child = new StackPanel
            {
                Spacing = Spacing.S,
                Children =
                {
                    new DockPanel
                    {
                        Children =
                        {
                            new TextBlock { Text = provider.KindName, FontSize = Typography.CaptionSize, Foreground = Palette.TextMuted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(Spacing.S, 0, 0, 0) }.Dock(Dock.Right),
                            name,
                        },
                    },
                    Label("Base URL"),
                    url,
                    Label(provider.NeedsKey ? "API key" : "API key (optional)"),
                    new DockPanel { Children = { clearKey.Dock(Dock.Right), saveKey.Dock(Dock.Right), key } },
                    new TextBlock { Text = provider.KeyHint, TextWrapping = TextWrapping.Wrap, FontSize = Typography.CaptionSize, Foreground = Palette.TextMuted },
                    status,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = Spacing.S, Margin = new Thickness(0, 2, 0, 0), Children = { test, remove } },
                },
            },
        };
    }

    private static TextBlock Label(string text) =>
        new() { Text = text, FontSize = Typography.SmallSize, Foreground = Palette.TextSecondary, Margin = new Thickness(0, Spacing.XXS, 0, 0) };
}
