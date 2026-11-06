using System.Globalization;
using System.Numerics;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Extras.Musou.Campaign;

/// <summary>
/// The engine-free rules of the battle's markers and capture tracker (Ashfall concept pass, the owner, 2026-11-06): when a position's letter shows (<see cref="LetterAlpha"/>), which position the own player stands on
/// (<see cref="OnPosition"/>), and what the tracker's bars and line say (<see cref="BarFill"/>, <see cref="OnPointLine"/>). The client's <c>TfwMarkers</c> and <c>TfwHud</c> draw them. Every value is ours.
/// </summary>
public static class TfwMarkerRules
{
    /// <summary>The letter's height over its point (m; the owner's "much higher": was 9).</summary>
    public const float LabelHeight = 24f;
    /// <summary>The ground ring's width (m; "thin").</summary>
    public const float RingWidth = 0.22f;
    /// <summary>The letter shows while the view's heading is within this many degrees of its point, and is gone by <see cref="LookFadeDegrees"/>.</summary>
    public const float LookDegrees = 25f, LookFadeDegrees = 38f;
    /// <summary>The letter fades out from this many metres outside the ring to the ring's edge: on the point it never covers the fight.</summary>
    public const float NearMargin = 8f;

    /// <summary>
    /// The letter's opacity (0..1) for a point at <paramref name="point"/> (game space, its ground) of radius <paramref name="radius"/>, seen from a camera at <paramref name="eye"/> heading <paramref name="forward"/>, with the
    /// player's feet at <paramref name="viewer"/>: full while the heading on the ground plane is within <see cref="LookDegrees"/> of the point, none from <see cref="LookFadeDegrees"/> (smoothstep between); none on the point,
    /// rising over <see cref="NearMargin"/> m outside its ring.
    /// </summary>
    public static float LetterAlpha(Vector3 point, float radius, Vector3 eye, Vector3 forward, Vector3 viewer)
    {
        float dx = point.X - viewer.X, dy = point.Y - viewer.Y;
        float off = MathF.Sqrt(dx * dx + dy * dy) - radius;
        float near = Math.Clamp(off / NearMargin, 0f, 1f);
        if (near <= 0f) return 0f;
        var to = new Vector2(point.X - eye.X, point.Y - eye.Y);
        var head = new Vector2(forward.X, forward.Y);
        if (to.LengthSquared() < 1e-6f || head.LengthSquared() < 1e-6f) return 0f;
        float cos = Vector2.Dot(Vector2.Normalize(to), Vector2.Normalize(head));
        float deg = MathF.Acos(Math.Clamp(cos, -1f, 1f)) * 180f / MathF.PI;
        float look = 1f - Math.Clamp((deg - LookDegrees) / (LookFadeDegrees - LookDegrees), 0f, 1f);
        look = look * look * (3f - 2f * look);
        return near * look;
    }

    /// <summary>The position the player at <paramref name="feet"/> stands on (inside its radius, <see cref="TfwHudModel.ClearingReach"/> up and down), or -1.</summary>
    public static int OnPosition(IReadOnlyList<TfwObjective> objectives, Vector3 feet)
    {
        for (int k = 0; k < objectives.Count; k++)
        {
            var o = objectives[k];
            float dx = feet.X - o.Position.X, dy = feet.Y - o.Position.Y;
            if (dx * dx + dy * dy <= o.Radius * o.Radius && MathF.Abs(feet.Z - o.Position.Z) <= TfwHudModel.ClearingReach) return k;
        }
        return -1;
    }

    /// <summary>How full a position's bar is (0..1): its progress while securing or clearing, full when secured or held, empty while locked.</summary>
    public static float BarFill(in TfwObjective o) => o.State switch
    {
        TfwPositionState.Securing or TfwPositionState.Clearing => Math.Clamp(o.Progress, 0f, 1f),
        TfwPositionState.Secured or TfwPositionState.Held => 1f,
        _ => 0f,
    };

    /// <summary>The tile's name: A, B, C, then HQ for the commander's position.</summary>
    public static string TileName(int k) => k < WarRules.Positions ? ((char)('A' + k)).ToString() : "HQ";

    /// <summary>The line over the wide bar while standing on position <paramref name="k"/>; <paramref name="orks"/>: the Orks the client counts on it.</summary>
    public static string OnPointLine(in TfwObjective o, int k, int orks) => o.State switch
    {
        TfwPositionState.Securing => string.Create(CultureInfo.InvariantCulture, $"SECURING {TileName(k)}  {MathF.Floor(Math.Clamp(o.Progress, 0f, 1f) * 100f):0}%"),
        TfwPositionState.Secured => $"{TileName(k)} SECURED",
        TfwPositionState.Clearing => orks > 0 ? string.Create(CultureInfo.InvariantCulture, $"CLEARING {TileName(k)}  {orks} ORKS LEFT") : $"CLEARING {TileName(k)}",
        TfwPositionState.Held => $"{TileName(k)} HELD BY THE ORKS",
        _ => $"{TileName(k)} LOCKED",
    };
}
