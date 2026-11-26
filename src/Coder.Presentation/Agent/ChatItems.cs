using System.Windows.Input;
using Coder.Core.Ai;
using Coder.Core.Diff;
using Coder.Presentation.Mvvm;

namespace Coder.Presentation.Agent;

/// <summary>One entry of the chat: a message, a tool the agent used, or a notice.</summary>
public abstract class ChatItem : ObservableObject;

/// <summary>What the user sent, with the files and images attached.</summary>
public sealed class UserMessageItem(string text, IReadOnlyList<AgentAttachment> attachments) : ChatItem
{
    public string Text { get; } = text;

    public IReadOnlyList<AgentAttachment> Attachments { get; } = attachments;
}

/// <summary>
/// The agent's reply as it streams in. Reasoning models' thoughts (sent apart, or inline in a leading
/// <c>&lt;think&gt;</c> block) are kept out of the text and shown folded.
/// </summary>
public sealed class AssistantMessageItem : ChatItem
{
    private const string ThinkOpen = "<think>";
    private const string ThinkClose = "</think>";

    private readonly System.Text.StringBuilder _raw = new();
    private readonly System.Text.StringBuilder _reasoning = new();
    private string _text = string.Empty;
    private string _inlineReasoning = string.Empty;
    private bool _isStreaming = true;
    private IReadOnlyList<ChatBlock>? _blocks;

    /// <summary>The reply's Markdown, without reasoning.</summary>
    public string Text => _text;

    public string Reasoning => _reasoning.Length > 0 ? _reasoning.ToString() + _inlineReasoning : _inlineReasoning;

    public bool HasReasoning => Reasoning.Length > 0;

    /// <summary>The reply as blocks to draw; parsed again only when the text changed.</summary>
    public IReadOnlyList<ChatBlock> Blocks => _blocks ??= ChatMarkdown.Parse(_text);

    public bool IsStreaming
    {
        get => _isStreaming;
        set => SetProperty(ref _isStreaming, value);
    }

    /// <summary>Still thinking: reasoning arrived and no answer yet.</summary>
    public bool IsThinking => _isStreaming && _text.Length == 0 && HasReasoning;

    public void AppendText(string delta)
    {
        _raw.Append(delta);
        var raw = _raw.ToString();
        var start = raw.TrimStart();
        if (start.StartsWith(ThinkOpen, StringComparison.Ordinal))
        {
            var close = start.IndexOf(ThinkClose, StringComparison.Ordinal);
            _inlineReasoning = (close < 0 ? start[ThinkOpen.Length..] : start[ThinkOpen.Length..close]).Trim();
            _text = close < 0 ? string.Empty : start[(close + ThinkClose.Length)..].TrimStart();
            OnPropertyChanged(nameof(Reasoning));
            OnPropertyChanged(nameof(HasReasoning));
        }
        else if (!ThinkOpen.StartsWith(start, StringComparison.Ordinal) || start.Length == 0)
        {
            _text = raw;
        }

        _blocks = null;
        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(Blocks));
        OnPropertyChanged(nameof(IsThinking));
    }

    public void AppendReasoning(string delta)
    {
        _reasoning.Append(delta);
        OnPropertyChanged(nameof(Reasoning));
        OnPropertyChanged(nameof(HasReasoning));
        OnPropertyChanged(nameof(IsThinking));
    }

    public void Complete()
    {
        IsStreaming = false;
        OnPropertyChanged(nameof(IsThinking));
    }
}

public enum ToolStatus
{
    Running,
    WaitingForUser,
    Succeeded,
    Failed,
}

/// <summary>A tool the agent used: what it did, how it went, and its result (folded). Edits and commands carry a card for the user's decision.</summary>
public sealed class ToolCallItem(ToolCall call, string title) : ChatItem
{
    private ToolStatus _status = ToolStatus.Running;
    private string _result = string.Empty;
    private bool _isExpanded;
    private ChatItem? _proposal;

    public ToolCall Call { get; } = call;

    public string Title { get; } = title;

    public ToolStatus Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    /// <summary>What the tool returned (for commands, their output).</summary>
    public string Result
    {
        get => _result;
        set => SetProperty(ref _result, value);
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    /// <summary>The edit or command waiting for (or decided by) the user; null for tools that only read.</summary>
    public ChatItem? Proposal
    {
        get => _proposal;
        set => SetProperty(ref _proposal, value);
    }

    public ICommand ToggleCommand => new RelayCommand(() => IsExpanded = !IsExpanded);
}

public enum ProposalState
{
    Pending,
    Accepted,
    Rejected,
    Failed,
    Cancelled,
}

public enum DiffLineKind
{
    Context,
    Added,
    Removed,

    /// <summary>Unchanged lines left out between two changes.</summary>
    Gap,
}

/// <summary>A row of a unified diff: its line numbers on each side (null where it does not exist) and its text.</summary>
public sealed record DiffLine(DiffLineKind Kind, int? OldNumber, int? NewNumber, string Text);

/// <summary>An edit the agent proposes, shown as a diff with Accept and Reject. Nothing changes before Accept.</summary>
public sealed class EditProposalItem : ChatItem
{
    /// <summary>Rows of a long diff beyond this are left out of the card (the full diff opens in a tab).</summary>
    public const int MaxCardLines = 400;

    private const int Context = 2;
    private readonly TaskCompletionSource<EditDecision> _decision = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ProposalState _state;
    private string? _error;

