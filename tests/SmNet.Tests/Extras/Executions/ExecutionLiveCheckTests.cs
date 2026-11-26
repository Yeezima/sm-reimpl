using System.Net;
using SmContent;
using SmNet.Extras.Executions;
using SmServer;
using SmSim;
using SmSim.Extras.Executions;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Executions;

/// <summary>
/// Executions X3's live Classic check (opt-in: <c>SM_X3_LIVE=1</c>, and the install): the player's own extended match config and rows, the install's Manufactorum, a GameServer on a real UDP socket
/// bound to 127.0.0.1 and two headless SmNet clients over UDP (no Godot). The chainsword player melees the Chaos player to death, taps Execute within the 0.8 s window, and the server's log shows
/// the offer, the Start and the End. The server's world places the pair (a test seam) so the swing connects; everything else is the real match. Lines go to
/// <c>%LOCALAPPDATA%\sm-reimpl\tmp\x3-live.log</c>.
/// </summary>
public class ExecutionLiveCheckTests(ITestOutputHelper output)
{
    sealed class LiveFact : FactAttribute
    {
        public LiveFact()
        {
            if (Environment.GetEnvironmentVariable("SM_X3_LIVE") != "1") Skip = "opt-in live check (SM_X3_LIVE=1)";
            else if (GameInstall.TryLocate() is null) Skip = "no install";
        }
    }

