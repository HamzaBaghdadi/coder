using Coder.Presentation.Mvvm;

namespace Coder.Presentation.Editors;

/// <summary>
/// Anything shown as a tab in the editor area: a file being edited (<see cref="EditorTabViewModel"/>)
/// or a comparison (<see cref="DiffTabViewModel"/>). The tab strip only needs what is here.
/// </summary>
public abstract class TabViewModel : ObservableObject
{
    private bool _isActive;

    public abstract string Title { get; }

    /// <summary>Shown next to the title and in tooltips (usually the path).</summary>
    public abstract string Description { get; }

    /// <summary>Unsaved changes (only documents have them).</summary>
    public virtual bool IsDirty => false;

    /// <summary>The file this tab is about, if any (used for its icon).</summary>
    public abstract string? FilePath { get; set; }

    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }

    /// <summary>Releases what the tab holds when it is closed.</summary>
    public virtual void Close()
    {
    }
}
