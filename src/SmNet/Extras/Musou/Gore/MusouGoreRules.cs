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
    /// <summary>
    /// What a death is. The killer is the death event's player (0: none; a human's id or a retainer's; Guardsmen's kills carry 0). A blast (a frag, a launcher or plasma
    /// burst), artillery (Barrage) and a lascannon shot (<paramref name="killerHasLascannon"/>: the killer's weapon in hand, read by the view) burst whoever the killer; a
    /// Thunderhawk strafe is gore; the rest only when a player or retainer killed. A failsafe removal (World) is nothing.
    /// </summary>
    public static GoreClass Classify(MusouDeathKind cause, int killer, bool killerHasLascannon) => cause switch
    {
        MusouDeathKind.World => GoreClass.None,
        MusouDeathKind.Barrage or MusouDeathKind.Blast => GoreClass.Explosion,
        MusouDeathKind.Strafe => GoreClass.Gore,
        _ => killer <= 0 ? GoreClass.None : killerHasLascannon && cause == MusouDeathKind.Shot ? GoreClass.Explosion : GoreClass.Gore,
    };

    /// <summary>How big an enemy is for gore: a Gretchin half a Boy, the Warboss four.</summary>
    public static float Weight(MusouEnemyType type) => type switch
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

