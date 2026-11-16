using System.Numerics;
using SmSim.Extras.Musou.Campaign;

namespace SmSim.Extras.Warfront.Fortress;

/// <summary>
/// The numbers of the fortress battle (slice W14b; spec "Warfront: the fortress battle"). Every value is ours, an estimate, unless named otherwise.
/// </summary>
public static class WarfrontFortressBattleRules
{
    /// <summary>The elite share of the crowds (plan section 2, slot 11: "elite share fixed at 0.6", Musou's own top of its curve).</summary>
    public const float EliteShare = 0.6f;
    /// <summary>A source sends no Ork while a human stands within this many metres of where it spawns (W14a's note, the brief's rule).</summary>
    public const float SourceClear = 30f;
    /// <summary>Stage 1 opens when a human first stands within this many metres (flat) of the gate or the breach (the gate is 12 m wide, the breach 8 m).</summary>
    public const float EntryRadius = 12f;
    /// <summary>Metres of height difference within which a human counts as at an entry or in the plaza (a storey's reach).</summary>
    public const float StageReach = 6f;
    /// <summary>A human's eye above its feet for the plaza sight rule, metres.</summary>
    public const float EyeHeight = 1.7f;
    /// <summary>Two entries closer than this are one waypoint.</summary>
    public const float DistinctWaypoint = 8f;
    /// <summary>The elites an elite post holds: one Nob and one Big Shoota Nob, leashed (the brief).</summary>
    public const int ElitePairSize = 2;
    /// <summary>The Boss Orks that stand on the approaches: one on each of the last two routes.</summary>
    public const int ApproachBossOrks = 2;
    /// <summary>The first leash group the fortress uses (Musou's are below 30 and 100000 on; each elite post takes one).</summary>
    public const int GroupFirst = 200;
    /// <summary>A leashed elite or Boss Ork wakes when a target comes this close (Musou's defender aggro).</summary>
    public const float Aggro = TfwBattleRules.DefenderAggro;
    /// <summary>The Warboss's leash is released when a human enters the plaza (its radius), or wakes by the usual aggro.</summary>
    public const float WarbossAggro = TfwBattleRules.DefenderAggro;
}

/// <summary>One fortress deployment's setup: the arena's plan, built once from the interface, and the line-of-sight test the server supplies (null: every plaza human sees every plaza source).</summary>
public sealed class WarfrontFortressSetup(WarfrontFortressPlan plan)
{
    /// <summary>The arena's plan.</summary>
    public WarfrontFortressPlan Plan { get; } = plan;
    /// <summary>A setup of any arena.</summary>
    public static WarfrontFortressSetup For(IWarfrontArena arena) => new(WarfrontArenaLayout.Plan(arena));
    /// <summary>Whether a straight line between two points is clear of the level (eye to spawn spot); the server supplies it from the arena's collision.</summary>
    public Func<Vector3, Vector3, bool>? Sight { get; set; }
}

/// <summary>
/// The stage flow of one fortress battle (slice W14b): stage 0 from the landing, stage 1 when a human first stands at the gate or the breach, stage 2 when a human first enters the plaza (which also frees the Warboss). The
/// stage never goes back. A source is open when its stage has come, no human stands within <see cref="WarfrontFortressBattleRules.SourceClear"/> of where it spawns and, for a stage-2 source, no human standing in
/// the plaza has a clear line to it.
/// </summary>
public sealed class WarfrontFortressState(WarfrontFortressSetup setup)
{
    readonly WarfrontFortressPlan _plan = setup.Plan;
    Vector3[] _humans = new Vector3[8];
    int _count;

    /// <summary>The setup.</summary>
    public WarfrontFortressSetup Setup { get; } = setup;
    /// <summary>The plan.</summary>
    public WarfrontFortressPlan Plan => _plan;
    /// <summary>The stage now (0, 1 or 2; at most the plan's highest).</summary>
    public int Stage { get; private set; }
    /// <summary>Whether a human has entered the plaza (the Warboss is free).</summary>
    public bool PlazaEntered { get; private set; }
    /// <summary>The humans seen this tick.</summary>
    public int Humans => _count;

    /// <summary>Whether <paramref name="p"/> stands in the plaza (flat radius round the boss spot, within a storey of its ground).</summary>
    public bool InPlaza(Vector3 p) => Flat(p, _plan.BossSpot) <= _plan.ArenaRadius && MathF.Abs(p.Z - _plan.BossSpot.Z) <= WarfrontFortressBattleRules.StageReach * 2f;

    bool AtEntry(Vector3 p) => (Flat(p, _plan.Gate) <= WarfrontFortressBattleRules.EntryRadius && MathF.Abs(p.Z - _plan.Gate.Z) <= WarfrontFortressBattleRules.StageReach)
        || (Flat(p, _plan.Breach) <= WarfrontFortressBattleRules.EntryRadius && MathF.Abs(p.Z - _plan.Breach.Z) <= WarfrontFortressBattleRules.StageReach);

    /// <summary>One tick: the living humans' feet. Returns true on the tick the plaza is first entered (the Warboss is to be freed).</summary>
    public bool Observe(ReadOnlySpan<Vector3> humans)
    {
        if (_humans.Length < humans.Length) _humans = new Vector3[humans.Length * 2];
        humans.CopyTo(_humans);
        _count = humans.Length;
        bool entry = false, plaza = false;
        for (int i = 0; i < _count; i++)
        {
            entry |= AtEntry(_humans[i]);
            plaza |= InPlaza(_humans[i]);
        }
        bool entered = plaza && !PlazaEntered;
        PlazaEntered |= plaza;
        int target = Math.Min(PlazaEntered ? 2 : entry ? 1 : 0, _plan.MaxStage);
        if (target > Stage) Stage = target;
        return entered;
    }

    /// <summary>Whether a source of layout index <paramref name="index"/> may send now: its stage has come, no human within the clear distance of <paramref name="anchor"/> (or of <paramref name="second"/>, the crowd anchor of a spawn point), no sight into the plaza for stage 2.</summary>
    public bool SourceOpen(int index, Vector3 anchor, Vector3? second = null)
    {
        if ((uint)index >= (uint)_plan.SourceStage.Length) return false;
        int stage = _plan.SourceStage[index];
        if (stage > Stage) return false;
        float clear = WarfrontFortressBattleRules.SourceClear;
        for (int i = 0; i < _count; i++)
        {
            var h = _humans[i];
            if (Flat(h, anchor) < clear && MathF.Abs(h.Z - anchor.Z) < clear) return false;
            if (second is { } s && Flat(h, s) < clear && MathF.Abs(h.Z - s.Z) < clear) return false;
        }
        if (stage >= 2 && (!SpawnHidden(anchor) || (second is { } sp && !SpawnHidden(sp)))) return false;
        return true;
    }

    /// <summary>Whether no human standing in the plaza has a clear line to <paramref name="at"/> (without a sight test: every human in the plaza sees it).</summary>
    public bool SpawnHidden(Vector3 at)
    {
        var sight = Setup.Sight;
        for (int i = 0; i < _count; i++)
        {
            if (!InPlaza(_humans[i])) continue;
            if (sight is null) return false;
            var eye = _humans[i] + new Vector3(0f, 0f, WarfrontFortressBattleRules.EyeHeight);
            if (sight(eye, at + new Vector3(0f, 0f, WarfrontFortressBattleRules.EyeHeight))) return false;
        }
        return true;
    }

    static float Flat(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Y - b.Y).Length();
}
