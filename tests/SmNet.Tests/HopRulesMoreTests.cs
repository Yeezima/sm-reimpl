using System.Numerics;
using SmNet;
using SmServer;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// sim/hop-rules review follow-ups (protocol 72): the pack's fuel after the ledge jump and the tackle-cancel hop, no second air jump, classes and weapons the rules do not touch, and the own client
/// predicting <see cref="PlayerState.JumpAirOpen"/> (and the tackle cancel) exactly under 150 ms RTT and 5 % loss.
/// </summary>
public class HopRulesMoreTests(ITestOutputHelper output)
{
    static readonly SimConfig Classic = MeleeCancelTests.Pack;
    static PlayerState Step(PlayerState s, SimConfig c, InputButtons b, float mx = 0f, float my = 0f) => Combat.StepPlayer(s, new PlayerInput(0, mx, my, 0f, b), c, out _);

    static PlayerState ChargedOffALedge(SimConfig c)
    {
        var s = MeleeCancelTests.Charging(c, cls: 0) with { Position = new Vector3(0f, 0f, 8f), Grounded = false };
        return Step(s, c, InputButtons.None);
    }

    [Fact]
    public void A_take_off_pressed_just_before_the_edge_lifts_off_in_the_air_instead_of_falling()
    {
        foreach (bool rules in new[] { true, false })
        {
            var c = Classic with { HopRules = rules };
            var s = MeleeCancelTests.Fresh(c);
            for (int k = 0; k < 3; k++) s = Step(s, c, InputButtons.Jump);
            Assert.Equal(JumpPhase.TakeOff, s.Jump);
            s = s with { Grounded = false, Position = new Vector3(0f, 0f, 6f) };   // the body ran off the edge during the take-off
            var phases = new List<JumpPhase>();
            for (int k = 0; k < 16; k++) { s = Step(s, c, InputButtons.Jump); phases.Add(s.Jump); }
            Assert.Equal(rules, phases.Contains(JumpPhase.Ascent));
        }
    }

    [Fact]
    public void The_ledge_jump_spends_the_packs_fuel_as_any_jump_does()
    {
        var c = Classic with { JumpPacks = [Classic.JumpPacks[0] with { MaxPower = 100f, RechargeRate = 0f }] };
        var s = ChargedOffALedge(c) with { JumpPower = 100f };
        s = Step(s, c, InputButtons.Jump);
        Assert.Equal(100f, s.JumpPower);   // the press alone spends nothing: the hop or the jump is decided later
        for (int k = 0; k < 6; k++) s = Step(s, c, InputButtons.Jump);
        Assert.Equal(JumpPhase.Ascent, s.Jump);
        Assert.True(s.JumpPower < 100f, $"power {s.JumpPower}");
        var empty = ChargedOffALedge(c) with { JumpPower = 0f };
        for (int k = 0; k < 14; k++) empty = Step(empty, c, InputButtons.Jump);
        Assert.NotEqual(JumpPhase.Ascent, empty.Jump);   // no fuel, no jump (the hop is free)
    }

