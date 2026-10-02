using System.Numerics;

namespace JazzHands.Render.Scene;

/// <summary>
/// The matrices of the 3D scenes (Phase 47), in the conventions the rest of the compositor uses:
/// sequence pixels, the origin at the frame centre, x right, y down, z away from the viewer, row
/// vectors (a point times a matrix), and degrees clockwise on screen for a turn about Z.
/// </summary>
public static class SceneMath
{
    /// <summary>
    /// A 3D layer's place: from its picture's own space (sequence pixels from the fitted picture's
    /// centre, unscaled) to the world.
    /// </summary>
    /// <remarks>
    /// The pivot goes to the origin, the picture is scaled about it and turned about X, then Y,
    /// then Z, the pivot goes back, and the whole moves to the position and depth. With depth 0 and
    /// no turn about X or Y this is the flat placement exactly, so a layer made 3D does not move.
    /// </remarks>
    /// <param name="pivot">The anchor, in the fitted picture's sequence pixels from its centre.</param>
    /// <param name="scale">Scale per axis.</param>
    /// <param name="rotationX">Degrees; positive tips the top edge away.</param>
    /// <param name="rotationY">Degrees; positive turns the right edge away.</param>
    /// <param name="rotationZ">Degrees clockwise on screen.</param>
    /// <param name="position">Across the frame from its centre, in sequence pixels.</param>
    /// <param name="depth">Away from the viewer, in sequence pixels.</param>
    public static Matrix4x4 LayerToWorld(Vector2 pivot, Vector2 scale, float rotationX, float rotationY, float rotationZ, Vector2 position, float depth)
    {
        var anchor = new Vector3(pivot, 0.0f);
        return Matrix4x4.CreateTranslation(-anchor)
            * Matrix4x4.CreateScale(scale.X, scale.Y, 1.0f)
            * Rotation(rotationX, rotationY, rotationZ)
            * Matrix4x4.CreateTranslation(anchor + new Vector3(position, depth));
    }

    /// <summary>The turn about X, then Y, then Z, in this space's senses.</summary>
    public static Matrix4x4 Rotation(float rotationX, float rotationY, float rotationZ) =>
        Matrix4x4.CreateRotationX(-Radians(rotationX))
        * Matrix4x4.CreateRotationY(-Radians(rotationY))
        * Matrix4x4.CreateRotationZ(Radians(rotationZ));

    /// <summary>World to a viewer's space, from its position and its three unit axes.</summary>
    public static Matrix4x4 View(Vector3 position, Vector3 right, Vector3 down, Vector3 forward) =>
        new(
            right.X, down.X, forward.X, 0.0f,
            right.Y, down.Y, forward.Y, 0.0f,
            right.Z, down.Z, forward.Z, 0.0f,
            -Vector3.Dot(position, right), -Vector3.Dot(position, down), -Vector3.Dot(position, forward), 1.0f);

    /// <summary>
    /// A perspective projection with reversed infinite depth: depth is <paramref name="near"/>
    /// over the distance, 1 at the near plane falling towards 0 far away, which keeps its
    /// precision at every distance. Clip y is up, so screen y comes out down.
    /// </summary>
    /// <param name="scaleX">Clip units per unit of x over depth: the focal length over half the width.</param>
    /// <param name="scaleY">The same for y, over half the height.</param>
    /// <param name="near">The nearest distance drawn.</param>
    public static Matrix4x4 Perspective(float scaleX, float scaleY, float near) =>
        new(
            scaleX, 0.0f, 0.0f, 0.0f,
            0.0f, -scaleY, 0.0f, 0.0f,
            0.0f, 0.0f, 0.0f, 1.0f,
            0.0f, 0.0f, near, 0.0f);

    /// <summary>
    /// An orthographic projection of a box in a viewer's space: x and y to clip space, depth from
    /// 1 at <paramref name="nearZ"/> to 0 at <paramref name="farZ"/> (reversed, as the perspective).
    /// </summary>
    public static Matrix4x4 Orthographic(Vector2 centre, Vector2 halfSize, float nearZ, float farZ)
    {
        float range = Math.Max(farZ - nearZ, 1e-3f);
        return new Matrix4x4(
            1.0f / halfSize.X, 0.0f, 0.0f, 0.0f,
            0.0f, -1.0f / halfSize.Y, 0.0f, 0.0f,
            0.0f, 0.0f, -1.0f / range, 0.0f,
            -centre.X / halfSize.X, centre.Y / halfSize.Y, 1.0f + (nearZ / range), 1.0f);
    }

