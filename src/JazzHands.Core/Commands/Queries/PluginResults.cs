namespace JazzHands.Core.Commands;

/// <summary>A CLAP plugin found on this computer.</summary>
/// <param name="Id">Its id, for <c>plugin.add</c>.</param>
/// <param name="Name">Its name.</param>
/// <param name="Vendor">Who makes it.</param>
/// <param name="Version">Its version.</param>
/// <param name="Library">Its file.</param>
/// <param name="Features">Its feature tags (<c>audio-effect</c>, <c>reverb</c>).</param>
public sealed record PluginInfo(string Id, string Name, string Vendor, string Version, string Library, string[] Features);

/// <summary>What a plugin scan found.</summary>
/// <param name="Plugins">Every plugin, by name.</param>
/// <param name="Crashed">Files that crashed when read, left alone until <c>plugin.scan --again</c>.</param>
/// <param name="Folders">The folders scans look in.</param>
public sealed record PluginsInfo(PluginInfo[] Plugins, string[] Crashed, string[] Folders);

/// <summary>A plugin parameter.</summary>
/// <param name="Name">Its name on the effect, for <c>param.set</c>: <c>p</c> and its id.</param>
/// <param name="Id">Its CLAP id.</param>
/// <param name="Label">What the plugin calls it.</param>
/// <param name="Min">Its lowest value.</param>
/// <param name="Max">Its highest.</param>
/// <param name="Default">Its default.</param>
/// <param name="Value">Its value now.</param>
public sealed record PluginParamInfo(string Name, uint Id, string Label, double Min, double Max, double Default, double Value);
