using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using JazzHands.App.ViewModels.Remote;

namespace JazzHands.App.Views.Remote;

/// <summary>
/// The Command Console. Its one piece of behaviour: the log follows the newest line, the way a
/// terminal does, unless a line is selected to be read.
/// </summary>
public partial class CommandConsoleView : UserControl
{
    private CommandConsoleViewModel? _model;

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
        if (e.Action == NotifyCollectionChangedAction.Add && _model is { SelectedEntry: null } model && model.Visible.Count > 0)
        {
            Log.ScrollIntoView(model.Visible[^1]);
        }
    }
}
