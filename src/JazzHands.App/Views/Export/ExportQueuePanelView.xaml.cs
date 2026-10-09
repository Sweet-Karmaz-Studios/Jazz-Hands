using System.Collections.Specialized;
using System.Windows.Controls;
using System.Windows.Threading;

namespace JazzHands.App.Views.Export;

/// <summary>The Export Queue panel. Everything it does is its viewmodel's, but for scrolling.</summary>
public partial class ExportQueuePanelView : UserControl
{
    /// <summary>Creates the view.</summary>
    public ExportQueuePanelView()
    {
        InitializeComponent();
        ((INotifyCollectionChanged)JobList.Items).CollectionChanged += OnJobsChanged;
    }

    /// <summary>
    /// A job added comes into view. New jobs go at the end, and below a long list of finished ones
    /// Add to queue seemed to do nothing (seen on screen, 2026-10-09).
    /// </summary>
    private void OnJobsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is { Count: > 0 } added)
        {
            // After layout, when the new row has a container to bring into view.
            object last = added[added.Count - 1]!;
            Dispatcher.BeginInvoke(() => JobList.ScrollIntoView(last), DispatcherPriority.Loaded);
        }
    }
}
