using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;

namespace JazzHands.App.Services;

/// <summary>A point parameter shown on the preview, in sequence pixels from the frame centre.</summary>
/// <param name="Label">What it is, for a tooltip.</param>
/// <param name="Position">Where it is.</param>
/// <param name="Active">True for the one being picked.</param>
public readonly record struct PreviewMarker(string Label, Vector2 Position, bool Active);

/// <summary>
/// Lets the inspector pick a point on the preview, and shows its points there.
/// </summary>
/// <remarks>
/// The inspector publishes the point parameters of what it is showing as markers; the preview
/// draws them as crosshairs. Pressing a row's crosshair starts a pick, and the next click on the
/// picture ends it by handing the row the place clicked, which it sends as an ordinary command.
/// Shared by the two panels, which otherwise know nothing of each other.
/// </remarks>
public sealed partial class PointPicker : ObservableObject
{
    private Action<Vector2>? _pick;

    /// <summary>The points to draw.</summary>
    [ObservableProperty]
    private IReadOnlyList<PreviewMarker> _markers = [];

    /// <summary>What is being picked, for the preview's hint; empty when nothing is.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPicking))]
    private string _picking = string.Empty;

    /// <summary>True while the next click on the picture will set a point.</summary>
    public bool IsPicking => Picking.Length > 0;

    /// <summary>Starts picking; a pick already under way is given up.</summary>
    /// <param name="label">What is being picked.</param>
    /// <param name="pick">Called with the place clicked, in sequence pixels from the frame centre.</param>
    public void Begin(string label, Action<Vector2> pick)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentNullException.ThrowIfNull(pick);

        _pick = pick;
        Picking = label;
    }

    /// <summary>Gives up a pick.</summary>
    public void Cancel()
    {
        _pick = null;
        Picking = string.Empty;
    }

    /// <summary>The picture was clicked: hands the place to whoever asked, and stops picking.</summary>
    /// <returns>False when nothing was being picked, so the click is the preview's own.</returns>
    public bool Pick(Vector2 fromCentre)
    {
        Action<Vector2>? pick = _pick;
        if (pick is null)
        {
            return false;
        }

        Cancel();
        pick(fromCentre);
        return true;
    }
}
