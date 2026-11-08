using System.Numerics;
using SmNet.Extras.Gore;
using SmSim.Extras.Musou;

namespace SmNet.Extras.Musou;

/// <summary>
/// The rules of Musou Mode's gore, engine-free (Musou pass 2 M10; design section D and the owner's items 8, 22 and 23). Every number is ours (kind: ours), inspired by
/// the Project Brutality mod ("Ha Ha Gore Goes Burr"): the budgets are the design's (gib pool 256, 12 deaths processed a frame, the distance bands), the pools of
/// droplets, pools on the ground and spatter are sized for 200 enemies and heavier death rates.
/// </summary>
public static class MusouGoreRules
{
    /// <summary>How solid a gore cloud is at its thickest in Musou Mode (the owner, 2026-11-02: "slightly more see through"; the shared cloud's is <see cref="GoreRules.CloudAlpha"/>).</summary>
    public const float CloudAlpha = 0.58f;

    /// <summary>Whether a death throws the body (the owner, 2026-11-02): artillery, a frag, a launcher's or a plasma cannon's blast. A lascannon shot still bursts with no corpse, and so does a
    /// bolt blast (<see cref="MusouDeathKind.BoltBlast"/>: owner 2026-11-08, torn apart on the spot, no ragdoll).</summary>
    public static bool Thrown(MusouDeathKind cause) => cause is MusouDeathKind.Blast or MusouDeathKind.Barrage or MusouDeathKind.OrkBlast;

    /// <summary>
    /// The velocity (m/s) a blast gives a body: one of three throws by a repeatable roll, mostly up, mostly sideways or both, away from the blast along <paramref name="push"/> (no push: a direction
    /// by id) give or take 35 degrees; small bodies fly further, big ones less. Every number is ours (spec "Flung corpses").
    /// </summary>
    public static Vector3 BlastThrow(Vector3 push, ushort id, uint tick, MusouEnemyType type)
    {
        float kind = GoreRules.Roll(id, tick), a = GoreRules.Roll(id, tick + 1u), b = GoreRules.Roll(id, tick + 2u), turn = (GoreRules.Roll(id, tick + 3u) * 2f - 1f) * 35f * MathF.PI / 180f;
        var (side, up) = kind < 0.3f ? (1.5f + 2.5f * a, 11f + 4f * b) : kind < 0.6f ? (11f + 5f * a, 1.5f + 2f * b) : (8f + 4f * a, 8f + 4f * b);
        var flat = new Vector2(push.X, push.Y);
        float angle = flat.LengthSquared() > 0.0025f ? MathF.Atan2(flat.Y, flat.X) + turn : id * 2.399963f;
        float weight = MusouBossOrks.Is(type) ? 0.4f : type switch   // a Boss Ork as the Warboss
        {
            MusouEnemyType.Gretchin => 1.3f,
            MusouEnemyType.ArdBoy => 0.9f,
            MusouEnemyType.Nob or MusouEnemyType.ChaosMarine => 0.7f,
            MusouEnemyType.Warboss => 0.4f,
            _ => 1f,
        };
        return new Vector3(MathF.Cos(angle) * side, MathF.Sin(angle) * side, up) * weight;
    }

    /// <summary>
    /// What a death is. The killer is the death event's player (0: none; a human's id or a retainer's; Guardsmen's kills carry 0). A blast (a frag, a launcher or plasma
    /// burst), artillery (Barrage) and a lascannon shot (<paramref name="killerHasLascannon"/>: the killer's weapon in hand, read by the view) burst whoever the killer; a
    /// Thunderhawk strafe is gore; the rest only when a player or retainer killed. A failsafe removal (World) is nothing.
    /// </summary>
    public static GoreClass Classify(MusouDeathKind cause, int killer, bool killerHasLascannon) => cause switch
    {
        MusouDeathKind.World => GoreClass.None,
        MusouDeathKind.Barrage or MusouDeathKind.Blast or MusouDeathKind.OrkBlast or MusouDeathKind.BoltBlast => GoreClass.Explosion,
        MusouDeathKind.Strafe => GoreClass.Gore,
        _ => killer <= 0 ? GoreClass.None : killerHasLascannon && cause == MusouDeathKind.Shot ? GoreClass.Explosion : GoreClass.Gore,
    };

    /// <summary>How big an enemy is for gore: a Gretchin half a Boy, the Warboss four.</summary>
    public static float Weight(MusouEnemyType type) => MusouBossOrks.Is(type) ? 4f : type switch   // a Boss Ork as the Warboss (3x the body)
    {
        MusouEnemyType.Gretchin => 0.5f,
        MusouEnemyType.Boy or MusouEnemyType.ShootaBoy => 1f,
        MusouEnemyType.ArdBoy => 1.5f,
        MusouEnemyType.ChaosMarine => 1.6f,
        MusouEnemyType.Nob => 2.2f,
        MusouEnemyType.Warboss => 4f,
        _ => 1f,
    };

    /// <summary>The chance a gore death (not an explosion, which always bursts) leaves no corpse: melee and charges 35 % (15 % for the big), a strafe 60 %, a shot never.</summary>
    public static float GibbedChance(MusouDeathKind cause, MusouEnemyType type) => cause switch
    {
        MusouDeathKind.Melee => Weight(type) <= 1f ? 0.35f : 0.15f,
        MusouDeathKind.Strafe => 0.6f,
        _ => 0f,
    };

    /// <summary>
    /// The burst of one death at <paramref name="metres"/> from the camera. <paramref name="load"/> (0.25 to 1, <see cref="GoreField.LoadScale"/>) thins every count
    /// while the pools are filling, so heavy death rates (200 enemies, a barrage) recycle fewer live particles.
    /// </summary>
    public static GoreBurst Plan(MusouDeathKind cause, MusouEnemyType type, int killer, bool killerHasLascannon, float metres, float load, ushort id, uint tick)
    {
        var cls = Classify(cause, killer, killerHasLascannon);
        if (cls == GoreClass.None) return GoreBurst.Nothing;
        var lod = GoreRules.LodOf(metres);
        bool gibbed = cls == GoreClass.Explosion || lod != GoreLod.None && GoreRules.Roll(id, tick) < GibbedChance(cause, type);
        if (lod == GoreLod.None) return new GoreBurst(cls, lod, 0, 0, 0, 0, 0, 0f, gibbed);

        return GoreRules.Burst(cls, cause == MusouDeathKind.Melee ? 0 : cause == MusouDeathKind.Strafe ? 1 : 2, Weight(type), metres, load, gibbed);
    }

    /// <summary>
    /// A cleave slash: whether it is drawn for a hit at <paramref name="metres"/> (item 22: a slashing bloody visual on each enemy a cleave hits). A melee hit is marker 3
    /// on a <see cref="MusouEventKind.Hit"/>; a player or retainer made it (<paramref name="attacker"/> above 0).
    /// </summary>
    public static bool SlashDrawn(int attacker, float metres) => attacker > 0 && metres <= GoreRules.SlashMetres;
}

