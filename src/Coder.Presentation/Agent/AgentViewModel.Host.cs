using System.Text;
using Coder.Core.Ai;
using Coder.Core.Lsp;
using Coder.Core.Text;
using Coder.Presentation.Editors;

namespace Coder.Presentation.Agent;

public sealed partial class AgentViewModel
{
    /// <summary>Runs on the UI thread (the agent's tools call in from the thread pool).</summary>
    private Task<T> OnUi<T>(Func<Task<T>> action)
    {
        if (_ui is null || SynchronizationContext.Current == _ui)
        {
            return action();
        }

        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui.Post(async _ =>
        {
            try
            {
                done.TrySetResult(await action());
            }
            catch (OperationCanceledException)
            {
                done.TrySetCanceled();
            }
            catch (Exception exception)
            {
                done.TrySetException(exception);
            }
        }, null);
        return done.Task;
    }

    /// <summary>Shows a proposed edit in the chat and waits for Accept or Reject.</summary>
    private async Task<EditDecision> ReviewEditAsync(EditProposal proposal, CancellationToken cancellationToken)
    {
        var item = await OnUi(() => Task.FromResult(new EditProposalItem(proposal, DisplayPath(proposal.Path), ApplyAsync, OpenDiff, _workspace.OpenFile)));
        await OnUi(() =>
        {
            if (_currentTool is { } tool)
            {
                tool.Proposal = item;
                tool.Status = ToolStatus.WaitingForUser;
            }

            return Task.FromResult(true);
        });
        using (cancellationToken.Register(() => _ = OnUi(() =>
        {
            item.Cancel();
            return Task.FromResult(true);
        })))
        {
            return await item.Decision.ConfigureAwait(false);
        }
    }

    private async Task<bool> ConfirmCommandAsync(CommandProposal proposal, CancellationToken cancellationToken)
    {
        var item = await OnUi(() =>
        {
            var card = new CommandProposalItem(proposal);
            if (_currentTool is { } tool)
            {
                tool.Proposal = card;
                tool.Status = ToolStatus.WaitingForUser;
            }

            return Task.FromResult(card);
        });
        bool run;
        using (cancellationToken.Register(() => _ = OnUi(() =>
        {
            item.Cancel();
            return Task.FromResult(true);
        })))
        {
            run = await item.Decision.ConfigureAwait(false);
        }

        if (run)
        {
            await OnUi(() =>
            {
                if (_currentTool is { } tool)
                {
                    tool.Status = ToolStatus.Running;
                }

                return Task.FromResult(true);
            });
        }

        return run;
    }

    /// <summary>
    /// Applies an accepted edit. An open file changes in its editor (one undo step) and is saved; another
    /// is written on disk keeping its encoding and line endings; a new file is created with its folders.
    /// </summary>
    private async Task ApplyAsync(EditProposalItem item)
    {
        var proposal = item.Proposal;
        var path = proposal.Path;
        if (_workspace.Editors.FindTab(path) is { } tab)
        {
            var current = tab.Document.GetText();
            var (start, end, text) = MinimalEdit(current, proposal.Proposed);
            tab.Editor.ApplyEdits([(start, end, text)]);
            if (!await _workspace.Editors.SaveAsync(tab))
            {
                throw new IOException($"{Path.GetFileName(path)} was changed in its editor but could not be saved.");
            }
        }
        else if (_workspace.FileSystem.Exists(path))
        {
            var content = await _workspace.FileSystem.ReadTextAsync(path);
            var ending = LineEndings.Detect(content.Text);
            await _workspace.FileSystem.WriteTextAsync(path, LineEndings.Apply(proposal.Proposed, ending), content.Encoding);
            _workspace.FilesChanged([(path, FileChangeType.Changed)]);
        }
        else
        {
            if (Path.GetDirectoryName(path) is { } folder)
            {
                _workspace.FileSystem.CreateDirectory(folder);
            }

            await _workspace.FileSystem.WriteTextAsync(path, LineEndings.Apply(proposal.Proposed, LineEndings.Platform), new UTF8Encoding(false));
            _workspace.FilesChanged([(path, FileChangeType.Created)]);
        }

        if (_currentTool is { Proposal: var shown } tool && ReferenceEquals(shown, item))
        {
            tool.Status = ToolStatus.Running;
        }
    }

    /// <summary>The one replacement that turns <paramref name="before"/> into <paramref name="after"/>: everything between their common start and end.</summary>
    internal static (int Start, int End, string Text) MinimalEdit(string before, string after)
    {
        var prefix = 0;
        var max = Math.Min(before.Length, after.Length);
        while (prefix < max && before[prefix] == after[prefix])
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < max - prefix && before[before.Length - 1 - suffix] == after[after.Length - 1 - suffix])
        {
            suffix++;
        }

        return (prefix, before.Length - suffix, after[prefix..(after.Length - suffix)]);
    }

    /// <summary>The proposal side by side in a diff tab: the file now on the left, the agent's version on the right.</summary>
    private void OpenDiff(EditProposalItem item) =>
        _workspace.Editors.OpenDiff(new DiffTabViewModel(item.Proposal.Path, item.IsNewFile ? "New File" : "Current", item.Proposal.Original ?? string.Empty, "Coder Agent", item.Proposal.Proposed));

    private sealed class ToolHost(AgentViewModel agent) : IAgentToolHost
    {
        public string? OpenDocumentText(string path) =>
            agent.OnUi(() => Task.FromResult(agent._workspace.Editors.FindTab(path)?.Document.GetText())).GetAwaiter().GetResult();

        public Task<EditDecision> ReviewEditAsync(EditProposal proposal, CancellationToken cancellationToken) =>
            agent.ReviewEditAsync(proposal, cancellationToken);

        public Task<bool> ConfirmCommandAsync(CommandProposal proposal, CancellationToken cancellationToken) =>
            agent.ConfirmCommandAsync(proposal, cancellationToken);
    }
}
