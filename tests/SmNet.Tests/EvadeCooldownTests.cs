using System.Numerics;
using SmNet;
using SmServer;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// sim/evade-cooldowns (the owner, 2026-11-01: "Add the game data cooldown to tactical roll, devastator/havoc hop, assault marine/raptor short hop ... Currently they are
/// completely spammable"): <c>special_nav_ability/min_time_between_activations</c> (0.8 s, 24 ticks at 30 Hz, on all three MP evades) from the start of one move to the
/// start of the next, not × the 0.7 lock scale. A press inside it does nothing: it ends no reaction or charge, starts no take-off, and only the input buffer's own 0.3 s
/// window (9 ticks) may carry it to the cooldown's end. Synthetic specs shaped like the install's.
/// </summary>
public class EvadeCooldownTests(ITestOutputHelper output)
{
    const int Assault = 0, Tactical = 1, Heavy = 3;
    const int Cooldown = 24;

    static readonly JumpPackSpec Tiered = JumpPackDashTests.Config.JumpPacks[0] with { HoldTiers = true, RechargeRate = 100f };

    /// <summary>The owner's feel (lock scale 0.7, the 0.3 s buffer) with the three moves: the pack class dashes, the Tactical rolls, the heavy hops.</summary>
    static readonly SimConfig Feel = EvadeTests.HopConfig with
    {
        Classes = [EvadeTests.HopConfig.Classes[0] with { Evade = 2 }, .. EvadeTests.HopConfig.Classes.Skip(1)],
        Evades = [.. EvadeTests.HopConfig.Evades, JumpPackDashTests.Dash],
        JumpPacks = [Tiered],
        ActionLockScale = 0.7f, InputBufferWindow = 0.3f,
    };

