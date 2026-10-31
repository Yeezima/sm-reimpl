using System.Numerics;
using SmNet;
using SmServer;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// M6 acceptance, end to end through <see cref="GameServer"/>, <see cref="GameClient"/> and the simulated network:
/// server-enforced weapon limits, lag-compensated hits, damage, death/respawn, regeneration and exact prediction.
/// The bolter and health values are the originals recorded in spec/behavior.md (typed in here; no game bytes).
/// </summary>
public class CombatAcceptanceTests(ITestOutputHelper output)
{
    // ---------------------------------------------------------------- configuration and helpers

    /// <summary>Original bolter, health and shield values (spec/behavior.md); RespawnTime is the placeholder 5 s.</summary>
    static SimConfig Bolter(SimConfig? baseConfig = null) => (baseConfig ?? new SimConfig()) with
    {
        Damage = 22f, ShotCooldown = 0.133f, ClipSize = 30, ReserveSize = 270, ReloadTime = 1.3f, MaxRange = 250f,
        FallOffStart = 45f, FallOffEnd = 70f, FallOffEndMultiplier = 0.8f, HeadMultiplier = 2.55f, BodyMultiplier = 1f,
        ShieldHeadMultiplier = 0.5f, ShieldBodyMultiplier = 0.505f, HealthMax = 100f, HealthRegenDelay = 5f, HealthRegenRate = 5f,
        ShieldMax = 100f, ShieldRegenDelay = 7f, ShieldRegenRate = 33f, RespawnTime = 5f,
    };

    /// <summary>Original marine movement (spec/behavior.md) on top of the bolter values.</summary>
    static SimConfig BolterOriginalMovement() => Bolter() with
    {
        RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, WalkSpeed = 3.6f,
        GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f,
    };

    /// <summary>Bolter values with very fast, instant movement: only used to reposition players for range tests.</summary>
    static SimConfig BolterFastMovement() => Bolter() with
    {
        RunSpeed = 40f, StrafeSpeed = 40f, BackSpeed = 40f, SprintSpeed = 40f, GroundTimeToMaxSpeed = 1f / 30f, MaxAcceleration = 1e5f,
    };

    static readonly LinkConditions Rtt150 = new(TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(10), 0.05);
    static readonly LinkConditions Lan = new(TimeSpan.FromMilliseconds(20), TimeSpan.Zero, 0);
    const float SkyPitch = 1.3f;   // aims almost straight up: shots that can hit nobody

    /// <summary>Records the server's authoritative world after every tick, and every resolved hit.</summary>
    sealed class Recorder
    {
        public readonly SortedDictionary<uint, Dictionary<int, PlayerState>> Truth = new();
        public readonly List<HitEvent> Hits = new();
        public int MissedTicks;
        uint _last = uint.MaxValue;

        public void Observe(World w)
        {
            if (w.Tick == _last) return;
            if (_last != uint.MaxValue && w.Tick != _last + 1) MissedTicks++;
            _last = w.Tick;
            Truth[w.Tick] = new Dictionary<int, PlayerState>(w.Players);
            Hits.AddRange(w.LastHits);
        }

        /// <summary>Server ticks at which this player's clip dropped by one: the shots the server counted.</summary>
        public List<uint> ShotTicks(int id)
        {
            var ticks = new List<uint>();
            PlayerState? prev = null;
            foreach (var (tick, players) in Truth)
            {
                if (!players.TryGetValue(id, out var s)) { prev = null; continue; }
                if (prev is { } p && s.Clip == p.Clip - 1) ticks.Add(tick);
                prev = s;
            }
            return ticks;
        }

        public PlayerState At(uint tick, int id) => Truth[tick][id];
    }

    sealed class Rig
    {
        public Harness H { get; }
        public Recorder Rec { get; } = new();
        public Rig(SimConfig config, LinkConditions link, int clients, int seed = 1, Func<IServerTransport, IServerTransport>? wrap = null)
            => H = new Harness(link, seed, clients, config, new ServerOptions { PeerTimeout = TimeSpan.FromMinutes(5) }, wrap);
        public SimConfig C => H.Config;
        public World W => H.Server.World;
        public void Run(TimeSpan d, Func<int, uint, PlayerInput>? input = null, Action? after = null)
            => H.Run(d, input, () => { Rec.Observe(W); after?.Invoke(); });
        /// <summary>One 1 ms step plus recording.</summary>
        public void Step(Func<int, uint, PlayerInput>? input = null) { H.Step(input); Rec.Observe(W); }
    }

    static (float Yaw, float Pitch) AimAt(Vector3 from, Vector3 to)
    {
        var d = to - from;
        return (MathF.Atan2(-d.X, d.Y), MathF.Atan2(d.Z, new Vector2(d.X, d.Y).Length()));
    }

    static Vector3 Eye(PlayerState s, SimConfig c) => s.Position + new Vector3(0, 0, c.EyeHeight);
    static Vector3 BodyCenter(Vector3 feet) => feet + new Vector3(0, 0, 1.0f);
    static Vector3 HeadCenter(Vector3 feet, SimConfig c) => feet + new Vector3(0, 0, c.HeadHeight);
    static float Horizontal(Vector3 v) => new Vector2(v.X, v.Y).Length();

    /// <summary>Stick (local frame) that moves a player with heading <paramref name="yaw"/> toward a goal; scaled down near it.</summary>
    static (float X, float Y) StickToward(Vector3 pos, Vector3 goal, float yaw, float stepPerTick)
    {
        var w = new Vector2(goal.X - pos.X, goal.Y - pos.Y);
        float dist = w.Length();
        if (dist < 1e-4f) return (0, 0);
        w /= dist;
        float s = Math.Min(1f, dist / stepPerTick);
        var forward = new Vector2(-MathF.Sin(yaw), MathF.Cos(yaw));
        var right = new Vector2(forward.Y, -forward.X);
        return (Vector2.Dot(w, right) * s, Vector2.Dot(w, forward) * s);
    }

