using System.Collections.Concurrent;

namespace SmContent;

/// <summary>
/// sound/mix-expand (owner, 2026-11-17: "expand the sound mix tool - let me adjust specific sounds from a dropdown labeled by weapon, player interaction event, combat move, equipment, etc."):
/// the groups the per-sound editor lists sounds under. Presentation only; independent of <see cref="SoundTuningGroups"/> (the older per-class volume groups, which stay).
/// </summary>
public enum SoundMixGroup { Weapons, PlayerEvents, CombatMoves, Equipment, Voices, Ambience, Ui, Other }

/// <summary>One sound as the editor lists it: the event name (the stable id), its group, a readable label, and the weapon or item it belongs to ("" when none).</summary>
public readonly record struct SoundMixInfo(string Event, SoundMixGroup Group, string Label, string Subject);

/// <summary>
/// The mapping from the game's sound event names to the editor's groups and readable labels. **Ours** (interpretation of the install's event names, spec "Sound mix editor"): a name
/// is lower-cased, its <c>play_</c>/<c>stop_</c> prefix and faction prefix (<c>sm_</c> Marine, <c>ch_</c> Chaos, <c>or_</c> Ork, <c>ig_</c> Guard) are read, the first matching row of
/// <see cref="Rows"/> names the weapon or item and the group, and what is left of the name is the action ("fire", "reload", "swing x2"). Total: every name has a group and a label
/// (a name no row claims is <see cref="SoundMixGroup.Other"/>), so none is ever missing from the editor.
/// </summary>
public static class SoundMixCatalog
{
    sealed record Row(string[] Keys, string Subject, SoundMixGroup Group);

    static Row R(SoundMixGroup g, string subject, params string[] keys) => new(keys, subject, g);

