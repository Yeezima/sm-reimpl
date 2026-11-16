using SmContent;
using SmContent.Extras.Musou.Arsenal;
using SmFormats.Baf;
using SmSim;
using SmSim.Extras.Musou.Arsenal;

namespace SmFormats.Tests;

/// <summary>
/// The autocannon pass 2 (the owner, 2026-11-11: "Give the chaos autocannon the chaos visual effects all the way through"): the effect catalogue as the client builds it for a match with the autocannon
/// (its twin read beside the classes' files, copied under the autocannon's name, the loyalist renames on that copy), and the entry each side's shot plays (<see cref="Arsenal.EffectsFor"/>, the
/// client's <c>NetworkView.ExtrasShooterEffects</c>): a Havoc's is the twin's own, Chaos through and through (flash, tracer, impact, stop smoke, casing); a Devastator's the loyalist copy. A synthetic
/// twin file named and shaped like the install's (the Chaos effect names are the ones <see cref="Arsenal"/> names); no original data.
/// </summary>
public partial class EffectCatalogueTests
{
    const string TwinContainer = "material_effect_container\\weapon_hits\\chaos\\ranged_weapons\\autocannon";

    /// <summary>The twin: the Chaos muzzle flash and a casing on fire, the Chaos tracer and the Chaos impact container on a hit, the Chaos smoke on stopping.</summary>
    static byte[] Twin() => SyntheticBafWriter.Build(
        ("common_data", new (string, object)[] { ("slot", "ranged") }),
        ("ability_data", new (string, object)[] { ("ability_definitions", new (string, object)[]
        {
            ("ability_ranged_primary", new (string, object)[] { ("delivery", new (string, object)[]
            {
                ("$ref", "wargear\\ability_components\\delivery\\ranged_delivery"),
                ("fire_fx", Filter(([("play_fx", PlayFx("shell_casing", 1.5f, "fx_shells_eject", attached: true))], [[("play_fx", PlayFx(Arsenal.ChaosMuzzleEffect, 1.5f, "fx_muzzle"))]], []))),
                ("hit_fx", Filter(([], [[("play_fx", PlayFx(Arsenal.ChaosTracerEffect, 1.5f, "fx_muzzle", attached: true, target: "hitpoint")), ("trigger_material_effect", Material(TwinContainer))]], []))),
                ("stop_fire_fx", Filter(([("play_fx", PlayFx("chaos_autocannon_smoke", 1f, "fx_muzzle", attached: true))], [], []))),
                ("max_range", 150f), ("muzzle_marker", "fx_muzzle"),
            }) }),
        }) }));

    static byte[] TwinHits(string prefix) => SyntheticBafWriter.Build(
        ("fx", new (string, object)[] { ("default", new (string, object)[] { ("default", new (string, object)[] { ("play_fx", PlayFx(prefix + "autocannon_dmg", 0.3f, direction: "hitpoint_normal")) }) }) }));

    /// <summary>The catalogue as <c>NetworkView.SetUpFx</c> reads it for a match whose config carries the autocannon (<paramref name="extras"/>).</summary>
    static EffectCatalogue MatchCatalogue(ContentExtras extras)
    {
        var read = EffectCatalogue.Build(
            new Dictionary<string, BafDocument> { [Arsenal.AutocannonTwinFile] = BafDocument.Read(Twin()) },
            _ => null,
            new Dictionary<string, IReadOnlyList<BafDocument>> { ["autocannon"] = [BafDocument.Read(TwinHits("")), BafDocument.Read(TwinHits("chaos_"))] },
            BafDocument.Read(Global([.. Surfaces.Default])), BafDocument.Read(Player()), null);
        var sources = new Dictionary<string, IReadOnlyList<string>> { [ArsenalRules.Autocannon] = Arsenal.PresentationSources(extras, ArsenalRules.Autocannon)! };
        var names = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase) { [ArsenalRules.Autocannon] = Arsenal.EffectNames(extras, ArsenalRules.Autocannon)! };
        return read.WithSources(sources).WithEffectNames(names);
    }

    [Fact]
    public void A_Havocs_autocannon_shot_plays_the_Chaos_twins_effects_and_a_Devastators_the_loyalist_ones()
    {
        foreach (var extras in new[] { ContentExtras.All, ContentExtras.Autocannon })
        {
            var c = MatchCatalogue(extras);
            var chaos = c.Weapon(Arsenal.EffectsFor(extras, ArsenalRules.Autocannon, chaos: true))!;
            var loyal = c.Weapon(Arsenal.EffectsFor(extras, ArsenalRules.Autocannon, chaos: false))!;
            Assert.NotSame(chaos, loyal);
            // The Havoc's: the twin's own purple flash, tracer, impact and smoke.
            Assert.Equal(Arsenal.ChaosMuzzleEffect, chaos.Muzzle!.Name);
            Assert.Equal(Arsenal.ChaosTracerEffect, chaos.Tracer!.Name);
            Assert.Equal([Arsenal.ChaosImpactEffect], c.Resolve(chaos.HitMaterial, ShotHit.World, 3, true).Select(p => p.Name));
            Assert.Equal(["chaos_autocannon_smoke"], chaos.StopFire.Select(p => p.Name));
            // The Devastator's: the loyalist flash, tracer and impact on the same commands (markers, scales), the rest as the twin's.
            Assert.Equal(Arsenal.LoyalistMuzzleEffect, loyal.Muzzle!.Name);
            Assert.Equal(Arsenal.LoyalistTracerEffect, loyal.Tracer!.Name);
            Assert.Equal([Arsenal.LoyalistImpactEffect], c.Resolve(loyal.HitMaterial, ShotHit.World, 3, true).Select(p => p.Name));
            Assert.Equal(chaos.Muzzle with { Name = Arsenal.LoyalistMuzzleEffect }, loyal.Muzzle);
            Assert.Equal(chaos.ShellEject, loyal.ShellEject);
            Assert.Equal(chaos.StopFire, loyal.StopFire);
        }
    }
}
