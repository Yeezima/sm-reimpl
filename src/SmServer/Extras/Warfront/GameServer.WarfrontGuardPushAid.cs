using System.Numerics;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmServer;

/// <summary>
/// Guardsmen G2 live check (test aid; loopback-bound servers with <c>--dev-tools</c> only): <c>SM_WF_GUARD_PUSH=1</c> scripts the controlled push of the tools' trial in a real lane battle, so a headless client sees
/// the vox lines and hears the barks. 3 s after the first human is in the field: A, B and C are secured (<c>ClearAndSecureForTests</c>) and the human is kept at the landing zone; every second every enemy that
/// belongs to no army dies (the controlled stage). 20 s later a 400-Ork wave at 0.6 elites goes for A with a 100-Ork army 80 m off its flank. When A's line reaches Withdrawn (or 120 s after the wave), the Orks within
/// 40 m of A die every second for 60 s, so the push is broken and the line rallies. Each step is logged as <c>test aid SM_WF_GUARD_PUSH</c>; the vox lines as <c>guard vox &lt;seq&gt; &lt;kind&gt;</c>.
/// </summary>
public sealed partial class GameServer
{
    TfwBattle? _wfPushBattle;
    int _wfPushState;   // -1 refused, 0 waiting, 1 secured, 2 wave sent, 3 breaking, 4 done
    uint _wfPushAt;
    ushort _wfPushVoxSeen;

    void WarfrontGuardPushAid(WarfrontServerState s)
    {
        if (Environment.GetEnvironmentVariable("SM_WF_GUARD_PUSH") != "1" || _wfPushState < 0) return;
        if (World.Tfw is not { Battle: { } battle } run || battle.IsWarfrontFortress || battle.WarfrontHq) return;
        var args = Environment.GetCommandLineArgs();
        int at = Array.IndexOf(args, "--bind");
        if (!DevToolsOn || at < 0 || at + 1 >= args.Length || !System.Net.IPAddress.TryParse(args[at + 1], out var bound) || !System.Net.IPAddress.IsLoopback(bound))
        {
            _wfPushState = -1;
            WfLog("test aid SM_WF_GUARD_PUSH refused: it needs --dev-tools and --bind 127.0.0.1");
            return;
        }
        int rate = WarfrontRate;
        uint tick = World.Tick;
        if (!ReferenceEquals(battle, _wfPushBattle)) { _wfPushBattle = battle; _wfPushState = 0; _wfPushAt = tick + (uint)(3 * rate); _wfPushVoxSeen = 0; }
        if (battle.WarfrontVoxNewest is { } line && line.Sequence != _wfPushVoxSeen)
        {
            _wfPushVoxSeen = line.Sequence;
            WfLog($"guard vox {line.Sequence} {line.Kind} from {line.From} to {line.To} at tick {line.Tick}");
        }
        int human = run.InField.FirstOrDefault(id => World.Players.TryGetValue(id, out var p) && p.Alive);
        if (tick % (uint)rate == 0 && _wfPushState >= 1)
        {
            QuietNonArmy(battle, _wfPushState == 3 ? battle.Layout.Positions[0] : (Vector3?)null);
            if (human != 0 && World.Players.TryGetValue(human, out var hp) && Vector3.Distance(hp.Position, battle.Layout.LandingZone) > 12f) World.TfwRespawnAt(human, battle.Layout.LandingZone, 0f);
        }
        if (_wfPushState is >= 1 and <= 3 && tick % (uint)(5 * rate) == 0)
            WfLog(string.Join(" | ", Enumerable.Range(0, 3).Select(k => $"{(char)('A' + k)} {battle.Objectives[k].State} pressure {battle.WarfrontPressure(k):0} men {battle.WarfrontMen(k)} line {battle.WarfrontLineOf(k)}{(battle.WarfrontFlankOn(k) ? " flank" : "")}"))
                + $" | Orks {World.Musou?.Alive}, Guardsmen {World.TfwGuards?.Alive}, vox {battle.WarfrontVoxLimiter?.Made ?? 0} (test aid SM_WF_GUARD_PUSH)");
        if (tick < _wfPushAt) return;
        switch (_wfPushState)
        {
            case 0:
                if (human == 0) return;
                for (int k = 0; k < 3; k++) battle.ClearAndSecureForTests(k);
                World.TfwRespawnAt(human, battle.Layout.LandingZone, 0f);
                _wfPushState = 1; _wfPushAt = tick + (uint)(20 * rate);
                WfLog($"test aid SM_WF_GUARD_PUSH: A, B, C secured; line on {battle.WarfrontLineOn}");
                return;
            case 1:
            {
                var wave = battle.SendWaveForTests(400, 0);
                if (wave is not null) wave.FarElites = 400 * 0.6f;
                var point = battle.Layout.Positions[0];
                var back = new Vector2(battle.Layout.Hq.X - point.X, battle.Layout.Hq.Y - point.Y);
                back = back.LengthSquared() < 4f ? Vector2.UnitY : Vector2.Normalize(back);
                float a = 110f * MathF.PI / 180f;
                var dir = new Vector2(back.X * MathF.Cos(a) - back.Y * MathF.Sin(a), back.X * MathF.Sin(a) + back.Y * MathF.Cos(a));
                var spot = new Vector3(point.X + dir.X * 80f, point.Y + dir.Y * 80f, point.Z);
                if (World.Musou?.Nav is { } nav && nav.Nearest(spot, 25f, 25f) is var node && node >= 0) spot = nav.Position(node);
                battle.AddArmyForTests(TfwArmySide.Ork, TfwArmyOrder.Attack, 0, 100, spot, MathF.Atan2(dir.X, -dir.Y), 0.1f);
                _wfPushState = 2; _wfPushAt = tick + (uint)(120 * rate);
                WfLog($"test aid SM_WF_GUARD_PUSH: wave of 400 (0.6 elites) at A and 100 on its flank (wave {(wave is null ? "refused" : "sent")})");
                return;
            }
            case 2:
                if (battle.WarfrontLineOf(0) != WarfrontLineState.Withdrawn && tick < _wfPushAt) return;
                _wfPushState = 3; _wfPushAt = tick + (uint)(60 * rate);
                WfLog($"test aid SM_WF_GUARD_PUSH: the push on A is broken (line {battle.WarfrontLineOf(0)})");
                return;
            case 3:
                _wfPushState = 4;
                WfLog($"test aid SM_WF_GUARD_PUSH: done (line A {battle.WarfrontLineOf(0)}, withdrawals {battle.WarfrontWithdrawals}, rallies {battle.WarfrontLineRallies}, vox lines {battle.WarfrontVoxLimiter?.Made ?? 0})");
                return;
        }
    }

    /// <summary>Every enemy outside an army dies; with <paramref name="breakAt"/>, every enemy within 40 m of it too (the Marines breaking the push).</summary>
    void QuietNonArmy(TfwBattle battle, Vector3? breakAt)
    {
        if (World.Musou is not { } musou) return;
        var army = new HashSet<ushort>();
        foreach (var a in battle.ArmyList) foreach (ushort id in a.Orks) army.Add(id);
        var all = new MusouEnemy[MusouSim.Capacity];
        int n = musou.CopyTo(all);
        for (int k = 0; k < n; k++)
        {
            if (!all[k].Alive) continue;
            bool near = breakAt is { } p && new Vector2(all[k].Position.X - p.X, all[k].Position.Y - p.Y).Length() <= 40f;
            if (near || !army.Contains(all[k].Id)) musou.Damage(all[k].Id, 0, 1e6f, Vector3.Zero, out _);
        }
    }
}
