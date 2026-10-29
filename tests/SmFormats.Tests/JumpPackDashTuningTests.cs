using System.Numerics;
using SmContent;
using SmFormats.Baf;
using SmFormats.Havok;
using SmSim;

namespace SmFormats.Tests;

/// <summary>
/// M44 readers and rules: the jump-pack dash from the jump evade file and SM_Base_MP's dash sequencers (flight motion with its height,
/// landing part, decision time), the capacity for two full jumps, the pound's stun in its whole radius. Synthetic files only.
/// </summary>
public class JumpPackDashTuningTests
{
    const float DashLength = 4.3333f, LandLength = 1.1333f;

    static BehaviorTag T(string kind, float start, string name) => new(kind, start, 1f, name, null);

    static BehaviorSequencer Flight(char d) => new($"dash {d}", [$"{EvadeTuning.DashClipPrefix}{d}"],
    [
        T("Branch", 0.0385f, EvadeTuning.ToJumpBranch), T("Modifier", 0.3f, EvadeTuning.FallingModifier), T("Branch", 0.6385f, "BRANCH_Run"),
        new BehaviorTag("EntityProperty", 0f, 0.3333f, "TargetableMode", null, 1), new BehaviorTag("EntityProperty", 0f, 0.4681f, "CollisionMode", null, 1),
    ], [1f]);

    static BehaviorSequencer Landing(string suffix) => new($"land {suffix}", [$"{EvadeTuning.DashLandingPrefix}{suffix}"],
    [
        T("Branch", 0.2222f, "BRANCH_Run"), T("Branch", 0.3333f, "BRANCH_ToRanged"), T("Branch", 0.4444f, "BRANCH_Evade"),
        T("Branch", 0.9259f, EvadeTuning.EndBranch), T("Branch", 0f, EvadeTuning.ToJumpBranch),
    ], [1f]);

    static readonly BehaviorGraph Base = new(EvadeTuning.Graph, [],
        [Flight('F'), Flight('B'), Flight('L'), Flight('R'), Landing("01"), Landing("B"), Landing("L"), Landing("R")]);

    static float Length(string clip) => clip.Contains("Landing") ? LandLength : DashLength;

    /// <summary>The flight: 16.4 m/s along the direction, height 4 × 1.7 × u(1 − u) with u = t / 0.733 (signed after it); the landing: 3 m along.</summary>
    static HavokExtractedMotion Motion(string clip)
    {
        bool landing = clip.Contains("Landing");
        char d = clip[^1] == '1' ? 'F' : clip[^1];
        var dir = d switch { 'F' => Vector2.UnitY, 'B' => -Vector2.UnitY, 'L' => -Vector2.UnitX, _ => Vector2.UnitX };
        int n = landing ? 35 : 131;
        float length = landing ? LandLength : DashLength;
        var samples = Enumerable.Range(0, n).Select(k =>
        {
            float t = length * k / (n - 1), u = t / 0.7333f;
            var xy = dir * (landing ? 3f * t / length : 16.37f * t);
            return new Vector4(xy.X + 0.5f, xy.Y, landing ? 0f : 4f * 1.7f * u * (1f - u), 0f);
        }).ToArray();
        return new HavokExtractedMotion(Vector3.UnitZ, Vector3.UnitY, length, samples);
    }

    static BafDocument Wargear() => BafDocument.Read(SyntheticBafWriter.EvadeAbility("", "evade_jumppack_default_mp", 0.8f, 25, 40, null));