    static PlayerInput Idle(uint t) => new(t, 0, 0, 0, InputButtons.None);

    /// <summary>Runs until every client is connected and has authoritative state.</summary>
    static void Connect(Rig r)
    {
        r.Run(TimeSpan.FromSeconds(1.5));
        Assert.All(r.H.Clients, c => Assert.Equal(ClientStatus.Connected, c.Status));
    }

    // ---------------------------------------------------------------- 1. weapon limits enforced by the server

    [Fact]
    public void HoldingFire_for_2s_server_counts_at_most_the_fire_rate()
    {
        var r = new Rig(Bolter(), Rtt150, clients: 1, seed: 11);
        Connect(r);
        var c = r.H.Clients[0];
        uint t0 = c.Tick + 10;
        int ticks = (int)Math.Round(2.0 * r.C.TickRate);
        r.Run(TimeSpan.FromSeconds(4), (_, t) => new PlayerInput(t, 0, 0, 0, t >= t0 && t < t0 + ticks ? InputButtons.Fire : InputButtons.None, SkyPitch));

        var shots = r.Rec.ShotTicks(c.PlayerId);
        int bound = (int)Math.Ceiling(2.0 / 0.133) + 1;
        output.WriteLine($"shots counted by the server: {shots.Count} (bound {bound}) at ticks {string.Join(",", shots.Select(s => s - t0))} (relative)");
        Assert.InRange(shots.Count, 1, bound);
        Assert.All(shots, s => Assert.InRange(s, t0, t0 + (uint)ticks - 1));   // only while Fire was held
        var last = r.Rec.Truth.Last().Value[c.PlayerId];
        Assert.Equal(30 - shots.Count, last.Clip);
        Assert.Equal(270, last.Reserve);
        Assert.Equal(15, shots.Count);   // 60 ticks / 4-tick interval (0.133 s rounds up to 4 ticks at 30 Hz)
        Assert.Empty(r.Rec.Hits);
    }

    [Fact]
    public void HoldingFire_empties_clip_reloads_for_1_3s_and_reserve_drops_by_the_reloaded_amount()
    {
        var r = new Rig(Bolter(), Rtt150, clients: 1, seed: 12);
        Connect(r);
        var c = r.H.Clients[0];
        uint t0 = c.Tick + 10;
        r.Run(TimeSpan.FromSeconds(7), (_, t) => new PlayerInput(t, 0, 0, 0, t >= t0 && t < t0 + 180 ? InputButtons.Fire : InputButtons.None, SkyPitch));

        int id = c.PlayerId;
        var shots = r.Rec.ShotTicks(id);
        var reloadTicks = r.Rec.Truth.Where(kv => kv.Value.TryGetValue(id, out var s) && s.ReloadLeft > 0).Select(kv => kv.Key).ToList();
        Assert.NotEmpty(reloadTicks);
        uint reloadStart = reloadTicks.First(), reloadEnd = reloadTicks.Last() + 1;   // first tick with the clip refilled
        var shotsBefore = shots.Where(s => s < reloadStart).ToList();
        var shotsAfter = shots.Where(s => s >= reloadEnd).ToList();
        output.WriteLine($"shots before reload {shotsBefore.Count}, reload ticks {reloadStart}..{reloadEnd} ({(reloadEnd - reloadStart) / 30.0:F3} s), shots after {shotsAfter.Count}, total {shots.Count}");

        Assert.Equal(30, shotsBefore.Count);                                   // never more than the clip
        Assert.DoesNotContain(shots, s => s >= reloadStart && s < reloadEnd);  // no shots while reloading
        Assert.True((reloadEnd - reloadStart) / (double)r.C.TickRate >= 1.3 - 1e-6, "reload shorter than 1.3 s");
        Assert.True((reloadEnd - reloadStart) / (double)r.C.TickRate <= 1.3 + 2.0 / r.C.TickRate, "reload much longer than 1.3 s");
        var refilled = r.Rec.At(reloadEnd, id);
        Assert.Equal(30, refilled.Clip);
        Assert.Equal(240, refilled.Reserve);
        Assert.True(shots.Count <= Math.Ceiling(6.0 / 0.133) + 1);
        for (int i = 1; i < shots.Count; i++)
            Assert.True((shots[i] - shots[i - 1]) / (double)r.C.TickRate >= 0.133 - 1e-6, $"shots {i - 1},{i} only {shots[i] - shots[i - 1]} ticks apart");
    }

    [Fact]
    public void Manual_reload_of_a_partial_clip_takes_only_what_is_missing_from_reserve()
    {
        var r = new Rig(Bolter(), Lan, clients: 1);
        Connect(r);
        var c = r.H.Clients[0];
        uint t0 = c.Tick + 10;
        r.Run(TimeSpan.FromSeconds(3), (_, t) => new PlayerInput(t, 0, 0, 0,
            t >= t0 && t <= t0 + 16 ? InputButtons.Fire : t == t0 + 30 ? InputButtons.Reload : InputButtons.None, SkyPitch));
        var last = r.Rec.Truth.Last().Value[c.PlayerId];
        Assert.Equal(5, r.Rec.ShotTicks(c.PlayerId).Count);
        Assert.Equal(30, last.Clip);
        Assert.Equal(265, last.Reserve);
        Assert.Equal(0f, last.ReloadLeft);
    }

