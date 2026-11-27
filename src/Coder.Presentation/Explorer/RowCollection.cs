using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Coder.Presentation.Explorer;

/// <summary>
/// The explorer's rows, with whole runs inserted or removed in one change. Opening node_modules adds
/// thousands of rows: one notification for them all keeps the list (and the UI thread) from redoing its
/// layout thousands of times.
/// </summary>
public sealed class RowCollection<T>() : ObservableCollection<T>(new List<T>())
{
    private List<T> List => (List<T>)Items;

    /// <summary>Inserts <paramref name="items"/> at <paramref name="index"/>, raising a single change.</summary>
    public void InsertRange(int index, IReadOnlyList<T> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        CheckReentrancy();
        var added = items.ToList();
        List.InsertRange(index, added);
        Changed(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, (IList)added, index));
    }

    /// <summary>Removes <paramref name="count"/> rows from <paramref name="index"/>, raising a single change.</summary>
    public void RemoveRange(int index, int count)
    {
        if (count <= 0)
        {
            return;
        }

        CheckReentrancy();
        var removed = List.GetRange(index, count);
        List.RemoveRange(index, count);
        Changed(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, removed, index));
    }

    private void Changed(NotifyCollectionChangedEventArgs change)
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(change);
    }
}
