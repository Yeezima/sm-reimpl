using SmFormats.Havok;
using SmSim.Extras.Executions;

namespace SmContent.Extras.Executions;

/// <summary>
/// Executions X7: the catalogue of <see cref="ExecutionRows.All"/> filled from the player's install (read-only: <see cref="RigProfile.LoadCharacter"/> opens
/// the archives for reading). Each row's attacker clip, distance variants and victim clip are read with <see cref="ExecutionClipReader"/> from the campaign
/// player rig (<see cref="ExecutionRows.AttackerRig"/>) and the victim's rig (<see cref="ExecutionRows.VictimRigs"/>); the lengths and root paths come from
/// the install, the event times from the row's own sequencer when the install names its tags (else the data's). A row whose clips are missing or cannot be
/// read is left out with a note (the coverage matrix's gap), so an install without them executes nothing rather than a wrong clip.
/// <para>For X3 (the server) and X5 (the client): play only rows of this loader's catalogue, never <see cref="ExecutionRows.All"/> as it is. The raw rows have
/// empty root paths, so <see cref="ExecutionAlign"/> would probe only the pair's feet and the slide, not the paths the clips travel. The wire carries
/// <see cref="ExecutionCatalogue.WireIndexOf"/> (the row's place in <see cref="ExecutionRows.All"/>), and the receiver resolves it with
/// <see cref="ExecutionCatalogue.AtWire"/>. When the receiver's install lacks that row, the result is null, and it plays no execution (the normal kill).</para>
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
        // The wire order is the source list's (ExecutionRows.All), not the kept rows': a row left out here leaves a hole, so every other row keeps its index.
        return ExecutionCatalogue.Create(filled, notes, rows.Select(r => r.Id).ToArray());
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
            if (!Plays(a, row.Sequencer))
                notes?.Add($"executions: row '{row.Id}' attacker sequencer '{row.Sequencer}' does not play {row.AttackerClip} in the install; speed 1 and the data's event times");
            if (row.VictimSequencer is { } vs && !Plays(v, vs))
                notes?.Add($"executions: row '{row.Id}' victim sequencer '{vs}' does not play {row.VictimClip} in the install; speed 1");
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

    static bool Plays(ExecutionClip clip, string sequencer) => clip.Uses.Any(u => string.Equals(u.Sequencer, sequencer, StringComparison.OrdinalIgnoreCase));

    /// <summary>How far a variant's authored gap may lie from the gap its root travel gives (metres; <see cref="VariantGaps"/>).</summary>
    public const float GapTolerance = 0.1f;

    /// <summary>
    /// Each distance variant of <paramref name="row"/>: its authored gap (<see cref="ExecutionVariant.Distance"/>, measured where the interaction bones meet)
    /// and the gap X2's derivation gives (<see cref="ExecutionClipReader.Fill"/>: the row's offset plus the difference of the variant's forward root travel
    /// from the reference clip's), null when either path is not extracted motion. The check that the two readings agree (the install test); where a
    /// variant's end pose differs from the reference clip's the travel reading is off, and the authored gap (the interaction bones) is the one the rows keep.
    /// </summary>
    public static IReadOnlyList<(string Clip, float Authored, float? Derived)> VariantGaps(ExecutionRow row, ExecutionClip reference, IReadOnlyList<ExecutionClip> variants)
    {
        static float? Travel(ExecutionClip c) => c.Root.Count > 1 && c.RootSource == ExecutionClipReader.ExtractedMotion ? c.Root[^1].Y - c.Root[0].Y : null;
        var list = new List<(string, float, float?)>();
        float? r = Travel(reference);
        for (int i = 0; i < row.AttackerVariants.Count && i < variants.Count; i++)
        {
            var v = row.AttackerVariants[i];
            float? derived = string.Equals(v.Clip, row.AttackerClip, StringComparison.OrdinalIgnoreCase) ? row.Offset.Y
                : r is { } rr && Travel(variants[i]) is { } t ? row.Offset.Y + (t - rr) : null;
            list.Add((v.Clip, v.Distance, derived));
        }
        return list;
    }

    /// <summary>
    /// The row's events with their times from its own sequencer (<see cref="ExecutionRow.Sequencer"/> in <see cref="ExecutionRow.Graph"/>) where the install
    /// has a tag of the event's name (the first such tag; the sync by the alignment window's kind, <see cref="AlignmentTag"/>), else the data's time, in time
    /// order. A change over 0.02 s is noted, and so is an event with no tag (the victim's dead branch, which the victim graph carries, aside).
    /// </summary>
    public static IReadOnlyList<ExecutionEvent> Retime(ExecutionRow row, ExecutionClip attacker, List<string>? notes = null)
    {
        var use = attacker.Uses.FirstOrDefault(u => string.Equals(u.Sequencer, row.Sequencer, StringComparison.OrdinalIgnoreCase) && string.Equals(u.Graph, row.Graph, StringComparison.OrdinalIgnoreCase))
                  ?? attacker.Uses.FirstOrDefault(u => string.Equals(u.Sequencer, row.Sequencer, StringComparison.OrdinalIgnoreCase));
        if (use is null) return row.Events;   // noted by Fill (the sequencer does not play the clip)
        var list = new List<ExecutionEvent>(row.Events.Count);
        foreach (var e in row.Events)
        {
            // The sync is the alignment window's start: an unnamed tag, found by its kind (ExecutionClipReader names an unnamed tag by its kind).
            var tag = e.Kind == ExecutionEventKind.Sync
                ? use.Events.FirstOrDefault(t => string.Equals(t.Tag, AlignmentTag, StringComparison.OrdinalIgnoreCase))
                : e.Name is null ? null : use.Events.FirstOrDefault(t => string.Equals(t.Name, e.Name, StringComparison.OrdinalIgnoreCase));
            if (tag is null)
            {
                // Unnamed events (the knife stab's end at the clip end) have no tag to find; the victim's dead branch is the victim graph's, not the
                // attacker sequencer's. Every other event without its tag is noted.
                if (e.Name is not null && (e.Kind != ExecutionEventKind.Other || e.Name.StartsWith("ACTN_", StringComparison.Ordinal)))
                    notes?.Add($"executions: row '{row.Id}' {e.Name ?? e.Kind.ToString()} has no tag in '{row.Sequencer}' in the install; the data's {e.Seconds:F3} s kept");
                list.Add(e);
                continue;
            }
            if (MathF.Abs(tag.Start - e.Seconds) > 0.02f) notes?.Add($"executions: row '{row.Id}' {e.Name} at {tag.Start:F3} s in the install, {e.Seconds:F3} s in the data");
            list.Add(e with { Seconds = tag.Start });
        }
        return list.OrderBy(x => x.Seconds).ThenBy(x => (byte)x.Kind).ToArray();
    }

    /// <summary>The tag kind of the alignment window (<c>hkbAttackAlignmentSequenceTag</c>), whose start is the row's sync event.</summary>
    public const string AlignmentTag = "AttackAlignment";
}
