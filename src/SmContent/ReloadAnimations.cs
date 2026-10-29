using SmFormats.Havok;

namespace SmContent;

/// <summary>The weapon families whose reload the marine rig's behaviour graphs animate (M35).</summary>
public enum ReloadFamily { Bolter, BoltPistol, StormBolter, StalkerPattern, GrenadeLauncher, Lascannon, PlasmaPistol }

/// <summary>One tag of a reload sequencer.</summary>
/// <param name="Kind">"Action" (a sound, named by the wargear's <c>triggered_actions</c>), "Event" (e.g. <c>StartAction</c>,
/// <c>Reload_Wargear_Ammunition</c>) or "Branch" (e.g. <c>BRANCH_Reload_Finished</c>).</param>
/// <param name="Start">Start as a fraction of the clip (0..1).</param>
public sealed record ReloadTag(string Kind, string Name, float Start);

/// <summary>
/// One weapon family's reload as the behaviour graphs hold it: the clip, how its length relates to the weapon's reload time, its
/// tags (when the magazine leaves and returns, the sounds), and how the clip is laid over the movement (the bone mask and blend times).
/// </summary>
/// <param name="Graph">The reload behaviour graph (e.g. <c>SM_Bolter_Reload</c>); its one sequencer plays <paramref name="Clip"/>.</param>
/// <param name="Clip">The reload animation (e.g. <c>SM_RNG_Bolter_Reload_UB_S_01</c>).</param>
/// <param name="ClipLength">Seconds at playback speed 1.</param>
/// <param name="EnforcedDuration">The graph variable the clip generator's <c>enforcedDuration</c> is bound to
/// (<c>FLT_WeaponReloadTime</c>): the clip is played over exactly the weapon's reload time, so its tags fall at the same
/// fractions of the reload; null when the clip plays at its own length.</param>
/// <param name="MagazineOut">When the old magazine leaves the weapon, as a fraction of the clip: the first <c>Action</c> tag whose name
/// has "remove" (<c>SFX_Bolter_Remove_Clip</c>, <c>ACTN_SND_SP_Clip_Remove</c>); null when the clip has none (grenade launcher).
/// **Interpretation**: the tag is a sound's moment; the model's magazine leaves at that time.</param>
/// <param name="MagazineIn">When the new magazine is loaded: the first <c>Action</c> tag whose name has "load" and "clip" (<c>SFX_Bolter_Load_Clip</c>).</param>
/// <param name="AmmunitionAt">The <c>Reload_Wargear_Ammunition</c> event: where the original moves the rounds (the simulation moves them at the end of the reload).</param>
/// <param name="BoneMask">The reload layer's bone weights over the movement, every skeleton bone: bone name → weight (the reload child of the
/// base graph's reload blend, bones past the mask's array 1: <see cref="BehaviorBoneWeights"/>); null when the base graph holds no such blend.</param>
/// <param name="BlendIn">Seconds to blend from the navigation state into the reload state (the transition's blending effect; null when not found).</param>
/// <param name="BlendOut">Seconds to blend back to navigation when the reload finishes (<c>BRANCH_Reload_Finished</c>); null when not found.</param>
public sealed record ReloadClip(ReloadFamily Family, string Graph, string Clip, float ClipLength, string? EnforcedDuration,
    IReadOnlyList<ReloadTag> Tags, float? MagazineOut, float? MagazineIn, float? AmmunitionAt,
    IReadOnlyDictionary<string, float>? BoneMask, float? BlendIn, float? BlendOut, IReadOnlyList<string> Notes)
{
    /// <summary>The clip's playback speed for a reload of <paramref name="reloadTime"/> seconds: it is stretched to the reload time when its duration is enforced.</summary>
    public float PlaybackRate(float reloadTime) => EnforcedDuration is not null && reloadTime > 1e-3f && ClipLength > 1e-3f ? ClipLength / reloadTime : 1f;
}

