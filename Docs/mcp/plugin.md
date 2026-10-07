# Plugins

The CLAP audio plugins installed on this computer (reverbs, compressors, de-essers, voice effects), used as effects on clips and tracks. Each runs in a process of its own, so one that crashes is bypassed and nothing else stops.

- `plugin_scan` looks in the standard CLAP folders (and `folder`, if given) for plugins it has not read yet. A file that crashed when read is left alone until `again`. `plugin_list` names what was found, with each plugin's id. The editor also scans by itself a few seconds after it starts.
- `plugin_add` puts a plugin on a clip or a track by its id. A plugin that delays its sound is made up for on either: a clip reads its file ahead, a track mixes its clips ahead, so it stays in time with the rest.
- `plugin_params` lists the plugin's parameters: each has a name on the effect (`p` and its CLAP id, such as `p1`), its range and its value now. Set one with `param_set`, keyframe it with the keyframe tools, like any effect parameter.
- `plugin_save_state` keeps the plugin's own state in the project, which covers what its own window changed. A project opened where a plugin is not installed keeps the effect, bypassed, and `diagnostics_list` names it (`plugin-missing`).
