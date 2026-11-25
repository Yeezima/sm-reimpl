using System.Numerics;
using SmSim;
using SmSim.Extras.Executions;

namespace SmNet.Tests.Extras.Executions.Acceptance;

/// <summary>
/// Executions X8 (acceptance suite): synthetic fixtures, written independently of the implementers' <c>ExecutionTestKit</c>. No original bytes: row ids come from the wire order
/// (<see cref="ExecutionRows.All"/>) so the wire resolves them, every clip name, length and root path is made up here. Worlds: flat, walled, gapped, stepped, sloped, ledged, kill-volume
/// and spawn-blocker levels as <see cref="CollisionWorld"/>s built from triangles.
/// </summary>
static class ExecutionFixtures
{
    // ---------------------------------------------------------------- synthetic rows

    /// <summary>A straight root path forward over <paramref name="metres"/> in <paramref name="samples"/> evenly spaced samples.</summary>
    public static Vector4[] Straight(float metres, int samples = 31) =>
        Enumerable.Range(0, samples).Select(i => new Vector4(0f, metres * (i / (float)(samples - 1)), 0f, 0f)).ToArray();

    static ExecutionRow Make(string id, string target, string weapon, float[] gaps, float seconds, float kill, float end)
    {
        var events = new List<ExecutionEvent> { new(ExecutionEventKind.Sync, 0.1f), new(ExecutionEventKind.Kill, kill), new(ExecutionEventKind.End, end) };
        float reference = gaps[gaps.Length / 2];
        return new ExecutionRow(id, "*", weapon, target, $"{id}_att", $"{id}_vic", seconds, seconds, events, new Vector3(0f, reference, 0f), MathF.PI, "graph", "seq")
        {
            AttackerVariants = gaps.Select((g, i) => new ExecutionVariant($"{id}_att{i}", g, Straight(MathF.Max(0f, g - 0.9f)))).ToArray(),
            AttackerRoot = Straight(MathF.Max(0f, reference - 0.9f)),
            VictimRoot = Straight(0f, 2),
        };
    }

    /// <summary>A two-variant Ork Boy row (gaps 1.0 and 1.6 m) played with the chainsword family.</summary>
    public static ExecutionRow BoyRow() => Make("O1.chainsword", ExecutionTargets.OrkBoy, ExecutionWeapons.Chainsword, [1.0f, 1.6f], 2f, 1f, 1.8f);

    /// <summary>A three-variant Chaos Marine row (gaps 1.0, 1.3 and 1.6 m), chainsword family. Also the race-swap row (a loyalist victim takes the Chaos Marine rows).</summary>
    public static ExecutionRow ChaosRow() => Make("C1.chainsword", ExecutionTargets.ChaosMarine, ExecutionWeapons.Chainsword, [1.0f, 1.3f, 1.6f], 2f, 1f, 1.8f);

    /// <summary>A second Chaos Marine chainsword row (another clip, so a seeded pick has two rows to choose between).</summary>
    public static ExecutionRow ChaosAltRow() => Make("R1.chainsword", ExecutionTargets.ChaosMarine, ExecutionWeapons.Chainsword, [1.1f, 1.4f], 2.2f, 1.1f, 2f);

    /// <summary>A single-clip Chaos Marine row (one gap, as the install's real C1 to C3).</summary>
    public static ExecutionRow ChaosSingleRow(float gap = 1.3f) => Make("C1.chainsword", ExecutionTargets.ChaosMarine, ExecutionWeapons.Chainsword, [gap], 2f, 1f, 1.8f);

    /// <summary>The Classic set: the three-variant Chaos row, the two-variant Boy row (wire order of <see cref="ExecutionRows.All"/>).</summary>
    public static ExecutionRowSet ClassicSet() => ExecutionRowSet.OfAll([ChaosRow(), BoyRow()]);

    // ---------------------------------------------------------------- the arena

    public const int Tactical = 0, Chaos = 1, Kicker = 2, Ork = 3, Tapper = 4, PowerSword = 5, Claw = 6, Spear = 7, Flincher = 8, Staggerer = 9, Stunner = 10;