/// <summary>
/// The reload animations of the multiplayer weapons (M35), from the marine rig's behaviour graphs (read-only;
/// <see cref="HavokBehaviorDetailReader"/>). In the original a reload press enters the weapon's <c>... Reload</c> state: a two-way blend
/// of the weapon's navigation graph (legs, pelvis; the aim spine) and the reload graph (the rest), each child with its bone mask, and the
/// reload graph plays one clip stretched to <c>FLT_WeaponReloadTime</c>; <c>BRANCH_Reload_Finished</c> at the clip's end returns to
/// navigation. Heavy bolter, plasma gun, plasma cannon and melta have no reload clip: their reload states are a brace switch and
/// heat vents or a pump (<c>Heavy Bolter Switch Mode</c>, <c>Plasma Vent</c>, <c>Melta Pump</c>), not reloads.
/// </summary>
public sealed record ReloadAnimations(IReadOnlyList<ReloadClip> Clips)
{
    public const string BaseGraph = "SM_Base_MP";

    /// <summary>Where a family's reload sits in the graphs (names as in the install).</summary>
    /// <param name="ReloadGraph">The graph that plays the clip.</param>
    /// <param name="OwnerGraph">The graph holding the family's reload state: the base graph, or the weapon's own graph (lascannon).</param>
    public sealed record Map(ReloadFamily Family, string ReloadGraph, string OwnerGraph, string ReloadState, string NavigationState)
    {
        /// <summary>The branch that ends the reload and returns to navigation (the Ork's graphs name it <c>BRANCH_Reload_End</c>).</summary>
        public string FinishedBranch { get; init; } = "BRANCH_Reload_Finished";
        /// <summary>
        /// The bone mask sits in the reload graph's own movement + reload blend (the Ork's <c>Moving Reload</c>: the reload child is the
        /// sequencer), not under the owner's reload state. False (the marine): the owner's blend child named like a reload.
        /// </summary>
        public bool MaskInReloadGraph { get; init; }
        /// <summary>
        /// Words of the first <c>Action</c> tag that marks the old magazine leaving (any of them; lower case). The Ork's clip has an unload
        /// sound (<c>ACTN_SND_Wep_Unload</c>), no remove; taking "unload" as the magazine leaving is **ours** (interpretation, as the marine's
        /// "remove": a sound's moment stands for the magazine's).
        /// </summary>
        public IReadOnlyList<string> MagazineOutWords { get; init; } = ["remove"];
    }

    public static readonly Map[] DefaultMaps =
    [
        new(ReloadFamily.Bolter, "SM_Bolter_Reload", BaseGraph, "Bolter Reload", "Bolter Navigation"),
        new(ReloadFamily.BoltPistol, "SM_BoltPistol_Reload", BaseGraph, "BoltPistol Reload", "BoltPistol Navigation"),
        new(ReloadFamily.StormBolter, "SM_Storm_Bolter_Reload", BaseGraph, "Storm Bolter Reload", "Storm Bolter Navigation"),
        new(ReloadFamily.StalkerPattern, "SM_Stalker_Pattern_Reload", BaseGraph, "Stalker Pattern Reload", "Stalker Pattern Navigation"),
        new(ReloadFamily.GrenadeLauncher, "SM_Grenade_Launcher_Reload", BaseGraph, "Grenade Launcher Reload", "Grenade Launcher Navigation"),
        new(ReloadFamily.Lascannon, "MP_SM_Lascannon_Reload", "MP_SM_LasCannon", "LasCannon Reload", "LasCannon Navigation"),
        new(ReloadFamily.PlasmaPistol, "SM_PlasmaPistol_Reload", BaseGraph, "PlasmaPistol Reload", "PlasmaPistol Navigation"),
    ];

    public ReloadClip? For(ReloadFamily family) => Clips.FirstOrDefault(c => c.Family == family);

