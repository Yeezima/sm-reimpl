using System.Numerics;
using SmContent;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// fix/hitboxes probe on the player's install (read-only; skipped without it, never in CI): a jump-pack Assault marine jumps (full and cut jumps, forward,
/// sideways and back) on a loopback network with latency; a Chaos shooter's client records, at every input it sends, the target as it draws it
/// (<see cref="GameClient.RemotePlayers"/> at the render tick) and the pose it draws it in (the client's rules, <see cref="FlightPoseRules.Step"/> on
/// the drawn states: take-off window, damped fraction, low or high set); the server records the target and the pose it keeps for it at every tick. Each
/// sample is a shot from 20 m (8 directions) at the centre of every drawn volume, tested against the target as the server rewinds it: the rest volumes at
/// the rounded view tick (before this branch), the undamped flight volumes between ticks (the branch's first commit), and the server's running pose
/// between ticks (now).
/// </summary>
public class FlyingHitboxProbe(ITestOutputHelper output)
{
    static readonly Lazy<string?> Game = new(() => GameInstall.TryLocate()?.Dir);
    static readonly Lazy<SimConfig?> Tuned = new(() => Game.Value is { } g ? GameplayTuning.Load(g).Config : null);

    public sealed class InstallFactAttribute : FactAttribute
    {
        public InstallFactAttribute()
        {
            if (Game.Value is null) Skip = "Space Marine install not found (needs the player's install; never in CI)";
        }
    }

    sealed record Sample(uint Tick, double RenderTick, PlayerState Drawn, FlightView DrawnView);

    [InstallFact]
    public void Shots_at_the_drawn_body_of_a_jumping_target()
    {
        var c = Tuned.Value!;
        Assert.NotNull(c.FlightHitVolumes);
        int assault = Enumerable.Range(0, c.Classes.Count).First(i => c.Classes[i].JumpPack >= 0 && c.Classes[i].Side == Team.SpaceMarines);
        int shooterClass = Enumerable.Range(0, c.Classes.Count).First(i => c.Classes[i].JumpPack < 0 && c.Classes[i].Side == Team.Chaos);
        float delay = c.JumpPacks[c.Classes[assault].JumpPack].TakeOffDelay;
        foreach (int latency in new[] { 30, 80 })
        {
            var h = new Harness(new SmNet.LinkConditions(TimeSpan.FromMilliseconds(latency), TimeSpan.FromMilliseconds(5), 0), 7, 0, c);
            var shooter = h.AddClient(nonce: 100, options: new ClientOptions { RequestedClass = (byte)shooterClass });
            var target = h.AddClient(nonce: 101, options: new ClientOptions { RequestedClass = (byte)assault });
            var samples = new List<Sample>();
            var server = new Dictionary<uint, (PlayerState State, FlightView View)>();
            int targetId = -1;
            var track = new FlightTrack();   // the shooter's client's view of the target's jump (no level: the high set, as the client)
            double lastRender = double.NaN;
            PlayerInput Input(int index, uint tick)
            {
                if (index == 0)
                {
                    if (targetId > 0 && shooter.RemotePlayers() is var (rt, players) && players.FirstOrDefault(p => p.PlayerId == targetId) is { Alive: true } drawn && rt > 0)
                    {
                        float dt = double.IsNaN(lastRender) ? c.TickSeconds : (float)((rt - lastRender) * c.TickSeconds);
                        lastRender = rt;
                        var view = FlightPoseRules.Step(track, drawn, dt, FlightPoseRules.AboveGround(drawn.Position, null), delay);
                        samples.Add(new Sample(tick, rt, drawn, view));
                    }
                    return new PlayerInput(tick, 0, 0, 0, InputButtons.None);
                }
                // The target: 5 s cycles; hold Jump for a full rise or a cut one, moving forward, sideways or back.
                uint phase = tick % 150, cycle = tick / 150;
                uint hold = cycle % 3 == 0 ? 60u : cycle % 3 == 1 ? 20u : 35u;
                float mx = (cycle % 4) switch { 1 => 1f, 3 => -1f, _ => 0f }, my = (cycle % 4) switch { 0 => 1f, 2 => -1f, _ => 0.3f };
                return new PlayerInput(tick, mx, my, 0.4f, phase < hold ? InputButtons.Jump : InputButtons.None);
            }
            h.Run(TimeSpan.FromSeconds(1));
            targetId = target.PlayerId;
            uint last = 0;
            for (int ms = 0; ms < 40_000; ms++)
            {
                h.Step(Input);
                if (h.Server.World.Tick != last && h.Server.World.Players.TryGetValue(targetId, out var s))
                    server[last = h.Server.World.Tick] = (s, h.Server.World.FlightViewOf(targetId));
            }
            Report(c, latency, samples, server);
        }
    }

