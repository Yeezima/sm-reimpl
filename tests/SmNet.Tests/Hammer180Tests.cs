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
