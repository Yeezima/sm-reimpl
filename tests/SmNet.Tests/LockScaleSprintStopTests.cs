using System.Numerics;
using SmContent;
using SmNet;
using SmServer;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// feel/locks-sprint-stop (protocol 36; the owner's requests of 2026-10-29): <see cref="SimConfig.ActionLockScale"/> (0.7, an estimate for review) on every
/// move, jump and evade gate of an action and nothing else, a melee attack's <c>BRANCH_Evade</c> (<see cref="MeleeStep.EvadeAt"/>), and the sprint stop
/// (<see cref="SprintStop"/>). Synthetic specs shaped like the install's; no original bytes.
/// </summary>
public class LockScaleSprintStopTests(ITestOutputHelper output)
{
    const float Dt = 1f / 30f;
    const float Scale = GameplayTuning.OwnerLockScale;
    static float T(float ticks) => ticks / 30f;
    const int Tactical = 0, Assault = 1;

    /// <summary>Light attack: hit at tick 7, combo window ticks 13..19, fire from 16, moves from 22 (scaled: 16), evade branch from 14 (scaled: 10), over at 31.</summary>
    static readonly MeleeStep Light = new MeleeStep(T(30.5f), T(6.5f), T(12.5f), T(18.5f), T(15.5f), T(21.5f), 10f, 5f, 3.5f) { EvadeAt = T(13.5f) };
    /// <summary>A heavy strike whose evade branch opens just after its hit (branch 11.5, hit 9.5): scaled (8.05), it still opens only once the hit has landed.</summary>
    static readonly MeleeStep Early = new MeleeStep(T(40.5f), T(9.5f), 0f, 0f, T(25.5f), T(29.5f), 15f, 5f, 3.5f) { EvadeAt = T(11.5f) };
    static readonly EvadeSpec Roll = EvadeTests.Roll with
    {
        Forward = EvadeTests.Roll.Forward with { NextAt = 0.9f }, Back = EvadeTests.Roll.Back with { NextAt = 0.9f },
        Left = EvadeTests.Roll.Left with { NextAt = 0.9f }, Right = EvadeTests.Roll.Right with { NextAt = 0.9f },
    };
    static readonly WeaponSpec Rifle = new("rifle", WeaponSlot.Ranged, TriggerKind.FullAuto, 1f, 0.1f, 30, 270, 1.0f, 250f, 45f, 70f, 0.8f, 2.5f, 1f, 0.5f, 0.5f);

    /// <summary>The data's times (scale 1), no sprint stop; speeds shaped like the install's (run 6.5, sprint 10).</summary>
    static readonly SimConfig Data = new()
    {
        Weapons = [Rifle], Thrown = [GrenadeTests.Frag], Evades = [Roll], JumpPacks = [JumpPackTests.Pack],
        Classes =
        [
            new ClassLoadout("tactical", 0, -1, [Light]) { MeleeHeavy = [Early], MeleeRush = RushTests.Spec, Evade = 0, EquipmentChoices = [0] },
            new ClassLoadout("assault", 0, -1, [Light]) { MeleeHeavy = [Early], JumpPack = 0, EquipmentChoices = [0] },
        ],
        RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f,
        HealthMax = 1000f, ShieldMax = 100f, HealthRegenDelay = 1e6f, ShieldRegenDelay = 1e6f,
    };
    /// <summary>The owner's feel: scale 0.7 and the stop over the install's 0.833 s.</summary>
    static readonly SimConfig Feel = Data with { ActionLockScale = Scale, SprintStopTime = SprintStopTuning.FallbackTime };

