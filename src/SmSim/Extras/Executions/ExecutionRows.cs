using System.Numerics;

namespace SmSim.Extras.Executions;

// Executions X7 (brief 2026-11-24, owner approval and answers 2026-11-25; design state/design-executions-1125.md "X7"; inventory docs/executions-coverage.md):
// the catalogue rows as data. Names, timings and offsets only (no original bytes): every value below was read from the install by `smtool sync-kills`
// (supported by evidence) and is listed in docs/executions-coverage.md sections 2.1, 2.3 and 2.4. SmContent.Extras.Executions.ExecutionCatalogueLoader reads
// the clips from the player's install at runtime and refreshes the lengths, root paths and event times from it; these values are the fallback and the
// reference the load test checks against.

/// <summary>
/// The original's paired sync kills as <see cref="ExecutionRow"/>s (X7): one row per move and weapon family, in the order of docs/executions-coverage.md
/// (Ork Boy O1 to O8, Gretchin O9, Chaos Marine C1 to C3, Bloodletter B1 to B3, renegade guardsman R1). A move several weapon graphs carry (the slam O3, the
/// arm break O4, the Gretchin punch O9, the backhand R1) has a row for each family with that graph's conduit priority. Row ids are <c>&lt;move&gt;.&lt;family&gt;</c>
/// (the wire carries a row's position in <see cref="All"/>: <c>ExecutionCatalogue.WireIndexOf</c> and <c>AtWire</c>, which stay stable when an install lacks a row).
/// These rows are not playable as they are: their root paths are empty. The server and the client play the rows of <c>ExecutionCatalogueLoader.Load</c>.
/// Rules the rows follow (owner 2026-11-25): the power sword has no rows of its own and plays the chainsword's (<see cref="ExecutionWeapons.FamilyOf"/>);
/// the chain axe plays the power axe's and the daemon maul the thunder hammer's; the combat knife has Ork rows only (O3, O8, O9); every row is "*" class
/// (the moves are on the shared player rig; no class has a move of its own). Events: <see cref="ExecutionEventKind.Sync"/> at the start of the clip's
/// <c>AttackAlignment</c> window, <see cref="ExecutionEventKind.Contact"/> and <see cref="ExecutionEventKind.Kill"/> at <c>ACTN_Hit_Primary_Target</c> (the
/// kill frame: interpretation, the same event marks ordinary strikes' hits), <see cref="ExecutionEventKind.Other"/> for the heal tag (<c>ACTN_give_health_*</c>,
/// the original's reward; not played) and the victim's dead branch, <see cref="ExecutionEventKind.End"/> at the start of <c>BRANCH_Melee_End</c> (the attacker is free;
/// the knife graph's <c>BRANCH_Melee_Draw_End</c>; the knife stab O8 has neither and ends at the clip end).
/// Offsets are the victim's start in the attacker's frame for the reference clip (the 3.5 m variant; the arm break's 4 m) with the victim facing him (π);
/// each variant's <see cref="ExecutionVariant.Distance"/> is the authored victim gap measured for that clip (the interaction bones met), not the name's.
/// </summary>
public static class ExecutionRows
{
    /// <summary>Raised when the rows change (hashed by <c>ExecutionCatalogue.Write</c>, X1's Executions block, with <see cref="ExecutionChoice.RulesVersion"/>).</summary>
    public const int DataVersion = 3;   // 3: the knife's arm break crossover O4.combat_knife appended (executions cues, 2026-11-27)

    /// <summary>Where the victims' halves are (docs/executions-coverage.md 4.9): kind to (archive, rig entry). The attacker clips are all on <see cref="AttackerRig"/>.</summary>
    public static readonly IReadOnlyDictionary<string, (string Archive, string Entry)> VictimRigs = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
    {
        [ExecutionTargets.OrkBoy] = ("data/chunks/ebp_sets__ork_core_units.oppc", "havok_boy__hkxx"),
        [ExecutionTargets.Gretchin] = ("data/chunks/ebp_sets__ork_core_units.oppc", "havok_gretchin__hkxx"),
        [ExecutionTargets.ChaosMarine] = ("data/chunks/ebp_sets__chaos_core_units.oppc", "havok_chaosspacemarine__hkxx"),
        [ExecutionTargets.Bloodletter] = ("data/chunks/ebp_sets__chaos_core_units.oppc", "havok_bloodletter__hkxx"),
        [ExecutionTargets.Renegade] = ("data/chunks/ebp_sets__chaos_core_units.oppc", "havok_guardsman_a__hkxx"),
    };