    [LiveFact]
    public void A_live_classic_kill_then_a_tap_plays_a_finisher()
    {
        string install = GameInstall.TryLocate()!.Dir;
        Assert.True(ContentProfiles.TryParse(ContentProfiles.Extended, out var flags, out _));
        var tuning = GameplayTuning.LoadForMatch(install, flags);
        var config = tuning.Config;
        var lines = new List<string>(tuning.Notes.Where(n => n.StartsWith("executions", StringComparison.Ordinal)));
        Assert.NotEmpty(config.Executions.Rows);
        var content = MapContent.Load(install, "mp_tdm_09");
        var collision = content.ToCollisionWorld();
        var map = new GameMap("mp_tdm_09", collision, content.SpawnPoints(), content.Hash, content.SimControlPoints(), content.SimStartAreas(), content.SimBlockers());
        ulong fingerprint = ContentFingerprint.Compute(install);

        int assault = -1, chaos = -1;
        for (int i = 0; i < config.Classes.Count; i++)
        {
            var c = config.Classes[i];
            bool sword = c.MeleeChoices.Any(m => ExecutionWeapons.FamilyOf(m.Wargear) == ExecutionWeapons.Chainsword);
            if (sword && c.Side == Team.SpaceMarines && assault < 0) assault = i;
            if (sword && c.Side == Team.Chaos && chaos < 0) chaos = i;
        }
        Assert.True(assault >= 0 && chaos >= 0, "no chainsword classes");
        lines.Add($"melee: range {config.MeleeRange:0.00} m; the chainsword combo's lunges {string.Join(", ", config.Classes[assault].MeleeChoices.SelectMany(m => m.Steps).Select(s => s.Lunge.ToString("0.00")).Distinct())} m; Chaos Marine row gaps {string.Join(", ", config.Executions.Rows.Where(r => r.Target == ExecutionTargets.ChaosMarine).Select(r => $"{r.Id} {r.DistanceBand.Min:0.00}-{r.DistanceBand.Max:0.00}"))} m");
        int swordChoice = config.Classes[assault].MeleeChoices.Select((m, i) => (m, i)).First(x => ExecutionWeapons.FamilyOf(x.m.Wargear) == ExecutionWeapons.Chainsword).i;

        const int port = 27391;
        using var serverTransport = new UdpServerTransport(new IPEndPoint(IPAddress.Loopback, port));
        var clock = new SystemClock();
        var server = new GameServer(config, fingerprint, serverTransport, clock, new ServerOptions(), map);
        server.Log += l => { lock (lines) lines.Add($"{DateTime.UtcNow:HH:mm:ss.fff} server: {l}"); };
        LoadedMap? Load(string name) => name == "mp_tdm_09" ? new LoadedMap(collision, content.Hash) : null;
        var killer = new GameClient(config, fingerprint, new UdpClientTransport(new IPEndPoint(IPAddress.Loopback, port)), clock,
            new ClientOptions { LoadMap = Load, RequestedClass = (byte)assault, RequestedMelee = (byte)swordChoice, RequestedSide = Team.SpaceMarines }, nonce: 31);
        var victim = new GameClient(config, fingerprint, new UdpClientTransport(new IPEndPoint(IPAddress.Loopback, port)), clock,
            new ClientOptions { LoadMap = Load, RequestedClass = (byte)chaos, RequestedSide = Team.Chaos }, nonce: 32);

        var w = server.World;
        float yaw = 0f;
        bool swing = false, execute = false, placed = false;
        float gap = 0f, forward = 0f;
        uint? killTick = null, pressTick = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
        int heardStarts = 0;
        uint lastNote = 0;
        while (DateTime.UtcNow < deadline)
        {
            server.Update();
            killer.Update(t => new PlayerInput(t, 0, forward, yaw, (swing ? InputButtons.Melee : InputButtons.None) | (execute ? InputButtons.Execute : InputButtons.None)));
            victim.Update(t => new PlayerInput(t, 0, 0, yaw + MathF.PI, InputButtons.None));
            foreach (var s in killer.TakeExtras()) if (s.Tag == ExecutionWire.Tag && ExecutionWire.KindOf(s.Payload) == ExecutionWire.StartKind) heardStarts++;
            victim.TakeExtras();
            Thread.Sleep(2);
            if (w.Tick % 60 == 0 && w.Tick != lastNote) { lastNote = w.Tick; lock (lines) lines.Add($"status tick {w.Tick}: {killer.Status}/{victim.Status} state {w.State} players {string.Join("; ", w.Players.Select(p => $"{p.Key} c{p.Value.Class} {p.Value.Team} alive {p.Value.Alive} hp {p.Value.Health:0} sh {p.Value.Shield:0} at {p.Value.Position}"))}"); }
            if (killer.Status != ClientStatus.Connected || victim.Status != ClientStatus.Connected || w.State is not (MatchState.Playing or MatchState.None or MatchState.Warmup)) continue;
            int k = killer.PlayerId, v = victim.PlayerId;
            if (!w.Players.TryGetValue(k, out var kp) || !w.Players.TryGetValue(v, out var vp)) continue;
            if (killTick is null)
            {
                if (!kp.Alive) continue;
                // A normal melee approach (lead 40: not a staged 1.3 m): the victim is put 4 m ahead once (again only if it ends up over 6 m away), the killer walks up to it
                // and swings from inside 2.8 m; the corpse lies wherever the swing (and its lunge) left it.
                var d = new System.Numerics.Vector2(vp.Position.X - kp.Position.X, vp.Position.Y - kp.Position.Y);
                if (!placed || (vp.Alive && d.Length() > 6f))
                {
                    if (vp.Alive) w.ExecutionsPlaceForTests(v, kp.Position + new System.Numerics.Vector3(-MathF.Sin(kp.Yaw), MathF.Cos(kp.Yaw), 0f) * 4f, kp.Yaw + MathF.PI);
                    placed = vp.Alive;
                    continue;
                }
                yaw = MathF.Atan2(-d.X, d.Y);
                gap = d.Length();
                forward = gap > 2.6f ? 1f : 0f;
                swing = placed && gap <= 2.8f && w.Tick % 12 < 2;   // a press every 0.4 s
                var kill = w.KillLog.LastOrDefault(e => e.Killer == k && e.Victim == v && e.Cause == KillCause.Melee);
                if (kill.Sequence != 0) { killTick = kill.Tick; swing = false; forward = 0f; lock (lines) lines.Add($"kill: player {k} on {v} by melee at tick {kill.Tick}, gap {gap:0.00} m"); }
                continue;
            }
            if (pressTick is null && w.Tick >= killTick + 6)   // 0.2 s after the kill: the tap
            {
                execute = true;
                pressTick = w.Tick;
                lock (lines) lines.Add($"press: Execute at server tick {w.Tick} ({w.Tick - killTick} ticks after the kill)");
                continue;
            }
            if (execute && w.Tick > pressTick + 2) execute = false;   // released
            if (pressTick is not null && w.Tick > pressTick + 30 * 8) break;   // 8 s
        }
        killer.Leave(); victim.Leave();
        for (int i = 0; i < 20; i++) { server.Update(); Thread.Sleep(5); }
        lines.Add($"client: the killer heard {heardStarts} Start sections");
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "sm-reimpl", "tmp");
        Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(dir, "x3-live.log"), lines);
        foreach (var l in lines.Where(l => l.Contains("executions") || l.StartsWith("kill") || l.StartsWith("press") || l.StartsWith("client") || l.StartsWith("status") || l.StartsWith("melee"))) output.WriteLine(l);
        Assert.NotNull(killTick);
        Assert.Contains(lines, l => l.Contains("executions: offer to player"));
        Assert.Contains(lines, l => l.Contains("executions: start player"));
        Assert.Contains(lines, l => l.Contains("executions: end player") && l.Contains("Done"));
        Assert.True(heardStarts >= 3);
    }
}
