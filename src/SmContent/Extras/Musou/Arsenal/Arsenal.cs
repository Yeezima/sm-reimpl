using SmSim;
using SmSim.Extras.Musou.Arsenal;

namespace SmContent.Extras.Musou.Arsenal;

/// <summary>
/// Musou's arsenal (W0, the base; docs/new-weapons-design.md, approved by the owner 2026-11-10): the registry of the four items Musou Mode adds (added content, Musou only), with one entry each.
/// Every entry is a set of partial methods its own milestone implements in its own file (one writer each): W1 the autocannon (<c>Autocannon.cs</c>), W2 the lightning claw
/// (<c>LightningClaw.cs</c>), W3 the power spear (<c>PowerSpear.cs</c>), W4 the Remote Detonators (<c>DemolitionCharge.cs</c>). Until then each is a stub that adds nothing, so a Musou config,
/// the catalogue and every name rule are what they were.
/// <list type="bullet">
/// <item><see cref="Append"/>: the items into Musou's config (weapons, melee combos, thrown items, the classes' choice lists), called by the match tuning's load dispatcher
/// (<c>GameplayTuning.ExtrasLoad</c>) before <c>MusouLoadout.Prepare</c>, so Musou's drops, rewards, extra slots and the Librarian's lists see them. Appended at the ends of the lists:
/// no existing index moves.</item>
/// <item><see cref="MeleeMap"/>: the graph map of an added melee weapon (<c>MpClasses.ExtrasMeleeMap</c>), with its clip speed (<see cref="MeleeTuning.WeaponMap.ClipSpeed"/>).</item>
/// <item><see cref="Catalog"/>: the customizer's rows for Musou's profile (<c>PerkCatalog.ExtrasItems</c>); a profile without Musou never shows them.</item>
/// <item><see cref="AliasOf"/>: the data weapon an added one borrows its animations from (<c>AnimationAlias.ExtrasAnimationAlias</c>).</item>
/// </list>
/// The names are <see cref="ArsenalRules"/>'s; the simulation's rules are <see cref="ArsenalRules"/> and <c>World.Arsenal.cs</c>, the client's <c>client/Extras/Musou/Arsenal/</c>.
/// </summary>
public static partial class Arsenal
{
    /// <summary>Musou's config with the arsenal's items appended (the config itself while every entry is a stub).</summary>
    public static SimConfig Append(SimConfig config, string gameDir, List<string> notes)
    {
        AppendAutocannon(ref config, gameDir, notes);
        AppendLightningClaw(ref config, gameDir, notes);
        AppendPowerSpear(ref config, gameDir, notes);
        AppendRemoteDetonators(ref config, gameDir, notes);
        return config;
    }

    /// <summary>The graph map of an added melee weapon, or null (every name the entries do not know).</summary>
    public static MeleeTuning.WeaponMap? MeleeMap(string wargear)
    {
        MeleeTuning.WeaponMap? map = null;
        LightningClawMap(wargear, ref map);
        PowerSpearMap(wargear, ref map);
        return map;
    }

    /// <summary>The catalogue with the arsenal's customizer rows (the catalogue itself while every entry is a stub).</summary>
    public static PerkCatalog Catalog(PerkCatalog catalog)
    {
        AutocannonItems(ref catalog);
        LightningClawItems(ref catalog);
        PowerSpearItems(ref catalog);
        RemoteDetonatorsItems(ref catalog);
        return catalog;
    }

    /// <summary>The name the animation rules read for <paramref name="wargear"/>: a data weapon's for an added one, else the name itself.</summary>
    public static string AliasOf(string wargear)
    {
        AutocannonAlias(ref wargear);
        LightningClawAlias(ref wargear);
        PowerSpearAlias(ref wargear);
        return wargear;
    }

    // ---- W1, the autocannon (Autocannon.cs): a Devastator primary in Musou; the heavy bolter's animations.
    static partial void AppendAutocannon(ref SimConfig config, string gameDir, List<string> notes);
    static partial void AutocannonItems(ref PerkCatalog catalog);
    static partial void AutocannonAlias(ref string wargear);

    // ---- W2, the lightning claw (LightningClaw.cs): an Assault melee weapon in Musou on the power sword's map at 1.25.
    static partial void AppendLightningClaw(ref SimConfig config, string gameDir, List<string> notes);
    static partial void LightningClawMap(string wargear, ref MeleeTuning.WeaponMap? map);
    static partial void LightningClawItems(ref PerkCatalog catalog);
    static partial void LightningClawAlias(ref string wargear);

    // ---- W3, the power spear (PowerSpear.cs): an Assault melee weapon in Musou on the thunder hammer's map at 0.85.
    static partial void AppendPowerSpear(ref SimConfig config, string gameDir, List<string> notes);
    static partial void PowerSpearMap(string wargear, ref MeleeTuning.WeaponMap? map);
    static partial void PowerSpearItems(ref PerkCatalog catalog);
    static partial void PowerSpearAlias(ref string wargear);

    // ---- W4, the Remote Detonators (DemolitionCharge.cs): every Musou class's equipment.
    static partial void AppendRemoteDetonators(ref SimConfig config, string gameDir, List<string> notes);
    static partial void RemoteDetonatorsItems(ref PerkCatalog catalog);
}
