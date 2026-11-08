using System.Numerics;

namespace SmSim.Extras.Musou;

/// <summary>
/// The Bomb Squig (expansion S2, docs/musou-expansion.md "Bomb Squig"; Musou Mode battles only). Every value **ours / estimate** (owner brief 2026-11-07) except the blast, which is the original
/// frag grenade's (<c>sm_mp_eq_frag_grenade</c>: 200, radius 10, the data's stepped fall-off; spec "Frag grenade"), not Musou's doubled player frag. Times in seconds, distances in metres.
/// A change raises <see cref="MusouSim.Version"/>.
/// </summary>
public static class MusouSquigRules
{
    /// <summary>Each human player gets a Squig dropped near him every <see cref="DropMinSeconds"/> to <see cref="DropMaxSeconds"/> (a roll per drop), during a battle only.</summary>
    public const float DropMinSeconds = 60f, DropMaxSeconds = 120f;
    /// <summary>It lands this far from him (flat), behind him (off-screen) when the graph allows.</summary>
    public const float DropMin = 12f, DropMax = 20f;
    /// <summary>A spot off-screen: more than this from the player's view heading (degrees; the view is about 70 degrees wide, ours).</summary>
    public const float OffScreenDegrees = 60f;
    /// <summary>It falls this long with a whistle before it runs (state <see cref="MusouEnemyState.Spawning"/>; the client draws the fall).</summary>
    public const float FallSeconds = 1.2f;
    /// <summary>The client draws the fall from this high (m, ours), as a body falling under gravity onto its spot at the end of <see cref="FallSeconds"/>.</summary>
    public const float FallHeight = 14f;
    /// <summary>How high above its spot a dropped Squig is drawn <paramref name="seconds"/> into its fall (0 once landed).</summary>
    public static float FallHeightAt(float seconds) => seconds >= FallSeconds || !(seconds >= 0f) ? 0f : FallHeight * (1f - seconds * seconds / (FallSeconds * FallSeconds));
    /// <summary>Most Squigs alive at once on the whole map (ours: a cap so a long lull with many players cannot pile them up).</summary>
    public const int MaxAlive = 8;
    /// <summary>It lights its fuse when a player, AI Marine or Guard squad comes within this (flat, a squad at its men) and <see cref="FuseVertical"/> up or down ...</summary>
    public const float FuseRadius = 10f, FuseVertical = 3f;
    /// <summary>... and bursts this long after (the client beeps and glows faster as it burns).</summary>
    public const float FuseSeconds = 1.8f;
    /// <summary>A kick (a player running into it): it is flung <see cref="KickDistance"/> along the run at <see cref="KickSpeed"/>; an unlit fuse is set to <see cref="KickFuseSeconds"/>.</summary>
    public const float KickDistance = 12f, KickSpeed = 20f, KickFuseSeconds = 1.0f;
    /// <summary>A kick needs the player this near (flat, feet to feet), within <see cref="KickVertical"/>, moving at least <see cref="KickMinSpeed"/> (m/s) and towards it (the cosine of the angle at least <see cref="KickMinCos"/>).</summary>
    public const float KickReach = 1.6f, KickVertical = 1.5f, KickMinSpeed = 3.5f, KickMinCos = 0.5f;
    /// <summary>A kicker is credited with what its blast kills within this long of the kick.</summary>
    public const float KickCreditSeconds = 10f;
    /// <summary>A Squig that has not lit its fuse this long after it landed lights it wherever it is (ours: none runs about for ever).</summary>
    public const float LifeSeconds = 45f;
    /// <summary>The burst's centre above its feet.</summary>
    public const float BlastHeight = 0.4f;

    /// <summary>The original frag's fall-off (spec "Frag grenade": 0 to 2.5 m 1, 2.6 to 5 m 0.5, 5.1 to 7 m 0.3, 10 m 0.05).</summary>
    public static readonly IReadOnlyList<DistancePoint> FragFallOff =
        [new(0f, 1f), new(2.5f, 1f), new(2.6f, 0.5f), new(5f, 0.5f), new(5.1f, 0.3f), new(7f, 0.3f), new(10f, 0.05f)];

    /// <summary>The burst: the original frag (200 at the centre, radius 10, its fall-off), through the Ork blast: it hurts everyone and sets off other Squigs.</summary>
    public static readonly OrkBlastSpec Blast = new(10f, 200f) { FallOff = FragFallOff };

    /// <summary>The Squig's cues (<see cref="MusouEventKind.Telegraph"/>'s value, above the Weirdboy's): a drop (the whistle; where it lands in the vector), the fuse lit (its ticks in the
    /// event's player), a kick (the kicker in the player, where it would land in the vector), the burst (where, in the vector; the credited player in the player).</summary>
    public const byte DropCue = 24, FuseCue = 25, KickCue = 26, BlastCue = 27;
}

/// <summary>
/// The Rokkit Boy (expansion S2, docs/musou-expansion.md "Rokkit Boy"; Musou Mode battles only). Every value **ours / estimate** (owner brief 2026-11-07). The rocket is a real projectile
/// (<c>MusouSim.Rokkit.cs</c>): it flies straight and bursts on the first Ork, solid or player in its path. A change raises <see cref="MusouSim.Version"/>.
/// </summary>
public static class MusouRokkitRules
{
    /// <summary>The rocket's speed (m/s: slow and visible) and its longest flight (s; then it bursts where it is).</summary>
    public const float RocketSpeed = 16f, RocketMaxSeconds = 4f;
    /// <summary>A rocket meets a player whose chest (<see cref="ChestHeight"/> above the feet) it passes within this (m).</summary>
    public const float BodyRadius = 0.6f, ChestHeight = 1.0f;
    /// <summary>Where it leaves the launcher: this high above his feet, this far in front.</summary>
    public const float MuzzleHeight = 1.6f, MuzzleForward = 0.5f;
    /// <summary>The burst: radius 3.5, 120 at the centre, falling off linearly to 25 % at the edge, through the Ork blast (it hurts Orks and sets off Squigs).</summary>
    public static readonly OrkBlastSpec Blast = new(3.5f, 120f) { EdgeFraction = 0.25f };
    /// <summary>The share of Rokkit Boyz among the Orks a war battle's crowds gather (ours; the rest Boyz and Shootas by their share).</summary>
    public const float CrowdShare = 0.06f;

    /// <summary>The client's warning: one directional "incoming" cue for the single most threatening rocket, starting this long before it would strike (s) ...</summary>
    public const float WarningLeadSeconds = 1.0f;
    /// <summary>... for a rocket whose path passes within this of the listener (m: the blast's radius and a margin).</summary>
    public const float WarningMissDistance = 5f;

    /// <summary>The rocket's cues (<see cref="MusouEventKind.Telegraph"/>'s value): fired (the rocket's serial in the event's player, the point it is aimed at in the vector), burst (the serial in
    /// the player, where in the vector).</summary>
    public const byte FireCue = 28, BlastCue = 29;
}
