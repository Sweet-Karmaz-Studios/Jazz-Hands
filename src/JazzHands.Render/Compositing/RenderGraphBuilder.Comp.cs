using System.Collections.Immutable;
using System.Numerics;
using JazzHands.Core.Effects;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Render.Scene;

namespace JazzHands.Render.Compositing;

/// <summary>Comp graphs (Phase 49): a clip's node graph evaluated at a frame into steps to draw.</summary>
public static partial class RenderGraphBuilder
{
    /// <summary>Decoder lanes for a comp graph's media nodes start here, apart from every track's.</summary>
    private const int CompLanes = 1 << 21;

    /// <summary>A clip's effects with every comp graph among them given its steps at the frame.</summary>
    private static ImmutableArray<EffectNode> Composited(
        Project project,
        Clip clip,
        Flicks time,
        ImmutableArray<EffectNode> effects,
        IFrameProvider frames,
        Vector2 frameSize,
        (int Width, int Height) output,
        float scale,
        RenderOptions options,
        int depth,
        Rational frameRate)
    {
        if (effects.IsDefaultOrEmpty || !effects.Any(effect => effect.Model is { TypeId: CompGraph.TypeId, Comp: not null }))
        {
            return effects;
        }

        return [.. effects.Select(effect => effect.Model is { TypeId: CompGraph.TypeId, Comp: { } comp }
            ? effect with { Comp = CompSteps(project, clip, comp, time, frames, frameSize, output, scale, options, depth, frameRate) }
            : effect)];
    }