    [Fact]
    public void The_hop_that_cancels_the_tackle_costs_no_fuel()
    {
        var c = JumpPackAttackTests.Config;
        var s = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = 0 }, c);
        s = Step(s, c, InputButtons.Jump, 0f, 1f);
        s = Step(s, c, InputButtons.None, 0f, 1f);
        while (s.EvadeTicks < 10) s = Step(s, c, InputButtons.None);
        s = Step(s, c, InputButtons.Melee);
        Assert.Equal(RushPhase.DashAttack, s.Rush);
        float before = s.JumpPower;
        var hop = Step(s, c, InputButtons.Jump, my: 1f);
        Assert.True(Evade.Evading(hop));
        Assert.True(hop.JumpPower >= before, $"fuel {before} -> {hop.JumpPower}");
    }

    [Fact]
    public void A_second_long_press_after_the_ledge_jump_is_refused_in_classic_but_allowed_in_musou()
    {
        int Launches(SimConfig c)
        {
            var s = ChargedOffALedge(c);
            var phases = new List<JumpPhase>();
            for (int k = 0; k < 14; k++) { s = Step(s, c, InputButtons.Jump); phases.Add(s.Jump); }
            int guard = 0;
            s = s with { Position = s.Position + new Vector3(0f, 0f, 30f) };   // high up, so the rest happens in the air
            for (int k = 0; k < 2; k++) { s = Step(s, c, InputButtons.None); phases.Add(s.Jump); }
            while (s.Jump is not (JumpPhase.Hang or JumpPhase.Fall) && guard++ < 200) { s = Step(s, c, InputButtons.None); phases.Add(s.Jump); }
            for (int k = 0; k < 14; k++) { s = Step(s, c, InputButtons.Jump); phases.Add(s.Jump); }
            return MeleeCancelTests.Launches(phases);
        }
        Assert.Equal(1, Launches(Classic));
        if (ContentExtras.Compiled) Assert.True(Launches(Classic with { Extras = ContentExtras.Musou }) >= 2);
    }

    /// <summary>Whether two runs of the same input script give the same state each tick with the rules on and off.</summary>
    static void SameWithAndWithoutTheRules(SimConfig c, int cls, (InputButtons B, float Mx, float My)[] script, bool charge = false)
    {
        var off = c with { HopRules = false };
        var a = MeleeCancelTests.Fresh(c, cls);
        var b = MeleeCancelTests.Fresh(off, cls);
        for (int k = 0; k < script.Length; k++)
        {
            a = Step(a, c, script[k].B, script[k].Mx, script[k].My);
            b = Step(b, off, script[k].B, script[k].Mx, script[k].My);
            Assert.True(a == b, $"tick {k}: with {a}; without {b}");
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Classes_without_a_jump_pack_are_unaffected(int cls)
    {
        // Tactical (class 0) and Devastator (class 1) of RushTests' spec: swings, a hop or roll, a charge held in the air, Jump presses throughout.
        var script = new List<(InputButtons, float, float)>();
        InputButtons[] cycle = [InputButtons.Melee, InputButtons.None, InputButtons.Jump, InputButtons.None, InputButtons.Melee, InputButtons.None, InputButtons.Jump, InputButtons.Jump,
            InputButtons.Sprint | InputButtons.MeleeHeavy, InputButtons.None, InputButtons.Jump, InputButtons.None];
        for (int r = 0; r < 12; r++) foreach (var b in cycle) { script.Add((b, 0f, 1f)); script.Add((InputButtons.None, 0f, 0f)); script.Add((InputButtons.None, 0f, 0f)); }
        SameWithAndWithoutTheRules(MeleeCancelTests.Heavy, cls, [.. script]);
        var air = MeleeCancelTests.Charging(MeleeCancelTests.Heavy, 0) with { Position = new Vector3(0f, 0f, 8f), Grounded = false };
        var off = MeleeCancelTests.Heavy with { HopRules = false };
        var x = Step(air, MeleeCancelTests.Heavy, InputButtons.Jump);
        var y = Step(air, off, InputButtons.Jump);
        Assert.Equal(x, y);
        Assert.False(x.JumpAirOpen);
    }

    [Fact]
    public void The_hammers_hop_swing_starts_at_every_point_of_the_hop_as_without_the_rules()
    {
        var c = HammerHopSlamTests.Pack();
        var off = c with { HopRules = false };
        foreach (int ticks in new[] { 1, 5, 9, 13, 17, 23 })
            foreach (var team in new[] { Team.SpaceMarines, Team.Chaos })
            {
                var a = Step(HammerHopSlamTests.HopFor(c, team, ticks), c, InputButtons.Melee);
                var b = Step(HammerHopSlamTests.HopFor(off, team, ticks), off, InputButtons.Melee);
                Assert.Equal(b, a);
                Assert.Equal(1, a.MeleeStep);
            }
    }

    // ---- prediction ----

    [Theory]
    [InlineData(811)]
    [InlineData(812)]
    public void Prediction_matches_the_server_through_the_ledge_jump_and_the_tackle_cancel_under_150ms_rtt_and_5pct_loss(int seed)
    {
        var pack = JumpPackAttackTests.Config.JumpPacks[0] with { HoldTiers = true, RechargeRate = 100f, MaxPower = 100f };
        var config = JumpPackAttackTests.Config with
        {
            Classes = [JumpPackAttackTests.Config.Classes[0] with { MeleeRush = RushTests.Spec }, JumpPackAttackTests.Config.Classes[1]],
            JumpPacks = [pack], InputBufferWindow = 0.3f,
            RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f,
        };
        var room = JumpPackFeelTests.Room;
        var map = new GameMap("hop", room, [new SpawnPoint(new Vector3(24, 0, 6), MathF.PI / 2f), new SpawnPoint(new Vector3(24, -40, 6), MathF.PI / 2f)], 92);
        var link = new LinkConditions(TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(10), 0.05);
        var h = new Harness(link, seed, 0, config, new ServerOptions { PeerTimeout = TimeSpan.FromMinutes(5), BodyGuard = false }, map: map);
        for (int i = 0; i < 2; i++) h.AddClient(nonce: (uint)(820 + i), options: new ClientOptions { RequestedClass = 0, LoadMap = _ => new LoadedMap(room, 92) });
        var truth = new SortedDictionary<uint, Dictionary<int, PlayerState>>();
        var predicted = Enumerable.Range(0, 2).Select(_ => new Dictionary<uint, PlayerState>()).ToArray();
        int[] holds = [1, 9, 14, 6];
        PlayerInput Input(int i, uint t)
        {
            // t < 120: the charge off the plateau's edge (x = 20, facing -X) and a Space press (a tap, or a hold) as the body leaves it; then, on the floor, cycles of
            // dash, tackle, Space in it (the hop that cancels it), a swing and a hop, with the press ticks shifted per client.
            if (t < 120)
            {
                var b = t < 3 ? InputButtons.None : InputButtons.Sprint | InputButtons.MeleeHeavy;
                if (t >= (i == 0 ? 17u : 34u) && t < (i == 0 ? 17u : 34u) + holds[(int)(i * 2 + 1) % holds.Length]) b |= InputButtons.Jump;
                return new PlayerInput(t, 0f, 1f, MathF.PI / 2f, b, -0.2f);
            }
            uint phase = (t + (uint)i * 7) % 75;
            var buttons = phase switch { 0 => InputButtons.Jump, 11 or 12 => InputButtons.Melee, 17 or 18 => InputButtons.Jump, 30 or 31 => InputButtons.Melee, 36 => InputButtons.Jump, 50 or 51 => InputButtons.Melee, _ => InputButtons.None };
            return new PlayerInput(t, 0f, phase < 20 ? 1f : 0f, MathF.PI / 2f + MathF.PI * ((t / 75) % 2), buttons, -0.2f);
        }
        h.Run(TimeSpan.FromSeconds(20), Input, () =>
        {
            truth[h.Server.World.Tick] = new Dictionary<int, PlayerState>(h.Server.World.Players);
            for (int i = 0; i < 2; i++)
                if (h.Clients[i].Status == ClientStatus.Connected) predicted[i][h.Clients[i].Tick] = h.Clients[i].Predicted;
        });
        bool anyOpen = false;
        for (int i = 0; i < 2; i++)
        {
            int id = h.Clients[i].PlayerId, compared = 0;
            var mismatches = new List<string>();
            uint from = truth.Keys.First() + 60;
            foreach (var (tick, pred) in predicted[i])
            {
                if (tick < from || !truth.TryGetValue(tick, out var world) || !world.TryGetValue(id, out var real)) continue;
                compared++;
                var norm = pred with { Health = real.Health, Shield = real.Shield, SinceDamage = real.SinceDamage, DamageCount = real.DamageCount, DamageFresh = real.DamageFresh, DamageShield = real.DamageShield, DamageHealth = real.DamageHealth };   // hits are not predicted
                if (norm != real) mismatches.Add($"tick {tick}: " + string.Join(", ", typeof(PlayerState).GetProperties().Where(pr => !Equals(pr.GetValue(norm), pr.GetValue(real))).Select(pr => $"{pr.Name} {pr.GetValue(pred)} vs {pr.GetValue(real)}")));
            }
            var mine = truth.Values.Where(w => w.ContainsKey(id)).Select(w => w[id]).ToList();
            anyOpen |= mine.Any(p => p.JumpAirOpen);
            output.WriteLine("early: " + string.Join(" | ", mine.Take(60).Where((_, n) => n % 3 == 0).Select(p => $"{p.Position.X:F1},{p.Position.Z:F1} {p.Rush} {p.Jump}")));
            int dash = mine.Count(p => Rush.IsDash(p.Rush));
            output.WriteLine($"client {i}: compared {compared}, {mismatches.Count} mismatches; {mine.Count(p => p.JumpAirOpen)} ticks with the air jump open, {dash} dash-attack ticks");
            foreach (var m in mismatches.Take(3)) output.WriteLine("  " + m);
            Assert.True(compared > 300);
            Assert.Empty(mismatches);
        }
        Assert.True(anyOpen, "the script reached the open air jump");
    }
}
