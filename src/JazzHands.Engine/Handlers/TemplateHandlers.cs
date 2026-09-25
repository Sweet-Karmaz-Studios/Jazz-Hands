using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Nodes;
using JazzHands.Core.Commands;
using JazzHands.Core.Effects;
using JazzHands.Core.Model;
using JazzHands.Core.Templates;
using JazzHands.Core.Time;
using JazzHands.Engine.Commands;
using JazzHands.Engine.Effects;
using JazzHands.Engine.Selection;
using JazzHands.Engine.Templates;
using Microsoft.Extensions.DependencyInjection;

namespace JazzHands.Engine.Handlers;

/// <summary>Places a motion template.</summary>
public sealed class ApplyTemplateHandler : ICommandHandler<ApplyTemplateCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, ApplyTemplateCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        MotionTemplate template = TemplateLibrary.Require(command.Name);
        Sequence sequence = HandlerHelp.Sequence(project, command.SequenceId);
        if (command.At < Flicks.Zero)
        {
            throw new CommandException("time-out-of-range", "A template cannot start before the sequence does.", "at");
        }

        // The command line splits a list at its commas, so a piece with no name is the rest of the
        // value before it: position=0, 330 and text=Hello, world arrive in two pieces each.
        var given = new Dictionary<string, string>(StringComparer.Ordinal);
        string? last = null;
        foreach (string pair in command.Params ?? [])
        {
            int split = pair.IndexOf('=', StringComparison.Ordinal);
            if (split <= 0 && last is not null)
            {
                given[last] += "," + pair;
                continue;
            }

            if (split <= 0)
            {
                throw new CommandException("invalid-value", $"'{pair}' is not name=value.", "param");
            }

            last = pair[..split].Trim();
            given[last] = pair[(split + 1)..];
        }

        IReadOnlyList<ICommand> steps = template.Expand(given, command.At, project.SettingsFor(sequence).FrameRate);

        // The steps work on the active sequence, so the one asked for is made so while they run.
        string? active = project.ActiveSequenceId;
        Project working = project with { ActiveSequenceId = sequence.Id };
        for (int index = 0; index < steps.Count; index++)
        {
            try
            {
                working = context.Run(working, steps[index]);
            }
            catch (CommandException error)
            {
                throw new CommandException(error.Code, $"Step {index + 1} of '{template.Name}' ({CommandRegistry.NameOf(steps[index])}) was refused: {error.Message}", error.Path);
            }
        }

        return working with { ActiveSequenceId = active };
    }
}

/// <summary>Lists the motion templates.</summary>
public sealed class ListTemplatesHandler : IQueryHandler<ListTemplatesQuery, TemplateInfo[]>
{
    /// <inheritdoc />
    public TemplateInfo[] Handle(Project project, ListTemplatesQuery query, QueryContext context) =>
        [.. TemplateLibrary.All.Select(TemplateLibrary.Info)];
}

