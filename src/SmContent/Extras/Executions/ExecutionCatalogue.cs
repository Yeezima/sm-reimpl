using SmSim.Extras.Executions;

namespace SmContent.Extras.Executions;

/// <summary>
/// Executions X2: every execution the game can play, one <see cref="ExecutionRow"/> per move (attacker class and weapon family, target kind, the attacker's
/// reference clip and distance variants, the victim clip, lengths, events, the pair's offset and yaw, the source graph and sequencer). Engine-free and
/// data-driven: the rows are filled by X7 from the coverage inventory (docs/executions-coverage.md: O1 to O9, C1 to C3 and the Chaos run's kinds) and checked
/// here; until then the catalogue is <see cref="Empty"/> and nothing can be executed. <see cref="Rows"/> keeps the order given (the fallback order among equal
/// priorities, <see cref="ExecutionChoice.Rows"/>).
/// <para>Wire identity (X7, for X1, X3 and X5): the wire carries a row's place in the source list (<see cref="WireIds"/>; <see cref="ExecutionRows.All"/> for the
/// install loader), never its place in <see cref="Rows"/>. A row left out (a clip missing from one install) leaves a hole, so every other row keeps its index;
/// the sender writes <see cref="WireIndexOf"/> and the receiver reads <see cref="AtWire"/>, which is null for a row this catalogue does not have (play no
/// execution: the normal kill stands). The hash (<see cref="Write"/>) carries the source list and each kept row's wire index.</para>
/// </summary>
public sealed class ExecutionCatalogue
{
    /// <summary>Raised when the catalogue's shape or rules change (for X1's hashed Executions block).</summary>
    public const int Version = 2;

    /// <summary>No rows: no execution can be played (the state before X7, or an install without the clips).</summary>
    public static readonly ExecutionCatalogue Empty = new([], []);

    /// <summary>The rows and their wire identity on the simulation's side (X3): what <c>GameplayTuning.ExecutionsLoad</c> puts on <see cref="SimConfig.Executions"/>.</summary>
    public ExecutionRowSet Set { get; }

    /// <summary>The valid rows, in the order given (duplicates by id and invalid rows left out with a note; <see cref="Create"/>).</summary>
    public IReadOnlyList<ExecutionRow> Rows => Set.Rows;

    /// <summary>The source list's row ids in wire order (every row given, kept or not): the index <see cref="WireIndexOf"/> gives and <see cref="AtWire"/> reads.</summary>
    public IReadOnlyList<string> WireIds => Set.WireIds;

    ExecutionCatalogue(IReadOnlyList<ExecutionRow> rows, IReadOnlyList<string> wireIds) => Set = new ExecutionRowSet(rows, wireIds);

    /// <summary>
    /// A catalogue of <paramref name="rows"/>: a row that is not valid (<see cref="ExecutionRow.IsValid"/>), repeats an earlier row's id (case aside), names a
    /// weapon that is no family (<see cref="ExecutionWeapons"/>; "*" aside) or a kind in <see cref="ExecutionTargets.Never"/> is left out with the reason in
    /// <paramref name="notes"/>. <paramref name="wireIds"/>: the source list's ids in wire order (<see cref="WireIds"/>; null: the ids of
    /// <paramref name="rows"/> as given, left-out rows included, first occurrence of an id). A kept row whose id is not in it cannot be sent (no wire index).
    /// </summary>
    public static ExecutionCatalogue Create(IEnumerable<ExecutionRow> rows, List<string>? notes = null, IReadOnlyList<string>? wireIds = null)
    {
        var given = rows as IReadOnlyList<ExecutionRow> ?? rows.ToArray();
        var wire = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in wireIds ?? given.Where(r => r is not null).Select(r => r.Id))
            if (!string.IsNullOrWhiteSpace(id) && seen.Add(id)) wire.Add(id);
        rows = given;
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
        return new ExecutionCatalogue(kept, wire);
    }

    /// <summary>The row with <paramref name="id"/> (case aside), or null.</summary>
    public ExecutionRow? Find(string id) => Set.Find(id);

    /// <summary>
    /// The wire index of the row with <paramref name="id"/>: its place in <see cref="WireIds"/> (the source list), or -1 when this catalogue does not have
    /// that row (left out, or never in the source list). What the sender writes (<see cref="ExecutionRowSet.WireIndexOf"/>).
    /// </summary>
    public int WireIndexOf(string id) => Set.WireIndexOf(id);

    /// <summary>The row at wire index <paramref name="wireIndex"/> (<see cref="WireIndexOf"/>), or null when it is out of range or this catalogue lacks that row (play no execution).</summary>
    public ExecutionRow? AtWire(int wireIndex) => Set.AtWire(wireIndex);

    /// <summary>The place of the row with <paramref name="id"/> in <see cref="Rows"/> (kept rows only), or -1. Not the wire index: use <see cref="WireIndexOf"/>.</summary>
    public int IndexOf(string id) => Set.IndexOf(id);

    /// <summary>The rows <paramref name="attackerClass"/> with <paramref name="weaponKey"/> (any spelling, <see cref="ExecutionWeapons.FamilyOf"/>) plays on a victim of <paramref name="kind"/>.</summary>
    public IReadOnlyList<ExecutionRow> For(string attackerClass, string weaponKey, string kind) =>
        ExecutionWeapons.FamilyOf(weaponKey) is { } family ? ExecutionChoice.Rows(Rows, attackerClass, family, kind) : [];

    /// <summary>The kinds the catalogue has rows for (ordinal, lower case, sorted): what the coverage matrix shows as covered.</summary>
    public IReadOnlyList<string> Kinds => Rows.Select(r => r.Target.ToLowerInvariant()).Distinct().Order(StringComparer.Ordinal).ToArray();

    /// <summary>
    /// The catalogue's content for comparing installs: <see cref="Version"/>, then <see cref="ExecutionRowSet.Write"/> (the versions, the wire order and each row's values; root sample counts only).
    /// Not in the config hash since X3 (lead 39: the server's rows are authoritative; <see cref="ExecutionRules.Hash"/> writes the code's versions and wire order only).
    /// </summary>
    public void Write(BinaryWriter w)
    {
        w.Write(Version);
        Set.Write(w);
    }
}
