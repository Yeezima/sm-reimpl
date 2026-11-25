using System.Numerics;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront.Chaos.Finale;

namespace SmServer;

/// <summary>
/// <c>SM_WF_FINALE=won|lost</c> (Chaos Warfront C5c test aid; a test script, not a game rule): in a Chaos fortress battle on a server started with <c>--dev-tools</c> on a loopback bind (<see cref="DevToolsOn"/>), it
/// drives the real relay without a real fight.
/// <list type="bullet">
/// <item>Bodyguard: every 2 s a line with the Lord's place, state and health (the proof that he stays home, idle and shielded); <c>SM_WF_FINALE_AFTER</c> seconds (default 15) after the first human is in the
/// field every Chosen is removed with no credit (<c>quiet</c>, attacker 0), so the World takes its real snapshot the next tick.</item>
/// <item>Duel: <c>SM_WF_FINALE_HOLD</c> seconds (default 6) after each entry, <c>won</c> deals the Lord lethal hits from the duelist (the duel's own gate admits only his; the per-hit cap makes it a few ticks), <c>lost</c>
/// gives every duelist the sim's test kill, so the real handoff, the next entry and finally <c>FinaleLost</c> follow.</item>
/// </list>
/// Each step is a <c>test aid SM_WF_FINALE</c> line. Refused (one line) without the developer-tools gate or without a private <c>SM_WF_DIR</c> (<see cref="FinaleAidRefusal"/>).
/// </summary>
public sealed partial class GameServer
{
    static readonly string? WfFinaleMode = Environment.GetEnvironmentVariable("SM_WF_FINALE") is "won" or "lost" ? Environment.GetEnvironmentVariable("SM_WF_FINALE") : null;
    static double WfFinaleSeconds(string name, double fallback) =>
        double.TryParse(Environment.GetEnvironmentVariable(name), System.Globalization.CultureInfo.InvariantCulture, out double v) && v >= 0 ? v : fallback;

    TfwBattle? _wfFinaleBattle;
    bool _wfFinaleRefused, _wfFinaleChosenGone;
    uint _wfFinaleFieldAt, _wfFinaleNextLog, _wfFinaleStrikeAt;
    int _wfFinaleAttempt = -1;

    /// <summary>
    /// Why the aid refuses to act (null: it may): developer tools on a loopback bind (<see cref="DevToolsOn"/>; a non-loopback bind never has them) and a private run folder (<c>SM_WF_DIR</c> set), so a lost
    /// relay it forces can never end the owner's real campaign (Fable review, safety).
    /// </summary>
    internal static string? FinaleAidRefusal(bool devToolsOn, string? wfDir) =>
        !devToolsOn ? "it needs --dev-tools on a loopback bind (--bind 127.0.0.1)"
        : string.IsNullOrWhiteSpace(wfDir) ? "it needs a private run folder (SM_WF_DIR); it never acts on the default campaign"
        : null;

    void WarfrontFinaleAid(WarfrontServerState s)
    {
        if (WfFinaleMode is not { } mode) return;
        if (FinaleAidRefusal(DevToolsOn, Environment.GetEnvironmentVariable("SM_WF_DIR")) is { } why)
        {
            if (!_wfFinaleRefused) { _wfFinaleRefused = true; WfLog("test aid SM_WF_FINALE refused: " + why); }
            return;
        }
        if (World.Tfw is not { Battle: { ChaosFortress: true } battle, Warfront: { } rules } || World.Musou is not { } musou) { _wfFinaleBattle = null; return; }
        int rate = WarfrontRate;
        if (!ReferenceEquals(battle, _wfFinaleBattle))
        {
            _wfFinaleBattle = battle;
            _wfFinaleChosenGone = false;
            _wfFinaleFieldAt = 0;
            _wfFinaleNextLog = World.Tick;
            _wfFinaleAttempt = -1;
            WfLog($"test aid SM_WF_FINALE={mode}: a Chaos fortress battle (Chosen made {battle.FortressBossOrksMade}, the Lord #{battle.BossId})");
        }
        var duel = World.WarfrontChaosDuel;
        ushort lord = battle.BossId;
        if (duel is null || duel.Phase == WarfrontDuelPhase.Bodyguard)
        {
            if (World.Tick >= _wfFinaleNextLog && lord != 0 && musou.TryGet(lord, out MusouEnemy l))
            {
                _wfFinaleNextLog = World.Tick + (uint)(2 * rate);
                var home = battle.FortressPlan?.BossSpot ?? battle.Layout.BossSpot;
                WfLog($"test aid SM_WF_FINALE: bodyguard: the Lord at ({l.Position.X:0.0},{l.Position.Y:0.0}) {Vector2.Distance(new(l.Position.X, l.Position.Y), new(home.X, home.Y)):0.0} m from home, state {l.State}, health {l.Health:0.#}; Chosen standing {battle.ChaosChosenStanding}");
            }
            if (_wfFinaleFieldAt == 0 && World.Players.Any(kv => kv.Value.Alive && !TfwContracts.IsPuppet(kv.Key))) _wfFinaleFieldAt = World.Tick + (uint)(WfFinaleSeconds("SM_WF_FINALE_AFTER", 15) * rate);
            if (!_wfFinaleChosenGone && _wfFinaleFieldAt != 0 && World.Tick >= _wfFinaleFieldAt)
            {
                _wfFinaleChosenGone = true;
                int n = 0;
                foreach (ushort id in battle.BossOrks.ToList()) if (musou.Damage(id, 0, 1e7f, Vector3.Zero, out bool killed, quiet: true) && killed) n++;
                WfLog($"test aid SM_WF_FINALE: {n} Chosen removed (attacker 0, quiet: no credit); the relay starts on the next tick");
            }
            return;
        }
        if (duel.Phase != WarfrontDuelPhase.Duel || duel.Current is not { } key || !rules.Progression.TryId(key, out int duelist)) return;
        if (duel.AttemptIndex != _wfFinaleAttempt)
        {
            _wfFinaleAttempt = duel.AttemptIndex;
            _wfFinaleStrikeAt = World.Tick + (uint)(WfFinaleSeconds("SM_WF_FINALE_HOLD", 6) * rate);
            WfLog($"test aid SM_WF_FINALE: attempt {duel.AttemptIndex + 1}: P{duelist} entered; {mode} in {WfFinaleSeconds("SM_WF_FINALE_HOLD", 6):0.#} s");
        }
        if (World.Tick < _wfFinaleStrikeAt) return;
        if (mode == "won")
        {
            if (lord != 0 && musou.TryGet(lord, out MusouEnemy l) && l.Alive)
            {
                musou.Damage(lord, duelist, l.Health + 1f, Vector3.Zero, out bool killed);
                if (killed) WfLog($"test aid SM_WF_FINALE: P{duelist}'s hit killed the Lord");
            }
        }
        else if (World.Players.TryGetValue(duelist, out var p) && p.Alive)
        {
            World.TfwKillForTests(duelist);
            WfLog($"test aid SM_WF_FINALE: P{duelist} falls (the sim's test kill) with the Lord at {(musou.TryGet(lord, out MusouEnemy l) ? l.Health : 0f):0.###} hp");
            _wfFinaleStrikeAt = uint.MaxValue;
        }
    }
}
