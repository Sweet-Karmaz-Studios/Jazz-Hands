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
    private readonly DragStart _drag = new();

    /// <summary>Creates the view.</summary>
    public EffectsPanelView() => InitializeComponent();

    private void OnEffectMouseDown(object sender, MouseButtonEventArgs e)
    {
        _drag.Press(e.GetPosition(this), ((FrameworkElement)sender).DataContext);

        if (e.ClickCount == 2 && ((FrameworkElement)sender).DataContext is EffectTypeItemViewModel effect)
        {
            effect.ApplyCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnEffectMouseMove(object sender, MouseEventArgs e)
    {
        if (Dragged(e) is EffectTypeItemViewModel effect)
        {
            DragDrop.DoDragDrop((DependencyObject)sender, EffectDragData.ForEffect(effect.TypeId), DragDropEffects.Copy);
        }
    }

    private void OnPresetMouseMove(object sender, MouseEventArgs e)
    {
        if (Dragged(e) is EffectPresetItemViewModel preset)
        {
            DragDrop.DoDragDrop((DependencyObject)sender, EffectDragData.ForPreset(preset.Id), DragDropEffects.Copy);
        }
    }

    private void OnTitlePresetMouseMove(object sender, MouseEventArgs e)
    {
        if (Dragged(e) is TitlePresetItemViewModel preset)
        {
            DragDrop.DoDragDrop((DependencyObject)sender, EffectDragData.ForTitlePreset(preset.Name), DragDropEffects.Copy);
        }
    }

    /// <summary>The item pressed, once the pointer has moved far enough to drag it; whatever row it is over now.</summary>
    private object? Dragged(MouseEventArgs e) => _drag.Take(
        e.GetPosition(this),
        e.LeftButton == MouseButtonState.Pressed,
        new Size(SystemParameters.MinimumHorizontalDragDistance, SystemParameters.MinimumVerticalDragDistance));
}
