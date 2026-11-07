using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using SmContent;
using SmSim;

namespace SmClient.Bullets;

/// <summary>
/// fx/tracers-impact (owner, 2026-11-07; engine-free, tested; <c>NetworkView.Bullets.cs</c> and <c>NetworkView.Fx.cs</c> call these): where a shot's tracer is drawn and
/// when its impact shows.
/// <para>
/// The owner: "the tracers should go in a straight line directly to where the decal of the projectile is painted"; "currently bolt weapon tracers are drawn
/// independently of the actual projectile path. line them up accurately." Since feel/bullets the tracer left the muzzle, bent onto the damaging ray (the
/// camera's line through the crosshair) 3 m out and flew along it: a line that read as following the crosshair, not as one bullet from the barrel. Now it is one
/// straight line from the muzzle to the shot's end point, which is the point the impact and its decal are placed at (the impact sits 2 cm off it along the
/// surface normal), from the same shot result: the own shot's ray from the input that was sent (scatter included, by the server's rules), another player's
/// from the server's shot record. <c>--bent-bullets</c> restores the bend.
/// </para>
/// </summary>
public static class TracerRules
{
    /// <summary>How far the impact effect sits off the shot's end point along the surface normal, metres (<c>FxImpact</c>: <c>end + normal * 0.02</c>).</summary>
    public const float ImpactLift = 0.02f;

    /// <summary>
    /// The drawn tracer as a polyline: straight from <paramref name="muzzle"/> to <paramref name="end"/> (the shot's end point, where the impact is painted), or,
    /// with <paramref name="bent"/> (<c>--bent-bullets</c>, the feel/bullets look), through the join point on the damaging ray when there is one.
    /// </summary>
    public static (Vector3 From, Vector3? Via, Vector3 To) Path(Vector3 muzzle, Vector3 end, Vector3? rayOrigin, Vector3? rayDir, bool bent)
    {
        if (bent && rayOrigin is { } o && rayDir is { } d && BulletPaths.JoinPoint(muzzle, o, d, end) is { } j) return (muzzle, j, end);
        return (muzzle, null, end);
    }

    /// <summary>
    /// The distance from the tracer's last point to the shot's end point (<paramref name="end"/>: the point the decal is projected onto; the impact's sprites sit
    /// <see cref="ImpactLift"/> off it along the normal), metres.
    /// </summary>
    public static float EndGap((Vector3 From, Vector3? Via, Vector3 To) path, Vector3 end) => Vector3.Distance(path.To, end);

    /// <summary>The largest distance of the drawn path from the straight barrel-to-impact line, sampled every <paramref name="step"/> metres along the path.</summary>
    public static float MaxOffLine((Vector3 From, Vector3? Via, Vector3 To) path, Vector3 muzzle, Vector3 end, float step = 0.25f)
    {
        float worst = 0f;
        void Leg(Vector3 a, Vector3 b)
        {
            float length = Vector3.Distance(a, b);
            int n = Math.Max(1, (int)MathF.Ceiling(length / MathF.Max(0.01f, step)));
            for (int i = 0; i <= n; i++) worst = MathF.Max(worst, BulletPaths.DistanceToSegment(Vector3.Lerp(a, b, i / (float)n), muzzle, end));
        }
        if (path.Via is { } via) { Leg(path.From, via); Leg(via, path.To); }
        else Leg(path.From, path.To);
        return worst;
    }

    /// <summary>
    /// Whether a shot's tracer and impact are drawn when it is fired: not for a shot that launches a projectile (a charged plasma shot, the plasma cannon, the
    /// launcher). Its impact and decal come with the server's detonation when the projectile gets there (<c>FxDetonation</c>), and its body is the projectile
    /// itself. Before fx/tracers-impact the own charged shot was traced like a hit-scan shot and its impact (the light shot's smaller decal) was painted at once,
    /// up to a second before the ball arrived; other players' projectile shots were never drawn that way (the server sends no shot record for them).
    /// </summary>
    public static bool DrawnAtFire(WeaponSpec spec, int stage) => Shots.ProjectileOf(spec, stage) is null;
}