    static PlayerState Fresh(SimConfig c, int cls = Tactical) => Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = cls }, c);

    static SelfEvents Step(ref PlayerState s, SimConfig c, InputButtons b, float mx = 0f, float my = 0f, float yaw = 0f)
    {
        s = Combat.StepPlayer(s, new PlayerInput(0, mx, my, yaw, b), c, out var e);
        return e;
    }

    /// <summary>The first n in 1..<paramref name="max"/> for which <paramref name="works"/>(n) holds, or -1.</summary>
    static int First(Func<int, bool> works, int max = 80)
    {
        for (int n = 1; n <= max; n++) if (works(n)) return n;
        return -1;
    }

    // ---------------------------------------------------------------- the scale and the hash

    [Fact]
    public void LockTime_scales_only_with_a_usable_factor_and_the_config_hash_covers_the_scale_the_stop_and_the_evade_branch()
    {
        Assert.Equal(0.6f, Data.LockTime(0.6f));
        Assert.Equal(0.42f, Feel.LockTime(0.6f), 5);
        foreach (float bad in new[] { 0f, -1f, float.NaN, float.PositiveInfinity }) Assert.Equal(0.6f, (Data with { ActionLockScale = bad }).LockTime(0.6f));
        Assert.Equal(float.PositiveInfinity, Feel.LockTime(float.PositiveInfinity));
        var plain = new SimConfig();
        var seen = new HashSet<ulong> { plain.Hash() };
        Assert.Equal(plain.Hash(), (plain with { ActionLockScale = 1f, SprintStopTime = 0f }).Hash());   // older hashes unchanged
        Assert.True(seen.Add((plain with { ActionLockScale = 0.7f }).Hash()));
        Assert.True(seen.Add((plain with { ActionLockScale = 0.8f }).Hash()));
        Assert.True(seen.Add((plain with { SprintStopTime = 0.8333f }).Hash()));
        Assert.True(seen.Add((plain with { SprintStopTime = 0.5f }).Hash()));
        var without = Data with { Classes = [Data.Classes[0] with { Melee = [Light with { EvadeAt = float.PositiveInfinity }] }] };
        var with = Data with { Classes = [Data.Classes[0]] };
        Assert.NotEqual(without.Hash(), with.Hash());
        Assert.Equal(without.Hash(), (Data with { Classes = [Data.Classes[0] with { Melee = [new MeleeStep(T(30.5f), T(6.5f), T(12.5f), T(18.5f), T(15.5f), T(21.5f), 10f, 5f, 3.5f)] }] }).Hash());
    }

    // ---------------------------------------------------------------- melee: MoveAt and the evade branch

    /// <summary>A light attack pressed at tick 0, the stick pushed from the press on (the heavy strike: from the tick after it): the first tick (after the press) the body moves.</summary>
    static int FirstMoveAfterSwing(SimConfig c, bool heavy = false)
    {
        var s = Fresh(c);
        Step(ref s, c, heavy ? InputButtons.MeleeHeavy : InputButtons.Melee, my: heavy ? 0f : 1f);   // protocol 39: F with W is the charge, so the stun strike is pressed without it
        Assert.True(s.MeleeStep > 0);
        for (int k = 1; k <= 60; k++)
        {
            Step(ref s, c, InputButtons.None, my: 1f);
            if (s.Velocity.Y > 0f) return k;
        }
        return -1;
    }

    [Fact]
    public void A_melee_attacks_move_gate_is_its_MoveAt_times_the_scale()
    {
        Assert.Equal(22, FirstMoveAfterSwing(Data));                 // MoveAt tick 21.5
        Assert.Equal(16, FirstMoveAfterSwing(Feel));                 // 21.5 × 0.7 = 15.05
        Assert.Equal(30, FirstMoveAfterSwing(Data, heavy: true));    // the stun strike: tick 29.5
        Assert.Equal(21, FirstMoveAfterSwing(Feel, heavy: true));    // 20.65
        Assert.Equal(Light.MoveAt * Scale, Combat.MoveAtOf(Light, Feel), 5);
    }

    /// <summary>An attack pressed at tick 0, Jump pressed at tick <paramref name="n"/>: whether that tick ended the attack and evaded (or took off).</summary>
    static bool JumpOutOfSwing(SimConfig c, int n, int cls = Tactical, bool heavy = false)
    {
        var s = Fresh(c, cls);
        Step(ref s, c, heavy ? InputButtons.MeleeHeavy : InputButtons.Melee);
        for (int k = 1; k < n; k++) Step(ref s, c, InputButtons.None);
        if (s.MeleeStep == 0) return false;   // the attack is over: not a branch
        Step(ref s, c, InputButtons.Jump);
        bool left = s.MeleeStep == 0 && (s.EvadeTicks == 1 || s.Jump == JumpPhase.TakeOff);
        if (!left) Assert.True(s.MeleeStep > 0, $"tick {n}: the attack ended without an evade");   // nothing took the press: the attack goes on
        return left;
    }

    [Fact]
    public void A_Jump_press_from_the_attacks_scaled_evade_branch_ends_it_and_rolls_or_takes_off_never_before_its_hit()
    {
        Assert.Equal(15, First(n => JumpOutOfSwing(Data, n)));                         // BRANCH_Evade tick 13.5: played 14 ticks before the press tick
        Assert.Equal(11, First(n => JumpOutOfSwing(Feel, n)));                         // 13.5 × 0.7 = 9.45
        Assert.Equal(11, First(n => JumpOutOfSwing(Feel, n, Assault)));                // the jump pack's take-off alike
        // The branch opens at 11.5 (scaled 8.05) and the hit lands at 9.5: the scale does not open it before the hit.
        Assert.Equal(13, First(n => JumpOutOfSwing(Data, n, heavy: true)));
        Assert.Equal(11, First(n => JumpOutOfSwing(Feel, n, heavy: true)));
        Assert.Equal(T(9.5f), Combat.MeleeGateAt(Early, Feel));
        // A branch the data opens before the hit (5.5, the hit at 9.5): the data's time, not scaled earlier still.
        SimConfig Before(SimConfig c) => c with { Classes = [c.Classes[0] with { MeleeHeavy = [Early with { EvadeAt = T(5.5f) }] }, c.Classes[1]] };
        Assert.Equal(7, First(n => JumpOutOfSwing(Before(Data), n, heavy: true)));
        Assert.Equal(7, First(n => JumpOutOfSwing(Before(Feel), n, heavy: true)));
        // Without a branch (the default): never during the attack.
        var none = Feel with { Classes = [Feel.Classes[0] with { Melee = [Light with { EvadeAt = float.PositiveInfinity }] }] };
        Assert.Equal(-1, First(n => JumpOutOfSwing(none, n), 30));
        // The hit of the attack lands before the jump out (tick 7) and only once.
        var s = Fresh(Feel);
        int hits = Step(ref s, Feel, InputButtons.Melee).MeleeHitStep > 0 ? 1 : 0;
        for (int k = 1; k < 11; k++) hits += Step(ref s, Feel, InputButtons.None).MeleeHitStep > 0 ? 1 : 0;
        hits += Step(ref s, Feel, InputButtons.Jump).MeleeHitStep > 0 ? 1 : 0;
        Assert.Equal((1, 0, 1), (hits, s.MeleeStep, s.EvadeTicks));
    }

    // ---------------------------------------------------------------- evades

    [Fact]
    public void An_evades_stick_and_next_evade_branches_and_its_spacing_are_scaled_its_other_branches_are_not()
    {
        Assert.Equal(24, Evade.LockTicks(Roll, Data));
        Assert.Equal(17, Evade.LockTicks(Roll, Feel));   // 0.56 s
        int Rolling(SimConfig c, float mx, float my, InputButtons held = InputButtons.None)
        {
            var s = Fresh(c);
            Step(ref s, c, InputButtons.Jump, mx, my);
            int n = 1;
            while (s.EvadeTicks > 0 && n < 60) { Step(ref s, c, held, mx, my); if (s.EvadeTicks > 0) n++; }
            return n;
        }
        Assert.Equal(18, Rolling(Data, 0f, 1f));    // MoveAt 0.6 s: the stick ends it after 18 ticks
        Assert.Equal(13, Rolling(Feel, 0f, 1f));    // 0.42 s
        Assert.Equal(18, Rolling(Data, 0f, 0f, InputButtons.Fire));   // RangedAt 0.6 s: the data's in both
        Assert.Equal(18, Rolling(Feel, 0f, 0f, InputButtons.Fire));
        bool Chains(SimConfig c, int n)
        {
            var s = Fresh(c);
            Step(ref s, c, InputButtons.Jump);
            for (int k = 1; k < n; k++) Step(ref s, c, InputButtons.None);
            Step(ref s, c, InputButtons.Jump);
            return s.EvadeTicks == 1;
        }
        Assert.Equal(27, First(n => Chains(Data, n)));   // NextAt 0.9 s: 27 ticks played before the press tick
        Assert.Equal(19, First(n => Chains(Feel, n)));   // 0.63 s: 18.9 (the 17-tick spacing is over by then)
    }

    [Fact]
    public void A_dash_landings_jump_branch_is_scaled()
    {
        var landing = JumpPackDashTests.Dash.Forward.Landing! with { JumpAt = 0.3f };
        var dash = JumpPackDashTests.Dash with { Forward = JumpPackDashTests.Dash.Forward with { Landing = landing } };
        var data = JumpPackDashTests.Config with { Evades = [dash] };
        var feel = data with { ActionLockScale = Scale };
        var s = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = 0 }, data)
            with { EvadeTicks = 1, EvadeLanded = true, EvadeDirection = EvadeDirection.Forward };
        int Opens(SimConfig c) => First(n => Evade.JumpAllowed(s with { EvadeTicks = n }, dash, c));
        Assert.Equal(10, Opens(data));   // 0.3 s: 9 ticks of the landing played (its tick 1 is time 0)
        Assert.Equal(8, Opens(feel));    // 0.21 s: 7 ticks (6.3)
    }

    // ---------------------------------------------------------------- reactions, the charge, the throw

    [Fact]
    public void A_reactions_stick_and_Jump_branches_are_scaled_only_where_the_data_has_them_never_cut_its_knockback_and_its_length_stays_the_datas()
    {
        int EndsOn(SimConfig c, ReactionEntry entry, InputButtons b, float my)
        {
            var s = Reaction.Start(Fresh(c), c, entry, ReactionDirection.Front, Vector2.UnitY);
            for (int n = 1; n <= 60; n++)
            {
                Step(ref s, c, b, my: my);
                if (s.Reaction == ReactionKind.None) return n;
            }
            return -1;
        }
        // A Jump press (an edge) at tick n: whether it broke the reaction then.
        bool Breaks(SimConfig c, ReactionEntry entry, int n)
        {
            var s = Reaction.Start(Fresh(c), c, entry, ReactionDirection.Front, Vector2.UnitY);
            for (int k = 1; k < n; k++) Step(ref s, c, InputButtons.None);
            if (s.Reaction == ReactionKind.None) return false;
            Step(ref s, c, InputButtons.Jump);
            return s.Reaction == ReactionKind.None;
        }
        var stun = new ReactionEntry(0f, ReactionKind.Stun, Backward: 2f, Duration: 0.2f);       // F clip: end 0.533, evade 0.333; 6 knockback ticks
        // The review of 2026-10-29: a clip without a stick branch (the stun's falls back to its end) keeps its full length with a key held.
        Assert.Equal(17, EndsOn(Data, stun, InputButtons.None, 1f));    // no run branch: the end (16 ticks played)
        Assert.Equal(17, EndsOn(Feel, stun, InputButtons.None, 1f));    // not scaled
        // Every reaction's length with no keys, and the branch-less stagger with keys held, are the data's.
        var stagger = new ReactionEntry(0f, ReactionKind.Stagger, Backward: 3f, Duration: 0.3f);
        foreach (var entry in new[] { stun, stagger, new ReactionEntry(0f, ReactionKind.Interrupt) })
        {
            Assert.Equal(EndsOn(Data, entry, InputButtons.None, 0f), EndsOn(Feel, entry, InputButtons.None, 0f));
            Assert.Equal(EndsOn(Data, entry, InputButtons.None, 1f), EndsOn(Feel, entry, InputButtons.None, 1f));
        }
        Assert.Equal(-1, First(n => Breaks(Feel, stagger, n) != Breaks(Data, stagger, n), 40));   // the stagger has no Jump branch either
        // A real branch is scaled: the stun's BRANCH_Evade (0.333 s) and an interrupt's BRANCH_Run before its end.
        Assert.Equal(11, First(n => Breaks(Data, stun, n)));            // the stun break at 0.333 s: 10 ticks played
        Assert.Equal(8, First(n => Breaks(Feel, stun, n)));             // 0.233 s (the knockback's 0.2 s is over): 7 ticks
        Assert.Equal(10, First(n => Breaks(Feel, stun with { Duration = 0.3f }, n)));   // a 0.3 s knockback: not before its 9 ticks
        var clip = new ReactionClip("itrpt", 1f, 0.9f, 0.6f, 0.4f, 0.7f, 0.3f);
        SimConfig Real(SimConfig c) => c with { Reactions = [new ReactionSpec(ReactionKind.Stun, false, clip, clip, clip, clip)] };   // sim/combat-2: a holding kind (a light one ends on any input)
        var interrupt = new ReactionEntry(0f, ReactionKind.Stun);
        Assert.Equal(19, EndsOn(Real(Data), interrupt, InputButtons.None, 1f));   // BRANCH_Run 0.6 s: 18 ticks played
        Assert.Equal(14, EndsOn(Real(Feel), interrupt, InputButtons.None, 1f));   // 0.42 s: 13 ticks
        Assert.Equal(28, EndsOn(Real(Feel), interrupt, InputButtons.None, 0f));   // no keys: its end, 0.9 s
        var slowPush = stun with { Duration = 0.3f };
        Assert.Equal(0.3f, Reaction.EscapeAt(Reaction.Start(Fresh(Feel), Feel, slowPush, ReactionDirection.Front, Vector2.UnitY), 0.3333f, 0.5333f, Feel), 4);   // 0.233 → the push's 0.3 s
        Assert.Equal(0.2333f, Reaction.EscapeAt(Reaction.Start(Fresh(Feel), Feel, stun, ReactionDirection.Front, Vector2.UnitY), 0.3333f, 0.5333f, Feel), 3);
        Assert.Equal(0.3333f, Reaction.EscapeAt(Reaction.Start(Fresh(Data), Data, stun, ReactionDirection.Front, Vector2.UnitY), 0.3333f, 0.5333f, Data));
        Assert.Equal(0.5333f, Reaction.EscapeAt(Reaction.Start(Fresh(Feel), Feel, stun, ReactionDirection.Front, Vector2.UnitY), 0.5333f, 0.5333f, Feel));   // no branch
    }

    [Fact]
    public void The_bull_rushs_stick_and_Jump_branches_are_scaled_and_its_end_is_not()
    {
        bool Ended(SimConfig c, RushPhase phase, int played, InputButtons b, float my, float mx = 0f)
        {
            var s = Fresh(c) with { Rush = phase, RushTicks = played };
            Step(ref s, c, b, mx, my);
            return s.Rush == RushPhase.None;
        }
        // The rush: MoveAt 0.6 (18 ticks), EvadeAt 0.5 (15), end 1.0.
        // sim/combat-2 (item 31): W does not end the bull rush any more (it ran to the stick branch with W held); a sideways stick still does.
        Assert.Equal((18, 13), (First(n => Ended(Data, RushPhase.Rush, n, InputButtons.None, 0f, 1f)), First(n => Ended(Feel, RushPhase.Rush, n, InputButtons.None, 0f, 1f))));
        Assert.Equal((30, 30), (First(n => Ended(Data, RushPhase.Rush, n, InputButtons.None, 1f)), First(n => Ended(Feel, RushPhase.Rush, n, InputButtons.None, 1f))));
        Assert.Equal((15, 11), (First(n => Ended(Data, RushPhase.Rush, n, InputButtons.Jump, 0f)), First(n => Ended(Feel, RushPhase.Rush, n, InputButtons.Jump, 0f))));
        Assert.Equal((30, 30), (First(n => Ended(Data, RushPhase.Rush, n, InputButtons.None, 0f)), First(n => Ended(Feel, RushPhase.Rush, n, InputButtons.None, 0f))));
        // The collision stop: MoveAt 0.9 (27), EvadeAt 0.6 (18).
        Assert.Equal((27, 19), (First(n => Ended(Data, RushPhase.Collision, n, InputButtons.None, 1f)), First(n => Ended(Feel, RushPhase.Collision, n, InputButtons.None, 1f))));
        Assert.Equal((18, 13), (First(n => Ended(Data, RushPhase.Collision, n, InputButtons.Jump, 0f)), First(n => Ended(Feel, RushPhase.Collision, n, InputButtons.Jump, 0f))));
    }

    [Fact]
    public void A_throws_evade_gate_is_scaled_never_before_the_release_and_the_evade_ends_the_throw()
    {
        bool RollsOutOfThrow(SimConfig c, int n)
        {
            var s = Fresh(c);
            Step(ref s, c, InputButtons.Grenade);
            Assert.True(s.Throwing);
            for (int k = 1; k < n; k++) Step(ref s, c, InputButtons.None);
            Step(ref s, c, InputButtons.Jump);
            if (s.EvadeTicks != 1) return false;
            Assert.False(s.Throwing);
            Assert.Equal(GrenadeTests.Frag.SpawnCount - 1, s.Grenades);   // the item left before
            return true;
        }
        Assert.Equal(21, First(n => RollsOutOfThrow(Data, n)));   // the throw ends at 0.667 s (tick 20)
        Assert.Equal(15, First(n => RollsOutOfThrow(Feel, n)));   // 0.467 s: 14 ticks played
        var early = Feel with { ActionLockScale = 0.1f };
        Assert.Equal(7, First(n => RollsOutOfThrow(early, n)));   // 0.067 s is before the release at 0.2 s: the release (6 ticks) first
    }

    // ---------------------------------------------------------------- the sprint stop

    /// <summary>Sprints forward 60 ticks (full speed), then runs <paramref name="after"/> on each later tick; the states from the release on.</summary>
    static List<PlayerState> SprintThen(SimConfig c, Func<int, (InputButtons B, float Mx, float My, float Yaw)> after, int ticks = 45, int cls = Tactical, bool sprint = true)
    {
        var s = Fresh(c, cls);
        for (int k = 0; k < 60; k++) Step(ref s, c, sprint ? InputButtons.Sprint : InputButtons.None, my: 1f);
        Assert.Equal(sprint ? c.SprintSpeed : c.RunSpeed, s.Velocity.Y, 3);
        var states = new List<PlayerState> { s };
        for (int k = 1; k <= ticks; k++) { var (b, mx, my, yaw) = after(k); Step(ref s, c, b, mx, my, yaw); states.Add(s); }
        return states;
    }

    static float Speed(PlayerState s) => new Vector2(s.Velocity.X, s.Velocity.Y).Length();

    [Fact]
    public void Letting_go_of_every_movement_key_after_a_sprint_slows_the_body_over_the_stop_time_along_the_stop_curve()
    {
        var stop = SprintThen(Feel, _ => (InputButtons.None, 0f, 0f, 0f));
        int ticks = stop.FindIndex(1, s => Speed(s) == 0f);
        float distance = stop[ticks].Position.Y - stop[0].Position.Y;
        output.WriteLine($"stop: {ticks} ticks ({ticks * Dt:0.000} s), {distance:0.00} m (continuous {SprintStop.Distance(10f, Feel):0.00} m over {SprintStop.Duration(10f, Feel):0.000} s)");
        Assert.Equal(25, ticks);                                    // 0.833 s from 10 m/s
        Assert.InRange(distance, 5.0f, SprintStop.Distance(10f, Feel));   // 2/3 × 10 × 0.833 = 5.56 m (a tick's worth less: the speed after each tick moves it)
        Assert.All(stop.Skip(1).Take(ticks - 1), s => Assert.True(s.SprintStop && s.Velocity.X == 0f));
        Assert.False(stop[ticks].SprintStop);
        for (int k = 1; k < ticks; k++) Assert.Equal(MathF.Sqrt(MathF.Max(0f, 100f - 4f * k)), Speed(stop[k]), 3);   // v² falls 120 m²/s³: 4 per tick
        // Half the speed is lost only in the last quarter of the time (the clip's shape): at 0.633 s still 4.9 m/s.
        Assert.Equal(4.9f, Speed(stop[19]), 2);
        Assert.Equal(SprintStop.ClipTime(Speed(stop[12]), Feel), 12 * Dt, 3);   // the client's clip time from the speed

        // Without the stop (the data's simulation) the ground rule brakes in about a quarter of a second.
        var data = SprintThen(Data, _ => (InputButtons.None, 0f, 0f, 0f));
        int quick = data.FindIndex(1, s => Speed(s) == 0f);
        output.WriteLine($"without: {quick} ticks, {data[quick].Position.Y - data[0].Position.Y:0.00} m");
        Assert.InRange(quick, 5, 9);
        Assert.All(data, s => Assert.False(s.SprintStop));
        // From a run (not faster than run + 0.25 × (sprint − run): 7.375 m/s with this test's speeds, 6.64 with the owner's): no stop.
        var run = SprintThen(Feel, _ => (InputButtons.None, 0f, 0f, 0f), sprint: false);
        Assert.All(run, s => Assert.False(s.SprintStop));
        Assert.InRange(run.FindIndex(1, s => Speed(s) == 0f), 3, 8);
        // The jump-pack classes on the ground alike.
        var pack = SprintThen(Feel, _ => (InputButtons.None, 0f, 0f, 0f), cls: Assault);
        Assert.Equal(25, pack.FindIndex(1, s => Speed(s) == 0f));
    }

    [Fact]
    public void The_stop_keeps_its_direction_while_the_view_turns_and_a_throw_or_reload_lets_it_run()
    {
        var turned = SprintThen(Feel, k => (k == 3 ? InputButtons.Grenade : k == 8 ? InputButtons.Reload : InputButtons.None, 0f, 0f, 0.1f * k));
        Assert.Equal(25, turned.FindIndex(1, s => Speed(s) == 0f));
        Assert.All(turned.Skip(1).Take(24), s => Assert.True(s.SprintStop && MathF.Abs(s.Velocity.X) < 1e-4f));
        Assert.Equal(0.1f * 10, turned[10].Yaw, 4);   // the body turns with the view
        Assert.True(turned[5].Throwing);
    }

    [Theory]
    [InlineData(InputButtons.None, 0f, 1f)]    // W
    [InlineData(InputButtons.None, -1f, 0f)]   // A
    [InlineData(InputButtons.Fire, 0f, 0f)]
    [InlineData(InputButtons.Aim, 0f, 0f)]
    [InlineData(InputButtons.Jump, 0f, 0f)]    // the roll
    public void A_movement_key_Jump_Fire_or_Aim_cancels_the_stop_into_the_normal_movement(InputButtons b, float mx, float my)
    {
        var states = SprintThen(Feel, k => k < 6 ? (InputButtons.None, 0f, 0f, 0f) : (b, mx, my, 0f), ticks: 12);
        Assert.True(states[5].SprintStop);
        Assert.All(states.Skip(6), s => Assert.False(s.SprintStop));
        var normal = Combat.StepPlayer(states[5] with { SprintStop = false }, new PlayerInput(0, mx, my, 0f, b), Feel, out _);
        Assert.Equal(normal, states[6]);   // exactly the tick of a body moving at that speed without a stop
        if (b == InputButtons.Jump) Assert.Equal(1, states[6].EvadeTicks);
        // Let go again after a cancel: a new stop only from above the sprint threshold.
        var again = SprintThen(Feel, k => k == 3 ? (InputButtons.None, 0f, 1f, 0f) : (InputButtons.None, 0f, 0f, 0f), ticks: 8);
        Assert.True(again[4].SprintStop == Speed(again[3]) > SprintStop.Threshold(Feel));
    }

    [Fact]
    public void A_stop_begun_above_sprint_speed_starts_from_sprint_speed()
    {
        // A body faster than sprinting (a roll or hop ended by a branch, a knockback's tail): the stop never lasts longer or slides further than from a sprint.
        var s = Fresh(Feel) with { Velocity = new Vector3(0f, 14f, 0f) };
        var start = s.Position;
        int ticks = 0;
        do { Step(ref s, Feel, InputButtons.None); ticks++; } while (Speed(s) > 0f && ticks < 60);
        Assert.Equal(MathF.Sqrt(100f - 4f), SprintThen(Feel, _ => (InputButtons.None, 0f, 0f, 0f), ticks: 1)[1].Velocity.Y, 3);
        Assert.Equal(25, ticks);   // as from 10 m/s
        Assert.True(s.Position.Y - start.Y <= SprintStop.Distance(Feel.SprintSpeed, Feel));
        Assert.Equal(new Vector2(0f, SprintStop.Next(10f, Feel)), SprintStop.Slowed(new Vector2(0f, 14f), Feel));
    }

    [Fact]
    public void A_charge_may_start_from_a_slide()
    {
        // Allowed (ours): W + Sprint + the heavy press while the body still slides faster than the charge's gate cancels the stop and charges.
        var states = SprintThen(Feel, k => k < 3 ? (InputButtons.None, 0f, 0f, 0f) : (InputButtons.Sprint | InputButtons.MeleeHeavy, 0f, 1f, 0f), ticks: 3);
        Assert.True(states[2].SprintStop);
        Assert.Equal((RushPhase.Rush, false), (states[3].Rush, states[3].SprintStop));
    }

    [Fact]
    public void A_reaction_death_or_respawn_clears_the_stop()
    {
        var s = SprintThen(Feel, _ => (InputButtons.None, 0f, 0f, 0f), ticks: 4)[^1];
        Assert.True(s.SprintStop);
        Assert.False(Reaction.Start(s, Feel, new ReactionEntry(0f, ReactionKind.Interrupt), ReactionDirection.Front, Vector2.UnitY).SprintStop);
        Assert.False(Combat.Killed(s, Feel).SprintStop);
        Assert.False(Combat.Respawned(s, Feel).SprintStop);
    }

    // ---------------------------------------------------------------- protocol 36

    [Fact]
    public void Protocol_36_carries_the_stop_in_every_records_melee_byte_and_refuses_it_with_a_time()
    {
        var stopping = SprintThen(Feel, _ => (InputButtons.None, 0f, 0f, 0f), ticks: 4)[^1] with { PlayerId = 3 };
        Assert.True(stopping.SprintStop);
        var other = stopping with { PlayerId = 4 };
        var bytes = Protocol.Write(new Snapshot(9, 1, 1, [stopping, other], Self: 3));
        var back = Assert.IsType<Snapshot>(Protocol.Read(bytes));
        Assert.Equal(stopping, back.Players[0]);
        Assert.Equal(Protocol.Slim(other), back.Players[1]);
        Assert.True(back.Players[1].SprintStop);
        Assert.Equal(48, Protocol.Version);
        // The slim record: melee byte [^30], time [^29..^26] (as in ReactionTests).
        var slim = Protocol.Write(new Snapshot(9, 1, 1, [other]));
        Assert.Equal(64, slim[^30]);
        Assert.Equal(0f, BitConverter.ToSingle(slim, slim.Length - 29));
        var timed = (byte[])slim.Clone();
        BitConverter.GetBytes(0.5f).CopyTo(timed, timed.Length - 29);
        Assert.Null(Protocol.Read(timed));   // the heavy flag without a step and with a time: refused as before
        // An attack, reaction or charge never carries it.
        Assert.False(Protocol.Slim(other with { MeleeStep = 1, MeleeTime = 0.1f }).SprintStop);
    }

    // ---------------------------------------------------------------- prediction

    [Theory]
    [InlineData(361)]
    [InlineData(362)]
    public void Prediction_matches_the_server_through_stops_jumps_out_of_swings_and_scaled_branches_under_150ms_rtt_and_5pct_loss(int seed)
    {
        var config = Feel with { InputBufferWindow = GameplayTuning.OwnerInputBuffer };
        var link = new LinkConditions(TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(10), 0.05);
        var h = new Harness(link, seed, 0, config, new ServerOptions { PeerTimeout = TimeSpan.FromMinutes(5) });
        h.AddClient(nonce: 360, options: new ClientOptions { RequestedClass = Tactical });
        h.AddClient(nonce: 361, options: new ClientOptions { RequestedClass = Assault });
        var truth = new SortedDictionary<uint, Dictionary<int, PlayerState>>();
        var predicted = Enumerable.Range(0, 2).Select(_ => new Dictionary<uint, PlayerState>()).ToArray();
        PlayerInput Input(int i, uint t)
        {
            // A 90-tick cycle: sprint 40 ticks, let go (the stop; some cycles cancelled by W, Fire or Space), then a swing with Space pressed in its
            // evade branch or the stick pushed past its scaled MoveAt, then a roll with the stick in its scaled branch.
            uint cycle = (t + (uint)i * 17) / 90, phase = (t + (uint)i * 17) % 90;
            var buttons = InputButtons.None;
            float mx = 0f, my = 0f;
            if (phase < 40) { buttons |= InputButtons.Sprint; my = 1f; }
            else if (phase == 48 + cycle % 4) { switch (cycle % 4) { case 1: my = 1f; break; case 2: buttons |= InputButtons.Fire; break; case 3: buttons |= InputButtons.Jump; break; } }
            else if (phase == 62) buttons |= cycle % 2 == 0 ? InputButtons.Melee : InputButtons.MeleeHeavy;
            else if (phase == 62 + 9 + cycle % 5) buttons |= cycle % 3 == 0 ? InputButtons.None : InputButtons.Jump;
            else if (phase > 75 && phase < 80) my = cycle % 2 == 0 ? 1f : 0f;
            else if (phase == 82) buttons |= InputButtons.Jump;
            else if (phase > 84) mx = 1f;
            return new PlayerInput(t, mx, my, 0.4f * i + 0.02f * cycle, buttons);
        }
        h.Run(TimeSpan.FromSeconds(30), Input, () =>
        {
            truth[h.Server.World.Tick] = new Dictionary<int, PlayerState>(h.Server.World.Players);
            for (int i = 0; i < 2; i++)
                if (h.Clients[i].Status == ClientStatus.Connected) predicted[i][h.Clients[i].Tick] = h.Clients[i].Predicted;
        });
        for (int i = 0; i < 2; i++)
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
            var mine = truth.Values.Where(w => w.ContainsKey(id)).Select(w => w[id]).ToList();
            int stopping = mine.Count(p => p.SprintStop);
            int outOfSwings = mine.Zip(mine.Skip(1)).Count(p => p.First.MeleeStep > 0 && p.Second.MeleeStep == 0 && (p.Second.EvadeTicks == 1 || p.Second.Jump == JumpPhase.TakeOff));
            output.WriteLine($"client {i}: compared {compared}, {mismatches.Count} mismatches; {stopping} stop ticks, {outOfSwings} jumps out of a swing");
            foreach (var m in mismatches.Take(3)) output.WriteLine("  " + m);
            Assert.True(compared > 300);
            Assert.True(stopping >= 50 && outOfSwings >= 3, $"client {i}: {stopping} stop ticks, {outOfSwings} jumps out of swings");
            Assert.Empty(mismatches);
        }
    }

    // ---------------------------------------------------------------- the install (read-only; skipped without it)

    static readonly Lazy<string?> Game = new(() => GameInstall.TryLocate()?.Dir);

    public sealed class InstallFactAttribute : FactAttribute
    {
        public InstallFactAttribute()
        {
            if (Game.Value is null) Skip = "Space Marine install not found (needs the player's install; never in CI)";
        }
    }

    /// <summary>The before / after table of every scaled gate on the install (spec/behavior.md "Action lock scale"), printed; the loader's values checked.</summary>
    [InstallFact]
    public void The_install_loads_the_owner_feel_and_every_scaled_gate_is_printed()
    {
        var tuning = GameplayTuning.Load(Game.Value!);
        var c = tuning.Config;
        Assert.Equal(Scale, c.ActionLockScale);
        Assert.Equal(0.625f * 4f / 3f, c.SprintStopTime, 3);
        var data = GameplayTuning.Load(Game.Value!, ownerFeel: false).Config;
        Assert.Equal((1f, 0f), (data.ActionLockScale, data.SprintStopTime));
        var stop = tuning.Classes!.SprintStop!;
        output.WriteLine($"sprint stop: {stop.Clip} {stop.Length:0.000} s, BRANCH_Sprint_Finished {stop.FinishedAt:0.000} s, BRANCH_Evade {stop.EvadeAt:0.000} s, root motion {stop.Distance:0.00} m ({stop.DistanceAtFinished:0.00} m by the branch)");
        output.WriteLine($"  simulation: from sprint {c.SprintSpeed:0.00} m/s: {SprintStop.Duration(c.SprintSpeed, c):0.000} s, {SprintStop.Distance(c.SprintSpeed, c):0.00} m; threshold {SprintStop.Threshold(c):0.00} m/s");
        string Row(string what, float before) => $"| {what} | {before:0.000} | {c.LockTime(before):0.000} |";
        output.WriteLine("| gate | before (s) | after (s) |");
        var seen = new HashSet<string>();
        foreach (var k in c.Classes)
            foreach (var combo in k.MeleeChoices)
            {
                if (!seen.Add(combo.Wargear)) continue;
                for (int i = 0; i < combo.Steps.Count; i++)
                {
                    var m = combo.Steps[i];
                    output.WriteLine(Row($"{combo.Wargear} light {i + 1} MoveAt", m.MoveAt));
                    if (float.IsFinite(m.EvadeAt)) output.WriteLine($"| {combo.Wargear} light {i + 1} BRANCH_Evade (new; hit {m.HitTime:0.000}) | {m.EvadeAt:0.000} (was: the attack's end {m.Duration:0.000}) | {Combat.MeleeGateAt(m, c):0.000} |");
                }
                for (int i = 0; i < combo.Heavy.Count; i++)
                {
                    var m = combo.Heavy[i];
                    output.WriteLine(Row($"{combo.Wargear} heavy {i + 1} MoveAt", m.MoveAt));
                    if (float.IsFinite(m.EvadeAt)) output.WriteLine($"| {combo.Wargear} heavy {i + 1} BRANCH_Evade (new; hit {m.HitTime:0.000}) | {m.EvadeAt:0.000} (was: the attack's end {m.Duration:0.000}) | {Combat.MeleeGateAt(m, c):0.000} |");
                }
            }
        foreach (var e in c.Evades)
        {
            output.WriteLine(Row($"{e.Name} spacing (MinInterval)", e.MinInterval) + $" ticks {Evade.LockTicks(e, data)} → {Evade.LockTicks(e, c)}");
            foreach (var d in new[] { EvadeDirection.Forward, EvadeDirection.Back, EvadeDirection.Left, EvadeDirection.Right })
            {
                var m = e.Move(d);
                output.WriteLine(Row($"{e.Name} {d} MoveAt", m.MoveAt));
                if (!e.Dash) output.WriteLine(Row($"{e.Name} {d} NextAt", m.NextAt));
                if (m.Landing is { } l) { output.WriteLine(Row($"{e.Name} {d} landing MoveAt", l.MoveAt)); output.WriteLine(Row($"{e.Name} {d} landing JumpAt", l.JumpAt)); }
            }
        }
        foreach (var r in c.Reactions)
            foreach (var (label, clip) in new[] { ("front", r.Front), ("right", r.Right), ("back", r.Back), ("left", r.Left) })
            {
                var none = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true), c);   // no knockback
                output.WriteLine($"| {r.Kind} {label} RunAt (end {clip.EndAt:0.000}) | {clip.RunAt:0.000} | {Reaction.EscapeAt(none, clip.RunAt, clip.EndAt, c):0.000} |");
                output.WriteLine($"| {r.Kind} {label} EvadeAt | {clip.EvadeAt:0.000} | {Reaction.EscapeAt(none, clip.EvadeAt, clip.EndAt, c):0.000} |");
            }
        if (c.Classes.Select(k => k.MeleeRush).FirstOrDefault(x => x is not null) is { } rush)
        {
            output.WriteLine(Row($"bull rush MoveAt (end {rush.Rush.End:0.000})", rush.Rush.MoveAt));
            output.WriteLine(Row("bull rush EvadeAt", rush.Rush.EvadeAt));
            output.WriteLine(Row($"collision stop MoveAt (end {rush.Collision.End:0.000})", rush.Collision.MoveAt));
            output.WriteLine(Row("collision stop EvadeAt", rush.Collision.EvadeAt));
        }
        output.WriteLine($"| throw: evade gate (release {c.ThrowRelease:0.000}) | {c.ThrowDuration:0.000} | {MathF.Max(c.LockTime(c.ThrowDuration), c.ThrowRelease):0.000} |");
        foreach (var p in c.JumpPacks)
            output.WriteLine($"jump pack {p.Name} (not changed here: JumpPack.cs): land move {p.LandMoveLock:0.000} → {c.LockTime(p.LandMoveLock):0.000}, land jump {p.LandJumpLock:0.000} → {c.LockTime(p.LandJumpLock):0.000}, idle move {p.LandIdleMoveLock:0.000} → {c.LockTime(p.LandIdleMoveLock):0.000}, idle jump {p.LandIdleJumpLock:0.000} → {c.LockTime(p.LandIdleJumpLock):0.000}");
        Assert.All(c.Classes.SelectMany(k => k.MeleeChoices).Where(m => m.Wargear.StartsWith("sm_mp_melee", StringComparison.Ordinal)).SelectMany(m => m.Steps),
            s => Assert.True(float.IsFinite(s.EvadeAt)));
    }
}
