namespace SmContent;

/// <summary>
/// Which impacts at a shot's destination get the owner's deeper, muddier treatment (sound/impacts; <c>--impact-treatment</c>): <see cref="Bolters"/> (default) the
/// bolter family's hit events (bolter, bolt pistol, storm bolter, heavy bolter: <c>play_bolter_hits</c>; stalker bolter: <c>play_stalker_hits</c>), <see cref="Ballistic"/>
/// every ray-delivered weapon's impact as well (e.g. the lascannon's; cone shots such as the melta are not ballistic), <see cref="Off"/> none (the data's sound).
/// </summary>
public enum ImpactTreatmentMode { Bolters, Ballistic, Off }

/// <summary>
/// The shooter's hit confirmation (sound/impacts; <c>--hit-confirm</c>): <see cref="Soft"/> (default, the owner's change) the weapon's own confirm event with only its
/// non-positioned smack layer, pitched down, low-passed and quieter, once per hit report with a cooldown, heavier on a kill; <see cref="Data"/> the data's event as it is
/// (with the victim's armour or flesh as the material, the faithful reading); <see cref="Off"/> nothing.
/// </summary>
public enum HitConfirmMode { Soft, Data, Off }

/// <summary>
/// The owner's request of 2026-10-29 (a deliberate change from SM1: docs/changes-from-original.md): "projectile hit sounds ... very high pitched and jarring, for bolter
/// weapons at least": impacts at the destination play deeper and muddier, and the shooter hears a fainter, muddier, deeper hit confirm (inspired by Call of Duty: World
/// at War's hit sounds; built only from the install's own sounds). All values are ours.
/// </summary>
public static class ImpactSounds
{
    /// <summary>The bolter family's hit events (the data: every bolter, bolt pistol, storm bolter and heavy bolter names <c>play_bolter_hits</c>; the stalker <c>play_stalker_hits</c>).</summary>
    public static readonly IReadOnlySet<string> BolterHitEvents = new HashSet<string>(["play_bolter_hits", "play_stalker_hits"], StringComparer.OrdinalIgnoreCase);

    /// <summary>The impact treatment: 400 cents down (a major third) and a low-pass of +40 (about 2.7 kHz with the client's mapping), same level. Ours.</summary>
    public static readonly VoiceOffset ImpactTreatment = new(0f, -400f, 40f);

    /// <summary>Whether an impact of <paramref name="hitEvent"/> (a hit-scan shot's when <paramref name="hitScan"/>) gets <see cref="ImpactTreatment"/>.</summary>
    public static bool Treated(ImpactTreatmentMode mode, string hitEvent, bool hitScan) => mode switch
    {
        ImpactTreatmentMode.Bolters => BolterHitEvents.Contains(hitEvent),
        ImpactTreatmentMode.Ballistic => hitScan || BolterHitEvents.Contains(hitEvent),
        _ => false,
    };

    /// <summary>The offset for an impact post: <see cref="ImpactTreatment"/> when <see cref="Treated"/>, else none.</summary>
    public static VoiceOffset ImpactOffset(ImpactTreatmentMode mode, string hitEvent, bool hitScan) =>
        Treated(mode, hitEvent, hitScan) ? ImpactTreatment : VoiceOffset.None;

    /// <summary>Shortest time (s) between two soft hit confirms (ours): automatic fire (bolter 0.13 s, heavy bolter faster) does not machine-gun it; a kill always plays.</summary>
    public const double HitConfirmCooldown = 0.15;

    /// <summary>
    /// The soft hit confirm's treatment (ours): −10 dB (a kill −7), 500 cents down on armour, 700 on flesh (shield gone: a duller, deeper thud), low-pass +45
    /// (about 2.2 kHz). The data's smack layer sits at −32 dB at the voice, so the confirm is about −42 dB: about a bolter impact 20 m away.
    /// </summary>
    public static VoiceOffset HitConfirmOffset(bool armoured, bool kill) =>
        new(kill ? -7f : -10f, armoured ? -500f : -700f, 45f);

