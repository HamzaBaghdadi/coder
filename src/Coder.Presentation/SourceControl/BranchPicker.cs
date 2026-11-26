using Coder.Core.Git;
using Coder.Presentation.Palette;

namespace Coder.Presentation.SourceControl;

/// <summary>
/// "Git: Checkout to…" in the palette, as in VS Code: typing filters the branches, and the first row
/// creates a new branch named after what was typed. Local branches come first, then remote ones
/// (choosing a remote branch checks it out as a local branch that tracks it).
/// </summary>
public sealed class BranchPicker(SourceControlViewModel sourceControl, IReadOnlyList<GitBranch> branches) : IPaletteProvider
{
    public string Prefix => string.Empty;

    public string Placeholder => "Select a branch to switch to, or type a name to create a new branch";

    public IEnumerable<PaletteItem> GetItems(string query)
    {
        var name = SourceControlViewModel.BranchName(query);
        if (name.Length > 0 && branches.All(b => b.Name != name))
        {
            yield return new PaletteItem($"+ Create new branch \"{name}\"", "From the current commit", null, () => sourceControl.CreateBranchAsync(name));
        }

        var rows = branches.Select(branch => new PaletteItem(
            branch.Name,
            branch.IsRemote ? $"Remote branch · {branch.LastCommitSubject}" : branch.LastCommitSubject,
            branch.IsCurrent ? "current" : null,
            () => branch.IsCurrent ? Task.CompletedTask : sourceControl.SwitchBranchAsync(branch)));
        foreach (var row in CommandPaletteViewModel.Rank(rows, query))
        {
            yield return row;
        }
    }
}
