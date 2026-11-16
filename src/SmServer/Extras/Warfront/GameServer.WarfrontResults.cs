using SmNet.Extras.Warfront.Results;
using SmSim.Extras.Warfront.Campaign;
using SmSim.Extras.Warfront.Results;

namespace SmServer;

/// <summary>The end of a Warfront run (plan section 7): the record, the archive without keys, the chunks to every client (and to whoever joins while the run is over).</summary>
public sealed partial class GameServer
{
    /// <summary>
    /// A contributor for one key from the stats ledger (W4b). The squad part is empty until the squad service records are fed here (W9 and W11). The feeds into the ledger itself (kill, assist, death, revive,
    /// capture, battle) are W3b seams that the lead routes: not wired in this slice, the ledger holds whatever has been fed.
    /// </summary>
    WarfrontContributor WarfrontContributorFor(WarfrontServerState s, ulong key, int joinIndex)
    {
        var c = s.Campaign;
        var l = c.Stats.ToPlayerStats(key);
        var player = l with { Name = c.Player(key)?.Name ?? "", Connected = c.IsConnected(key), JoinIndex = joinIndex };
        return WarfrontContributor.Solo(player);
    }

    void WarfrontFinishRun(WarfrontServerState s)
    {
        var c = s.Campaign;
        try
        {
            var stats = new Dictionary<ulong, WarfrontContributor>();
            for (int i = 0; i < c.JoinOrder.Count; i++) stats[c.JoinOrder[i]] = WarfrontContributorFor(s, c.JoinOrder[i], i);
            var weights = WarfrontScoreWeights.LoadOverride(s.Dir, WfLog);
            var record = WarfrontResults.Build(c, stats, DateTime.UtcNow - s.StartedUtc, DateTime.UtcNow, weights);
            string path = WarfrontResultsArchive.Archive(s.Dir, c, record, DateTime.UtcNow);
            s.ResultsBytes = WarfrontResultsWire.Encode(record);
            s.ResultsTransfer++;
            s.NextResultsTick = 0;
            WfLog($"the run is over ({record.Result}); archived as {Path.GetFileName(path)}; the record ({s.ResultsBytes.Length} bytes) goes to every client, and to anyone who joins while it stands");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        {
            WfLog($"the end of the run could not be archived: {e.Message}");
        }
    }
}