    [Fact]
    public void The_dash_is_read_from_the_jump_evade_and_the_dash_sequencers()
    {
        var notes = new List<string>();
        var e = EvadeTuning.ReadDash("sm_mp_core_abil_jump_evade", Wargear(), [Base], Length, Motion, notes)!;
        Assert.NotNull(e);
        Assert.True(e.Dash);
        Assert.True(e.Diagonals);   // the owner's request: eight directions
        Assert.Equal(("jump_evade", "evade_jumppack_default_mp", 0.8f), (e.AnimationSet, e.Camera, e.MinInterval));
        Assert.Equal(0.0385f * DashLength, e.ToJumpAt, 4);   // 0.167 s
        var f = e.Forward;
        Assert.True(f.Flight);
        Assert.Equal(EvadeTuning.DashClipPrefix + "F", f.Animation);
        float step = DashLength / 130f;
        Assert.Equal(MathF.Round(0.3f * DashLength / step) * step, f.Duration, 4);   // to the Falling modifier (1.3 s), on a sample
        Assert.Equal(f.Path.Count, f.Height.Count);
        Assert.Equal(Vector2.Zero, f.Path[0]);   // relative to the first sample
        Assert.InRange(f.PeakHeight, 1.69f, 1.71f);
        Assert.True(f.Height[^1] < 0f);   // the root keeps falling after the arc: signed
        Assert.False(f.LiftsPose);   // the pose is in place: clients do not lower the model
        var land = f.Landing!;
        Assert.Equal(EvadeTuning.DashLandingPrefix + "01", land.Animation);
        Assert.False(land.Flight);
        Assert.Empty(land.Height);
        float landStep = LandLength / 34f;
        Assert.Equal(MathF.Round(0.9259f * LandLength / landStep) * landStep, land.Duration, 4);
        Assert.Equal(0.2222f * LandLength, land.MoveAt, 4);
        Assert.Equal(0.3333f * LandLength, land.RangedAt, 4);
        Assert.Equal(0f, land.JumpAt);
        Assert.Equal(EvadeTuning.DashLandingPrefix + "B", e.Back.Landing!.Animation);
        Assert.True(e.Back.Path[^1].Y < 0f && e.Left.Path[^1].X < 0f && e.Right.Path[^1].X > 0f);
        Assert.Equal(0.4681f * DashLength, e.HyperArmourTime, 4);
        Assert.Contains(notes, n => n.Contains("jump-pack dash"));
        // The config accepts it on a pack class, and its landing and signed heights pass the checks.
        var pack = JumpPackTuning.Read("p", BafDocument.Read(SyntheticBafWriter.JumpPack([(0.1f, 4f), (0.4f, 18f)], [(0.1f, 35f), (0.4f, 55f)], 100f, 10f, 20f, 12f, 0.5f, 30f, 16f, 0.02f, 0.01f)), notes)!;
        var pistol = new WeaponSpec("pistol", WeaponSlot.Pistol, TriggerKind.SemiAuto, 20f, 0.2f, 10, 50, 1.3f, 100f, 100f, 100f, 1f, 2.5f, 1f, 0.5f, 0.5f);
        var config = GameplayTuning.WithClasses(new SimConfig(), [pistol], [new ClassLoadout("assault", -1, 0, SimConfig.PlaceholderMeleeSteps) { JumpPack = 0, Evade = 0 }], [], [pack], [e]);
        Assert.NotNull(config);
        Assert.Same(config!.Evades[0], Loadouts.Evade(config, 0));
        // Refused: a dash without a landing; a roll with a flight.
        Assert.Null(GameplayTuning.WithClasses(new SimConfig(), [pistol], [new ClassLoadout("assault", -1, 0, SimConfig.PlaceholderMeleeSteps) { JumpPack = 0, Evade = 0 }], [], [pack],
            [e with { Forward = e.Forward with { Landing = null } }]));
        Assert.Null(GameplayTuning.WithClasses(new SimConfig(), [pistol], [new ClassLoadout("assault", -1, 0, SimConfig.PlaceholderMeleeSteps) { JumpPack = 0, Evade = 0 }], [], [pack],
            [e with { Dash = false }]));
        // Refused (review): a decision time of 0 would make every press a free jump.
        Assert.Null(GameplayTuning.WithClasses(new SimConfig(), [pistol], [new ClassLoadout("assault", -1, 0, SimConfig.PlaceholderMeleeSteps) { JumpPack = 0, Evade = 0 }], [], [pack],
            [e with { ToJumpAt = 0f }]));
    }

    [Fact]
    public void The_dash_is_not_read_without_its_sequencers()
    {
        var notes = new List<string>();
        var noLanding = Base with { Sequencers = Base.Sequencers.Where(s => s.Name != "land R").ToList() };
        Assert.Null(EvadeTuning.ReadDash("j", Wargear(), [noLanding], Length, Motion, notes));
        Assert.Contains(notes, n => n.Contains("Landing_Shallow_R") && n.Contains("no dash"));
        Assert.Null(EvadeTuning.ReadDash("j", Wargear(), [Base with { Name = "Other" }], Length, Motion, notes));
    }

