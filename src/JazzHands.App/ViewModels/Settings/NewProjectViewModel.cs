using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JazzHands.App.Services;
using JazzHands.Core.Time;

namespace JazzHands.App.ViewModels.Settings;

/// <summary>A starting shape for a project: a frame rate and a size, named for what it is for.</summary>
/// <param name="Label">What the list says.</param>
/// <param name="Fps">The frame rate.</param>
/// <param name="Width">The width in pixels.</param>
/// <param name="Height">The height in pixels.</param>
public sealed record ProjectPreset(string Label, Rational Fps, int Width, int Height)
{
    /// <inheritdoc />
    public override string ToString() => Label;
}

/// <summary>
/// File, New Project: a name and a starting shape. The project is made with <c>project.new</c>,
/// as <c>jazz new</c> makes one, and saved wherever the first save puts it.
/// </summary>
public sealed partial class NewProjectViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    private string _name = "Untitled";

    [ObservableProperty]
    private ProjectPreset _preset;

    /// <summary>A dialog on the first preset.</summary>
    public NewProjectViewModel() => _preset = Presets[0];

    /// <summary>Raised when the dialog should close.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>The shapes to start from: 1080p60, 4K60, 1080p30 and vertical for phones.</summary>
    public static IReadOnlyList<ProjectPreset> Presets { get; } =
    [
        new("1080p, 60 fps (game capture)", new Rational(60, 1), 1920, 1080),
        new("4K, 60 fps", new Rational(60, 1), 3840, 2160),
        new("1080p, 30 fps", new Rational(30, 1), 1920, 1080),
        new("Vertical 1080x1920, 30 fps (Shorts, TikTok)", new Rational(30, 1), 1080, 1920),
        new("1080p, 29.97 fps (broadcast)", Rational.Fps2997, 1920, 1080),
    ];

    /// <summary>What was chosen, or null when the dialog was cancelled.</summary>
    public NewProjectChoice? Choice { get; private set; }

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private void Create()
    {
        Choice = new NewProjectChoice(Name.Trim(), Preset.Fps, Preset.Width, Preset.Height);
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private bool CanCreate() => Name.Trim().Length > 0;

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, EventArgs.Empty);
}
