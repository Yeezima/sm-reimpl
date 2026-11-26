namespace SmSim.Extras.Executions;

/// <summary>
/// The executions' playable rows and their wire identity, on the simulation's side (X3, lead 39 2026-11-25: the wire identity must reach the sim): <see cref="Rows"/> (the valid rows, in
/// the catalogue's order) and <see cref="WireIds"/> (the source list's ids in wire order: <see cref="ExecutionRows.All"/> for the install loader). <c>SmContent.Extras.Executions.ExecutionCatalogue</c>
/// builds and checks it and wraps it; <see cref="SimConfig.Executions"/> carries it to the state machine, the server's sender and the client.
/// <para>The wire carries <see cref="WireIndexOf"/> (a row's place in <see cref="WireIds"/>), never <see cref="IndexOf"/> (its place among the kept rows): a row one install lacks leaves a hole,
/// so every other row keeps its index. A receiver resolves it with <see cref="AtWire"/>; null means this install lacks the row: no execution is drawn or predicted (the server's run still
/// plays; the server's row set is authoritative, lead 39).</para>
/// </summary>
public sealed class ExecutionRowSet
{
    /// <summary>No rows: no execution can be played (an install without the clips, or a config built without <c>GameplayTuning</c>).</summary>
    public static readonly ExecutionRowSet Empty = new([], []);

    /// <summary>The valid rows, in the order given.</summary>
    public IReadOnlyList<ExecutionRow> Rows { get; }

    /// <summary>The source list's row ids in wire order (every row given, kept or not).</summary>
    public IReadOnlyList<string> WireIds { get; }

    /// <summary>The set of <paramref name="rows"/> (as given: the caller checks them, <c>ExecutionCatalogue.Create</c>) with the wire order <paramref name="wireIds"/>.</summary>
    public ExecutionRowSet(IReadOnlyList<ExecutionRow> rows, IReadOnlyList<string> wireIds) => (Rows, WireIds) = (rows, wireIds);

    /// <summary>A set whose wire order is <see cref="ExecutionRows.All"/> (the order the install loader and the wire use), for tests and synthetic rows whose ids are in it.</summary>
    public static ExecutionRowSet OfAll(IReadOnlyList<ExecutionRow> rows) => new(rows, ExecutionRows.All.Select(r => r.Id).ToArray());

    /// <summary>The row with <paramref name="id"/> (case aside), or null.</summary>
    public ExecutionRow? Find(string id)
    {
        foreach (var r in Rows) if (string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase)) return r;
        return null;
    }

    /// <summary>The wire index of the row with <paramref name="id"/>: its place in <see cref="WireIds"/>, or -1 when this set does not have that row (left out, or not in the wire order).</summary>
    public int WireIndexOf(string id)
    {
        if (Find(id) is null) return -1;
        for (int i = 0; i < WireIds.Count; i++) if (string.Equals(WireIds[i], id, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>The row at wire index <paramref name="wireIndex"/>, or null when it is out of range or this set lacks that row (play no execution).</summary>
    public ExecutionRow? AtWire(int wireIndex) => wireIndex >= 0 && wireIndex < WireIds.Count ? Find(WireIds[wireIndex]) : null;

    /// <summary>The place of the row with <paramref name="id"/> in <see cref="Rows"/> (kept rows only), or -1. Not the wire index: use <see cref="WireIndexOf"/>.</summary>
    public int IndexOf(string id)
    {
        for (int i = 0; i < Rows.Count; i++) if (string.Equals(Rows[i].Id, id, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>
    /// The full content (the rules versions, the wire order and each kept row's wire index, identity, keys, clips, lengths, offset, yaw, band, priority, variants and events; the root
    /// paths' sample counts only). Not in the config hash (lead 39: the server's rows are authoritative and a client whose install lacks a clip still joins; <see cref="ExecutionRules.Hash"/>
    /// writes the code's versions and wire order only); <c>ExecutionCatalogue.Write</c> and the tests compare installs with it.
    /// </summary>
    public void Write(BinaryWriter w)
    {
        w.Write(ExecutionChoice.RulesVersion); w.Write(ExecutionAlign.RulesVersion); w.Write(ExecutionRows.DataVersion);
        w.Write(WireIds.Count);
        foreach (var id in WireIds) w.Write(id);
        w.Write(Rows.Count);
        foreach (var r in Rows)
        {
            w.Write(WireIndexOf(r.Id)); w.Write(r.Id); w.Write(r.AttackerClass); w.Write(r.Weapon); w.Write(r.Target);
            w.Write(r.AttackerClip); w.Write(r.VictimClip); w.Write(r.Graph); w.Write(r.Sequencer);
            w.Write(r.AttackerSeconds); w.Write(r.VictimSeconds); w.Write(r.VictimSequencer ?? "");
            for (int i = 0; i < r.AttackerVariants.Count; i++) w.Write(r.DistanceOf(i));
            w.Write(r.Offset.X); w.Write(r.Offset.Y); w.Write(r.Offset.Z); w.Write(r.RelativeYaw); w.Write(r.Priority);
            w.Write(r.AttackerVariants.Count);
            foreach (var v in r.AttackerVariants) { w.Write(v.Clip); w.Write(v.Distance); w.Write(v.Root.Count); }
            w.Write(r.Events.Count);
            foreach (var e in r.Events) { w.Write((byte)e.Kind); w.Write(e.Seconds); w.Write(e.Name ?? ""); }
            w.Write(r.AttackerRoot.Count); w.Write(r.VictimRoot.Count);
        }
    }
}
