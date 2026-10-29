using SmContent;
using SmFormats.Fx;

namespace SmFormats.Tests;

/// <summary>
/// fx/no-smoke review S3: on the player's install, the named effects players read in a fight (muzzle flashes, tracers, plasma shots, charges and impact glows, the frag and the blind
/// grenade's flash, the launcher, the melta cone, the jump pack's thrust and dash, the lascannon beam and impact) keep a visible sprite, and how many sprites <see cref="SmokeRules"/> removes
/// from each is pinned. Skipped without an install (never in CI).
/// </summary>
public class SmokeInstallPinTests
{
    static readonly Lazy<string?> Game = new(() => GameInstall.TryLocate()?.Dir);
    static readonly Lazy<FxLibrary?> Library = new(() => Game.Value is { } dir ? FxLibrary.Load(dir) : null);

    /// <summary>A fact that is skipped unless the game install is found on this machine.</summary>
    public sealed class InstallTheoryAttribute : TheoryAttribute
    {
        public InstallTheoryAttribute()
        {
            if (Game.Value is null) Skip = "Space Marine install not found (the pin needs the player's install; never in CI)";
        }
    }

    public sealed class InstallFactAttribute : FactAttribute
    {
        public InstallFactAttribute()
        {
            if (Game.Value is null) Skip = "Space Marine install not found (the pin needs the player's install; never in CI)";
        }
    }

    static (int Removed, int Sprites, int VisibleKept) Count(string name)
    {
        var sys = Library.Value!.System(name) ?? throw new Xunit.Sdk.XunitException($"{name} is not in the effects package");
        var sprites = sys.ElementsWithCollisions.Select(e => (Traits: SmokeRules.Traits(e), Class: SmokeRules.Classify(e))).Where(x => x.Traits is not null).ToList();
        return (sprites.Count(x => x.Class == SmokeClass.Smoke), sprites.Count,
                sprites.Count(x => x.Class == SmokeClass.Keep && x.Traits!.Value.Blend != 2));   // distortion sprites are not drawn
    }

    [InstallTheory]
    // muzzle flashes
    [InlineData("bolter_muzzle", 0, 5)]
    [InlineData("heavy_bolter_muzzle", 1, 8)]
    [InlineData("storm_bolter_muzzle", 0, 5)]
    [InlineData("stalker_pattern_bolter_muzzle", 3, 8)]
    [InlineData("plasma_gun_muzzle", 1, 4)]
    [InlineData("plasma_cannon_muzzle", 1, 6)]
    [InlineData("grenade_launcher_muzzle", 1, 3)]
    // tracers and beams
    [InlineData("bolter_tracer", 18, 24)]
    [InlineData("heavy_bolter_tracer", 18, 24)]
    [InlineData("storm_bolter_tracer", 18, 25)]
    [InlineData("stalker_pattern_bolter_tracer", 1, 3)]
    [InlineData("plasma_gun_tracer", 2, 4)]
    [InlineData("lascannon_tracer", 2, 8)]
    // plasma: shots, charge, impact glows
    [InlineData("plasma_gun_charged_trail", 0, 6)]
    [InlineData("plasma_cannon_primary_trail", 0, 10)]
    [InlineData("plasma_charging", 1, 7)]
    [InlineData("plasma_charging_ready", 1, 6)]
    [InlineData("plasma_gun_dmg_stone", 3, 14)]
    [InlineData("plasma_gun_charged_dmg", 6, 23)]
    // grenades (the flash players look away from), the launcher
    [InlineData("frag_grenade_explosion", 6, 19)]
    [InlineData("krak_grenade_explosion", 1, 13)]
    [InlineData("grenade_launcher_explosion", 4, 26)]
    [InlineData("grenade_launcher_trail", 1, 8)]
    // melta cone, jump pack thrust (the dash plays it too) and the pound, lascannon impact, bolter impacts
    [InlineData("meltagun_cylinder_blast_4", 0, 13)]
    [InlineData("jumppack_thrust", 2, 12)]
    [InlineData("jumppack_land_heavy", 5, 14)]
    [InlineData("lascannon_blast_dmg_metal", 9, 25)]
    [InlineData("bolter_dmg_stone", 9, 17)]
    [InlineData("bolter_dmg_metal", 3, 18)]
    public void Named_effects_keep_a_visible_sprite_and_lose_this_much_smoke(string name, int removed, int sprites)
    {
        var (r, n, visible) = Count(name);
        Assert.Equal((removed, sprites), (r, n));
        Assert.True(visible > 0, $"{name} keeps no visible sprite");
    }

    [InstallFact]
    public void God_rays_lose_nothing()
    {
        foreach (var name in new[] { "amb_god_ray_thick", "amb_god_ray_thin", "amb_god_ray_huge", "amb_god_ray_flat" })
        {
            var (r, n, visible) = Count(name);
            Assert.Equal(0, r);
            Assert.True(n > 0 && visible == n, name);
        }
    }

    [InstallFact]
    public void The_map_s_fog_steam_and_sand_ambiences_draw_nothing_more()
    {
        foreach (var name in new[] { "amb_fog_patch", "amb_steam", "amb_sand_falling", "amb_steamy_fog_cool", "oob_valley_fog" })
        {
            var (r, n, _) = Count(name);
            Assert.True(n > 0 && r == n, $"{name}: {r} of {n} removed");
        }
    }
}