    /// <summary>The rows, first match wins (specific names before general ones: "heavy_bolter" before "bolter", "grenade_launcher" before "grenade").</summary>
    static readonly Row[] Rows =
    [
        // Voices and announcer (character lines are "speech/..." posts and handled first; the Ork's barks are named events)
        R(SoundMixGroup.Voices, "Ork voice", "_barks", "_roar", "_snorts", "_speech", "bo_2_run", "_ambient_speech", "squig_vox", "growls", "taunt"),
        // Interface
        R(SoundMixGroup.Ui, "Menu", "menu_"), R(SoundMixGroup.Ui, "Interface", "mp_points_total", "play_ui", "_ui_", "countdown", "match_end", "victory", "defeat", "_loss"),
        // Ambience and map
        R(SoundMixGroup.Ambience, "Map ambience", "_ambs", "ambience", "ambient", "powerlines", "rolloff", "music_", "wind", "steam", "tdm_09", "shaft", "power_conduit", "world_fire"),
        R(SoundMixGroup.Ambience, "Vehicles and fly-bys", "thunderhawk", "valkyrie", "flyby", "droppod", "engine"),
        R(SoundMixGroup.Ambience, "Industrial fan", "industrial_fan"), R(SoundMixGroup.Ambience, "Gas pipe", "gas_pipe"), R(SoundMixGroup.Ambience, "Machine", "tf_machine", "machine"),
        R(SoundMixGroup.Weapons, "Impacts", "set_state_impact"),
        // Footsteps, landings, bodies
        R(SoundMixGroup.PlayerEvents, "Footsteps", "footstep", "footfalls", "footfall", "_step"),
        // Equipment (before weapons, so a grenade or jump pack is not a weapon)
        R(SoundMixGroup.Weapons, "Grenade launcher", "grenade_launcher"),
        R(SoundMixGroup.Equipment, "Jump pack", "jump_pack", "jumppack"),
        R(SoundMixGroup.Equipment, "Grenade", "grenade", "krak_", "frag_", "max_bounce"),
        R(SoundMixGroup.Equipment, "Stimpack", "stimpack", "stim_"),
        R(SoundMixGroup.Equipment, "Iron halo", "iron_halo"), R(SoundMixGroup.Equipment, "Weirdboy shield", "weirdboy_shield"),
        R(SoundMixGroup.Equipment, "Armour and gear", "armor", "armour", "reactive_blast", "shield_warning", "gear_move", "sk_foley_movement", "foley_movement"),
        R(SoundMixGroup.PlayerEvents, "Body fall", "bodyfall"), R(SoundMixGroup.PlayerEvents, "Landing", "land_normal", "_land"),
        // Melee weapons are combat moves (their swings, rev-ups, hits); ranged weapons are Weapons
        R(SoundMixGroup.CombatMoves, "Dreadnought melee", "dreadnought_melee"), R(SoundMixGroup.CombatMoves, "Power klaw", "power_klaw", "klaw"),
        R(SoundMixGroup.CombatMoves, "Psychic powers", "lightning", "spell_cast", "teleport", "_arcs"),
        R(SoundMixGroup.CombatMoves, "Chainsword", "chainsword", "chain_sword"), R(SoundMixGroup.CombatMoves, "Chain axe", "chain_axe"),
        R(SoundMixGroup.CombatMoves, "Power axe", "power_axe"), R(SoundMixGroup.CombatMoves, "Power sword", "power_sword", "powersword"),
        R(SoundMixGroup.CombatMoves, "Power fist", "power_fist", "fist_hit"), R(SoundMixGroup.CombatMoves, "Thunder hammer", "thunder_hammer", "thunderhammer"),
        R(SoundMixGroup.CombatMoves, "Power mace", "power_mace", "huge_mace"), R(SoundMixGroup.CombatMoves, "Combat knife", "combat_knife"),
        R(SoundMixGroup.CombatMoves, "Ork choppa", "choppa"), R(SoundMixGroup.CombatMoves, "Ork hand", "hand_swing"),
        R(SoundMixGroup.CombatMoves, "Bull rush", "bull_rush", "bullrush"), R(SoundMixGroup.CombatMoves, "Evade roll", "evade"),
        R(SoundMixGroup.CombatMoves, "Kick", "kick_"), R(SoundMixGroup.CombatMoves, "Stomp", "stomp"), R(SoundMixGroup.CombatMoves, "Body slam", "bodyslam"),
        R(SoundMixGroup.CombatMoves, "Melee swing", "melee_whoosh", "foley_swing", "_swing", "whoosh", "execution", "dismember"),
        // Ranged weapons
        R(SoundMixGroup.Weapons, "Lascannon", "lascannon"), R(SoundMixGroup.Weapons, "Heavy bolter", "heavy_bolter", "hb_brace"), R(SoundMixGroup.Weapons, "Storm bolter", "storm_bolter"),
        R(SoundMixGroup.Weapons, "Stalker bolter", "bolter_stalker", "stalker_bolter", "stalker"), R(SoundMixGroup.Weapons, "Bolt pistol", "bolt_pistol", "boltpistol"),
        R(SoundMixGroup.Weapons, "Bolter", "bolter", "kraken"), R(SoundMixGroup.Weapons, "Plasma cannon", "plasma_cannon", "plasmacannon"),
        R(SoundMixGroup.Weapons, "Plasma pistol", "plasma_pistol"), R(SoundMixGroup.Weapons, "Plasma rifle", "plasma_rifle"), R(SoundMixGroup.Weapons, "Plasma weapons", "plasma"),
        R(SoundMixGroup.Weapons, "Melta gun", "melta_gun", "melta"), R(SoundMixGroup.Weapons, "Autocannon", "autocannon", "auto_cannon"), R(SoundMixGroup.Weapons, "Lasgun", "lasgun"),
        R(SoundMixGroup.Weapons, "Ork shoota", "shoota"), R(SoundMixGroup.Weapons, "Ork slugga", "slugga"), R(SoundMixGroup.Weapons, "Ork gun", "ork_gun"),
        R(SoundMixGroup.Weapons, "Ork rokkit launcher", "missle_launcher", "missile_launcher", "rokkit"), R(SoundMixGroup.Weapons, "Psyker bolt", "psyker_bolt"), R(SoundMixGroup.Weapons, "Assault cannon", "assault_cannon"),
        // Player events
        R(SoundMixGroup.PlayerEvents, "Capture point", "point_being_captured", "capture"), R(SoundMixGroup.PlayerEvents, "Objective", "objective"),
        R(SoundMixGroup.PlayerEvents, "Spawn", "spawn"), R(SoundMixGroup.PlayerEvents, "Kill", "kill_shot"), R(SoundMixGroup.PlayerEvents, "Death", "death"),
        R(SoundMixGroup.PlayerEvents, "Health", "health_stage"), R(SoundMixGroup.PlayerEvents, "Stun", "stunned"), R(SoundMixGroup.PlayerEvents, "Final vengeance", "final_vengeance"),
        R(SoundMixGroup.PlayerEvents, "Pickup", "pick_up", "pickup"), R(SoundMixGroup.PlayerEvents, "Damage", "blood", "big_damage", "blind_"),
        // Generic weapon sounds (every gun): ammo, switching, impacts
        R(SoundMixGroup.Weapons, "All weapons", "dry_fire", "clip", "reload", "weapon_", "primary_switch", "secondary_switch", "zoom", "insert_clip", "bullet", "impact", "ricochet", "_hits", "_hit",
            "smack", "explo", "blast", "overheat", "venting", "mode_switch"),
    ];

