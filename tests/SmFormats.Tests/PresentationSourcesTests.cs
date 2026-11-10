using System.Reflection;
using SmContent;
using SmFormats.Baf;

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
