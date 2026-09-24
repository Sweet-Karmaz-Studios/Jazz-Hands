using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using JazzHands.App.Services;
using JazzHands.App.ViewModels.Inspector;

namespace JazzHands.App.Views.Inspector;

/// <summary>
/// The inspector. Drag and drop is view logic: an effect or a preset dropped anywhere on the
/// panel goes on the inspected clip, and an effect's header dragged onto another's moves it there.
/// </summary>
public partial class InspectorPanelView : UserControl
{
    private const string EffectInstanceFormat = "JazzHands.EffectInstance.v1";

    private Point? _pressed;

    /// <summary>Creates the view.</summary>
    public InspectorPanelView()
    {
        InitializeComponent();
        DragOver += OnDragOver;
        Drop += OnDrop;
    }

    private InspectorPanelViewModel? Model => DataContext as InspectorPanelViewModel;

    private void OnDragOver(object sender, DragEventArgs e)
    {
        bool accepted = Model?.ClipId is not null
            && (EffectDragData.Effect(e.Data) is not null || EffectDragData.Preset(e.Data) is not null || e.Data.GetDataPresent(EffectInstanceFormat));
        e.Effects = accepted ? (e.Data.GetDataPresent(EffectInstanceFormat) ? DragDropEffects.Move : DragDropEffects.Copy) : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (Model is not { } model || e.Handled)
        {
            return;
        }

        if (EffectDragData.Effect(e.Data) is { } typeId)
        {
            e.Handled = true;
            await model.AddEffectAsync(typeId).ConfigureAwait(true);
        }
        else if (EffectDragData.Preset(e.Data) is { } presetId)
        {
            e.Handled = true;
            await model.ApplyPresetAsync(presetId).ConfigureAwait(true);
        }
    }

    /// <summary>Starts moving an effect when its header is dragged.</summary>
    private void OnEffectHeaderMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _pressed = null;
            return;
        }

        var header = (FrameworkElement)sender;
        Point here = e.GetPosition(this);
        _pressed ??= here;

        if (Math.Abs(here.Y - _pressed.Value.Y) < SystemParameters.MinimumVerticalDragDistance
            || header.DataContext is not EffectItemViewModel effect)
        {
            return;
        }

        _pressed = null;
        var data = new DataObject();
        data.SetData(EffectInstanceFormat, effect.Id);
        DragDrop.DoDragDrop(header, data, DragDropEffects.Move);
    }

    /// <summary>An effect dropped on another's header takes its place.</summary>
    private void OnEffectHeaderDrop(object sender, DragEventArgs e)
    {
        if (Model is not { } model
            || e.Data.GetData(EffectInstanceFormat) is not string movingId
            || ((FrameworkElement)sender).DataContext is not EffectItemViewModel target
            || model.Effects.FirstOrDefault(effect => effect.Id == movingId) is not { } moving
            || ReferenceEquals(moving, target))
        {
            return;
        }

        e.Handled = true;
        model.Move(moving, target.Index);
    }

    /// <summary>Enter takes what was typed in a text field, as leaving it would.</summary>
    private void OnFieldKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox box)
        {
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            e.Handled = true;
        }
    }
}