    /// <summary>A lethal light attack: hit at tick 4 (5000 damage on a 100 hp body).</summary>
    public static readonly MeleeStep Lethal = new(T(20.5f), T(3.5f), T(10.5f), T(15.5f), T(12.5f), T(18.5f), 5000f, 0f, 3.5f);
    /// <summary>A nonlethal light attack: 10 damage, an interrupt reaction.</summary>
    public static readonly MeleeStep Light = new(T(20.5f), T(3.5f), T(10.5f), T(15.5f), T(12.5f), T(18.5f), 10f, 10f, 3.5f) { PrimaryReaction = new ReactionEntry(0f, ReactionKind.Interrupt, Duration: 0.5f) };
    /// <summary>A light strike with no reaction entry: it only flinches the victim's aim (sim/light-melee-flinch).</summary>
    public static readonly MeleeStep FlinchOnly = Light with { PrimaryReaction = default };
    /// <summary>A stagger-only strike: 5 damage, a half-second stagger.</summary>
    public static readonly MeleeStep Stagger = Light with { Damage = 5f, PrimaryReaction = new ReactionEntry(0f, ReactionKind.Stagger, Backward: 0.3f, Duration: 0.5f) };
    /// <summary>A stun strike: 5 damage, a half-second stun.</summary>
    public static readonly MeleeStep StunStrike = Light with { Damage = 5f, PrimaryReaction = new ReactionEntry(0f, ReactionKind.Stun, Duration: 0.5f) };
    // In this sim a light hit only flinches (sim/light-melee-flinch); reactions come from heavy strikes, so the reaction classes carry theirs on the heavy key (InputButtons.MeleeHeavy).
    static float T(float ticks) => ticks / 30f;

    static ClassLoadout Class(string name, string wargear, Team side, MeleeStep step, Race race = Race.None, MeleeStep? heavy = null) =>
        new(name, 0, 0, [step]) { MeleeChoices = [new MeleeCombo(wargear, [step]) { Heavy = heavy is { } h ? [h] : [] }], MeleeHeavy = heavy is { } hh ? [hh] : [], Side = side, Race = race };

    static readonly WeaponSpec Pistol = new("pistol", WeaponSlot.Pistol, TriggerKind.SemiAuto, 20f, 0.2f, 10, 50, 1.3f, 100f, 100f, 100f, 1f, 2.5f, 1f, 0.5f, 0.5f);