    static PlayerState Fresh(int cls, SimConfig? c = null) => Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = cls }, c ?? Feel);

    static PlayerState Step(PlayerState s, InputButtons b, float my = 0f, SimConfig? c = null) =>
        Combat.StepPlayer(s, new PlayerInput(0, 0f, my, 0f, b), c ?? Feel, out _);

    /// <summary>The ticks on which a move started (its cooldown was set), for a Space held on the ticks <paramref name="space"/> asks and W held throughout (so a roll or hop ends at its stick branch).</summary>
    static List<int> Starts(int cls, Func<int, bool> space, int ticks = 70)
    {
        var s = Fresh(cls);
        var starts = new List<int>();
        for (int k = 0; k < ticks; k++)
        {
            var before = s;
            s = Step(s, space(k) ? InputButtons.Jump : InputButtons.None, 1f);
            if (s.EvadeLock > before.EvadeLock) starts.Add(k);
        }
        return starts;
    }

    // ---------------------------------------------------------------- the value

    [Theory]
    [InlineData(1f)]
    [InlineData(0.7f)]
    public void The_cooldown_is_the_datas_whatever_the_lock_scale(float scale)
    {
        var c = Feel with { ActionLockScale = scale };
        foreach (var spec in c.Evades) Assert.Equal(Cooldown, Evade.LockTicks(spec, c));
        // The lock scale's other evade gates stay scaled: the roll's stick branch (0.6 → 0.42 s).
        Assert.Equal(scale == 1f ? 0.6f : 0.42f, c.LockTime(EvadeTests.Roll.Forward.MoveAt), 5);
    }

    // ---------------------------------------------------------------- per class: refused inside, works after

    [Theory]
    [InlineData(Tactical)]
    [InlineData(Heavy)]
    public void A_press_before_the_buffers_window_is_refused_and_a_press_after_the_cooldown_works(int cls)
    {
        // Tick 0 starts the move; W ends it at its scaled stick branch (tick 13 the roll, 10 the hop). The press on tick 14 is 10 ticks before the cooldown ends: out of the window.
        Assert.Equal([0], Starts(cls, k => k is 0 or 14));
        Assert.Equal([0, Cooldown], Starts(cls, k => k is 0 or 14 or Cooldown));   // the first free press
        Assert.Equal([0, Cooldown + 3], Starts(cls, k => k is 0 or (Cooldown + 3)));
    }

    [Theory]
    [InlineData(Tactical)]
    [InlineData(Heavy)]
    public void A_press_in_the_buffers_window_acts_on_the_cooldowns_end_not_before(int cls)
    {
        // 9 ticks of window: a press on tick 15 is still held on tick 24, a press on tick 14 is not.
        Assert.Equal([0, Cooldown], Starts(cls, k => k is 0 or 15));
        Assert.Equal([0, Cooldown], Starts(cls, k => k is 0 or 20));
    }

    [Theory]
    [InlineData(Tactical)]
    [InlineData(Heavy)]
    public void Mashing_gives_one_move_per_cooldown_at_the_most(int cls)
    {
        var starts = Starts(cls, k => k % 2 == 0, 200);
        output.WriteLine($"class {cls}: starts at {string.Join(", ", starts)}");
        Assert.True(starts.Count >= 6);
        Assert.All(starts.Zip(starts.Skip(1)), p => Assert.True(p.Second - p.First >= Cooldown, $"{p.First} then {p.Second}"));
        Assert.Equal(Cooldown, starts[1] - starts[0]);
    }

    [Theory]
    [InlineData(Tactical)]
    [InlineData(Heavy)]
    public void Holding_Space_through_the_cooldown_keeps_no_late_move(int cls)
    {
        // Held from before the move ended and from after it: past its window the held press is gone, so nothing starts at the cooldown's end.
        Assert.Equal([0], Starts(cls, k => k == 0 || (k >= 5 && k < 60)));
        Assert.Equal([0], Starts(cls, k => k == 0 || (k >= 14 && k < 60)));
        // Released, a new press works.
        Assert.Equal([0, 62], Starts(cls, k => k == 0 || (k >= 14 && k < 60) || k == 62));
    }

    [Fact]
    public void A_press_held_through_the_roll_itself_still_chains_at_its_end()
    {
        // The hold rule keeps its meaning for an action: no stick, so the roll plays out past the cooldown and the held press acts when it may. (Not the hop:
        // a class without a jump pack keeps no held Jump in the air, an older rule.)
        var s = Fresh(Tactical);
        s = Step(s, InputButtons.Jump);
        s = Step(s, InputButtons.None);
        int started = -1;
        for (int k = 2; k < 45 && started < 0; k++)
        {
            int lockBefore = s.EvadeLock;
            s = Step(s, InputButtons.Jump);
            if (s.EvadeLock > lockBefore) started = k;
        }
        output.WriteLine($"the held press chained on tick {started}");
        Assert.InRange(started, Cooldown, 40);
    }

    // ---------------------------------------------------------------- the jump pack's short hop

    /// <summary>A tap: Jump down on the tick, up on the next.</summary>
    static PlayerState Tap(PlayerState s)
    {
        s = Step(s, InputButtons.Jump);
        return Step(s, InputButtons.None);
    }

    [Fact]
    public void A_dash_tap_inside_the_cooldown_does_nothing_at_all_and_one_after_it_dashes()
    {
        var s = Fresh(Assault) with { EvadeLock = 20 };
        var at = s.Position;
        foreach (var b in new[] { InputButtons.Jump, InputButtons.None, InputButtons.None })
        {
            s = Step(s, b);
            Assert.Equal((JumpPhase.None, 0), (s.Jump, s.EvadeTicks));   // no take-off, so no take-off pose or animation on any client, and no move
            Assert.Equal(at, s.Position);
        }
        for (int k = 0; k < 20; k++) s = Step(s, InputButtons.None);
        Assert.Equal((0, 0), (s.EvadeLock, s.EvadeTicks));               // the window ran out: nothing late
        Assert.Equal(BufferedAction.None, s.Buffered);
        s = Tap(s);
        Assert.Equal(1, s.EvadeTicks);
        Assert.Equal(Cooldown, s.EvadeLock);
    }

    [Fact]
    public void A_dash_tap_in_the_buffers_window_dashes_when_the_cooldown_ends()
    {
        var s = Fresh(Assault) with { EvadeLock = 6 };
        s = Tap(s);
        Assert.Equal((JumpPhase.None, 0, BufferedAction.Jump), (s.Jump, s.EvadeTicks, s.Buffered));
        int waited = 0;
        while (s.EvadeTicks == 0 && waited++ < 12) s = Step(s, InputButtons.None);
        output.WriteLine($"dashed {waited} ticks after the tap's release");
        Assert.Equal(1, s.EvadeTicks);
        Assert.InRange(waited, 4, 7);
    }

    [Fact]
    public void Mashing_the_dash_gives_one_dash_per_cooldown_at_the_most()
    {
        var starts = Starts(Assault, k => k % 8 == 0, 300);
        output.WriteLine($"dash starts at {string.Join(", ", starts)}");
        Assert.True(starts.Count >= 5);
        Assert.All(starts.Zip(starts.Skip(1)), p => Assert.True(p.Second - p.First >= Cooldown, $"{p.First} then {p.Second}"));
    }

    [Fact]
    public void A_hold_inside_the_cooldown_is_still_a_jump_only_its_take_off_waits_for_the_decision()
    {
        var s = Fresh(Assault) with { EvadeLock = 20 };
        JumpPhase seen = JumpPhase.None;
        int tookOff = -1;
        for (int k = 0; k < 16; k++)
        {
            s = Step(s, InputButtons.Jump);
            if (tookOff < 0 && s.Jump != JumpPhase.None) { tookOff = k; seen = s.Jump; }
        }
        output.WriteLine($"take-off on tick {tookOff}");
        Assert.Equal(JumpPhase.TakeOff, seen);
        Assert.Equal(6, tookOff);                  // the tick after the dash's decision tick (5): the press is known to be no tap, and replays as the long press
        Assert.Equal(0, s.EvadeTicks);
        Assert.False(s.Grounded);                  // it flew
    }

    [Fact]
    public void A_jump_after_the_cooldown_takes_off_at_once_as_before()
    {
        var s = Step(Fresh(Assault), InputButtons.Jump);
        Assert.Equal(JumpPhase.TakeOff, s.Jump);
    }

    // ---------------------------------------------------------------- what a refused press must not do

    static readonly ReactionEntry Stun = new(0f, ReactionKind.Stun, Backward: 2f, Duration: 0.2f);

    [Theory]
    [InlineData(Tactical)]
    [InlineData(Heavy)]
    [InlineData(Assault)]
    public void A_press_in_the_cooldown_does_not_break_a_stun(int cls)
    {
        bool Breaks(int lockTicks)
        {
            var s = Reaction.Start(Fresh(cls), Feel, Stun, ReactionDirection.Front, Vector2.UnitY) with { EvadeLock = lockTicks };
            for (int k = 1; k < 8; k++) s = Step(s, InputButtons.None);   // the scaled BRANCH_Evade opens after 7 ticks
            Assert.Equal(ReactionKind.Stun, s.Reaction);
            s = Step(s, InputButtons.Jump);
            return s.Reaction == ReactionKind.None;
        }
        Assert.True(Breaks(0));
        Assert.False(Breaks(30));
    }

    [Fact]
    public void A_press_in_the_cooldown_does_not_end_a_charge()
    {
        var c = Feel with { Classes = [.. Feel.Classes.Select((k, i) => i == Tactical ? k with { MeleeRush = RushTests.Spec } : k)] };
        PlayerState Charging(int lockTicks) => Fresh(Tactical, c) with { Rush = RushPhase.Rush, RushTicks = 20, EvadeLock = lockTicks };
        var free = Step(Charging(0), InputButtons.Jump, c: c);
        Assert.Equal((RushPhase.None, 1), (free.Rush, free.EvadeTicks));   // its Jump branch: ends it and rolls
        var cooling = Step(Charging(30), InputButtons.Jump, c: c);
        Assert.Equal((RushPhase.Rush, 0), (cooling.Rush, cooling.EvadeTicks));
    }

    [Fact]
    public void Only_a_hop_that_starts_cancels_the_stomp()
    {
        var stomp = new MeleeStep(1.6666666f, 0.767f, 1.0f, 1.4f, 0.9f, 1.333f, 0f, 0f, 3.5f) { InPlace = true, EvadeAt = 0.5f };
        var c = Feel with { Classes = [.. Feel.Classes.Select((k, i) => i == Heavy ? k with { MeleeHeavy = [stomp] } : k)] };
        PlayerState Stomping(int lockTicks)
        {
            var s = Step(Fresh(Heavy, c), InputButtons.MeleeHeavy, c: c);
            for (int k = 0; k < 16; k++) s = Step(s, InputButtons.None, c: c);
            Assert.True(s.MeleeStep > 0);
            return s with { EvadeLock = lockTicks };
        }
        var free = Step(Stomping(0), InputButtons.Jump, c: c);
        Assert.Equal((0, 1), (free.MeleeStep, free.EvadeTicks));           // the hop ends it
        var cooling = Step(Stomping(30), InputButtons.Jump, c: c);
        Assert.True(cooling.MeleeStep > 0);                                // the refused hop leaves the stomp running
        Assert.Equal(0, cooling.EvadeTicks);
    }

    // ---------------------------------------------------------------- respawn

    [Theory]
    [InlineData(Tactical)]
    [InlineData(Heavy)]
    [InlineData(Assault)]
    public void Respawning_clears_the_cooldown_and_the_next_press_acts(int cls)
    {
        var used = Fresh(cls) with { EvadeLock = 20, Buffered = BufferedAction.Jump, BufferTicks = 5 };
        var back = Combat.Respawned(used, Feel);
        Assert.Equal((0, BufferedAction.None), (back.EvadeLock, back.Buffered));
        back = cls == Assault ? Tap(back) : Step(back, InputButtons.Jump);
        Assert.Equal(1, back.EvadeTicks);
    }

    // ---------------------------------------------------------------- the client agrees

    [Theory]
    [InlineData(811)]
    [InlineData(812)]
    public void Prediction_matches_the_server_through_mashed_presses_under_150ms_rtt_and_5pct_loss(int seed)
    {
        var config = Feel with { RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f };
        var link = new LinkConditions(TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(10), 0.05);
        var h = new Harness(link, seed, 0, config, new ServerOptions { PeerTimeout = TimeSpan.FromMinutes(5) });
        int[] classes = [Tactical, Heavy, Assault];
        for (int i = 0; i < classes.Length; i++) h.AddClient(nonce: (uint)(900 + i), options: new ClientOptions { RequestedClass = (byte)classes[i] });
        var truth = new SortedDictionary<uint, Dictionary<int, PlayerState>>();
        var predicted = classes.Select(_ => new Dictionary<uint, PlayerState>()).ToArray();
        // A one-tick press every 7 ticks (most inside the cooldown), the stick walking the four ways so the moves stay near the start.
        PlayerInput Input(int i, uint t)
        {
            var stick = ((t + (uint)i * 11) / 60 % 4) switch { 0 => new Vector2(0, 1), 1 => new Vector2(1, 0), 2 => new Vector2(0, -1), _ => new Vector2(-1, 0) };
            return new PlayerInput(t, stick.X, stick.Y, 0.3f * i, (t + (uint)i * 3) % 7 == 0 ? InputButtons.Jump : InputButtons.None, 0f);
        }
        h.Run(TimeSpan.FromSeconds(30), Input, () =>
        {
            truth[h.Server.World.Tick] = new Dictionary<int, PlayerState>(h.Server.World.Players);
            for (int i = 0; i < classes.Length; i++)
                if (h.Clients[i].Status == ClientStatus.Connected) predicted[i][h.Clients[i].Tick] = h.Clients[i].Predicted;
        });
        for (int i = 0; i < classes.Length; i++)
        {
            int id = h.Clients[i].PlayerId, compared = 0;
            var mismatches = new List<string>();
            uint from = truth.Keys.First() + 90;
            foreach (var (tick, pred) in predicted[i])
            {
                if (tick < from || !truth.TryGetValue(tick, out var world) || !world.TryGetValue(id, out var real)) continue;
                compared++;
                if (pred != real) mismatches.Add($"tick {tick}: predicted {pred}; server {real}");
            }
            var mine = truth.Where(kv => kv.Value.ContainsKey(id)).Select(kv => (kv.Key, State: kv.Value[id])).ToList();
            var starts = mine.Zip(mine.Skip(1)).Where(p => p.Second.State.EvadeLock > p.First.State.EvadeLock).Select(p => p.Second.Key).ToList();
            output.WriteLine($"class {classes[i]}: compared {compared}, {mismatches.Count} mismatches; {starts.Count} starts");
            foreach (var m in mismatches.Take(3)) output.WriteLine(m);
            Assert.True(compared > 300);
            Assert.True(starts.Count >= 10, $"class {classes[i]}: {starts.Count} starts");
            Assert.All(starts.Zip(starts.Skip(1)), p => Assert.True(p.Second - p.First >= Cooldown, $"{p.First} then {p.Second}"));
            Assert.Empty(mismatches);
        }
    }
}