    [Fact]
    public void Firing_until_all_ammunition_is_gone_spends_exactly_clip_plus_reserve()
    {
        var r = new Rig(Bolter(), Lan, clients: 1);
        Connect(r);
        var c = r.H.Clients[0];
        r.Run(TimeSpan.FromSeconds(60), (_, t) => new PlayerInput(t, 0, 0, 0, InputButtons.Fire, SkyPitch));
        var shots = r.Rec.ShotTicks(c.PlayerId);
        var last = r.Rec.Truth.Last().Value[c.PlayerId];
        output.WriteLine($"shots {shots.Count}, clip {last.Clip}, reserve {last.Reserve}, reload {last.ReloadLeft}, last shot at {shots.LastOrDefault()} of {r.W.Tick}");
        Assert.Equal(300, shots.Count);
        Assert.Equal(0, last.Clip);
        Assert.Equal(0, last.Reserve);
        Assert.Equal(0f, last.ReloadLeft);
        Assert.True(r.W.Tick - shots.Last() > 60, "the run should end well after the last possible shot");
    }

    /// <summary>
    /// A forged client floods duplicate batches (every ms, 16 future ticks), junk button bits, non-finite pitch, and
    /// toggles Fire/Reload. The server's fire rate, clip and reload still hold.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Forged_input_flood_cannot_exceed_fire_rate_or_ammunition(bool toggleReload)
    {
        var r = new Rig(Bolter(), LinkConditions.Perfect, clients: 0);
        var raw = r.H.Net.Connect();
        var (id, session) = RobustnessTests.ConnectRaw(r.H, raw);
        var fireBits = (InputButtons)0xF0 | InputButtons.Fire | InputButtons.Sprint;
        for (int ms = 0; ms < 8000; ms++)
        {
            uint w = r.W.Tick;
            var batch = Enumerable.Range(1, Protocol.MaxInputsPerBatch).Select(k =>
            {
                uint t = w + (uint)k;
                var b = toggleReload && t % 2 == 0 ? (InputButtons.Reload | InputButtons.Fire) : fireBits;
                return new PlayerInput(t, 0, 0, 0, b, ms % 2 == 0 ? float.NaN : 9f, ViewTick: uint.MaxValue - (uint)k);
            }).ToArray();
            raw.Send(Protocol.Write(new InputBatch(batch, session)));
            raw.Send(Protocol.Write(new InputBatch(batch, session)));   // duplicate
            r.Step();
        }

        var shots = r.Rec.ShotTicks(id);
        var first = r.Rec.Truth.First(kv => kv.Value.ContainsKey(id)).Key;
        int inFirst2s = shots.Count(s => s < first + 60);
        output.WriteLine($"toggleReload={toggleReload}: shots {shots.Count} in 8 s, {inFirst2s} in the first 2 s; final {r.W.Players[id].Clip}/{r.W.Players[id].Reserve}");
        Assert.True(inFirst2s <= Math.Ceiling(2.0 / 0.133) + 1, $"{inFirst2s} shots in 2 s");
        Assert.True(shots.Count <= Math.Ceiling(8.0 / 0.133) + 1);
        for (int i = 1; i < shots.Count; i++)
            Assert.True((shots[i] - shots[i - 1]) / 30.0 >= 0.133 - 1e-6, $"shots {shots[i - 1]} and {shots[i]} too close");

        // Ammunition accounting: every round fired came out of the clip, every reload out of the reserve.
        var final = r.W.Players[id];
        Assert.Equal(300, final.Clip + final.Reserve + shots.Count);
        // Between two reserve decreases (reloads) there are never more than 30 shots.
        int sinceReload = 0; PlayerState? prev = null;
        foreach (var (tick, players) in r.Rec.Truth)
        {
            if (!players.TryGetValue(id, out var s)) continue;
            if (prev is { } p && s.Reserve < p.Reserve) sinceReload = 0;
            if (prev is { } q && s.Clip == q.Clip - 1) sinceReload++;
            Assert.True(sinceReload <= 30, $"more than a clip without reloading at tick {tick}");
            prev = s;
        }
        Assert.Empty(r.Rec.Hits);
    }

    // ---------------------------------------------------------------- 2. lag compensation

    /// <summary>Records (input tick, view tick) of every input a connection sends, as the server receives them.</summary>
    sealed class InputSpy(IServerTransport inner, int connection) : IServerTransport
    {
        public readonly Dictionary<uint, uint> ViewTicks = new();
        public void Send(int c, byte[] packet) => inner.Send(c, packet);
        public void Disconnect(int c) => inner.Disconnect(c);
        public void Pin(int c) => inner.Pin(c);
        public bool TryReceive(out int c, out byte[] packet)
        {
            if (!inner.TryReceive(out c, out packet)) return false;
            if (c == connection && Protocol.Read(packet) is InputBatch b)
                foreach (var i in b.Inputs) ViewTicks.TryAdd(i.Tick, i.ViewTick);
            return true;
        }
    }

