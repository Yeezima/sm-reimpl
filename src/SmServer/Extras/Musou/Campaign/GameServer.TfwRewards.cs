using SmNet;
using SmNet.Extras.Musou.Campaign;
using SmSim;
using SmSim.Extras.Musou.Campaign;

namespace SmServer;

/// <summary>
/// The score, the reward offer and the extra slots on the wire (M6, M6b; tags 12, 14 and 15), one message per client per tick at most, only in a war. Each part goes out on a change and once a second, so a lost datagram
/// costs at most a second: the score's own part at most 10 times a second, the others and the history a second apart, the history at once when a map banks (about 1 KB/s in all, the addendum's). Each banked map and
/// the run's end are logged, so a test can hold what a client shows against them.
/// </summary>
public sealed partial class GameServer
{
    sealed class TfwSent
    {
        public byte[]? Own, History, Offer, Arsenal;
        public uint OwnTick, OthersTick, HistoryTick, OfferTick, ArsenalTick;
    }

    /// <summary>The fastest the own score goes, in ticks at 30 Hz (the addendum: 10 Hz).</summary>
    const uint TfwScoreEveryTicks = 3;

    readonly Dictionary<int, TfwSent> _tfwSent = new();
    readonly Dictionary<int, int> _tfwLoggedMaps = new();
    bool _tfwLoggedEnd;

    void SendTfwRewards()
    {
        if (World.Tfw is not { Idle: false } run) return;   // a test's standing run feeds no wire
        TfwLogScores(run);
        uint tick = World.Tick, second = (uint)Math.Max(1, World.Config.TickRate);
        var others = new List<TfwScorePlayer>();
        foreach (int id in run.Score.Players) if (id is > 0 and <= ushort.MaxValue) others.Add(new TfwScorePlayer((ushort)id, run.Score.TotalOf(id)));

        foreach (var peer in _peers.Values)
        {
            int id = peer.PlayerId;
            if (!_tfwSent.TryGetValue(id, out var sent)) _tfwSent[id] = sent = new TfwSent();
            bool Due(uint last) => tick - last >= second || tick < last;
            var sections = new List<(byte, byte[])>(3);

            var own = TfwWire.WriteScore(new TfwScoreUpdate(run.Score.OwnOf(id)));
            bool ownDue = sent.Own is null || Due(sent.OwnTick) || (tick - sent.OwnTick >= TfwScoreEveryTicks && !sent.Own.AsSpan().SequenceEqual(own));
            var history = run.Score.HistoryOf(id);
            int skip = Math.Max(0, history.Count - TfwWire.MaxScoreMaps);
            var historyBytes = TfwWire.WriteScore(new TfwScoreUpdate(null, History: history.Skip(skip).ToList()));
            bool historyDue = sent.History is null || !sent.History.AsSpan().SequenceEqual(historyBytes) || Due(sent.HistoryTick);
            bool othersDue = Due(sent.OthersTick);
            if (ownDue || historyDue || othersDue)
            {
                var update = new TfwScoreUpdate(ownDue ? run.Score.OwnOf(id) : null, othersDue ? others.Where(o => o.Player != id).Take(TfwWire.MaxScorePlayers).ToList() : null, historyDue ? history.Skip(skip).ToList() : null);
                sections.Add((TfwWire.ScoreTag, TfwWire.WriteScore(update)));
                if (ownDue) { sent.Own = own; sent.OwnTick = tick; }
                if (historyDue) { sent.History = historyBytes; sent.HistoryTick = tick; }
                if (othersDue) sent.OthersTick = tick;
            }

            if (run.Rewards.Offer(id) is { } offer)
            {
                var bytes = TfwWire.WriteReward(offer);
                if (sent.Offer is null || !sent.Offer.AsSpan().SequenceEqual(bytes) || Due(sent.OfferTick))
                {
                    sections.Add((TfwWire.RewardTag, bytes));
                    sent.Offer = bytes; sent.OfferTick = tick;
                }
            }
            else sent.Offer = null;

            var arsenal = TfwWire.WriteArsenal(World.TfwArsenalOf(id));
            if (sent.Arsenal is null || !sent.Arsenal.AsSpan().SequenceEqual(arsenal) || Due(sent.ArsenalTick))
            {
                sections.Add((TfwWire.ArsenalTag, arsenal));
                sent.Arsenal = arsenal; sent.ArsenalTick = tick;
            }
            if (sections.Count > 0) SendExtras(new ExtrasUpdate(tick, sections.ToArray()), id);
        }
    }

    void TfwLogScores(TfwRun run)
    {
        foreach (int id in run.Score.Players)
        {
            var history = run.Score.HistoryOf(id);
            int logged = _tfwLoggedMaps.GetValueOrDefault(id);
            if (logged > history.Count) logged = 0;   // a new run
            for (; logged < history.Count; logged++)
            {
                var m = history[logged];
                Console.WriteLine($"TFWSCORE player {id} map {logged + 1}: lane {m.Lane} node {m.Node} {m.Result}, KOs {m.Kos}, chain {m.BestChain}, points {m.Points}, total {run.Score.OwnOf(id).RunTotal}");
            }
            _tfwLoggedMaps[id] = logged;
        }
        if (run.Ended && !_tfwLoggedEnd)
        {
            _tfwLoggedEnd = true;
            foreach (int id in run.Score.Players) Console.WriteLine($"TFWSCORE player {id} run {run.Phase}: total {run.Score.TotalOf(id)}");
        }
        else if (!run.Ended) _tfwLoggedEnd = false;
    }
}
