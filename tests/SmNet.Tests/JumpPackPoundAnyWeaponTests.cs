using System.Numerics;
using SmNet;
using SmServer;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// sim/pound-any-weapon (protocol 73; the owner 2026-11-14: "power sword and chainsword both cannot ground pound immediately after a full power jump ... only after reaching the apex"):
/// the pack's 2 s <c>smash_available_delay</c> (Air-Cooled Thrusters) held every weapon but the hammers back; now a melee press in the ascent pounds as soon as the feet are 8 m above the ground (the slash's
/// height), and slashes below it, whatever melee weapon the Assault Marine or Raptor carries. Synthetic specs shaped like the install's; no original bytes.
/// </summary>
public class JumpPackPoundAnyWeaponTests(ITestOutputHelper output)
{
    const int Assault = 0;

    static readonly JumpPackSpec Pack = JumpPackTests.Pack with { AirSpeed = 8f, AirAcceleration = 15f, Slash = JumpPackFeelTests.Snappy, MaxPower = 300f, PoundDelay = 2f };   // the perk's delay on
    static readonly SimConfig Base = JumpPackTests.Config with { JumpPacks = [Pack], RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f };

    static SimConfig With(string wargear) =>
        Base with { Classes = Base.Classes.Select(k => k with { MeleeChoices = [new MeleeCombo(wargear, k.Melee)] }).ToArray() };