    /// <summary>The material the data's confirm event plays for a hit (its switch has <c>ceramite_armour</c> and <c>flesh</c> cases): the victim's armour or flesh. Interpretation.</summary>
    public static string ConfirmMaterial(bool armoured) => armoured ? "ceramite_armour" : "flesh";

    /// <summary>The state the data's confirm event's kill layer reads (group <c>0x47e2171c</c>): set by <c>play_set_state_death_armored</c> / <c>_flesh</c> before a kill's confirm.</summary>
    public static string KillStateEvent(bool armoured) => armoured ? "play_set_state_death_armored" : "play_set_state_death_flesh";

    /// <summary>The confirm events' death-state group (<c>0x47e2171c</c>, name not found) and its two death states (<c>death_flesh</c>, <c>death_armored</c>: set by the events above).</summary>
    public const uint DeathStateGroup = 0x47e2171c, DeathFlesh = 0xc8871eac, DeathArmored = 0x07fb5fa0;

    public static bool IsDeathState(uint? state) => state is DeathFlesh or DeathArmored;

    /// <summary>
    /// The death state to set before a confirm post (review fix): soft mode sets the kill's death state for a kill and clears it otherwise; data mode keeps a death state the data
    /// itself set (the Ork Boy's <c>death_action</c>) only for a kill, else clears it, so a stale one never plays the kill layer on a later hit. "Cleared" is Wwise's None.
    /// </summary>
    public static uint StateBeforeConfirm(HitConfirmMode mode, uint? current, bool kill, bool armoured)
    {
        if (mode == HitConfirmMode.Soft) return kill ? (armoured ? DeathArmored : DeathFlesh) : AudioCatalog.NoneState;
        return kill && IsDeathState(current) ? current!.Value : AudioCatalog.NoneState;
    }

    /// <summary>
    /// The death state after a confirm post: soft mode puts back what was there before (a death state becomes None); data mode keeps what the event left (the bolter, plasma
    /// and stalker smacks set <c>alive</c> after 50 ms) unless it is still a death state (an event without the reset, e.g. <c>play_grenade_launcher_smack</c>), which becomes None.
    /// </summary>
    public static uint StateAfterConfirm(HitConfirmMode mode, uint? before, uint? now)
    {
        uint v = (mode == HitConfirmMode.Soft ? before : now) ?? AudioCatalog.NoneState;
        return IsDeathState(v) ? AudioCatalog.NoneState : v;
    }

    /// <summary>Parses <c>--impact-treatment</c> / <c>--hit-confirm</c> values (names only); a missing value (the flag last) or an unknown name is an error.</summary>
    public static bool TryParse<T>(string? text, out T value, out string? error) where T : struct, Enum
    {
        value = default;
        error = null;
        string choices = string.Join(", ", Enum.GetNames<T>().Select(n => n.ToLowerInvariant()));
        if (string.IsNullOrEmpty(text) || text.StartsWith("--", StringComparison.Ordinal)) { error = $"needs a value ({choices})"; return false; }
        var name = Enum.GetNames<T>().FirstOrDefault(n => n.Equals(text, StringComparison.OrdinalIgnoreCase));
        if (name is null) { error = $"'{text}' is not one of {choices}"; return false; }
        value = Enum.Parse<T>(name);
        return true;
    }
}

/// <summary>The soft hit confirm's cooldown (<see cref="ImpactSounds.HitConfirmCooldown"/>): a kill always plays; other hits not within the cooldown of the last confirm.</summary>
public sealed class HitConfirmGate
{
    double _last = double.NegativeInfinity;

    public bool Admit(double now, bool kill)
    {
        if (!kill && now - _last >= 0 && now - _last < ImpactSounds.HitConfirmCooldown) return false;
        _last = now;
        return true;
    }
}