    /// <summary>
    /// 150 ms RTT, 5% loss: a stationary shooter aims at the target's interpolated (rendered) position while the target
    /// strafes at the original strafe speed, reversing every 1.5 s. <paramref name="lateralOffset"/> moves the aim point
    /// along the rendered target's velocity, still inside the rendered body (radius 0.45 m): negative = trailing edge.
    /// Health/shield are made huge here so the target never dies (damage is not the subject).
    /// </summary>
    [Theory]
    [InlineData(0f, 0.97, 0)]
    [InlineData(-0.3f, 0.95, 0)]
    [InlineData(0.3f, 0.95, 0)]
    // Controls: the same shots with a rewind cap large enough for this round trip (isolates the cap as the cause).
    [InlineData(0f, 0.97, 14)]
    [InlineData(-0.3f, 0.95, 14)]
    public void Shooter_aiming_at_the_rendered_target_hits_under_150ms_rtt(float lateralOffset, double minHitRate, int maxRewindOverride)
    {
        var config = BolterOriginalMovement() with { HealthMax = 1e7f, ShieldMax = 1e7f };
        if (maxRewindOverride > 0) config = config with { MaxRewindTicks = maxRewindOverride };
        InputSpy? spy = null;
        var r = new Rig(config, Rtt150, clients: 2, seed: 21, wrap: t => spy = new InputSpy(t, 0));
        Connect(r);
        var shooter = r.H.Clients[0]; var target = r.H.Clients[1];
        int sid = shooter.PlayerId, tid = target.PlayerId;
        var sPos = r.W.Players[sid].Position;
        uint start = shooter.Tick + 30;
        var aimed = new Dictionary<uint, bool>();

        PlayerInput Input(int i, uint t)
        {
            if (i == 1)
            {
                // Facing away from the shooter and strafing: moves across the line of fire (circle strafe).
                var u = target.Predicted.Position - sPos;
                return new PlayerInput(t, (t / 45) % 2 == 0 ? 1f : -1f, 0, MathF.Atan2(-u.X, u.Y), InputButtons.None);
            }
            if (t < start) return Idle(t);
            var seen = shooter.RemotePlayers().Players.Where(p => p.PlayerId == tid).ToList();
            if (seen.Count == 0) { aimed[t] = false; return Idle(t); }
            var rendered = seen[0];
            var v = new Vector3(rendered.Velocity.X, rendered.Velocity.Y, 0);
            var side = v.LengthSquared() > 0.01f ? Vector3.Normalize(v) : Vector3.Zero;
            var point = BodyCenter(rendered.Position) + side * lateralOffset;
            var (yaw, pitch) = AimAt(Eye(shooter.Predicted, config), point);
            aimed[t] = true;
            return new PlayerInput(t, 0, 0, yaw, InputButtons.Fire, pitch);
        }

        r.Run(TimeSpan.FromSeconds(12), Input);

        var shots = r.Rec.ShotTicks(sid).Where(t => aimed.GetValueOrDefault(t)).ToList();
        var hitTicks = r.Rec.Hits.Where(h => h.Shooter == sid && h.Target == tid).Select(h => h.Tick).ToHashSet();
        int hits = shots.Count(hitTicks.Contains);
        double rate = shots.Count == 0 ? 0 : hits / (double)shots.Count;
        var rewinds = shots.Where(spy!.ViewTicks.ContainsKey).Select(t => (int)(t - spy.ViewTicks[t])).ToList();
        output.WriteLine($"offset {lateralOffset:+0.0;-0.0;0} m: {hits}/{shots.Count} hits ({100 * rate:F1}%); requested rewind ticks min {rewinds.DefaultIfEmpty().Min()} " +
            $"median {rewinds.OrderBy(x => x).ElementAtOrDefault(rewinds.Count / 2)} max {rewinds.DefaultIfEmpty().Max()} (cap {config.MaxRewindTicks}); " +
            $"{rewinds.Count(x => x > config.MaxRewindTicks)} of {rewinds.Count} shots asked for more than the cap");
        Assert.True(shots.Count >= 40, $"only {shots.Count} aimed shots");
        Assert.True(rate >= minHitRate, $"hit rate {100 * rate:F1}% < {100 * minHitRate:F0}% (offset {lateralOffset} m)");
        Assert.Equal(0, r.Rec.MissedTicks);
    }

