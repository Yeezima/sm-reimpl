namespace SmSim.Extras.Musou;

/// <summary>
/// The Orks' pace and charge in Musou Mode (the owner, 2026-11-09: "make orks run 20% faster, and sprint to 'charge' the enemy when they reach within 30m of an enemy"). Every value ours unless said
/// (spec "Musou Mode: the Orks' pace and charge"); a war's pool only (the 5-wave run, without the crowd rules, as pinned); the server's <c>SM_MUSOU_OLD_ORK_PACE=1</c> restores the rules before
/// (<see cref="MusouSim.OldOrkPace"/>).
/// <list type="bullet">
/// <item>Pace: every Ork on foot (Boyz, Shoota and Rokkit Boyz, 'Ard Boyz, Gretchin, Nobz, the Big Shoota Nob, the Stormboyz on the ground, the Weirdboy, the Scrap Mek, the four Boss Orks and the
/// Warboss) moves <see cref="PaceScale"/> times his speed. Not the Bomb Squig (a beast, not an Ork), the Chaos Marine, a Stormboy's hop (its own flight time) or a knockback.</item>
/// <item>Charge: a melee-first Ork (<see cref="Charges"/>: a Boy, 'Ard Boy, Gretchin, Nob, Stormboy, Boss Boy, Boss 'Ard Boy or Boss Nob) with an enemy (a player, a retainer or AI Marine, a
/// Guard squad; not a place to retake) within <see cref="ChargeWithin"/> m sprints at it until his blow: his paced speed times the original's sprint over its run (the boy rig's
/// <c>BOY_1HM_NAV_Sprint_F_01</c> covers 9.91 m/s on its planted feet, its <c>BOY_1HM_NAV_Run_F_*</c> 5.98, both measured: <see cref="SprintOverRun"/>), at most the sprint clip's own 9.9 m/s
/// (<see cref="SprintMost"/>). The data has no Ork sprint speed of its own: the ratio is the clips', the cap the clip's (supported by evidence for the clip, ours as a speed). The gun kinds never charge:
/// they hold their range and shoot (a Big Shoota Nob, a Boss Shoota Boy, a Shoota or Rokkit Boy, the Weirdboy, the Scrap Mek); the Warboss keeps his own charge ability and does not sprint.</item>
/// </list>
/// </summary>
public static class MusouChargeRules
{
    /// <summary>Every Ork on foot's speed over before (x; the owner's "20% faster").</summary>
    public const float PaceScale = 1.2f;
    /// <summary>The flat distance to an enemy (m, from its edge) within which a melee Ork charges (the owner's 30 m).</summary>
    public const float ChargeWithin = 30f;
    /// <summary>The original's sprint clip's planted-foot speed over its run clip's (9.91 / 5.98 m/s, measured on the boy rig).</summary>
    public const float SprintOverRun = 9.91f / 5.98f;
    /// <summary>The fastest a charge runs (m/s): the sprint clip's own planted-foot speed.</summary>
    public const float SprintMost = 9.9f;

    /// <summary>Whether a kind moves at the pace (every Ork on foot).</summary>
    public static bool Paced(MusouEnemyType type) => type is not (MusouEnemyType.BombSquig or MusouEnemyType.ChaosMarine);

    /// <summary>Whether a kind charges (the melee-first Orks; not the gun kinds or the Warboss).</summary>
    public static bool Charges(MusouEnemyType type) => type is MusouEnemyType.Boy or MusouEnemyType.ArdBoy or MusouEnemyType.Gretchin or MusouEnemyType.Nob or MusouEnemyType.Stormboy
        or MusouEnemyType.BossBoy or MusouEnemyType.BossArdBoy or MusouEnemyType.BossNob;

    /// <summary>A charging Ork's speed over his paced run (x) for a kind whose speed is <paramref name="speed"/> m/s before the pace: the sprint ratio, capped at <see cref="SprintMost"/>.</summary>
    public static float SprintScale(float speed)
    {
        float run = speed * PaceScale;
        return run <= 1e-3f ? 1f : MathF.Min(run * SprintOverRun, MathF.Max(SprintMost, run)) / run;
    }

    /// <summary>A charging Ork's speed (m/s) for a kind whose speed is <paramref name="speed"/> m/s before the pace.</summary>
    public static float SprintSpeed(float speed) => speed * PaceScale * SprintScale(speed);
}

public sealed partial class MusouSim
{
    /// <summary>Test aid (server <c>SM_MUSOU_OLD_ORK_PACE=1</c>): the Orks' speed before 2026-11-09's pace and charge.</summary>
    public static readonly bool DefaultOldOrkPace = Environment.GetEnvironmentVariable("SM_MUSOU_OLD_ORK_PACE") == "1";
    /// <summary>This pool's switch (tests set it per pool; the server's from <see cref="DefaultOldOrkPace"/>).</summary>
    public bool OldOrkPace { get; set; } = DefaultOldOrkPace;

    bool PaceRules => !OldOrkPace && Swarm is not null;
    readonly bool[] _charging = new bool[Capacity];

    /// <summary>Whether this enemy charges this tick (set by <see cref="Behave"/>).</summary>
    public bool Charging(ushort id) => id >= 1 && id <= Capacity && _used[id - 1] && _charging[id - 1];

    /// <summary>This enemy's pace over his speed: <see cref="MusouChargeRules.PaceScale"/> for an Ork on foot, times the sprint while he charges.</summary>
    float PaceOf(int i)
    {
        if (!PaceRules || !MusouChargeRules.Paced(_type[i])) return 1f;
        return MusouChargeRules.PaceScale * (_charging[i] ? MusouChargeRules.SprintScale(Rules.Of(_type[i]).Speed) : 1f);
    }

    /// <summary>Decides the charge for a seeking melee Ork <paramref name="reach"/> m from his target's edge.</summary>
    void DecideCharge(int i, in MusouTarget target, float reach) =>
        _charging[i] = PaceRules && MusouChargeRules.Charges(_type[i]) && !target.Goal && target.Alive && _state[i] == MusouEnemyState.Seek && reach <= MusouChargeRules.ChargeWithin;
}
