using System.Numerics;
using SmSim;

namespace SmNet.Tests;

/// <summary>
/// weapons/spread (the owner's decision 2026-10-28): the data's scatter per stance, the bloom, perk sets, the deterministic draw shared by
/// the server and the client's prediction, the cone clamp, bursts and the config hash. Synthetic sets (shaped like the install's bolter and
/// stalker sets; the reader itself is tested in SmFormats.Tests.SpreadTuningTests).
/// </summary>
public class SpreadTests
{
    const string Targeter = "u_targeter", Burst = "u_burst";

    static SpreadSet Set(string name, float initial, float increase, float max, float decrease = 2.4f, float delay = 0.1f, float climb = 0.3f) =>
        new(name, new ScatterSet(initial, initial, increase, max, max, decrease, delay),
            HandlingSet.None with { TemporaryClimbPerShot = climb, MaxTemporaryClimb = 0.65f, ClimbRate = 50f, ClimbRecoveryRate = 1.5f });

    /// <summary>Hip (standing) sets for idle / walking / running, then aim mode's.</summary>
    static SpreadSpec Spec(SpreadSet hip, SpreadSet aim, SpreadSet? running = null) =>
        new(new SpreadStances([hip, hip, running ?? hip, aim, aim, aim]), []);

    static readonly SpreadSpec BolterSpread = Spec(Set("hip", 0.25f, 0.35f, 1.5f), Set("aim", 0.075f, 0.3f, 1.5f), Set("run", 1f, 0.35f, 3f));
    static readonly SpreadSpec TargeterSpread = Spec(Set("hip_perk", 0.125f, 0.21f, 1.5f, 2.6f), Set("aim_perk", 0.03f, 0.17f, 1.5f, 2.6f));

    static WeaponSpec Gun(string name, TriggerKind trigger, float cooldown) =>
        new(name, WeaponSlot.Ranged, trigger, 20f, cooldown, 30, 270, 1.5f, 250f, 245f, 250f, 1f, 2.5f, 1f, 0.5f, 0.5f) { HitType = DamageType.Ranged };

    static readonly WeaponSpec BolterBase = Gun("bolter", TriggerKind.FullAuto, 0.155f) with { Spread = BolterSpread };
    static readonly WeaponSpec Bolter = BolterBase with { Variants = [new([Targeter], BolterBase with { Spread = TargeterSpread })] };
    static readonly WeaponSpec StalkerBase = Gun("stalker", TriggerKind.SemiAuto, 0.267f) with { Spread = Spec(Set("s", 0.4f, 0.85f, 2.15f, 3f, 0.15f), Set("sa", 0.225f, 0.7f, 1.8f, 3f, 0.15f)) };
    static readonly WeaponSpec Stalker = StalkerBase with
    {
        Variants = [new([Burst], StalkerBase with { ShotCooldown = 0.475f, BurstLength = 2, BurstInterval = 0.1f,
            Spread = Spec(Set("burst", 0.4f, 0.6f, 2.5f, 2.5f, 0.25f), Set("aim_burst", 0.225f, 0.35f, 2.25f, 3f, 0.25f)) })],
    };

    static readonly SimConfig Config = new()
    {
        Weapons = [Bolter, Stalker],
        Perks = [new PerkSpec("mp_perk_weap_bolter_accuracy", Targeter, PerkKind.Class, 1), new PerkSpec("mp_perk_weap_stalker_zoom_modes", Burst, PerkKind.Class, 1)],
        Classes =
        [
            new ClassLoadout("tac", 0, -1, []) { PrimaryChoices = [0, 1], Side = Team.SpaceMarines, PerkChoices = [0, 1] },
            new ClassLoadout("csm", 0, -1, []) { PrimaryChoices = [0, 1], Side = Team.Chaos, PerkChoices = [0, 1] },
        ],
        HealthMax = 100f, ShieldMax = 0f, HealthRegenDelay = 1e6f, ShieldRegenDelay = 1e6f,
    };