    /// <summary>The campaign player rig in the shared player set: every attacker clip (the same 50-bone skeleton as the MP marine and Chaos rigs, inventory 1.4).</summary>
    public static readonly (string Archive, string Entry) AttackerRig = ("data/chunks/ebp_sets__player_set.oppc", "havok_spacemarine__hkxx");

    const string CS = ExecutionWeapons.Chainsword, PA = ExecutionWeapons.PowerAxe, TH = ExecutionWeapons.ThunderHammer, CK = ExecutionWeapons.CombatKnife;
    const string CsGraph = "SM_MEL_ChainSword", PaGraph = "SM_MEL_PowerAxe", ThGraph = "SM_MEL_ThunderHammer", CkGraph = "SM_MEL_CombatKnife";

    /// <summary>
    /// One move's timing (seconds into the attacker clip): alignment start, hit, heal, free, and the victim's dead branch (null: none in the data); the heal
    /// tag's name (50 on most moves, 25 on the Gretchin punch and the backhand, 100 on the Chaos Marine struggle wins) and the free tag's (null: the clip end,
    /// no tag; the knife graph's is <c>BRANCH_Melee_Draw_End</c>, set by <see cref="Row"/>).
    /// </summary>
    readonly record struct Times(float Align, float Hit, float Heal, float Free, float? VictimDead = null, string Heals = "ACTN_give_health_50", string? Frees = "BRANCH_Melee_End");

    static IReadOnlyList<ExecutionEvent> Events(Times t)
    {
        var e = new List<ExecutionEvent>
        {
            new(ExecutionEventKind.Sync, t.Align, "AttackAlignment"),
            new(ExecutionEventKind.Contact, t.Hit, "ACTN_Hit_Primary_Target"),
            new(ExecutionEventKind.Kill, t.Hit, "ACTN_Hit_Primary_Target"),
            new(ExecutionEventKind.Other, t.Heal, t.Heals),
            new(ExecutionEventKind.End, t.Free, t.Frees),
        };
        if (t.VictimDead is { } dead) e.Add(new(ExecutionEventKind.Other, dead, "victim dead branch"));
        return e.OrderBy(x => x.Seconds).ThenBy(x => (byte)x.Kind).ToArray();
    }

    static ExecutionRow Row(string move, string family, string kind, string attackerClip, float attackerSeconds, string victimClip, float victimSeconds,
        Times t, float x, float y, string graph, string sequencer, int priority, (string Clip, float Gap)[] variants, string? note = null) =>
        new($"{move}.{family}", "*", family, kind, attackerClip, victimClip, attackerSeconds, victimSeconds,
            Events(graph == CkGraph && t.Frees is not null ? t with { Frees = "BRANCH_Melee_Draw_End" } : t), new Vector3(x, y, 0f), MathF.PI, graph, sequencer)
        {
            Priority = priority,
            VictimSequencer = VictimSequencers[move],
            AttackerVariants = variants.Select(v => new ExecutionVariant(v.Clip, v.Gap, [])).ToArray(),
            Note = note,
        };

