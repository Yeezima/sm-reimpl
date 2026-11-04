using Godot;
using Num = System.Numerics;

namespace SmMapmakerEditor;

/// <summary>
/// The two frames of the Mapmaker. Documents (.smmap) use the SM1 source frame (Z up, metres; rotation = degrees about X, then Y, then Z, fixed axes;
/// <c>MapScene.Transform</c>). Godot's editor is Y up. A document transform T becomes the node transform C·T·C^-1 (C = the client's GameSpace basis),
/// and a node's content, authored in game coordinates, hangs under a child whose basis is C. Conjugation is a homomorphism, so nested objects compose in
/// either frame, and scale (sx, sy, sz) of the game frame is (sx, sz, sy) on the Godot node.
/// </summary>
public static class Frame
{
    /// <summary>The client's GameSpace basis: game x to Godot x, game y to -z, game z to y.</summary>
    public static readonly Basis C = new(new Vector3(1, 0, 0), new Vector3(0, 0, -1), new Vector3(0, 1, 0));
    static readonly Basis Ci = C.Inverse();

    public static Vector3 ToGodot(Vector3 game) => new(game.X, game.Z, -game.Y);
    public static Vector3 ToGame(Vector3 godot) => new(godot.X, -godot.Z, godot.Y);

    /// <summary>Rotation matrix of the document's Euler angles (degrees): x applied first, then y, then z.</summary>
    public static Basis GameRotation(Vector3 degrees) =>
        Basis.FromEuler(new Vector3(Mathf.DegToRad(degrees.X), Mathf.DegToRad(degrees.Y), Mathf.DegToRad(degrees.Z)), EulerOrder.Zyx);

    /// <summary>A document transform (components) as the transform of its Godot node.</summary>
    public static Transform3D NodeTransform(Vector3 position, Vector3 rotationDegrees, Vector3 scale)
    {
        var game = GameRotation(rotationDegrees).Scaled(scale);
        return new Transform3D(C * game * Ci, ToGodot(position));
    }

    /// <summary>The components of a Godot node transform, in the document's frame.</summary>
    public static (Vector3 Position, Vector3 Rotation, Vector3 Scale) Components(Transform3D node)
    {
        var game = Ci * node.Basis * C;
        var scale = new Vector3(game.Column0.Length(), game.Column1.Length(), game.Column2.Length());
        if (game.Determinant() < 0f) scale.X = -scale.X;
        var rotation = new Basis(
            scale.X == 0f ? Vector3.Right : game.Column0 / scale.X,
            scale.Y == 0f ? Vector3.Up : game.Column1 / scale.Y,
            scale.Z == 0f ? Vector3.Back : game.Column2 / scale.Z);
        var euler = rotation.GetEuler(EulerOrder.Zyx);
        return (ToGame(node.Origin), new Vector3(Mathf.RadToDeg(euler.X), Mathf.RadToDeg(euler.Y), Mathf.RadToDeg(euler.Z)), scale);
    }

    /// <summary>A System.Numerics (row-vector) matrix of the game frame as a Godot transform of the same frame (not converted to Y up).</summary>
    public static Transform3D FromNumerics(Num.Matrix4x4 m) => new(
        new Basis(new Vector3(m.M11, m.M12, m.M13), new Vector3(m.M21, m.M22, m.M23), new Vector3(m.M31, m.M32, m.M33)),
        new Vector3(m.M41, m.M42, m.M43));

    /// <summary>A game-frame transform as the Godot-frame transform of the same placement.</summary>
    public static Transform3D GameToNode(Transform3D game) => new(C * game.Basis * Ci, ToGodot(game.Origin));

    public static Vector3 Round(Vector3 v, int digits = 4) =>
        new((float)System.Math.Round(v.X, digits), (float)System.Math.Round(v.Y, digits), (float)System.Math.Round(v.Z, digits));
}
