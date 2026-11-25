using System.Numerics;
using SmSim;

namespace SmNet;

/// <summary>
/// Protocol 94 (placeholder number; Phase B T2b review fix): which lean records a crowded snapshot carries, decided so the same ones stay in from snapshot to snapshot. The room is reserved against the
/// worst case of every other block (<see cref="WorstOtherBytes"/>: 12 projectiles, 4 kills, the guaranteed shots), so a burst of projectiles, kills or shots never pushes a record out; when the lean
/// records still do not all fit, the fixed head (the retainers and the allied pair) stays and the tail (the pod Marines) is swept across snapshots in a window, so every one of them is sent at least
/// once every <see cref="SweepSnapshots"/> snapshots and the client's hold (<see cref="LeanHold"/>) draws it in between. Pure and engine-free; only snapshots with lean records use it (Classic never).
/// </summary>
public static class LeanSchedule
{
    /// <summary>The bytes of a snapshot besides its player records with every other block at its most (the projectiles, kills and guaranteed shots) and <paramref name="points"/> control points.</summary>
    public static int WorstOtherBytes(int points) =>
        Protocol.SnapshotBytesOf(Protocol.MaxSnapshotProjectiles, Protocol.MaxSnapshotKills, Math.Clamp(points, 0, Protocol.MaxSnapshotPoints), Protocol.MinSnapshotShots, slimOthers: 0, full: false, lean: 0);

    /// <summary>
    /// The lean records that always fit beside <paramref name="slimOthers"/> slim records and the recipient's full one (<paramref name="full"/>), whatever the other blocks hold: the stable capacity.
    /// At most <see cref="Protocol.MaxPlayers"/> records in all.
    /// </summary>
    public static int Capacity(int slimOthers, bool full, int points)
    {
        int room = Protocol.SnapshotBudget - WorstOtherBytes(points) - slimOthers * Protocol.SlimRecordBytes - (full ? Protocol.FullRecordBytes : 0) - 1;   // 1: the lean block's count byte
        return Math.Clamp(room / Protocol.LeanRecordBytes, 0, Math.Max(0, Protocol.MaxPlayers - slimOthers - (full ? 1 : 0)));
    }

    /// <summary>
    /// The lean records (indices 0..<paramref name="count"/>-1, in the order given) snapshot number <paramref name="sequence"/> carries within <paramref name="capacity"/>: all when they fit; else the first
    /// <paramref name="fixedHead"/> (when fewer than the capacity) and a window of the rest that moves on by its own width each snapshot (wrapping), or, when the head alone fills the capacity, a window
    /// over all of them. Ascending indices.
    /// </summary>
    public static int[] Pick(int count, int fixedHead, int capacity, long sequence)
    {
        if (count <= 0 || capacity <= 0) return [];
        if (count <= capacity) return [.. Enumerable.Range(0, count)];
        int head = Math.Clamp(fixedHead, 0, count);
        if (head >= capacity) head = 0;   // the head alone does not fit: everyone takes turns
        int tail = count - head, width = capacity - head;
        int start = (int)(((sequence % tail + tail) % tail) * width % tail);
        var picked = new List<int>(capacity);
        for (int k = 0; k < head; k++) picked.Add(k);
        for (int k = 0; k < width; k++) picked.Add(head + (start + k) % tail);
        picked.Sort();
        return [.. picked];
    }

    /// <summary>The most snapshots from one send of a record to its next under <see cref="Pick"/> with consecutive sequence numbers (1: sent every snapshot; 0: never, with no capacity).</summary>
    public static int SweepSnapshots(int count, int fixedHead, int capacity)
    {
        if (count <= 0) return 1;
        if (capacity <= 0) return 0;
        if (count <= capacity) return 1;
        int head = Math.Clamp(fixedHead, 0, count);
        if (head >= capacity) head = 0;
        int tail = count - head, width = capacity - head;
        // The window sweeps the tail at its width per snapshot (unrolled: snapshot s covers [s w, s w + w)); a record's covering points lie a tail apart, so at most ceil(tail / width) snapshots apart.
        return (tail + width - 1) / width;
    }
}

/// <summary>
/// Protocol 94 (placeholder number; Phase B T2b review fix): the client's hold for a Warfront AI Marine that arrived as a lean record and is missing from a later snapshot (the server sweeps the pod
/// Marines across snapshots when they do not all fit, <see cref="LeanSchedule"/>). For up to <see cref="HoldSeconds"/> after its last record the Marine is put back into each snapshot it is missing from,
/// at its last pose (carried along its last ground velocity for at most <see cref="CarrySeconds"/>), so the interpolation never sees it leave and its model is never freed and rebuilt on a single miss.
/// A record sent slim (a human, or every record of a snapshot of 16 or fewer) ends the hold of its id. Classic snapshots carry no lean records, so this never acts there. Pure and engine-free.
/// </summary>
public sealed class LeanHold
{
    /// <summary>How long a lean record is held after its last arrival (s; ours: about 30 snapshots at 30 Hz, far past the sweep's at most 3 at four humans).</summary>
    public const double HoldSeconds = 1.0;
    /// <summary>How long a held, grounded, living record is carried along its horizontal velocity (s; ours: the sweep's gap at 30 Hz with room), then stands.</summary>
    public const double CarrySeconds = 0.2;

    readonly Dictionary<int, (PlayerState Last, uint Tick)> _held = new();

    /// <summary>The ids held now (diagnostics and tests).</summary>
    public IReadOnlyCollection<int> Held => _held.Keys;

    public void Clear() => _held.Clear();

    /// <summary>
    /// Notes snapshot <paramref name="tick"/>'s players (<paramref name="players"/>, by id; the last <paramref name="lean"/> of <paramref name="records"/> arrived lean) and fills in every held record
    /// missing from it (<paramref name="tickRate"/> ticks a second). Returns how many were filled in.
    /// </summary>
    public int Apply(uint tick, IReadOnlyList<PlayerState> records, int lean, Dictionary<int, PlayerState> players, int tickRate)
    {
        lean = Math.Clamp(lean, 0, records.Count);
        int slim = records.Count - lean;
        for (int k = 0; k < slim; k++) _held.Remove(records[k].PlayerId);
        for (int k = slim; k < records.Count; k++) _held[records[k].PlayerId] = (records[k], tick);
        if (_held.Count == 0) return 0;
        double holdTicks = HoldSeconds * tickRate;
        int filled = 0;
        List<int>? expired = null;
        foreach (var (id, (last, at)) in _held)
        {
            if (players.ContainsKey(id)) continue;
            if (tick < at || tick - at > holdTicks) { (expired ??= []).Add(id); continue; }
            players[id] = Carried(last, (tick - at) / (double)tickRate);
            filled++;
        }
        foreach (var id in expired ?? []) _held.Remove(id);
        return filled;
    }

    /// <summary>A held record <paramref name="seconds"/> after it arrived: a grounded, living one moved along its horizontal velocity for at most <see cref="CarrySeconds"/>; anything else where it was.</summary>
    public static PlayerState Carried(PlayerState last, double seconds)
    {
        if (!last.Alive || !last.Grounded || !(seconds > 0)) return last;
        float t = (float)Math.Min(seconds, CarrySeconds);
        return last with { Position = last.Position + new Vector3(last.Velocity.X, last.Velocity.Y, 0f) * t };
    }
}
