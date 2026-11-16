using System.Reflection;
using SmContent;
using SmFormats.Baf;
using SmSim;

namespace SmFormats.Tests;

/// <summary>
/// W0b: <see cref="EffectCatalogue.WithSources"/> and <see cref="SoundTriggers.WithSources"/>, the core half of "presentation by source" (a weapon only added content carries shows and sounds as
/// install files). Without sources both give the very same object, so a Classic or faithful match reads and keeps its catalogues exactly as before. Synthetic names and files only.
/// </summary>
public partial class EffectCatalogueTests
{
    static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> NoSources = new Dictionary<string, IReadOnlyList<string>>();

    [Fact]
    public void Without_sources_the_effect_catalogue_is_the_same_object()
    {
        var c = Catalogue();
        Assert.Same(c, c.WithSources(NoSources));
        Assert.Same(c, c.WithSources(new Dictionary<string, IReadOnlyList<string>> { ["ours_gun"] = ["not_read", "nor_this"] }));   // no candidate has effects
    }

    [Fact]
    public void A_sourced_name_shows_the_first_candidate_with_effects_and_the_rest_is_kept()
    {
        var c = Catalogue();
        var sourced = c.WithSources(new Dictionary<string, IReadOnlyList<string>> { ["OURS_GUN"] = ["not_read", "csm_test_rifle", "test_rifle"] });
        Assert.NotSame(c, sourced);
        Assert.Same(c.Weapon("csm_test_rifle"), sourced.Weapon("ours_gun"));   // the first candidate read, matched ignoring case; it keeps its own file name
        Assert.Equal("csm_test_rifle", sourced.Weapon("ours_gun")!.Wargear);
        Assert.Null(c.Weapon("ours_gun"));                                     // the original is untouched
        Assert.Same(c.Weapon("test_rifle"), sourced.Weapon("test_rifle"));
        Assert.Contains(sourced.Notes, n => n.Contains("ours_gun: the effects of csm_test_rifle", StringComparison.OrdinalIgnoreCase));   // the note names the key as given
        // Every other part is the same object (a property added later and not copied fails here).
        foreach (var p in typeof(EffectCatalogue).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.Name is not (nameof(EffectCatalogue.Weapons) or nameof(EffectCatalogue.Notes))))
            Assert.True(ReferenceEquals(p.GetValue(c), p.GetValue(sourced)) || Equals(p.GetValue(c), p.GetValue(sourced)), $"EffectCatalogue.{p.Name} is not carried over by WithSources");
    }

    // ---- W1c: WithEffectNames (the autocannon's loyalist muzzle flash and tracer in place of its Chaos twin's)

    static Dictionary<string, IReadOnlyDictionary<string, string>> Names(string wargear, params (string From, string To)[] pairs) =>
        new(StringComparer.OrdinalIgnoreCase) { [wargear] = pairs.ToDictionary(p => p.From, p => p.To, StringComparer.OrdinalIgnoreCase) };

    [Fact]
    public void Without_effect_names_or_with_none_played_the_catalogue_is_the_same_object()
    {
        var c = Catalogue();
        Assert.Same(c, c.WithEffectNames(new Dictionary<string, IReadOnlyDictionary<string, string>>()));
        Assert.Same(c, c.WithEffectNames(Names("test_rifle", ("not_played", "other"))));            // no command plays the name
        Assert.Same(c, c.WithEffectNames(Names("not_a_weapon", ("test_muzzle", "other"))));         // no such entry
        Assert.Same(c, c.WithEffectNames(Names("test_rifle", ("test_muzzle", "test_muzzle"))));     // renamed to itself
    }

    [Fact]
    public void Named_effects_of_the_named_entry_play_other_effects_and_everything_else_is_kept()
    {
        var c = Catalogue();
        var sourced = c.WithSources(new Dictionary<string, IReadOnlyList<string>> { ["ours_gun"] = ["csm_test_rifle"] });
        var named = sourced.WithEffectNames(Names("OURS_GUN", ("test_muzzle", "loyal_muzzle"), ("TEST_TRACER", "loyal_tracer"), ("test_trail", "loyal_trail")));
        Assert.NotSame(sourced, named);
        var twin = sourced.Weapon("csm_test_rifle")!;
        var ours = named.Weapon("ours_gun")!;
        // The basic shot's flash and tracer are the new effects with the source's marker, scale, target and attachment; the casing, the smoke and the impacts are the source's.
        Assert.Equal("loyal_muzzle", ours.Muzzle!.Name);
        Assert.Equal(twin.Muzzle! with { Name = "loyal_muzzle" }, ours.Muzzle);
        Assert.Equal("loyal_tracer", ours.Tracer!.Name);
        Assert.Equal(twin.Tracer! with { Name = "loyal_tracer" }, ours.Tracer);
        Assert.Equal(twin.ShellEject, ours.ShellEject);
        Assert.Equal(twin.StopFire, ours.StopFire);
        Assert.Equal(twin.HitMaterial, ours.HitMaterial);
        Assert.Equal(["test_led", "shell_casing", "test_perk_muzzle", "loyal_muzzle", "test_perk_trail", "", "loyal_tracer", "", "test_smoke", "test_charging", "test_heavy_muzzle"],
            ours.Bindings.Select(b => b.Play?.Name ?? ""));   // an upgrade's own flash (test_perk_muzzle) is another name: kept
        Assert.Equal("loyal_trail", ours.Projectiles.Single().Trail.Single().Name);   // the projectiles' commands too
        Assert.Equal("csm_test_rifle", ours.Wargear);                                // still the file the commands came from
        // Only the named entry changes: the source file and the class weapon show their own.
        Assert.Same(twin, named.Weapon("csm_test_rifle"));
        Assert.Equal("test_muzzle", named.Weapon("csm_test_rifle")!.Muzzle!.Name);
        Assert.Same(sourced.Weapon("test_rifle"), named.Weapon("test_rifle"));
        Assert.Contains(named.Notes, n => n.StartsWith("OURS_GUN: 3 effect command(s) renamed", StringComparison.Ordinal));
        foreach (var p in typeof(EffectCatalogue).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.Name is not (nameof(EffectCatalogue.Weapons) or nameof(EffectCatalogue.Notes))))
            Assert.True(ReferenceEquals(p.GetValue(sourced), p.GetValue(named)) || Equals(p.GetValue(sourced), p.GetValue(named)), $"EffectCatalogue.{p.Name} is not carried over by WithEffectNames");
    }

    [Fact]
    public void A_named_impact_effect_plays_from_the_entrys_own_copy_of_the_shared_container()
    {
        // W1c "Loyalist impact": the twin's container is shared (the Chaos file names it too), so the named entry's impacts come from a copy of its own; every other weapon's are untouched.
        var c = Catalogue();
        var sourced = c.WithSources(new Dictionary<string, IReadOnlyList<string>> { ["ours_gun"] = ["csm_test_rifle"] });
        var named = sourced.WithEffectNames(Names("ours_gun", ("chaos_test_dmg_stone", "test_dmg_stone"), ("chaos_test_sparks", "loyal_sparks")));
        Assert.NotSame(sourced, named);
        var twin = sourced.Weapon("csm_test_rifle")!;
        var ours = named.Weapon("ours_gun")!;
        // The commands name the copy (the file name still last), and the copy plays the new names with the source command's scale and direction; the rest of the table and its sound as read.
        Assert.Equal(EffectCatalogue.CopyKey("ours_gun", ChaosContainer), ours.HitMaterial!.Container);
        Assert.Equal("test_rifle", ours.HitMaterial.ContainerName);
        Assert.Equal(twin.HitMaterial! with { Container = EffectCatalogue.CopyKey("ours_gun", ChaosContainer) }, ours.HitMaterial);
        var stone = sourced.Impact("csm_test_rifle", ShotHit.World, 3).Single();
        Assert.Equal([stone with { Name = "test_dmg_stone" }], named.Impact("ours_gun", ShotHit.World, 3));            // stone falls back to default/default
        Assert.Equal(["chaos_test_dmg_metal", "loyal_sparks"], named.Impact("ours_gun", ShotHit.World, 4).Select(p => p.Name));   // only the named one of a surface's two
        Assert.Equal(["chaos_test_dmg_body"], named.Impact("ours_gun", ShotHit.Body, -1).Select(p => p.Name));         // a surface of its own, not named: kept
        Assert.Equal(["chaos_test_dmg_head"], named.Impact("ours_gun", ShotHit.Head, -1).Select(p => p.Name));
        Assert.Equal("play_test_hits", named.Container(ours.HitMaterial)!.SoundEvent);
        Assert.Equal(twin.Muzzle, ours.Muzzle);                                         // the commands not named are kept
        Assert.Equal(twin.Tracer, ours.Tracer);
        // The shared tables and every other weapon's impacts are as read: the twin, the class weapon, the projectile's container.
        Assert.Same(twin, named.Weapon("csm_test_rifle"));
        Assert.Equal(["chaos_test_dmg_stone"], named.Impact("csm_test_rifle", ShotHit.World, 3).Select(p => p.Name));
        Assert.Equal(["test_dmg_metal", "test_sparks"], named.Impact("test_rifle", ShotHit.World, 4).Select(p => p.Name));
        foreach (var (key, table) in sourced.Containers) Assert.Same(table, named.Containers[key]);
        Assert.Equal([EffectCatalogue.CopyKey("ours_gun", ChaosContainer)], named.Containers.Keys.Except(sourced.Containers.Keys));
        Assert.Contains(named.Notes, n => n.StartsWith("ours_gun: 0 effect command(s) renamed, 2 impact effect(s) in its own copy of ours_gun#", StringComparison.Ordinal));
        foreach (var p in typeof(EffectCatalogue).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.Name is not (nameof(EffectCatalogue.Weapons) or nameof(EffectCatalogue.Containers) or nameof(EffectCatalogue.Notes))))
            Assert.True(ReferenceEquals(p.GetValue(sourced), p.GetValue(named)) || Equals(p.GetValue(sourced), p.GetValue(named)), $"EffectCatalogue.{p.Name} is not carried over by WithEffectNames");
        // A projectile's container is copied the same way (the bolt's detonation), each container once however many commands name it.
        var blasted = sourced.WithEffectNames(Names("ours_gun", ("test_dmg_stone", "loyal_blast_hit"), ("chaos_test_dmg_stone", "loyal_hit")));
        var bolt = blasted.ProjectileFor("ours_gun", "test_bolt")!;
        Assert.Equal(["loyal_blast_hit"], blasted.Resolve(bolt.DetonationMaterial, ShotHit.World, 2).Select(p => p.Name));
        Assert.Equal(["test_dmg_stone"], blasted.Resolve(sourced.ProjectileFor("csm_test_rifle", "test_bolt")!.DetonationMaterial, ShotHit.World, 2).Select(p => p.Name));
        Assert.Equal(2, blasted.Containers.Count - sourced.Containers.Count);
    }
}

