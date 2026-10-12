using System.Collections;
using System.Collections.Specialized;

namespace Encspect.Gui.ViewModels;

// Whether a row has rows before and after it in a list that keeps changing, as Previous and Next
// need while the list fills in. Adding a row never takes a neighbor away, and it gives one only
// when it lands at an end the row is at, so an added row costs a look at its index alone. A row
// that is not in the list stays without neighbors until it is added itself. Only other changes
// (a sort, rows removed) look the row up in the list again.
public sealed class ListNeighbors
{
    private readonly IList _list;

    public ListNeighbors(IList list)
    {
        ArgumentNullException.ThrowIfNull(list);
        _list = list;
    }

    public object? Row { get; private set; }

    public bool IsListed { get; private set; }

    public bool HasPrevious { get; private set; }

    public bool HasNext { get; private set; }

    // How often the row was looked up in the list, each a linear search.
    internal int Lookups { get; private set; }

    public void Track(object? row)
    {
        Row = row;
        Locate();
    }

    // Brings the state up to date with a change of the list; true when it changed.
    public bool Update(NotifyCollectionChangedEventArgs change)
    {
        ArgumentNullException.ThrowIfNull(change);

        (bool, bool, bool) before = (IsListed, HasPrevious, HasNext);
        if (Row is null)
        {
            return false;
        }

        if (change is { Action: NotifyCollectionChangedAction.Add, NewItems.Count: 1, NewStartingIndex: >= 0 } && change.NewStartingIndex < _list.Count)
        {
            int index = change.NewStartingIndex;
            if (ReferenceEquals(change.NewItems[0], Row))
            {
                Place(index);
            }
            else if (IsListed)
            {
                HasPrevious |= index == 0;
                HasNext |= index == _list.Count - 1;
            }
        }
        else
        {
            Locate();
        }

        return before != (IsListed, HasPrevious, HasNext);
    }

    private void Locate()
    {
        if (Row is null)
        {
            Place(-1);
            return;
        }

        Lookups++;
        Place(_list.IndexOf(Row));
    }

    private void Place(int index)
    {
        IsListed = index >= 0;
        HasPrevious = index > 0;
        HasNext = index >= 0 && index < _list.Count - 1;
    }
}
