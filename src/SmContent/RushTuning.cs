using System.Numerics;
using SmFormats.Baf;
using SmFormats.Havok;
using SmSim;

namespace SmContent;

/// <summary>
/// The charge (M29; the bull rush) of a melee weapon from the install: the weapon's wargear <see cref="Ability"/> (default
/// entries: damage, shield part, reactions, reach, spacing) and the marine rig's sprint graph <see cref="Graph"/>: the
/// sequencer playing <see cref="RushClip"/> (the rush attack; extracted motion and branch tags) and the one playing
/// <see cref="CollisionClip"/> (the stop after hitting something). Values in spec/behavior.md "Charge".
/// </summary>
public static class RushTuning
{
    public const string Ability = "ability_bullrush_01";
    public const string Graph = "MP_SM_Bolter_NuSprint";
    /// <summary>The 5 m variant of the rush (<c>AttackDistance</c> 5; a 2.5 m variant is blended by target distance in the original: not used).</summary>
    public const string RushClip = "SM_BUL_Hit_M_5m";
    public const string CollisionClip = "SM_NAV_Bullrush2Idle_F_Collision_01";

    /// <summary>The weapon's charge, or null with a note when the ability, the graph, a clip or its motion is missing.</summary>
    public static RushSpec? Read(string name, BafDocument wargear, IReadOnlyList<BehaviorGraph> graphs, Func<string, float> animationLength,
        Func<string, HavokExtractedMotion?> extractedMotion, IReadOnlyDictionary<string, string>? mapping, List<string> notes)
    {
        if (wargear.Root.Table($"ability_data/ability_definitions/{Ability}") is not BafTable ability) { notes.Add($"{name}: no {Ability}; no charge"); return null; }
        var graph = graphs.FirstOrDefault(g => g.Name.Equals(Graph, StringComparison.OrdinalIgnoreCase));
        if (graph is null) { notes.Add($"{name}: graph {Graph} not found; no charge"); return null; }
        var primaryActions = ability.Table("primary_target_actions/hit_sim/actions");
        var secondaryActions = ability.Table("secondary_target_actions/hit_sim/actions");
        var primary = Damage(primaryActions, name, notes);
        var secondary = Damage(secondaryActions, name, notes);
        if (primary is null) { notes.Add($"{name}: {Ability} has no primary damage; no charge"); return null; }
        float range = ability.Table("ability_metadata/generic_metadata")?.Float("max_range") is float r && float.IsFinite(r) && r > 0f ? r : 4f;
        float interval = ability.Float("min_time_between_activations") is float m && float.IsFinite(m) && m >= 0f ? m : 1f;

        if (Clip(RushClip, out var rushSeq) is not { } rush || Clip(CollisionClip, out _) is not { } collision) return null;
        float rushLength = rush.ClipLength;
        float? Tag(BehaviorSequencer seq, string kind, string? tagName) =>
            seq.Tags.Where(t => t.Kind == kind && (tagName is null || t.Name == tagName)).Select(t => (float?)t.Start).Min();
        float contactUntil = (Tag(rushSeq!, "Branch", "BRANCH_Attack2Idle") ?? 0.4f) * rushLength;
        float wallFrom = (Tag(rushSeq!, "WallCollisionTest", null) ?? 0.2f) * rushLength;
        // M32: the charger's hyper armour (the owner: it wears off roughly halfway through the charge). The rush clip's entity
        // properties are recorded; none is a window that ends inside the rush (install: CollisionMode 1 over the whole clip), so the
        // armour lasts half of the attack part, up to BRANCH_Attack2Idle (user's knowledge, approximate; the halving is ours).
        foreach (var t in rushSeq!.Tags.Where(t => t.Kind == "EntityProperty"))
            notes.Add($"{name}: {RushClip} {t.Name} {t.Mode} over {t.Start:F3}-{t.End:F3} of the clip: not used for the charger's hyper armour (half of the attack part, {0.5f * rush.MoveAt:F3} s)");
        return new RushSpec(Ability, rush, collision, primary, ReactionTuning.HitReaction(primaryActions, mapping),
            secondary ?? new DamageSpec(0f, 0f, 1f, 1f, 1f, 1f, false), ReactionTuning.HitReaction(secondaryActions, mapping), range, interval)
        {
            ContactUntil = Math.Min(contactUntil, rush.End), WallFrom = Math.Min(wallFrom, rush.End), HyperArmourUntil = 0.5f * rush.MoveAt,
        };

        RushMove? Clip(string clip, out BehaviorSequencer? sequencer)
        {
            var seq = graph.Sequencers.FirstOrDefault(s => s.Animations.Any(a => a.Equals(clip, StringComparison.OrdinalIgnoreCase)));
            sequencer = seq;
            if (seq is null) { notes.Add($"{name}: no {Graph} sequencer plays {clip}; no charge"); return null; }
            float length;
            HavokExtractedMotion? motion;
            try { length = animationLength(clip); motion = extractedMotion(clip); }
            catch (Exception e) when (e is InvalidDataException or KeyNotFoundException or NotSupportedException or ArgumentException
                or IndexOutOfRangeException or OverflowException)
            { notes.Add($"{name}: {clip}: {e.Message}; no charge"); return null; }
            if (!float.IsFinite(length) || length <= 0f || motion is null || motion.Samples.Count < 2) { notes.Add($"{name}: {clip} has no usable length or motion; no charge"); return null; }
            if (Vector3.Dot(Vector3.Normalize(motion.Up), Vector3.UnitZ) < 0.999f || Vector3.Dot(Vector3.Normalize(motion.Forward), Vector3.UnitY) < 0.999f)
            { notes.Add($"{name}: {clip}'s motion frame is not +Z up, +Y forward; no charge"); return null; }
            var first = new Vector2(motion.Samples[0].X, motion.Samples[0].Y);
            var path = motion.Samples.Select(v => new Vector2(v.X, v.Y) - first).ToArray();
            // Branch starts × the clip's length; the end is the first BRANCH_Sprint_Finished (the clip's end without one).
            float? Start(string branch) => seq.Tags.Where(t => t.Kind == "Branch" && t.Name == branch).Select(t => (float?)t.Start).Min();
            float end = Math.Clamp(Start("BRANCH_Sprint_Finished") ?? 1f, 0f, 1f) * length;
            float At(params string[] branches) => branches.Select(Start).FirstOrDefault(f => f is not null) is float f ? Math.Min(f * length, end) : end;
            return new RushMove(clip, path, length, end, At("BRANCH_Attack2Idle"), At("BRANCH_Evade", "BRANCH_Sprint_Evade"), At("BRANCH_To_Melee"));
        }
    }

    /// <summary>A hit's damage: <c>do_damage</c> directly in the actions or in the default <c>actions_by_upgrade</c> entry.</summary>
    internal static DamageSpec? Damage(BafTable? actions, string name, List<string> notes)
    {
        // M43: the entry chosen for the granted upgrades (Furious Charge's 100 + 50 under a perk read).
        return WeaponTuning.DamageOf(UpgradeScope.Command(actions, "do_damage"), name, notes);
    }
}
