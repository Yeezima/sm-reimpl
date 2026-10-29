namespace SmFormats.Havok;

/// <summary>A timed tag on a sequencer: its kind, its window as a fraction of the clip, and what it carries.</summary>
/// <param name="Kind">Class name without "hkb" and "SequenceTag", e.g. "Event", "Branch", "Action", "AttackDistance".</param>
/// <param name="Start">Start of the window, as a fraction of the clip's duration (0–1).</param>
/// <param name="End">End of the window, same unit.</param>
/// <param name="Name">Event name (Event and Branch tags, resolved through the graph's event names) or action name (Action tags).</param>
/// <param name="Distance">AttackDistance tags: the distance, metres (assumed).</param>
/// <param name="Mode">EntityProperty tags: the property data's mode value (@28 of the data object; meaning not established).</param>
/// <remarks>
/// Modifier tags carry the modifier's name (e.g. "Align To Nav Direction01"); EntityProperty tags the property data's class
/// without "hkbEntityProperty" and "Data" (e.g. "TargetableMode", "CollisionMode").
/// </remarks>
public sealed record BehaviorTag(string Kind, float Start, float End, string? Name, float? Distance, int? Mode = null);

/// <summary>A sequencer: a named node playing one clip, or a blend of clips, with its tags.</summary>
/// <param name="Animations">Animation names (without ".hkxa"): the clip's, or each blended child clip's.</param>
/// <param name="PlaybackSpeeds">Each clip generator's playback speed (@60; 1 for 950 of the marine's 985 clips).</param>
public sealed record BehaviorSequencer(string Name, IReadOnlyList<string> Animations, IReadOnlyList<BehaviorTag> Tags, IReadOnlyList<float> PlaybackSpeeds)
{
    /// <summary>
    /// feel/jump-pack-2: each clip generator's crop (<c>cropStartAmountLocalTime</c> @48, <c>cropEndAmountLocalTime</c> @52: seconds cut from the clip's start and
    /// end; the layout's reading is an interpretation of hkbClipGenerator's declared order), in <see cref="Animations"/>' order; empty when not read.
    /// </summary>
    public IReadOnlyList<(float Start, float End)> Crops { get; init; } = [];
}

public sealed record BehaviorGraph(string Name, IReadOnlyList<string> Events, IReadOnlyList<BehaviorSequencer> Sequencers);

