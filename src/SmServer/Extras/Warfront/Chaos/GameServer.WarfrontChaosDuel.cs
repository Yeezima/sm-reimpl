using SmNet;
using SmNet.Extras.Warfront;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront.Chaos.Finale;

namespace SmServer;

/// <summary>
/// The Chaos finale on the server (Chaos Warfront C5; tag 51 kinds 16 and 17, <see cref="WarfrontChaosDuelWire"/>): only on a Chaos stage (C4's sub-partials). The duel state goes to every identified client on
/// every change and twice a second once the finale is past the bodyguard (one last time when it ends); each of the Lord's telegraphs three times, three ticks apart. The World's finale lines (snapshot,
/// entries, defeats, disconnects, handoffs with the Lord's health bits, the win or the loss) go to the Warfront log, and so does every phase change. A disconnect needs no hook: the World's duel step sees the
/// challenger gone from its players and advances. The retainer bench and the squads' records for the ranking come from the squad wiring.
/// </summary>
public sealed partial class GameServer
{
    WarfrontDuel? _wfDuelSeen;
    int _wfDuelVersion = -1, _wfTelegraphsTaken;
    WarfrontDuelPhase _wfDuelPhaseSeen;
    WarfrontDuel? _wfDuelLastFor;
    float _wfDuelLastHealth, _wfDuelLastMax;
    uint _wfDuelEndedAt;
    readonly List<(byte[] Bytes, int Left, uint Next)> _wfTelegraphSends = new();

    /// <summary>
    /// C5's seam in the battle's end (<c>WarfrontNodeEnded</c>): the battle's loss is the campaign's <c>FinaleLost</c> when the relay ran out; and the battle left by its last human while the relay was on is
    /// <c>FinaleLost</c> too (lead 40, 2026-11-25: quitting never dodges the owner's one-attempt rule), not the "nobody connected, no result" path.
    /// </summary>
    partial void WarfrontChaosFinaleLost(TfwMapResult result, ref bool lost);

    partial void WarfrontChaosFinaleLost(TfwMapResult result, ref bool lost)
    {
        if (!WarfrontChaosStage || result == TfwMapResult.Won) return;
        if (World.WarfrontFinaleLost) { lost = true; WfLog("chaos finale: the relay ran out: FinaleLost"); return; }
        if (result == TfwMapResult.Left && World.WarfrontDuelActive) { lost = true; WfLog("chaos finale: the last human left during the duel relay: FinaleLost"); }
    }

    /// <summary>Tests (C5c review): what the battle's end maps to here, without a campaign deployment (the harness's forced fortress has none).</summary>
    internal bool WarfrontChaosFinaleLostForTests(TfwMapResult result) { bool lost = false; WarfrontChaosFinaleLost(result, ref lost); return lost; }

    partial void WarfrontChaosDuelCreated()
    {
        World.WarfrontDuelBenchRetainers = () => _wfWiring?.Retainers.EndBattle();   // Phase B T2 (series-37 integration): the retainers are World players; DuelIsolate dismisses them (TfwDismissMarines) and this clears their battle records, so none stands or re-lands
        World.WarfrontDuelSquadOf = key => _wfWiring?.RecordsOf(key) ?? [];
        WfLog($"chaos stage: the finale on (rules {WarfrontDuelRules.Version}): the bodyguard, then the duel relay; state on tag {WarfrontChaosWire.Tag} kinds {WarfrontChaosDuelWire.KindState}/{WarfrontChaosDuelWire.KindTelegraph}");
    }