    /// <summary>
    /// Each move's victim sequencer: the victim graph state that plays its half (the boy's "SYNC KILL / &lt;family&gt; Sync Kill / ..." states, the Gretchin's
    /// "SYNC / Sync - Chainsword", the Chaos Marine's "... CSM Loses ( Melee )", the Bloodletter's "SYNC / ... Sync", the guardsman's "SYNC KILL / CS Sync Kill"),
    /// read by <c>smtool sync-kills</c>. Its playback speed gives <see cref="ExecutionRow.VictimSeconds"/>.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> VictimSequencers = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["O1"] = "CS Grind Sequencer", ["O2"] = "CS Grind 2 Sequencer", ["O3"] = "Slam Sequencer", ["O4"] = "Arm Break Sequencer",
        ["O5"] = "Decap Sequencer", ["O6"] = "Hack Sequencer", ["O7"] = "Head Pop Sequencer", ["O8"] = "SequencerGenerator Stick00",
        ["O9"] = "SequencerGenerator_Sync_Death_Punch",
        ["C1"] = "SequencerGenerator CSM CS Sync Death", ["C2"] = "SequencerGenerator CSM Ax Sync Death", ["C3"] = "SequencerGenerator CSM TH Sync Death",
        ["B1"] = "Sequencer_Sync_Cutthroat", ["B2"] = "Sequencer_Sync_Tongue_Grab", ["B3"] = "Sequencer_Sync_Thunder_Hammer",
        ["R1"] = "SequencerGenerator_Renegade_Backhand",
    };

    // ---------------------------------------------------------------- the moves (docs/executions-coverage.md tables 2.1, 2.3, 2.4)

    static readonly Times O1T = new(0.167f, 2.200f, 2.267f, 3.294f, 1.267f), O2T = new(0.200f, 3.933f, 4.033f, 4.583f, 0.400f),
        O3T = new(0.133f, 2.433f, 2.467f, 3.381f, 0.733f), O4T = new(0.143f, 1.433f, 1.433f, 2.824f, 0.500f), O5T = new(0.069f, 1.867f, 1.867f, 2.833f, 0.400f),
        O6T = new(0.200f, 2.600f, 2.600f, 3.667f, 0.400f), O7T = new(0.200f, 2.033f, 2.033f, 3.333f, 0.633f), O8T = new(0.000f, 1.200f, 1.200f, 2.800f, Frees: null),
        O9T = new(0.167f, 0.333f, 0.367f, 0.833f, Heals: "ACTN_give_health_25"),
        C1T = new(0.168f, 4.367f, 4.400f, 6.333f, 0.833f, "ACTN_give_health_100"), C2T = new(0.106f, 2.933f, 2.967f, 4.000f, 0.833f, "ACTN_give_health_100"),
        C3T = new(0.167f, 2.533f, 2.600f, 4.000f, 0.433f, "ACTN_give_health_100"),
        B1T = new(0.200f, 3.100f, 3.083f, 3.933f), B2T = new(0.200f, 2.074f, 2.146f, 3.600f), B3T = new(0.200f, 2.267f, 2.300f, 3.667f),
        R1T = new(0.100f, 1.233f, 1.233f, 2.067f, Heals: "ACTN_give_health_25");

    static readonly (string, float)[] O1V = [("SM_SYN_ATT_F_ORKGRIND_01_1M", 0.97f), ("SM_SYN_ATT_F_ORKGRIND_01_3.5M", 3.48f), ("SM_SYN_ATT_F_ORKGRIND_01_7M", 6.98f)];
    static readonly (string, float)[] O2V = [("SM_SYN_SwordGrind_1m", 1.15f), ("SM_SYN_SwordGrind_3.5m", 3.65f), ("SM_SYN_SwordGrind_7m", 7.16f)];
    static readonly (string, float)[] O3V = [("SM_SYN_ATT_F_ORKSLAM_1M", 1.00f), ("SM_SYN_ATT_F_ORKSLAM_3.5M", 3.50f), ("SM_SYN_ATT_F_ORKSLAM_7M", 7.01f)];
    static readonly (string, float)[] O4V = [("SM_SYN_ARMBREAK_01_2m", 2.00f), ("SM_SYN_ARMBREAK_01_4m", 4.00f), ("SM_SYN_ARMBREAK_01_7m", 7.00f)];
    static readonly (string, float)[] O5V = [("SM_SYN_PA_Boy_Decap_1m", 1.70f), ("SM_SYN_PA_Boy_Decap_3.5m", 3.50f), ("SM_SYN_PA_Boy_Decap_7m", 7.00f)];
    static readonly (string, float)[] O6V = [("SM_SYN_PA_Boy_AxeHack_3.5m", 3.37f)];
    static readonly (string, float)[] O7V = [("SM_SYN_TH_OrkBoy_headPOP1M", 1.00f), ("SM_SYN_TH_OrkBoy_headPOP", 3.51f), ("SM_SYN_TH_OrkBoy_headPOP7M", 7.00f)];
    static readonly (string, float)[] O8V = [("SM_MEL_CK_XATK_HVY_1m", 1.87f), ("SM_MEL_CK_XATK_HVY_3.5m", 3.36f), ("SM_MEL_CK_XATK_HVY_7m", 6.86f)];
    static readonly (string, float)[] O9V = [("SM_SYN_GretchinPunch_1m", 1.43f), ("SM_SYN_GretchinPunch_3.5M", 3.50f), ("SM_SYN_GretchinPunch_7m", 7.00f)];
    static readonly (string, float)[] C1V = [("SM_SYN_CS_StrglWin_ChaosDecap", 1.32f)];
    static readonly (string, float)[] C2V = [("SM_SYN_PA_CSM_AxeHack_01", 1.46f)];
    static readonly (string, float)[] C3V = [("SM_SYN_Chaos_hamburger", 1.15f)];
    static readonly (string, float)[] B1V = [("SM_SYN_BloodLtr_Cutthroat_1m", 1.52f), ("SM_SYN_BloodLtr_Cutthroat_3.5m", 3.51f), ("SM_SYN_BloodLtr_Cutthroat_7m", 7.00f)];
    static readonly (string, float)[] B2V = [("SM_SYN_PA_BL_TongueGrab_1m", 2.50f), ("SM_SYN_PA_BL_TongueGrab_3.5m", 3.46f), ("SM_SYN_PA_BL_TongueGrab_7m", 6.96f)];
    static readonly (string, float)[] B3V = [("SM_SYN_TH_Bloodletter_slam1M", 2.80f), ("SM_SYN_TH_Bloodletter_slam", 3.46f), ("SM_SYN_TH_Bloodletter_slam7M", 8.84f)];
    static readonly (string, float)[] R1V = [("SM_SYN_IG_BackHand_Punch_1m", 1.43f), ("SM_SYN_IG_BackHand_Punch_3.5m", 3.91f), ("SM_SYN_IG_BackHand_Punch_7m", 7.43f)];

    const string Boy = ExecutionTargets.OrkBoy, Grot = ExecutionTargets.Gretchin, Csm = ExecutionTargets.ChaosMarine, Bl = ExecutionTargets.Bloodletter, Ig = ExecutionTargets.Renegade;

    /// <summary>Every row, in wire order (a row's index here is its wire index, <c>ExecutionCatalogue.WireIndexOf</c>): append only, never reorder or remove, and raise <see cref="DataVersion"/> on any change.</summary>
    public static readonly IReadOnlyList<ExecutionRow> All =
    [
        // Ork Boy (Warfront's Boy, Shoota Boy, 'Ard Boy, Rokkit Boy and grounded Stormboy; Classic's Ork-player victims): chainsword conduit priorities 4 / 3 / 2 / 1.
        Row("O1", CS, Boy, "SM_SYN_ATT_F_ORKGRIND_01_3.5M", 3.5f, "BOY_SYN_ATT_F_ORKGRIND_01_3.5M", 3.5f, O1T, 0.07f, 3.48f, CsGraph, "Ork Raised Grind Sequence01", 4, O1V),
        Row("O2", CS, Boy, "SM_SYN_SwordGrind_3.5m", 4.833f, "BOY_SYN_SwordGrind_3.5m", 4.833f, O2T, 0.09f, 3.65f, CsGraph, "SequencerGenerator_Ork_Grind_Sync", 3, O2V),
        Row("O3", CS, Boy, "SM_SYN_ATT_F_ORKSLAM_3.5M", 3.667f, "BOY_SYN_ATT_F_ORKSLAM_3.5M", 3.667f, O3T, 0.07f, 3.50f, CsGraph, "SequencerGenerator Ork Sync Slam", 2, O3V),
        Row("O4", CS, Boy, "SM_SYN_ARMBREAK_01_4m", 3.0f, "BOY_SYN_ARMBREAK_01_4m", 3.0f, O4T, 0.07f, 4.00f, CsGraph, "Ork Raised Grind Sequence02", 1, O4V),
        // Power axe (and the chain axe): 4 / 3 / 2 / 1.
        Row("O5", PA, Boy, "SM_SYN_PA_Boy_Decap_3.5m", 2.833f, "BOY_SYN_PA_Boy_Decap_3.5m", 3.467f, O5T, 0.00f, 3.50f, PaGraph, "SequencerGenerator28", 4, O5V),
        Row("O4", PA, Boy, "SM_SYN_ARMBREAK_01_4m", 3.0f, "BOY_SYN_ARMBREAK_01_4m", 3.0f, O4T, 0.07f, 4.00f, PaGraph, "Ork Raised Grind Sequence01", 3, O4V),
        Row("O3", PA, Boy, "SM_SYN_ATT_F_ORKSLAM_3.5M", 3.667f, "BOY_SYN_ATT_F_ORKSLAM_3.5M", 3.667f, O3T, 0.07f, 3.50f, PaGraph, "SequencerGenerator Ork Sync Slam", 2, O3V),
        Row("O6", PA, Boy, "SM_SYN_PA_Boy_AxeHack_3.5m", 3.667f, "BOY_SYN_PA_Boy_AxeHack_3.5m", 3.667f, O6T, 0.00f, 3.37f, PaGraph, "SequencerGenerator30", 1, O6V,
            "the 3.5 m variant only; the attacker turns 45 degrees; a second hit tag at 2.724 s"),
        // Thunder hammer (and the daemon maul): 2 / 1.
        Row("O7", TH, Boy, "SM_SYN_TH_OrkBoy_headPOP", 3.333f, "BOY_SYN_TH_OrkBoy_headPOP", 3.333f, O7T, 0.07f, 3.51f, ThGraph, "SequencerGenerator Ork Sync ( Head Pop )", 2, O7V),
        Row("O3", TH, Boy, "SM_SYN_ATT_F_ORKSLAM_3.5M", 3.667f, "BOY_SYN_ATT_F_ORKSLAM_3.5M", 3.667f, O3T, 0.07f, 3.50f, ThGraph, "SequencerGenerator Ork Sync Slam", 1, O3V),
        // Combat knife: Ork rows only (owner): 2 / 1.
        Row("O3", CK, Boy, "SM_SYN_ATT_F_ORKSLAM_3.5M", 3.667f, "BOY_SYN_ATT_F_ORKSLAM_3.5M", 3.667f, O3T, 0.07f, 3.50f, CkGraph, "SequencerGenerator Ork Sync Slam", 2, O3V),
        Row("O8", CK, Boy, "SM_MEL_CK_XATK_HVY_3.5m", 2.8f, "BOY_MEL_CK_XATK_HVY_3.5m", 1.933f, O8T, -0.08f, 3.36f, CkGraph, "SequencerGenerator08", 1, O8V,
            "the boy's half is its \"CK Stab\" state's clip (not a SYN name); no dead branch, free at the clip end"),
        // Gretchin: the punch on every family (the knife's priority 3; the others give none in the data).
        Row("O9", CS, Grot, "SM_SYN_GretchinPunch_3.5M", 1.567f, "GRE_SYN_GretchinPunch_3.5M", 1.567f, O9T, -0.01f, 3.50f, CsGraph, "SequencerGenerator_Punch", 0, O9V),
        Row("O9", PA, Grot, "SM_SYN_GretchinPunch_3.5M", 1.567f, "GRE_SYN_GretchinPunch_3.5M", 1.567f, O9T, -0.01f, 3.50f, PaGraph, "SequencerGenerator_Punch", 0, O9V),
        Row("O9", TH, Grot, "SM_SYN_GretchinPunch_3.5M", 1.567f, "GRE_SYN_GretchinPunch_3.5M", 1.567f, O9T, -0.01f, 3.50f, ThGraph, "SequencerGenerator_Punch", 0, O9V),
        Row("O9", CK, Grot, "SM_SYN_GretchinPunch_3.5M", 1.567f, "GRE_SYN_GretchinPunch_3.5M", 1.567f, O9T, -0.01f, 3.50f, CkGraph, "SequencerGenerator_Punch", 3, O9V),
        // Chaos Marine (Classic PvP Extended; the Chaos run's Tactical, Havoc and Champion): the struggle wins, played without the struggle (ours, by the brief).
        Row("C1", CS, Csm, "SM_SYN_CS_StrglWin_ChaosDecap", 6.333f, "CSM_SYN_CS_StrglWin_ChaosDecap", 6.333f, C1T, 0.00f, 1.32f, CsGraph, "SequencerGenerator Struggle Win ( CSM )00", 4, C1V,
            "struggle win; the struggle (clash and loop) is skipped (ours)"),
        Row("C2", PA, Csm, "SM_SYN_PA_CSM_AxeHack_01", 4.0f, "CSM_SYN_PA_CSM_AxeHack_01", 4.0f, C2T, 0.00f, 1.46f, PaGraph, "SequencerGenerator PA Sync ( CSM )00", 4, C2V,
            "struggle win; the struggle (clash and loop) is skipped (ours)"),
        Row("C3", TH, Csm, "SM_SYN_Chaos_hamburger", 4.0f, "SM2_SYN_Chaos_hamburger", 4.0f, C3T, -0.32f, 1.11f, ThGraph, "SequencerGenerator Struggle Win ( CSM )00", 4, C3V,
            "struggle win; the struggle is skipped (ours); the attacker turns 89 degrees and the victim is carried 4.7 m sideways"),
        // Bloodletter (the Chaos run).
        Row("B1", CS, Bl, "SM_SYN_BloodLtr_Cutthroat_3.5m", 3.933f, "BL_SYN_BloodLtr_Cutthroat_3.5m", 3.933f, B1T, 0.10f, 3.50f, CsGraph, "Sync Kill Bloodletter Decap Sequence01", 0, B1V),
        Row("B2", PA, Bl, "SM_SYN_PA_BL_TongueGrab_3.5m", 3.6f, "BL_SYN_PA_BL_TongueGrab_3.5m", 3.367f, B2T, 0.00f, 3.46f, PaGraph, "Sync Kill Bloodletter Tongue Grab Sequencer", 0, B2V,
            "the attacker turns 109 degrees"),
        Row("B3", TH, Bl, "SM_SYN_TH_Bloodletter_slam", 3.667f, "BL_SYN_TH_Bloodletter_slam", 3.667f, B3T, 0.00f, 3.46f, ThGraph, "Sync Kill Bloodletter Decap Sequence01", 0, B3V),
        // Renegade guardsman (the Chaos run's Traitor Guard and Cultists): the backhand punch on three families.
        Row("R1", CS, Ig, "SM_SYN_IG_BackHand_Punch_3.5m", 2.867f, "IG_SYN_IG_BackHand_Punch_3.5m", 2.867f, R1T, 0.00f, 3.91f, CsGraph, "Struggle SM Wins Sequence04", 0, R1V,
            "the victim is thrown to 8.7 m"),
        Row("R1", PA, Ig, "SM_SYN_IG_BackHand_Punch_3.5m", 2.867f, "IG_SYN_IG_BackHand_Punch_3.5m", 2.867f, R1T, 0.00f, 3.91f, PaGraph, "Struggle SM Wins Sequence03", 0, R1V,
            "the victim is thrown to 8.7 m"),
        Row("R1", TH, Ig, "SM_SYN_IG_BackHand_Punch_3.5m", 2.867f, "IG_SYN_IG_BackHand_Punch_3.5m", 2.867f, R1T, 0.00f, 3.91f, ThGraph, "Struggle SM Wins Sequence06", 0, R1V,
            "the victim is thrown to 8.7 m"),
        // Crossovers (owner brief 2026-11-27, ours: an original move on another equipped weapon; each passes the playback checks and the lead's footage review, docs/executions-cues.md 3).
        // The knife's arm break: the chainsword's O4 sequencer and clips played with the combat knife in hand (source: the brief's first trial; the shipped coverage matrix; the Support worksheet,
        // medium). Priority 0: below the knife's own O3 (2) and O8 (1), so the prompt's first fit is unchanged; the press's seeded pick takes it among the moves that align.
        Row("O4", CK, Boy, "SM_SYN_ARMBREAK_01_4m", 3.0f, "BOY_SYN_ARMBREAK_01_4m", 3.0f, O4T, 0.07f, 4.00f, CsGraph, "Ork Raised Grind Sequence02", 0, O4V,
            "crossover (ours): the chainsword's arm break with the combat knife"),
    ];
}
