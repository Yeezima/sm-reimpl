using System.Numerics;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront;

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
    int _wfPushWave, _wfPushPoint = -1;
    bool _wfPushFlankSent, _wfPushRallySeen;

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
        if (!ReferenceEquals(battle, _wfPushBattle)) { _wfPushBattle = battle; _wfPushState = 0; _wfPushAt = tick + (uint)(3 * rate); _wfPushVoxSeen = 0; _wfPushRallySeen = false; _wfPushPoint = -1; }
        if (battle.WarfrontVoxNewest is { } line && line.Sequence != _wfPushVoxSeen)
        {
            _wfPushVoxSeen = line.Sequence;
            WfLog($"guard vox {line.Sequence} {line.Kind} from {line.From} to {line.To} at tick {line.Tick}");
        }
        int human = run.InField.FirstOrDefault(id => World.Players.TryGetValue(id, out var p) && p.Alive);
        if (tick % (uint)rate == 0 && _wfPushState >= 1)
        {
            QuietNonArmy(battle, _wfPushState == 3 && _wfPushPoint >= 0 ? battle.Layout.Positions[_wfPushPoint] : (Vector3?)null);
            if (human != 0 && World.Players.TryGetValue(human, out var hp) && Vector3.Distance(hp.Position, battle.Layout.LandingZone) > 12f) World.TfwRespawnAt(human, battle.Layout.LandingZone, 0f);
        }
        if (_wfPushState is >= 1 and <= 3 && tick % (uint)(5 * rate) == 0)
            WfLog(string.Join(" | ", Enumerable.Range(0, 3).Select(k => $"{(char)('A' + k)} {battle.Objectives[k].State} pressure {battle.WarfrontPressure(k):0} men {battle.WarfrontMen(k)} line {battle.WarfrontLineOf(k)}{(battle.WarfrontFlankOn(k) ? " flank" : "")}"))
                + $" | Orks {World.Musou?.Alive}, Guardsmen {World.TfwGuards?.Alive}, vox {battle.WarfrontVoxLimiter?.Made ?? 0} (test aid SM_WF_GUARD_PUSH)");
        if (_wfPushState is 0 or 1 or >= 4 && tick < _wfPushAt) return;   // the wave and break phases run every tick and keep their own deadlines
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
                int men = Math.Max(battle.WarfrontMen(0), 40);
                int size = Math.Clamp(men * 14, 400, 1400);   // round 5: sized from the line's men so it breaks under the overwhelm rules (14 men's weight a man at 0.6 elites is about 26x the men)
                var wave = battle.SendWaveForTests(size, 0);
                if (wave is not null) wave.FarElites = size * 0.6f;
                _wfPushWave = size;
                _wfPushState = 2; _wfPushAt = tick + (uint)(180 * rate); _wfPushFlankSent = false; _wfPushPoint = -1;
                WfLog($"test aid SM_WF_GUARD_PUSH: phase wave: {size} Orks (0.6 elites) at A, {men} men there ({(wave is null ? "refused" : "sent")})");
                return;
            }
            case 2:
            {
                if (!_wfPushFlankSent && battle.WarfrontPressure(0) >= 40f)   // the main push is on A: the flank arm comes from the side (half its size, so the block passes the 35 % share)
                {
                    _wfPushFlankSent = true;
                    var point = battle.Layout.Positions[0];
                    var back = new Vector2(battle.Layout.Hq.X - point.X, battle.Layout.Hq.Y - point.Y);
                    back = back.LengthSquared() < 4f ? Vector2.UnitY : Vector2.Normalize(back);
                    float a = 110f * MathF.PI / 180f;
                    var dir = new Vector2(back.X * MathF.Cos(a) - back.Y * MathF.Sin(a), back.X * MathF.Sin(a) + back.Y * MathF.Cos(a));
                    var spot = new Vector3(point.X + dir.X * 60f, point.Y + dir.Y * 60f, point.Z);
                    if (World.Musou?.Nav is { } nav && nav.Nearest(spot, 25f, 25f) is var node && node >= 0) spot = nav.Position(node);
                    int flank = Math.Max(150, _wfPushWave / 2);
                    battle.AddArmyForTests(TfwArmySide.Ork, TfwArmyOrder.Attack, 0, flank, spot, MathF.Atan2(dir.X, -dir.Y), 0.3f);
                    WfLog($"test aid SM_WF_GUARD_PUSH: phase flank: {flank} Orks 60 m off A's side (pressure at A {battle.WarfrontPressure(0):0})");
                }
                if (_wfPushFlankSent && battle.WarfrontLineOf(0) == WarfrontLineState.Holding && tick % (uint)rate == 0 && World.TfwGuards is { } guards)
                {
                    // AIDED (round 5): the line's crisis is scripted: every squad standing for A is cut to 3 men and A's garrison aggregate to 3, so the line has no healthy squad under the push and the
                    // journey (Line breaking, Falling back, Rallying) shows in every run; the rules that decide each step stay the battle's own
                    int cut = 0;
                    var aAt = battle.Layout.Positions[0];
                    for (int q = 0; q < GuardSim.MaxSquads; q++)
                    {
                        bool forA = battle.SquadPost(q) == 0 || (battle.GarrisonOf(0)?.Squads.Contains(q) ?? false);
                        while (forA && guards.AliveIn(q) > WarfrontGuardRules.BatteredMen && guards.KillOne(q, aAt)) cut++;
                    }
                    if (battle.GarrisonOf(0) is { FarCount: > 3 } g) { cut += g.FarCount - 3; TfwBattle.LoseForTests(g, g.FarCount - 3); }
                    if (cut > 0) WfLog($"test aid SM_WF_GUARD_PUSH: AIDED: {cut} Guardsmen of A's line taken out (each squad left at {WarfrontGuardRules.BatteredMen} men)");
                }
                for (int k = 0; k < 3 && _wfPushPoint < 0; k++) if (battle.WarfrontLineOf(k) == WarfrontLineState.Withdrawn) _wfPushPoint = k;
                if (_wfPushPoint < 0 && tick < _wfPushAt) return;
                if (_wfPushPoint < 0) _wfPushPoint = 0;
                _wfPushState = 3; _wfPushAt = tick + (uint)(90 * rate);
                WfLog($"test aid SM_WF_GUARD_PUSH: phase break: the Marines clear 70 m round {(char)('A' + _wfPushPoint)} every second (line {battle.WarfrontLineOf(_wfPushPoint)})");
                return;
            }
            case 3:
                if (battle.WarfrontLineOf(_wfPushPoint) == WarfrontLineState.Rallying && !_wfPushRallySeen) { _wfPushRallySeen = true; WfLog($"test aid SM_WF_GUARD_PUSH: phase rally: {(char)('A' + _wfPushPoint)} is rallying"); }
                if (!(_wfPushRallySeen && battle.WarfrontLineOf(_wfPushPoint) == WarfrontLineState.Holding) && tick < _wfPushAt) return;
                _wfPushState = 4;
                WfLog($"test aid SM_WF_GUARD_PUSH: done (line {(char)('A' + _wfPushPoint)} {battle.WarfrontLineOf(_wfPushPoint)}, withdrawals {battle.WarfrontWithdrawals}, rallies {battle.WarfrontLineRallies}, flanks {battle.WarfrontFlanksRaised}, vox lines {battle.WarfrontVoxLimiter?.Made ?? 0})");
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
            bool near = breakAt is { } p && new Vector2(all[k].Position.X - p.X, all[k].Position.Y - p.Y).Length() <= SmSim.Extras.Warfront.WarfrontLineRules.RallyRingRadius;
            if (near || !army.Contains(all[k].Id)) musou.Damage(all[k].Id, 0, 1e6f, Vector3.Zero, out _);
        }
    }
}