    /// <summary>
    /// Forged (raw) shooter with explicit view ticks against a target strafing at 6 m/s, perfect network: the server
    /// tests the shot against the target as it was at the view tick, not where it is now; a view tick older than
    /// MaxRewindTicks is clamped to the cap. The shooter first acknowledges snapshots 28 ticks late (a laggy client, so
    /// the server allows the full cap), then acknowledges the current tick: the server then rewinds at most the
    /// interpolation delay plus 2 ticks behind the acknowledged snapshot. A view tick of 0 means no rewind.
    /// </summary>
    [Fact]
    public void Shots_are_tested_against_the_rewound_target_and_the_rewind_is_capped()
    {
        var config = BolterOriginalMovement();
        var r = new Rig(config, LinkConditions.Perfect, clients: 0);
        var rawShooter = r.H.Net.Connect(); var rawTarget = r.H.Net.Connect();
        var (sid, sSession) = RobustnessTests.ConnectRaw(r.H, rawShooter, nonce: 1);
        var (tid, tSession) = RobustnessTests.ConnectRaw(r.H, rawTarget, nonce: 2);
        var sPos = r.W.Players[sid].Position;
        var planned = new Dictionary<uint, PlayerInput>();
        var expectations = new List<(string Name, uint Tick, bool Hit)>();
        uint lastSeen = r.W.Tick, beganStrafing = 0;
        int cap = config.MaxRewindTicks;

        void Plan(string name, uint cur, uint viewTick, uint aimTick, bool expectHit)
        {
            uint fire = cur + 2;
            var (yaw, pitch) = AimAt(sPos + new Vector3(0, 0, config.EyeHeight), BodyCenter(r.Rec.At(aimTick, tid).Position));
            planned[fire] = new PlayerInput(fire, 0, 0, yaw, InputButtons.Fire, pitch, viewTick);
            expectations.Add((name, fire, expectHit));
        }

        for (int ms = 0; ms < 8000; ms++)
        {
            r.Step();
            uint cur = r.W.Tick;
            if (cur == lastSeen) continue;
            lastSeen = cur;
            if (beganStrafing == 0) beganStrafing = cur;
            uint k = cur - beganStrafing;
            // Once the target is at full strafe speed (after 1 s), one probe every 20 ticks.
            if (k == 30) Plan("aim at view tick (rewind 6)", cur, cur - 4, cur - 4, true);
            if (k == 50) Plan("aim at server's current position, view tick 4 ticks old", cur, cur - 4, cur, false);
            if (k == 70) Plan($"view tick 32 ticks old, aim at cap ({cap})", cur, cur - 30, cur + 2 - (uint)cap, true);
            if (k == 90) Plan("view tick 32 ticks old, aim at that old position", cur, cur - 30, cur - 30, false);
            if (k == 110) Plan($"aim at view tick at the cap exactly (rewind {cap})", cur, cur + 2 - (uint)cap, cur + 2 - (uint)cap, true);
            if (k == 130) Plan("view tick 0 (no rewind), aim at cap", cur, 0, cur + 2 - (uint)cap, false);
            if (k == 150) Plan("view tick 0 (no rewind), aim at the old position", cur, 0, cur - 30, false);
            bool honest = k >= 160;
            if (k == 170) Plan("current ack, view tick 30 old, aim at that old position", cur, cur - 30, cur - 30, false);
            if (k == 190) Plan("current ack, aim at view tick (rewind 6)", cur, cur - 4, cur - 4, true);
            var shooterInputs = Enumerable.Range(1, 4).Select(i => cur + (uint)i)
                .Select(t => planned.TryGetValue(t, out var p) ? p : new PlayerInput(t, 0, 0, 0, InputButtons.None)).ToArray();
            var u = r.W.Players[tid].Position - sPos;   // circle strafe: always across the line of fire
            float targetYaw = MathF.Atan2(-u.X, u.Y);
            var targetInputs = Enumerable.Range(1, 4).Select(i => new PlayerInput(cur + (uint)i, 1f, 0, targetYaw, InputButtons.None)).ToArray();
            rawShooter.Send(Protocol.Write(new InputBatch(shooterInputs, sSession, honest ? cur : Math.Max(1, cur - 28))));
            rawTarget.Send(Protocol.Write(new InputBatch(targetInputs, tSession)));
        }

        Assert.Equal(9, expectations.Count);
        var speed = Horizontal(r.Rec.At(expectations[0].Tick, tid).Velocity);
        output.WriteLine($"target speed at first probe {speed:F2} m/s");
        Assert.InRange(speed, 5.9f, 6.1f);
        var failures = new List<string>();
        foreach (var (name, tick, hit) in expectations)
        {
            bool didHit = r.Rec.Hits.Any(h => h.Shooter == sid && h.Target == tid && h.Tick == tick);
            bool fired = r.Rec.ShotTicks(sid).Contains(tick);
            output.WriteLine($"{name}: fired {fired}, hit {didHit} (expected {hit})");
            if (!fired) failures.Add($"{name}: shot not fired");
            else if (didHit != hit) failures.Add($"{name}: hit {didHit}, expected {hit}");
        }
        Assert.Empty(failures);
    }

    // ---------------------------------------------------------------- 3. damage

    /// <summary>Stationary shooter and target; the shooter fires a fixed window of body shots aimed by the server truth.</summary>
    static (Rig Rig, int Shooter, int Target) BodyShots(SimConfig config, Func<Vector3, SimConfig, Vector3> aimPoint, int shotCount, double seconds)
    {
        var r = new Rig(config, Lan, clients: 2);
        Connect(r);
        var shooter = r.H.Clients[0];
        int sid = shooter.PlayerId, tid = r.H.Clients[1].PlayerId;
        uint t0 = shooter.Tick + 10, t1 = t0 + (uint)(4 * (shotCount - 1));
        var targetFeet = r.W.Players[tid].Position;
        r.Run(TimeSpan.FromSeconds(seconds), (i, t) =>
        {
            if (i != 0 || t < t0 || t > t1) return Idle(t);
            var (yaw, pitch) = AimAt(Eye(shooter.Predicted, config), aimPoint(targetFeet, config));
            return new PlayerInput(t, 0, 0, yaw, InputButtons.Fire, pitch);
        });
        return (r, sid, tid);
    }

    [Fact]
    public void Body_hits_wear_the_shield_first_then_health_then_kill()
    {
        var (r, sid, tid) = BodyShots(Bolter(), (feet, _) => BodyCenter(feet), shotCount: 16, seconds: 3);
        var hits = r.Rec.Hits.Where(h => h.Shooter == sid).ToList();
        foreach (var h in hits)
        {
            var after = r.Rec.At(h.Tick, tid);
            output.WriteLine($"tick {h.Tick} {h.Zone} d={h.Distance:F2} shield -{h.ShieldDamage:F4} health -{h.HealthDamage:F4} -> shield {after.Shield:F4} health {after.Health:F4} alive {after.Alive}");
        }
        Assert.All(hits, h => Assert.Equal(HitZone.Body, h.Zone));
        Assert.All(hits, h => Assert.True(h.Distance < 45f));
        // 100 / (22 × 0.505) = 9.0009: nine full shield hits, the tenth breaks the rest of the shield.
        for (int i = 0; i < 9; i++)
        {
            Assert.Equal(22f * 0.505f, hits[i].ShieldDamage, 3);
            Assert.Equal(0f, hits[i].HealthDamage, 4);
            Assert.Equal(100f, r.Rec.At(hits[i].Tick, tid).Health, 4);
        }
        float shieldLeft = 100f - 9 * 22f * 0.505f;
        Assert.Equal(shieldLeft, hits[9].ShieldDamage, 3);
        Assert.Equal((22f - shieldLeft / 0.505f) * 1f, hits[9].HealthDamage, 3);
        Assert.Equal(0f, r.Rec.At(hits[9].Tick, tid).Shield, 4);
        for (int i = 10; i < 13; i++) { Assert.Equal(0f, hits[i].ShieldDamage, 4); Assert.Equal(22f, hits[i].HealthDamage, 3); }
        Assert.True(hits[13].Killed, "the 14th body hit should kill");
        Assert.Equal(14, hits.Count);   // the remaining two shots find a dead target and are not hits
        var dead = r.Rec.At(hits[13].Tick, tid);
        Assert.False(dead.Alive);
        Assert.Equal(0f, dead.Health);
        Assert.All(hits.Take(13), h => Assert.False(h.Killed));
    }

