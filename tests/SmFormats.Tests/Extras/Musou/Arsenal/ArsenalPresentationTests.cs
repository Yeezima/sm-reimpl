using SmContent;
using SmContent.Extras.Musou.Arsenal;
using SmFormats.Baf;
using SmFormats.Obpk;
using SmSim.Extras.Musou.Arsenal;

namespace SmFormats.Tests;

/// <summary>
/// W0b, Musou's arsenal (lead 2026-11-10): where each added item takes its presentation from (the claw the power sword, the spear the thunder hammer, the autocannon its install twin then the
/// heavy bolter), the autocannon's own HUD reticle and ammunition display, and that no name of the install has a source (the client's hooks ask Musou's content as well).
/// </summary>
public class ArsenalPresentationTests
{
    [Fact]
    public void Each_item_takes_its_presentation_from_the_designs_install_files()
    {
        Assert.Equal(["sm_mp_melee_powersword"], Arsenal.PresentationSources(ArsenalRules.LightningClaw)!);
        Assert.Equal(["sm_mp_melee_2hand_thunder_hammer"], Arsenal.PresentationSources(ArsenalRules.PowerSpear.ToUpperInvariant())!);
        Assert.Equal(["csm_mp_exotic_auto_cannon", "sm_mp_exotic_heavy_bolter"], Arsenal.PresentationSources(ArsenalRules.Autocannon)!);   // the twin first; the heavy bolter only if it has none
        Assert.Null(Arsenal.PresentationSources(ArsenalRules.RemoteDetonators));   // an install name with its own file
        // The sources are files of the install's classes or the twin, and none of them is an arsenal name (no source points back into the arsenal).
        foreach (var name in ArsenalRules.Names)
            foreach (var source in Arsenal.PresentationSources(name) ?? []) Assert.False(ArsenalRules.IsArsenal(source), source);
        foreach (var data in new[] { "sm_mp_melee_powersword", "sm_mp_melee_2hand_thunder_hammer", "csm_mp_exotic_auto_cannon", "sm_mp_exotic_heavy_bolter", "sm_mp_bolter" })
            Assert.Null(Arsenal.PresentationSources(data));
    }

    [Fact]
    public void The_autocannon_shows_the_movies_own_reticle_and_ammunition_display()
    {
        // The core rules know no autocannon (its twin's weapon_type is the launcher's), so only the Musou-gated hooks give it one: Classic's choice is unchanged.
        Assert.Null(ReticleRules.For(ArsenalRules.Autocannon, null));
        Assert.Null(WeaponBoxRules.For(ArsenalRules.Autocannon, null, false));
        var reticle = Arsenal.Reticle(ArsenalRules.Autocannon)!;
        Assert.Equal(("autocannon", "autocannon_mc", "autocannon_active_mc", false), (reticle.Family, reticle.Idle, reticle.Active, reticle.Spread));
        var box = Arsenal.WeaponBox(ArsenalRules.Autocannon, chaos: false)!;
        Assert.Equal(["ammo_autocannon_mc", "ammo_mc", "ammo_mc/ammo_txt"], box.Show);
        Assert.Equal(("ammo_autocannon_mc/ammo_bar", WeaponBoxBar.Heat), (box.Bar, box.Value));
        Assert.All(box.Show, path => Assert.Contains(path, WeaponBoxRules.Optional));
        foreach (var other in ArsenalRules.Names.Where(n => n != ArsenalRules.Autocannon).Append("sm_mp_exotic_heavy_bolter"))
        {
            Assert.Null(Arsenal.Reticle(other));
            Assert.Null(Arsenal.WeaponBox(other, chaos: true));
        }
    }

    // ---- the player's install (skipped without it; nothing of it is kept)

    static readonly Lazy<string?> Game = new(() => GameInstall.TryLocate()?.Dir);

    public sealed class InstallFactAttribute : FactAttribute
    {
        public InstallFactAttribute()
        {
            if (Game.Value is null) Skip = "Space Marine install not found (needs the player's install; never in CI)";
        }
    }

