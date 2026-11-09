using SmClient.Bullets;

namespace SmClient;

/// <summary>
/// plasma-original-fx (owner, 2026-11-09, Musou Mode and Classic PvP: the plasma gun's and pistol's projectiles drawn with the original game files' visuals; client presentation only, no
/// simulation, wire, protocol or config-hash change). The light shot plays the data tracer as bound (<c>plasma_gun_tracer</c> / <c>chaos_plasma_gun_tracer</c>, own colours and sizes); the
/// charged shot is its blueprint's <c>lifetime_action</c> trail alone (<c>plasma_gun_charged_trail</c> / <c>chaos_plasma_gun_trail</c>, no mesh: <c>invisible_projectile</c>), its streamers'
/// width read along the ribbon (<see cref="PlasmaOriginalLook"/>). Every shooter, own and others', every mode. <c>--old-plasma-look</c> restores the look before (light ball, recoloured thin
/// bolt, solid ball, softened ribbon head). <c>--log-plasma-look</c> logs the counts.
/// </summary>
public partial class NetworkView
{
    bool? _oldPlasmaLook;
    /// <summary><c>--old-plasma-look</c> (switch back, plasma-original-fx): the plasma look before this pass.</summary>
    bool OldPlasmaLook => _oldPlasmaLook ??= CmdFlag("old-plasma-look");
    /// <summary>The original plasma projectile look (default).</summary>
    bool OriginalPlasmaLook => PlasmaOriginalLook.Original(OldPlasmaLook);
    /// <summary>Whether a plasma gun's or pistol's projectile shows our solid roiling ball mesh beside its data trail (old look only, and not with <c>--old-plasma-ball</c>).</summary>
    bool PlasmaBallMeshShown => !OldPlasmaBall && OldPlasmaLook;

    /// <summary>The same for a snapshot projectile's weapon: the plasma cannon keeps the ball (not in this pass), the gun and pistol show it in the old look only.</summary>
    bool PlasmaBallMeshShownFor(int weaponIndex) =>
        !OldPlasmaBall && (OldPlasmaLook || _config is null || weaponIndex < 0 || weaponIndex >= _config.Weapons.Count || !PlasmaBolt.IsBoltWeapon(_config.Weapons[weaponIndex].Name));

    int _plasmaOriginalTracers, _plasmaOriginalTrails;

    string PlasmaLookLog() => $"plasma look: {(OldPlasmaLook ? "old (--old-plasma-look)" : "original")}; data light-shot tracers {_plasmaOriginalTracers}, charged trails with width along the ribbon {_plasmaOriginalTrails}";
}