/// <summary>
/// fx/tracers-impact (owner, 2026-11-07): the uncharged shot of the plasma gun and plasma pistol is a blue bolt, "smaller than the charged shot, longer and skinnier",
/// instead of a bolter-like streak. The effect is the weapon's own data: the tracer its wargear binds (the gun's <c>plasma_gun_tracer</c> on its basic shot; the pistol's
/// basic shot is read from its secondary ability, which binds the same <c>plasma_gun_tracer</c>; a basic shot naming none falls back to the wargear's first delivery tracer: interpretation; the Chaos files'
/// <c>chaos_plasma_gun_tracer</c>). Its streak is the <c>plasma_tracer</c> texture with pale yellow keys (255, 246, 139) inside blue soft smoke; the bolt is
/// that effect recoloured to the side's plasma colour and drawn skinnier with a short streak (sizes and colour **ours**, refine register). <c>--old-plasma-bolt</c>
/// restores the data's colours and sizes (and the pistol's fallback streak).
/// </summary>
public static class PlasmaBolt
{
    /// <summary>The share of the data's streak width the bolt keeps (the gun's 0.3 m core: 0.09 m; ours).</summary>
    public const float WidthScale = 0.3f;
    /// <summary>The share of the data's streak length (metres per U: the gun's 60) the bolt keeps (1.8 m; ours). The charged shot's ball is 0.7 m across (client sphere, radius 0.35 m), so the bolt is a far thinner, longer streak.</summary>
    public const float TrailScale = 0.03f;
    /// <summary>The share of the data's soft smoke width the bolt keeps (the gun's 0.32 to 0.61 m smoke: 0.1 to 0.18 m; ours).</summary>
    public const float SmokeWidth = 0.3f;

    /// <summary>
    /// fx/plasma-tracers-safe-mode (owner, 2026-11-07: no plasma tracer drawn in safe mode): the Compatibility renderer also draws the bolt as a pooled streak (a plain unshaded mesh, the path of
    /// the bolter's muzzle leg) beside the sprite-shader effect. Its tail is the bolt's 1.8 m (<see cref="TrailScale"/> of the data's 60 m per U) and its width the share of the pooled
    /// streak's 0.16 m glow that matches the bolt's 0.09 m (ours).
    /// </summary>
    public static bool UsesPooledStreak(bool compatibility) => compatibility;
    public const float SafeTail = 60f * TrailScale;
    public const float SafeWidthScale = 0.09f / 0.16f;

    /// <summary>The loyalist bolt's blue (0..1 RGB; ours, between the data's tracer smoke keys (113, 255, 255) / (79, 149, 255) and a hot core).</summary>
    public static readonly (float R, float G, float B) LoyalistBlue = (0.45f, 0.72f, 1f);

    /// <summary>Whether the wargear's basic shot is a plasma bolt: the plasma gun and the plasma pistol, either side (not the plasma cannon, whose shots are projectiles).</summary>
    public static bool IsBoltWeapon(string wargear) =>
        wargear.Contains("plasma_gun", StringComparison.OrdinalIgnoreCase) || wargear.Contains("plasma_pistol", StringComparison.OrdinalIgnoreCase);

    /// <summary>The bolt's colour for a wargear: the Chaos purple (<see cref="PlasmaColour.ChaosPurple"/>) or the loyalist blue.</summary>
    public static (float R, float G, float B) Colour(string wargear) => PlasmaColour.IsChaosPlasma(wargear) ? PlasmaColour.ChaosPurple : LoyalistBlue;

    /// <summary>
    /// The tracer the bolt plays: the basic shot's own when the data names one, else the first default delivery hit effect with a target that the wargear binds
    /// anywhere (the plasma pistol's), else null.
    /// </summary>
    public static FxPlay? Tracer(WeaponEffects w)
    {
        if (w.Basic.Tracer is { } own) return own;
        return w.Bindings.Where(b => b.IsDefault && b.InDelivery && (b.Event == "hit_fx" || b.Event == "initial_tracer_fx")).Select(b => b.Play).FirstOrDefault(p => p is { HasTarget: true });
    }
}