    [Fact]
    public void First_body_hit_on_a_full_shield_leaves_health_exactly_full()
    {
        var (r, sid, tid) = BodyShots(Bolter(), (feet, _) => BodyCenter(feet), shotCount: 1, seconds: 1);
        var hit = Assert.Single(r.Rec.Hits, h => h.Shooter == sid);
        var after = r.Rec.At(hit.Tick, tid);
        output.WriteLine($"shield -{hit.ShieldDamage:R} health -{hit.HealthDamage:R}; health now {after.Health:R}");
        Assert.Equal(100f, after.Health);   // exact: no residue from converting shield damage back
        Assert.Equal(0f, hit.HealthDamage);
    }

    [Fact]
    public void Head_hits_use_the_head_multipliers()
    {
        var (r, sid, tid) = BodyShots(Bolter(), HeadCenter, shotCount: 11, seconds: 2);
        var hits = r.Rec.Hits.Where(h => h.Shooter == sid).ToList();
        output.WriteLine(string.Join("; ", hits.Select(h => $"{h.Zone} d={h.Distance:F3} s={h.ShieldDamage:F3} h={h.HealthDamage:F3}")));
        Assert.NotEmpty(hits);
        Assert.All(hits, h => Assert.Equal(HitZone.Head, h.Zone));
        Assert.Equal(22f * 0.5f, hits[0].ShieldDamage, 3);   // shield at the head shield multiplier
        Assert.Equal(0f, hits[0].HealthDamage, 4);
        // 100 / 11 = 9.09: the tenth head hit breaks the last 1 shield, the eleventh does 22 × 2.55 = 56.1 to health.
        Assert.Equal(22f * 2.55f, hits[10].HealthDamage, 2);
    }

    /// <summary>
    /// The shooter backs away along the line to the target and fires one body shot at each centre distance. Expected
    /// damage is ×1 to 45 m, linear to ×0.8 at 70 m, ×0.8 beyond, nothing past 250 m (measured to the hit surface).
    /// </summary>
    [Fact]
    public void Damage_falls_off_with_distance_and_stops_at_max_range()
    {
        var config = BolterFastMovement();
        var r = new Rig(config, Lan, clients: 2);
        Connect(r);
        var shooter = r.H.Clients[0];
        int sid = shooter.PlayerId, tid = r.H.Clients[1].PlayerId;
        var targetFeet = r.W.Players[tid].Position;
        var away = Vector3.Normalize(r.W.Players[sid].Position - targetFeet) * new Vector3(1, 1, 0);
        away = Vector3.Normalize(away);
        float[] centreDistances = [30f, 57.5f, 100f, 250.3f, 251f];
        int stage = 0, settled = 0; uint fireTick = 0;
        var fired = new List<(float Centre, uint Tick, Vector3 Eye)>();
        float step = config.RunSpeed / config.TickRate;

        r.Run(TimeSpan.FromSeconds(16), (i, t) =>
        {
            if (i != 0 || stage >= centreDistances.Length) return Idle(t);
            var me = shooter.Predicted;
            var goal = targetFeet + away * centreDistances[stage];
            var (yaw, pitch) = AimAt(Eye(me, config), BodyCenter(targetFeet));
            if (fireTick != 0)
            {
                if (t > fireTick + 40) { fireTick = 0; settled = 0; stage++; }
                return new PlayerInput(t, 0, 0, yaw, InputButtons.None, pitch);
            }
            if (Horizontal(me.Position - goal) < 0.002f && Horizontal(me.Velocity) < 1e-3f) settled++; else settled = 0;
            if (settled >= 5)
            {
                fireTick = t;
                fired.Add((centreDistances[stage], t, Eye(me, config)));
                return new PlayerInput(t, 0, 0, yaw, InputButtons.Fire, pitch);
            }
            var (mx, my) = StickToward(me.Position, goal, yaw, step);
            return new PlayerInput(t, mx, my, yaw, InputButtons.None, pitch);
        });

        Assert.Equal(centreDistances.Length, fired.Count);
        var failures = new List<string>();
        foreach (var (centre, tick, eye) in fired)
        {
            var hit = r.Rec.Hits.Where(h => h.Shooter == sid && h.Tick == tick).ToList();
            bool shot = r.Rec.ShotTicks(sid).Contains(tick);
            float surface = Vector3.Distance(eye, BodyCenter(targetFeet)) - config.BodyRadius;   // approximately
            float m = surface <= 45f ? 1f : surface >= 70f ? 0.8f : 1f - 0.2f * (surface - 45f) / 25f;
            output.WriteLine($"centre {centre} m (surface ≈ {surface:F2}): fired {shot}, hits {hit.Count}" +
                (hit.Count > 0 ? $", d={hit[0].Distance:F3}, shield -{hit[0].ShieldDamage:F4} (expected {22f * m * 0.505f:F4}), health -{hit[0].HealthDamage:F4}" : ""));
            if (!shot) { failures.Add($"{centre}: no shot"); continue; }
            if (surface > 250f)
            {
                if (hit.Count != 0) failures.Add($"{centre}: hit beyond max range");
                continue;
            }
            if (hit.Count != 1) { failures.Add($"{centre}: {hit.Count} hits"); continue; }
            var h = hit[0];
            if (Math.Abs(h.Distance - surface) > 0.05f) failures.Add($"{centre}: distance {h.Distance} vs {surface}");
            float mExact = h.Distance <= 45f ? 1f : h.Distance >= 70f ? 0.8f : 1f - 0.2f * (h.Distance - 45f) / 25f;
            if (Math.Abs(h.ShieldDamage - 22f * mExact * 0.505f) > 1e-3f) failures.Add($"{centre}: shield damage {h.ShieldDamage} vs {22f * mExact * 0.505f}");
            if (Math.Abs(h.HealthDamage) > 1e-3f) failures.Add($"{centre}: health damage {h.HealthDamage}");
        }
        Assert.Empty(failures);
    }

