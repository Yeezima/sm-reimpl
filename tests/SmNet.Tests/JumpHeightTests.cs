using System.Numerics;
using SmContent;
using SmFormats.Baf;
using SmNet;
using SmServer;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// M45 (the owner's knowledge of the original, 2026-10-28: "let go of Space mid-jump to stop rising"): the jump pack's middle jump. Superseded
/// on 2026-10-29 by the three jumps (feel/jump-pack-2, JumpTierTests; the reader switches the cut off): the rule stays in the simulation as an option.
/// Letting go of Jump while a committed jump rises cuts the rise: no more hold growth at once, the rise goes on unchanged for
/// <see cref="JumpPackSpec.ReleaseCutDelay"/> (ours), then the vertical speed decays (<see cref="JumpPackSpec.ReleaseCutDecay"/>, ours)
/// to the apex, where the usual hang and fall follow. A full hold is the old jump exactly; a tap is still the dash. Synthetic specs
/// shaped like the install's values (the M44 dash config with the cut switched on). No original bytes.
/// </summary>
public class JumpHeightTests(ITestOutputHelper output)
{
    const int Assault = 0;
    const float Dt = 1f / 30f;

    static readonly JumpPackSpec Cut = JumpPackDashTests.Config.JumpPacks[0] with { ReleaseCut = true };
    static readonly SimConfig On = JumpPackDashTests.Config with { JumpPacks = [Cut] };
    static readonly SimConfig Off = JumpPackDashTests.Config;

    static PlayerState Fresh(SimConfig c) => Combat.Respawned(new PlayerState(1, default, Vector3.Zero, 0f, true) { Class = Assault }, c);

    static SelfEvents Step(ref PlayerState s, InputButtons b, SimConfig c, float my = 0f, float pitch = 0f)
    {
        s = Combat.StepPlayer(s, new PlayerInput(0, 0f, my, 0f, b, pitch), c, out var e);
        return e;
    }

    /// <summary>The whole jump with Jump down on ticks 0 .. held − 1 (the press is tick 0): every state until the landing.</summary>
    static List<PlayerState> Jump(SimConfig c, int held, float my = 0f, Func<int, PlayerState, InputButtons>? extra = null)
    {
        var s = Fresh(c);
        var states = new List<PlayerState>();
        for (int k = 0; k < 400; k++)
        {
            var b = k < held ? InputButtons.Jump : InputButtons.None;
            if (extra is not null) b |= extra(k, s);
            Step(ref s, b, c, my);
            states.Add(s);
            if (s.Jump is JumpPhase.Landed or JumpPhase.Recover || (k > 6 && s.Jump == JumpPhase.None && s.EvadeTicks == 0 && s.Grounded)) break;
        }
        return states;
    }

    static float Apex(List<PlayerState> states) => states.Max(s => s.Position.Z);

    // ---------------------------------------------------------------- heights

