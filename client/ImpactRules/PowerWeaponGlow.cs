using System;

namespace SmClient.ImpactRules;

/// <summary>The colour a power weapon's light and field glow in.</summary>
public enum PowerGlowColour { None, Blue, Green }

/// <summary>
/// fx/weapon-visuals (owner, 2026-11-14; client presentation only, engine-free, tested; <c>NetworkView.PowerGlow.cs</c> draws it): the light every power weapon throws and the Serrated Combat Blade's green field.
/// <para>
/// The owner: "make all power weapons have a light source effect just like the thunder hammer - blue for all other power weapons - green for serrated combat knife - blue for power sword and axe";
/// "make the serrated combat blade perk make the tactical marine's combat knife have a green power field glow and green outline on the blade of the knife - like the thunder hammer but green".
/// The thunder hammer's light is the data's <c>thunderhammer_idle_light</c> (wargear <c>while_deployed_actions</c>, scale 1, attached at the hammer's <c>fx_swing</c>): one looping point light,
/// colour (3, 113, 221), full intensity, attenuation end 1 m. Only the hammer's wargear binds a light; the power sword's data light (<c>powersword_idle</c>, 115, 186, 255, 1.5 m) has been off since
/// fx/character-visuals (its edge sprites reached out of the blade), and the power axe and the daemon maul bind none. So the others get that same light (type, range, energy, looping with no flicker,
/// attached at the weapon's own marker), in the hammer's blue; the combat knife gets it in green only while its wielder has the Serrated Combat Blade perk. The green is **ours** (refine register).
/// </para>
/// </summary>
public static class PowerWeaponGlow
{
    /// <summary>The Serrated Combat Blade perk's upgrade string (<c>mp_perk_combat_knife_damage</c>).</summary>
    public const string SerratedUpgrade = "mp_perk_combat_knife_damage";

    /// <summary>The hammer's light colour (0..1 RGB): the data's (3, 113, 221). All power weapons use it.</summary>
    public static readonly (float R, float G, float B) Blue = (3f / 255f, 113f / 255f, 221f / 255f);
    /// <summary>The serrated knife's light colour (ours: the hammer's light turned green).</summary>
    public static readonly (float R, float G, float B) Green = (0.12f, 0.9f, 0.2f);
    /// <summary>The light's attenuation end, metres, and its intensity: the hammer's data (<c>thunderhammer_idle_light</c>), copied.</summary>
    public const float LightRange = 1f, LightIntensity = 1f;

    /// <summary>The markers a power weapon's light may sit at, best first: the hammer's and the maul's <c>fx_swing</c>, the sword's, then the model's grip (always present). Ours for the weapons that name none.</summary>
    public static readonly string[] LightMarkers = ["fx_swing", "fx_powersword_swing", "fx_prefix_03", "RIG_Prop_RHand"];

    /// <summary>Whether the wargear is a combat knife (<c>sm_mp_melee_combat_knife</c>, <c>csm_mp_melee_combat_knife</c>).</summary>
    public static bool IsKnife(string? wargear) => wargear?.Contains("combat_knife", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// The light's colour for a melee wargear: blue for every power weapon (thunder hammer, power sword, power axe, daemon maul, both races), green for the combat knife of a wielder with the
    /// Serrated Combat Blade, else none. A claw or spear of Musou is not one of them (it is named differently; <c>PresentedAs</c> is not asked).
    /// </summary>
    public static PowerGlowColour ColourFor(string? wargear, bool serrated)
    {
        if (wargear is null) return PowerGlowColour.None;
        if (PowerFlashRules.KindOf(wargear) is not null) return PowerGlowColour.Blue;
        return IsKnife(wargear) && serrated ? PowerGlowColour.Green : PowerGlowColour.None;
    }

    /// <summary>Whether the data already plays this weapon's light (only the thunder hammer's wargear binds it), so ours is not added on top.</summary>
    public static bool DataBindsLight(string? wargear) => PowerFlashRules.KindOf(wargear) == PowerKind.Hammer;

    /// <summary>Whether the knife's green power field (the overlay on its blade) shows: the combat knife with the perk.</summary>
    public static bool KnifeField(string? wargear, bool serrated) => serrated && IsKnife(wargear);

    public static (float R, float G, float B) Rgb(PowerGlowColour c) => c == PowerGlowColour.Green ? Green : Blue;
}

/// <summary>
/// fx/weapon-visuals (owner, 2026-11-14: "make a bolter have large and thicker tracers colored red when kraken bolts are equipped. currently the tracers are the same as a bolter without kraken bolts
/// equipped. both game modes"; client presentation only, engine-free, tested): the Kraken Bolts perk's tracer. Every bolter that can take the perk (the data's wargear entries with the Kraken
/// upgrade: the Space Marine and Chaos bolters, their gold ones and the pistol-slot bolters, <c>sm_mp_ranged_bolter</c>, <c>sm_mp_pistol_bolter</c> and the <c>csm_</c> twins) draws its tracer red and
/// larger when its shooter has the perk. The sizes and the red are **ours** (refine register); the stalker, heavy bolter and storm bolter have no Kraken entry and are unchanged.
/// </summary>
public static class KrakenTracer
{
    /// <summary>The perk's upgrade string (<c>mp_perk_weap_bolter_kraken_rounds</c>).</summary>
    public const string Upgrade = "mp_perk_weap_bolter_kraken_rounds";

    /// <summary>The share of the data's beam width and of its 40 m trail the Kraken tracer keeps (ours; the plain bolt's are 0.28 and 0.22, so 2.2 x as wide and 1.9 x as long).</summary>
    public const float WidthScale = 0.62f, TrailScale = 0.42f;

    /// <summary>The tracer's colour (0..1 RGB, ours): a saturated red; the effect's colours keep their brightness and take this hue.</summary>
    public static readonly (float R, float G, float B) Red = (1f, 0.08f, 0.04f);

    /// <summary>Whether a ranged wargear can take the Kraken Bolts perk.</summary>
    public static bool CanTake(string? wargear) =>
        wargear is not null && (wargear.Contains("ranged_bolter", StringComparison.OrdinalIgnoreCase) || wargear.Contains("pistol_bolter", StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether a shot draws the Kraken tracer: its wargear can take the perk and its shooter has it.</summary>
    public static bool Applies(string? wargear, bool shooterHasPerk) => shooterHasPerk && CanTake(wargear);
}
