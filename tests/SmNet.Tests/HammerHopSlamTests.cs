using System.Numerics;
using SmContent;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// The owner's report of 2026-11-07 on the thunder hammer and the daemon maul (Assault and Raptor; Classic PvP and Musou Mode): (1) sim/hop-swing, a right-click light swing at any point of a
/// ground hop, also from the dash's <c>BRANCH_Dash_Attack</c> on where other weapons dash-attack; (2) sim/slam-recovery, after the F,F slam a new swing or stun strike as soon after its hit as
/// after the light combo's last swing (0.633 s), with mashed presses firing on that tick; (3) sim/slam-steer, the slam turns toward the view like a turret, at most 300 degrees a second and 185
/// in all, and its hit follows the final facing. Synthetic specs with the install's hammer timings (spec "Thunder hammer X1..X3", "Thunder hammer heavies"); the install part is skipped without one.
/// </summary>
public class HammerHopSlamTests(ITestOutputHelper output)
{
    const float Dt = 1f / 30f;
    static readonly Lazy<string?> Game = new(() => GameInstall.TryLocate()?.Dir);

    /// <summary>A fact that is skipped unless the game install is found on this machine (never in CI).</summary>
    public sealed class InstallFactAttribute : FactAttribute
    {
        public InstallFactAttribute()
        {
            if (Game.Value is null) Skip = "Space Marine install not found (never in CI)";
        }
    }

    // The install's hammer (MeleeTuning.ThunderHammer): X1..X3 with their string resets (X3 the owner's 1.333 s), Y "Interrupt Attack" chaining to the "Hammer Slam".
    static readonly MeleeStep X1 = new(2.667f, 0.604f, 0.933f, 1.92f, 1.245f, 1.28f, 200f, 100f, 0f) { HeavyNext = 1, StringResetAt = 1.92f };
    static readonly MeleeStep X2 = new(2.333f, 0.567f, 0.967f, 1.981f, 1.656f, 1.4f, 200f, 100f, 0f) { HeavyNext = 1, StringResetAt = 1.995f };
    static readonly MeleeStep X3 = new(2.333f, 0.7f, 0.892f, 1.544f, 0.961f, 1.51f, 200f, 100f, 0f) { StringEnd = true, HeavyNext = -1, StringResetAt = 1.333f };
    static readonly MeleeStep Y = new(1.667f, 0.433f, 0.533f, 1f, 1f, 1f, 25f, 0f, 0f) { YNext = 2, YComboOpen = 0.667f, YComboClose = 1f, PrimaryReaction = new ReactionEntry(0f, ReactionKind.Stun) };
    static readonly MeleeStep SlamData = new(3f, 1.1f, 0f, 0f, 3f, 2.077f, 100f, 100f, 0f) { PrimaryReaction = new ReactionEntry(0f, ReactionKind.Stun) };
    static readonly MeleeStep Slam = MeleeTuning.Finisher(SlamData, X3, recovery: true, steer: true);

    static ClassLoadout HammerClass(ClassLoadout c, bool hopSwing = true) => c with { Melee = [X1 with { HopSwing = hopSwing }, X2, X3], MeleeHeavy = [Y, Slam] };

    /// <summary>A hammer marine without a pack (the slam and its recovery).</summary>
    static readonly SimConfig Hammer = new()
    {
        Classes = [HammerClass(new ClassLoadout("hammer", -1, -1, [])) with { Evade = -1 }],
        MeleeRange = 4f, MeleeConeDegrees = 60f, MeleeVerticalReach = 1.5f, InputBufferWindow = 0.3f,
        RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f, HealthMax = 1e6f,
    };

    /// <summary>The Assault (pack, hop, dash attack from 10 ticks of the flight) holding the hammer; <paramref name="hopSwing"/> false: a weapon without the rule.</summary>
    internal static SimConfig Pack(bool hopSwing = true) => JumpPackAttackTests.Config with
    {
        Classes = [HammerClass(JumpPackAttackTests.Config.Classes[0], hopSwing), JumpPackAttackTests.Config.Classes[1]],
        InputBufferWindow = 0.3f,
    };

