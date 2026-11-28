using Coder.Presentation.Editors;

namespace Coder.Presentation.Extensions;

/// <summary>
/// An extension's page in the editor area (like VS Code's): its name, publisher, version and description,
/// the same Install / Uninstall / Set Theme actions as its row in the Extensions view, and its README.
/// </summary>
public sealed class ExtensionDetailsTabViewModel : TabViewModel
{
    private string? _readme;
    private bool _isLoading = true;

    public ExtensionDetailsTabViewModel(ExtensionItemViewModel item, Func<Task<string?>> loadReadme)
    {
        Item = item;
        item.PropertyChanged += (_, _) => OnPropertyChanged(nameof(Item)); // installed, busy, status
        _ = LoadAsync(loadReadme);
    }

    public ExtensionItemViewModel Item { get; }

    public override string Title => $"Extension: {Item.DisplayName}";

    public override string Description => Item.Id;

    public override string? FilePath
    {
        get => null;
        set { }
    }

    /// <summary>The README's Markdown; null until loaded, or when the extension has none.</summary>
    public string? Readme
    {
        get => _readme;
        private set => SetProperty(ref _readme, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    /// <summary>The folder the README's relative images and links resolve against (installed extensions).</summary>
    public string? BaseFolder => Item.Installed?.Folder;

    private async Task LoadAsync(Func<Task<string?>> loadReadme)
    {
        try
        {
            Readme = await loadReadme();
        }
        finally
        {
            IsLoading = false;
        }
    }
}
