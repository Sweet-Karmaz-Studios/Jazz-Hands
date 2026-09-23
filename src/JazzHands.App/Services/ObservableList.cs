using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace JazzHands.App.Services;

/// <summary>
/// An observable collection that can be refilled in one notification.
/// </summary>
/// <remarks>
/// Clearing and re-adding an <see cref="ObservableCollection{T}"/> raises one event per item, and
/// each one makes the bound list rebuild containers. A search box that refilters on every
/// keystroke does that several times a second, so the refill is a single reset instead. See
/// <c>Docs/PERF.md</c> for the measurement behind this.
/// </remarks>
public sealed class ObservableList<T> : ObservableCollection<T>
{
    /// <summary>Replaces everything with <paramref name="items"/>, raising one reset.</summary>
    public void Reset(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        CheckReentrancy();

        Items.Clear();
        foreach (T item in items)
        {
            Items.Add(item);
        }

        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
