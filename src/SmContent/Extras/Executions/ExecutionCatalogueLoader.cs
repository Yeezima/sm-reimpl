using SmFormats.Havok;
using SmSim.Extras.Executions;

namespace SmContent.Extras.Executions;

/// <summary>
/// Executions X7: the catalogue of <see cref="ExecutionRows.All"/> filled from the player's install (read-only: <see cref="RigProfile.LoadCharacter"/> opens
/// the archives for reading). Each row's attacker clip, distance variants and victim clip are read with <see cref="ExecutionClipReader"/> from the campaign
/// player rig (<see cref="ExecutionRows.AttackerRig"/>) and the victim's rig (<see cref="ExecutionRows.VictimRigs"/>); the lengths and root paths come from
/// the install, the event times from the row's own sequencer when the install names its tags (else the data's). A row whose clips are missing or cannot be
/// read is left out with a note (the coverage matrix's gap), so an install without them executes nothing rather than a wrong clip.
/// </summary>
public static class ExecutionCatalogueLoader
{
    /// <summary>The rig profiles of the attacker and every victim kind (body visual empty: only the clips are read).</summary>
    public static RigProfile AttackerProfile => new("executions_attacker", ExecutionRows.AttackerRig.Archive, ExecutionRows.AttackerRig.Entry, "");

    /// <summary>The victim rig profile of <paramref name="kind"/>, or null when no row kind uses it.</summary>
    public static RigProfile? VictimProfile(string kind) =>
        ExecutionRows.VictimRigs.TryGetValue(kind, out var rig) ? new RigProfile("executions_" + kind.ToLowerInvariant(), rig.Archive, rig.Entry, "") : null;

    /// <summary>The catalogue of <paramref name="rows"/> (default <see cref="ExecutionRows.All"/>) filled from <paramref name="gameDir"/>; notes for every row left out or changed.</summary>
    public static ExecutionCatalogue Load(string gameDir, List<string>? notes = null, IReadOnlyList<ExecutionRow>? rows = null)
    {
        rows ??= ExecutionRows.All;
        var characters = new Dictionary<string, (HavokCharacterFile Character, IReadOnlyList<BehaviorGraph> Graphs)?>(StringComparer.OrdinalIgnoreCase);
        (HavokCharacterFile, IReadOnlyList<BehaviorGraph>)? Rig(RigProfile profile)
        {
            string key = profile.Archive + ":" + profile.Entry;
            if (characters.TryGetValue(key, out var done)) return done;
            (HavokCharacterFile, IReadOnlyList<BehaviorGraph>)? loaded = null;
            try
            {
                var c = profile.LoadCharacter(gameDir);
                loaded = (c, HavokBehavior.Read(c.Pack));
            }
            catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException or KeyNotFoundException or UnauthorizedAccessException)
            {
                notes?.Add($"executions: rig {profile.Entry} in {profile.Archive} could not be read ({e.Message})");
            }
            characters[key] = loaded;
            return loaded;
        }

        var attacker = Rig(AttackerProfile);
        var filled = new List<ExecutionRow>();
        foreach (var row in rows)
        {
            if (attacker is not { } a) { notes?.Add($"executions: row '{row.Id}' left out (no attacker rig)"); continue; }
            if (VictimProfile(row.Target) is not { } vp || Rig(vp) is not { } v) { notes?.Add($"executions: row '{row.Id}' left out (no victim rig for '{row.Target}')"); continue; }
            if (Fill(row, a.Item1, a.Item2, v.Item1, v.Item2, notes) is { } r) filled.Add(r);
        }
        return ExecutionCatalogue.Create(filled, notes);
    }

    /// <summary>
    /// <paramref name="row"/> filled from the clips of <paramref name="attacker"/> and <paramref name="victim"/>: lengths, root paths, the variants' root paths
    /// (their authored gaps kept), and the event times of the row's sequencer by tag name; null (with a note) when a clip is missing or unreadable.
    /// </summary>
    public static ExecutionRow? Fill(ExecutionRow row, HavokCharacterFile attacker, IReadOnlyList<BehaviorGraph> attackerGraphs,
        HavokCharacterFile victim, IReadOnlyList<BehaviorGraph> victimGraphs, List<string>? notes = null)
    {
        try
        {
            var a = ExecutionClipReader.Read(attacker, row.AttackerClip, attackerGraphs);
            var v = ExecutionClipReader.Read(victim, row.VictimClip, victimGraphs);
            var variants = row.AttackerVariants.Select(x => (string.Equals(x.Clip, a.Clip, StringComparison.OrdinalIgnoreCase) ? a : ExecutionClipReader.Read(attacker, x.Clip, attackerGraphs), (float?)x.Distance)).ToArray();
            var filled = ExecutionClipReader.Fill(row, a, v, variants.Length > 0 ? variants : null);
            filled = filled with { Events = Retime(row, a, notes) };
            if (MathF.Abs(filled.AttackerSeconds - row.AttackerSeconds) > 0.02f || MathF.Abs(filled.VictimSeconds - row.VictimSeconds) > 0.02f)
                notes?.Add($"executions: row '{row.Id}' lengths {filled.AttackerSeconds:F3} / {filled.VictimSeconds:F3} s in the install, {row.AttackerSeconds:F3} / {row.VictimSeconds:F3} s in the data");
            if (!filled.IsValid) { notes?.Add($"executions: row '{row.Id}' is not valid once filled; left out"); return null; }
            return filled;
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidDataException or NotSupportedException)
        {
            notes?.Add($"executions: row '{row.Id}' left out ({e.Message})");
            return null;
        }
    }

    /// <summary>
    /// The row's events with their times from its own sequencer (<see cref="ExecutionRow.Sequencer"/> in <see cref="ExecutionRow.Graph"/>) where the install
    /// has a tag of the event's name (the first such tag; the alignment window by its kind), else the data's time. A change over 0.02 s is noted.
    /// </summary>
    public static IReadOnlyList<ExecutionEvent> Retime(ExecutionRow row, ExecutionClip attacker, List<string>? notes = null)
    {
        var use = attacker.Uses.FirstOrDefault(u => string.Equals(u.Sequencer, row.Sequencer, StringComparison.OrdinalIgnoreCase) && string.Equals(u.Graph, row.Graph, StringComparison.OrdinalIgnoreCase))
                  ?? attacker.Uses.FirstOrDefault(u => string.Equals(u.Sequencer, row.Sequencer, StringComparison.OrdinalIgnoreCase));
        if (use is null) return row.Events;
        var list = new List<ExecutionEvent>(row.Events.Count);
        foreach (var e in row.Events)
        {
            var tag = e.Name is null ? null : use.Events.FirstOrDefault(t => string.Equals(t.Name, e.Name, StringComparison.OrdinalIgnoreCase));
            if (tag is null) { list.Add(e); continue; }
            if (MathF.Abs(tag.Start - e.Seconds) > 0.02f) notes?.Add($"executions: row '{row.Id}' {e.Name} at {tag.Start:F3} s in the install, {e.Seconds:F3} s in the data");
            list.Add(e with { Seconds = tag.Start });
        }
        return list;
    }
}