    static PlayerState Fresh(SimConfig c) => Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = Assault }, c);

    static PlayerState Step(PlayerState s, SimConfig c, InputButtons b, float pitch = 0f) => Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, b, pitch), c, out _, null);

    /// <summary>The states of a full jump (Jump held 13 ticks), the state before each tick paired with it: the press of tick k decides on the state before it.</summary>
    static List<PlayerState> FullJump(SimConfig c)
    {
        var s = Fresh(c);
        var states = new List<PlayerState> { s };
        for (int k = 0; k < 60; k++) { s = Step(s, c, k < 13 ? InputButtons.Jump : InputButtons.None); states.Add(s); }
        return states;
    }

    public static IEnumerable<object[]> Weapons() => new[]
    {
        "sm_mp_melee_power_sword", "sm_mp_melee_chainsword", "sm_mp_melee_2hand_thunder_hammer", "csm_mp_melee_2hand_power_mace", "csm_mp_melee_chainsword",
        "sm_mp_melee_power_axe", "csm_mp_melee_chain_axe", "sm_mp_melee_combat_knife",
    }.Select(w => new object[] { w });

    [Theory]
    [MemberData(nameof(Weapons))]
    public void A_melee_press_in_the_ascent_pounds_just_above_the_height_and_slashes_just_below(string wargear)
    {
        var c = With(wargear);
        var states = FullJump(c);
        // Ticks of the ascent after the lift-off whose start is under / over 8 m (the height the press decides on).
        int above = states.FindIndex(s => s.Jump == JumpPhase.Ascent && s.Position.Z >= 8f);
        int below = states.FindLastIndex(above, s => s.Jump == JumpPhase.Ascent && s.Position.Z < 8f);
        Assert.True(above > 0 && below > 0, "the jump climbs through 8 m while rising");
        Assert.True(above - below == 1 || states[above].Position.Z - states[below].Position.Z < 2f);
        Assert.Equal(JumpPhase.Ascent, states[above].Jump);   // the ascent, not the hang

        var pound = Step(states[above], c, InputButtons.Melee, pitch: -1.2f);
        Assert.Equal(JumpPhase.Dive, pound.Jump);
        Assert.False(pound.JumpSlash);

        var slash = Step(states[below], c, InputButtons.Melee, pitch: -1.2f);
        Assert.Equal(JumpPhase.Dive, slash.Jump);
        Assert.True(slash.JumpSlash);
        output.WriteLine($"{wargear}: pounds from {states[above].Position.Z:F2} m at jump tick {states[above].JumpTicks}, slashes at {states[below].Position.Z:F2} m (the 2 s delay is {Loadouts.JumpPack(c, Fresh(c))!.PoundDelay} s)");
    }

    [Theory]
    [MemberData(nameof(Weapons))]
    public void CanPound_agrees_in_the_ascent_above_the_height_for_every_weapon(string wargear)
    {
        var c = With(wargear);
        var states = FullJump(c);
        int above = states.FindIndex(s => s.Jump == JumpPhase.Ascent && s.Position.Z >= 8f);
        var pack = Loadouts.JumpPack(c, states[above])!;
        Assert.Equal(0f, pack.PoundDelay);
        Assert.True(JumpPack.CanPound(states[above], pack, c));
        Assert.True(JumpPack.CanPound(states[above], pack, c, null));
    }

    [Fact]
    public void The_pound_is_unchanged_by_the_rule_damage_speed_and_gravity_are_the_packs()
    {
        var c = With("sm_mp_melee_power_sword");
        var pack = Loadouts.JumpPack(c, Fresh(c))!;
        Assert.Equal((75f, 50f, 50f), (pack.PoundGravity, pack.PoundSpeed, pack.PoundLimitAngle));
        Assert.Equal(Pack.Pound, pack.Pound);
        Assert.Equal(8f, pack.Slash!.MinPoundHeight);
    }

    [Theory]
    [InlineData(751)]
    [InlineData(752)]
    public void Prediction_matches_the_server_with_early_ascent_pounds_under_150ms_rtt_and_5pct_loss(int seed)
    {
        var config = With("sm_mp_melee_power_sword") with { JumpPacks = [Pack with { RechargeRate = 100f }] };
        var map = new GameMap("pound", JumpPackFeelTests.Room, [new SpawnPoint(new Vector3(0, 3, 0), 0f), new SpawnPoint(new Vector3(-6, 3, 0), 0f)], 91);
        var link = new LinkConditions(TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(10), 0.05);
        var h = new Harness(link, seed, 0, config, new ServerOptions { PeerTimeout = TimeSpan.FromMinutes(5) }, map: map);
        for (int i = 0; i < 2; i++) h.AddClient(nonce: (uint)(750 + i), options: new ClientOptions { RequestedClass = Assault, LoadMap = _ => new LoadedMap(JumpPackFeelTests.Room, 91) });
        var truth = new SortedDictionary<uint, Dictionary<int, PlayerState>>();
        var predicted = Enumerable.Range(0, 2).Select(_ => new Dictionary<uint, PlayerState>()).ToArray();
        PlayerInput Input(int i, uint t)
        {
            // A full jump every 60 ticks, a melee press on a tick of the climb that moves with the cycle (7 .. 22 ticks after the press of Jump): slashes low, pounds from 8 m.
            uint cycle = (t + (uint)i * 11) / 60, phase = (t + (uint)i * 11) % 60;
            var buttons = phase < 13 ? InputButtons.Jump : InputButtons.None;
            if (phase == 8 + cycle % 12) buttons |= InputButtons.Melee;
            return new PlayerInput(t, 0f, 0f, 0.2f * i, buttons, -0.6f - 0.1f * (cycle % 6));
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
            // A pound that started in the ascent (the jump's rise still going, the delay of 2 s far off), not the slash.
            int early = mine.Zip(mine.Skip(1)).Count(p => p.First.Jump == JumpPhase.Ascent && p.Second.Jump == JumpPhase.Dive && !p.Second.JumpSlash);
            int slashes = mine.Zip(mine.Skip(1)).Count(p => JumpPack.InFlight(p.First.Jump) && p.Second.Jump == JumpPhase.Dive && p.Second.JumpSlash);
            output.WriteLine($"client {i}: compared {compared}, {mismatches.Count} mismatches; {early} ascent pounds, {slashes} slashes");
            foreach (var m in mismatches.Take(3)) output.WriteLine("  " + m);
            Assert.True(compared > 300);
            Assert.True(early >= 2 && slashes >= 2, $"client {i}: {early} ascent pounds, {slashes} slashes");
            Assert.Empty(mismatches);
        }
    }
}
