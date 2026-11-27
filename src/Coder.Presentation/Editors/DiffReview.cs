namespace Coder.Presentation.Editors;

/// <summary>
/// A proposed change waiting for the user in a diff tab: Accept or Reject. Closing the tab, or another
/// proposal for the same file replacing it, rejects it.
/// </summary>
public sealed class DiffReview
{
    private readonly TaskCompletionSource<bool> _decision = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>True when accepted; false when rejected or closed.</summary>
    public Task<bool> Decision => _decision.Task;

    public bool IsPending => !_decision.Task.IsCompleted;

    /// <summary>Raised once the decision is made, so the toolbar hides its buttons.</summary>
    public event EventHandler? Decided;

    public void Accept() => Decide(true);

    public void Reject() => Decide(false);

    private void Decide(bool accepted)
    {
        if (_decision.TrySetResult(accepted))
        {
            Decided?.Invoke(this, EventArgs.Empty);
        }
    }
}
