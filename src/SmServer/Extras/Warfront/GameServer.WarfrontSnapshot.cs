using System.Numerics;
using SmNet;
using SmSim;
using SmSim.Extras.Musou.Campaign;

namespace SmServer;

/// <summary>
/// Warfront's snapshot records (Phase B T2b, protocol 94; owner 2026-11-24: "Drop pod marines don't count towards the limit"): a drop pod's Marines are World players in their own id range
/// (<see cref="TfwContracts.PodIdFirst"/>), so with four humans, their retainers and the pods a snapshot may name more than the 16 players a match admits. Then, per recipient, its own player and the
/// other humans go first as slim records, and the AI Marines follow as lean ones (<see cref="Protocol.LeanRecordBytes"/>): the retainers and the pair, then the pod Marines. The lean records sent are the
/// ones that always fit (<see cref="LeanSchedule.Capacity"/>, reserved against every other block at its most, so a burst of projectiles, kills or shots never moves the cut): at four humans 18 with no
/// control points (Warfront has none; 17 with Seize Ground's 8), against 8 retainers, up to 20 pod Marines and the pair when it is out. When they do not all fit, the retainers and the pair always go and
/// the pod Marines take turns, a window sweeping them by id snapshot by snapshot (<see cref="LeanSchedule.Pick"/>; at four humans with four pods every one is sent at least every 3 snapshots, 0.1 s at
/// 30 Hz), and the client holds a missing lean record for up to a second (<see cref="LeanHold"/>), so every pod Marine is drawn (owner rule: a pod always brings its Marines). With 16 records or fewer
/// nothing changes (protocol 91's slim records). The scoreboard lists the pod Marines last for the same reason. Both return at once unless the match runs Warfront.
/// </summary>
public sealed partial class GameServer
{
    /// <summary>Each recipient's count of crowded snapshots (the sweep's sequence number; a few entries a battle, one per player id that received one).</summary>
    readonly Dictionary<int, long> _warfrontLeanSequence = new();

    partial void WarfrontSnapshotRecords(int self, Vector3 viewer, int points, ref PlayerState[] records, ref int lean)
    {
        if (records.Length <= Protocol.MaxMatchPlayers || World.Tfw is not { Warfront: not null } run) return;
        long sequence = _warfrontLeanSequence.GetValueOrDefault(self);
        _warfrontLeanSequence[self] = sequence + 1;
        records = WarfrontSnapshotPick(records, self, viewer, id => run.MarineOf(id)?.Pod?.At, points, sequence, out lean);
    }

    /// <summary>
    /// The records of crowded Warfront snapshot number <paramref name="sequence"/> for recipient <paramref name="self"/> (ordered as <see cref="WarfrontSnapshotOrder(PlayerState[], int, Vector3, Func{int, Vector3?}, out int, out int)"/>),
    /// with only the lean records that always fit beside the slim ones and <paramref name="points"/> control points: all of them when they fit, else the retainers and the pair and a sweeping window of the
    /// pod Marines by id (<see cref="LeanSchedule.Pick"/>). <paramref name="lean"/>: the lean records among those returned (the last ones).
    /// </summary>
    internal static PlayerState[] WarfrontSnapshotPick(PlayerState[] records, int self, Vector3 viewer, Func<int, Vector3?> podAt, int points, long sequence, out int lean)
    {
        var ordered = WarfrontSnapshotOrder(records, self, viewer, podAt, out int all, out int fixedHead);
        int slim = ordered.Length - all;
        bool full = slim > 0 && self != 0 && ordered[0].PlayerId == self;
        int capacity = LeanSchedule.Capacity(slim - (full ? 1 : 0), full, points);
        lean = all;
        if (all <= capacity) return ordered;
        var tail = ordered[slim..];
        Array.Sort(tail, fixedHead, all - fixedHead, Comparer<PlayerState>.Create((a, b) => a.PlayerId.CompareTo(b.PlayerId)));   // the sweep needs a fixed order: the pod Marines by id
        var picked = LeanSchedule.Pick(all, fixedHead, capacity, sequence);
        lean = picked.Length;
        return [.. ordered.Take(slim), .. picked.Select(k => tail[k])];
    }

    /// <summary>
    /// The order of a crowded Warfront snapshot (more than <see cref="Protocol.MaxMatchPlayers"/> records) for recipient <paramref name="self"/> at <paramref name="viewer"/>: the recipient, the other humans
    /// by id, then (lean, <paramref name="lean"/> of them) the other AI Marines by id and the pod Marines (a pod id with a pod, <paramref name="podAt"/>) by their pod's distance, then id.
    /// </summary>
    internal static PlayerState[] WarfrontSnapshotOrder(PlayerState[] records, int self, Vector3 viewer, Func<int, Vector3?> podAt, out int lean) =>
        WarfrontSnapshotOrder(records, self, viewer, podAt, out lean, out _);

    /// <summary>As above; <paramref name="fixedHead"/>: the lean records before the pod Marines (the retainers and the pair).</summary>
    internal static PlayerState[] WarfrontSnapshotOrder(PlayerState[] records, int self, Vector3 viewer, Func<int, Vector3?> podAt, out int lean, out int fixedHead)
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
        fixedHead = marines.Count;
        return [.. humans, .. marines, .. pods.Select(x => x.Player)];
    }

    partial void WarfrontScoreRank(int id, ref int rank)
    {
        if (World.Tfw is { Warfront: not null } && TfwContracts.IsPodId(id)) rank = 1;
    }
}
