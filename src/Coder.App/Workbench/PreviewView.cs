using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using Coder.Presentation.Editors;

namespace Coder.App.Workbench;

/// <summary>
/// The content of a preview tab: the rendered Markdown document, or the drawn SVG. Typing in the file's
/// editor tab updates it, a moment after the last keystroke so fast typing stays smooth.
/// </summary>
public sealed class PreviewView : Panel
{
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(150);

    private readonly MarkdownDocumentView _markdown = new();
    private readonly ContentControl _svgHost = new();
    private readonly DispatcherTimer _refresh;
    private PreviewTabViewModel? _tab;

    public PreviewView(Func<string, Task> openFile)
    {
        _markdown.OpenFile = openFile;
        Children.Add(_markdown);
        Children.Add(_svgHost);
        _refresh = new DispatcherTimer(Settle, DispatcherPriority.Background, (_, _) =>
        {
            _refresh!.Stop();
            Render();
        });
    }

    /// <summary>The drawing of SVG text: fitted and centered on a transparency grid, or why it can't be drawn.</summary>
    public static Func<string, Control>? CreateSvgPreview { get; set; } = text =>
    {
        try
        {
            var document = Core.Svg.SvgDocument.Parse(text);
            var view = new Controls.SvgView
            {
                Document = document,
                ShowTransparencyGrid = true,
                Margin = new Avalonia.Thickness(Coder.App.Theme.Spacing.XL),
            };
            Avalonia.Automation.AutomationProperties.SetName(view, "SVG Preview");
            return view;
        }
        catch (Core.Svg.SvgException exception)
        {
            return new TextBlock
            {
                Text = $"This SVG can't be drawn: {exception.Message}",
                Classes = { "secondary" },
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Margin = new Avalonia.Thickness(Coder.App.Theme.Spacing.XL),
            };
        }
    };

    /// <summary>The Markdown view (tests).</summary>
    internal MarkdownDocumentView Markdown => _markdown;

    public PreviewTabViewModel? Tab
    {
        get => _tab;
        set
        {
            if (ReferenceEquals(_tab, value))
            {
                return;
            }

            if (_tab is not null)
            {
                _tab.PropertyChanged -= OnTabChanged;
            }

            _tab = value;
            if (_tab is not null)
            {
                _tab.PropertyChanged += OnTabChanged;
            }

            Render();
        }
    }

    private void OnTabChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PreviewTabViewModel.Text) or nameof(PreviewTabViewModel.FilePath))
        {
            _refresh.Stop();
            _refresh.Start();
        }
    }

    private void Render()
    {
        var tab = _tab;
        _markdown.IsVisible = tab?.Kind == PreviewKind.Markdown;
        _svgHost.IsVisible = tab?.Kind == PreviewKind.Svg;
        if (tab is null)
        {
            return;
        }

        if (tab.Kind == PreviewKind.Markdown)
        {
            _markdown.Show(tab.Text, tab.BaseFolder);
        }
        else
        {
            _svgHost.Content = CreateSvgPreview?.Invoke(tab.Text)
                ?? new TextBlock { Text = "SVG preview isn't available.", Classes = { "secondary" }, Margin = new Avalonia.Thickness(Coder.App.Theme.Spacing.XL) };
        }
    }
}
