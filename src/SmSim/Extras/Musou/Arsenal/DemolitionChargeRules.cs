using System.Numerics;

namespace SmSim.Extras.Musou.Arsenal;

/// <summary>
/// The Remote Detonators' rules (W4, docs/new-weapons-design.md section 7; Musou Mode only, the install's <c>sm_mp_eq_demolition_charge</c> on the melta bomb's model): the numbers of the owner's charge
/// (D19: "600 damage at the centre, 40m reach, bosses take 2x, up to 80 orks thrown flying, survivors knocked back up to 10m, 5s fuse after it sticks"), fitted as a centre blast and a rolling ring of
/// 16 stacking spots (D22, D23), and the pure ring geometry the server (<c>World.Charge.cs</c>) and the client (<c>client/Extras/Musou/Arsenal/Charge/</c>) both place spots with. Every value is ours (an
/// estimate fitted to the owner's numbers) unless marked data; spec/behavior.md "Remote Detonators". Distances in metres, times in seconds.
/// </summary>
public static class DemolitionChargeRules
{
    /// <summary>Data (<c>pp_grenade_remote_detonation</c>): the flight's speed in m/s.</summary>
    public const float Speed = 25f;
    /// <summary>The fuse after the charge sticks, and from the throw for one that never sticks (D19; the fallback ours).</summary>
    public const float FuseSeconds = 5f;
    /// <summary>One charge back every this many seconds in Musou Mode (D9; grenades: 15).</summary>
    public const float RefillSeconds = 30f;

    /// <summary>The centre blast: damage at the centre, and the data's curve (0 to 4 m 1, 6 m 0.75, 10 m 0.3) stretched by <see cref="CentreStretch"/> (ours).</summary>
    public const float CentreDamage = 600f, CentreRadius = 16f, CentreStretch = 1.6f;
    /// <summary>Each ring spot: damage at its centre and the same curve and radius as the centre blast (ours). The spots stack (D22).</summary>
    public const float SpotDamage = 180f;
    /// <summary>The ring: spots on a circle of this radius round the point the charge went off, this many, one every <see cref="SpotTicks"/> ticks, the first <see cref="FirstSpotTicks"/> ticks after the centre blast (ours).</summary>
    public const float RingRadius = 24f;
    public const int Spots = 16, FirstSpotTicks = 3, SpotTicks = 2;
    /// <summary>Boss Orks and the Warboss take this much more from every part (D19).</summary>
    public const float BossFactor = 2f;
    /// <summary>The most kills of one charge that die as flying corpses: 80 in the owner's D19, lowered to 40 by the owner (2026-11-10, "Lower to 40", after W4's measurement); the rest are torn apart where they stand.</summary>
    public const int ThrownMax = 40;
    /// <summary>The farthest a survivor is thrown back, scaled by the share of the blast he took (D19), once per charge.</summary>
    public const float KnockbackMax = 10f;
    /// <summary>Master-Crafted Wargear (the upgrade string <see cref="MasterCrafted"/>): the data's ratio, damage x1.25 and reach x1.5 on every part (D24).</summary>
    public const float MasterDamage = 1.25f, MasterReach = 1.5f;
    public const string MasterCrafted = "equipment_effectiveness", Favor = "equipment_double";

    /// <summary>A spot's effect lasts this long, so the first still burns when the last goes off and the full circle stands for a moment (ours).</summary>
    public const float SpotEffectSeconds = 1.0f;

    /// <summary>The golden-ish angle step of the start bearing from the charge's projectile id (so the server and every client agree without a message; ours).</summary>
    public const float StartStepDegrees = 137.5f;

    static readonly DistancePoint[] DataCurve = [new(0f, 1f), new(4f, 1f), new(6f, 0.75f), new(10f, 0.3f)];

    /// <summary>The blast curve at <paramref name="reach"/> times the centre's stretched distances.</summary>
    public static DistancePoint[] Curve(bool master) =>
        [.. DataCurve.Select(p => new DistancePoint(p.Distance * CentreStretch * (master ? MasterReach : 1f), p.Value))];

    /// <summary>The centre blast (every player, Ork and contraption in reach, in sight; the thrower too, the data's <c>ignore_self</c> false).</summary>
    public static SplashSpec Centre(bool master) => Blast(CentreDamage, master);

    /// <summary>One ring spot.</summary>
    public static SplashSpec Spot(bool master) => Blast(SpotDamage, master);

    static SplashSpec Blast(float damage, bool master) => new((CentreRadius) * (master ? MasterReach : 1f),
        new DamageSpec(damage * (master ? MasterDamage : 1f), 0f, 1f, 1f, 1f, 1f, false) { Type = DamageType.Explosive }, Curve(master), LineOfSight: true, IgnoreSelf: false, TargetFilter: "target_all");

    /// <summary>The ring's radius for a charge (Master-Crafted: x1.5).</summary>
    public static float Ring(bool master) => RingRadius * (master ? MasterReach : 1f);

    /// <summary>Tick of spot <paramref name="k"/> (0-based) relative to the centre blast's tick.</summary>
    public static int SpotTick(int k) => FirstSpotTicks + k * SpotTicks;

    /// <summary>The bearing (radians, counter-clockwise from +X seen from above) of spot <paramref name="k"/> of charge <paramref name="projectile"/>: clockwise from a start fixed by the id.</summary>
    public static float Bearing(int projectile, int k)
    {
        float start = ((uint)projectile * StartStepDegrees % 360f) * (MathF.PI / 180f);
        return start - k * (MathF.Tau / Spots);
    }

    /// <summary>A ray against the level: the distance to the first hit within <c>length</c> (null: none).</summary>
    public delegate float? Ray(Vector3 origin, Vector3 direction, float length);

    /// <summary>
    /// Where spot <paramref name="k"/> sits: the ring point at the charge's height, stopped at a wall's face when the level stands between (ours), then dropped to the ground below it (a ray down, at
    /// most 6 m; with no ground it goes off in the air). With no level (<paramref name="ray"/> null) the point itself.
    /// </summary>
    public static Vector3 SpotAt(Vector3 centre, int projectile, int k, float ring, Ray? ray)
    {
        float a = Bearing(projectile, k);
        var dir = new Vector3(MathF.Cos(a), MathF.Sin(a), 0f);
        var at = centre + dir * ring;
        if (ray is null) return at;
        if (ray(centre, dir, ring) is float wall) at = centre + dir * MathF.Max(0f, wall - 0.3f);
        const float lift = 0.5f, reach = 6f;
        if (ray(at + new Vector3(0f, 0f, lift), -Vector3.UnitZ, lift + reach) is float down) at.Z = at.Z + lift - down + 0.3f;
        return at;
    }
}
