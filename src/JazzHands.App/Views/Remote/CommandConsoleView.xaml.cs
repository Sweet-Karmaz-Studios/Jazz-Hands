using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using JazzHands.App.ViewModels.Remote;

namespace JazzHands.App.Views.Remote;

/// <summary>
/// The Command Console. Its one piece of behaviour: the log follows the newest line, the way a
/// terminal does, unless a line is selected to be read.
/// </summary>
public partial class CommandConsoleView : UserControl
{
    private CommandConsoleViewModel? _model;
    private bool _scrollQueued;

    /// <summary>Creates the view.</summary>
    public CommandConsoleView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        _model?.Visible.CollectionChanged -= OnLogChanged;
        _model = e.NewValue as CommandConsoleViewModel;
        _model?.Visible.CollectionChanged += OnLogChanged;
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || _scrollQueued)
        {
            return;
        }

        // Not now: this handler can run before the list's own has taken the new line in, and
        // scrolling from here lays the list out against items it has not heard of, which WPF
        // stops the editor for ("An ItemsControl is inconsistent with its items source"). Once
        // the notification is over, the list has the line.
        _scrollQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _scrollQueued = false;
            if (_model is { SelectedEntry: null } model && model.Visible.Count > 0)
            {
                Log.ScrollIntoView(model.Visible[^1]);
            }
        });
    }
}
