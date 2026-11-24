using System.Numerics;
using SmNet;
using SmSim;
using SmSim.Extras.Musou.Campaign;

namespace SmServer;

/// <summary>
/// Warfront's snapshot records (Phase B T2b, protocol 94; owner 2026-11-24: "Drop pod marines don't count towards the limit"): a drop pod's Marines are World players in their own id range
/// (<see cref="TfwContracts.PodIdFirst"/>), so with four humans, their retainers and the pods a snapshot may name more than the 16 players a match admits. Then, per recipient, its own player and the
/// other humans go first as slim records, and the AI Marines follow as lean ones (<see cref="Protocol.LeanRecordBytes"/>): the retainers and the pair, then the pod Marines pod by pod, the nearest pod
/// first. The writer leaves out the last records past the 1200-byte budget, so only the farthest pod's Marines can drop out of a crowded snapshot. With 16 records or fewer nothing changes (protocol 91's
/// slim records). The scoreboard lists the pod Marines last for the same reason. Both return at once unless the match runs Warfront.
/// </summary>
public sealed partial class GameServer
{
    partial void WarfrontSnapshotRecords(int self, Vector3 viewer, ref PlayerState[] records, ref int lean)
    {
        if (records.Length <= Protocol.MaxMatchPlayers || World.Tfw is not { Warfront: not null } run) return;
        records = WarfrontSnapshotOrder(records, self, viewer, id => run.MarineOf(id)?.Pod?.At, out lean);
    }

    /// <summary>
    /// The order of a crowded Warfront snapshot (more than <see cref="Protocol.MaxMatchPlayers"/> records) for recipient <paramref name="self"/> at <paramref name="viewer"/>: the recipient, the other humans
    /// by id, then (lean, <paramref name="lean"/> of them) the other AI Marines by id and the pod Marines (a pod id with a pod, <paramref name="podAt"/>) by their pod's distance, then id.
    /// </summary>
    internal static PlayerState[] WarfrontSnapshotOrder(PlayerState[] records, int self, Vector3 viewer, Func<int, Vector3?> podAt, out int lean)
    {
        var humans = new List<PlayerState>(records.Length);
        var marines = new List<PlayerState>();
        var pods = new List<(float Distance, PlayerState Player)>();
        foreach (var p in records)
        {
            if (!TfwContracts.IsPuppet(p.PlayerId) || p.PlayerId == self) humans.Add(p);
            else if (TfwContracts.IsPodId(p.PlayerId) && podAt(p.PlayerId) is { } at) pods.Add((Vector3.Distance(at, viewer), p));
            else marines.Add(p);
        }
        humans.Sort((a, b) => a.PlayerId == b.PlayerId ? 0 : a.PlayerId == self ? -1 : b.PlayerId == self ? 1 : a.PlayerId.CompareTo(b.PlayerId));
        marines.Sort((a, b) => a.PlayerId.CompareTo(b.PlayerId));
        pods.Sort((a, b) => a.Distance != b.Distance ? a.Distance.CompareTo(b.Distance) : a.Player.PlayerId.CompareTo(b.Player.PlayerId));   // whole pods together: a pod's Marines share its distance
        lean = marines.Count + pods.Count;
        return [.. humans, .. marines, .. pods.Select(x => x.Player)];
    }

    partial void WarfrontScoreRank(int id, ref int rank)
    {
        if (World.Tfw is { Warfront: not null } && TfwContracts.IsPodId(id)) rank = 1;
    }
}