    /// <summary>
    /// A graph as steps: the nodes its output (or the node being looked at) needs, each after what
    /// it reads. A graph with no output, or one that goes round in a circle, draws nothing. A group
    /// (Phase 49a) is its own graph's steps in place, its In the step wired into the group (the
    /// clip's picture when none is), its Out what the group gives; a node being looked at inside a
    /// group is reached through the groups holding it.
    /// </summary>
    private static ImmutableArray<CompStep> CompSteps(
        Project project,
        Clip clip,
        CompGraph comp,
        Flicks time,
        IFrameProvider frames,
        Vector2 frameSize,
        (int Width, int Height) output,
        float scale,
        RenderOptions options,
        int depth,
        Rational frameRate)
    {
        string? view = options.CompView;
        if (Last(comp) is null)
        {
            return [];
        }

        Flicks local = time - clip.Start;
        var steps = ImmutableArray.CreateBuilder<CompStep>();
        return Emit(comp, null, 0) < 0 ? [] : steps.ToImmutable();

        // The node a graph ends at: the one being looked at, the group holding it, or its output.
        CompNode? Last(CompGraph graph) => view is not null && graph.Towards(view) is { } towards ? towards : graph.OutputNode;

        // A graph's steps added to the list; the index of the last, or -1 when it draws nothing.
        int Emit(CompGraph graph, int? groupInput, int level)
        {
            if (Last(graph) is not { } last || graph.OrderFrom(last) is not { Count: > 0 } order)
            {
                return -1;
            }

            var index = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (CompNode node in order)
            {
                string type = node.Effect.TypeId;
                EffectDescriptor? registered = options.Effects.Find(type);
                bool generator = registered is { Kind: EffectKind.Generator };
                ImmutableArray<int> inputs = [.. CompGraph.PortsOf(type, generator).Select(port => node.Input(port) is { } from && index.TryGetValue(from, out int at) ? at : -1)];

                // A group draws its own graph here, with what is wired into it as its In.
                if (type == CompGraph.Group && node.Effect.Enabled && node.Effect.Comp is { } inner && level + 1 < CompGraph.MostDepth)
                {
                    int given = Emit(inner, inputs[0] >= 0 ? inputs[0] : null, level + 1);
                    if (given >= 0)
                    {
                        index[node.Id] = given;
                        continue;
                    }
                }

                CompStep step = type == CompGraph.In && groupInput is { } wired
                    ? new CompStep(CompStepKind.Pass, [wired])
                    : node.Effect.Enabled || type is CompGraph.In or CompGraph.Out
                        ? Step(graph, index, node, type, registered, inputs)
                        : new CompStep(CompStepKind.Pass, inputs.IsEmpty ? [-1] : [inputs[0]]);
                index[node.Id] = steps.Count;
                steps.Add(step with { NodeId = node.Id });
            }

            return index[last.Id];
        }

        CompStep Step(CompGraph graph, Dictionary<string, int> index, CompNode node, string type, EffectDescriptor? registered, ImmutableArray<int> inputs)
        {
            switch (type)
            {
                case CompGraph.In:
                    return new CompStep(CompStepKind.In, []);

                case CompGraph.Group:
                    return new CompStep(CompStepKind.Pass, inputs);

                case CompGraph.Out:
                case CompGraph.Plane:
                    return new CompStep(CompStepKind.Pass, inputs);

                case CompGraph.Media:
                    return Media(node);

                case CompGraph.Transform:
                    return new CompStep(CompStepKind.Transform, inputs) { Transform = Moved(ParameterSet.Evaluate(CompNodes.Transform, node.Effect, local)), Frame = frameSize };

                case CompGraph.Merge:
                {
                    ParameterSet p = ParameterSet.Evaluate(CompNodes.Merge, node.Effect, local);
                    return new CompStep(CompStepKind.Merge, [inputs[0], inputs[1]])
                    {
                        Transform = Moved(p),
                        Frame = frameSize,
                        Opacity = Math.Clamp(p.Float("opacity"), 0.0f, 1.0f),
                        Blend = p.Enum("blend") switch
                        {
                            "add" => BlendMode.Add,
                            "multiply" => BlendMode.Multiply,
                            "screen" => BlendMode.Screen,
                            "overlay" => BlendMode.Overlay,
                            "darken" => BlendMode.Darken,
                            "lighten" => BlendMode.Lighten,
                            "difference" => BlendMode.Difference,
                            "soft-light" => BlendMode.SoftLight,
                            "hard-light" => BlendMode.HardLight,
                            _ => BlendMode.Normal,
                        },
                        Mask = inputs[2],
                    };
                }

                case CompGraph.Matte:
                    return new CompStep(CompStepKind.Matte, inputs)
                    {
                        MatteMode = ParameterSet.Evaluate(CompNodes.Matte, node.Effect, local).Enum("mode") switch
                        {
                            "luma" => 1u,
                            "alpha-inverted" => 2u,
                            "luma-inverted" => 3u,
                            _ => 0u,
                        },
                    };

                case CompGraph.Render3D:
                    return Render3D(graph, index, node, inputs);

                case var _ when CompGraph.IsObject3D(type):
                    return new CompStep(CompStepKind.Empty, []);

                case var _ when registered is { Kind: EffectKind.Generator }:
                {
                    var made = new Clip(node.Id, clip.Range, Flicks.Zero, GeneratorId: type, Effects: [node.Effect]);
                    return Source(project, made, time, CompLanes, frameSize, output, frames, options, depth, frameRate) is { } source
                        ? new CompStep(CompStepKind.Picture, []) { Picture = Placed(source) }
                        : new CompStep(CompStepKind.Empty, []);
                }

                default:
                    return Node(node.Effect, local, clip.Duration, time, options) is { } effect
                        ? new CompStep(CompStepKind.Effect, inputs) { Effect = effect }
                        : new CompStep(CompStepKind.Pass, inputs);
            }
        }

        // Frame pixels to output pixels: moved from the frame centre, scaled and turned about the anchor.
        Matrix3x2 Moved(ParameterSet p) =>
            Placement(frameSize, frameSize, ConformPolicy.Stretch, p.Float2("position"), p.Float2("scale"), p.Float("rotation"), p.Float2("anchor"), scale);

        LayerNode Placed((LayerSource Source, Vector2 Size, ConformPolicy Policy) source) =>
            new(source.Source, (int)source.Size.X, (int)source.Size.Y, Placement(source.Size, frameSize, source.Policy, Vector2.Zero, Vector2.One, 0.0f, Vector2.Zero, scale), LayerNode.NoCrop, 1.0f, BlendMode.Normal, [], scale);

        CompStep Media(CompNode node)
        {
            ParameterSet p = ParameterSet.Evaluate(CompNodes.Media, node.Effect, local);
            if (p.Text("media") is not { Length: > 0 } mediaId || project.MediaItem(mediaId) is not { } media)
            {
                return new CompStep(CompStepKind.Empty, []);
            }

            // The media from its offset at the clip's start, on a decoder lane of its own.
            Flicks offset = Flicks.Max(Flicks.Zero, Flicks.FromSeconds(p.Float("offset")));
            var made = new Clip(node.Id, clip.Range, offset, MediaId: media.Id);
            int lane = CompLanes + 1 + (StableSeed(node.Id) & 0xFFFF);
            if (Source(project, made, time, lane, frameSize, output, frames, options, depth, frameRate) is not { } source)
            {
                return new CompStep(CompStepKind.Empty, []);
            }

            ConformPolicy policy = p.Enum("fit") switch
            {
                "fill" => ConformPolicy.Fill,
                "stretch" => ConformPolicy.Stretch,
                "native" => ConformPolicy.Native,
                _ => ConformPolicy.Fit,
            };
            return new CompStep(CompStepKind.Picture, []) { Picture = Placed(source with { Policy = policy }) };
        }

        CompStep Render3D(CompGraph comp, Dictionary<string, int> index, CompNode node, ImmutableArray<int> inputs)
        {
            var layers = ImmutableArray.CreateBuilder<SceneLayer>();
            var planes = ImmutableArray.CreateBuilder<int>();
            var meshes = ImmutableArray.CreateBuilder<SceneMesh>();
            var lights = ImmutableArray.CreateBuilder<SceneLight>();
            SceneCamera? camera = null;
            var canvas = new RenderGraph(output.Width, output.Height, []);

            foreach (CompInput wire in node.Inputs)
            {
                if (comp.Node(wire.From) is not { Effect.Enabled: true } item || !index.TryGetValue(item.Id, out int step))
                {
                    continue;
                }

                string type = item.Effect.TypeId;
                if (type == CompGraph.Plane)
                {
                    ParameterSet p = ParameterSet.Evaluate(CompNodes.Plane, item.Effect, local);

                    // The plane's picture is a frame at the working size: its texels to frame
                    // pixels from the centre, then set in the world.
                    Matrix4x4 world = Matrix4x4.CreateTranslation(new Vector3(-output.Width / 2.0f, -output.Height / 2.0f, 0.0f))
                        * Matrix4x4.CreateScale(1.0f / scale, 1.0f / scale, 1.0f)
                        * Placed3D(p);
                    layers.Add(new SceneLayer(canvas, world, 1.0f, new SceneMaterial(p.Bool("lights"), p.Bool("casts-shadows"), p.Bool("accepts-shadows"))));
                    planes.Add(step);
                }
                else if (SceneObjects.IsMesh(type) && options.Effects.Find(type) is { } shape)
                {
                    meshes.AddRange(MeshesFrom(type, ParameterSet.Evaluate(shape, item.Effect, local), local, Placed3D(ParameterSet.Evaluate(CompNodes.Placement3D, item.Effect, local)), 1.0f, true, true, true, options));
                }
                else if (type == SceneObjects.Camera && options.Effects.Find(type) is { } lens)
                {
                    camera ??= CameraFrom(ParameterSet.Evaluate(lens, item.Effect, local), frameSize);
                }
                else if (type == SceneObjects.Light && options.Effects.Find(type) is { } lamp)
                {
                    lights.Add(LightFrom(project, item.Id, ParameterSet.Evaluate(lamp, item.Effect, local), frames));
                }
            }

            var scene = new SceneLayerSource(layers.ToImmutable(), camera ?? DefaultCamera(frameSize), lights.ToImmutable()) { Meshes = meshes.ToImmutable() };
            return new CompStep(CompStepKind.Render3D, inputs) { Scene = scene, Planes = planes.ToImmutable() };
        }

        static Matrix4x4 Placed3D(ParameterSet p) =>
            SceneMath.LayerToWorld(Vector2.Zero, p.Float2("scale"), p.Float("rotation-x"), p.Float("rotation-y"), p.Float("rotation"), p.Float2("position"), p.Float("z"));
    }
}
