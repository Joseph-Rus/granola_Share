using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia.Utilities;
using StudyStash.App.ViewModels;

namespace StudyStash.App.Controls;

/// <summary>
/// Some of the quick panel's rows, kept up to date as the search changes them: the Mac draws its actions as a row of
/// chips under the list, so the list shows every row but the actions and the chips show only those. It listens to the
/// rows weakly, so a view that lets go of it doesn't keep it alive.
/// </summary>
public sealed class FilteredRows : IReadOnlyList<QuickRow>, IList, INotifyCollectionChanged, IWeakEventSubscriber<NotifyCollectionChangedEventArgs>
{
    readonly ObservableCollection<QuickRow> source;
    readonly Func<QuickRow, bool> keep;
    readonly List<QuickRow> shown = [];

    public FilteredRows(ObservableCollection<QuickRow> source, Func<QuickRow, bool> keep)
    {
        this.source = source;
        this.keep = keep;
        shown.AddRange(source.Where(keep));
        WeakEvents.CollectionChanged.Subscribe(source, this);
    }

    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public void OnEvent(object? sender, WeakEvent ev, NotifyCollectionChangedEventArgs e)
    {
        // One row added or taken away (how the search fills the list) passes through as that; anything else starts over.
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is [QuickRow added])
        {
            if (!keep(added)) return;
            int at = source.Take(e.NewStartingIndex).Count(keep);
            shown.Insert(at, added);
            CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, added, at));
            return;
        }
        if (e.Action == NotifyCollectionChangedAction.Remove && e.OldItems is [QuickRow removed])
        {
            int at = shown.IndexOf(removed);
            if (at < 0) return;
            shown.RemoveAt(at);
            CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, removed, at));
            return;
        }
        shown.Clear();
        shown.AddRange(source.Where(keep));
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    public QuickRow this[int index] => shown[index];

    public int Count => shown.Count;

    public IEnumerator<QuickRow> GetEnumerator() => shown.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => shown.GetEnumerator();

    // Read-only: the rows are the search's to change.
    object? IList.this[int index]
    {
        get => shown[index];
        set => throw new NotSupportedException();
    }

    bool IList.IsFixedSize => false;

    bool IList.IsReadOnly => true;

    bool ICollection.IsSynchronized => false;

    object ICollection.SyncRoot => this;

    int IList.Add(object? value) => throw new NotSupportedException();

    void IList.Clear() => throw new NotSupportedException();

    bool IList.Contains(object? value) => value is QuickRow r && shown.Contains(r);

    int IList.IndexOf(object? value) => value is QuickRow r ? shown.IndexOf(r) : -1;

    void IList.Insert(int index, object? value) => throw new NotSupportedException();

    void IList.Remove(object? value) => throw new NotSupportedException();

    void IList.RemoveAt(int index) => throw new NotSupportedException();

    void ICollection.CopyTo(Array array, int index) => ((ICollection)shown).CopyTo(array, index);
}