/// <summary>Saves clips as a motion template.</summary>
public sealed class SaveTemplateHandler : ICommandHandler<SaveTemplateCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SaveTemplateCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        string[] ids = command.ClipIds is { Length: > 0 } named
            ? named
            : [.. context.Services?.GetService<SelectionService>()?.Ids.Where(id => project.FindClip(id) is not null) ?? []];
        if (ids.Length == 0)
        {
            throw new CommandException("nothing-selected", "Select the clips to save, or name them with --clips.", "clips");
        }

        ClipLocation[] clips = [.. ids.Select(id => HandlerHelp.Clip(project, id)).OrderBy(found => found.Clip.Start.Value)];
        var promote = new HashSet<string>(command.Promote ?? [], StringComparer.Ordinal);
        var writer = new TemplateWriter(project, promote);
        MotionTemplate template = writer.Write(command.Name, command.Label ?? string.Empty, command.Description ?? string.Empty, clips);
        if (MotionTemplate.Check(template) is { } problem)
        {
            throw new CommandException("invalid-template", problem, "name");
        }

        string path = Path.Combine(TemplateLibrary.UserFolder, template.Name + ".json");
        if (File.Exists(path) && !command.Force)
        {
            throw new CommandException("template-exists", $"There is already a template '{template.Name}' at {path}. --force replaces it.", "name");
        }

        Directory.CreateDirectory(TemplateLibrary.UserFolder);
        File.WriteAllText(path, template.ToJson());
        context.Changed(clips.Select(found => found.Clip.Id));
        return project;
    }

    /// <summary>Clips, as the steps that would make them again.</summary>
    private sealed class TemplateWriter(Project project, HashSet<string> promote)
    {
        private readonly JsonArray _steps = [];
        private readonly SortedDictionary<string, TemplateParam> _params = new(StringComparer.Ordinal);

        public MotionTemplate Write(string name, string label, string description, ClipLocation[] clips)
        {
            Flicks first = clips[0].Clip.Start;
            var tracks = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Track track in clips.Select(found => found.Track).DistinctBy(track => track.Id).OrderBy(track => track.Order))
            {
                string key = $"${{id:track-{tracks.Count + 1}}}";
                tracks[track.Id] = key;
                Step("track.add", new JsonObject { ["kind"] = track.Kind.ToString().ToLowerInvariant(), ["trackId"] = key });
            }

            int media = 0;
            for (int index = 0; index < clips.Length; index++)
            {
                Clip clip = clips[index].Clip;
                string key = $"${{id:clip-{index + 1}}}";
                var args = new JsonObject
                {
                    ["trackId"] = tracks[clips[index].Track.Id],
                    ["at"] = Moment(clip.Start - first),
                    ["duration"] = Timecode.FormatClock(clip.Duration),
                    ["clipId"] = key,
                    ["name"] = clip.Name,
                    ["withAudio"] = "false",
                };

                if (clip.GeneratorId is { } generator)
                {
                    args["generatorId"] = generator;
                }
                else if (clip.MediaId is { } mediaId)
                {
                    // A file is the thing most likely to change from one use to the next.
                    string slot = ++media == 1 ? "media" : $"media-{media}";
                    _params[slot] = new TemplateParam("media", mediaId, $"The file '{clip.Name}' plays: a media id in the project.");
                    args["mediaId"] = $"${{{slot}}}";
                    args["sourceIn"] = Timecode.FormatClock(clip.SourceIn);
                    args["sourceStreamIndex"] = clip.SourceStreamIndex.ToString(CultureInfo.InvariantCulture);
                }
                else
                {
                    continue;
                }

                Step("clip.add", args);
                if (ParamTargets.Find(project, clip.Id) is { } owner)
                {
                    foreach (ParamDescriptor descriptor in ParamTargets.Params(owner, EffectCatalog.Registry))
                    {
                        Value(key, descriptor, ParamTargets.Get(owner, descriptor.Name));
                    }
                }

                int effectIndex = 0;
                foreach (Effect effect in clip.Effects.Where(effect => !EffectChains.IsOwnParameters(clip, effect)))
                {
                    string effectKey = $"${{id:effect-{index + 1}-{++effectIndex}}}";
                    Step("effect.add", new JsonObject { ["ownerId"] = key, ["typeId"] = effect.TypeId, ["effectId"] = effectKey });
                    EffectDescriptor? type = EffectCatalog.Registry.Find(effect.TypeId);
                    foreach (EffectParameter parameter in effect.Parameters)
                    {
                        if (type?.Param(parameter.Name) is { } descriptor)
                        {
                            Value(effectKey, descriptor, parameter.Value);
                        }
                    }
                }
            }

            return new MotionTemplate(name, label, description, _params.ToImmutableSortedDictionary(StringComparer.Ordinal), _steps);
        }

        /// <summary>A parameter as it is set: a value, keyframes or a driver.</summary>
        private void Value(string owner, ParamDescriptor descriptor, AnimatedValue? stored)
        {
            switch (stored)
            {
                case StaticValue fixedValue:
                    string text = ParamValues.Format(fixedValue.Value);
                    if (promote.Contains(descriptor.Name))
                    {
                        _params.TryAdd(descriptor.Name, new TemplateParam(TypeOf(fixedValue.Value), text, descriptor.Description));
                        text = $"${{{descriptor.Name}}}";
                    }

                    Step("param.set", new JsonObject { ["ownerId"] = owner, ["param"] = descriptor.Name, ["value"] = text });
                    break;

                case KeyframedValue keyed:
                    foreach (Keyframe keyframe in keyed.Keyframes)
                    {
                        Step("keyframe.add", new JsonObject
                        {
                            ["ownerId"] = owner,
                            ["param"] = descriptor.Name,
                            ["at"] = Timecode.FormatClock(keyframe.Time),
                            ["value"] = ParamValues.Format(keyframe.Value),
                            ["interp"] = Kebab(keyframe.Interp.ToString()),
                            ["local"] = "true",
                        });
                    }

                    break;

                case DrivenValue driven:
                    Value(owner, descriptor, driven.Base);
                    Step("param.set-driver", new JsonObject { ["ownerId"] = owner, ["param"] = descriptor.Name, ["expression"] = driven.Expression });
                    break;
            }
        }

        private void Step(string command, JsonObject args) => _steps.Add(new JsonObject { ["command"] = command, ["args"] = args });

        private static string Moment(Flicks offset) => offset <= Flicks.Zero
            ? "${at}"
            : string.Create(CultureInfo.InvariantCulture, $"${{at+{Math.Round(offset.ToSeconds(), 4)}}}");

        private static string TypeOf(ParamValue value) => value switch
        {
            ParamValue.Color => "color",
            ParamValue.Float or ParamValue.Int => "number",
            ParamValue.Bool => "switch",
            _ => "text",
        };

        private static string Kebab(string name) =>
            string.Concat(name.Select((letter, index) => char.IsUpper(letter) && index > 0 ? "-" + char.ToLowerInvariant(letter) : char.ToLowerInvariant(letter).ToString()));
    }
}