    [Fact]
    public void A_full_pack_holds_the_owners_300_five_full_jumps_back_to_back_and_then_a_short_one()
    {
        var notes = new List<string>();
        var doc = BafDocument.Read(SyntheticBafWriter.JumpPack([(0.1f, 4f), (0.1f, 4f), (0.25f, 13f), (0.4f, 18f)], [(0.1f, 35f), (0.25f, 35f), (0.4f, 55f)],
            100f, 10f, 20f, 12f, 0.5f, 30f, 16f, 0.02f, 0.01f));
        var pack = JumpPackTuning.Read("sm_mp_abil_jumppack", doc, notes)!;
        // feel/jump-pack, the owner's decision (2026-10-29): the baseline capacity is 300 (the data's 100; M44's 110 for two full jumps is superseded).
        Assert.Equal(300f, pack.MaxPower);
        Assert.Equal(10f, pack.RechargeRate);   // the data's
        Assert.Contains(notes, n => n.Contains("becomes 300"));
        Assert.Equal(pack, JumpPackTuning.WithFullJumps(pack, notes));   // M44's rule (kept for the record) changes nothing at 300
        var config = new SimConfig { Classes = [new ClassLoadout("assault", -1, -1, SimConfig.PlaceholderMeleeSteps) { JumpPack = 0 }], JumpPacks = [pack] };
        var s = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = 0 }, config);
        Assert.Equal(300f, s.JumpPower);
        int launches = 0, refused = 0;
        for (int k = 0, wait = 0; k < 3000 && launches < 8 && refused == 0; k++)
        {
            // Press as soon as a jump may start (after the landing's lock), hold for the full height.
            bool canPress = s.Jump == JumpPhase.None && s.Grounded && wait == 0;
            bool hold = s.Jump is JumpPhase.TakeOff or JumpPhase.Ascent;
            var before = s;
            s = Combat.StepPlayer(s, new PlayerInput((uint)k, 0f, 0f, 0f, canPress || hold ? InputButtons.Jump : InputButtons.None, 0f), config, out var e);
            wait = canPress || hold ? 1 : 0;   // release one tick between jumps
            if (e.Jump == JumpEventKind.Launch) launches++;
            if (canPress && s.Jump == JumpPhase.None) refused++;   // too little power: no take-off
        }
        // Five full jumps (55 each) back to back, then the power left with the landings' recharge (about 40) still starts a short sixth; the seventh waits.
        Assert.Equal(6, launches);
        Assert.Equal(1, refused);
        Assert.True(s.JumpPower < pack.CostAt(0f));
    }

    [Fact]
    public void The_pound_stuns_in_its_whole_radius_with_the_datas_stun()
    {
        var notes = new List<string>();
        var pound = new SplashSpec(7f, new DamageSpec(25f, 50f, 1f, 1f, 1f, 1f, false), [new(0f, 1f), new(4.5f, 1f), new(7f, 0.05f)], true, true, "target_all")
        {
            Reactions = [new ReactionEntry(0.81f, ReactionKind.Stun, 0.5f), new ReactionEntry(0.8f, ReactionKind.Interrupt)],
        };
        var all = JumpPackTuning.StunAll(pound, "p", notes);
        Assert.Equal([new ReactionEntry(0f, ReactionKind.Stun, 0.5f)], all.Reactions);   // the data's stun (its knockback kept) from intensity 0
        Assert.Equal(ReactionKind.Stun, Reaction.Pick(all.Reactions, Combat.SplashIntensity(all.FallOff, 6.9f))!.Value.Kind);
        Assert.Contains(notes, n => n.Contains("whole 7 m radius"));
        // No stun entry in the data: a plain stun.
        Assert.Equal([new ReactionEntry(0f, ReactionKind.Stun)], JumpPackTuning.StunAll(pound with { Reactions = [] }, "p", notes).Reactions);
    }
}
