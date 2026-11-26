using Coder.Core.Files;
using Coder.Core.Git;
using Coder.Core.Syntax;
using Coder.Presentation.Mvvm;

namespace Coder.Presentation.Explorer;

/// <summary>One visible row of the explorer tree.</summary>
public sealed class ExplorerItemViewModel(FileEntry entry, int depth) : ObservableObject
{
    private bool _isExpanded;
    private GitChangeKind? _gitChange;
    private bool _containsGitChanges;

    public FileEntry Entry { get; } = entry;

    public string Name => Entry.Name;

    public string Path => Entry.Path;

    public bool IsDirectory => Entry.IsDirectory;

    /// <summary>Nesting level, used for indentation.</summary>
    public int Depth { get; } = depth;

    /// <summary>Language accent used to tint the file icon.</summary>
    public string IconColor => IsDirectory ? "#9B9B9B" : Languages.FromFileName(Entry.Path).IconColor;

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    /// <summary>How git sees this file (null when unchanged or not in a repository).</summary>
    public GitChangeKind? GitChange
    {
        get => _gitChange;
        set => SetProperty(ref _gitChange, value);
    }

    /// <summary>A folder with changed files somewhere inside.</summary>
    public bool ContainsGitChanges
    {
        get => _containsGitChanges;
        set => SetProperty(ref _containsGitChanges, value);
    }

    /// <summary>Rows currently shown under this folder (empty while collapsed).</summary>
    internal List<ExplorerItemViewModel> VisibleChildren { get; } = [];

    private bool _isEditing;

    /// <summary>The name is being typed (a new file or folder, or a rename): the row shows a text box.</summary>
    public bool IsEditing
    {
        get => _isEditing;
        set => SetProperty(ref _isEditing, value);
    }

    /// <summary>A row for a file or folder that doesn't exist yet: its name is being typed.</summary>
    public bool IsPending { get; init; }

    /// <summary>For a pending row: the folder it will be created in.</summary>
    public string? ParentFolder { get; init; }
}