    partial void WarfrontChaosDuelSend()
    {
        if (_wf is not { } s) return;
        uint tick = World.Tick;
        var duel = World.WarfrontChaosDuel;
        if (!ReferenceEquals(duel, _wfDuelSeen))
        {
            _wfDuelSeen = duel;
            _wfDuelVersion = -1;
            _wfDuelPhaseSeen = WarfrontDuelPhase.Bodyguard;
            _wfTelegraphsTaken = World.Musou?.LordTelegraphCount ?? 0;
            _wfTelegraphSends.Clear();
        }
        if (duel is null) return;
        var ids = s.KeyOf.Keys.ToList();
        if (duel.Phase != _wfDuelPhaseSeen)
        {
            WfLog($"chaos finale: phase {_wfDuelPhaseSeen} -> {duel.Phase}");
            _wfDuelPhaseSeen = duel.Phase;
            if (duel.Phase is WarfrontDuelPhase.Won or WarfrontDuelPhase.Lost) _wfDuelEndedAt = tick;
        }
        bool changed = duel.Version != _wfDuelVersion;
        int every = Math.Max(1, WarfrontRate / WarfrontDuelRules.StateHz);
        bool ended = duel.Phase is WarfrontDuelPhase.Won or WarfrontDuelPhase.Lost;
        bool resend = !ended || tick - _wfDuelEndedAt < (uint)(WarfrontDuelRules.EndResendSeconds * WarfrontRate);   // the end is repeated a few seconds, then only a change is sent (Fable review C5s, finding 2)
        if (duel.Phase != WarfrontDuelPhase.Bodyguard && (changed || resend && tick % (uint)every == 0))
        {
            _wfDuelVersion = duel.Version;
            byte[] state = WarfrontChaosDuelWire.Encode(DuelState(duel));
            foreach (int id in ids) SendExtras(new ExtrasUpdate(tick, [(WarfrontChaosWire.Tag, state)]), id);
        }
        if (World.Musou is { } musou)
        {
            int count = musou.LordTelegraphCount, kept = musou.LordTelegraphs.Count;
            for (; _wfTelegraphsTaken < count; _wfTelegraphsTaken++)
            {
                int at = kept - (count - _wfTelegraphsTaken);
                if (at < 0) continue;   // older than the list keeps
                _wfTelegraphSends.Add((WarfrontChaosDuelWire.Encode(WarfrontChaosDuelWire.Of(musou.LordTelegraphs[at])), WarfrontDuelRules.TelegraphRepeats, tick));
            }
        }
        for (int k = _wfTelegraphSends.Count - 1; k >= 0; k--)
        {
            var t = _wfTelegraphSends[k];
            if (tick < t.Next) continue;
            foreach (int id in ids) SendExtras(new ExtrasUpdate(tick, [(WarfrontChaosWire.Tag, t.Bytes)]), id);
            if (t.Left <= 1) _wfTelegraphSends.RemoveAt(k);
            else _wfTelegraphSends[k] = (t.Bytes, t.Left - 1, tick + WarfrontDuelRules.TelegraphRepeatTicks);
        }
    }

    /// <summary>The wire's state now: the champion and the next present challenger by player id, the queue left, the attempt, the Lord's health.</summary>
    WarfrontChaosDuelWire.State DuelState(WarfrontDuel duel)
    {
        var rt = World.Tfw?.Warfront?.Progression;
        int Pid(ulong? key) => key is { } k && rt is not null && rt.TryId(k, out int id) ? id : 0;
        ulong? next = duel.Next(k => rt is not null && rt.TryId(k, out int id) && World.Players.ContainsKey(id));
        float health = 0f, max = World.Tfw?.Battle?.BossMaxHealth ?? 0f;
        if (!ReferenceEquals(duel, _wfDuelLastFor)) { _wfDuelLastFor = duel; _wfDuelLastHealth = 0f; _wfDuelLastMax = 0f; }
        if (World.Musou is { } musou && musou.TryGet(duel.LordId, out MusouEnemy lord) && lord.Alive) { health = lord.Health; if (max > 0f) { _wfDuelLastHealth = health; _wfDuelLastMax = max; } }
        else if (duel.Phase == WarfrontDuelPhase.Lost) { health = _wfDuelLastHealth; max = _wfDuelLastMax; }   // C5c review: the battle's leave cleared the pool and the battle the same tick; the Lord stood at his last health
        return WarfrontChaosDuelWire.Of(duel.Phase, Pid(duel.Current), Pid(next), duel.Remaining, duel.Current is null ? -1 : duel.AttemptIndex, duel.LordId, health, max);
    }
}
