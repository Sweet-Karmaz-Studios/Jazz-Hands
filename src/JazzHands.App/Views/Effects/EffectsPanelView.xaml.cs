using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using JazzHands.App.Services;
using JazzHands.App.ViewModels.Effects;

namespace JazzHands.App.Views.Effects;

/// <summary>
/// The effects browser. Dragging is view logic: an effect or a preset dragged out carries its id
/// to the timeline or the inspector, which turn the drop into a command. A double click applies
/// an effect to the selection.
/// </summary>
public partial class EffectsPanelView : UserControl
{
    private Point? _pressed;

    /// <summary>Creates the view.</summary>
    public EffectsPanelView() => InitializeComponent();

    private void OnEffectMouseDown(object sender, MouseButtonEventArgs e)
    {
        _pressed = e.GetPosition(this);

        if (e.ClickCount == 2 && ((FrameworkElement)sender).DataContext is EffectTypeItemViewModel effect)
        {
            effect.ApplyCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnEffectMouseMove(object sender, MouseEventArgs e)
    {
        if (StartsDrag(e) && ((FrameworkElement)sender).DataContext is EffectTypeItemViewModel effect)
        {
            DragDrop.DoDragDrop((DependencyObject)sender, EffectDragData.ForEffect(effect.TypeId), DragDropEffects.Copy);
        }
    }

    private void OnPresetMouseMove(object sender, MouseEventArgs e)
    {
        if (StartsDrag(e) && ((FrameworkElement)sender).DataContext is EffectPresetItemViewModel preset)
        {
            DragDrop.DoDragDrop((DependencyObject)sender, EffectDragData.ForPreset(preset.Id), DragDropEffects.Copy);
        }
    }

    private bool StartsDrag(MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _pressed is not { } pressed)
        {
            return false;
        }

        Vector moved = e.GetPosition(this) - pressed;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return false;
        }

        _pressed = null;
        return true;
    }
}
