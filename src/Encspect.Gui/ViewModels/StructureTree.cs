using System.Collections;
using System.Collections.Specialized;
using Encspect.Core;

namespace Encspect.Gui.ViewModels;

// The report's tree as the flat list of rows a virtualizing list shows: the root, then each
// expanded row followed by the rows below it. Nothing is built ahead: the list asks for the rows it
// shows by index, and a row is located by walking down from the root past the expanded rows before
// it, so expanding a stream of 100,000 frames costs nothing until frames are shown, and then only
// the blocks of frames on screen are built. Changes are announced with ranges that resolve their
// rows on demand too, so a handler reads them while it handles the change, as lists do.
public sealed class StructureTree : IReadOnlyList<TreeRow>, IList, INotifyCollectionChanged
{
    public StructureTree(Node root)
    {
        ArgumentNullException.ThrowIfNull(root);

        Root = new TreeRow(root, null, 0);
        if (Root.HasChildren)
        {
            Root.LoadChildren();
            Root.IsExpanded = true;
        }
    }

    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public TreeRow Root { get; }

    public int Count => 1 + (Root.IsExpanded ? Root.VisibleCount : 0);

    bool IList.IsFixedSize => false;

    bool IList.IsReadOnly => true;

    bool ICollection.IsSynchronized => false;

    object ICollection.SyncRoot => this;

    public TreeRow this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)index, (uint)Count, nameof(index));
            return index == 0 ? Root : Descendant(Root, index - 1);
        }
    }

    object? IList.this[int index]
    {
        get => this[index];
        set => throw new NotSupportedException();
    }

    // The row's index in the list, or -1 when it is not shown (an ancestor is collapsed) or not of
    // this tree.
    public int IndexOf(TreeRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        int index = 0;
        for (TreeRow current = row; current.Parent is { } parent; current = parent)
        {
            if (!parent.IsExpanded || parent.Rows?[current.ChildIndex] != current)
            {
                return -1;
            }

            index += 1 + Position(parent, current.ChildIndex);
        }

        return ReferenceEquals(TopOf(row), Root) ? index : -1;
    }

    public void Expand(TreeRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.IsExpanded || !row.HasChildren)
        {
            return;
        }

        int index = IndexOf(row);
        row.LoadChildren();
        row.IsExpanded = true;

        row.Parent?.ExpandedChildren.Add(row.ChildIndex, row);
        AddToAncestors(row, row.VisibleCount);
        if (index >= 0 && row.VisibleCount > 0)
        {
            CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(
                NotifyCollectionChangedAction.Add, new RowRange(i => this[index + 1 + i], row.VisibleCount), index + 1));
        }
    }

    public void Collapse(TreeRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!row.IsExpanded)
        {
            return;
        }

        int index = IndexOf(row);
        row.IsExpanded = false;
        row.Parent?.ExpandedChildren.Remove(row.ChildIndex);
        AddToAncestors(row, -row.VisibleCount);
        if (index >= 0 && row.VisibleCount > 0)
        {
            // The rows that leave are still the row's, so the range resolves them below it.
            CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(
                NotifyCollectionChangedAction.Remove, new RowRange(i => Descendant(row, i), row.VisibleCount), index + 1));
        }
    }

    public void Toggle(TreeRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.IsExpanded)
        {
            Collapse(row);
        }
        else
        {
            Expand(row);
        }
    }

    // The row at the end of a path of child indexes from the root (Finding.Path), with every row
    // on the way expanded; null when the path leads nowhere.
    public TreeRow? Reveal(IReadOnlyList<int> path)
    {
        ArgumentNullException.ThrowIfNull(path);

        TreeRow row = Root;
        foreach (int childIndex in path)
        {
            if (!row.HasChildren || childIndex < 0 || childIndex >= row.LoadChildren().Count)
            {
                return null;
            }

            Expand(row);
            row = row.RowAt(childIndex);
        }

        return row;
    }

    public IEnumerator<TreeRow> GetEnumerator()
    {
        for (int i = 0; i < Count; i++)
        {
            yield return this[i];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    bool IList.Contains(object? value) => value is TreeRow row && IndexOf(row) >= 0;

    int IList.IndexOf(object? value) => value is TreeRow row ? IndexOf(row) : -1;

    void ICollection.CopyTo(Array array, int index)
    {
        foreach (TreeRow row in this)
        {
            array.SetValue(row, index++);
        }
    }

    int IList.Add(object? value) => throw new NotSupportedException();

    void IList.Clear() => throw new NotSupportedException();

    void IList.Insert(int index, object? value) => throw new NotSupportedException();

    void IList.Remove(object? value) => throw new NotSupportedException();

    void IList.RemoveAt(int index) => throw new NotSupportedException();

    private static TreeRow TopOf(TreeRow row)
    {
        while (row.Parent is { } parent)
        {
            row = parent;
        }

        return row;
    }

    // The position of a child among the rows below its parent: the children before it, and the
    // rows below those of them that are expanded.
    private static int Position(TreeRow parent, int childIndex)
    {
        int position = childIndex;
        foreach ((int index, TreeRow expanded) in parent.ExpandedChildren)
        {
            if (index >= childIndex)
            {
                break;
            }

            position += expanded.VisibleCount;
        }

        return position;
    }

    // The row at a position among the rows below row, as if row were expanded.
    private static TreeRow Descendant(TreeRow row, int position)
    {
        while (true)
        {
            int extra = 0;
            TreeRow? into = null;
            foreach ((int index, TreeRow expanded) in row.ExpandedChildren)
            {
                int at = index + extra;
                if (position < at)
                {
                    break;
                }

                if (position == at)
                {
                    return expanded;
                }

                if (position <= at + expanded.VisibleCount)
                {
                    into = expanded;
                    position -= at + 1;
                    break;
                }

                extra += expanded.VisibleCount;
            }

            if (into is null)
            {
                return row.RowAt(position - extra);
            }

            row = into;
        }
    }

    // A row's rows count toward its parent while it is expanded, and the parent's toward its own
    // parent while the parent is expanded, collapsed ancestors included, so they are right when
    // those are expanded again.
    private static void AddToAncestors(TreeRow row, int delta)
    {
        for (TreeRow child = row; child.Parent is { } parent; child = parent)
        {
            parent.VisibleCount += delta;
            if (!parent.IsExpanded)
            {
                break;
            }
        }
    }

    // A range of rows that resolves each one only when it is read, for change notifications.
    private sealed class RowRange(Func<int, TreeRow> resolve, int count) : IList
    {
        public int Count => count;

        public bool IsFixedSize => true;

        public bool IsReadOnly => true;

        public bool IsSynchronized => false;

        public object SyncRoot => this;

        public object? this[int index]
        {
            get => (uint)index < (uint)count ? resolve(index) : throw new ArgumentOutOfRangeException(nameof(index));
            set => throw new NotSupportedException();
        }

        public IEnumerator GetEnumerator()
        {
            for (int i = 0; i < count; i++)
            {
                yield return resolve(i);
            }
        }

        public bool Contains(object? value) => throw new NotSupportedException();

        public int IndexOf(object? value) => throw new NotSupportedException();

        public void CopyTo(Array array, int index) => throw new NotSupportedException();

        public int Add(object? value) => throw new NotSupportedException();

        public void Clear() => throw new NotSupportedException();

        public void Insert(int index, object? value) => throw new NotSupportedException();

        public void Remove(object? value) => throw new NotSupportedException();

        public void RemoveAt(int index) => throw new NotSupportedException();
    }
}
