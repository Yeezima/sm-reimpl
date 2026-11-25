using System.Numerics;
using SmSim;
using SmSim.Extras.Executions;

namespace SmNet.Tests.Extras.Executions;

/// <summary>Executions X3's synthetic rows (no original bytes: ids, names and timings from <see cref="ExecutionRows.All"/> or made up here, root paths generated).</summary>
static class ExecutionTestKit
{
    /// <summary>A straight root path forward over <paramref name="metres"/> in <paramref name="samples"/> evenly spaced samples (the turn <paramref name="turn"/> spread over it).</summary>
    public static Vector4[] Straight(float metres, int samples = 31, float turn = 0f) =>
        Enumerable.Range(0, samples).Select(i => { float f = samples == 1 ? 0f : i / (float)(samples - 1); return new Vector4(0f, metres * f, 0f, turn * f); }).ToArray();

    /// <summary>
    /// A synthetic two-variant row (ids of <see cref="ExecutionRows.All"/> so the wire order resolves it): the reference clip at the authored gap <paramref name="gap"/>
    /// and a near variant at <paramref name="near"/>, each walking its own distance minus 0.9 m forward, 2 s long, kill at 1 s, end at 1.8 s.
    /// </summary>
    public static ExecutionRow Row(string id = "C1.chainsword", string target = ExecutionTargets.ChaosMarine, string weapon = ExecutionWeapons.Chainsword, float gap = 1.3f, float near = 1.0f,
        float seconds = 2f, float kill = 1f, float? end = 1.8f)
    {
        var events = new List<ExecutionEvent> { new(ExecutionEventKind.Sync, 0.1f), new(ExecutionEventKind.Kill, kill) };
        if (end is { } e) events.Add(new(ExecutionEventKind.End, e));
        return new ExecutionRow(id, "*", weapon, target, "ref_clip", "victim_clip", seconds, seconds, events, new Vector3(0f, gap, 0f), MathF.PI, "graph", "seq")
        {
            AttackerVariants = [new("ref_clip", gap, Straight(MathF.Max(0f, gap - 0.9f))), new("near_clip", near, Straight(MathF.Max(0f, near - 0.9f)))],
            AttackerRoot = Straight(MathF.Max(0f, gap - 0.9f)),
            VictimRoot = Straight(0f, 2),
        };
    }

    /// <summary>Rows for every Classic victim kind and the chainsword (Chaos Marine C1, Ork Boy O1) as a set in the wire order of <see cref="ExecutionRows.All"/>.</summary>
    public static ExecutionRowSet ClassicSet(float gap = 1.3f) => ExecutionRowSet.OfAll(
        [Row("C1.chainsword", ExecutionTargets.ChaosMarine, gap: gap), Row("O1.chainsword", ExecutionTargets.OrkBoy, gap: gap)]);

    // ---- a synthetic Classic arena (no level: flat ground at z 0; no rules: everyone hostile) ----

    public const float Dt = 1f / 30f;
    static float T(float ticks) => ticks / 30f;

    /// <summary>A lethal light attack: hit at tick 4, over at 21 (5000 damage on a 100 hp body; none to the others in the cone).</summary>
    public static readonly MeleeStep Lethal = new(T(20.5f), T(3.5f), T(10.5f), T(15.5f), T(12.5f), T(18.5f), 5000f, 0f, 3.5f);
    /// <summary>A nonlethal light attack (10 damage) that interrupts.</summary>
    public static readonly MeleeStep Light = new(T(20.5f), T(3.5f), T(10.5f), T(15.5f), T(12.5f), T(18.5f), 10f, 10f, 3.5f) { PrimaryReaction = new ReactionEntry(0f, ReactionKind.Interrupt) };

    public const int Tactical = 0, Chaos = 1, Kicker = 2, Ork = 3, Tapper = 4;

    static ClassLoadout Class(string name, string wargear, Team side, MeleeStep step, Race race = Race.None) =>
        new(name, 0, 0, [step]) { MeleeChoices = [new MeleeCombo(wargear, [step])], Side = side, Race = race };