    internal static PlayerState Fresh(SimConfig c, Team team) => Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = 0, Team = team }, c);

    static PlayerState Step(PlayerState s, SimConfig c, InputButtons b, float yaw = 0f) => Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, yaw, b), c, out _);

    public static readonly TheoryData<Team> Sides = new() { Team.SpaceMarines, Team.Chaos };

    // ---------------------------------------------------------------- (1) a swing anywhere in the hop

    internal static PlayerState HopFor(SimConfig c, Team team, int ticks)
    {
        var s = Step(Fresh(c, team), c, InputButtons.Jump);
        s = Step(s, c, InputButtons.None);   // released before the decision tick: the hop
        Assert.True(Evade.Evading(s));
        for (int k = 0; k < ticks; k++) s = Step(s, c, InputButtons.None);
        return s;
    }

    [Theory]
    [MemberData(nameof(Sides))]
    public void A_light_swing_starts_at_early_mid_and_late_points_of_the_hop(Team team)
    {
        var c = Pack();
        // early (0.1 s into the flight), mid (0.37 s: past BRANCH_Dash_Attack, 0.333 s), late (0.63 s, just before the landing), and in the landing part
        foreach (int ticks in new[] { 1, 9, 17, 23 })
        {
            var s = HopFor(c, team, ticks);
            float played = Evade.Time(s, c);
            bool landed = s.EvadeLanded;
            s = Step(s, c, InputButtons.Melee);
            output.WriteLine($"{team}: press {played:0.000} s into the hop{(landed ? " (landing part)" : "")}: step {s.MeleeStep} heavy {s.MeleeHeavy} rush {s.Rush} evading {Evade.Evading(s)}");
            Assert.Equal((1, false, RushPhase.None, false), (s.MeleeStep, s.MeleeHeavy, s.Rush, Evade.Evading(s)));
        }
    }

    [Fact]
    public void Without_the_rule_a_weapon_keeps_the_data_dash_attack_in_the_hops_second_half()
    {
        var c = Pack(hopSwing: false);
        var early = Step(HopFor(c, Team.SpaceMarines, 1), c, InputButtons.Melee);
        Assert.Equal(1, early.MeleeStep);   // before BRANCH_Dash_Attack: the cancel swing (feel/melee-cancels)
        var late = Step(HopFor(c, Team.SpaceMarines, 12), c, InputButtons.Melee);
        Assert.Equal((RushPhase.DashAttack, 0), (late.Rush, late.MeleeStep));
        Assert.False(Rush.CanDashAttack(HopFor(Pack(), Team.SpaceMarines, 12), Pack(), null));   // the hammer: never the dash attack
    }

    [Fact]
    public void The_hop_swing_flag_is_in_the_config_hash_only_when_set()
    {
        Assert.NotEqual(Pack(true).Hash(), Pack(false).Hash());
        Assert.Equal(new SimConfig { Classes = [new ClassLoadout("x", -1, -1, [X1])] }.Hash(), new SimConfig { Classes = [new ClassLoadout("x", -1, -1, [X1 with { HopSwing = false }])] }.Hash());
    }

    // ---------------------------------------------------------------- (2) the recovery after F,F

    [Fact]
    public void The_slam_frees_a_new_string_as_long_after_its_hit_as_the_light_combo_does()
    {
        float lightGap = X3.StringResetAt - X3.HitTime;   // 1.333 - 0.700 = 0.633 s
        Assert.Equal(0.633f, lightGap, 3);
        Assert.Equal(SlamData.HitTime + lightGap, Slam.StringResetAt, 4);   // 1.733 s of 3 s
        Assert.Equal(1.733f, Slam.StringResetAt, 3);
        Assert.False(float.IsFinite(MeleeTuning.Finisher(SlamData, X3 with { StringResetAt = float.PositiveInfinity }, true, false).StringResetAt));   // no light reset: none
    }

    /// <summary>F, F (the second F in Y's slam window), then the given button pressed every other tick: the ticks from the slam's start to the next attack, and that attack.</summary>
    static (int Ticks, PlayerState State) SlamThenMash(SimConfig c, Team team, InputButtons button, int every = 2)
    {
        var s = Step(Fresh(c, team), c, InputButtons.MeleeHeavy);
        int guard = 0;
        while (!(s.MeleeHeavy && s.MeleeStep == 2) && guard++ < 90) s = Step(s, c, guard % 2 == 0 ? InputButtons.MeleeHeavy : InputButtons.None);
        Assert.Equal((2, true), (s.MeleeStep, s.MeleeHeavy));   // the slam
        for (int k = 1; k < 120; k++)
        {
            s = Step(s, c, k % every == 0 ? button : InputButtons.None);
            if (!(s.MeleeHeavy && s.MeleeStep == 2)) return (k, s);
        }
        return (-1, s);
    }

    [Theory]
    [MemberData(nameof(Sides))]
    public void Mashing_after_the_slam_swings_on_the_recovery_tick(Team team)
    {
        var (light, afterLight) = SlamThenMash(Hammer, team, InputButtons.Melee);
        Assert.Equal((1, false), (afterLight.MeleeStep, afterLight.MeleeHeavy));
        Assert.InRange(light, 52, 53);   // 1.733 s = 52 ticks, on the press (every other tick; buffered presses replay)
        var (stun, afterStun) = SlamThenMash(Hammer, team, InputButtons.MeleeHeavy);
        Assert.Equal((1, true), (afterStun.MeleeStep, afterStun.MeleeHeavy));   // another F (and so F,F)
        Assert.InRange(stun, 52, 53);
        // A single press 0.2 s before the reset (kept by the 0.3 s input buffer) fires on the reset tick itself.
        var (once, _) = SlamThenMash(Hammer, team, InputButtons.Melee, every: 46);
        Assert.Equal(52, once);
        // Before: the slam played its whole 3 s clip.
        var old = Hammer with { Classes = [Hammer.Classes[0] with { MeleeHeavy = [Y, SlamData] }] };
        var (before, _) = SlamThenMash(old, team, InputButtons.Melee);
        Assert.InRange(before, 89, 91);
        output.WriteLine($"{team}: after the slam's start a light swing at tick {light} ({light * Dt:0.000} s), a stun strike at {stun}; before sim/slam-recovery {before} ({before * Dt:0.000} s)");
    }

    [Fact]
    public void Musou_mode_has_the_same_recovery()
    {
        var musou = Hammer with { Extras = ContentExtras.Musou };
        var (ticks, after) = SlamThenMash(musou, Team.SpaceMarines, InputButtons.Melee);
        Assert.Equal(0, after.MeleeHeavy ? 1 : 0);
        Assert.InRange(ticks, 52, 53);
    }

    // ---------------------------------------------------------------- (3) the steered slam

    /// <summary>
    /// F, F, then the view turned at <paramref name="degreesPerTick"/> from the tick the slam's steering window opens (a player turning the mouse as the hammer comes down; a view turned
    /// further than 180 degrees ahead of the body is followed the shortest way, as a turret follows its aim point), at most <paramref name="viewLimit"/> degrees: the body's yaw per tick of the
    /// slam (radians, unwrapped against the start).
    /// </summary>
    static List<float> SteerTrace(SimConfig c, Team team, float degreesPerTick, float startYaw = 0f, int ticks = 70, float viewLimit = 1000f)
    {
        float view = startYaw;
        var s = Combat.StepPlayer(Fresh(c, team) with { Yaw = startYaw }, new PlayerInput(0, 0f, 0f, view, InputButtons.MeleeHeavy), c, out _);
        int guard = 0;
        while (!(s.MeleeHeavy && s.MeleeStep == 2) && guard++ < 90)
            s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, view, guard % 2 == 0 ? InputButtons.MeleeHeavy : InputButtons.None), c, out _);
        Assert.Equal(2, s.MeleeStep);
        float start = s.Yaw, total = 0f, last = s.Yaw;
        var trace = new List<float> { 0f };
        for (int k = 0; k < ticks && s.MeleeStep == 2; k++)
        {
            if (s.MeleeTime + Dt > Slam.SteerFrom && MathF.Abs(view - startYaw) * 180f / MathF.PI < viewLimit - 1e-3f) view += degreesPerTick * MathF.PI / 180f;
            s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, view, InputButtons.None), c, out _);
            total += MeleeLock.Delta(last, s.Yaw); last = s.Yaw;
            trace.Add(total);
        }
        return trace;
    }

    [Theory]
    [MemberData(nameof(Sides))]
    public void The_slam_turns_toward_the_view_at_most_300_degrees_a_second_and_185_in_all(Team team)
    {
        var trace = SteerTrace(Hammer, team, 15f);   // the view turns 450 degrees a second, faster than the cap
        float Deg(float r) => r * 180f / MathF.PI;
        int open = (int)MathF.Floor(Slam.SteerFrom / Dt);
        Assert.Equal(0.4833f, Slam.SteerFrom, 3);
        Assert.Equal(1.1f, Slam.SteerTo, 3);
        Assert.All(trace.Take(open), y => Assert.Equal(0f, y, 4));   // before the window: the facing it began with
        int moveAt = (int)MathF.Floor(SlamData.MoveAt / Dt);   // after the slam's move gate the body follows the view as it walks (every strike's rule)
        for (int k = 1; k < Math.Min(trace.Count, moveAt); k++) Assert.True(Deg(trace[k] - trace[k - 1]) <= 300f * Dt + 0.01f, $"tick {k}: {Deg(trace[k] - trace[k - 1])}");
        float final = Deg(trace[(int)MathF.Ceiling(1.1f / Dt) + 1]);
        output.WriteLine($"{team}: steered {final:0.0} degrees; at the hit tick {Deg(trace[(int)MathF.Round(1.1f / Dt)]):0.0}");
        Assert.InRange(final, 184f, 185.5f);   // clamped at 185
        Assert.Equal(final, Deg(trace[moveAt - 1]), 1);   // no turn after the hit, up to the move gate
    }

    [Fact]
    public void A_small_view_turn_is_followed_exactly_and_either_way()
    {
        var right = SteerTrace(Hammer, Team.SpaceMarines, -5f, ticks: 50, viewLimit: 60f);   // 60 degrees at 150 degrees a second, slower than the cap: followed within the window
        var left = SteerTrace(Hammer, Team.SpaceMarines, 5f, ticks: 50, viewLimit: 60f);
        float Deg(float r) => r * 180f / MathF.PI;
        Assert.Equal(-Deg(left[^1]), Deg(right[^1]), 2);
        Assert.InRange(Deg(left[^1]), 59.5f, 60.5f);
    }

    [Fact]
    public void The_slams_hit_follows_its_final_facing()
    {
        // A target 2.5 m to the attacker's left (+90 degrees: -X); the slam starts facing +Y and the view turns left. Unsteered (the old 1:1 view follow without the window) and
        // steered alike it lands there; held still it misses (the cone is 60 degrees).
        static World Arena(SimConfig c)
        {
            var w = new World(c, null, [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(-2.5f, 0f, 0f), 0f)]);
            w.Spawn(1); w.Spawn(2);
            return w;
        }
        int Hits(float degreesPerTick)
        {
            var w = Arena(Hammer);
            float view = 0f;
            int hits = 0;
            w.Step(id => new PlayerInput(0, 0f, 0f, id == 1 ? view : 0f, id == 1 ? InputButtons.MeleeHeavy : InputButtons.None));
            for (int k = 0; k < 140; k++)
            {
                var me = w.Players[1];
                if (me.MeleeHeavy && me.MeleeStep == 2 && MathF.Abs(view) < MathF.PI / 2f - 1e-3f) view += degreesPerTick * MathF.PI / 180f;   // to 90 degrees, then held
                var b = !(me.MeleeHeavy && me.MeleeStep == 2) && k % 2 == 0 && k < 40 ? InputButtons.MeleeHeavy : InputButtons.None;
                w.Step(id => new PlayerInput(0, 0f, 0f, id == 1 ? view : 0f, id == 1 ? b : InputButtons.None));
                hits += w.LastHits.Count(h => h.Shooter == 1 && h.Target == 2 && h.Melee);
                if (k > 60 && w.Players[1].MeleeStep == 0) break;
            }
            output.WriteLine($"view {degreesPerTick} degrees a tick: {hits} melee hits on the target; attacker yaw {w.Players[1].Yaw * 180f / MathF.PI:0.0}");
            return hits;
        }
        Assert.Equal(0, Hits(0f));
        Assert.True(Hits(10f) >= 1);   // turned toward it (90 degrees, inside the 185 limit)
        Assert.Equal(0, Hits(-10f));   // turned away
    }

    [Fact]
    public void The_steering_is_in_the_config_hash_only_when_set()
    {
        var steered = new SimConfig { Classes = [new ClassLoadout("x", -1, -1, [X1]) { MeleeHeavy = [Y, Slam] }] };
        var plain = new SimConfig { Classes = [new ClassLoadout("x", -1, -1, [X1]) { MeleeHeavy = [Y, Slam with { SteerFrom = float.PositiveInfinity }] }] };
        Assert.NotEqual(steered.Hash(), plain.Hash());
        Assert.False(Y.Steers);
        Assert.True(Slam.Steers);
    }

    // ---------------------------------------------------------------- the install

    [InstallFact]
    public void The_install_steers_and_recovers_the_hammers_and_mauls_slams_and_no_other_weapon()
    {
        var c = GameplayTuning.Load(Game.Value!).Config;
        var seen = new HashSet<string>();
        bool hammer = false;
        foreach (var cls in c.Classes)
            foreach (var m in cls.MeleeChoices)
            {
                if (!seen.Add(m.Wargear) || m.Steps.Count == 0) continue;
                bool isHammer = m.Wargear.Contains("thunder_hammer", StringComparison.OrdinalIgnoreCase) || m.Wargear.Contains("power_mace", StringComparison.OrdinalIgnoreCase);
                output.WriteLine($"{m.Wargear}: hop swing {m.Steps[0].HopSwing}; heavies {string.Join(", ", m.Heavy.Select(h => $"{h.Duration:0.000} s hit {h.HitTime:0.000}" + (float.IsFinite(h.StringResetAt) ? $" reset {h.StringResetAt:0.000}" : "") + (h.Steers ? $" steer {h.SteerFrom:0.000}..{h.SteerTo:0.000} at {h.SteerRate}" : "")))}");
                if (isHammer)
                {
                    hammer = true;
                    Assert.True(m.Steps[0].HopSwing);
                    var finishers = m.Heavy.Skip(3).ToList();
                    Assert.Equal(2, finishers.Count);
                    float gap = m.Steps[^1].StringResetAt - m.Steps[^1].HitTime;
                    Assert.All(finishers, f => Assert.Equal(f.HitTime + gap, f.StringResetAt, 3));
                    Assert.All(finishers, f => Assert.True(f.Steers && MathF.Abs((f.SteerTo - f.SteerFrom) * f.SteerRate - 185f) < 0.5f));
                    Assert.All(m.Heavy.Take(3), h => Assert.False(h.Steers || float.IsFinite(h.StringResetAt)));
                }
                else
                {
                    Assert.All(m.Steps, x => Assert.False(x.HopSwing));
                    Assert.All(m.Heavy, h => Assert.False(h.Steers || float.IsFinite(h.StringResetAt)));
                }
            }
        Assert.True(hammer);
    }

    [InstallFact]
    public void The_install_hammer_slam_is_clamped_at_185_degrees_with_its_root_motion()
    {
        var c = GameplayTuning.Load(Game.Value!).Config;
        foreach (var team in new[] { Team.SpaceMarines, Team.Chaos })
        {
            int cls = -1, choice = -1;
            for (int i = 0; i < c.Classes.Count && cls < 0; i++)
                if (c.Classes[i].JumpPack >= 0 && c.Classes[i].Side == team)
                    for (int j = 0; j < c.Classes[i].MeleeChoices.Count; j++)
                        if (c.Classes[i].MeleeChoices[j].Wargear.Contains("hammer") || c.Classes[i].MeleeChoices[j].Wargear.Contains("mace")) { cls = i; choice = j; break; }
            Assert.True(cls >= 0, $"{team}: no jump-pack class with the hammer or maul");
            float view = 0f;
            var s = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = cls, MeleeChoice = choice, Team = team }, c);
            s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, view, InputButtons.MeleeHeavy), c, out _);
            for (int k = 0; k < 60 && !(s.MeleeHeavy && s.MeleeStep >= 4); k++)
                s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, view, k % 2 == 0 ? InputButtons.MeleeHeavy : InputButtons.None), c, out _);
            Assert.True(s.MeleeHeavy && s.MeleeStep >= 4, $"{team}: no slam");
            float total = 0f, last = s.Yaw;
            for (int k = 0; k < 80 && s.MeleeStep >= 4 && s.MeleeTime < 1.3f; k++)
            {
                if (s.MeleeTime + c.TickSeconds > 0.48f) view += 12f * MathF.PI / 180f;   // from the steering window, 360 degrees a second
                s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, view, InputButtons.None), c, out _);
                total += MeleeLock.Delta(last, s.Yaw); last = s.Yaw;
            }
            output.WriteLine($"{team}: the install's slam turned {total * 180f / MathF.PI:0.0} degrees");
            Assert.InRange(total * 180f / MathF.PI, 183f, 186f);
        }
    }
}
