namespace JazzHands.Core.Time;

/// <summary>
/// How a conversion that lands between two discrete units resolves. Always pass this
/// explicitly: the right answer differs between "which frame is on screen at t" (Floor),
/// "snap the user's drag" (Nearest) and "the first frame after t" (Ceiling).
/// </summary>
public enum RoundingMode
{
    /// <summary>Toward negative infinity. The frame displayed at time t.</summary>
    Floor,

    /// <summary>Toward positive infinity. The first frame at or after time t.</summary>
    Ceiling,

    /// <summary>Toward zero.</summary>
    Truncate,

    /// <summary>To the closest unit, ties away from zero. Snapping user input.</summary>
    Nearest,
}
