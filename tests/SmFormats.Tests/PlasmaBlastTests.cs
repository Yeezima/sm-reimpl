using SmContent;
using SmSim;
using Xunit;

namespace SmFormats.Tests;

/// <summary>
/// fx/plasma-blast (the owner, 2026-11-06: "plasma explosion currently looks like this. i want you to make it look exactly like the original game"; his screenshot showed the placeholder
/// blast sphere): a detonation's material container is the projectile's own, never a per-victim one under its <c>area_of_effect/hit_actions</c>. The client draws the placeholder
/// sphere only when <see cref="EffectCatalogue.DetonationPlays"/> returns nothing (<c>NetworkView.FxDetonation</c>), so non-empty plays here mean no sphere.
/// </summary>
public class PlasmaBlastTests
{
    static FxBinding Material(string path, string container) =>
        new(path, "detonate_action", "", [], null, new FxMaterialTrigger(container, ""));

    [Fact]
    public void The_per_victim_area_container_is_not_the_blasts_container()
    {
        var projectile = new ProjectileEffects("ebps\\projectiles\\test_charged", [
            Material("extensions/projectile_ext/detonate_action/commands/area_of_effect/hit_actions/commands/trigger_material_effect", "c\\hit_from_player_highlight"),
            Material("extensions/projectile_ext/detonate_action/commands/trigger_material_effect", "c\\test_charged_blast"),
        ]);
        Assert.True(projectile.Bindings[0].OnAreaTarget);
        Assert.False(projectile.Bindings[1].OnAreaTarget);
        Assert.Equal("test_charged_blast", projectile.DetonationMaterial!.ContainerName);
        Assert.Equal("hit_from_player_highlight", projectile.DetonationMaterialOld!.ContainerName);   // the choice before (--old-plasma-blast)
        var onlyArea = new ProjectileEffects("p", [projectile.Bindings[0]]);
        Assert.Null(onlyArea.DetonationMaterial);
    }

    // ---- the player's install (skipped without it; nothing of it is kept) ----

    static readonly Lazy<string?> Game = new(() => GameInstall.TryLocate()?.Dir);

    public sealed class InstallFactAttribute : FactAttribute
    {
        public InstallFactAttribute()
        {
            if (Game.Value is null) Skip = "Space Marine install not found (needs the player's install; never in CI)";
        }
    }

    static readonly Lazy<(EffectCatalogue Catalogue, SimConfig Config)> Loaded = new(() => (EffectCatalogue.Load(Game.Value!), GameplayTuning.Load(Game.Value!).Config));

    /// <summary>The level surfaces of the MP maps (ids 0, 1, 2, 3, 4, 7: default, dirt, gravel, stone, metal, air; <see cref="Surfaces"/>), and an unknown one.</summary>
    static readonly int[] MapSurfaces = [0, 1, 2, 3, 4, 7, -1];

    [InstallFact]
    public void Every_plasma_shot_of_both_sides_plays_original_effects_and_no_placeholder()
    {
        var (catalogue, config) = Loaded.Value;
        using var fx = FxLibrary.Load(Game.Value!);
        var plasma = config.Weapons.Select((w, i) => (w, i)).Where(x => x.w.Name.Contains("plasma", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Equal(6, plasma.Count);   // gun, cannon, pistol; Space Marines and Chaos
        int detonating = 0, hitScan = 0;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (w, index) in plasma)
        {
            int stages = 1 + (w.Charge?.Stages.Count ?? 0);
            for (int stage = 0; stage < stages; stage++)
            {
                if (stage > 0 && Shots.ChargeShotOf(w, stage) is null) continue;   // a charge stage that fires no shot
                var spec = Shots.ProjectileOf(config, index, stage, false);
                if (spec is null)
                {
                    // A hit-scan shot (the gun's and pistol's basic shots): its impacts, no detonation and no blast sphere.
                    var effects = catalogue.Weapon(w.Name)!;
                    var hit = stage == 0 ? effects.HitMaterial : null;
                    if (stage == 0) { foreach (int s in MapSurfaces) Assert.NotEmpty(catalogue.Resolve(hit, ShotHit.World, s)); hitScan++; }
                    continue;
                }
                var projectile = catalogue.ProjectileFor(w.Name, spec.Blueprint);
                Assert.NotNull(projectile);
                Assert.Equal(spec.Blueprint.Split('\\')[^1], projectile!.Blueprint.Split('\\')[^1], ignoreCase: true);
                Assert.DoesNotContain("hit_from_player_highlight", projectile.DetonationMaterial?.ContainerName ?? "");
                foreach (int surface in MapSurfaces)
                {
                    var (plays, byMaterial) = catalogue.DetonationPlays(projectile, surface);
                    Assert.True(plays.Count + byMaterial.Count > 0, $"{w.Name} stage {stage} on surface {surface}: no original effect (the placeholder sphere would draw)");
                    foreach (var p in plays.Concat(byMaterial)) names.Add(p.Name);
                }
                detonating++;
            }
        }
        Assert.True(detonating >= 6, $"{detonating} detonating shots");   // the cannon's basic and charged, the gun's and pistol's charged, both sides
        Assert.True(hitScan >= 4, $"{hitScan} hit-scan basic shots");
        var failures = new List<string>();
        var systems = fx.Systems(names, failures);
        Assert.Empty(names.Where(n => !systems.ContainsKey(n)));   // every one is in master_fx
        Assert.Contains("plasma_gun_mp_overcharged_dmg", names);    // the charged cannon's own explosion (plasma_cannon_charged_mp), not the per-victim highlight
        Assert.Contains("csm_plasma_gun_mp_overcharged_dmg", names);
    }

    [InstallFact]
    public void The_old_rule_left_the_charged_cannon_without_an_explosion_on_stone_and_metal()
    {
        var (catalogue, config) = Loaded.Value;
        foreach (string name in new[] { "sm_mp_exotic_plasma_cannon", "csm_mp_exotic_plasma_cannon" })
        {
            int index = config.Weapons.Select((w, i) => (w, i)).Single(x => x.w.Name == name).i;
            var w = config.Weapons[index];
            int charged = Enumerable.Range(1, w.Charge!.Stages.Count).First(s => Shots.ProjectileOf(w, s) is not null);
            var projectile = catalogue.ProjectileFor(name, Shots.ProjectileOf(w, charged)!.Blueprint)!;
            foreach (int surface in new[] { 3, 4 })
            {
                var (oldPlays, oldMaterial) = catalogue.DetonationPlays(projectile, surface, oldRule: true);
                Assert.Empty(oldPlays.Concat(oldMaterial));   // the owner's screenshot: the placeholder sphere
                var (plays, byMaterial) = catalogue.DetonationPlays(projectile, surface);
                Assert.Contains(byMaterial, p => p.Name.EndsWith("plasma_gun_mp_overcharged_dmg", StringComparison.Ordinal));
                Assert.Empty(plays);
            }
        }
    }
}
