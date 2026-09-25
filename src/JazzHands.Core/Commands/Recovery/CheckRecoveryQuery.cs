namespace JazzHands.Core.Commands;

/// <summary>
/// Whether the editor closed without saving this project, and what can be brought back: the
/// commands since the last save, the autosave copy, and projects that were never saved.
/// </summary>
[Query("recovery.check", Description = "Say whether there is unsaved work to recover beside the project, and how much")]
public sealed record CheckRecoveryQuery : IQuery<RecoveryInfo>;

/// <summary>What <c>recovery.check</c> found.</summary>
/// <param name="Available">True when there is work to recover beside this project.</param>
/// <param name="ProjectPath">The project it is about.</param>
/// <param name="Description">A sentence for a person.</param>
/// <param name="Since">When the project was last saved, or when autosave last wrote, whichever the recovery starts from.</param>
/// <param name="Commands">Commands since the project was last saved or opened.</param>
/// <param name="HasCopy">True when autosave left a copy to fall back on.</param>
/// <param name="Untitled">Projects that were never saved and were rescued when the editor crashed, newest first.</param>
public sealed record RecoveryInfo(
    bool Available,
    string ProjectPath,
    string Description,
    DateTimeOffset? Since,
    int Commands,
    bool HasCopy,
    UntitledRecoveryInfo[] Untitled);

/// <summary>A never saved project a crash rescued.</summary>
/// <param name="Path">The file, for <c>recovery.accept --file</c>.</param>
/// <param name="Name">The project's name.</param>
/// <param name="SavedAt">When it was rescued.</param>
public sealed record UntitledRecoveryInfo(string Path, string Name, DateTimeOffset SavedAt);
