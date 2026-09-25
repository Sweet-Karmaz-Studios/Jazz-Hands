using CommunityToolkit.Mvvm.ComponentModel;
using JazzHands.Core.Export;

namespace JazzHands.App.ViewModels.Export;

/// <summary>One mode the export dialog offers, and whether the planner would take it for what is on the timeline now.</summary>
public sealed partial class ExportModeChoice(ExportMode mode, string label) : ObservableObject
{
    [ObservableProperty]
    private string _note = string.Empty;

    [ObservableProperty]
    private bool _isAvailable = true;

    /// <summary>The mode.</summary>
    public ExportMode Mode { get; } = mode;

    /// <summary>Its name in the dialog.</summary>
    public string Label { get; } = label;

    /// <summary>The label, which is what screen readers and the closed combo box's text search use.</summary>
    public override string ToString() => Label;
}
