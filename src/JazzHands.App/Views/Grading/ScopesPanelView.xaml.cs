using System.Windows;
using System.Windows.Controls;
using JazzHands.App.ViewModels.Grading;

namespace JazzHands.App.Views.Grading;

/// <summary>The scopes.</summary>
public partial class ScopesPanelView : UserControl
{
    /// <summary>Creates the panel.</summary>
    public ScopesPanelView()
    {
        InitializeComponent();

        // On screen is the view's to say: a panel behind another tab, closed or minimised is not
        // visible, and the engine stops measuring for it.
        IsVisibleChanged += (_, _) => Shown();
        DataContextChanged += (_, _) => Shown();
    }

    private void Shown() => (DataContext as ScopesPanelViewModel)?.SetShown(IsVisible);
}