    [Fact]
    public void Later_releases_give_higher_apexes_and_holding_to_the_top_is_the_old_full_jump_exactly()
    {
        var full = Jump(Off, 400);
        int apexTick = full.FindIndex(s => s.Jump == JumpPhase.Hang);
        float fullApex = Apex(full);
        output.WriteLine($"full hold (cut off): apex {fullApex:F2} m at tick {apexTick} ({apexTick * Dt:F2} s after the press)");
        float previous = 0f;
        foreach (int held in new[] { 6, 7, 8, 10, 12, 15, 20, 25, 30, 35, 40 })
        {
            float apex = Apex(Jump(On, held)), old = Apex(Jump(Off, held));
            output.WriteLine($"released at tick {held} ({held * Dt:F3} s after the press): apex {apex:F2} m (M44 rules {old:F2} m)");
            Assert.True(apex > previous + 0.05f, $"release at {held}: apex {apex:F3} not above {previous:F3}");
            Assert.True(apex < fullApex, $"release at {held}: apex {apex:F3} not below the full jump's {fullApex:F3}");
            previous = apex;
        }
        // Held to the apex (and beyond, or let go at the apex tick or after): the same states, tick by tick, as the M44 full jump.
        foreach (int held in new[] { apexTick + 1, apexTick + 5, 400 })
        {
            var cut = Jump(On, held);
            var old = Jump(Off, held);
            Assert.Equal(old.Count, cut.Count);
            for (int k = 0; k < old.Count; k++) Assert.Equal(old[k], cut[k]);
            Assert.Equal(fullApex, Apex(cut));
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]   // released on tick 5 (held ticks 0..4): still the dash
    public void A_tap_is_still_the_dash_unchanged(int held)
    {
        var cut = Jump(On, held, my: 1f);
        var old = Jump(Off, held, my: 1f);
        Assert.Contains(cut, s => s.EvadeTicks > 0);
        Assert.DoesNotContain(cut, s => s.Jump == JumpPhase.Ascent);
        Assert.Equal(old.Count, cut.Count);
        for (int k = 0; k < old.Count; k++) Assert.Equal(old[k], cut[k]);
    }

    [Fact]
    public void Nav_jump_holds_span_a_low_hop_to_the_full_jump_and_the_bots_full_rise_holds_to_the_apex()
    {
        var v = new[] { new Vector3(-300, -300, 0), new Vector3(300, -300, 0), new Vector3(300, 300, 0), new Vector3(-300, 300, 0) };
        var mesh = new StaticMesh(v, [0, 1, 2, 0, 2, 3]);
        var world = new CollisionWorld(mesh, mesh);
        int shortest = NavJumpFlight.ShortestJumpHold(On, Assault);
        Assert.Equal(5, shortest);
        var holds = new NavJumpOptions().Holds.Select(h => Math.Max(h, shortest)).ToArray();
        Assert.Equal([5, 14, 19, 60], holds);
        float[] expected = [2f, 9f, 12f, 17.5f];
        float previous = 0f;
        for (int i = 0; i < holds.Length; i++)
        {
            var r = NavJumpFlight.Simulate(world, On, Assault, Team.SpaceMarines, Vector3.Zero, 0, holds[i]);
            output.WriteLine($"nav hold {holds[i]}: apex {r.Apex:F2} m, {r.Ticks} ticks, landed {r.Landed} ({r.Note})");
            Assert.True(r.Landed);
            Assert.InRange(r.Apex, expected[i] - 1f, expected[i] + 1f);
            Assert.True(r.Apex > previous);
            previous = r.Apex;
        }
        // The full rise: holding FullRiseTicks gives the full jump exactly.
        int full = JumpPack.FullRiseTicks(Cut, On);
        Assert.Equal(49, full);
        Assert.Equal(Apex(Jump(Off, 400)), Apex(Jump(On, full)));
        Assert.True(Apex(Jump(On, full - 8)) < Apex(Jump(Off, 400)));
    }

    // ---------------------------------------------------------------- the rule

    [Fact]
    public void A_release_in_the_ascent_keeps_the_rise_for_the_delay_then_decays_it_to_the_apex_and_the_hang_follows()
    {
        const int release = 20;   // after the hold window (tick 12): only the cut changes the jump
        var s = Jump(On, release);
        float f = MathF.Exp(-Dt / Cut.ReleaseCutDecay);
        int delay = (int)MathF.Round(Cut.ReleaseCutDelay / Dt);
        Assert.Equal(3, delay);
        Assert.Equal((JumpPhase.Ascent, false, false), (s[release - 1].Jump, s[release - 1].JumpCut, s[release - 1].JumpHolding));
        // The release tick: cut, its mark is the release tick; the rise goes on under jumping gravity for the delay's ticks.
        Assert.Equal((true, release), (s[release].JumpCut, s[release].JumpMark));
        for (int k = release; k < release + delay; k++) Assert.Equal(-20f * Dt, s[k].Velocity.Z - s[k - 1].Velocity.Z, 3);
        int apex = s.FindIndex(release, p => p.Jump == JumpPhase.Hang);
        Assert.True(apex > release + delay, "the apex came before the decay");
        for (int k = release + delay; k < apex; k++)
        {
            Assert.Equal(JumpPhase.Ascent, s[k].Jump);
            Assert.True(s[k].JumpCut);
            Assert.Equal(s[k - 1].Velocity.Z * f - 20f * Dt, s[k].Velocity.Z, 3);
        }
        output.WriteLine($"released at tick {release} at {s[release].Position.Z:F2} m, {s[release].Velocity.Z:F1} m/s up; apex at tick {apex}, {Apex(s):F2} m " +
                         $"({(apex - release) * Dt:F2} s, {Apex(s) - s[release].Position.Z:F2} m after the release)");
        Assert.InRange(apex - release, delay + 2, delay + 8);
        // The apex: the usual hang (12 m/s² for 15 ticks after the apex tick), then the fall; the cut flag is gone.
        Assert.False(s[apex].JumpCut);
        for (int k = apex + 1; k <= apex + 15; k++) Assert.Equal(-12f * Dt, s[k].Velocity.Z - s[k - 1].Velocity.Z, 3);
        Assert.Equal(JumpPhase.Fall, s[apex + 16].Jump);
        Assert.Equal(JumpPhase.Landed, s[^1].Jump);
    }

    [Fact]
    public void A_release_inside_the_hold_window_stops_the_hold_at_once_and_a_new_press_in_the_air_does_not_undo_the_cut()
    {
        const int release = 8;
        var cut = Jump(On, release);
        var old = Jump(Off, release);
        // Up to and including the release tick nothing differs: the hold stopped growing at the release (tick 7 was the last held one).
        for (int k = 0; k < release; k++) Assert.Equal(old[k], cut[k]);
        Assert.Equal(old[release].Velocity.Z, cut[release].Velocity.Z);
        Assert.Equal((false, true, release), (cut[release].JumpHolding, cut[release].JumpCut, cut[release].JumpMark));
        Assert.True(Apex(cut) < Apex(old) - 3f, $"cut {Apex(cut):F2} m, M44 {Apex(old):F2} m");
        // Pressing Jump again from tick 12 on changes nothing: the cut is latched until the apex.
        var again = Jump(On, release, extra: (k, _) => k >= 12 ? InputButtons.Jump : InputButtons.None);
        int apex = cut.FindIndex(p => p.Jump == JumpPhase.Hang);
        for (int k = 0; k <= apex; k++) Assert.Equal(cut[k] with { JumpHeld = false }, again[k] with { JumpHeld = false });
    }

    [Fact]
    public void Released_on_the_lift_off_tick_the_cut_counts_from_the_lift_off()
    {
        var s = Jump(On, 6);   // held ticks 0..5 (committed on tick 5), let go on the lift-off tick 6
        Assert.Equal((JumpPhase.Ascent, true, 6), (s[6].Jump, s[6].JumpCut, s[6].JumpMark));
        Assert.Equal(MathF.Sqrt(2f * 20f * Cut.HeightAt(5 * Dt)) - 20f * Dt, s[6].Velocity.Z, 3);   // the launch of the 5-tick hold (8 m)
        output.WriteLine($"shortest middle jump: apex {Apex(s):F2} m");
        Assert.InRange(Apex(s), 1.5f, 4.5f);
    }

    [Fact]
    public void Let_go_in_the_take_off_and_pressed_again_before_the_lift_off_is_no_cut_but_the_jump_of_the_hold_so_far()
    {
        // The install's dash decides on tick 5 and lifts off on tick 6: no tick between. With a decision on tick 3 there are two.
        var early = JumpPackDashTests.Dash with { ToJumpAt = 3f / 30f };
        var on = On with { Evades = [early] };
        var off = Off with { Evades = [early] };
        // Held ticks 0..3 (committed on tick 3), let go on tick 4, Jump down again from tick 5 to the landing.
        InputButtons B(int k, PlayerState _) => k == 4 ? InputButtons.None : InputButtons.Jump;
        var cut = Jump(on, 0, extra: B);
        var old = Jump(off, 0, extra: B);
        Assert.Equal((JumpPhase.TakeOff, false), (cut[4].Jump, cut[4].JumpHolding));   // the hold stopped growing at the release (as M14)
        Assert.DoesNotContain(cut, s => s.JumpCut);                                     // down at the lift-off and after: no cut
        Assert.Equal(old.Count, cut.Count);
        for (int k = 0; k < old.Count; k++) Assert.Equal(old[k], cut[k]);
        Assert.InRange(Apex(cut), Cut.HeightAt(3 * Dt) - 0.6f, Cut.HeightAt(3 * Dt) + 0.05f);   // the 3-tick hold's height (0.1 s: 4 m), not more
    }

    // ---------------------------------------------------------------- power, pound, reactions

    [Theory]
    [InlineData(6, 35f)]     // hold 5 ticks (0.167 s): the data's flat 35
    [InlineData(10, 41.667f)] // hold 9 ticks (0.3 s): 35 + 20 × 0.05 / 0.15
    [InlineData(20, 55f)]    // past the hold window: the full jump's cost
    [InlineData(400, 55f)]
    public void Power_is_charged_for_the_hold_used_only(int held, float cost)
    {
        var s = Jump(On, held);
        Assert.Equal(JumpPhase.Landed, s[^1].Jump);
        int landed = s.Count - 1;
        // No recharge in the air; the landing tick recharges nothing either (phase Landed starts in Move).
        Assert.Equal(110f - cost, s[landed].JumpPower, 2);
        Assert.Equal(Cut.MaxPower - Cut.CostAt(MathF.Min((held - 1) * Dt, Cut.MaxHold)), s[landed].JumpPower, 2);
    }

    [Fact]
    public void A_ground_pound_after_an_early_release_works_on_the_way_up_and_from_the_hang()
    {
        foreach (bool fromHang in new[] { false, true })
        {
            bool pressed = false;
            var s = Jump(On, 9, extra: (k, p) =>
            {
                bool now = !pressed && (fromHang ? p.Jump == JumpPhase.Hang : k == 11);
                if (now) pressed = true;
                return now ? InputButtons.Melee : InputButtons.None;
            });
            int dive = s.FindIndex(p => p.Jump == JumpPhase.Dive);
            Assert.True(dive > 0, $"no dive (from the hang: {fromHang})");
            Assert.False(s[dive].JumpCut);
            Assert.Equal(JumpPhase.Recover, s[^1].Jump);
            if (!fromHang) Assert.True(s[dive - 1].JumpCut);
        }
    }

    [Fact]
    public void Reactions_a_cut_rise_negates_a_stun_and_a_reaction_in_the_take_off_ends_the_jump_with_no_cut_left()
    {
        var s = Jump(On, 15);
        var rising = s.First(p => p.JumpCut);
        Assert.False(Reaction.CanReact(rising, On));   // jump-pack flight negates reactions (M28), cut or not
        // A reaction in the take-off (committed hold) ends the jump; the reset leaves no cut behind.
        var taking = s[5];
        Assert.Equal(JumpPhase.TakeOff, taking.Jump);
        Assert.True(Reaction.CanReact(taking, On));
        var stunned = Reaction.Start(taking, On, new ReactionEntry(0f, ReactionKind.Stun), ReactionDirection.Front, Vector2.Zero);
        Assert.Equal((JumpPhase.None, false), (stunned.Jump, stunned.JumpCut));
        // The same reset for a (hypothetical) cut ascent the reaction rules let through.
        var forced = Reaction.Start(rising, On, new ReactionEntry(0f, ReactionKind.Stun), ReactionDirection.Front, Vector2.Zero);
        Assert.Equal((JumpPhase.None, false), (forced.Jump, forced.JumpCut));
        // Letting go of Jump while stunned on the ground does nothing to the jump state (no jump runs).
        var g = stunned;
        for (int k = 0; k < 10; k++) { Step(ref g, k % 2 == 0 ? InputButtons.Jump : InputButtons.None, On); Assert.False(g.JumpCut); }
    }

    // ---------------------------------------------------------------- data, wire and prediction

    [Fact]
    public void The_reader_keeps_the_cut_s_values_but_switches_it_off_since_the_three_jumps()
    {
        var doc = BafDocument.Read(SyntheticBafWriter.JumpPack([(0.1f, 4f), (0.25f, 13f), (0.4f, 18f)], [(0.1f, 35f), (0.25f, 35f), (0.4f, 55f)],
            100f, 10f, 20f, 12f, 0.5f, 30f, 16f, 0.02f, 0.01f));
        var pack = JumpPackTuning.Read("sm_mp_abil_jumppack", doc, [])!;
        // feel/jump-pack-2 (the owner, 2026-10-29: "revert to the old jump behavior"): the three jumps replace the middle jump (JumpTierTests).
        Assert.False(pack.ReleaseCut);
        Assert.True(pack.HoldTiers);
        Assert.Equal((JumpPackTuning.ReleaseRiseDelay, JumpPackTuning.ReleaseRiseDecay), (pack.ReleaseCutDelay, pack.ReleaseCutDecay));
        Assert.Equal((0.1f, 0.05f), (JumpPackTuning.ReleaseRiseDelay, JumpPackTuning.ReleaseRiseDecay));
    }

    [Fact]
    public void Protocol_27_carries_the_cut_in_the_own_record_and_refuses_it_elsewhere_and_the_config_hash_covers_it()
    {
        Assert.Equal(42, Protocol.Version);
        var s = Jump(On, 15);
        var p = s.First(x => x.JumpCut) with { PlayerId = 7 };
        var bytes = Protocol.Write(new Snapshot(1, 1, 1, [p], Self: 7));
        var back = Assert.IsType<Snapshot>(Protocol.Read(bytes)).Players[0];
        Assert.Equal((JumpPhase.Ascent, true, p.JumpMark, false), (back.Jump, back.JumpCut, back.JumpMark, back.JumpHolding));
        // The jump byte's offset: find it by flipping the flag off and comparing.
        var plain = Protocol.Write(new Snapshot(1, 1, 1, [p with { JumpCut = false }], Self: 7));
        int at = Enumerable.Range(0, bytes.Length).Single(i => bytes[i] != plain[i]);
        Assert.Equal(64, bytes[at] ^ plain[at]);
        var hang = (byte[])bytes.Clone(); hang[at] = (byte)((hang[at] & ~7) | (int)JumpPhase.Hang);
        Assert.Null(Protocol.Read(hang));            // the cut outside the ascent
        var holding = (byte[])bytes.Clone(); holding[at] |= 16;
        Assert.Null(Protocol.Read(holding));         // a cut rise that still holds
        Assert.NotEqual(Off.Hash(), On.Hash());
        Assert.NotEqual(On.Hash(), (On with { JumpPacks = [Cut with { ReleaseCutDelay = 0.15f }] }).Hash());
        Assert.NotEqual(On.Hash(), (On with { JumpPacks = [Cut with { ReleaseCutDecay = 0.1f }] }).Hash());
        Assert.Equal(1200, Protocol.MaxSnapshotBytes);
    }

    [Theory]
    [InlineData(461)]
    [InlineData(462)]
    public void Prediction_matches_the_server_through_releases_at_every_height_under_150ms_rtt_and_5pct_loss(int seed)
    {
        var config = On with
        {
            RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f,
            JumpPacks = [Cut with { RechargeRate = 100f }],   // every cycle has the power for a jump
        };
        var link = new LinkConditions(TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(10), 0.05);
        var h = new Harness(link, seed, 0, config, new ServerOptions { PeerTimeout = TimeSpan.FromMinutes(5) });
        for (int i = 0; i < 2; i++) h.AddClient(nonce: (uint)(600 + i), options: new ClientOptions { RequestedClass = Assault });
        var truth = new SortedDictionary<uint, Dictionary<int, PlayerState>>();
        var predicted = Enumerable.Range(0, 2).Select(_ => new Dictionary<uint, PlayerState>()).ToArray();
        int[] holds = [6, 9, 14, 22, 30, 38, 60];
        PlayerInput Input(int i, uint t)
        {
            uint cycle = (t + (uint)i * 23) / 120, phase = (t + (uint)i * 23) % 120;
            int hold = holds[(int)(cycle % (uint)holds.Length)];
            var buttons = phase < hold ? InputButtons.Jump : InputButtons.None;
            if (cycle % 3 == 1 && phase == 26) buttons |= InputButtons.Melee;   // a pound after some releases
            return new PlayerInput(t, cycle % 2 == 0 ? 1f : 0f, 1f, 0.3f * i, buttons, -0.6f);
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
            int cuts = mine.Zip(mine.Skip(1)).Count(p => !p.First.JumpCut && p.Second.JumpCut);
            int launches = mine.Zip(mine.Skip(1)).Count(p => p.First.Jump == JumpPhase.TakeOff && p.Second.Jump == JumpPhase.Ascent);
            int releaseTicksCompared = predicted[i].Count(kv => kv.Key >= from && truth.TryGetValue(kv.Key, out var w) && w.TryGetValue(id, out var r) && r.JumpCut && r.JumpMark == r.JumpTicks);
            output.WriteLine($"client {i}: compared {compared}, {mismatches.Count} mismatches; {launches} lift-offs, {cuts} cuts ({releaseTicksCompared} release ticks compared)");
            foreach (var m in mismatches.Take(3)) output.WriteLine("  " + m);
            Assert.True(compared > 300);
            Assert.True(launches >= 6 && cuts >= 4, $"client {i}: {launches} lift-offs, {cuts} cuts");
            Assert.True(releaseTicksCompared >= 3, $"client {i}: {releaseTicksCompared} release ticks compared");
            Assert.Empty(mismatches);
        }
    }
}