    // ---------------------------------------------------------------- 4. death and respawn

    [Fact]
    public void Dead_player_cannot_fire_or_move_and_respawns_full_after_RespawnTime_and_clients_see_it()
    {
        var config = Bolter();
        var r = new Rig(config, Lan, clients: 2);
        Connect(r);
        var shooter = r.H.Clients[0]; var target = r.H.Clients[1];
        int sid = shooter.PlayerId, tid = target.PlayerId;
        var targetFeet = r.W.Players[tid].Position;
        uint t0 = shooter.Tick + 10;
        bool died = false, respawned = false;
        var deadInputs = new List<uint>();
        bool shooterSawDead = false, targetSawDead = false, shooterSawAliveAgain = false, targetSawAliveAgain = false;

        r.Run(TimeSpan.FromSeconds(10), (i, t) =>
        {
            if (i == 0)
            {
                if (t < t0) return Idle(t);
                var (yaw, pitch) = AimAt(Eye(shooter.Predicted, config), BodyCenter(targetFeet));
                return new PlayerInput(t, 0, 0, yaw, InputButtons.Fire, pitch);
            }
            if (died && !respawned)
            {
                deadInputs.Add(t);
                return new PlayerInput(t, 0, 1, 0, InputButtons.Fire | InputButtons.Sprint, SkyPitch);
            }
            return Idle(t);
        }, () =>
        {
            var now = r.W.Players[tid];
            if (!died && !now.Alive) died = true;
            if (died && !respawned && now.Alive) respawned = true;
            if (shooter.RemotePlayers().Players.FirstOrDefault(p => p.PlayerId == tid) is { PlayerId: > 0 } seen)
            {
                if (!seen.Alive && seen.Health == 0f) shooterSawDead = true;
                // Other players arrive slim (protocol 5): no health, so "alive again" is the Alive flag alone.
                if (shooterSawDead && seen.Alive) shooterSawAliveAgain = true;
            }
            if (!target.Predicted.Alive && target.Predicted.Health == 0f && died) targetSawDead = true;
            if (targetSawDead && target.Predicted.Alive && target.Predicted.Health == config.HealthMax) targetSawAliveAgain = true;
        });

        uint deathTick = r.Rec.Hits.First(h => h.Killed && h.Target == tid).Tick;
        uint respawnTick = r.Rec.Truth.First(kv => kv.Key > deathTick && kv.Value[tid].Alive).Key;
        output.WriteLine($"died at {deathTick}, respawned at {respawnTick} ({(respawnTick - deathTick) / 30.0:F3} s); {deadInputs.Count} move+fire inputs sampled while dead");
        var atDeath = r.Rec.At(deathTick, tid);
        Assert.False(atDeath.Alive);
        Assert.Equal(0f, atDeath.Health);

        // While dead: no movement, no shots, no hits taken; inputs asking to move and fire were sent for those ticks.
        Assert.Contains(deadInputs, t => t > deathTick && t < respawnTick);
        for (uint t = deathTick; t < respawnTick; t++)
        {
            var s = r.Rec.At(t, tid);
            Assert.False(s.Alive);
            Assert.Equal(atDeath.Position, s.Position);
            Assert.Equal(atDeath.Clip, s.Clip);
            Assert.Equal(0f, s.Health);
        }
        Assert.DoesNotContain(r.Rec.Hits, h => h.Target == tid && h.Tick > deathTick && h.Tick < respawnTick);
        Assert.DoesNotContain(r.Rec.Hits, h => h.Shooter == tid);

        // Respawn after RespawnTime (to the tick), full health, shield and ammunition.
        double seconds = (respawnTick - deathTick) / (double)config.TickRate;
        Assert.InRange(seconds, config.RespawnTime - 1e-6, config.RespawnTime + 1.0 / config.TickRate + 1e-6);
        var fresh = r.Rec.At(respawnTick, tid);
        Assert.True(fresh.Alive);
        Assert.Equal(config.HealthMax, fresh.Health);
        Assert.Equal(config.ShieldMax, fresh.Shield);
        Assert.Equal(config.ClipSize, fresh.Clip);
        Assert.Equal(config.ReserveSize, fresh.Reserve);

        Assert.True(shooterSawDead, "shooter's client never showed the target dead (Alive false, Health 0)");
        Assert.True(targetSawDead, "target's own client never showed itself dead");
        Assert.True(shooterSawAliveAgain, "shooter's client never showed the respawned target");
        Assert.True(targetSawAliveAgain, "target's client never showed its own respawn");
    }

    // ---------------------------------------------------------------- 5. regeneration

