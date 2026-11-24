using SmSim.Extras.Executions;

namespace SmContent.Extras.Executions;

/// <summary>
/// Executions X2: every execution the game can play, one <see cref="ExecutionRow"/> per move (attacker class and weapon family, target kind, the attacker's
/// reference clip and distance variants, the victim clip, lengths, events, the pair's offset and yaw, the source graph and sequencer). Engine-free and
/// data-driven: the rows are filled by X7 from the coverage inventory (docs/executions-coverage.md: O1 to O9, C1 to C3 and the Chaos run's kinds) and checked
/// here; until then the catalogue is <see cref="Empty"/> and nothing can be executed. <see cref="Rows"/> keeps the order given (the fallback order among equal
/// priorities, <see cref="ExecutionChoice.Rows"/>) and its index is the clip index X1's wire carries.
/// </summary>
public sealed class ExecutionCatalogue
{
    /// <summary>Raised when the catalogue's shape or rules change (for X1's hashed Executions block).</summary>
    public const int Version = 1;

    /// <summary>No rows: no execution can be played (the state before X7, or an install without the clips).</summary>
    public static readonly ExecutionCatalogue Empty = new([]);

    /// <summary>The valid rows, in the order given (duplicates by id and invalid rows left out with a note; <see cref="Create"/>).</summary>
    public IReadOnlyList<ExecutionRow> Rows { get; }

    ExecutionCatalogue(IReadOnlyList<ExecutionRow> rows) => Rows = rows;

    /// <summary>
    /// A catalogue of <paramref name="rows"/>: a row that is not valid (<see cref="ExecutionRow.IsValid"/>), repeats an earlier row's id (case aside), names a
    /// weapon that is no family (<see cref="ExecutionWeapons"/>; "*" aside) or a kind in <see cref="ExecutionTargets.Never"/> is left out with the reason in
    /// <paramref name="notes"/>.
    /// </summary>
    public static ExecutionCatalogue Create(IEnumerable<ExecutionRow> rows, List<string>? notes = null)
    {
        var kept = new List<ExecutionRow>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
        {
            if (r is null) continue;
            if (!r.IsValid) { notes?.Add($"executions: row '{r.Id}' is not valid; left out"); continue; }
            if (!ids.Add(r.Id)) { notes?.Add($"executions: row '{r.Id}' repeats an id; left out"); continue; }
            if (r.Weapon != "*" && ExecutionWeapons.FamilyOf(r.Weapon) != r.Weapon.ToLowerInvariant())
            {
                notes?.Add($"executions: row '{r.Id}' names weapon '{r.Weapon}', which is no family; left out");
                continue;
            }
            if (ExecutionTargets.Never.Contains(r.Target)) { notes?.Add($"executions: row '{r.Id}' targets '{r.Target}', which is never executed; left out"); continue; }
            kept.Add(r);
        }
        return new ExecutionCatalogue(kept);
    }

    /// <summary>The row with <paramref name="id"/> (case aside), or null.</summary>
    public ExecutionRow? Find(string id) => Rows.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The index of the row with <paramref name="id"/> (the clip index on X1's wire), or -1.</summary>
    public int IndexOf(string id)
    {
        for (int i = 0; i < Rows.Count; i++) if (string.Equals(Rows[i].Id, id, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>The rows <paramref name="attackerClass"/> with <paramref name="weaponKey"/> (any spelling, <see cref="ExecutionWeapons.FamilyOf"/>) plays on a victim of <paramref name="kind"/>.</summary>
    public IReadOnlyList<ExecutionRow> For(string attackerClass, string weaponKey, string kind) =>
        ExecutionWeapons.FamilyOf(weaponKey) is { } family ? ExecutionChoice.Rows(Rows, attackerClass, family, kind) : [];

    /// <summary>The kinds the catalogue has rows for (ordinal, lower case, sorted): what the coverage matrix shows as covered.</summary>
    public IReadOnlyList<string> Kinds => Rows.Select(r => r.Target.ToLowerInvariant()).Distinct().Order(StringComparer.Ordinal).ToArray();

    /// <summary>
    /// For a config hash: the versions and each row's identity, keys, clips, lengths, offset, yaw, band, priority, variants and events (the values the server
    /// plays with); never the root paths' samples (decoded with platform maths), only their counts.
    /// </summary>
    public void Write(BinaryWriter w)
    {
        w.Write(Version); w.Write(ExecutionChoice.RulesVersion); w.Write(ExecutionAlign.RulesVersion);
        w.Write(Rows.Count);
        foreach (var r in Rows)
        {
            w.Write(r.Id); w.Write(r.AttackerClass); w.Write(r.Weapon); w.Write(r.Target);
            w.Write(r.AttackerClip); w.Write(r.VictimClip); w.Write(r.Graph); w.Write(r.Sequencer);
            w.Write(r.AttackerSeconds); w.Write(r.VictimSeconds);
            w.Write(r.Offset.X); w.Write(r.Offset.Y); w.Write(r.Offset.Z); w.Write(r.RelativeYaw); w.Write(r.Priority);
            w.Write(r.AttackerVariants.Count);
            foreach (var v in r.AttackerVariants) { w.Write(v.Clip); w.Write(v.Distance); w.Write(v.Root.Count); }
            w.Write(r.Events.Count);
            foreach (var e in r.Events) { w.Write((byte)e.Kind); w.Write(e.Seconds); w.Write(e.Name ?? ""); }
            w.Write(r.AttackerRoot.Count); w.Write(r.VictimRoot.Count);
        }
    }
}
