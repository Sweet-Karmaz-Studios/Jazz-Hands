namespace JazzHands.Core.Commands;

/// <summary>Looks for CLAP plugins on this computer.</summary>
/// <remarks>
/// Reads every .clap file in CLAP's standard folders (<c>%COMMONPROGRAMFILES%\CLAP</c>,
/// <c>%LOCALAPPDATA%\Programs\Common\CLAP</c>) and any folder given, each in a process of its own,
/// so a file that crashes on reading is only noted: later scans leave it alone until
/// <c>--again</c>. What was found is kept between runs, and a file already read is read again only
/// when it has changed.
/// </remarks>
/// <param name="Folder">Another folder to look in.</param>
/// <param name="Again">Read every file again, including ones that crashed.</param>
[Command("plugin.scan", Description = "Look for CLAP plugins on this computer",
    Undoable = false,
    NotUndoableReason = "It reads plugin files and keeps a list of them beside the settings. The project does not change.")]
public sealed record ScanPluginsCommand(
    [property: Option("folder", "Another folder to look in")] string? Folder = null,
    [property: Option("again", "Read every file again, including ones that crashed")] bool Again = false) : ICommand;

/// <summary>The CLAP plugins found on this computer.</summary>
[Query("plugin.list", Description = "List the CLAP plugins found on this computer")]
public sealed record ListPluginsQuery : IQuery<PluginsInfo>;

/// <summary>Puts a CLAP plugin on a clip's or a track's sound.</summary>
/// <remarks>
/// The plugin runs in a process of its own; its parameters are the effect's, named <c>p</c> and
/// their CLAP id (<c>plugin.params</c> lists them), set and keyframed with <c>param.set</c> and
/// the keyframe commands like any effect's. A plugin with latency goes on a clip, where the mixer
/// makes up for it. One undo.
/// </remarks>
/// <param name="OwnerId">The clip or track.</param>
/// <param name="PluginId">The plugin's id, from <c>plugin.list</c>.</param>
/// <param name="Index">Where in the chain; the end when left out.</param>
/// <param name="EffectId">The identifier to give the effect.</param>
[Command("plugin.add", Description = "Put a CLAP plugin on a clip or track")]
public sealed record AddPluginCommand(
    [property: Arg(0, "The clip or track id")] string OwnerId,
    [property: Arg(1, "The plugin's id, from plugin.list")] string PluginId,
    [property: Option("index", "Where in the chain")] int? Index = null,
    [property: Option("id", "The identifier to give the effect")] string? EffectId = null) : ICommand;

/// <summary>A plugin effect's parameters: what they are, and their values now.</summary>
/// <param name="EffectId">The plugin effect.</param>
[Query("plugin.params", Description = "List a plugin effect's parameters and their values")]
public sealed record PluginParamsQuery(
    [property: Arg(0, "The plugin effect's id")] string EffectId) : IQuery<PluginParamInfo[]>;

/// <summary>Keeps a plugin's own state in the project.</summary>
/// <remarks>
/// Asks the plugin for its state (from the running one while the editor plays it, which is where
/// changes made in its own window are) and keeps it on the effect as base64, so the project
/// reopens with the plugin as it was. One undo.
/// </remarks>
/// <param name="EffectId">The plugin effect.</param>
[Command("plugin.save-state", Description = "Keep a plugin's own state in the project")]
public sealed record SavePluginStateCommand(
    [property: Arg(0, "The plugin effect's id")] string EffectId) : ICommand;