    [InstallFact]
    public void The_autocannons_twin_has_effects_and_sounds_of_its_own()
    {
        var sources = new Dictionary<string, IReadOnlyList<string>> { [ArsenalRules.Autocannon] = Arsenal.PresentationSources(ArsenalRules.Autocannon)! };
        var plain = EffectCatalogue.Load(Game.Value!);
        Assert.Null(plain.Weapon(Arsenal.AutocannonTwinFile));   // no class names the twin: the classes' catalogue never read it
        var catalogue = EffectCatalogue.Load(Game.Value!, sources.Values.SelectMany(v => v)).WithSources(sources);
        var twin = catalogue.Weapon(Arsenal.AutocannonTwinFile)!;
        Assert.NotEmpty(twin.Bindings);
        Assert.Same(twin, catalogue.Weapon(ArsenalRules.Autocannon));   // the twin's own, not the heavy bolter's fallback
        Assert.Contains(twin.Bindings, b => b.Event == "fire_fx" && b.Play is { Name.Length: > 0 });
        var triggers = SoundTriggers.Load(Game.Value!, sources.Values.SelectMany(v => v), [], (IReadOnlyList<SmFormats.Havok.BehaviorGraph>?)null).WithSources(sources);
        Assert.Same(triggers.File(Arsenal.AutocannonTwinFile), triggers.File(ArsenalRules.Autocannon));
        Assert.Contains(triggers.File(ArsenalRules.Autocannon)!.Bindings, b => b.StartEvent.Length > 0);
    }

    [InstallFact]
    public void The_autocannon_fires_the_loyalist_flash_and_tracer_with_the_twins_commands()
    {
        // W1c (owner 2026-11-10, "Loyalist effects"): the client's load (NetworkView.Fx.cs) with the arsenal's sources and effect names.
        var sources = new Dictionary<string, IReadOnlyList<string>> { [ArsenalRules.Autocannon] = Arsenal.PresentationSources(ArsenalRules.Autocannon)! };
        var names = new Dictionary<string, IReadOnlyDictionary<string, string>> { [ArsenalRules.Autocannon] = Arsenal.EffectNames(ArsenalRules.Autocannon)! };
        var catalogue = EffectCatalogue.Load(Game.Value!, sources.Values.SelectMany(v => v)).WithSources(sources).WithEffectNames(names);
        var twin = catalogue.Weapon(Arsenal.AutocannonTwinFile)!;
        var ours = catalogue.Weapon(ArsenalRules.Autocannon)!;
        Assert.Equal((Arsenal.ChaosMuzzleEffect, Arsenal.ChaosTracerEffect), (twin.Muzzle!.Name, twin.Tracer!.Name));   // the install's file as read: purple
        Assert.Equal(("autocannon_muzzle", "heavy_bolter_tracer"), (ours.Muzzle!.Name, ours.Tracer!.Name));
        Assert.Equal(twin.Muzzle with { Name = "autocannon_muzzle" }, ours.Muzzle);   // the twin's marker, scale 1.5 and length
        Assert.Equal(twin.Tracer with { Name = "heavy_bolter_tracer" }, ours.Tracer);
        Assert.Equal(twin.HitMaterial, ours.HitMaterial);
        Assert.DoesNotContain(ours.Bindings, b => b.Play is { } p && (p.Name == Arsenal.ChaosMuzzleEffect || p.Name == Arsenal.ChaosTracerEffect));
        // The heavy bolter's own tracer command has the very parameters the renamed one keeps (its file: scale 1.5, length 1, marker fx_muzzleflash).
        var heavy = catalogue.Weapon(Arsenal.HeavyBolterFile)!.Tracer!;
        Assert.Equal((heavy.Name, heavy.Scale, heavy.Length, heavy.Marker), (ours.Tracer.Name, ours.Tracer.Scale, ours.Tracer.Length, ours.Tracer.Marker));
        // Both effects are in the effects package.
        using var library = FxLibrary.Load(Game.Value!);
        Assert.Contains("autocannon_muzzle", library.SystemNames);
        Assert.Contains("heavy_bolter_tracer", library.SystemNames);
    }
}
