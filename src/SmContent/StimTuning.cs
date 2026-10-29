using SmFormats.Baf;
using SmFormats.Havok;
using SmSim;

namespace SmContent;

/// <summary>
/// Combat stimulants (perks/inert-2): the equipment whose only ability is <c>ability_stimpack</c> (<c>wargear\ability_types\stimpack</c>,
/// no delivery; <c>sm_mp_eq_stimpack</c>, both sides), read into a <see cref="ThrownSpec"/> with a <see cref="StimSpec"/> (layout
/// **Verified-local**, docs/customizer-and-perks-data.md):
/// <list type="bullet">
/// <item>count: <c>common_data/ammunition/entry/by_upgrade</c>'s chosen <c>ammunition_info</c> (<c>capacity_clip</c> 1; Favor of the Armory 2);</item>
/// <item>cooldown: <c>ability_stimpack/min_time_between_activations</c> (6 s);</item>
/// <item>effect: <c>action_data/triggered_actions/actn_stims_activated/commands</c>'s <c>actions_by_upgrade</c> entry chosen for the granted
/// upgrades (<see cref="UpgradeScope.Command"/>): its <c>apply_modifiers</c> (<c>received_*_damage_health_only</c> and <c>melee_damage</c>
/// multiplications, <c>lifetime</c>) and <c>apply_upgrade_string</c> (<c>mp_reaction_immunity</c>); Master-Crafted Wargear's entry is a variant;</item>
/// <item>timing: the player graph <see cref="Graph"/>'s "Equip - StimPack" sequencer (the one with a <see cref="ReleaseEvent"/> event and a
/// <see cref="EndBranch"/> branch: 0.2 and 0.6 of <c>SM_RNG_Idle_Bolter_Reload_UB_S_01</c>, 1.5 s). Binding <c>actn_stims_activated</c> to that
/// <c>StartAction</c> event is **interpretation** (the graph names no action; the death sequencers' <c>StartAction</c> events start their actions alike).</item>
/// </list>
/// Not simulated: the effects, rumble and sound (client), the ability's <c>duration</c> 6 and <c>regen_multiplier</c> 1 (no effect at 1).
/// </summary>
public static class StimTuning
{
    public const string Ability = "ability_stimpack", Action = "actn_stims_activated";
    public const string Graph = "SM_Base_MP", ReleaseEvent = "StartAction", EndBranch = "BRANCH_UseEquipment_End";
    /// <summary>The upgrade string the stim applies that keeps its user out of hit reactions.</summary>
    public const string ReactionImmunity = "mp_reaction_immunity";

    /// <summary>Whether <paramref name="item"/> is a stim (an <see cref="Ability"/> and no thrown delivery).</summary>
    public static bool IsStim(BafDocument item) =>
        item.Root.Table("ability_data/ability_definitions/" + Ability) is not null
        && item.Root.Table("ability_data/ability_definitions/ability_utility/delivery") is null;

    /// <summary>The use's timing (release, end; seconds) from the player graph, or null with a note.</summary>
    public static (float Release, float End, string Animation)? ReadTiming(IReadOnlyList<BehaviorGraph> graphs, Func<string, float> animationLength, List<string> notes)
    {
        var seq = graphs.FirstOrDefault(g => g.Name == Graph)?.Sequencers.FirstOrDefault(s => s.Animations.Count > 0
            && s.Tags.Any(t => t.Name == ReleaseEvent) && s.Tags.Any(t => t.Kind == "Branch" && t.Name == EndBranch));
        if (seq is null) { notes.Add($"stims: no {Graph} sequencer with {ReleaseEvent} and {EndBranch}; stims not simulated"); return null; }
        float speed = seq.PlaybackSpeeds.Count > 0 && float.IsFinite(seq.PlaybackSpeeds[0]) && seq.PlaybackSpeeds[0] > 0f ? seq.PlaybackSpeeds[0] : 1f;
        float duration;
        try { duration = animationLength(seq.Animations[0]) / speed; }
        catch (Exception e) when (e is InvalidDataException or KeyNotFoundException or ArgumentException) { notes.Add($"stims: {seq.Animations[0]}: {e.Message}; stims not simulated"); return null; }
        float release = seq.Tags.Where(t => t.Name == ReleaseEvent).Min(t => t.Start) * duration;
        float end = seq.Tags.Where(t => t.Kind == "Branch" && t.Name == EndBranch).Min(t => t.Start) * duration;
        if (!(float.IsFinite(release) && float.IsFinite(end) && release >= 0f && end >= release)) { notes.Add($"stims: timing {release}/{end} s unusable; stims not simulated"); return null; }
        return (release, end, seq.Animations[0]);
    }