    /// <summary>The arena's config: executions on (Classic: no Warfront), health 100 and shield 50, no regeneration inside the tests' time.</summary>
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
            Class("tapper", "csm_mp_melee_chainsword", Team.Chaos, Light, heavy: Light),
            Class("sworder", "sm_mp_melee_powersword", Team.SpaceMarines, Lethal),
            Class("clawer", "ork_mp_melee_claw", Team.SpaceMarines, Lethal),
            Class("speared", "sm_mp_melee_spear", Team.SpaceMarines, Lethal),
            Class("flincher", "csm_mp_melee_chainsword", Team.Chaos, FlinchOnly),
            Class("staggerer", "csm_mp_melee_chainsword", Team.Chaos, FlinchOnly, heavy: Stagger),
            Class("stunner", "csm_mp_melee_chainsword", Team.Chaos, FlinchOnly, heavy: StunStrike),
        ],
        MeleeRange = 4f, MeleeConeDegrees = 60f, MeleeVerticalReach = 1.5f,
        HealthMax = 100f, ShieldMax = 50f, HealthRegenDelay = 1e6f,
    };

    public static World Arena(SimConfig c, params (int Class, Vector3 At)[] players) => Arena(c, null, null, players);

    public static World Arena(SimConfig c, CollisionWorld? level, params (int Class, Vector3 At)[] players) => Arena(c, level, null, players);

    /// <summary>A world with players 1.. of the given classes at the given places (player 1 faces +Y, the others -Y); <paramref name="rules"/> null: no match rules (everyone hostile).</summary>
    public static World Arena(SimConfig c, CollisionWorld? level, MatchRules? rules, params (int Class, Vector3 At)[] players)
    {
        var w = new World(c, level, players.Select((p, i) => new SpawnPoint(p.At, i == 0 ? 0f : MathF.PI)).ToArray()) { Rules = rules };
        for (int i = 0; i < players.Length; i++) w.Spawn(i + 1, players[i].Class);
        for (int i = 0; i < players.Length; i++) w.ExecutionsPlaceForTests(i + 1, players[i].At, i == 0 ? 0f : MathF.PI);
        return w;
    }

    /// <summary>One tick with each player's buttons (player 1 faces +Y, the others -Y) and the view tick <paramref name="view"/> (null: the tick being stepped).</summary>
    public static void Step(World w, Func<int, InputButtons> buttons, uint? view = null) =>
        w.Step(id => new PlayerInput(0, 0f, 0f, id == 1 ? 0f : MathF.PI, buttons(id), 0f, view ?? w.Tick));

    public static void Idle(World w, int ticks) { for (int i = 0; i < ticks; i++) Step(w, _ => InputButtons.None); }

    /// <summary>Player <paramref name="killer"/> swings once at the first tick; steps until the first kill (at most 40 ticks) and returns its tick.</summary>
    public static uint KillByMelee(World w, int killer = 1)
    {
        for (int k = 0; k < 40; k++)
        {
            Step(w, id => id == killer && k == 0 ? InputButtons.Melee : InputButtons.None);
            if (w.LastKills.Count > 0) return w.LastKills[0].Tick;
        }
        throw new Xunit.Sdk.XunitException("no kill within 40 ticks");
    }

    /// <summary>A tap of Execute by <paramref name="id"/> this tick with view tick <paramref name="view"/>.</summary>
    public static void Tap(World w, int id, uint? view = null) => Step(w, p => p == id ? InputButtons.Execute : InputButtons.None, view);

    public static readonly Vector3 Near = new(0f, 1.2f, 0f);

    /// <summary>Player 1 of class <paramref name="killer"/> kills player 2 of class <paramref name="victim"/> 1.2 m ahead by melee; returns the world and the kill tick.</summary>
    public static (World W, uint Kill) Killed(int killer = Tactical, int victim = Chaos, SimConfig? config = null, CollisionWorld? level = null, MatchRules? rules = null, params (int Class, Vector3 At)[] more)
    {
        var w = Arena(config ?? Config(), level, rules, [(killer, Vector3.Zero), (victim, Near), .. more]);
        uint kill = KillByMelee(w);
        if (w.LastKills[0].Cause != KillCause.Melee) throw new Xunit.Sdk.XunitException("the first kill was not a melee kill");
        return (w, kill);
    }

    // ---------------------------------------------------------------- levels

    /// <summary>Builds a level from quads (4 corners each); the floor is a 200 m square at z 0 unless <paramref name="floor"/> is false.</summary>
    public static CollisionWorld Level(IEnumerable<Vector3[]> quads, bool floor = true, IReadOnlyList<KillVolume>? kill = null, IReadOnlyList<SpawnBlocker>? blockers = null)
    {
        var v = new List<Vector3>();
        var i = new List<int>();
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) { int n = v.Count; v.AddRange([a, b, c, d]); i.AddRange([n, n + 1, n + 2, n, n + 2, n + 3]); }
        if (floor) Quad(new(-100, -100, 0), new(100, -100, 0), new(100, 100, 0), new(-100, 100, 0));
        foreach (var q in quads) Quad(q[0], q[1], q[2], q[3]);
        var m = new StaticMesh(v.ToArray(), i.ToArray());
        return new CollisionWorld(m, m, kill, blockers);
    }

    public static IEnumerable<Vector3[]> Box(Vector3 min, Vector3 max)
    {
        Vector3 P(int x, int y, int z) => new(x == 0 ? min.X : max.X, y == 0 ? min.Y : max.Y, z == 0 ? min.Z : max.Z);
        yield return [P(0, 0, 0), P(1, 0, 0), P(1, 1, 0), P(0, 1, 0)];
        yield return [P(0, 0, 1), P(0, 1, 1), P(1, 1, 1), P(1, 0, 1)];
        yield return [P(0, 0, 0), P(0, 0, 1), P(1, 0, 1), P(1, 0, 0)];
        yield return [P(0, 1, 0), P(1, 1, 0), P(1, 1, 1), P(0, 1, 1)];
        yield return [P(0, 0, 0), P(0, 1, 0), P(0, 1, 1), P(0, 0, 1)];
        yield return [P(1, 0, 0), P(1, 0, 1), P(1, 1, 1), P(1, 1, 0)];
    }

    /// <summary>The flat floor (top at z 0), 200 m square.</summary>
    public static CollisionWorld Flat() => Level([]);

    /// <summary>A waist-high wall across the corridor 0.9 to 1.2 m ahead of the origin (x -8 to 8, 1 m tall: blocks the slide).</summary>
    public static CollisionWorld Walled() => Level(Box(new(-8f, 0.9f, 0f), new(8f, 1.2f, 1f)));

    /// <summary>The floor cut between y = <paramref name="from"/> and <paramref name="to"/>: a hole (a gap) the slide would cross.</summary>
    public static CollisionWorld Gapped(float from = 0.7f, float to = 1.5f)
    {
        var quads = new List<Vector3[]>
        {
            new Vector3[] { new(-100, -100, 0), new(100, -100, 0), new(100, from, 0), new(-100, from, 0) },
            new Vector3[] { new(-100, to, 0), new(100, to, 0), new(100, 100, 0), new(-100, 100, 0) },
        };
        return Level(quads, floor: false);
    }

    /// <summary>The floor beyond y = <paramref name="at"/> stepped by <paramref name="height"/> metres (a rise when positive, a ledge down when negative).</summary>
    public static CollisionWorld Stepped(float at, float height)
    {
        var quads = new List<Vector3[]>
        {
            new Vector3[] { new(-100, -100, 0), new(100, -100, 0), new(100, at, 0), new(-100, at, 0) },
            new Vector3[] { new(-100, at, height), new(100, at, height), new(100, 100, height), new(-100, 100, height) },
            // the riser, so the step is a closed surface
            new Vector3[] { new(-100, at, MathF.Min(0, height)), new(100, at, MathF.Min(0, height)), new(100, at, MathF.Max(0, height)), new(-100, at, MathF.Max(0, height)) },
        };
        return Level(quads, floor: false);
    }

    /// <summary>The floor from y = <paramref name="from"/> onwards rising at <paramref name="degrees"/> (a ramp, then level at its top is not modelled: the ramp runs on).</summary>
    public static CollisionWorld Sloped(float from, float degrees)
    {
        float rise = MathF.Tan(degrees * MathF.PI / 180f) * (100f - from);
        var quads = new List<Vector3[]>
        {
            new Vector3[] { new(-100, -100, 0), new(100, -100, 0), new(100, from, 0), new(-100, from, 0) },
            new Vector3[] { new(-100, from, 0), new(100, from, 0), new(100, 100, rise), new(-100, 100, rise) },
        };
        return Level(quads, floor: false);
    }

    /// <summary>The height of <see cref="Sloped"/>'s surface at <paramref name="y"/>.</summary>
    public static float SlopeZ(float y, float from, float degrees) => y <= from ? 0f : (y - from) * MathF.Tan(degrees * MathF.PI / 180f);

    /// <summary>A kill volume straddling the slide's path (x -8 to 8, y 0.8 to 1.2, from the floor up 3 m).</summary>
    public static CollisionWorld KillVolumed() => Level([], kill: [new KillVolume(new Vector3(0f, 1.0f, 1.5f), new Vector3(8f, 0.2f, 1.5f), 0f)]);

    /// <summary>A spawn blocker of <paramref name="side"/> (it keeps the other side out) across the path (x -8 to 8, y 0.8 to 1.2).</summary>
    public static CollisionWorld Blockered(Team side) => Level([], blockers: [new SpawnBlocker(side, new Vector3(0f, 1.0f, 2f), new Vector3(16f, 0.4f, 4f), 0f)]);
}
