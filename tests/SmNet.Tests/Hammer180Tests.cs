using System.Numerics;
using SmContent;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// sim/hammer-180 (protocol 93; the owner 2026-11-25, "Add it for the hammer"): the thunder hammer's and the daemon maul's Dir 180 strikes from the graph <c>MP_SM_MEL_ThunderHammer</c> 'TH Attack Behavior':
/// a light press with the stick more than 135 degrees from where the character faces (<c>FLT_LStick_Angle_Char</c>) starts "TH Default Dir 180" from rest or from X2, the Left Side 180 from X1 or the 180; a
/// plain press after them goes on to "TH Default 2 ( X , X ) From 180" and "TH Default 3 ... From 180", which end the string. Synthetic steps with the install's timings (spec "Thunder hammer Dir 180");
/// the install part is skipped without one.
/// </summary>
public class Hammer180Tests(ITestOutputHelper output)
{
    const float Dt = 1f / 30f;
    static readonly Lazy<string?> Game = new(() => GameInstall.TryLocate()?.Dir);

    public sealed class InstallFactAttribute : FactAttribute
    {
        public InstallFactAttribute()
        {
            if (Game.Value is null) Skip = "Space Marine install not found (never in CI)";
        }
    }

    // The install's hammer (melee-steps, 2026-11-25): X1..X3 and the Dir 180 family as MeleeTuning appends it (light 4..7).
    static readonly MeleeStep X1 = new(2.667f, 0.604f, 0.933f, 1.92f, 1.244f, 1.28f, 200f, 100f, 0f) { BackNext = 5, StringResetAt = 1.92f };
    static readonly MeleeStep X2 = new(2.333f, 0.567f, 0.967f, 1.981f, 1.656f, 1.4f, 200f, 100f, 0f) { BackNext = 4, StringResetAt = 1.995f };
    static readonly MeleeStep X3 = new(2.333f, 0.7f, 0.892f, 1.544f, 0.961f, 1.51f, 200f, 100f, 0f) { StringEnd = true, HeavyNext = -1, StringResetAt = 1.333f };
    static readonly MeleeStep D180 = new(3f, 0.6f, 1.1f, 2.143f, 1.071f, 1.886f, 200f, 100f, 0f)
    { Dir180 = true, TurnsBack = true, BackStart = true, LightNext = 6, BackNext = 5, HeavyNext = 2, StringResetAt = 2.143f, TurnAt = 0.7f };
    static readonly MeleeStep D180Left = new(3f, 0.8f, 1.033f, 2.143f, 0.967f, 1.9f, 200f, 100f, 0f)
    { Dir180 = true, TurnsBack = true, LightNext = 7, HeavyNext = 3, StringResetAt = 2.143f, TurnAt = 0.633f };
    static readonly MeleeStep X2From180 = new(2.333f, 0.633f, 0.767f, 1.5f, 0.833f, 1.4f, 200f, 100f, 0f) { Dir180 = true, StringEnd = true, HeavyNext = -1, StringResetAt = 1.333f };
    static readonly MeleeStep X3From180 = new(2.333f, 0.7f, 0.892f, 1.544f, 0.961f, 1.51f, 200f, 100f, 0f) { Dir180 = true, StringEnd = true, HeavyNext = -1, StringResetAt = 1.333f };
    static readonly MeleeStep Y = new(1.667f, 0.433f, 0.533f, 1f, 1f, 1f, 25f, 0f, 0f) { PrimaryReaction = new ReactionEntry(0f, ReactionKind.Stun) };
    static readonly MeleeStep XY = new(2f, 0.367f, 1f, 1.324f, 0.824f, 1.1f, 35f, 30f, 0f) { PrimaryReaction = new ReactionEntry(0f, ReactionKind.Stun) };
    static readonly MeleeStep XXY = new(2f, 0.36f, 0.88f, 1.324f, 0.88f, 0.92f, 50f, 30f, 0f) { PrimaryReaction = new ReactionEntry(0f, ReactionKind.Stun) };

    static SimConfig Hammer(bool family = true) => new()
    {
        Classes = [new ClassLoadout("hammer", -1, -1, family ? [X1, X2, X3, D180, D180Left, X2From180, X3From180] : [X1 with { BackNext = 0 }, X2 with { BackNext = 0 }, X3]) { MeleeHeavy = [Y, XY, XXY], Evade = -1 }],
        MeleeRange = 4f, MeleeConeDegrees = 60f, MeleeVerticalReach = 1.5f, InputBufferWindow = 0.3f,
        RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f, HealthMax = 1e6f,
    };