    /// <summary>
    /// Unit axes looking from <paramref name="from"/> towards <paramref name="to"/>, with screen
    /// down as near to the world's down (+y) as the direction allows, and a roll clockwise.
    /// </summary>
    public static (Vector3 Right, Vector3 Down, Vector3 Forward) LookAt(Vector3 from, Vector3 to, float rollDegrees = 0.0f)
    {
        Vector3 forward = to - from;
        forward = forward.LengthSquared() > 1e-8f ? Vector3.Normalize(forward) : Vector3.UnitZ;

        // The world's up is -y. Looking straight up or down, take +z as up instead.
        Vector3 up = MathF.Abs(Vector3.Dot(forward, -Vector3.UnitY)) > 0.9999f ? Vector3.UnitZ : -Vector3.UnitY;
        Vector3 right = Vector3.Normalize(Vector3.Cross(forward, up));
        Vector3 down = Vector3.Cross(forward, right);

        if (rollDegrees != 0.0f)
        {
            float roll = Radians(rollDegrees);
            (float sin, float cos) = MathF.SinCos(roll);
            Vector3 rolledRight = (right * cos) + (down * sin);
            Vector3 rolledDown = (down * cos) - (right * sin);
            right = rolledRight;
            down = rolledDown;
        }

        return (right, down, forward);
    }

    /// <summary>The focal length, in sequence pixels, of an angle of view across a frame's width.</summary>
    public static float ZoomFor(float angleDegrees, float frameWidth) =>
        frameWidth / 2.0f / MathF.Tan(Radians(Math.Clamp(angleDegrees, 1.0f, 170.0f)) / 2.0f);

    /// <summary>
    /// A camera from its parameters: at rest it looks at the frame centre from in front, at the
    /// distance where the rest angle of view fits the frame's width, so a layer at depth 0 is seen
    /// at its own size.
    /// </summary>
    /// <param name="frameSize">The sequence frame.</param>
    /// <param name="position">Across the frame from its centre.</param>
    /// <param name="dolly">Moved in from rest, with the point of interest; negative pulls back.</param>
    /// <param name="target">The point of interest.</param>
    /// <param name="orbit">Degrees round the point of interest, left and right; positive swings to the right.</param>
    /// <param name="tilt">Degrees round it, up (positive) and down.</param>
    /// <param name="roll">Degrees about the way it looks, clockwise.</param>
    /// <param name="angle">The angle of view across the frame.</param>
    /// <param name="restAngle">The angle of view that sets the rest distance.</param>
    public static (Vector3 Position, Vector3 Right, Vector3 Down, Vector3 Forward, float Zoom) Camera(
        Vector2 frameSize,
        Vector2 position,
        float dolly,
        Vector3 target,
        float orbit,
        float tilt,
        float roll,
        float angle,
        float restAngle)
    {
        // A dolly carries the point of interest in with the camera, so the camera never passes
        // what it looks at and turns round.
        float rest = ZoomFor(restAngle, frameSize.X);
        var eye = new Vector3(position, -rest + dolly);
        target += new Vector3(0.0f, 0.0f, dolly);

        // Swing round the point of interest: tilt first, about the horizontal, then orbit about
        // the vertical, so a tilt stays a tilt whichever way the orbit has turned.
        if (orbit != 0.0f || tilt != 0.0f)
        {
            Vector3 offset = eye - target;
            offset = Vector3.Transform(offset, Matrix4x4.CreateRotationX(-Radians(tilt)) * Matrix4x4.CreateRotationY(-Radians(orbit)));
            eye = target + offset;
        }

        (Vector3 right, Vector3 down, Vector3 forward) = LookAt(eye, target, roll);
        return (eye, right, down, forward, ZoomFor(angle, frameSize.X));
    }

    /// <summary>Degrees to radians.</summary>
    public static float Radians(float degrees) => degrees * MathF.PI / 180.0f;
}