    void Report(SimConfig c, int latency, List<Sample> samples, Dictionary<uint, (PlayerState State, FlightView View)> server)
    {
        var rest = c with { FlightHitVolumes = null };
        int n = 0, takeOff = 0, agree = 0;
        // [variant, group]: 0 rest volumes at the rounded tick (before the branch), 1 undamped flight volumes between ticks (the branch's first commit),
        // 2 the server's running pose between ticks (now); groups: every drawn volume, the torso (head, chest, pelvis), head-zone hits at the drawn head.
        var hits = new int[3, 3]; var shots = new int[3];
        var lerpError = new List<float>();
        float maxSpeed = 0f;
        foreach (var s in samples)
        {
            uint floor = (uint)Math.Floor(s.RenderTick);
            if (!server.TryGetValue((uint)Math.Round(s.RenderTick), out var rounded) || !server.TryGetValue(floor, out var a) || !server.TryGetValue(floor + 1, out var b)) continue;
            if (s.DrawnView.Phase == FlightViewPhase.None && a.View.Phase == FlightViewPhase.None) continue;
            n++;
            if (s.DrawnView.Phase == FlightViewPhase.TakeOff) takeOff++;
            maxSpeed = MathF.Max(maxSpeed, s.Drawn.Velocity.Length());
            float f = (float)(s.RenderTick - floor);
            var lerped = World.Between(a.State, b.State, f);
            lerpError.Add(Vector3.Distance(s.Drawn.Position, lerped.Position));
            var serverBody = c.HitBody(lerped, World.Between(a.View, b.View, f));   // as World.SeenAt
            var drawnBody = c.HitBody(s.Drawn, s.DrawnView);
            if (ReferenceEquals(serverBody.HitVolumes, drawnBody.HitVolumes)) agree++;
            var bodies = new[] { (rest.HitBody(rounded.State, null), rounded.State), (c.HitBody(lerped, null), lerped), (serverBody, lerped) };
            foreach (var drawnVolume in drawnBody.HitVolumes!)
            {
                var aim = HitVolumes.ToWorld(PosedHitVolumes.Centre(drawnVolume), s.Drawn.Position, s.Drawn.Yaw);
                bool head = drawnVolume.Zone == HitZone.Head;
                bool torso = head || drawnVolume.Name.StartsWith("Chest", StringComparison.Ordinal) || drawnVolume.Name.StartsWith("Pelvis", StringComparison.Ordinal);
                for (int k = 0; k < 8; k++)
                {
                    float az = k * MathF.PI / 4f;
                    var toShooter = Vector3.Normalize(new Vector3(MathF.Cos(az), MathF.Sin(az), -0.25f));
                    var origin = aim + toShooter * 20f;
                    shots[0]++; if (torso) shots[1]++; if (head) shots[2]++;
                    for (int v = 0; v < 3; v++)
                    {
                        var zone = Combat.HitTest(origin, -toShooter, bodies[v].Item2.Position, bodies[v].Item1, 40f, bodies[v].Item2.Yaw).Zone;
                        if (zone == HitZone.None) continue;
                        hits[v, 0]++; if (torso) hits[v, 1]++; if (head && zone == HitZone.Head) hits[v, 2]++;
                    }
                }
            }
        }
        output.WriteLine($"latency {latency} ms: {n} jumping samples ({takeOff} in the take-off clip; speed up to {maxSpeed:0.0} m/s); the server's pose set equals the drawn one in {Pct(agree, n)}; " +
                         $"drawn feet vs rewound feet between ticks median {Harness.Percentile(lerpError, 0.5):0.000} m, max {lerpError.DefaultIfEmpty().Max():0.000}");
        string[] names = ["rest volumes, rounded tick (before)", "undamped flight volumes, between ticks (first commit)", "the server's running pose, between ticks (now)"];
        for (int v = 0; v < 3; v++)
            output.WriteLine($"  {names[v]}: shots at every drawn volume {Pct(hits[v, 0], shots[0])}, at the drawn torso {Pct(hits[v, 1], shots[1])}, headshots at the drawn head {Pct(hits[v, 2], shots[2])}");
        output.WriteLine($"  ({shots[0]} shots, {shots[1]} at the torso, {shots[2]} at the head)");
        static string Pct(int a, int b) => b == 0 ? "-" : $"{100.0 * a / b:0.0}%";
    }
}
