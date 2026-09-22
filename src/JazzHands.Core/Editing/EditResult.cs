namespace JazzHands.Core.Editing;

/// <summary>Why an edit could not be done, in a form the CLI, the API and the UI can all show.</summary>
/// <param name="Code">A stable kebab-case code, for example clip-not-found.</param>
/// <param name="Message">A sentence a person can act on.</param>
public sealed record EditError(string Code, string Message)
{
    /// <summary>The clip named does not exist on the track.</summary>
    public static EditError ClipNotFound(string clipId) =>
        new("clip-not-found", $"No clip with id '{clipId}' on this track.");

    /// <summary>The track named does not exist in the sequence.</summary>
    public static EditError TrackNotFound(string trackId) =>
        new("track-not-found", $"No track with id '{trackId}' in this sequence.");

    /// <summary>The track is locked and refuses edits.</summary>
    public static EditError TrackLocked(string trackName) =>
        new("track-locked", $"Track '{trackName}' is locked.");

    /// <summary>A time fell outside the range the operation allows.</summary>
    public static EditError TimeOutOfRange(string message) => new("time-out-of-range", message);

    /// <summary>The edit would leave a clip with no duration.</summary>
    public static EditError EmptyResult(string message) => new("empty-result", message);

    /// <summary>The edit would run past the end of the source material.</summary>
    public static EditError NoSourceLeft(string message) => new("no-source-left", message);

    /// <summary>The edit would overlap another clip on the same track.</summary>
    public static EditError WouldOverlap(string message) => new("would-overlap", message);

    /// <summary>The two clips given are not next to each other.</summary>
    public static EditError NotAdjacent(string message) => new("not-adjacent", message);

    /// <summary>The operation was given nothing to work on.</summary>
    public static EditError NothingSelected(string message) => new("nothing-selected", message);
}

/// <summary>
/// The outcome of an editing operation: a new value, or a reason it could not be done.
/// </summary>
/// <remarks>
/// Editing operations fail for ordinary reasons all the time, because people drag clips past the
/// end of their source and trim them to nothing. Those are results, not exceptions; exceptions
/// are for bugs. The command layer turns an <see cref="EditError"/> into a CommandException so
/// the CLI can exit with a code and the UI can show a message.
/// </remarks>
/// <typeparam name="T">What the operation produces when it succeeds.</typeparam>
public readonly record struct EditResult<T>
{
    private readonly T? _value;

    private EditResult(T? value, EditError? error)
    {
        _value = value;
        Error = error;
    }

    /// <summary>Why it failed, or null when it succeeded.</summary>
    public EditError? Error { get; }

    /// <summary>True when the operation produced a value.</summary>
    public bool IsOk => Error is null;

    /// <summary>The value. Throws when the operation failed, so check <see cref="IsOk"/> first.</summary>
    public T Value => Error is null
        ? _value!
        : throw new InvalidOperationException($"The edit failed: {Error.Code}: {Error.Message}");

    /// <summary>A successful result.</summary>
    public static EditResult<T> Ok(T value) => new(value, null);

    /// <summary>A failed result.</summary>
    public static EditResult<T> Fail(EditError error) => new(default, error);

    /// <summary>Converts a value into a successful result.</summary>
    public static implicit operator EditResult<T>(T value) => Ok(value);

    /// <summary>Converts an error into a failed result.</summary>
    public static implicit operator EditResult<T>(EditError error) => Fail(error);

    /// <summary>Applies a function to the value when the operation succeeded.</summary>
    public EditResult<TNext> Then<TNext>(Func<T, EditResult<TNext>> next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return Error is null ? next(_value!) : EditResult<TNext>.Fail(Error);
    }

    /// <summary>The value when it succeeded, otherwise the fallback.</summary>
    public T ValueOr(T fallback) => Error is null ? _value! : fallback;
}