    /// <summary>
    /// The family whose reload clip a weapon plays, by its wargear name (both sides share the graphs): the bolt pistol, plasma pistol, storm
    /// bolter, stalker bolter (<c>sniper_rifle</c>), grenade launcher and lascannon by name; every other bolter (also the bolters in the pistol
    /// slot, <c>*_pistol_bolter</c>: as <see cref="HipFireAnimations.FamilyOf"/> does, **interpretation**) the bolter's. Null for weapons with no
    /// reload clip (heavy bolter, plasma gun, plasma cannon, melta).
    /// </summary>
    public static ReloadFamily? FamilyOf(string wargear)
    {
        bool Has(string text) => wargear.Contains(text, StringComparison.OrdinalIgnoreCase);
        if (Has("heavy_bolter")) return null;   // heat, no reserve: no reload (its name contains "bolter")
        if (Has("bolt_pistol")) return ReloadFamily.BoltPistol;
        if (Has("plasma_pistol")) return ReloadFamily.PlasmaPistol;
        if (Has("storm_bolter")) return ReloadFamily.StormBolter;
        if (Has("sniper_rifle") || Has("stalker")) return ReloadFamily.StalkerPattern;
        if (Has("grenade_launcher")) return ReloadFamily.GrenadeLauncher;
        if (Has("las_cannon") || Has("lascannon")) return ReloadFamily.Lascannon;
        if (Has("bolter")) return ReloadFamily.Bolter;
        return null;
    }

    public static ReloadAnimations Load(string gameDir) => Load(gameDir, RigProfiles.Marine);

    /// <summary>A profile's reload clips (Ork O2).</summary>
    public static ReloadAnimations Load(string gameDir, RigProfile rig) => From(rig.LoadCharacter(gameDir), rig);

    /// <summary>The data from the marine rig's file (already read).</summary>
    public static ReloadAnimations From(HavokCharacterFile character) => From(character, RigProfiles.Marine);

    /// <summary>The data from a profile's rig file (already read).</summary>
    public static ReloadAnimations From(HavokCharacterFile character, RigProfile rig) =>
        From(HavokBehaviorDetailReader.Read(character.Pack), character.Skeleton.BoneNames, clip => character.LoadAnimation(clip).Animation.Duration, rig.Reloads);