    /// <summary>
    /// sound/editor-roll-hop (owner, 2026-11-17: "add a sound editor tool option for tactical hop and devastator roll", read as the Tactical roll and the Devastator / Havoc hop): two editor
    /// entries that are not events of the game. The Tactical and Chaos Tactical roll (<c>sm_mp_core_abil_evade</c>) and the Devastator and Havoc hop (<c>sm_mp_core_abil_heavy_evade</c>) post the same
    /// three events (<c>play_evade_start</c>, <c>play_evade_roll</c>, <c>play_evade_end</c>; the install's two files bind them identically), so the editor could not tell them apart by event name. A voice
    /// of one of those events made by a player who last evaded with the roll (or the hop) takes the entry's override first (<see cref="AliasOf"/>), else the event's own; nothing is changed until an
    /// entry is edited. Ours (editor presentation, the evidence is the trigger files above).
    /// </summary>
    public const string TacticalRollKey = "mix:tactical_roll", HeavyHopKey = "mix:heavy_hop";

    /// <summary>The evade kinds a player's emitter carries: set when it starts an evade (<c>NetworkView.EvadeMixKind</c>), read by <see cref="AliasOf"/>.</summary>
    public const string EvadeKindRoll = "roll", EvadeKindHop = "hop";

    static readonly string[] EvadeEvents = ["play_evade_start", "play_evade_roll", "play_evade_end"];

    /// <summary>The two entries that are not game events, listed in the Combat moves group.</summary>
    public static IReadOnlyList<string> Aliases { get; } = [TacticalRollKey, HeavyHopKey];

    /// <summary>True for an entry of <see cref="Aliases"/>.</summary>
    public static bool IsAlias(string evt) => Aliases.Contains(evt, StringComparer.OrdinalIgnoreCase);

    /// <summary>The entry a voice of <paramref name="evt"/> also answers to, given the evade kind of its emitter (null: none).</summary>
    public static string? AliasOf(string evt, string? evadeKind)
    {
        if (evadeKind is null || !EvadeEvents.Contains(evt, StringComparer.OrdinalIgnoreCase)) return null;
        return evadeKind == EvadeKindHop ? HeavyHopKey : evadeKind == EvadeKindRoll ? TacticalRollKey : null;
    }

    static readonly (string Prefix, string Faction)[] Factions = [("sm_", "Marine"), ("ch_", "Chaos"), ("or_", "Ork"), ("ig_", "Guard")];

    static readonly ConcurrentDictionary<string, SoundMixInfo> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static string GroupLabel(SoundMixGroup g) => g switch
    {
        SoundMixGroup.Weapons => "Weapons", SoundMixGroup.PlayerEvents => "Player events", SoundMixGroup.CombatMoves => "Combat moves", SoundMixGroup.Equipment => "Equipment",
        SoundMixGroup.Voices => "Voices and announcer", SoundMixGroup.Ambience => "Ambience and map", SoundMixGroup.Ui => "UI sounds", _ => "Other",
    };

    public static IReadOnlyList<SoundMixGroup> Groups { get; } = Enum.GetValues<SoundMixGroup>();