    static readonly WeaponSpec Pistol = new("pistol", WeaponSlot.Pistol, TriggerKind.SemiAuto, 20f, 0.2f, 10, 50, 1.3f, 100f, 100f, 100f, 1f, 2.5f, 1f, 0.5f, 0.5f);

    /// <summary>The arena's config: the executions flag (Classic: no Warfront) and <see cref="ClassicSet"/>.</summary>
    public static SimConfig Config(ExecutionRowSet? rows = null, ContentExtras extras = ContentExtras.Executions) => new()
    {
        Extras = extras,
        Executions = rows ?? ClassicSet(),
        Weapons = [Pistol],
        Classes =
        [
            Class("tactical", "sm_mp_melee_chainsword", Team.SpaceMarines, Lethal),
            Class("chaos", "csm_mp_melee_chainsword", Team.Chaos, Lethal),
            Class("kicker", "sm_mp_melee_kick", Team.SpaceMarines, Lethal),
            Class("ork", "sm_mp_melee_chainsword", Team.None, Lethal, Race.Orks),
            Class("tapper", "csm_mp_melee_chainsword", Team.Chaos, Light),
        ],
        MeleeRange = 4f, MeleeConeDegrees = 60f, MeleeVerticalReach = 1.5f,
        HealthMax = 100f, ShieldMax = 0f, HealthRegenDelay = 1e6f,
    };

    /// <summary>A world with players 1.. of the given classes at the given places (player 1 faces +Y, the others -Y).</summary>
    public static World Arena(SimConfig c, params (int Class, Vector3 At)[] players) => Arena(c, null, players);

    /// <summary>As <see cref="Arena(SimConfig, (int, Vector3)[])"/> on <paramref name="level"/> (null: none).</summary>
    public static World Arena(SimConfig c, CollisionWorld? level, params (int Class, Vector3 At)[] players)
    {
        var w = new World(c, level, players.Select((p, i) => new SpawnPoint(p.At, i == 0 ? 0f : MathF.PI)).ToArray());
        for (int i = 0; i < players.Length; i++) w.Spawn(i + 1, players[i].Class);
        for (int i = 0; i < players.Length; i++) w.ExecutionsPlaceForTests(i + 1, players[i].At, i == 0 ? 0f : MathF.PI);   // the spawn choice need not follow the list's order
        return w;
    }

    /// <summary>One tick: each player's buttons (player 1 faces +Y, the others -Y), the view tick <paramref name="view"/> (null: the tick being stepped; the lambda runs after the world's tick has advanced).</summary>
    public static void Step(World w, Func<int, InputButtons> buttons, uint? view = null, Func<int, (float X, float Y)>? stick = null) =>
        w.Step(id => { var (x, y) = stick?.Invoke(id) ?? (0f, 0f); return new PlayerInput(0, x, y, id == 1 ? 0f : MathF.PI, buttons(id), 0f, view ?? w.Tick); });

    /// <summary>Player 1 swings once at tick 0; steps until the first kill (at most 40 ticks) and returns its tick.</summary>
    public static uint KillByMelee(World w, int killer = 1)
    {
        for (int k = 0; k < 40; k++)
        {
            Step(w, id => id == killer && k == 0 ? InputButtons.Melee : InputButtons.None);
            if (w.LastKills.Count > 0) return w.LastKills[0].Tick;
        }
        throw new Xunit.Sdk.XunitException("no kill");
    }

    /// <summary>A tap of Execute by <paramref name="id"/> this tick (with <paramref name="view"/>).</summary>
    public static void Tap(World w, int id, uint? view = null)
    {
        Step(w, p => p == id ? InputButtons.Execute : InputButtons.None, view);
    }

    /// <summary>A flat floor (its top at z 0), 200 m square.</summary>
    public static CollisionWorld Flat()
    {
        Vector3[] v = [new(-100, -100, 0), new(100, -100, 0), new(100, 100, 0), new(-100, 100, 0)];
        var m = new StaticMesh(v, [0, 1, 2, 0, 2, 3]);
        return new CollisionWorld(m, m);
    }
}