    static PlayerState Fresh(int primary = 0, int perk = -1) =>
        Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = 0, Primary = primary, Perk1 = perk, Perk2 = -1 }, Config);

    static PlayerInput Fire(uint tick, InputButtons extra = InputButtons.None, float yaw = 0f) => new(tick, 0f, 0f, yaw, InputButtons.Fire | extra);
    static PlayerInput Idle(uint tick, InputButtons extra = InputButtons.None) => new(tick, 0f, 0f, 0f, extra);

    /// <summary>Steps <paramref name="s"/> through the inputs, returning each tick's state and whether it fired.</summary>
    static List<(PlayerState State, bool Fired)> Steps(PlayerState s, IEnumerable<PlayerInput> inputs)
    {
        var list = new List<(PlayerState, bool)>();
        foreach (var input in inputs) { s = Combat.StepPlayer(s, input, Config, out var ev); list.Add((s, ev.Fired)); }
        return list;
    }

    /// <summary>The angle between two directions (atan2 of the cross and dot products: acos near 1 cannot resolve below about 0.02 degrees in floats).</summary>
    static float AngleDegrees(Vector3 a, Vector3 b)
    {
        a = Vector3.Normalize(a); b = Vector3.Normalize(b);
        return MathF.Atan2(Vector3.Cross(a, b).Length(), Vector3.Dot(a, b)) * 180f / MathF.PI;
    }

    // ---------------------------------------------------------------- the draw

    [Fact]
    public void The_draw_is_deterministic_inside_the_unit_disc_and_uniform()
    {
        Assert.Equal(Spread.Draw(3, 1234), Spread.Draw(3, 1234));
        Assert.NotEqual(Spread.Draw(3, 1234), Spread.Draw(3, 1235));
        Assert.NotEqual(Spread.Draw(3, 1234), Spread.Draw(4, 1234));
        int inner = 0, n = 20000;
        var mean = Vector2.Zero;
        for (uint t = 0; t < n; t++)
        {
            var p = Spread.Draw(7, t);
            Assert.True(p.Length() <= 1f + 1e-6f);
            if (p.Length() < 0.5f) inner++;
            mean += p / n;
        }
        Assert.InRange(inner / (float)n, 0.23f, 0.27f);   // a disc of half the radius holds a quarter of a uniform disc
        Assert.InRange(mean.Length(), 0f, 0.02f);
    }

    // ---------------------------------------------------------------- per stance, bloom and decay

    [Fact]
    public void Hip_aim_and_running_use_their_sets()
    {
        var s = Fresh();
        Assert.Equal((0.25f, 0.25f), Spread.HalfWidths(Bolter, s));
        Assert.Equal((0.075f, 0.075f), Spread.HalfWidths(Bolter, s with { Aiming = true }));
        Assert.Equal((1f, 1f), Spread.HalfWidths(Bolter, s with { Velocity = new Vector3(0f, 6.5f, 0f) }));          // running
        Assert.Equal((0.25f, 0.25f), Spread.HalfWidths(Bolter, s with { Velocity = new Vector3(2f, 0f, 0f) }));      // walking: the hip set
        Assert.Equal(SpreadMovement.Idle, Spread.MovementOf(new Vector3(0.9f, 0f, 5f)));                            // vertical speed does not count
        Assert.Equal(SpreadMovement.Running, Spread.MovementOf(new Vector3(0f, Spread.RunSpeed, 0f)));
        Assert.Equal((0f, 0f), Spread.HalfWidths(Bolter with { Spread = null }, s));
    }

    [Fact]
    public void Each_shot_blooms_up_to_the_maximum_and_the_bloom_decays_after_the_delay()
    {
        var states = Steps(Fresh(), Enumerable.Range(1, 60).Select(t => Fire((uint)t)));
        var shots = states.Where(x => x.Fired).Select(x => x.State).ToList();
        Assert.True(shots.Count >= 10);
        // The first shot is drawn in the initial half-width; every shot's turn stays inside its half-width; the bloom reaches the cap.
        float first = MathF.Sqrt(shots[0].SpreadYaw * shots[0].SpreadYaw + shots[0].SpreadPitch * shots[0].SpreadPitch) * 180f / MathF.PI;
        Assert.InRange(first, 0f, 0.25f + 1e-4f);
        Assert.True(shots[0].SpreadBloom > 0.3f && shots[0].SpreadBloom <= 0.35f + 1e-5f);
        Assert.Equal(1.25f, shots[^1].SpreadBloom, 3);   // max 1.5 - initial 0.25
        Assert.All(shots, s => Assert.InRange(MathF.Abs(s.SpreadYaw) * 180f / MathF.PI, 0f, 1.5f + 1e-4f));
        // Released: nothing for the delay (0.1 s = 3 ticks; the first counts from the shot), then 2.4 °/s.
        var rest = Steps(shots[^1], Enumerable.Range(61, 40).Select(t => Idle((uint)t)));
        Assert.Equal(1.25f, rest[1].State.SpreadBloom, 3);
        Assert.True(rest[10].State.SpreadBloom < 1.25f - 0.5f);
        Assert.Equal(0f, rest[^1].State.SpreadBloom);
        Assert.Equal((0.25f, 0.25f), Spread.HalfWidths(Bolter, rest[^1].State));
    }

    [Fact]
    public void A_switch_or_death_clears_the_bloom()
    {
        var hot = Steps(Fresh(), Enumerable.Range(1, 10).Select(t => Fire((uint)t)))[^1].State;
        Assert.True(hot.SpreadBloom > 0f);
        var dead = Combat.StepPlayer(Combat.Killed(hot, Config), Idle(11), Config, out _);
        Assert.Equal((0f, 0f, 0f, 0f), (dead.SpreadBloom, dead.SpreadSince, dead.SpreadYaw, dead.SpreadPitch));
    }

    // ---------------------------------------------------------------- perks

    [Fact]
    public void Bolter_Targeter_switches_to_its_tighter_sets()
    {
        var plain = Fresh();
        var perk = Fresh(perk: 0);
        Assert.Equal((0.125f, 0.125f), Spread.HalfWidths(Loadouts.InHand(Config, perk), perk));
        Assert.Equal((0.03f, 0.03f), Spread.HalfWidths(Loadouts.InHand(Config, perk), perk with { Aiming = true }));
        Assert.Equal((0.25f, 0.25f), Spread.HalfWidths(Loadouts.InHand(Config, plain), plain));
        // After the same five shots the perk's bloom is smaller (0.21 a shot against 0.35).
        float Bloom(PlayerState s) => Steps(s, Enumerable.Range(1, 5).Select(t => Fire((uint)t)))[^1].State.SpreadBloom;
        Assert.True(Bloom(perk) < Bloom(plain));
        Assert.Contains(Targeter, Perks.Consumed(Config));
        Assert.Equal(PerkActivity.Active, Perks.StatusOf(Config, Config.Perks[0]).Activity);
        Assert.Equal(PerkActivity.Active, Perks.StatusOf(Config, Config.Perks[1]).Activity);
    }

    [Fact]
    public void Burst_Fire_rounds_are_drawn_apart_and_the_second_from_the_bloomed_burst_set()
    {
        var s = Fresh(primary: 1, perk: 1);
        var states = Steps(s, Enumerable.Range(1, 8).Select(t => t == 1 ? Fire((uint)t) : Idle((uint)t)));
        var rounds = states.Where(x => x.Fired).Select(x => x.State).ToList();
        Assert.Equal(2, rounds.Count);
        Assert.NotEqual((rounds[0].SpreadYaw, rounds[0].SpreadPitch), (rounds[1].SpreadYaw, rounds[1].SpreadPitch));
        Assert.Equal(0.6f, rounds[0].SpreadBloom, 4);   // the burst set's 0.6 a round (the plain stalker's is 0.85)
        Assert.Equal(1.2f, rounds[1].SpreadBloom, 4);
        // Over many pulls the second round's turns spread wider than the first's (it is drawn in 0.4 + 0.6).
        float first = 0f, second = 0f;
        for (uint pull = 0; pull < 400; pull++)
        {
            var r = Steps(s, Enumerable.Range(1, 8).Select(t => t == 1 ? Fire(pull * 100 + (uint)t) : Idle(pull * 100 + (uint)t))).Where(x => x.Fired).Select(x => x.State).ToList();
            first += MathF.Abs(r[0].SpreadYaw); second += MathF.Abs(r[1].SpreadYaw);
        }
        Assert.InRange(second / first, 2f, 3f);   // 1.0 / 0.4 = 2.5
    }

    // ---------------------------------------------------------------- server and client agree

    [Fact]
    public void The_server_shot_goes_where_the_clients_prediction_says()
    {
        var world = new World(Config, null, [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(40f, 40f, 0f), 0f)]);
        world.Spawn(1, 0);
        world.Spawn(2, 1);
        var predicted = world.Players[1];
        int checkedShots = 0;
        for (int k = 0; k < 40; k++)
        {
            uint tick = world.Tick + 1;
            var input = new PlayerInput(tick, 0f, 0f, 0.3f, InputButtons.Fire, 0.05f).WithShotDirection(Combat.AimDirection(0.32f, 0.07f));
            world.Step(id => id == 1 ? input : new PlayerInput(tick, 0f, 0f, 0f, InputButtons.None));
            predicted = Combat.StepPlayer(predicted, input with { Tick = world.Tick }, Config, out var ev);
            Assert.Equal((world.Players[1].SpreadYaw, world.Players[1].SpreadPitch, world.Players[1].SpreadBloom), (predicted.SpreadYaw, predicted.SpreadPitch, predicted.SpreadBloom));
            if (!ev.Fired) continue;
            var shot = world.LastShots.Single(x => x.Shooter == 1);
            var server = Vector3.Normalize(shot.End - shot.Origin);
            var client = Combat.ShotDirection(input, Config, predicted);
            Assert.InRange(AngleDegrees(server, client), 0f, 0.01f);
            Assert.InRange(AngleDegrees(server, Combat.ShotDirection(input, Config)), 0f, 1.5f + 1e-3f);   // off the crosshair by at most the bolter's max
            checkedShots++;
        }
        Assert.True(checkedShots >= 8);
    }

    [Fact]
    public void Carry_keeps_the_predicted_spread_across_a_snapshot_record()
    {
        var predicted = Fresh() with { SpreadBloom = 0.8f, SpreadSince = 0.2f, SpreadYaw = 0.01f, SpreadPitch = -0.004f };
        var record = Fresh() with { Clip = 12 };
        var carried = Spread.Carry(record, predicted);
        Assert.Equal((0.8f, 0.2f, 0.01f, -0.004f, 12), (carried.SpreadBloom, carried.SpreadSince, carried.SpreadYaw, carried.SpreadPitch, carried.Clip));
        // A mispredicted switch, death or loadout: the prediction's bloom belongs to something else; rest instead.
        foreach (var other in new[] { record with { Slot = WeaponSlot.Pistol }, record with { Alive = false }, record with { Primary = 1 }, record with { Class = 1 } })
        {
            var reset = Spread.Carry(other, predicted);
            Assert.Equal((0f, 0f, 0f, 0f), (reset.SpreadBloom, reset.SpreadSince, reset.SpreadYaw, reset.SpreadPitch));
        }
    }

    [Fact]
    public void A_respawn_rests_the_spread_even_for_a_player_who_was_alive()
    {
        // A round restart or a countdown respawns living players in place (Combat.Respawned on a living state): the bloom must not survive it.
        var hot = Steps(Fresh(), Enumerable.Range(1, 10).Select(t => Fire((uint)t)))[^1].State;
        Assert.True(hot.Alive && hot.SpreadBloom > 0f && hot.SpreadYaw != 0f);
        var again = Combat.Respawned(hot, Config);
        Assert.Equal((0f, 0f, 0f, 0f), (again.SpreadBloom, again.SpreadSince, again.SpreadYaw, again.SpreadPitch));
        Assert.Equal((0.25f, 0.25f), Spread.HalfWidths(Bolter, again));
    }

    [Theory]
    [InlineData(51)]
    [InlineData(52)]
    public void Prediction_draws_the_servers_scatter_through_loss_late_inputs_and_switches(int seed)
    {
        // A lossy, jittery link (75 ms each way, ±40 ms jitter: late and reordered packets, 8% loss): client 0 holds the bolter in bursts and
        // presses the switch while firing (only one weapon: refused on both sides); client 1 pulls the stalker with Burst Fire. Every predicted
        // state, spread fields included, must equal the server's at the same tick, and every fired shot's predicted direction the server's.
        var config = Config with { RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f,
            Weapons = [Bolter with { Damage = 0f }, Stalker with { Damage = 0f, Variants = [new([Burst], Stalker.Variants[0].Spec with { Damage = 0f })] }] };
        var link = new LinkConditions(TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(40), 0.08);
        var h = new Harness(link, seed, 0, config, new SmServer.ServerOptions { PeerTimeout = TimeSpan.FromMinutes(5) });
        h.AddClient(nonce: 100, options: new ClientOptions { RequestedClass = 0, RequestedPrimary = 0 });
        h.AddClient(nonce: 101, options: new ClientOptions { RequestedClass = 1, RequestedPrimary = 1, RequestedPerk1 = 1 });
        PlayerInput Input(int i, uint t)
        {
            var move = Harness.Scripted(i, t);
            var buttons = move.Buttons & ~(InputButtons.Jump | InputButtons.Sprint);
            uint phase = (t + (uint)i * 7) % 90;
            if (i == 0 && phase < 50) buttons |= InputButtons.Fire;                        // bursts of held fire (mid-burst snapshots)
            if (i == 0 && phase is 20 or 21) buttons |= InputButtons.Switch;               // a switch the simulation refuses
            if (i == 1 && phase % 25 == 0) buttons |= InputButtons.Fire;                   // stalker pulls (two-round bursts)
            if (i == 1 && phase >= 60) buttons |= InputButtons.Aim;                        // aim mode: the other set
            return move with { Buttons = buttons, Pitch = 0.4f };
        }
        var truth = new SortedDictionary<uint, Dictionary<int, PlayerState>>();
        var shots = new Dictionary<(uint, int), Vector3>();
        var predicted = new[] { new Dictionary<uint, PlayerState>(), new Dictionary<uint, PlayerState>() };
        uint seenTick = 0;
        h.Run(TimeSpan.FromSeconds(20), Input, () =>
        {
            var w = h.Server.World;
            if (w.Tick != seenTick)
            {
                seenTick = w.Tick;
                truth[w.Tick] = new Dictionary<int, PlayerState>(w.Players);
                foreach (var s in w.LastShots) if (Vector3.Distance(s.End, s.Origin) > 1f) shots[(w.Tick, s.Shooter)] = Vector3.Normalize(s.End - s.Origin);   // a shot that starts inside a body ends within centimetres: too short a line to measure a direction
            }
            for (int i = 0; i < 2; i++)
                if (h.Clients[i].Status == ClientStatus.Connected) predicted[i][h.Clients[i].Tick] = h.Clients[i].Predicted;
        });

        for (int i = 0; i < 2; i++)
        {
            var c = h.Clients[i];
            int id = c.PlayerId, compared = 0, fired = 0, directions = 0;
            var mismatches = new List<string>();
            uint from = truth.Keys.First() + 90;
            foreach (var (tick, pred) in predicted[i])
            {
                if (tick < from || !truth.TryGetValue(tick, out var world) || !world.TryGetValue(id, out var real)) continue;
                compared++;
                if (pred != real) mismatches.Add($"tick {tick}: predicted bloom {pred.SpreadBloom} turn {pred.SpreadYaw},{pred.SpreadPitch}; server {real.SpreadBloom} turn {real.SpreadYaw},{real.SpreadPitch}; since {pred.SpreadSince} vs {real.SpreadSince}; other fields equal: {(pred with { SpreadBloom = 0, SpreadSince = 0, SpreadYaw = 0, SpreadPitch = 0 }) == (real with { SpreadBloom = 0, SpreadSince = 0, SpreadYaw = 0, SpreadPitch = 0 })}");
                if (!shots.TryGetValue((tick, id), out var server)) continue;
                fired++;
                var mine = Combat.ShotDirection(Protocol.Quantize(Input(i, tick) with { Tick = tick }), config, pred);   // the server applies the quantized input
                if (AngleDegrees(server, mine) < 0.01f) directions++; else mismatches.Add($"tick {tick}: shot {AngleDegrees(server, mine):F4} degrees apart");
            }
            Assert.True(compared > 300, $"client {i}: only {compared} ticks compared");
            Assert.True(mismatches.Count == 0, $"client {i}: {mismatches.Count} of {compared} ticks differ; first: {mismatches.FirstOrDefault()}");
            Assert.True(fired > 20, $"client {i}: only {fired} shots compared");
            Assert.True(fired == directions, $"client {i}: {fired - directions} of {fired} shot directions differ; {string.Join("; ", mismatches)}");
        }
    }

    [Fact]
    public void The_scattered_shot_stays_inside_the_server_cone()
    {
        var c = Config;   // ShotConeDegrees 30
        var input = new PlayerInput(1, 0f, 0f, 0f, InputButtons.Fire).WithShotDirection(Combat.AimDirection(29f * MathF.PI / 180f, 0f));
        var shooter = Fresh() with { SpreadYaw = 0.2f, SpreadPitch = 0f };   // 11.5° further out
        var dir = Combat.ShotDirection(input, c, shooter);
        Assert.InRange(AngleDegrees(dir, Combat.AimDirection(0f, 0f)), 0f, c.ShotConeDegrees + 1e-3f);
        // Without offsets the scatter turns the view's own direction.
        var straight = Combat.ShotDirection(new PlayerInput(1, 0f, 0f, 0f, InputButtons.Fire), c, Fresh() with { SpreadYaw = 0.01f, SpreadPitch = 0.02f });
        Assert.InRange(AngleDegrees(straight, Combat.AimDirection(0f, 0f)), 1.27f, 1.29f);   // atan(hypot(tan 0.01, tan 0.02))
    }

    [Fact]
    public void A_round_respawn_is_read_from_the_match_states()
    {
        // World.StepMatch: warm-up or an ended match into the countdown, an ended match straight into play, Seize Ground's warm-up straight into play.
        Assert.True(SpreadReconcile.RoundRespawned(MatchState.Warmup, MatchState.Countdown, GameMode.Annihilation));
        Assert.True(SpreadReconcile.RoundRespawned(MatchState.Ended, MatchState.Countdown, GameMode.Annihilation));
        Assert.True(SpreadReconcile.RoundRespawned(MatchState.Ended, MatchState.Playing, GameMode.Annihilation));
        Assert.True(SpreadReconcile.RoundRespawned(MatchState.Warmup, MatchState.Playing, GameMode.SeizeGround));
        Assert.False(SpreadReconcile.RoundRespawned(MatchState.Warmup, MatchState.Playing, GameMode.Annihilation));   // Annihilation plays on from warm-up
        Assert.False(SpreadReconcile.RoundRespawned(MatchState.Countdown, MatchState.Playing, GameMode.Annihilation)); // already respawned at the countdown
        Assert.False(SpreadReconcile.RoundRespawned(MatchState.Countdown, MatchState.Countdown, GameMode.Annihilation));
        Assert.False(SpreadReconcile.RoundRespawned(MatchState.Playing, MatchState.Ended, GameMode.Annihilation));
        // Choose: a round respawn and a missing prediction rest; otherwise the prediction's bloom is kept.
        var record = Fresh();
        var warm = record with { SpreadBloom = 1f, SpreadSince = 0.1f, SpreadYaw = 0.01f };
        Assert.Equal(0f, SpreadReconcile.Choose(record, warm, true, out var o1).SpreadBloom); Assert.Equal(SpreadReconcile.Outcome.RoundRespawn, o1);
        Assert.Equal(0f, SpreadReconcile.Choose(record, null, false, out var o2).SpreadBloom); Assert.Equal(SpreadReconcile.Outcome.NoHistory, o2);
        Assert.Equal(1f, SpreadReconcile.Choose(record, warm, false, out var o3).SpreadBloom); Assert.Equal(SpreadReconcile.Outcome.Carried, o3);
        Assert.Equal(0f, SpreadReconcile.Choose(record with { Slot = WeaponSlot.Pistol }, warm, false, out var o4).SpreadBloom); Assert.Equal(SpreadReconcile.Outcome.Mismatch, o4);
    }

    static readonly SimConfig NetConfig = Config with
    {
        RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f,
        Weapons = [Bolter with { Damage = 0f }, Stalker with { Damage = 0f, Variants = [new([Burst], Stalker.Variants[0].Spec with { Damage = 0f })] }],
    };

    [Fact]
    public void After_a_stall_the_replay_starts_from_rest_and_never_counts_shots_twice()
    {
        // The client stops for 0.4 s while holding the trigger; the server repeats its last input and keeps firing. The first snapshot after the
        // stall is ahead of the client (no prediction for its tick): the replay starts from rest, so the predicted bloom may lag the server's but
        // never exceeds it, and it catches up once both saturate.
        var h = new Harness(new LinkConditions(TimeSpan.FromMilliseconds(30), TimeSpan.Zero, 0), 7, 0, NetConfig, new SmServer.ServerOptions { PeerTimeout = TimeSpan.FromMinutes(5) });
        var a = h.AddClient(nonce: 100, options: new ClientOptions { RequestedClass = 0, RequestedPrimary = 0 });
        h.AddClient(nonce: 101, options: new ClientOptions { RequestedClass = 1 });
        PlayerInput Hold(int client, uint tick) => new(tick, 0f, 0f, client == 0 ? 0f : MathF.PI, client == 0 ? InputButtons.Fire : InputButtons.None, 0.4f);
        h.Run(TimeSpan.FromSeconds(2), Hold);
        Assert.True(a.Predicted.SpreadBloom > 1f, $"{a.Predicted.SpreadBloom}");   // warm before the stall
        int noHistoryBefore = a.SpreadOutcomes[(int)SpreadReconcile.Outcome.NoHistory];
        for (int ms = 0; ms < 400; ms++) { h.Clock.Advance(TimeSpan.FromMilliseconds(1)); h.Server.Update(); }   // the client is frozen
        var server = new Dictionary<uint, float>();
        var client = new Dictionary<uint, float>();
        h.Run(TimeSpan.FromSeconds(2), Hold, () =>
        {
            if (h.Server.World.Players.TryGetValue(a.PlayerId, out var p)) server[h.Server.World.Tick] = p.SpreadBloom;
            client[a.Tick] = a.Predicted.SpreadBloom;
        });
        Assert.True(a.SpreadOutcomes[(int)SpreadReconcile.Outcome.NoHistory] > noHistoryBefore, "the stall's snapshot had a prediction for its tick");
        var common = client.Keys.Where(server.ContainsKey).Order().ToList();
        Assert.True(common.Count > 30);
        Assert.All(common, t => Assert.True(client[t] <= server[t] + 1e-4f, $"tick {t}: predicted {client[t]} above the server's {server[t]}"));
        Assert.Equal(server[common[^1]], client[common[^1]], 4);   // settled
        Assert.True(a.SpreadOutcomes[(int)SpreadReconcile.Outcome.Carried] > 10);
    }

    [Fact]
    public void A_round_restart_rests_the_predicted_bloom_with_the_servers()
    {
        // Short rounds (2 s), a 0.3 s end and a 0.5 s countdown: client 0 holds the bolter through everything, so its bloom is warm when the
        // server respawns everyone in place at each restart. Ticks the client predicted before the restart's snapshot reached it cannot know of the
        // respawn (every field differs there, not only the bloom); from that snapshot on the prediction rests with the server and agrees tick for tick.
        // Without the round rule the warm bloom would be carried into the countdown and differ until it decayed (about half a second).
        var rules = new MatchRules { TimeLimit = 2f, CountdownTime = 0.5f, RestartDelay = 0.3f, ScoreLimit = 1000 };
        var h = new Harness(new LinkConditions(TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(5), 0), 9, 0, NetConfig,
            new SmServer.ServerOptions { Match = rules, PeerTimeout = TimeSpan.FromMinutes(5) });
        var a = h.AddClient(nonce: 100, options: new ClientOptions { RequestedClass = 0, RequestedPrimary = 0 });
        h.AddClient(nonce: 101, options: new ClientOptions { RequestedClass = 1 });
        PlayerInput Hold(int client, uint tick) => new(tick, 0f, 0f, client == 0 ? 0f : MathF.PI, client == 0 ? InputButtons.Fire : InputButtons.None, 0.4f);
        var truth = new Dictionary<uint, PlayerState>();
        var predicted = new Dictionary<uint, PlayerState>();
        var restartTicks = new List<uint>(); var last = MatchState.Warmup;
        h.Run(TimeSpan.FromSeconds(10), Hold, () =>
        {
            var w = h.Server.World;
            if (last == MatchState.Ended && w.State != MatchState.Ended) restartTicks.Add(w.Tick);
            last = w.State;
            if (w.Players.TryGetValue(a.PlayerId, out var p)) truth[w.Tick] = p;
            if (a.Status == ClientStatus.Connected) predicted[a.Tick] = a.Predicted;
        });
        Assert.True(restartTicks.Count >= 2, $"{restartTicks.Count} restarts");
        // The window a prediction cannot know: from the restart tick for one round trip (80 ms, plus jitter and the input lead: 6 ticks).
        bool Blind(uint t) => restartTicks.Any(r => t >= r && t <= r + 6);
        uint from = truth.Keys.Min() + 90;
        var diffs = predicted.Where(kv => kv.Key >= from && truth.ContainsKey(kv.Key) && !Blind(kv.Key) && kv.Value.SpreadBloom != truth[kv.Key].SpreadBloom)
            .Select(kv => $"tick {kv.Key}: predicted {kv.Value.SpreadBloom} server {truth[kv.Key].SpreadBloom}").ToList();
        Assert.True(diffs.Count == 0, $"{diffs.Count} ticks differ outside the blind window; first: {diffs.FirstOrDefault()}; restarts at {string.Join(",", restartTicks)}");
        Assert.True(a.SpreadOutcomes[(int)SpreadReconcile.Outcome.RoundRespawn] >= 2, string.Join(",", a.SpreadOutcomes));
        Assert.Contains(truth.Values, p => p.SpreadBloom > 1f);   // the bloom was warm in play
    }

    [Fact]
    public void Turn_is_across_and_up_in_the_shots_frame()
    {
        var forward = Combat.AimDirection(0f, 0f);   // +Y
        var right = Spread.Turn(forward, 0.1f, 0f);
        Assert.True(right.X > 0f && MathF.Abs(right.Z) < 1e-6f);
        var up = Spread.Turn(forward, 0f, 0.1f);
        Assert.True(up.Z > 0f && MathF.Abs(up.X) < 1e-6f);
        var vertical = Spread.Turn(Vector3.UnitZ, 0.1f, 0f);   // straight up: no NaN
        Assert.True(float.IsFinite(vertical.X) && float.IsFinite(vertical.Z));
    }

    // ---------------------------------------------------------------- the config hash

    [Fact]
    public void The_spread_changes_the_config_hash_and_none_leaves_it_alone()
    {
        var plain = Config with { Weapons = [BolterBase with { Spread = null }, StalkerBase with { Spread = null }] };
        var noSpreadAgain = Config with { Weapons = [Gun("bolter", TriggerKind.FullAuto, 0.155f), Gun("stalker", TriggerKind.SemiAuto, 0.267f)] };
        Assert.Equal(plain.Hash(), noSpreadAgain.Hash());
        Assert.NotEqual(plain.Hash(), Config.Hash());
        var tweaked = Config with { Weapons = [Bolter with { Spread = Spec(Set("hip", 0.25f, 0.36f, 1.5f), Set("aim", 0.075f, 0.3f, 1.5f)) }, Stalker] };
        Assert.NotEqual(Config.Hash(), tweaked.Hash());
        var climb = Config with { Weapons = [Bolter with { Spread = Spec(Set("hip", 0.25f, 0.35f, 1.5f, climb: 0.4f), Set("aim", 0.075f, 0.3f, 1.5f)) }, Stalker] };
        Assert.NotEqual(Config.Hash(), climb.Hash());
    }
}
