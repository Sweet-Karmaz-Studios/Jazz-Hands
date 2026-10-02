namespace JazzHands.Core.Commands;

/// <summary>What is in a glTF model, for <c>model3d.add</c> (Phase 48).</summary>
/// <param name="File">The .gltf or .glb file.</param>
[Query("model3d.info", Description = "Say what is in a glTF model: meshes, materials, animations and anything left out")]
public sealed record Model3DInfoQuery(
    [property: Arg(0, "The .gltf or .glb file")] string File) : IQuery<Model3DInfo>;
