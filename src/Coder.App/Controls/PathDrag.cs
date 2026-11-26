using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace Coder.App.Controls;

/// <summary>A place inside the window that files dragged from the explorer or a tab can be dropped on (Coder Agent's chat).</summary>
public interface IPathDropTarget
{
    /// <summary>A drag of paths is over the target (true) or has left it (false): show or hide the drop hint.</summary>
    void SetPathDragOver(bool isOver);

    Task DropPathsAsync(IReadOnlyList<string> paths);
}

/// <summary>
/// Drags of files within the window. The explorer and the tab strip track their own drags with pointer
/// capture (the explorer also moves files onto folders); this finds the drop target under the pointer,
/// keeps its hint lit while the drag is over it, and drops there on release.
/// </summary>
public sealed class PathDrag
{
    private IPathDropTarget? _over;

    /// <summary>The drop target under the pointer, if any.</summary>
    public static IPathDropTarget? TargetAt(Visual source, PointerEventArgs e)
    {
        if (TopLevel.GetTopLevel(source) is not { } top)
        {
            return null;
        }

        var hit = top.InputHitTest(e.GetPosition(top)) as Visual;
        return hit?.GetSelfAndVisualAncestors().OfType<IPathDropTarget>().FirstOrDefault();
    }

    /// <summary>Follows the pointer during a drag; returns the target it is over (null when none).</summary>
    public IPathDropTarget? Move(Visual source, PointerEventArgs e)
    {
        var target = TargetAt(source, e);
        if (!ReferenceEquals(target, _over))
        {
            _over?.SetPathDragOver(false);
            _over = target;
            _over?.SetPathDragOver(true);
        }

        return target;
    }

    /// <summary>Ends the drag: drops on the target under the pointer and returns true, or returns false when there is none.</summary>
    public bool Drop(Visual source, PointerEventArgs e, IReadOnlyList<string> paths)
    {
        var target = TargetAt(source, e);
        Cancel();
        if (target is null || paths.Count == 0)
        {
            return false;
        }

        _ = target.DropPathsAsync(paths);
        return true;
    }

    public void Cancel()
    {
        _over?.SetPathDragOver(false);
        _over = null;
    }
}
