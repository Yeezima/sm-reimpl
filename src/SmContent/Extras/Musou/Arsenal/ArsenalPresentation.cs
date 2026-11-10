using SmSim.Extras.Musou.Arsenal;

namespace SmContent.Extras.Musou.Arsenal;

/// <summary>
/// W0b, Musou's arsenal (lead 2026-11-10, after W1 to W3 found the gaps W0 left): where each added item takes its presentation from. The client's name rules and catalogues read the install's
/// files by wargear name (the power flash, the hit synth, the sword's glow and idle sound, the weapon effects of <see cref="EffectCatalogue"/>, the sounds of <see cref="SoundTriggers"/>), and the
/// arsenal's names are ours, so without a source the claw is silent and the autocannon shows no muzzle flash, tracer or casing and plays no fire sound. Each item names the install files whose
/// presentation it shows, in order: the first that has any is used (<see cref="EffectCatalogue.WithSources"/>, <see cref="SoundTriggers.WithSources"/>; the name rules take the first).
/// <list type="bullet">
/// <item>the lightning claw: the power sword (<c>sm_mp_melee_powersword</c>);</item>
/// <item>the power spear: the thunder hammer (<c>sm_mp_melee_2hand_thunder_hammer</c>);</item>
/// <item>the autocannon: its install twin <c>csm_mp_exotic_auto_cannon</c> (Chaos only, which no class names: read beside the classes' files), then the heavy bolter
/// (<c>sm_mp_exotic_heavy_bolter</c>) only if the twin has none;</item>
/// <item>the Remote Detonators: none here (an install name with its own file; W4 draws its model, <c>NetworkView.ExtrasThrownVisual</c>).</item>
/// </list>
/// Read only by the client's Musou-gated hooks (<c>NetworkView.ExtrasPresentationSources</c>, <c>Hud.ExtrasReticle</c>, <c>Hud.ExtrasWeaponBox</c>): nothing changes without Musou's content.
/// </summary>
public static partial class Arsenal
{
    /// <summary>The power sword's file: the claw's presentation.</summary>
    public const string PowerSwordFile = "sm_mp_melee_powersword";
    /// <summary>The thunder hammer's file: the spear's presentation.</summary>
    public const string ThunderHammerFile = "sm_mp_melee_2hand_thunder_hammer";
    /// <summary>The autocannon's install twin (Chaos only, no class names it): the autocannon's effects and sounds.</summary>
    public const string AutocannonTwinFile = "csm_mp_exotic_auto_cannon";
    /// <summary>The heavy bolter's file: the autocannon's presentation only if the twin has none.</summary>
    public const string HeavyBolterFile = "sm_mp_exotic_heavy_bolter";

    static readonly IReadOnlyList<string> ClawSources = [PowerSwordFile];
    static readonly IReadOnlyList<string> SpearSources = [ThunderHammerFile];
    static readonly IReadOnlyList<string> AutocannonSources = [AutocannonTwinFile, HeavyBolterFile];

    /// <summary>The install files <paramref name="wargear"/> takes its presentation from, in order, or null (every name but the claw's, the spear's and the autocannon's).</summary>
    public static IReadOnlyList<string>? PresentationSources(string wargear) =>
        string.Equals(wargear, ArsenalRules.LightningClaw, StringComparison.OrdinalIgnoreCase) ? ClawSources
        : string.Equals(wargear, ArsenalRules.PowerSpear, StringComparison.OrdinalIgnoreCase) ? SpearSources
        : string.Equals(wargear, ArsenalRules.Autocannon, StringComparison.OrdinalIgnoreCase) ? AutocannonSources
        : null;

    /// <summary>
    /// The autocannon's reticle: the HUD movie's <c>autocannon_mc</c> and, with an enemy under it, <c>autocannon_active_mc</c> (both in the reticle container, <c>smtool gfx-dump auto --grep
    /// autocannon</c>; the twin's <c>ui_data/reticule_type</c> is "autocannon"), with no spread ticks. Its <c>weapon_type</c> is "grenade_launcher", so the type rule alone would show the
    /// launcher's. Null for every other name.
    /// </summary>
    public static ReticleSpec? Reticle(string wargear) =>
        string.Equals(wargear, ArsenalRules.Autocannon, StringComparison.OrdinalIgnoreCase) ? AutocannonReticle : null;

    static readonly ReticleSpec AutocannonReticle = new("autocannon", "autocannon_mc", "autocannon_active_mc", false);

    /// <summary>
    /// The autocannon's weapon box: the movie's <c>ammo_autocannon_mc</c> with its heat bar and the rounds left in <c>ammo_mc/ammo_txt</c>, the display the heavy bolter's family already shows
    /// (<see cref="WeaponBoxRules.For"/> "heavy_bolter"). Null for every other name.
    /// </summary>
    public static WeaponBoxSpec? WeaponBox(string wargear, bool chaos) =>
        string.Equals(wargear, ArsenalRules.Autocannon, StringComparison.OrdinalIgnoreCase) ? chaos ? AutocannonBoxChaos : AutocannonBox : null;

    // Built once (the HUD asks every frame; its show-set cache keys on the display's paths, and the rules' own specs are built once too).
    static readonly WeaponBoxSpec? AutocannonBox = WeaponBoxRules.For(ArsenalRules.Autocannon, "heavy_bolter", false), AutocannonBoxChaos = WeaponBoxRules.For(ArsenalRules.Autocannon, "heavy_bolter", true);
}
