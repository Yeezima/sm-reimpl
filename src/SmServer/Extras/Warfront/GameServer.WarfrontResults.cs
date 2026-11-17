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
        return new WarfrontContributor(player, _wfWiring?.RecordsOf(key) ?? []);
    }

    /// <summary>
    /// The end of the run, once: the record is built and set for the clients first (its own failure is logged and nothing else is lost), then archived in its own try. <see cref="WarfrontServerState.Finished"/>
    /// stops every later save from writing the finished campaign back over the archive. A run that is over when a stage starts (the archive did not complete) is finished here too.
    /// </summary>
    void WarfrontFinishRun(WarfrontServerState s)
    {
        if (s.Finished) return;
        s.Finished = true;
        WfSaveFlush();   // an autosave still on its way must not land after the archive removed run.json
        var c = s.Campaign;
        WarfrontRunRecord? record = null;
        try
        {
            var stats = new Dictionary<ulong, WarfrontContributor>();
            for (int i = 0; i < c.JoinOrder.Count; i++) stats[c.JoinOrder[i]] = WarfrontContributorFor(s, c.JoinOrder[i], i);
            var weights = WarfrontScoreWeights.LoadOverride(s.Dir, WfLog);
            record = WarfrontResults.Build(c, stats, TimeSpan.FromSeconds(c.PlaySeconds), DateTime.UtcNow, weights);
            s.ResultsBytes = WarfrontResultsWire.Encode(record);
            s.ResultsTransfer++;
            s.NextResultsTick = 0;
            WfLog($"the run is over ({record.Result}, {(int)c.PlaySeconds} s played); the record ({s.ResultsBytes.Length} bytes) goes to every client, and to anyone who joins while it stands");
        }
        catch (Exception e) when (e is ArgumentException or InvalidDataException or InvalidOperationException)
        {
            WfLog($"the record of the run could not be built: {e.Message}");
        }
        try
        {
            string path = record is null ? WarfrontSave.Archive(s.Dir, c, DateTime.UtcNow) : WarfrontResultsArchive.Archive(s.Dir, c, record, DateTime.UtcNow);
            WfLog($"archived as {Path.GetFileName(path)}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        {
            WfLog($"the end of the run could not be archived: {e.Message}; run.json stays and the next start finishes it");
        }
        try { File.Delete(Path.Combine(s.Dir, WarfrontRunFiles.StageFile)); } catch (IOException) { }   // it holds campaign keys; the run is over
        try { File.Delete(Path.Combine(s.Dir, WarfrontSquadsFile)); } catch (IOException) { }
    }
}