    /// <summary>The group, label and subject of an event name. Total and deterministic; the result is cached.</summary>
    public static SoundMixInfo Classify(string evt)
    {
        evt ??= "";
        if (Cache.TryGetValue(evt, out var known)) return known;
        var info = Compute(evt);
        if (Cache.Count < 20000) Cache[evt] = info;
        return info;
    }

    static SoundMixInfo Compute(string evt)
    {
        string e = evt.ToLowerInvariant();
        if (e == TacticalRollKey) return new(evt, SoundMixGroup.CombatMoves, "Tactical roll", "Tactical roll");
        if (e == HeavyHopKey) return new(evt, SoundMixGroup.CombatMoves, "Devastator / Havoc hop", "Devastator / Havoc hop");
        if (e.StartsWith("speech/", StringComparison.Ordinal))
            return new(evt, SoundMixGroup.Voices, "Voice line: " + e["speech/".Length..].Replace('/', ' ').Replace('_', ' '), "Character voice");
        bool stop = e.StartsWith("stop_", StringComparison.Ordinal);
        string core = e;
        foreach (var p in new[] { "play_", "stop_", "set_state_" })
            if (core.StartsWith(p, StringComparison.Ordinal)) { core = core[p.Length..]; break; }
        string faction = "";
        foreach (var (prefix, name) in Factions)
            if (core.StartsWith(prefix, StringComparison.Ordinal)) { core = core[prefix.Length..]; faction = name; break; }
        string matchOn = e.StartsWith("stop_", StringComparison.Ordinal) || e.StartsWith("play_", StringComparison.Ordinal) ? "_" + e[5..] + "_" : "_" + e + "_";
        foreach (var row in Rows)
            foreach (var key in row.Keys)
            {
                int at = matchOn.IndexOf(key, StringComparison.Ordinal);
                if (at < 0) continue;
                string action = Tidy(core.Replace(key.Trim('_'), "", StringComparison.Ordinal));
                if (faction.Length == 0 && matchOn.Contains("_or_", StringComparison.Ordinal)) faction = "Ork";
                return new(evt, row.Group, Label(row.Subject, action, faction, stop), row.Subject);
            }
        return new(evt, SoundMixGroup.Other, Label(Tidy(core), "", faction, stop), "");
    }

    static string Tidy(string s) => string.Join(' ', s.Split(['_', ' '], StringSplitOptions.RemoveEmptyEntries));

    static string Label(string subject, string action, string faction, bool stop)
    {
        string text = action.Length == 0 ? (stop ? subject + ": stop" : subject) : subject + ": " + (stop ? "stop " : "") + action;
        return faction.Length == 0 ? text : text + " (" + faction + ")";
    }

    // ---- the events the client knows ----

    static readonly object KnownLock = new();
    static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase) { TacticalRollKey, HeavyHopKey };
    static List<string>? _knownSorted;

    /// <summary>Adds event names to the editor's list (the client registers every name its triggers and map can post, then each name it plays).</summary>
    public static void Register(IEnumerable<string> names)
    {
        lock (KnownLock)
            foreach (var n in names)
                if (!string.IsNullOrWhiteSpace(n) && Known.Add(n)) _knownSorted = null;
    }

    public static void Register(string name) => Register([name]);

    /// <summary>Every known event name, sorted.</summary>
    public static IReadOnlyList<string> KnownEvents()
    {
        lock (KnownLock) return _knownSorted ??= Known.Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The known events of a group, sorted by label (a name only an override or a file mentions is listed too, so nothing configured is ever hidden).</summary>
    public static List<SoundMixInfo> InGroup(SoundMixGroup group, IEnumerable<string>? extra = null)
    {
        var names = new HashSet<string>(KnownEvents(), StringComparer.OrdinalIgnoreCase);
        if (extra is not null) foreach (var n in extra) names.Add(n);
        return names.Select(Classify).Where(i => i.Group == group).OrderBy(i => i.Label, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Event, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Counts per group for a list of names (the coverage report and its tests): every name is in exactly one group.</summary>
    public static Dictionary<SoundMixGroup, int> Coverage(IEnumerable<string> names)
    {
        var counts = Groups.ToDictionary(g => g, _ => 0);
        foreach (var n in names) counts[Classify(n).Group]++;
        return counts;
    }
}