    /// <summary>The data from already-read graphs (testable without an install).</summary>
    /// <param name="animationLength">Seconds of an animation by name.</param>
    public static ReloadAnimations From(IReadOnlyList<BehaviorGraphDetail> graphs, IReadOnlyList<string> boneNames, Func<string, float> animationLength,
        IEnumerable<Map>? maps = null)
    {
        BehaviorGraphDetail? Graph(string name) => graphs.FirstOrDefault(g => g.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var clips = new List<ReloadClip>();
        foreach (var map in maps ?? DefaultMaps)
        {
            var notes = new List<string>();
            if (Graph(map.ReloadGraph) is not { } graph) { continue; }
            var sequencer = graph.Sequencers.OrderBy(kv => kv.Key).Select(kv => kv.Value).FirstOrDefault(s => s.Animations.Count > 0);
            if (sequencer is null) { notes.Add($"{map.ReloadGraph}: no sequencer with a clip"); continue; }
            string clip = sequencer.Animations[0];
            float length = 0f;
            try { length = animationLength(clip); }
            catch (Exception ex) when (ex is KeyNotFoundException or NotSupportedException or InvalidDataException) { notes.Add($"{map.ReloadGraph}: clip {clip} unreadable ({ex.Message})"); }
            string? enforced = null;
            if (graph.Clips.FirstOrDefault(c => c.Animation.Equals(clip, StringComparison.OrdinalIgnoreCase)) is { } generator
                && graph.Nodes.TryGetValue(generator.Offset, out var node) && node.Bindings.TryGetValue("enforcedDuration", out var variable))
                enforced = variable;
            var tags = sequencer.Tags.Where(t => t.Name is { Length: > 0 } && t.Kind is "Action" or "Event" or "Branch")
                .Select(t => new ReloadTag(t.Kind, t.Name!, t.Start)).OrderBy(t => t.Start).ToList();
            var actions = tags.Where(t => t.Kind == "Action").ToList();
            float? Earliest(Func<string, bool> match) => actions.Where(t => match(t.Name.ToLowerInvariant())).Select(t => (float?)t.Start).FirstOrDefault();
            float? magOut = Earliest(n => map.MagazineOutWords.Any(n.Contains));
            // "reload" and "unload" contain "load" but are not the new magazine going in (e.g. a "..._Clip_Unload" action).
            float? magIn = Earliest(n => n.Contains("load") && n.Contains("clip") && !n.Contains("reload") && !n.Contains("unload"));
            float? ammunition = tags.Where(t => t.Kind == "Event" && t.Name == "Reload_Wargear_Ammunition").Select(t => (float?)t.Start).FirstOrDefault();

            // The base (or the weapon's own) graph: the reload state's two-way blend, its masks, and the blends in and out of the state.
            IReadOnlyDictionary<string, float>? mask = null;
            float? blendIn = null, blendOut = null;
            if (Graph(map.OwnerGraph) is not { } owner) notes.Add($"graph {map.OwnerGraph} not found");
            else
            {
                var found = owner.AllStates.FirstOrDefault(x => x.State.Name == map.ReloadState);
                if (found.State is null) notes.Add($"{map.OwnerGraph}: no state '{map.ReloadState}'");
                else
                {
                    var (machine, reload) = (found.Machine, found.State);
                    var nav = machine.States.FirstOrDefault(s => s.Name == map.NavigationState);
                    if (nav?.Transitions.FirstOrDefault(t => t.ToStateId == reload.Id) is { } into) blendIn = owner.EffectAt(into.Transition)?.Duration ?? 0f;
                    else notes.Add($"{map.OwnerGraph}: no transition from '{map.NavigationState}' to '{map.ReloadState}'");
                    if (nav is not null && reload.Transitions.FirstOrDefault(t => t.ToStateId == nav.Id && t.BranchEvent == map.FinishedBranch) is { } outOf)
                        blendOut = owner.EffectAt(outOf.Transition)?.Duration ?? 0f;
                    else notes.Add($"{map.OwnerGraph}: no {map.FinishedBranch} transition from '{map.ReloadState}' back to '{map.NavigationState}'");
                    if (!map.MaskInReloadGraph) mask = ReloadMask(owner, reload.Generator, boneNames, notes);
                }
            }
            if (map.MaskInReloadGraph) mask = ReloadMask(graph, graph.Root, boneNames, notes, sequencerChild: true);
            clips.Add(new ReloadClip(map.Family, map.ReloadGraph, clip, length, enforced, tags, magOut, magIn, ammunition, mask, blendIn, blendOut, notes));
        }
        return new ReloadAnimations(clips);
    }

    /// <summary>
    /// The reload child of the two-way blend under a reload state: the child whose generator is named like a reload
    /// (<c>REF_BolterReload</c>, <c>BoltPistol Reload Ref</c>), its bone weights for every bone. With <paramref name="sequencerChild"/>
    /// (a reload graph's own blend, the Ork's <c>Moving Reload</c>): the child whose generator is the sequencer playing the clip.
    /// </summary>
    static IReadOnlyDictionary<string, float>? ReloadMask(BehaviorGraphDetail g, int? state, IReadOnlyList<string> boneNames, List<string> notes, bool sequencerChild = false)
    {
        foreach (var at in Walk(g, state))
        {
            if (g.BlenderAt(at) is not { } blend) continue;
            foreach (var child in blend.Children)
            {
                if (child.Generator is not int generator || !g.Nodes.TryGetValue(generator, out var node)) continue;
                if (sequencerChild ? !g.Sequencers.ContainsKey(generator) : !node.Name.Contains("reload", StringComparison.OrdinalIgnoreCase)) continue;
                if (g.BoneWeightsAt(child.BoneWeights) is not { } weights) { notes.Add($"blend '{blend.Name}': the reload child has no bone mask"); return null; }
                var result = new Dictionary<string, float>(StringComparer.Ordinal);
                for (int i = 0; i < boneNames.Count; i++) result[boneNames[i]] = weights.WeightOf(i);
                return result;
            }
        }
        notes.Add("no blend with a reload child under the reload state");
        return null;
    }

    static IEnumerable<int> Walk(BehaviorGraphDetail g, int? node)
    {
        var seen = new HashSet<int>();
        var stack = new Stack<(int At, int Depth)>();
        if (node is int start) stack.Push((start, 0));
        while (stack.Count > 0)
        {
            var (at, depth) = stack.Pop();
            if (!g.Nodes.TryGetValue(at, out var n) || !seen.Add(at)) continue;
            yield return at;
            if (depth >= 16) continue;
            for (int i = n.Children.Count - 1; i >= 0; i--)
                if (n.Children[i].Target is int c) stack.Push((c, depth + 1));
        }
    }
}
