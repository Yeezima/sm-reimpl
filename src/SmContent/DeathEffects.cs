using SmFormats.Baf;
using SmFormats.Obpk;

namespace SmContent;

/// <summary>What one step of a death action does.</summary>
public enum DeathStepKind { Effect, Sound, RemoveWargear }

/// <summary>
/// One command of a death action with the time it is due after the action starts: a <c>play_fx</c> (<see cref="Fx"/>), a <c>post_sound_event</c> (<see cref="Sound"/>, its
/// <c>time_delay</c> in milliseconds already in <see cref="Delay"/>) or a <c>remove_wargear</c> (<see cref="Slot"/>). <see cref="Delay"/> adds up the <c>delayed_action</c>s around it.
/// </summary>
public sealed record DeathStep(DeathStepKind Kind, float Delay, FxPlay? Fx = null, string Sound = "", string Slot = "");

/// <summary>A named action of the MP data that a death runs (<see cref="Source"/>: where it is read from), flattened to its timed steps.</summary>
public sealed record DeathAction(string Name, string Source, IReadOnlyList<DeathStep> Steps)
{
    public IEnumerable<DeathStep> Effects => Steps.Where(s => s.Kind == DeathStepKind.Effect && s.Fx is { Name.Length: > 0 });
    public IEnumerable<string> EffectNames => Effects.Select(s => s.Fx!.Name).Distinct(StringComparer.Ordinal);

    /// <summary>Reads the commands of an action (<paramref name="commands"/> is its <c>commands</c> table), following <c>delayed_action</c>s; area-of-effect, random and camera commands are not death presentation and are left out.</summary>
    public static DeathAction Read(string name, string source, BafTable? commands)
    {
        var steps = new List<DeathStep>();
        if (commands is { } c) Walk(c, 0f, steps, 0);
        return new DeathAction(name, source, steps);
    }

    static void Walk(BafTable commands, float offset, List<DeathStep> steps, int depth)
    {
        if (depth > 8) return;
        foreach (var e in commands.Entries)
        {
            if (e.Type != BafType.Table) continue;
            var t = e.AsTable();
            string kind = t.String("$ref") ?? "";
            if (kind.EndsWith("action_commands\\play_fx", StringComparison.OrdinalIgnoreCase))
                steps.Add(new DeathStep(DeathStepKind.Effect, offset, FxPlay.Read(t)));
            else if (kind.EndsWith("action_commands\\post_sound_event", StringComparison.OrdinalIgnoreCase))
            {
                if (t.String("start_event_name") is { Length: > 0 } ev) steps.Add(new DeathStep(DeathStepKind.Sound, offset + Number(t, "time_delay") / 1000f, Sound: ev));
            }
            else if (kind.EndsWith("action_commands\\remove_wargear", StringComparison.OrdinalIgnoreCase))
                steps.Add(new DeathStep(DeathStepKind.RemoveWargear, offset, Slot: t.String("wargear_slot") ?? ""));
            else if (kind.EndsWith("action_commands\\delayed_action", StringComparison.OrdinalIgnoreCase))
            {
                if (t.Table("action/commands") is { } inner) Walk(inner, offset + Number(t, "delay"), steps, depth + 1);
            }
        }
    }

    static float Number(BafTable t, string name) => t[name] is { } e ? e.Type switch { BafType.Float => e.AsFloat(), BafType.Int => e.Raw, _ => 0f } : 0f;
}

/// <summary>A death action that needs a perk: the player blueprint's <c>death_action/actions_by_upgrade</c> entry for <see cref="Upgrade"/> (Final Vengeance: <c>mp_perk_jump_pack_explodes_on_death</c>).</summary>
public sealed record PerkDeath(string Upgrade, DeathAction Action);

/// <summary>The death actions of one side: its player blueprint's <c>actn_plasma_death</c> and <c>actn_melta_death</c> and perk death entries, and its jump pack's <c>actn_jumpdeath_start</c> and <c>actn_jumpdeath_impact</c>.</summary>
public sealed record DeathSide(DeathAction? Plasma, DeathAction? Melta, DeathAction? JumpStart, DeathAction? JumpImpact, IReadOnlyList<PerkDeath> Perks)
{
    public static readonly DeathSide Empty = new(null, null, null, null, []);

    public IEnumerable<DeathAction> All => new[] { Plasma, Melta, JumpStart, JumpImpact }.OfType<DeathAction>().Concat(Perks.Select(p => p.Action));
}

