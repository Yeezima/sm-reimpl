using SmFormats.Baf;

namespace SmContent;

/// <summary>
/// The upgrade strings the tuning readers treat as granted (M43, perks). Every "default entry" choice of the readers
/// (<see cref="WeaponTuning.DefaultEntry"/>, <see cref="Pick"/>, <see cref="Command"/>) takes, among the entries named alike, the
/// one whose <c>required_upgrade_name/required_list</c> is all granted and needs the most (the file's default entry needs none).
/// Outside <see cref="Grant"/> nothing is granted: the readers read the base values as before. <see cref="Grant"/> scopes a set to
/// the current thread for one read (the readers are synchronous), so a perk variant is the same file read with its string granted.
/// </summary>
public static class UpgradeScope
{
    [ThreadStatic] static IReadOnlyCollection<string>? _granted;

    /// <summary>The upgrade strings granted on this thread (empty outside <see cref="Grant"/>).</summary>
    public static IReadOnlyCollection<string> Current => _granted ?? [];

    /// <summary>Grants <paramref name="upgrades"/> until the result is disposed (the previous set is restored).</summary>
    public static IDisposable Grant(IReadOnlyCollection<string> upgrades)
    {
        var previous = _granted;
        _granted = upgrades.Count == 0 ? null : new HashSet<string>(upgrades, StringComparer.OrdinalIgnoreCase);
        return new Restore(previous);
    }

    sealed class Restore(IReadOnlyCollection<string>? previous) : IDisposable
    {
        bool _done;
        public void Dispose() { if (!_done) { _granted = previous; _done = true; } }
    }

    /// <summary>The child table named <paramref name="name"/> (any name when null) chosen for the granted upgrades (<see cref="WeaponTuning.Entry"/>).</summary>
    public static BafTable? Pick(BafTable? parent, string? name) => WeaponTuning.Entry(parent, name, Current);

    /// <summary>
    /// A command of an action list (<c>…/actions</c> or <c>…/commands</c>): the one directly in it, else the one in its first
    /// <c>actions_by_upgrade</c> block's entry chosen for the granted upgrades (<see cref="Pick"/>; the readers' rule before M43:
    /// the first block); a chosen perk entry without the command falls back to the block's default entry's; with <paramref name="allBlocks"/>, the first found in any block (a block may have no default entry, so
    /// without its upgrade it adds nothing: the heal-on-hit blocks). Null without one.
    /// </summary>
    public static BafTable? Command(BafTable? actions, string command, bool allBlocks = false)
    {
        if (actions is not { } a) return null;
        if (a.Table(command) is { } direct) return direct;
        foreach (var block in a.Entries.Where(e => e.Type == BafType.Table && e.Name == "actions_by_upgrade").Select(e => e.AsTable()))
        {
            if (Pick(block.Table("actions"), "entry")?.Table("action/commands/" + command) is { } found) return found;
            // The perk's entry lacks the command (it changes something else): the default entry's, never nothing (review, M43).
            if (WeaponTuning.Entry(block.Table("actions"), "entry", [])?.Table("action/commands/" + command) is { } fallback) return fallback;
            if (!allBlocks) break;
        }
        return null;
    }

    /// <summary>Every upgrade string a table tree requires anywhere (<c>required_upgrade_name/required_list</c> entries), in first-seen order.</summary>
    public static List<string> Required(BafTable root)
    {
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Walk(BafTable t, int depth)
        {
            if (depth > 64) return;
            foreach (var e in t.Entries)
            {
                if (e.Type != BafType.Table) continue;
                var child = e.AsTable();
                if (e.Name == "required_list")
                {
                    foreach (var u in child.Entries.Where(x => x.Type == BafType.String)) if (seen.Add(u.AsString())) found.Add(u.AsString());
                    continue;
                }
                Walk(child, depth + 1);
            }
        }
        Walk(root, 0);
        return found;
    }

    /// <summary>
    /// The perk variants of one spec (<see cref="SmSim.UpgradeVariant{T}"/>): for every set of one or two of <paramref name="upgrades"/>
    /// (those the file requires that are perks' strings), <paramref name="read"/> under <see cref="Grant"/>; a single-string variant is
    /// kept when its <paramref name="bytes"/> differ from the base's, a pair's when they differ from what the singles already give
    /// that pair (<see cref="SmSim.Variants.Pick"/>). A read that fails (null) is skipped.
    /// </summary>
    public static List<SmSim.UpgradeVariant<T>> VariantsOf<T>(T baseSpec, IReadOnlyList<string> upgrades, Func<T?> read, Func<T, byte[]> bytes) where T : class
    {
        var list = new List<SmSim.UpgradeVariant<T>>();
        if (upgrades.Count == 0) return list;
        var baseBytes = bytes(baseSpec);
        T? Read(IReadOnlyCollection<string> set) { using (Grant(set)) return read(); }
        foreach (var u in upgrades)
            if (Read([u]) is { } single && !bytes(single).AsSpan().SequenceEqual(baseBytes)) list.Add(new([u], single));
        for (int i = 0; i < upgrades.Count; i++)
            for (int j = i + 1; j < upgrades.Count; j++)
            {
                string[] pair = [upgrades[i], upgrades[j]];
                if (Read(pair) is not { } both) continue;
                var without = SmSim.Variants.Pick(baseSpec, list, new SmSim.PerkUpgrades(pair[0], pair[1]));
                if (!bytes(both).AsSpan().SequenceEqual(bytes(without))) list.Add(new(pair, both));
            }
        return list;
    }
}
