using System.Numerics;
using SmNet;
using SmServer;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// sim/stomp-release (the owner, 2026-11-14: "allow a devastator/havoc player in both game modes to be able to act after a stomp move (F) 1.2 seconds into the animation. shortly after the
/// stomp hits and triggers, the bolt pistol should be able to fire as an example"): the in-place stomp ends 1.2 s into its clip (tick 36 at 30 Hz, after its hit at 0.767 s); from that tick fire,
/// aim, move, switch, reload, throw and melee work as from rest; before it (tick 35, 1.167 s) sim/stomp-uninterruptible still holds and only the hop cancels. Both modes; other classes' attacks
/// unchanged. Synthetic attacks and weapons (<see cref="StompUninterruptibleTests"/>' kit).
/// </summary>
public class StompReleaseTests(ITestOutputHelper output)
{
    static readonly SimConfig Config = StompUninterruptibleTests.Config with { StompRelease = true };
    static readonly SimConfig MusouConfig = Config with { Extras = ContentExtras.Musou };

    static PlayerState Fresh(SimConfig c) => Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Team = Team.SpaceMarines, Class = 0 }, c);

    static (PlayerState S, SelfEvents E) Step(SimConfig c, PlayerState s, InputButtons b, float my = 0f)
    {
        var next = Combat.StepPlayer(s, new PlayerInput(0, 0f, my, 0f, b), c, out var e);
        return (next, e);
    }

    /// <summary>The state after the press tick and <paramref name="k"/> more ticks of buttons-free stomp (tick k = k/30 s into the clip); optionally with the pistol in hand.</summary>
    static PlayerState StompAt(SimConfig c, int k, bool pistol = false)
    {
        var s = Fresh(c);
        if (pistol) s = s with { Slot = WeaponSlot.Pistol, ReadyTicks = 300 };
        for (int i = 0; i < 5; i++) s = Step(c, s, InputButtons.None).S;
        s = Step(c, s, InputButtons.MeleeHeavy).S;
        for (int i = 0; i < k; i++) s = Step(c, s, InputButtons.None).S;
        return s;
    }

    public static TheoryData<bool> Modes => new() { false, true };

    [Theory, MemberData(nameof(Modes))]
    public void The_stomp_ends_at_1_2_s_not_at_the_clips_end(bool musou)
    {
        var c = musou ? MusouConfig : Config;
        var s = StompAt(c, 34);
        Assert.Equal((1, true), (s.MeleeStep, s.MeleeHeavy));      // 1.133 s: still locked
        s = Step(c, s, InputButtons.None).S;
        Assert.Equal(1, s.MeleeStep);                                // tick 35, 1.167 s
        s = Step(c, s, InputButtons.None).S;
        output.WriteLine($"tick 36: step {s.MeleeStep}");
        Assert.Equal(0, s.MeleeStep);                                // tick 36, 1.2 s: released (the clip runs to 1.667 s)
        Assert.True(StompUninterruptibleTests.Stomp.HitTime < StompTuning.ReleaseAt);
    }

    [Theory, MemberData(nameof(Modes))]
    public void A_pistol_shot_at_1_2_s_fires_and_at_1_17_s_does_not(bool musou)
    {
        var c = musou ? MusouConfig : Config;
        var before = StompAt(c, 34, pistol: true);                  // the state entering tick 35 (1.167 s)
        var (still, e35) = Step(c, before, InputButtons.Fire);      // a press at 1.17 s: no shot, the stomp runs on (protocol 51)
        Assert.False(e35.Fired);
        Assert.Equal(1, still.MeleeStep);
        var (freed, e36) = Step(c, Step(c, before, InputButtons.None).S, InputButtons.Fire);   // a press at tick 36 (1.2 s)
        output.WriteLine($"tick 36 with Fire: fired {e36.Fired}, step {freed.MeleeStep}");
        Assert.Equal(0, freed.MeleeStep);
        Assert.True(e36.Fired);
    }

    [Theory, MemberData(nameof(Modes))]
    public void The_heavy_bolter_fires_and_aim_and_move_work_from_1_2_s(bool musou)
    {
        var c = musou ? MusouConfig : Config;
        var at35 = StompAt(c, 35);
        Assert.True(Step(c, at35, InputButtons.Fire).E.Fired);
        Assert.True(Step(c, at35, InputButtons.Aim).S.Aiming);
        var moved = Step(c, at35, InputButtons.None, my: 1f).S;
        Assert.Equal(0, moved.MeleeStep);
        Assert.True(moved.Velocity.Length() > 0.1f);
        Assert.True(Step(c, at35, InputButtons.Switch).S.SwitchLeft > 0f);
        Assert.True(Step(c, at35, InputButtons.Grenade).S.Throwing);
        // Before it: none of them (tick 35 entered from 34).
        var at34 = StompAt(c, 34);
        var (n, e) = Step(c, at34, InputButtons.Fire | InputButtons.Aim);
        Assert.False(e.Fired); Assert.False(n.Aiming); Assert.Equal(1, n.MeleeStep);
        Assert.Equal(1, Step(c, at34, InputButtons.None, my: 1f).S.MeleeStep);
    }

    [Fact]
    public void A_melee_press_at_1_2_s_starts_the_next_attack()
    {
        // The press tick itself replays from the buffer or starts at once; either way the stomp is gone and a swing runs within two ticks.
        var s = StompAt(Config, 35);
        s = Step(Config, s, InputButtons.Melee).S;
        s = Step(Config, s, InputButtons.None).S;
        Assert.True(s.MeleeStep == 1 && !s.MeleeHeavy, $"step {s.MeleeStep} heavy {s.MeleeHeavy}");
    }

    [Fact]
    public void The_hop_still_cancels_earlier()
    {
        var s = StompAt(Config, 13);
        int hopAt = -1;
        for (int k = 14; k < 36 && hopAt < 0; k++)
        {
            s = Step(Config, s, k % 2 == 1 ? InputButtons.Jump : InputButtons.None).S;
            if (s.EvadeTicks > 0) hopAt = k;
        }
        output.WriteLine($"hop at stomp tick {hopAt}");
        Assert.InRange(hopAt, 15, 17);
        Assert.Equal(0, s.MeleeStep);
    }

    [Fact]
    public void Other_attacks_are_unchanged_and_the_switch_restores_the_old_lock()
    {
        // A kick (not in place) is not released at 1.2 s: it plays its 1.667 s... its own end, as before.
        var c = Config;
        var s = Fresh(c);
        for (int i = 0; i < 5; i++) s = Step(c, s, InputButtons.None).S;
        s = Step(c, s, InputButtons.Melee).S;
        for (int k = 0; k < 40; k++) s = Step(c, s, InputButtons.None).S;   // 1.333 s: past 1.2, the kick's duration 1.667 s runs on
        Assert.Equal(1, s.MeleeStep);
        // The restore switch: the stomp holds to its clip's end (51 ticks).
        var old = c with { StompRelease = false };
        var o = StompAt(old, 40);
        Assert.Equal(1, o.MeleeStep);
        Assert.NotEqual(Config.Hash(), old.Hash());
    }

    [Theory]
    [InlineData(901)]
    [InlineData(902)]
    public void Prediction_matches_the_server_through_the_stomp_release_under_150ms_rtt_and_5pct_loss(int seed)
    {
        var config = Config with { InputBufferWindow = 0.3f };
        var room = JumpPackFeelTests.Room;
        var map = new GameMap("stomp", room, [new SpawnPoint(new Vector3(24, 0, 6), MathF.PI / 2f), new SpawnPoint(new Vector3(24, -40, 6), MathF.PI / 2f)], 92);
        var link = new LinkConditions(TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(10), 0.05);
        var h = new Harness(link, seed, 0, config, new ServerOptions { PeerTimeout = TimeSpan.FromMinutes(5), BodyGuard = false }, map: map);
        for (int i = 0; i < 2; i++) h.AddClient(nonce: (uint)(910 + i), options: new ClientOptions { RequestedClass = 0, LoadMap = _ => new LoadedMap(room, 92) });
        var truth = new SortedDictionary<uint, Dictionary<int, PlayerState>>();
        var predicted = Enumerable.Range(0, 2).Select(_ => new Dictionary<uint, PlayerState>()).ToArray();
        int[] after = [34, 35, 36, 37, 40];   // the press tick after the F: before, at and after the 1.2 s release
        PlayerInput Input(int i, uint t)
        {
            uint cycle = (t / 100 + (uint)i) % 5, phase = t % 100;
            var b = phase == 0 ? InputButtons.MeleeHeavy : InputButtons.None;
            if (phase == 1 + (uint)after[cycle]) b |= cycle % 2 == 0 ? InputButtons.Fire : InputButtons.Aim;
            if (phase == 2 + (uint)after[cycle] && cycle == 4) b |= InputButtons.Jump;
            if (phase == 20 && cycle == 3) b |= InputButtons.Jump;   // a hop out of the stomp
            return new PlayerInput(t, 0f, phase > 38 + cycle && phase < 60 ? 1f : 0f, MathF.PI / 2f, b, -0.2f);
        }
        h.Run(TimeSpan.FromSeconds(20), Input, () =>
        {
            truth[h.Server.World.Tick] = new Dictionary<int, PlayerState>(h.Server.World.Players);
            for (int i = 0; i < 2; i++)
                if (h.Clients[i].Status == ClientStatus.Connected) predicted[i][h.Clients[i].Tick] = h.Clients[i].Predicted;
        });
        for (int i = 0; i < 2; i++)
        {
            int id = h.Clients[i].PlayerId, compared = 0;
            var mismatches = new List<string>();
            uint from = truth.Keys.First() + 60;
            foreach (var (tick, pred) in predicted[i])
            {
                if (tick < from || !truth.TryGetValue(tick, out var world) || !world.TryGetValue(id, out var real)) continue;
                compared++;
                var norm = pred with { Health = real.Health, Shield = real.Shield, SinceDamage = real.SinceDamage, DamageCount = real.DamageCount, DamageFresh = real.DamageFresh, DamageShield = real.DamageShield, DamageHealth = real.DamageHealth };
                if (norm != real) mismatches.Add($"tick {tick}: " + string.Join(", ", typeof(PlayerState).GetProperties().Where(pr => !Equals(pr.GetValue(norm), pr.GetValue(real))).Select(pr => $"{pr.Name} {pr.GetValue(pred)} vs {pr.GetValue(real)}")));
            }
            var mine = truth.Values.Where(w => w.ContainsKey(id)).Select(w => w[id]).ToList();
            int stomps = mine.Count(p => p.MeleeStep == 1 && p.MeleeHeavy);
            output.WriteLine($"client {i}: compared {compared}, {mismatches.Count} mismatches, {stomps} stomp ticks");
            foreach (var m in mismatches.Take(3)) output.WriteLine("  " + m);
            Assert.True(compared > 300);
            Assert.True(stomps > 100, "the script stomped");
            Assert.Empty(mismatches);
        }
    }
}