/// <summary>The weapon families whose kills the player blueprint binds a death action to.</summary>
public enum DeathFamily { None, Plasma, Melta }

/// <summary>Which death action a death plays.</summary>
public enum DeathKind { None, Plasma, Melta, Jump }

/// <summary>
/// The death effects the original multiplayer data binds to marine and Chaos player deaths (fidelity task F-D1; evidence in docs/discovery.md "Death effects" and
/// spec/behavior.md "Death effects"). Read from the install at run time: the player blueprints (<c>sm_space_marine_player</c>, <c>csm_chaos_marine_player</c> in <c>ebps</c>)
/// and the jump packs (<c>sm_mp_abil_jumppack</c>, <c>csm_mp_abil_jumppack</c> in <c>wargear</c>).
/// <para>
/// The data holds the actions and the markers they play at but no trigger: the player behaviour graph (<c>SM_Base_MP</c>) has a DEATH state whose Plasma, Melta and Jumppack
/// death sequencers carry <c>StartAction</c> events a frame in, chosen by the killing hit's reaction (the plasma weapons' <c>rng_plasma_*</c> reactions carry <c>BRANCH_Plasma_Death</c>, the melta's
/// <c>rng_melta_*</c> <c>BRANCH_Melta_Death</c>) or by <c>BOOL_JumpIsActive</c>. Binding those events to these actions is **interpretation** (<see cref="DeathEffectRules"/>).
/// </para>
/// </summary>
public sealed class DeathEffects
{
    public const string MarineJumpPack = "sm_mp_abil_jumppack", ChaosJumpPack = "csm_mp_abil_jumppack";
    public const string PlasmaAction = "actn_plasma_death", MeltaAction = "actn_melta_death";
    public const string JumpStartAction = "actn_jumpdeath_start", JumpImpactAction = "actn_jumpdeath_impact";
    const string TriggeredActions = "extensions/entity_actions_ext/triggered_actions/";
    const string WargearActions = "action_data/triggered_actions/";
    const string DeathActionUpgrades = "extensions/health_ext/death_action/commands/actions_by_upgrade/actions";