public class SoundSourcesTests
{
    static BafDocument Sounding(string evt) => BafDocument.Read(SyntheticBafWriter.Build(
        ("fire_audio", new (string, object)[] { ("commands", new (string, object)[] { ("post_sound_event", new (string, object)[] { ("$ref", "action_commands\\post_sound_event"), ("start_event_name", evt) }) }) })));

    static BafDocument Silent() => BafDocument.Read(SyntheticBafWriter.Build(("common_data", new (string, object)[] { ("slot", "ranged") })));

    static Dictionary<string, string> D(params (string Key, string Value)[] pairs) => pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);

    static SoundTriggers Triggers()
    {
        var built = SoundTriggers.Build(new Dictionary<string, BafDocument> { ["src_sword"] = Sounding("play_sword_swing"), ["src_twin"] = Sounding("play_twin_fire"), ["src_silent"] = Silent() },
            new Dictionary<string, string>(), null);
        return new SoundTriggers
        {
            Files = built.Files, ContainerSounds = built.ContainerSounds, ClipTags = built.ClipTags, Notes = built.Notes,
            HitContainers = D(("src_twin", "c_twin_hit")), ShotHitContainers = D(("src_twin", "c_twin_shot")),
            MeleeHitContainers = D(("src_sword", "c_sword"), ("ours_claw", "c_stale")), ChargeHitContainers = D(("src_sword", "c_body_smash")),
            MeleeAbilityContainers = new Dictionary<(string, string, bool), IReadOnlyList<string>>(SoundTriggers.AbilityKeyComparer.Instance)
            {
                [("src_sword", "ability_melee_x1", false)] = ["c_sword"], [("src_sword", "ability_melee_x1", true)] = ["c_sword_2nd"], [("ours_claw", "ability_old", false)] = ["c_stale"],
            },
        };
    }

    [Fact]
    public void Without_sources_the_triggers_are_the_same_object()
    {
        var t = Triggers();
        Assert.Same(t, t.WithSources(new Dictionary<string, IReadOnlyList<string>>()));
        Assert.Same(t, t.WithSources(new Dictionary<string, IReadOnlyList<string>> { ["ours_claw"] = ["src_silent", "not_read"] }));   // nothing read with sounds
    }

    [Fact]
    public void A_sourced_name_sounds_as_the_first_candidate_read_with_sounds()
    {
        var t = Triggers();
        var s = t.WithSources(new Dictionary<string, IReadOnlyList<string>> { ["ours_claw"] = ["src_sword"], ["ours_cannon"] = ["src_silent", "not_read", "src_twin"] });
        Assert.Same(t.File("src_sword"), s.File("ours_claw"));
        Assert.Same(t.File("src_twin"), s.File("OURS_CANNON"));                  // the silent file and the unread one are passed over
        Assert.Null(t.File("ours_claw"));                                        // the original is untouched
        Assert.Equal("c_sword", s.MeleeHitContainers["ours_claw"]);              // the name's own (stale) entry replaced
        Assert.Equal("c_body_smash", s.ChargeHitContainers["ours_claw"]);
        Assert.Equal("c_twin_hit", s.HitContainers["ours_cannon"]);
        Assert.Equal("c_twin_shot", s.ShotHitContainers["ours_cannon"]);
        Assert.False(s.MeleeHitContainers.ContainsKey("ours_cannon"));           // the source has none: none
        Assert.Equal(["c_sword"], s.MeleeAbilityContainers[("ours_claw", "ability_melee_x1", false)]);
        Assert.Equal(["c_sword_2nd"], s.MeleeAbilityContainers[("ours_claw", "ability_melee_x1", true)]);
        Assert.False(s.MeleeAbilityContainers.ContainsKey(("ours_claw", "ability_old", false)));
        Assert.Equal(["c_sword"], s.MeleeAbilityContainers[("src_sword", "ability_melee_x1", false)]);
        Assert.Equal(["play_sword_swing"], s.File("ours_claw")!.Bindings.Select(b => b.StartEvent));
        Assert.Contains(s.Notes, n => n.Contains("ours_cannon: the sounds of src_twin", StringComparison.Ordinal));
        string[] copied = [nameof(SoundTriggers.Files), nameof(SoundTriggers.HitContainers), nameof(SoundTriggers.ShotHitContainers), nameof(SoundTriggers.MeleeHitContainers),
            nameof(SoundTriggers.ChargeHitContainers), nameof(SoundTriggers.MeleeAbilityContainers), nameof(SoundTriggers.Notes)];
        foreach (var p in typeof(SoundTriggers).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => !copied.Contains(p.Name)))
            Assert.True(ReferenceEquals(p.GetValue(t), p.GetValue(s)), $"SoundTriggers.{p.Name} is not carried over by WithSources");
    }
}
