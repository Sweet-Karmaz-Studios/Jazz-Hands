using CommunityToolkit.Mvvm.ComponentModel;
using JazzHands.Core.Commands;
using JazzHands.Core.Model;

namespace JazzHands.App.ViewModels.Grading;

/// <summary>A choice in one of the Colour panel's colour management lists.</summary>
/// <typeparam name="T">What choosing it sets.</typeparam>
/// <param name="Value">The value.</param>
/// <param name="Label">What the list shows.</param>
public sealed record ColorChoice<T>(T Value, string Label);

/// <summary>
/// The Colour panel's colour management (Phase 44): the project's pipeline, display referred or
/// ACES for SDR or HDR10, and in ACES the selected clip's input transform. Both are commands
/// (<c>project.set-color-management</c>, <c>clip.set-input-transform</c>), so they are undoable and
/// reach the CLI and MCP the same way.
/// </summary>
public sealed partial class ColorPanelViewModel
{
    /// <summary>The project's pipelines.</summary>
    public IReadOnlyList<ColorChoice<ColorManagement>> PipelineChoices { get; } =
    [
        new(new ColorManagement(), "Display referred"),
        new(new ColorManagement(ColorPipeline.Aces, AcesOutput.Rec709), "ACES 2.0, SDR Rec.709"),
        new(new ColorManagement(ColorPipeline.Aces, AcesOutput.Hdr10), "ACES 2.0, HDR10"),
    ];

    /// <summary>The input transforms a clip can come in by.</summary>
    public IReadOnlyList<ColorChoice<InputTransform>> InputChoices { get; } =
    [
        new(InputTransform.Auto, "Automatic"),
        new(InputTransform.Srgb, "sRGB (captures, stills, graphics)"),
        new(InputTransform.Rec709, "Rec.709 camera"),
        new(InputTransform.LinearRec709, "Linear Rec.709"),
        new(InputTransform.SLog3, "Sony S-Log3"),
        new(InputTransform.LogC3, "ARRI LogC3"),
        new(InputTransform.VLog, "Panasonic V-Log"),
        new(InputTransform.SdrDisplay, "Made for an SDR display"),
        new(InputTransform.Rec2100Pq, "HDR10 (PQ)"),
    ];

    /// <summary>The project's pipeline.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAces))]
    [NotifyPropertyChangedFor(nameof(ShowInput))]
    private ColorChoice<ColorManagement>? _pipeline;

    /// <summary>The selected clip's input transform, in an ACES project.</summary>
    [ObservableProperty]
    private ColorChoice<InputTransform>? _input;

    /// <summary>True when the selected clip plays a file, so it has an input transform to choose.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowInput))]
    private bool _hasInput;

    /// <summary>True for an ACES project.</summary>
    public bool IsAces => Pipeline?.Value.IsAces == true;

    /// <summary>True when the input list applies: an ACES project and a clip that plays a file.</summary>
    public bool ShowInput => IsAces && HasInput;

    partial void OnPipelineChanged(ColorChoice<ColorManagement>? value)
    {
        if (!_loading && value is not null)
        {
            _ = RunAsync(new SetColorManagementCommand(value.Value.Pipeline, value.Value.Output));
        }
    }

    partial void OnInputChanged(ColorChoice<InputTransform>? value)
    {
        if (!_loading && value is not null && _clipId is { } clip)
        {
            _ = RunAsync(new SetClipInputTransformCommand(clip, value.Value));
        }
    }

    /// <summary>Reads the project's pipeline and the clip's input transform into the lists.</summary>
    private void LoadColorManagement(Project project, Clip? clip)
    {
        ColorManagement current = project.Settings.ColorManagement ?? new ColorManagement();
        Pipeline = PipelineChoices.FirstOrDefault(choice => choice.Value == current)
            ?? PipelineChoices.First(choice => choice.Value.IsAces == current.IsAces);
        HasInput = clip?.MediaId is not null;
        InputTransform input = clip?.InputTransform ?? InputTransform.Auto;
        Input = InputChoices.First(choice => choice.Value == input);
    }
}
