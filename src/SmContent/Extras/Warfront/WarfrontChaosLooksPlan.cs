using SmSim.Extras.Musou;

namespace SmContent.Extras.Warfront;

/// <summary>Which original body a Chaos unit is drawn with (Chaos Warfront C3).</summary>
public enum WarfrontChaosBody
{
    /// <summary>An Ork-only kind (never fielded against Chaos): nothing is drawn.</summary>
    None,
    /// <summary>The co-op renegade guard (<c>renegade_guard</c>) on the Guardsman's co-op rig, lasgun in hand: the Traitor Guardsman.</summary>
    Renegade,
    /// <summary>The same renegade body, recoloured and unarmed, with the rig's melee strike: the Cultist (owner 2026-11-25: "Recoloured renegade guards").</summary>
    Cultist,
    /// <summary>A Chaos Space Marine on the MP marine rig (a PvP Chaos class look, <see cref="WarfrontChaosMarineLook"/>).</summary>
    Marine,
    /// <summary>The co-op Bloodletter on its own rig: the breach's Daemon.</summary>
    Bloodletter,
}

/// <summary>The PvP Chaos class whose look a marine-rig unit wears, and the armour set it puts on over it.</summary>
/// <param name="ClassWord">A word of the Chaos class's key or name (tactical, havoc, raptor), matched case-blind; the first Chaos class when none matches.</param>
/// <param name="SetWords">Words of a predefined Chaos armour set to wear whole, in order of preference (none: the class's default armour).</param>
/// <param name="Scale">The drawn size against the sim's body (1: as modelled).</param>
/// <param name="Held">Wargear whose model is put in the hands (the co-op unit's own first), in order of preference; empty: unarmed as Musou Mode's Chaos Marine.</param>
public sealed record WarfrontChaosMarineLook(string ClassWord, IReadOnlyList<string> SetWords, float Scale, IReadOnlyList<string> Held);

/// <summary>
/// How the Chaos army is drawn and heard (Chaos Warfront C3; design "Army aliasing", inventory docs/warfront-chaos-roster.md). Engine-free, so the tests read it. Every body, clip, sound and weapon named
/// here is an original asset of the player's install, read at run time (*original asset loaded* where the client logs it built); which one stands for which unit, the scales and the Cultist's paint
/// are **ours** (spec "Warfront Chaos presentation").
/// </summary>
public static class WarfrontChaosLooksPlan
{
    /// <summary>The co-op set that holds the whole co-op Chaos roster (inventory "Where Chaos lives": the renegade, the Bloodletter and their rigs).</summary>
    public const string CoopArchive = "data/chunks/ebp_sets__mp_coop_set_grid_test.oppc";
    public const string RenegadeRig = "havok_guardsman_a_coop__hkxx", RenegadeBody = "renegade_guard";
    public const string BloodletterRig = "havok_bloodletter_coop__hkxx", BloodletterBody = "bloodletter";
    /// <summary>The renegade's lasgun (<c>chaos_coop_lasgun</c>); the Imperial Guardsman's (<c>ig_coop_lasgun</c>, the same rig) when it cannot be mounted.</summary>
    public static readonly IReadOnlyList<string> RenegadeGuns = ["chaos_coop_lasgun", "ig_coop_lasgun"];

    /// <summary>The body of each kind in a Chaos battle (the kind's row stands for <c>WarfrontChaosRoster.Unit</c>).</summary>
    public static WarfrontChaosBody BodyOf(MusouEnemyType kind) => kind switch
    {
        MusouEnemyType.Boy => WarfrontChaosBody.Cultist,
        MusouEnemyType.ShootaBoy => WarfrontChaosBody.Renegade,
        MusouEnemyType.Gretchin => WarfrontChaosBody.Bloodletter,
        MusouEnemyType.ChaosMarine or MusouEnemyType.ArdBoy or MusouEnemyType.Nob or MusouEnemyType.BigShootaNob or MusouEnemyType.RokkitBoy or MusouEnemyType.Stormboy
            or MusouEnemyType.BossBoy or MusouEnemyType.BossShootaBoy or MusouEnemyType.BossArdBoy or MusouEnemyType.BossNob or MusouEnemyType.Warboss => WarfrontChaosBody.Marine,
        _ => WarfrontChaosBody.None,   // the Weirdboy, Mek and Squig never spawn against Chaos
    };

    /// <summary>The Chaos unit a kind's row stands for (<c>WarfrontChaosRoster.Unit</c>, the labels' source).</summary>
    public static string UnitName(MusouEnemyType kind) => SmSim.Extras.Warfront.Chaos.WarfrontChaosRoster.Unit(kind);