    public EditProposalItem(EditProposal proposal, string relativePath, Func<EditProposalItem, Task> apply, Action<EditProposalItem> openDiff, Func<string, Task> openFile)
    {
        Proposal = proposal;
        RelativePath = relativePath;
        var oldLines = proposal.Original is null ? [] : LineDiff.SplitLines(proposal.Original);
        var newLines = LineDiff.SplitLines(proposal.Proposed);
        (Lines, Added, Removed) = Unified(oldLines, newLines);
        AcceptCommand = new RelayCommand(async () =>
        {
            if (State != ProposalState.Pending)
            {
                return;
            }

            try
            {
                await apply(this);
                State = ProposalState.Accepted;
                _decision.TrySetResult(new EditDecision(true));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Error = exception.Message;
                State = ProposalState.Failed;
                _decision.TrySetResult(new EditDecision(false, $"Applying the change failed: {exception.Message}"));
            }
        });
        RejectCommand = new RelayCommand(() => Reject(null));
        OpenDiffCommand = new RelayCommand(() => openDiff(this));
        OpenFileCommand = new RelayCommand(() => _ = openFile(proposal.Path));
    }

    public EditProposal Proposal { get; }

    public string RelativePath { get; }

    public string FileName => Path.GetFileName(Proposal.Path);

    public bool IsNewFile => Proposal.Original is null;

    public IReadOnlyList<DiffLine> Lines { get; }

    public int Added { get; }

    public int Removed { get; }

    public ProposalState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(IsPending));
            }
        }
    }

    public bool IsPending => _state == ProposalState.Pending;

    public string? Error
    {
        get => _error;
        private set => SetProperty(ref _error, value);
    }

    public Task<EditDecision> Decision => _decision.Task;

    public ICommand AcceptCommand { get; }

    public ICommand RejectCommand { get; }

    public ICommand OpenDiffCommand { get; }

    public ICommand OpenFileCommand { get; }

    public void Reject(string? feedback)
    {
        if (State == ProposalState.Pending)
        {
            State = ProposalState.Rejected;
            _decision.TrySetResult(new EditDecision(false, feedback));
        }
    }

    /// <summary>The agent stopped before the user decided.</summary>
    public void Cancel()
    {
        if (State == ProposalState.Pending)
        {
            State = ProposalState.Cancelled;
            _decision.TrySetCanceled();
        }
    }

    /// <summary>A unified diff: each change with a little context, long unchanged stretches folded into a gap.</summary>
    internal static (IReadOnlyList<DiffLine> Lines, int Added, int Removed) Unified(IReadOnlyList<string> oldLines, IReadOnlyList<string> newLines)
    {
        var hunks = LineDiff.Compute(oldLines, newLines);
        var lines = new List<DiffLine>();
        int added = 0, removed = 0, oldLine = 0, newLine = 0;
        for (var h = 0; h < hunks.Count; h++)
        {
            var hunk = hunks[h];
            var contextStart = Math.Max(oldLine, hunk.OldStart - Context);
            if (contextStart > oldLine && (h > 0 || contextStart > 0))
            {
                lines.Add(new DiffLine(DiffLineKind.Gap, null, null, $"{contextStart - oldLine} unchanged lines"));
            }

            newLine += contextStart - oldLine;
            for (oldLine = contextStart; oldLine < hunk.OldStart; oldLine++, newLine++)
            {
                lines.Add(new DiffLine(DiffLineKind.Context, oldLine + 1, newLine + 1, oldLines[oldLine]));
            }

            for (var i = 0; i < hunk.OldCount; i++, oldLine++)
            {
                lines.Add(new DiffLine(DiffLineKind.Removed, oldLine + 1, null, oldLines[oldLine]));
                removed++;
            }

            for (var i = 0; i < hunk.NewCount; i++, newLine++)
            {
                lines.Add(new DiffLine(DiffLineKind.Added, null, newLine + 1, newLines[newLine]));
                added++;
            }

            var next = h + 1 < hunks.Count ? hunks[h + 1].OldStart : oldLines.Count;
            var contextEnd = Math.Min(next, oldLine + Context);
            for (; oldLine < contextEnd; oldLine++, newLine++)
            {
                lines.Add(new DiffLine(DiffLineKind.Context, oldLine + 1, newLine + 1, oldLines[oldLine]));
            }
        }

        if (oldLine < oldLines.Count && hunks.Count > 0)
        {
            lines.Add(new DiffLine(DiffLineKind.Gap, null, null, $"{oldLines.Count - oldLine} unchanged lines"));
        }

        return (lines, added, removed);
    }
}

/// <summary>A command the agent wants to run, with Run and Skip. Nothing runs before Run.</summary>
public sealed class CommandProposalItem : ChatItem
{
    private readonly TaskCompletionSource<bool> _decision = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ProposalState _state;

    public CommandProposalItem(CommandProposal proposal)
    {
        Proposal = proposal;
        RunCommand = new RelayCommand(() => Decide(true));
        SkipCommand = new RelayCommand(() => Decide(false));
    }

    public CommandProposal Proposal { get; }

    public string Command => Proposal.Command;

    public ProposalState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(IsPending));
            }
        }
    }

    public bool IsPending => _state == ProposalState.Pending;

    public Task<bool> Decision => _decision.Task;

    public ICommand RunCommand { get; }

    public ICommand SkipCommand { get; }

    public void Decide(bool run)
    {
        if (State == ProposalState.Pending)
        {
            State = run ? ProposalState.Accepted : ProposalState.Rejected;
            _decision.TrySetResult(run);
        }
    }

    public void Cancel()
    {
        if (State == ProposalState.Pending)
        {
            State = ProposalState.Cancelled;
            _decision.TrySetCanceled();
        }
    }
}

/// <summary>A line from Coder rather than the model: an error (with Retry), or information.</summary>
public sealed class NoticeItem(string text, bool isError, ICommand? retry = null) : ChatItem
{
    public string Text { get; } = text;

    public bool IsError { get; } = isError;

    /// <summary>Sends the last request again; null when there is nothing to retry.</summary>
    public ICommand? RetryCommand { get; } = retry;
}