/// <summary>
/// Behavior graphs in a character packfile (hk_2010, 32-bit, with Relic's sequencer and tag classes). Layout inferred from
/// the data (docs/discovery.md): a graph's objects are contiguous, from its <c>hkbBehaviorGraph</c> (name @32) through
/// its <c>hkbBehaviorGraphStringData</c> (event names @8). <c>hkbSequencerGenerator</c>: name @32, tags (pointer
/// array) @40, generator @52, either an <c>hkbClipGenerator</c> (animation file @40) or an <c>hkbBlenderGenerator</c>
/// (children @64, each <c>hkbBlenderGeneratorChild</c> with its generator @32). Tags: window start @32 and end @36 as
/// fractions of the clip; Event and Branch tags hold an event index @52, Action tags a name @52, AttackDistance tags a
/// distance @56; Modifier and EntityProperty tags point @52 at the modifier (name @32) or the property data (mode @28; M21).
/// Clip generators: playback speed @60. Only what the melee, jump and evade timing need is read.
/// </summary>
public static class HavokBehavior
{
    public static IReadOnlyList<BehaviorGraph> Read(HavokPackfile pack)
    {
        var objects = pack.Objects.OrderBy(o => o.Offset).ToList();
        var classes = new Dictionary<int, string>();
        foreach (var o in objects) classes.TryAdd(o.Offset, o.ClassName);
        var graphStarts = objects.Where(o => o.ClassName == "hkbBehaviorGraph").Select(o => o.Offset).ToList();
        var graphs = new List<BehaviorGraph>();
        for (int g = 0; g < graphStarts.Count; g++)
        {
            int start = graphStarts[g], end = g + 1 < graphStarts.Count ? graphStarts[g + 1] : int.MaxValue;
            var members = objects.Where(o => o.Offset >= start && o.Offset < end).ToList();
            string name = pack.String(start + 32) ?? $"graph{g}";
            var events = new List<string>();
            if (members.FirstOrDefault(o => o.ClassName == "hkbBehaviorGraphStringData") is { } strings)
            {
                var (at, count) = pack.Array(strings.Offset + 8, 4);
                for (int i = 0; i < count; i++) events.Add(pack.String(at!.Value + 4 * i) ?? "");
            }
            var sequencers = members.Where(o => o.ClassName == "hkbSequencerGenerator").Select(s => Sequencer(pack, classes, s.Offset, events)).ToList();
            graphs.Add(new BehaviorGraph(name.EndsWith(".hkb", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name, events, sequencers));
        }
        return graphs;
    }

    /// <summary>Generators visited per sequencer; far above any real blend (3 children), to bound corrupt files.</summary>
    const int MaxGenerators = 256;

    internal static BehaviorSequencer Sequencer(HavokPackfile pack, Dictionary<int, string> classes, int at, List<string> events)
    {
        var animations = new List<string>();
        var speeds = new List<float>();
        var crops = new List<(float, float)>();
        if (pack.Pointer(at + 52) is int generator) CollectAnimations(pack, classes, generator, animations, speeds, crops, new HashSet<int>(), 0);
        var tags = new List<BehaviorTag>();
        var (tagsAt, count) = pack.Array(at + 40, 4);
        for (int i = 0; i < count; i++)
        {
            if (pack.Pointer(tagsAt!.Value + 4 * i) is not int tag || !classes.TryGetValue(tag, out var cls)) continue;
            string kind = cls.StartsWith("hkb") ? cls[3..] : cls;
            if (kind.EndsWith("SequenceTag")) kind = kind[..^"SequenceTag".Length];
            // Modifier and EntityProperty tags point (@52) at an object: the modifier (name @32) or the property data (mode @28).
            int? target = kind is "Modifier" or "EntityProperty" && pack.Pointer(tag + 52) is int p && classes.ContainsKey(p) ? p : null;
            string? name = kind switch
            {
                "Event" or "Branch" => pack.ReadInt32(tag + 52) is var e && e >= 0 && e < events.Count ? events[e] : null,
                "Action" => pack.String(tag + 52),
                "Modifier" when target is int m => pack.String(m + 32),
                "EntityProperty" when target is int d => PropertyName(classes[d]),
                _ => null,
            };
            float? distance = kind == "AttackDistance" ? pack.ReadSingle(tag + 56) : null;
            int? mode = kind == "EntityProperty" && target is int data ? pack.ReadInt32(data + 28) : null;
            tags.Add(new BehaviorTag(kind, pack.ReadSingle(tag + 32), pack.ReadSingle(tag + 36), name, distance, mode));
        }
        return new BehaviorSequencer(pack.String(at + 32) ?? "", animations, tags, speeds) { Crops = crops };
    }

    /// <summary>"hkbEntityPropertyTargetableModeData" → "TargetableMode".</summary>
    static string PropertyName(string cls)
    {
        if (cls.StartsWith("hkbEntityProperty")) cls = cls["hkbEntityProperty".Length..];
        return cls.EndsWith("Data") && cls.Length > 4 ? cls[..^4] : cls;
    }

    /// <summary>Each generator once per sequencer: a blend that refers back to itself or repeats a child is not followed again.</summary>
    static void CollectAnimations(HavokPackfile pack, Dictionary<int, string> classes, int generator, List<string> animations, List<float> speeds, List<(float, float)> crops, HashSet<int> visited, int depth)
    {
        if (depth > 8 || !visited.Add(generator)) return;
        if (visited.Count > MaxGenerators) throw new InvalidDataException($"Sequencer blends more than {MaxGenerators} generators.");
        switch (classes.GetValueOrDefault(generator))
        {
            case "hkbClipGenerator":
                if (pack.String(generator + 40) is { Length: > 0 } file)
                {
                    animations.Add(file.EndsWith(".hkxa", StringComparison.OrdinalIgnoreCase) ? file[..^5] : file);
                    speeds.Add(pack.ReadSingle(generator + 60));
                    crops.Add((pack.ReadSingle(generator + 48), pack.ReadSingle(generator + 52)));
                }
                break;
            case "hkbBlenderGenerator":
            {
                var (childrenAt, count) = pack.Array(generator + 64, 4);
                for (int i = 0; i < count; i++)
                    if (pack.Pointer(childrenAt!.Value + 4 * i) is int child && pack.Pointer(child + 32) is int inner)
                        CollectAnimations(pack, classes, inner, animations, speeds, crops, visited, depth + 1);
                break;
            }
        }
    }
}