    static PlayerState Fresh(SimConfig c) => Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = 0, Team = Team.SpaceMarines }, c);
    static PlayerState Step(PlayerState s, SimConfig c, InputButtons b, float x = 0f, float y = 0f) => Combat.StepPlayer(s, new PlayerInput(0, x, y, 0f, b), c, out _);

    /// <summary>Lets the strike run (no input) until its clock reaches <paramref name="at"/> seconds.</summary>
    static PlayerState RunTo(PlayerState s, SimConfig c, float at)
    {
        int step = s.MeleeStep;
        for (int k = 0; k < 400 && s.MeleeStep == step && s.MeleeTime + 1e-4f < at; k++) s = Step(s, c, InputButtons.None);
        return s;
    }

    /// <summary>A light press in the combo window of the strike in progress (just after it opens), the stick at (<paramref name="x"/>, <paramref name="y"/>).</summary>
    static PlayerState Chain(PlayerState s, SimConfig c, float x = 0f, float y = 0f, InputButtons b = InputButtons.Melee)
    {
        var m = Combat.MeleeStepOf(c, s)!.Value;
        s = RunTo(s, c, m.ComboOpen + Dt);
        return Step(s, c, b, x, y);
    }

    [Fact]
    public void Stick_back_from_rest_starts_the_Dir_180_strike_struck_backwards()
    {
        var c = Hammer();
        var s = Step(Fresh(c), c, InputButtons.Melee, 0f, -1f);   // S
        output.WriteLine($"S + melee: step {s.MeleeStep} direction {s.MeleeDirection} yaw {s.Yaw:0.###}");
        Assert.Equal((4, false, EvadeDirection.Back), (s.MeleeStep, s.MeleeHeavy, s.MeleeDirection));
        Assert.Equal(MathF.PI, MathF.Abs(s.Yaw), 3);
    }

    [Fact]
    public void Without_the_stick_back_the_combo_is_unchanged()
    {
        var c = Hammer();
        Assert.Equal((1, EvadeDirection.Forward), (Step(Fresh(c), c, InputButtons.Melee).MeleeStep, Step(Fresh(c), c, InputButtons.Melee).MeleeDirection));
        // S with A or D is exactly 135 degrees: the directional strike (sim/combat-pass item 19), not the 180.
        var diag = Step(Fresh(c), c, InputButtons.Melee, -0.7071f, -0.7071f);
        Assert.Equal((1, EvadeDirection.BackLeft), (diag.MeleeStep, diag.MeleeDirection));
        var s = Chain(Step(Fresh(c), c, InputButtons.Melee), c);
        Assert.Equal(2, s.MeleeStep);
        s = Chain(s, c);
        Assert.Equal(3, s.MeleeStep);
        // A weapon without the family (the other weapons, Musou's read, SM_OLD_HAMMER_180) strikes X1 backwards as before.
        var old = Step(Fresh(Hammer(false)), Hammer(false), InputButtons.Melee, 0f, -1f);
        Assert.Equal((1, EvadeDirection.Back), (old.MeleeStep, old.MeleeDirection));
    }

    [Fact]
    public void A_plain_press_after_the_180_is_the_second_strike_from_180_and_keeps_its_facing()
    {
        var c = Hammer();
        var s = Step(Fresh(c), c, InputButtons.Melee, 0f, -1f);
        s = Chain(s, c);
        Assert.Equal((6, EvadeDirection.Back), (s.MeleeStep, s.MeleeDirection));
        // "TH Default 2 ( X , X ) From 180 MP" has no transition out: a press in its window does not chain; the string reset (the owner's 1 s cut) starts a new string from rest.
        var held = Chain(s, c);
        Assert.Equal(6, held.MeleeStep);
        var reset = Step(RunTo(Step(held, c, InputButtons.None), c, X2From180.StringResetAt), c, InputButtons.Melee);
        output.WriteLine($"after the reset: step {reset.MeleeStep} at {reset.MeleeTime:0.###} s");
        Assert.Equal(1, reset.MeleeStep);
    }

    [Fact]
    public void Back_relative_to_the_turned_character_chains_the_Left_Side_180()
    {
        var c = Hammer();
        var s = Step(Fresh(c), c, InputButtons.Melee, 0f, -1f);   // faces back now
        var held = Chain(s, c, 0f, -1f);   // S again: toward where the character faces, not back from it
        Assert.Equal((6, EvadeDirection.Back), (held.MeleeStep, held.MeleeDirection));
        var turn = Chain(s, c, 0f, 1f);   // W: back from the turned character ('180 to 180')
        Assert.Equal((5, EvadeDirection.Forward), (turn.MeleeStep, turn.MeleeDirection));
        var third = Chain(turn, c);
        Assert.Equal((7, EvadeDirection.Forward), (third.MeleeStep, third.MeleeDirection));
    }

    [Fact]
    public void X1_and_X2_with_the_stick_back_chain_into_the_Left_Side_180_and_the_180()
    {
        var c = Hammer();
        var x1 = Step(Fresh(c), c, InputButtons.Melee);
        var left = Chain(x1, c, 0f, -1f);
        Assert.Equal((5, EvadeDirection.Back), (left.MeleeStep, left.MeleeDirection));
        var x2 = Chain(x1, c);
        var r = Chain(x2, c, 0f, -1f);
        Assert.Equal((4, EvadeDirection.Back), (r.MeleeStep, r.MeleeDirection));
        Assert.Equal(3, Chain(x2, c).MeleeStep);   // X2 without the stick: X3 as before
    }

    [Fact]
    public void A_heavy_press_after_the_180s_chains_to_the_sweeping_attacks_and_keeps_the_facing()
    {
        var c = Hammer();
        var s = Chain(Step(Fresh(c), c, InputButtons.Melee, 0f, -1f), c, b: InputButtons.MeleeHeavy);
        Assert.Equal((2, true, EvadeDirection.Back), (s.MeleeStep, s.MeleeHeavy, s.MeleeDirection));   // "Combo End 2 ( X , Y )"
        var left = Chain(Step(Fresh(c), c, InputButtons.Melee), c, 0f, -1f);
        var y = Chain(left, c, b: InputButtons.MeleeHeavy);
        Assert.Equal((3, true, EvadeDirection.Back), (y.MeleeStep, y.MeleeHeavy, y.MeleeDirection));   // "Combo End 3 ( X , X , Y )"
        var end = Chain(Chain(Step(Fresh(c), c, InputButtons.Melee, 0f, -1f), c), c, b: InputButtons.MeleeHeavy);
        Assert.Equal((6, false), (end.MeleeStep, end.MeleeHeavy));   // the second strike from 180: no heavy transition
    }

    [Fact]
    public void The_hits_land_at_the_data_times()
    {
        var c = Hammer();
        var s = Step(Fresh(c), c, InputButtons.Melee, 0f, -1f);
        float hitAt = float.NaN;
        for (int k = 0; k < 120 && float.IsNaN(hitAt); k++)
        {
            float before = s.MeleeTime;
            s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, InputButtons.None), c, out var e);
            if (e.MeleeHitStep == 4) hitAt = s.MeleeTime;
        }
        Assert.InRange(hitAt, D180.HitTime, D180.HitTime + Dt + 1e-4f);
    }

    [Fact]
    public void The_hop_swing_stays_attack_1_with_the_stick_back()
    {
        var c = HammerHopSlamTests.Pack() is var pack ? pack with { Classes = [pack.Classes[0] with { Melee = [X1 with { HopSwing = true }, X2, X3, D180, D180Left, X2From180, X3From180] }, pack.Classes[1]] } : null!;
        var s = HammerHopSlamTests.HopFor(c, Team.SpaceMarines, 5);
        s = Combat.StepPlayer(s, new PlayerInput(0, 0f, -1f, 0f, InputButtons.Melee), c, out _);
        Assert.Equal((1, false), (s.MeleeStep, s.MeleeHeavy));
    }

    [Fact]
    public void Stick_back_is_beyond_135_degrees_of_the_characters_facing()
    {
        Assert.True(MeleeChain.StickBack(0f, -1f, 0f));                 // S
        Assert.False(MeleeChain.StickBack(-0.7071f, -0.7071f, 0f));     // S + A: 135 exactly
        Assert.True(MeleeChain.StickBack(0.34f, -0.94f, 0f));          // a pad stick 160 degrees round
        Assert.False(MeleeChain.StickBack(0f, 0.05f, 0f));              // inside the dead zone
        Assert.True(MeleeChain.StickBack(0f, 1f, MathF.PI));            // W, the character turned round
        Assert.False(MeleeChain.StickBack(0f, -1f, MathF.PI));
        Assert.Equal(EvadeDirection.BackRight, MeleeChain.Opposite(EvadeDirection.ForwardLeft));
    }

    [Fact]
    public void Musous_random_swing_never_draws_a_Dir_180_strike()
    {
        IReadOnlyList<MeleeStep> light = [X1, X2, X3, D180, D180Left, X2From180, X3From180];
        for (uint t = 0; t < 500; t++) Assert.InRange(MeleeChain.MusouSwing(light, (int)(t % 3) + 1, t, 3), 1, 3);
    }

    [Fact]
    public void The_new_fields_are_in_the_config_hash_only_when_set()
    {
        static SimConfig One(MeleeStep m) => new() { Classes = [new ClassLoadout("x", -1, -1, [m])] };
        Assert.Equal(One(X3).Hash(), One(X3 with { Dir180 = false, BackNext = 0, LightNext = 0, TurnAt = float.NaN }).Hash());
        Assert.NotEqual(One(X3).Hash(), One(X3 with { Dir180 = true }).Hash());
        Assert.NotEqual(One(X3).Hash(), One(X3 with { BackNext = 1 }).Hash());
        Assert.NotEqual(One(D180).Hash(), One(D180 with { TurnAt = 0.5f }).Hash());
    }

    [InstallFact]
    public void The_install_hammer_and_maul_read_the_Dir_180_family_with_the_data_timings()
    {
        foreach (var wargear in new[] { "sm_mp_melee_2hand_thunder_hammer", "csm_mp_melee_2hand_power_mace" })
        {
            var t = MeleeTuning.Load(Game.Value!, MpClasses.MeleeMapFor(wargear)!);
            Assert.Equal(7, t.Steps.Count);
            Assert.Equal(["SM_MEL_TH_XATK_Combo2", "SM_MEL_TH_XATK_Combo3", "SM_MEL_TH_HeavyATK02", "SM_MEL_TH_XATK_180_MP", "SM_MEL_TH_XATK_180_combo2", "SM_MEL_TH_XATK180_to_XATK2", "SM_MEL_TH_HeavyATK01"], t.Animations);
            var (d, l, x2, x3) = (t.Steps[3], t.Steps[4], t.Steps[5], t.Steps[6]);
            // Sequencer08a: 3 s, ACTN_Hit_Targets_Multiplayer 0.2, BRANCH_Melee_Combo 0.3667-0.7143, BRANCH_Melee_String_Reset 0.7143, the root's half turn at 0.7 s.
            Assert.Equal((3f, 0.6f, 1.1f, 2.143f, 2.143f, 0.7f), (d.Duration, d.HitTime, d.ComboOpen, d.ComboClose, d.StringResetAt, d.TurnAt), new Tol());
            Assert.Equal((true, true, true, 6, 5, 2), (d.Dir180, d.TurnsBack, d.BackStart, d.LightNext, d.BackNext, d.HeavyNext));
            Assert.Equal((200f, 100f), (d.Damage, d.SecondaryDamage));   // ability_melee_180
            Assert.True(d.Reach is { } r && r.Variants[0].Forward[^1] > 3f, "the 180's backward root motion, taken along the turned facing");
            // Sequencer10: 0.2667 hit, 0.3444-0.7143 combo, the half turn at 0.633 s; SequencerGenerator13: 2.333 s, hit 0.2714, combo 0.3286-0.6429 (string end: the owner's 1 s cut); SequencerGenerator17 as X3.
            Assert.Equal((3f, 0.8f, 1.033f, 2.143f, 0.633f), (l.Duration, l.HitTime, l.ComboOpen, l.ComboClose, l.TurnAt), new Tol());
            Assert.Equal((7, 0, 3, false), (l.LightNext, l.BackNext, l.HeavyNext, l.BackStart));
            Assert.Equal((2.333f, 0.633f, 0.767f, 1.5f, 1.333f), (x2.Duration, x2.HitTime, x2.ComboOpen, x2.ComboClose, x2.StringResetAt), new Tol());
            Assert.Equal((true, -1, false), (x2.StringEnd, x2.HeavyNext, x2.TurnsBack));
            Assert.Equal((2.333f, 0.7f, true), (x3.Duration, x3.HitTime, x3.StringEnd), new Tol3());
            Assert.Equal((5, 4, 0), (t.Steps[0].BackNext, t.Steps[1].BackNext, t.Steps[2].BackNext));
            // The weapon's own strikes are unchanged by the family.
            Assert.Equal((1.333f, -1), (t.Steps[2].StringResetAt, t.Steps[2].HeavyNext), new Tol2());
            output.WriteLine($"{wargear}: {string.Join("; ", t.Steps.Select((m, i) => $"{i + 1} {t.Animations[i]} hit {m.HitTime:0.###}"))}");
        }
        // Musou and Warfront read none (the read under WithoutSwordStab).
        using (MeleeTuning.WithoutSwordStab())
            Assert.Equal(3, MeleeTuning.Load(Game.Value!, MeleeTuning.ThunderHammer).Steps.Count);
    }

    // ---------------------------------------------------------------- review fixes (2026-11-25)

    /// <summary>A light press with the stick at (<paramref name="x"/>, <paramref name="y"/>) once the strike's clock reaches <paramref name="at"/>, the stick held at (<paramref name="hx"/>, <paramref name="hy"/>) until then.</summary>
    static PlayerState PressAt(PlayerState s, SimConfig c, float at, float hx, float hy, float x, float y)
    {
        int step = s.MeleeStep;
        for (int k = 0; k < 400 && s.MeleeStep == step && s.MeleeTime + Dt + 1e-5f < at; k++) s = Step(s, c, InputButtons.None, hx, hy);
        return Step(s, c, InputButtons.Melee, x, y);
    }

    [Fact]
    public void After_the_strike_lets_the_body_go_the_stick_is_read_against_the_body_not_the_latched_direction()
    {
        var c = Hammer();
        // A + X: X1 struck to the left; keep strafing left. Past max(MoveAt 1.28, hit + dt) the strafe faces the body to the view again.
        var x1 = Step(Fresh(c), c, InputButtons.Melee, -1f, 0f);
        Assert.Equal((1, EvadeDirection.Left), (x1.MeleeStep, x1.MeleeDirection));
        // D + X at 1.35 s (in X1's combo window, 0.933-1.92): D is 90 degrees from the body (the view): X2 struck to the right, not the Left Side 180.
        var late = PressAt(x1, c, 1.35f, -1f, 0f, 1f, 0f);
        output.WriteLine($"D + X at 1.35 s: step {late.MeleeStep} {late.MeleeDirection} yaw {late.Yaw:0.###}");
        Assert.Equal((2, EvadeDirection.Right), (late.MeleeStep, late.MeleeDirection));
        // While the strike still holds its facing (before 1.28 s) the latched left facing counts: D is behind it, the Left Side 180 (turned from the left: right).
        var held = PressAt(x1, c, 0.95f, -1f, 0f, 1f, 0f);
        Assert.Equal((5, EvadeDirection.Right), (held.MeleeStep, held.MeleeDirection));
        // The mirror: S + X on the tick X1's string resets (1.92 s), the body facing the view: S is behind it, the 180 from rest, struck backwards (before: X1 struck backwards).
        var reset = PressAt(x1, c, X1.StringResetAt, -1f, 0f, 0f, -1f);
        output.WriteLine($"S + X at the reset: step {reset.MeleeStep} {reset.MeleeDirection} yaw {reset.Yaw:0.###} time {reset.MeleeTime:0.###}");
        Assert.Equal((4, EvadeDirection.Back), (reset.MeleeStep, reset.MeleeDirection));
        Assert.Equal(MathF.PI, MathF.Abs(reset.Yaw), 3);
        // Standing still after X1 (no stick: the body keeps its left heading; no reach data here, the walk faces the view) the body's own facing still decides.
        Assert.Equal(EvadeDirection.Forward, MeleeChain.DirectionOfOffset(0.1f));
        Assert.Equal(EvadeDirection.BackRight, MeleeChain.DirectionOfOffset(-2.4f));
        Assert.Equal(EvadeDirection.Back, MeleeChain.DirectionOfOffset(-MathF.PI + 0.05f));
    }

    /// <summary>A turn strike's root motion after the loader's negation (forward along the turned facing): <paramref name="total"/> m, most of it between 0.3 s and 1.9 s of the 3 s clip.</summary>
    static MeleeVariant TurnMotion(float total) => new(1f, Enumerable.Range(0, 91).Select(i =>
    {
        float t = 3f * i / 90f, u = Math.Clamp((t - 0.3f) / 1.6f, 0f, 1f);
        return total * (0.05f * MathF.Min(1f, t / 0.3f) + 0.95f * u * u * (3f - 2f * u));
    }).ToArray());

    static MeleeStep WithReach(MeleeStep m, float total, float factor = 0.689f) => m with { Reach = new MeleeReach(0f, 0.1f, 0f, m.HitTime, 3.5f, 1f, [TurnMotion(total)]), LungeFactor = factor };

    /// <summary>The hammer with reach data and the install's lunge rules (lunge scale 1.625, front-loading 1, the early step 1.5).</summary>
    static SimConfig HammerWithReach() => Hammer() is var c ? c with
    {
        Classes = [c.Classes[0] with { Melee = [X1, X2, X3, WithReach(D180, 3.95f, 1f), WithReach(D180Left, 5.08f, 1f), WithReach(X2From180, 2f), X3From180] }],
        MeleeLungeScale = 1.625f, MeleeLock = MeleeLockSpec.Default with { StepFront = 1f, StepEarly = MeleeLockSpec.InstallStepEarly },
    } : null!;

    [Fact]
    public void A_turn_strike_travels_as_its_clip_no_lunge_scale_no_front_loading_no_early_step()
    {
        var c = HammerWithReach();
        var turn = c.Classes[0].Melee[3];
        var follow = c.Classes[0].Melee[5];
        float Sum(MeleeStep m, Func<float, float, float> per) { float total = 0f; for (float t = 0f; t < m.Duration - 1e-5f; t += Dt) total += per(t, MathF.Min(t + Dt, m.Duration)); return total; }
        Assert.Equal(1f, MeleeLock.LungeFactorOf(turn, c));
        Assert.Equal(1.625f * 0.689f, MeleeLock.LungeFactorOf(follow, c), 4);
        // Tick by tick the clip's own motion: the same total (3.95 m) and the same profile (no burst on the press tick).
        var v = turn.Reach!.Variants[0];
        for (float t = 0f; t < turn.Duration - 1e-5f; t += Dt)
            Assert.Equal(v.At(MathF.Min(t + Dt, turn.Duration), turn.Duration) - v.At(t, turn.Duration), MeleeLock.LungeStep(turn, t, MathF.Min(t + Dt, turn.Duration), 0f, null, c), 4);
        Assert.Equal(3.95f, Sum(turn, (a, b) => MeleeLock.LungeStep(turn, a, b, 0f, null, c)), 2);
        // The strikes after them are forward strikes and keep the owner's factors (2 m x 1.625 x 0.689).
        Assert.Equal(2f * 1.625f * 0.689f, Sum(follow, (a, b) => MeleeLock.LungeStep(follow, a, b, 0f, null, c)), 2);
    }

    [Fact]
    public void With_reach_data_the_180_carries_the_body_along_the_turned_facing_and_the_Left_Side_back_again()
    {
        var c = HammerWithReach();
        var s = Step(Fresh(c), c, InputButtons.Melee, 0f, -1f);   // S + X, the stick let go after the press
        var trace = new List<PlayerState> { s };
        while (s.MeleeStep == 4 && s.MeleeTime + Dt < D180.ComboOpen + Dt) trace.Add(s = Step(s, c, InputButtons.None));
        output.WriteLine($"the 180 by its combo window: ({s.Position.X:0.###}, {s.Position.Y:0.###}) yaw {s.Yaw:0.###}");
        Assert.All(trace, p => Assert.Equal(MathF.PI, MathF.Abs(p.Yaw), 3));   // the heading stays the turned facing (no target: Align keeps the strike's heading)
        Assert.True(s.Position.Y < -0.3f && MathF.Abs(s.Position.X) < 1e-3f, $"backwards from the view: {s.Position}");
        // Run it out: the clip's 3.95 m, unscaled.
        var whole = s;
        while (whole.MeleeStep == 4) whole = Step(whole, c, InputButtons.None);
        Assert.Equal(-3.95f, whole.Position.Y, 1);
        // W + X in the 180's combo window (back from the turned character): the Left Side 180 turns to the view again and travels forward.
        var left = Step(s, c, InputButtons.Melee, 0f, 1f);
        Assert.Equal((5, EvadeDirection.Forward), (left.MeleeStep, left.MeleeDirection));
        float y0 = left.Position.Y;
        for (int k = 0; k < 20; k++) left = Step(left, c, InputButtons.None);
        Assert.True(left.Position.Y > y0 + 0.2f, $"the Left Side 180 moves toward the view: {y0:0.###} -> {left.Position.Y:0.###}");
        Assert.Equal(0f, left.Yaw, 3);
    }

    [Fact]
    public void A_turn_strike_locks_only_along_its_heading_so_letting_go_of_S_does_not_turn_it_back_to_an_enemy_in_front()
    {
        // sim/hammer-180 (the re-review, the lead's decision 2026-11-25; ours): S + X with an enemy 3 m in front of the view. The press's search goes along the stick (behind): no lock, the 180 turns away.
        // Under way a turn strike keeps and acquires a lock only along its own heading (MeleeLock.HeadingOnly), so with S let go the view's search no longer takes the enemy behind it (before: locked
        // from the next tick, the heading turned back and the lunge carried the body past the enemy). A client naming the enemy anyway is refused by the server's check.
        var c = HammerWithReach();
        var enemy = Combat.Respawned(new PlayerState(2, new Vector3(0f, 3f, 0f), Vector3.Zero, MathF.PI, true) { Class = 0, Team = Team.Chaos }, c);
        var view = new Dictionary<int, PlayerState> { [2] = enemy };
        foreach (bool forced in new[] { false, true })
        {
            var self = Fresh(c);
            int locked = 0;
            var locks = new List<int>();
            for (uint i = 0; i < 30; i++)
            {
                var input = new PlayerInput(i, 0f, i == 0 ? -1f : 0f, 0f, i == 0 ? InputButtons.Melee : InputButtons.None);
                input = input with { MeleeTarget = forced ? 2 : MeleeLock.Choose(self, input, view, c, null, locked) };
                locked = input.MeleeTarget;
                locks.Add(locked);
                self = Combat.StepPlayer(self, input, c, out _, null, view);
            }
            output.WriteLine($"{(forced ? "forced" : "chosen")}: locks {string.Join(",", locks.Distinct())}; after 1 s ({self.Position.X:0.###}, {self.Position.Y:0.###}) yaw {self.Yaw:0.###}");
            Assert.Equal(4, self.MeleeStep);
            if (!forced) Assert.All(locks, l => Assert.Equal(0, l));
            Assert.Equal(MathF.PI, MathF.Abs(self.Yaw), 3);   // still struck away from the enemy
            Assert.True(self.Position.Y < -0.5f && MathF.Abs(self.Position.X) < 1e-3f, $"travelled its clip away from the enemy: {self.Position}");
        }
        // An enemy along the turned heading (behind the view) is taken under way as before, and a plain strike keeps the view's alternative.
        var behind = enemy with { Position = new Vector3(0f, -3f, 0f) };
        var turned = Fresh(c) with { MeleeStep = 4, MeleeTime = 0.1f, Yaw = MathF.PI };
        var idle = new PlayerInput(1, 0f, 0f, 0f, InputButtons.None);
        Assert.Equal(2, MeleeLock.Choose(turned, idle, new Dictionary<int, PlayerState> { [2] = behind }, c, null, 0));
        Assert.Equal(0, MeleeLock.Choose(turned, idle, view, c, null, 0));
        Assert.True(MeleeLock.HeadingOnly(turned, c));
        var plain = Fresh(c) with { MeleeStep = 1, MeleeTime = 0.1f, Yaw = MathF.PI };
        Assert.False(MeleeLock.HeadingOnly(plain, c));
        Assert.Equal(2, MeleeLock.Choose(plain, idle, view, c, null, 0));   // X1 under way: the view's search still finds the enemy in front
    }

    [Theory]
    [InlineData(0.01f)]
    [InlineData(-0.01f)]
    public void S_plus_A_after_the_hold_stays_the_directional_strike_while_the_view_pans(float pan)
    {
        // The re-review: past the hold the body faces the previous tick's view, so the raw offset was the view's turn over one tick and S + A (exactly 135 degrees) flipped to stick-back on a pan.
        var c = Hammer();
        float yaw = 0f;
        var s = Step(Fresh(c), c, InputButtons.Melee);   // X1, forward
        while (s.MeleeTime + Dt < 1.5f)
        {
            yaw += pan;
            s = Combat.StepPlayer(s, new PlayerInput(0, -0.7071f, -0.7071f, yaw, InputButtons.None), c, out _);   // walking S + A, the view panning
        }
        yaw += pan;
        s = Combat.StepPlayer(s, new PlayerInput(0, -0.7071f, -0.7071f, yaw, InputButtons.Melee), c, out _);
        output.WriteLine($"pan {pan}: step {s.MeleeStep} {s.MeleeDirection}");
        Assert.Equal((2, EvadeDirection.BackLeft), (s.MeleeStep, s.MeleeDirection));
    }

    [Fact]
    public void The_plain_chain_never_runs_into_the_appended_family()
    {
        IReadOnlyList<MeleeStep> light = [X1, X2, X3 with { StringEnd = false }, D180, D180Left, X2From180, X3From180];
        Assert.Equal(1, MeleeChain.LightNext(light, 3, false, false));   // X3's old n + 1 rule wraps over the weapon's own three (before: light 4, the 180)
        Assert.Equal(2, MeleeChain.LightNext(light, 1, false, false));
        Assert.Equal(6, MeleeChain.LightNext(light, 4, false, false));
        Assert.Equal(1, MeleeChain.LightNext(light, 6, false, false));
        Assert.Equal(3, MeleeChain.LightNext([X1, X2, X3], 2, false, false));   // a weapon without the family: unchanged
    }

    [Fact]
    public void Over_the_network_a_180_chain_is_predicted_without_correction()
    {
        var c = HammerWithReach();
        var config = c with { Classes = [c.Classes[0] with { Side = Team.SpaceMarines }, c.Classes[0] with { Side = Team.Chaos }] };
        var h = new Harness(new LinkConditions(TimeSpan.FromMilliseconds(30), TimeSpan.Zero, 0), seed: 5, 0, config);
        var client = h.AddClient(config, nonce: 91);
        h.Run(TimeSpan.FromSeconds(1.5));
        Assert.True(client.PlayerId > 0);
        uint start = 0;
        // S + X (the 180), W + X at its combo window (the Left Side 180, back from the turned character), X at the Left Side's (light 7); synthetic clips with reach data (the body moves), 30 ms each way.
        int open4 = (int)MathF.Ceiling(D180.ComboOpen * 30f) + 1, open5 = (int)MathF.Ceiling(D180Left.ComboOpen * 30f) + 1;
        PlayerInput Input(int who, uint tick)
        {
            if (start == 0) start = tick + 5;
            long k = (long)tick - start;
            if (k == 0) return new(tick, 0f, -1f, 0f, InputButtons.Melee);
            if (k == open4) return new(tick, 0f, 1f, 0f, InputButtons.Melee);
            if (k == open4 + open5) return new(tick, 0f, 0f, 0f, InputButtons.Melee);
            return new(tick, 0f, 0f, 0f, InputButtons.None);
        }
        var server = new List<int>();
        var predicted = new List<int>();
        int before = client.CorrectionErrors.Count;
        for (int i = 0; i < 4500; i++)   // 1 ms a step: 4.5 s
        {
            h.Step(Input);
            if (h.Server.World.Players.TryGetValue(client.PlayerId, out var s)) server.Add(s.MeleeStep);
            predicted.Add(client.Predicted.MeleeStep);
        }
        output.WriteLine($"server steps {string.Join(",", server.Distinct())}; predicted {string.Join(",", predicted.Distinct())}");
        foreach (int step in new[] { 4, 5, 7 }) { Assert.Contains(step, server); Assert.Contains(step, predicted); }
        var errors = client.CorrectionErrors.Skip(before).ToList();
        Assert.True(errors.Count == 0 || errors.Max() < 0.01f, $"corrections {errors.DefaultIfEmpty().Max()}");
    }

    [InstallFact]
    public void The_install_match_configs_of_Musou_and_Warfront_hold_no_Dir_180_strike_and_Classic_does()
    {
        static IEnumerable<MeleeStep> All(SimConfig c) => c.MeleeSteps.Concat(c.Classes.SelectMany(k => k.Melee.Concat(k.MeleeHeavy).Concat(k.MeleeChoices.SelectMany(m => m.Steps.Concat(m.Heavy)))));
        static bool Family(MeleeStep m) => m.Dir180 || m.TurnsBack || m.BackStart || m.BackNext != 0 || m.LightNext != 0 || float.IsFinite(m.TurnAt);
        var classic = GameplayTuning.LoadForMatch(Game.Value!, ContentExtras.None).Config;
        Assert.Contains(All(classic), Family);
        // Classic's turn strikes travel as their clips (the review's faithful exemption): x1.
        Assert.All(All(classic).Where(m => m.TurnsBack), m => Assert.Equal(1f, MeleeLock.LungeFactorOf(m, classic)));
        foreach (var t in All(classic).Where(m => m.TurnsBack))
            output.WriteLine($"Classic turn strike: {t.Duration:0.###} s, travel {Total(t, classic):0.00} m");
        if (!ContentExtras.Compiled) return;
        var musou = GameplayTuning.LoadForMatch(Game.Value!, ContentExtras.Musou).Config;
        Assert.DoesNotContain(All(musou), Family);
        if (!ContentExtras.WarfrontCompiled) return;
        var warfront = GameplayTuning.LoadForMatch(Game.Value!, ContentExtras.Musou | ContentExtras.Warfront).Config;
        Assert.DoesNotContain(All(warfront), Family);

        static float Total(MeleeStep m, SimConfig c) { float total = 0f; for (float t = 0f; t < m.Duration - 1e-5f; t += Dt) total += MeleeLock.LungeStep(m, t, MathF.Min(t + Dt, m.Duration), 0f, null, c); return total; }
    }

    [InstallFact]
    public void The_install_turn_clips_jump_in_the_sample_before_TurnAt_and_the_drawn_time_skips_it()
    {
        // The client's Hammer180Draw assumes both clips sample at 1/30 s and their bones jump in the interval that ends at TurnAt (measured: the pelvis spins ~160 degrees there).
        var t = MeleeTuning.Load(Game.Value!, MeleeTuning.ThunderHammer);
        foreach (var m in new[] { t.Steps[3], t.Steps[4] })
        {
            Assert.Equal(0f, MathF.IEEERemainder(m.TurnAt, SmNet.Hammer180Draw.SampleSeconds), 3);
            float a = m.TurnAt - SmNet.Hammer180Draw.SampleSeconds;
            Assert.Equal(a, SmNet.Hammer180Draw.ClipTime(m, a + 0.005f), 4);
            Assert.Equal(m.TurnAt, SmNet.Hammer180Draw.ClipTime(m, m.TurnAt - 0.005f), 4);
            Assert.Equal(m.TurnAt + 0.01f, SmNet.Hammer180Draw.ClipTime(m, m.TurnAt + 0.01f), 4);
            Assert.True(SmNet.Hammer180Draw.Unturned(m, a + 0.005f) && !SmNet.Hammer180Draw.Unturned(m, m.TurnAt - 0.005f));
        }
    }

    [Fact]
    public void Remote_yaw_holds_across_a_change_into_or_out_of_a_turn_strike_only()
    {
        var c = Hammer();
        var at180 = Fresh(c) with { MeleeStep = 4, MeleeTime = 1.2f, Yaw = MathF.PI };
        var atLeft = Fresh(c) with { MeleeStep = 5, MeleeTime = 0.033f, Yaw = 0f };
        var x1 = Fresh(c) with { MeleeStep = 1, MeleeTime = 1f };
        var x2 = Fresh(c) with { MeleeStep = 2, MeleeTime = 0.03f };
        Assert.True(SmNet.Hammer180Draw.HoldsYaw(c, at180, atLeft));       // 4 -> 5: the facing flips; the older strike is drawn
        Assert.True(SmNet.Hammer180Draw.HoldsYaw(c, Fresh(c), at180 with { MeleeTime = 0.03f }));   // rest -> the 180
        Assert.False(SmNet.Hammer180Draw.HoldsYaw(c, at180, at180 with { MeleeTime = 1.233f }));   // the same strike
        Assert.False(SmNet.Hammer180Draw.HoldsYaw(c, x1, x2));             // other chains interpolate as before
        Assert.False(SmNet.Hammer180Draw.HoldsYaw(null, at180, atLeft));
    }

    sealed class Tol : IEqualityComparer<(float, float, float, float, float, float)>, IEqualityComparer<(float, float, float, float, float)>
    {
        static bool Near(float a, float b) => MathF.Abs(a - b) < 2e-3f;
        public bool Equals((float, float, float, float, float, float) a, (float, float, float, float, float, float) b) =>
            Near(a.Item1, b.Item1) && Near(a.Item2, b.Item2) && Near(a.Item3, b.Item3) && Near(a.Item4, b.Item4) && Near(a.Item5, b.Item5) && Near(a.Item6, b.Item6);
        public bool Equals((float, float, float, float, float) a, (float, float, float, float, float) b) =>
            Near(a.Item1, b.Item1) && Near(a.Item2, b.Item2) && Near(a.Item3, b.Item3) && Near(a.Item4, b.Item4) && Near(a.Item5, b.Item5);
        public int GetHashCode((float, float, float, float, float, float) o) => 0;
        public int GetHashCode((float, float, float, float, float) o) => 0;
    }
    sealed class Tol3 : IEqualityComparer<(float, float, bool)>
    {
        public bool Equals((float, float, bool) a, (float, float, bool) b) => MathF.Abs(a.Item1 - b.Item1) < 2e-3f && MathF.Abs(a.Item2 - b.Item2) < 2e-3f && a.Item3 == b.Item3;
        public int GetHashCode((float, float, bool) o) => 0;
    }
    sealed class Tol2 : IEqualityComparer<(float, int)>
    {
        public bool Equals((float, int) a, (float, int) b) => MathF.Abs(a.Item1 - b.Item1) < 2e-3f && a.Item2 == b.Item2;
        public int GetHashCode((float, int) o) => 0;
    }
}