    public required DeathSide Marines { get; init; }
    public required DeathSide Chaos { get; init; }
    /// <summary>The death family of each wargear file that names a plasma or melta reaction (<c>trigger_reaction</c>/<c>reaction_type</c>), by file name.</summary>
    public required IReadOnlyDictionary<string, DeathFamily> Families { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = [];

    public DeathSide Side(bool chaos) => chaos ? Chaos : Marines;

    /// <summary>
    /// The family of a weapon's kills: the file's own reactions (<see cref="Families"/>), else its name (the plasma cannon's reaction types are not in its wargear file; the graph has the
    /// ports <c>PORT_rng_plasma_cannon_strong</c> / <c>_weak</c> all the same), else <see cref="DeathFamily.None"/>.
    /// </summary>
    public DeathFamily FamilyOf(string wargear) =>
        Families.TryGetValue(wargear, out var f) ? f
        : wargear.Contains("plasma", StringComparison.OrdinalIgnoreCase) ? DeathFamily.Plasma
        : wargear.Contains("melta", StringComparison.OrdinalIgnoreCase) ? DeathFamily.Melta : DeathFamily.None;

    /// <summary>Every effect any of the actions plays, for preloading.</summary>
    public IEnumerable<string> EffectNames => Marines.All.Concat(Chaos.All).SelectMany(a => a.EffectNames).Distinct(StringComparer.Ordinal);

    /// <summary>
    /// The seconds each effect is played over: the largest <c>length</c> of its <c>play_fx</c> commands (a length of 0 or less: 1 s). The effects whose elements all have a duration of 0
    /// (<c>plasma_limb_vanish</c>, <c>jumppack_dmg_burn_limb</c>, <c>jumppack_death_mp</c>, <c>jumppack_predeath</c>) are compiled with it as their element duration (interpretation).
    /// </summary>
    public IReadOnlyDictionary<string, float> EffectLengths => Marines.All.Concat(Chaos.All).SelectMany(a => a.Effects).Select(s => s.Fx!)
        .GroupBy(p => p.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Max(p => p.Length) is var l and > 0f ? l : 1f, StringComparer.Ordinal);

    /// <summary>Reads the install's files (a few hundred milliseconds; for a background thread).</summary>
    public static DeathEffects Load(string gameDir, List<string>? notes = null)
    {
        notes ??= [];
        var blueprints = new Dictionary<string, BafDocument>(StringComparer.OrdinalIgnoreCase);
        using (var archive = ObpkArchive.Open(Path.Combine(gameDir, GameplayTuning.BlueprintPackage)))
            archive.ReadEntries(e => archive.GroupName(e) == "attr_pc" && e.FileName is GameplayTuning.MarineBlueprint or GameplayTuning.ChaosBlueprint,
                (e, d) => blueprints.TryAdd(e.FileName, BafDocument.Read(d)));
        var wargear = new Dictionary<string, BafDocument>(StringComparer.OrdinalIgnoreCase);
        using (var archive = ObpkArchive.Open(Path.Combine(gameDir, GameplayTuning.WargearPackage)))
            archive.ReadEntries(e => archive.GroupName(e) == "attr_pc" && (e.FileName is MarineJumpPack or ChaosJumpPack || MaybeFamily(e.FileName)),
                (e, d) => wargear.TryAdd(e.FileName, BafDocument.Read(d)));
        return Build(blueprints.GetValueOrDefault(GameplayTuning.MarineBlueprint), blueprints.GetValueOrDefault(GameplayTuning.ChaosBlueprint),
            wargear.GetValueOrDefault(MarineJumpPack), wargear.GetValueOrDefault(ChaosJumpPack),
            wargear.Where(kv => kv.Key is not (MarineJumpPack or ChaosJumpPack)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase), notes);
    }

    static bool MaybeFamily(string file) => file.Contains("plasma", StringComparison.OrdinalIgnoreCase) || file.Contains("melta", StringComparison.OrdinalIgnoreCase);

    /// <summary>The effects from already-read files (testable without an install). <paramref name="weapons"/>: the wargear files to classify.</summary>
    public static DeathEffects Build(BafDocument? marine, BafDocument? chaos, BafDocument? marinePack, BafDocument? chaosPack, IReadOnlyDictionary<string, BafDocument> weapons, List<string>? notes = null)
    {
        notes ??= [];
        var families = new Dictionary<string, DeathFamily>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, doc) in weapons)
            if (FamilyOfFile(doc) is var f and not DeathFamily.None) families[name] = f;
        return new DeathEffects
        {
            Marines = ReadSide("sm", marine, marinePack, notes), Chaos = ReadSide("csm", chaos, chaosPack, notes), Families = families, Notes = notes,
        };
    }

    /// <summary>The family a wargear file's reactions name: the first <c>reaction_type</c> mentioning plasma or melta ("rng_plasma_gun_weak", "rng_melta_short"), in file order.</summary>
    public static DeathFamily FamilyOfFile(BafDocument doc)
    {
        foreach (var r in EffectCatalogue.Strings(doc.Root, "reaction_type"))
        {
            if (r.Contains("plasma", StringComparison.OrdinalIgnoreCase)) return DeathFamily.Plasma;
            if (r.Contains("melta", StringComparison.OrdinalIgnoreCase)) return DeathFamily.Melta;
        }
        return DeathFamily.None;
    }

    static DeathSide ReadSide(string side, BafDocument? player, BafDocument? pack, List<string> notes)
    {
        DeathAction? Triggered(BafDocument? doc, string prefix, string action, string owner)
        {
            if (doc?.Root.Table(prefix + action + "/commands") is not { } commands) { if (doc is not null) notes.Add($"{side}: no {action} in {owner}"); return null; }
            return DeathAction.Read(action, owner + "/" + prefix + action, commands);
        }
        var perks = new List<PerkDeath>();
        if (player?.Root.Table(DeathActionUpgrades) is { } actions)
            foreach (var e in actions.Entries.Where(x => x.Type == BafType.Table))
            {
                var entry = e.AsTable();
                var required = entry.Table("required_upgrade_name/required_list")?.Entries.Where(x => x.Type == BafType.String).Select(x => x.AsString()).ToList() ?? [];
                if (required.Count == 0) continue;
                perks.Add(new PerkDeath(required[0], DeathAction.Read("death_action:" + required[0], "death_action/actions_by_upgrade", entry.Table("action/commands"))));
            }
        return new DeathSide(Triggered(player, TriggeredActions, PlasmaAction, side + " player blueprint"), Triggered(player, TriggeredActions, MeltaAction, side + " player blueprint"),
            Triggered(pack, WargearActions, JumpStartAction, side + " jump pack"), Triggered(pack, WargearActions, JumpImpactAction, side + " jump pack"), perks);
    }
}