    /// <summary>
    /// The kind whose Chaos look draws a type byte (reviewer note, 2026-11-25): the 'Ard Boy slot is the Chaos Marine (the server sends ChaosMarine, but the client's far-army impostors pick slot kinds
    /// with <c>TfwArmyLayout.OrkType</c>, which gives ArdBoy); every other kind is its own (a Gretchin byte is the breach's Daemon: the server spawns that row only through <c>SpawnDaemon</c>).
    /// </summary>
    public static MusouEnemyType DrawnKind(MusouEnemyType kind) => kind == MusouEnemyType.ArdBoy ? MusouEnemyType.ChaosMarine : kind;

    /// <summary>Whether a kind is drawn on the marine rig (an armoured Chaos Space Marine: no Ork blood, no flinch, never torn apart).</summary>
    public static bool IsMarine(MusouEnemyType kind) => BodyOf(kind) == WarfrontChaosBody.Marine;

    /// <summary>The Chosen's set and the Lord's (owner 2026-11-25: "Elite marine, hammer"): the co-op elite's berserker helmet, then any berserker or Khorne set.</summary>
    static readonly string[] ChosenSets = ["berserk", "khorne"], LordSets = ["berserk_03", "berserker_3", "khorne", "berserk", "dlc"];
    static readonly string[] Hammer = ["chaos_coop_thunderhammer", "csm_mp_thunderhammer", "thunder_hammer", "thunderhammer"];

    /// <summary>The marine-rig units' looks (<see cref="BodyOf"/> Marine). The Chosen and the Lord are drawn at a marine's size scaled up a little, not at their rows' sim bodies (the Boss Ork rows' 3x, the Warboss row's boss scale): their bodies are C5's to set.</summary>
    public static WarfrontChaosMarineLook? MarineLook(MusouEnemyType kind) => kind switch
    {
        // Chaos Tactical Marine: the PvP Chaos Marine (the co-op chaos_coop_marine_bolter wears the basic armour), bolter in hand.
        MusouEnemyType.ChaosMarine or MusouEnemyType.ArdBoy => new("tactical", [], 1f, ["chaos_coop_bolter"]),
        // Aspiring Champion: the army's leader in a full predefined set, a little taller (ours), the chain axe (chaos_coop_chainaxe).
        MusouEnemyType.Nob => new("tactical", ["champion", "aspiring", "black_legion", "set"], 1.1f, ["chaos_coop_chainaxe"]),
        // Havocs: the PvP Havoc class (its own armour), with the heavy bolter or the missile launcher as primary (the co-op Havocs carry a lascannon and a plasma cannon: ours, the kinds' code fires those).
        MusouEnemyType.BigShootaNob => new("havoc", [], 1f, ["csm_mp_heavy_bolter", "heavy_bolter"]),
        MusouEnemyType.RokkitBoy => new("havoc", [], 1f, ["csm_mp_missile_launcher", "missile_launcher"]),
        // Raptor: the PvP Raptor class (raptor armour and its jump pack); the chain axe.
        MusouEnemyType.Stormboy => new("raptor", [], 1f, ["chaos_coop_chainaxe"]),
        // Chosen bodyguard (the four Boss Ork rows): the co-op elite's berserker look and thunder hammer.
        MusouEnemyType.BossBoy or MusouEnemyType.BossShootaBoy or MusouEnemyType.BossArdBoy or MusouEnemyType.BossNob => new("tactical", ChosenSets, ChosenScale, Hammer),
        // The Chaos Lord: the elite hammer marine in a distinct set, a modest scale-up (owner 2026-11-25).
        MusouEnemyType.Warboss => new("tactical", LordSets, LordScale, Hammer),
        _ => null,
    };

    /// <summary>The Chosen's drawn scale (ours, provisional): a head above a marine. The sim still gives their rows the Boss Ork body (3x) until C5 sets the Chosen rows.</summary>
    public const float ChosenScale = 1.2f;
    /// <summary>The Lord's drawn scale (ours, provisional; owner 2026-11-25: "a modest scale-up"). The Warboss row's sim body is the boss scale (4.5) until C5's duel gives him his own.</summary>
    public const float LordScale = 1.35f;

    /// <summary>The Cultist's paint over the renegade's own textures (ours): a dark crimson multiply, so the zealot reads apart from the drab Traitor Guardsman.</summary>
    public static readonly (float R, float G, float B) CultistTint = (0.85f, 0.32f, 0.30f);