    [Fact]
    public void Shield_and_health_regenerate_after_their_delays_at_their_rates()
    {
        var config = Bolter();
        var (r, sid, tid) = BodyShots(config, (feet, _) => BodyCenter(feet), shotCount: 10, seconds: 16);
        var hits = r.Rec.Hits.Where(h => h.Shooter == sid).ToList();
        Assert.Equal(10, hits.Count);
        uint last = hits[^1].Tick;
        var hurt = r.Rec.At(last, tid);
        output.WriteLine($"after the last hit: shield {hurt.Shield:F4}, health {hurt.Health:F4}");
        Assert.Equal(0f, hurt.Shield, 4);
        Assert.InRange(hurt.Health, 70f, 90f);

        float Health(double s) => r.Rec.At(last + (uint)Math.Round(s * 30), tid).Health;
        float Shield(double s) => r.Rec.At(last + (uint)Math.Round(s * 30), tid).Shield;
        uint firstHealthRegen = r.Rec.Truth.Keys.First(t => t > last && r.Rec.At(t, tid).Health > hurt.Health);
        uint firstShieldRegen = r.Rec.Truth.Keys.First(t => t > last && r.Rec.At(t, tid).Shield > hurt.Shield);
        output.WriteLine($"health starts at +{(firstHealthRegen - last) / 30.0:F3} s, shield at +{(firstShieldRegen - last) / 30.0:F3} s");

        Assert.InRange((firstHealthRegen - last) / 30.0, 5.0, 5.0 + 2.0 / 30);
        Assert.InRange((firstShieldRegen - last) / 30.0, 7.0, 7.0 + 2.0 / 30);
        Assert.Equal(15f, Health(9.0) - Health(6.0), 2);    // 5/s
        Assert.Equal(11f, Shield(7.6) - Shield(7.266666), 2);   // 33/s over 10 ticks
        Assert.Equal(100f, Health(12.0));
        Assert.Equal(100f, Shield(12.0));
    }

    // ---------------------------------------------------------------- 6. prediction stays exact

    [Theory]
    [InlineData(31)]
    [InlineData(32)]
    public void Prediction_of_weapon_state_matches_the_server_while_firing_and_reloading(int seed)
    {
        var config = BolterOriginalMovement();
        var r = new Rig(config, Rtt150, clients: 2, seed: seed);
        var predicted = new[] { new Dictionary<uint, PlayerState>(), new Dictionary<uint, PlayerState>() };
        PlayerInput Input(int i, uint t)
        {
            var move = Harness.Scripted(i, t);
            var buttons = move.Buttons & ~InputButtons.Jump;
            // Protocol 51 (sim/no-fire-while-sprinting): a sprint blocks the trigger; one fire window in two lets go of Sprint, the other keeps the script's (blocked shots predicted alike).
            if ((t / 45 + (uint)i) % 3 != 0) buttons = (buttons | InputButtons.Fire) & ((t / 45) % 2 == 0 ? ~InputButtons.Sprint : ~InputButtons.None);
            if ((t + (uint)i * 31) % 173 == 0) buttons |= InputButtons.Reload;
            return move with { Buttons = buttons, Pitch = SkyPitch };
        }
        r.Run(TimeSpan.FromSeconds(20), Input, () =>
        {
            for (int i = 0; i < 2; i++)
                if (r.H.Clients[i].Status == ClientStatus.Connected) predicted[i][r.H.Clients[i].Tick] = r.H.Clients[i].Predicted;
        });

        Assert.Empty(r.Rec.Hits);
        for (int i = 0; i < 2; i++)
        {
            var c = r.H.Clients[i];
            int id = c.PlayerId, compared = 0;
            var mismatches = new List<string>();
            uint from = r.Rec.Truth.Keys.First() + 90;   // after the first authoritative state
            foreach (var (tick, pred) in predicted[i])
            {
                if (tick < from || !r.Rec.Truth.TryGetValue(tick, out var world) || !world.TryGetValue(id, out var truth)) continue;
                compared++;
                if (pred.Clip != truth.Clip || pred.Reserve != truth.Reserve || pred.Cooldown != truth.Cooldown ||
                    pred.ReloadLeft != truth.ReloadLeft || pred.Position != truth.Position || pred.Alive != truth.Alive ||
                    pred.Health != truth.Health || pred.Shield != truth.Shield)
                    mismatches.Add($"tick {tick}: predicted clip {pred.Clip}/{pred.Reserve} cd {pred.Cooldown} rl {pred.ReloadLeft} pos {pred.Position}; " +
                        $"server {truth.Clip}/{truth.Reserve} cd {truth.Cooldown} rl {truth.ReloadLeft} pos {truth.Position}");
            }
            int shots = r.Rec.ShotTicks(id).Count;
            int reloads = r.Rec.Truth.Values.Zip(r.Rec.Truth.Values.Skip(1)).Count(p => p.First.TryGetValue(id, out var a) && p.Second.TryGetValue(id, out var b) && b.Reserve < a.Reserve);
            output.WriteLine($"client {i}: compared {compared} ticks, {mismatches.Count} mismatches, {shots} shots, {reloads} reloads, " +
                $"position corrections max {(c.CorrectionErrors.Count > 0 ? c.CorrectionErrors.Max() : float.NaN):F5} m");
            foreach (var m in mismatches.Take(5)) output.WriteLine("  " + m);
            Assert.True(compared > 300, $"client {i}: only {compared} ticks compared");
            Assert.True(shots > 60 && reloads >= 2, $"client {i}: too little firing ({shots} shots, {reloads} reloads)");
            Assert.Empty(mismatches);
        }
    }
}