    /// <summary>The stim item as read with the upgrades <see cref="UpgradeScope"/> grants, or null with a note.</summary>
    public static ThrownSpec? Read(string name, BafDocument item, (float Release, float End, string Animation) timing, List<string> notes)
    {
        if (item.Root.Table("ability_data/ability_definitions/" + Ability) is not { } ability) { notes.Add($"{name}: no {Ability}"); return null; }
        var ammo = WeaponTuning.DefaultEntry(item.Root.Table("common_data/ammunition/entry/by_upgrade"), null);
        int count = WeaponTuning.Int(ammo, "capacity_clip") ?? 0;
        if (count <= 0) { notes.Add($"{name}: no stim count"); return null; }
        int initial = WeaponTuning.Int(ammo, "initial_clip_amount") ?? -1;
        float cooldown = ability.Float("min_time_between_activations") is float cd && float.IsFinite(cd) && cd > 0f ? cd : 0f;
        var commands = item.Root.Table($"action_data/triggered_actions/{Action}/commands");
        if (UpgradeScope.Command(commands, "apply_modifiers") is not { } apply) { notes.Add($"{name}: {Action} without apply_modifiers; not simulated"); return null; }
        float lifetime = apply.Float("lifetime") is float l && float.IsFinite(l) && l > 0f ? l : 0f;
        if (lifetime <= 0f) { notes.Add($"{name}: stim modifiers without a lifetime; not simulated"); return null; }
        var stim = new StimSpec(lifetime, timing.Release, timing.End) { Animation = timing.Animation };
        foreach (var m in apply.Table("modifiers")?.Entries.Where(e => e.Type == BafType.Table && e.Name == "modifier").Select(e => e.AsTable()) ?? [])
        {
            string type = m.String("modifier_type") ?? "", op = m.String("operation_type") ?? "";
            if (m.Float("amount") is not float amount || !float.IsFinite(amount) || amount < 0f) { notes.Add($"{name}: stim modifier {type} without a usable amount"); continue; }
            if (op != "multiplication") { notes.Add($"{name}: stim modifier {type} ({op}) not simulated"); continue; }
            stim = type switch
            {
                "received_world_damage_health_only" => stim with { ReceivedWorld = stim.ReceivedWorld * amount },
                "received_ranged_damage_health_only" => stim with { ReceivedRanged = stim.ReceivedRanged * amount },
                "received_melee_damage_health_only" => stim with { ReceivedMelee = stim.ReceivedMelee * amount },
                "received_explosive_damage_health_only" => stim with { ReceivedExplosive = stim.ReceivedExplosive * amount },
                "melee_damage" => stim with { MeleeDamage = stim.MeleeDamage * amount },
                _ => Noted(stim, $"{name}: stim modifier {type} ({op} {amount}) not simulated"),
            };
        }
        var strings = UpgradeScope.Command(commands, "apply_upgrade_string")?.Table("strings")?.Entries.Where(e => e.Type == BafType.String).Select(e => e.AsString()).ToList() ?? [];
        stim = stim with { ReactionImmune = strings.Any(s => s.Equals(ReactionImmunity, StringComparison.OrdinalIgnoreCase)) };
        foreach (var other in strings.Where(s => !s.Equals(ReactionImmunity, StringComparison.OrdinalIgnoreCase))) notes.Add($"{name}: stim upgrade string {other} not simulated");
        if (!stim.Valid) { notes.Add($"{name}: stim values unusable; not simulated"); return null; }
        return new ThrownSpec(name, count, initial, cooldown, 0f, ThrownSpec.NoProjectile) { Stim = stim };

        StimSpec Noted(StimSpec s, string note) { notes.Add(note); return s; }
    }
}