    /// <summary>The renegade's clips on the Guardsman's co-op rig, in the crowd's order (run, idle, attack, stagger, death, sprint, the two other deaths): names the rig has (TF7a, docs/discovery.md "Guardsman"), picked by us.</summary>
    public static IReadOnlyList<string?> RenegadeClips(bool armed) => armed
        ? ["IG_NAV_Run_F_AimForward", "IG_NAV_Idle_AimForward", "IG_RNG_Fire_S_01", "IG_REC_Light_Front_Hi_Left", "IG_DIE_Basic_01", null, "IG_DIE_Basic_03", "IG_DIE_FallForward_01"]
        : ["IG_NAV_Run_F_01", "IG_NAV_Idle_01", "IG_MEL_Attack_F_01", "IG_REC_Light_Front_Hi_Left", "IG_DIE_Basic_01", null, "IG_DIE_Headshot_01", "IG_REC_Death_02"];

    /// <summary>The Bloodletter's clips on its own rig, in the crowd's order (inventory: <c>BL_NAV_*</c>, <c>BL_MEL_*</c>, <c>BL_REC_*</c>, <c>BL_DIE_*</c>), picked by us.</summary>
    public static readonly IReadOnlyList<string?> BloodletterClips =
        ["BL_NAV_Run_F_01", "BL_NAV_CombatIdle_01", "BL_MEL_Att_F_3_2M", "BL_REC_Lt_F_High_L_01", "BL_DIE_Base_01", "BL_NAV_Sprint_F_01", "BL_DIE_Base_04", "BL_DIE_Base_05"];

    /// <summary>The Bloodletter's arrival through a breach (C4 may play it on a breach spawn): <c>BL_NAV_GateSpawn_01</c>, 3.03 s.</summary>
    public const string BloodletterGateSpawn = "BL_NAV_GateSpawn_01";

    /// <summary>
    /// The sound files of the kinds not on the marine rig (the marine kinds sound as Musou Mode's Chaos Marine: the PvP Chaos blueprint, the co-op chain axe and bolter, the tactical's voice). The renegade
    /// and the Bloodletter have their co-op blueprints (steps, evades, roars, death) and weapons; no voice set is named for the renegade (inventory), so he says nothing.
    /// </summary>
    public static readonly IReadOnlyList<(MusouEnemyType Kind, string Blueprint, string Melee, string? Gun)> SoundFiles =
    [
        (MusouEnemyType.Boy, "chaos_coop_renegade_ig", "chaos_coop_lasgun", null),
        (MusouEnemyType.ShootaBoy, "chaos_coop_renegade_ig", "chaos_coop_lasgun", "chaos_coop_lasgun"),
        (MusouEnemyType.Gretchin, "chaos_coop_bloodletter", "chaos_coop_hellblade", null),
    ];

    /// <summary>The chest height of a Chaos kind's gore and hit point (m above the feet; ours): a marine's 1.3 (Musou Mode's Chaos Marine), scaled; a man's 1.2; the Bloodletter's 1.4.</summary>
    public static float ChestHeight(MusouEnemyType kind) => BodyOf(kind) switch
    {
        WarfrontChaosBody.Marine => 1.3f * (MarineLook(kind)?.Scale ?? 1f),
        WarfrontChaosBody.Bloodletter => 1.4f,
        _ => 1.2f,
    };

    /// <summary>The Musou HUD's Ork words and the Chaos unit each stands for in a Chaos battle (the boss bars and the call-outs), longest first.</summary>
    static readonly (string Ork, string Chaos)[] HudWords =
    [
        ("BOSS SHOOTA BOY", "CHOSEN"), ("BOSS 'ARD BOY", "CHOSEN"), ("ORK WARBOSS", "CHAOS LORD"), ("BOSS BOY", "CHOSEN"), ("BOSS NOB", "CHOSEN"), ("BOSS ORK", "CHOSEN"),
        ("WARBOSS", "CHAOS LORD"), ("THE HORDE PREVAILS", "CHAOS PREVAILS"),
    ];

    /// <summary>A HUD text of a Chaos battle: its Ork words as the Chaos units (<c>WarfrontChaosRoster.Unit</c>: Chosen, Chaos Lord); any other text unchanged.</summary>
    public static string HudLabel(string text)
    {
        foreach (var (ork, chaos) in HudWords)
            if (text.Contains(ork, StringComparison.Ordinal)) return text.Replace(ork, chaos, StringComparison.Ordinal);
        return text;
    }
}
