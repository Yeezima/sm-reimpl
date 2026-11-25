using System.Numerics;
using SmNet.Tests.Extras.Musou.Campaign;
using SmSim;
using SmSim.Extras.Executions;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Executions.Orks;

/// <summary>
/// Executions X4's Warfront rig (synthetic only: no install, no original bytes): <c>TfwRig</c>'s war under <see cref="ContentProfiles.WarfrontProfile"/> (Musou, Warfront, Executions), its two
/// classes given a chainsword melee choice, and synthetic Ork Boy and Gretchin rows (<see cref="ExecutionTestKit.Row"/>, ids from <see cref="ExecutionRows.All"/>).
/// </summary>
static class ExecutionOrkKit
{
    /// <summary>The Warfront config with chainsword classes and the Ork rows (<paramref name="rows"/>; null: <see cref="OrkSet"/>).</summary>
    public static SimConfig Config(ExecutionRowSet? rows = null, ContentExtras? extras = null) => TfwRig.Config with
    {
        Extras = extras ?? ContentProfiles.WarfrontProfile,
        Executions = rows ?? OrkSet(),
        Classes = [.. TfwRig.Config.Classes.Select(c => c with { MeleeChoices = [new MeleeCombo(c.Side == Team.Chaos ? "csm_mp_melee_chainsword" : "sm_mp_melee_chainsword", c.Melee)] })],
    };

    /// <summary>An Ork Boy row (O1) and, with <paramref name="gretchin"/>, a Gretchin row (O9), both chainsword, 2 s, kill at 1 s, end at 1.8 s, gap 1.3 m (near variant 1.0 m).</summary>
    public static ExecutionRowSet OrkSet(bool gretchin = true) => ExecutionRowSet.OfAll(gretchin
        ? [ExecutionTestKit.Row("O1.chainsword", ExecutionTargets.OrkBoy), ExecutionTestKit.Row("O9.chainsword", ExecutionTargets.Gretchin)]
        : [ExecutionTestKit.Row("O1.chainsword", ExecutionTargets.OrkBoy)]);

    /// <summary>The war, deployed to <paramref name="lane"/> and landed (the human, player 1, in the battle). The isolation pin's battle: as <c>TfwRig</c> sets it up.</summary>
    public static World Battle(SimConfig? config = null, TfwLane lane = TfwLane.B, int humans = 1)
    {
        var w = TfwRig.War(config: config ?? Config());
        for (int id = 2; id <= humans; id++) w.Spawn(id, w.AssignClass(0));
        TfwRig.Deploy(w, lane);
        return w;
    }

    /// <summary>
    /// A quiet battle for the unit-level checks: no Guard squads or allies, every Ork killed, the pool frozen (nobody acts until <see cref="Settle"/> or a test unfreezes it), the human(s) facing +Y.
    /// </summary>
    public static World Rig(SimConfig? config = null, int humans = 1)
    {
        var w = TfwRig.War(config: config ?? Config());
        w.Tfw!.Guardsmen = false;
        w.Tfw.Allies = false;
        for (int id = 2; id <= humans; id++) w.Spawn(id, w.AssignClass(0));
        TfwRig.Deploy(w, TfwLane.B);
        KillOthers(w);
        w.Musou!.Frozen = true;
        for (int k = 0; k < 90; k++) { KillOthers(w); Idle(w); }   // the dying bodies are freed
        return w;
    }

    /// <summary>Every living enemy but <paramref name="keep"/> dies (no credit: attacker 0).</summary>
    public static void KillOthers(World w, params ushort[] keep)
    {
        var all = new MusouEnemy[MusouSim.Capacity];
        int n = w.Musou!.CopyTo(all);
        for (int k = 0; k < n; k++)
            if (all[k].Alive && Array.IndexOf(keep, all[k].Id) < 0) w.Musou.Damage(all[k].Id, 0, 1e7f, Vector3.Zero, out _, cause: MusouDeathKind.World);
    }

    /// <summary>Spawns an Ork of <paramref name="type"/> <paramref name="gap"/> m ahead of player <paramref name="id"/> (<paramref name="side"/> m to its right), facing it.</summary>
    public static ushort OrkAhead(World w, MusouEnemyType type = MusouEnemyType.Boy, float gap = 1.3f, int id = 1, float side = 0f, float health = 100f)
    {
        var p = w.Players[id];
        var forward = new Vector3(-MathF.Sin(p.Yaw), MathF.Cos(p.Yaw), 0f);
        var right = new Vector3(forward.Y, -forward.X, 0f);
        var at = p.Position + forward * gap + right * side;
        ushort e = w.Musou!.Spawn(type, at with { Z = p.Position.Z }, p.Yaw + MathF.PI, health, keepYaw: true);
        Assert.NotEqual(0, e);
        return e;
    }

    /// <summary>Steps (the pool running, every other enemy killed each tick) until the placed Orks <paramref name="orks"/> have left their spawning state, then freezes the pool again.</summary>
    public static void Settle(World w, params ushort[] orks)
    {
        w.Musou!.Frozen = false;
        for (int k = 0; k < 60 && orks.Any(o => w.Musou.TryGet(o, out var e) && e.State == MusouEnemyState.Spawning); k++) { KillOthers(w, orks); Idle(w); }
        w.Musou.Frozen = true;
        foreach (var o in orks) Assert.True(w.Musou.TryGet(o, out var e) && e.State != MusouEnemyState.Spawning, $"Ork {o} still spawning");
    }

    /// <summary>Places player <paramref name="id"/> at <paramref name="at"/> facing <paramref name="yaw"/> (a test teleport).</summary>
    public static void Place(World w, int id, Vector3 at, float yaw = 0f) => w.ExecutionsPlaceForTests(id, at, yaw);

    /// <summary>Player <paramref name="id"/>'s input standing still, facing its current yaw, with <paramref name="buttons"/>.</summary>
    public static PlayerInput Hold(World w, int id, InputButtons buttons = InputButtons.None) =>
        new(0, 0f, 0f, w.Players.TryGetValue(id, out var p) ? p.Yaw : 0f, buttons, 0f, w.Tick);

    /// <summary>One tick: player <paramref name="tapper"/> taps Execute, everyone else stands still.</summary>
    public static void Tap(World w, int tapper = 1) => w.Step(id => Hold(w, id, id == tapper ? InputButtons.Execute : InputButtons.None));

    /// <summary>One tick with nobody pressing anything (each facing its yaw).</summary>
    public static void Idle(World w) => w.Step(id => Hold(w, id));

    /// <summary>Steps idle until player <paramref name="id"/>'s run ends (at most <paramref name="most"/> ticks); the end, or null.</summary>
    public static ExecutionEnded? RunOut(World w, int id = 1, int most = 200)
    {
        for (int k = 0; k < most; k++)
        {
            Idle(w);
            foreach (var e in w.ExecutionsEndedThisTick) if (e.Run.Executor == id) return e;
        }
        return null;
    }

    /// <summary>The refusal of player <paramref name="id"/>'s press in the last step, or null.</summary>
    public static ExecutionRefusal? RefusedLast(World w, int id = 1)
    {
        foreach (var (pid, why) in w.ExecutionsRefusedThisTick) if (pid == id) return why;
        return null;
    }
}
