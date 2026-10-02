namespace JazzHands.Core.Commands;

/// <summary>One mesh of a glTF model.</summary>
/// <param name="Index">Its number in the file.</param>
/// <param name="Triangles">How many triangles it has.</param>
/// <param name="Parts">How many parts (glTF primitives), each with its own material.</param>
public sealed record Model3DMeshInfo(int Index, int Triangles, int Parts);

/// <summary>One animation of a glTF model.</summary>
/// <param name="Index">Its number, for <c>--animation</c>.</param>
/// <param name="Name">Its name, or empty.</param>
/// <param name="Seconds">How long it lasts.</param>
public sealed record Model3DAnimationInfo(int Index, string Name, double Seconds);

/// <summary>What is in a glTF model (Phase 48).</summary>
/// <param name="File">The file, in full.</param>
/// <param name="Meshes">Its meshes.</param>
/// <param name="Materials">How many distinct materials its meshes draw with.</param>
/// <param name="Textures">How many pictures its materials read.</param>
/// <param name="Animations">Its animations.</param>
/// <param name="Size">Its box at rest, width, height and depth, in the file's units.</param>
/// <param name="Problems">What this reader leaves out, in words; empty when nothing is.</param>
public sealed record Model3DInfo(string File, Model3DMeshInfo[] Meshes, int Materials, int Textures, Model3DAnimationInfo[] Animations, double[] Size, string[] Problems);
