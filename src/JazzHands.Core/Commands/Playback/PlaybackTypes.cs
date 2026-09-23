namespace JazzHands.Core.Commands;

/// <summary>How much of the frame the preview renders.</summary>
/// <remarks>
/// A scale on the working resolution, not on the decode: a 4K source is still decoded at 4K, and
/// what gets cheaper is everything drawn after it. Auto is Full while the playhead sits still and
/// Half while it is being dragged or shuttled, which is what keeps a scrub responsive.
/// </remarks>
public enum PreviewQuality
{
    /// <summary>The sequence's own resolution.</summary>
    Full,

    /// <summary>Half the width and half the height.</summary>
    Half,

    /// <summary>A quarter of each.</summary>
    Quarter,

    /// <summary>Full when still, Half while scrubbing or shuttling.</summary>
    Auto,
}

/// <summary>Somewhere the playhead can be sent by name.</summary>
public enum GoToTarget
{
    /// <summary>The first frame of the sequence.</summary>
    Start,

    /// <summary>The last frame of the sequence.</summary>
    End,

    /// <summary>The next place a clip starts or ends, on any track.</summary>
    NextEdit,

    /// <summary>The previous place a clip starts or ends.</summary>
    PrevEdit,

    /// <summary>The next sequence marker.</summary>
    NextMarker,

    /// <summary>The previous sequence marker.</summary>
    PrevMarker,

    /// <summary>The in point.</summary>
    In,

    /// <summary>The last frame inside the out point.</summary>
    Out,
}