/// <summary>
/// When and which death action plays (the rules of F-D1; the engine-free part of the client's death effects). Everything here is read from the
/// player behaviour graph <c>SM_Base_MP</c>'s DEATH state (extracted); binding its <c>StartAction</c> events to the actions of the same name is **interpretation**.
/// </summary>
public static class DeathEffectRules
{
    /// <summary>
    /// Seconds from the death to the plasma and melta actions: the death sequencers' <c>StartAction</c> event sits at 0.0147 of the 2.267 s plasma clip, 0.05 / 0.0588 of the
    /// 0.667 / 0.567 s melta clips = 1/30 s in every one (extracted; the frame after the state is entered).
    /// </summary>
    public const float StartActionDelay = 1f / 30f;

    /// <summary>Seconds from the death to <c>actn_jumpdeath_start</c>: the Jumppack Death Start sequencer's <c>StartAction</c> at 0.0333 of its 0.6 s clip (extracted).</summary>
    public const float JumpStartDelay = 0.02f;

    /// <summary>
    /// The least seconds from <c>actn_jumpdeath_start</c> to <c>actn_jumpdeath_impact</c>: the start clip (0.6 s) hands over to the fall at 0.9412 of its length
    /// (<c>BRANCH_Jump_Death_Start</c>, extracted) and the impact event follows the floor test of the fall state, so never before the hand-over.
    /// </summary>
    public const float JumpFallMinimum = 0.6f * 0.9412f;

    /// <summary>Longest seconds the impact waits for the body to reach the ground (ours; the original's fall state ends on a floor collision test).</summary>
    public const float JumpImpactLimit = 4f;

    /// <summary>
    /// Which action a death plays. A death in a jump (<c>BOOL_JumpIsActive</c>: the graph's transition into the Jumppack death has the highest priority, 10, above the
    /// headshot, lascannon, melta and plasma deaths) with a jump pack plays the jump death; else the killing weapon's family picks plasma or melta; else none.
    /// </summary>
    public static DeathKind Choose(bool jumping, bool hasJumpPack, DeathFamily family) =>
        jumping && hasJumpPack ? DeathKind.Jump : family switch { DeathFamily.Plasma => DeathKind.Plasma, DeathFamily.Melta => DeathKind.Melta, _ => DeathKind.None };

    /// <summary>The action of a kind on a side (null: none).</summary>
    public static DeathAction? ActionOf(DeathSide side, DeathKind kind) => kind switch
    {
        DeathKind.Plasma => side.Plasma, DeathKind.Melta => side.Melta, DeathKind.Jump => side.JumpStart, _ => null,
    };

    /// <summary>Seconds from the death to the action's start.</summary>
    public static float StartOf(DeathKind kind) => kind == DeathKind.Jump ? JumpStartDelay : StartActionDelay;

    /// <summary>
    /// Whether the jump death's impact is due: the body is near the ground (<paramref name="heightAbove"/> metres above the level below it, null when there is none within reach)
    /// and the fall state has begun, or <see cref="JumpImpactLimit"/> seconds have passed.
    /// </summary>
    public static bool ImpactDue(float secondsSinceStart, float? heightAbove, float nearGround = 0.6f) =>
        secondsSinceStart >= JumpImpactLimit || (secondsSinceStart >= JumpFallMinimum && heightAbove is { } h && h <= nearGround);

    /// <summary>
    /// Whether a rig bone is the data's marker: equal ignoring case and underscores (ours: the melta death names <c>rig_lhand</c>, <c>rig_rthigh</c> and <c>rig_lcalf</c>, which the marine rig
    /// has as <c>RIG_L_Hand</c>, <c>RIG_R_Thigh</c> and <c>RIG_L_Calf</c>; <c>rig_chest</c> and <c>rig_spine1</c> match no bone).
    /// </summary>
    public static bool SameBone(string bone, string marker)
    {
        int a = 0, b = 0;
        while (true)
        {
            while (a < bone.Length && bone[a] == '_') a++;
            while (b < marker.Length && marker[b] == '_') b++;
            if (a >= bone.Length || b >= marker.Length) return a >= bone.Length && b >= marker.Length;
            if (char.ToLowerInvariant(bone[a]) != char.ToLowerInvariant(marker[b])) return false;
            a++; b++;
        }
    }

    /// <summary>The upgrade string of Final Vengeance, whose blueprint entry has a death presentation (<c>jumppack_predeath</c>, then after 2.5 s the explosion).</summary>
    public const string FinalVengeanceUpgrade = "mp_perk_jump_pack_explodes_on_death";
}
