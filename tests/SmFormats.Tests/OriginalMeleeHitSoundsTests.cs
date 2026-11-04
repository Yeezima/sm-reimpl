using SmContent;
using SmFormats.Baf;

namespace SmFormats.Tests;

/// <summary>
/// sound/original-hits: the melee hit sound per (wargear, ability, rank) the data gives (<see cref="SoundTriggers.MeleeHitEvents"/>), the strike-to-ability map
/// (<see cref="MeleeTuning.WeaponMap.AbilityOf"/>) and the default of the shooter's hit confirm. Synthetic bindings shaped like the install's (names invented where they would be the game's).
/// </summary>
public class OriginalMeleeHitSoundsTests
{
    const string Hits = "material_effect_container\\weapon_hits\\space_marines\\melee_weapons\\";

    static FxBinding Hit(string ability, string slot, string container, string part = "hit_fx", params string[] upgrades) =>
        new($"ability_data/ability_definitions/{ability}/{slot}/{part}/actions/trigger_material_effect", part, ability, upgrades, null, new FxMaterialTrigger(Hits + container, "hit_material"));

    static SoundTriggers Triggers(IEnumerable<FxBinding> fx, string wargear, Dictionary<string, string> sounds, Dictionary<string, IReadOnlyList<string>>? choices = null)
    {
        var table = new Dictionary<(string, string, bool), List<string>>();
        foreach (var (ability, secondary, container) in SoundTriggers.MeleeAbilityHits(fx))
        {
            if (!table.TryGetValue((wargear, ability, secondary), out var list)) table[(wargear, ability, secondary)] = list = [];
            list.Add(container);
        }
        return new SoundTriggers
        {
            Files = new Dictionary<string, SoundFile>(), ContainerSounds = new Dictionary<string, string>(sounds, StringComparer.OrdinalIgnoreCase),
            ClipTags = new Dictionary<string, IReadOnlyList<(string Tag, float At)>>(), Notes = [],
            MeleeAbilityContainers = table.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value),
            ContainerEventChoices = new Dictionary<string, IReadOnlyList<string>>(choices ?? [], StringComparer.OrdinalIgnoreCase),
        };
    }

    static readonly Dictionary<string, string> Sounds = new()
    {
        ["chainsword"] = "play_t_chain_hit", ["kick"] = "play_t_kick_hit", ["fist"] = "play_t_fist_hit", ["power_sword"] = "play_t_sword_hit", ["hammer"] = "play_t_hammer_hit",
    };

    [Fact]
    public void A_swings_sound_is_the_event_of_the_container_its_ability_names_by_rank()
    {
        var fx = new[]
        {
            Hit("ability_melee_x1", "primary_target_actions", "chainsword"),
            Hit("ability_melee_x1", "secondary_target_actions", "fist"),
            Hit("ability_melee_y", "primary_target_actions", "kick"),
            Hit("ability_melee_y", "secondary_target_actions", "fist"),
        };
        var t = Triggers(fx, "sm_mp_melee_chainsword", Sounds);
        Assert.Equal(["play_t_chain_hit"], t.MeleeHitEvents("sm_mp_melee_chainsword", "ability_melee_x1", secondary: false));
        Assert.Equal(["play_t_fist_hit"], t.MeleeHitEvents("sm_mp_melee_chainsword", "ability_melee_x1", secondary: true));
        Assert.Equal(["play_t_kick_hit"], t.MeleeHitEvents("sm_mp_melee_chainsword", "ability_melee_y", secondary: false));
        // light and heavy differ, an unknown ability, wargear or none at all give nothing
        Assert.Empty(t.MeleeHitEvents("sm_mp_melee_chainsword", "ability_melee_x9", secondary: false));
        Assert.Empty(t.MeleeHitEvents("sm_mp_melee_power_axe", "ability_melee_x1", secondary: false));
        Assert.Empty(t.MeleeHitEvents(null, "ability_melee_x1", secondary: false));
        Assert.Empty(t.MeleeHitEvents("sm_mp_melee_chainsword", null, secondary: false));
    }

    [Fact]
    public void The_combat_knifes_abilities_and_hit_sim_containers_count_and_a_second_container_adds_its_event()
    {
        // ability_melee_ck* (the old light-only lookup missed them), a primary container under hit_sim (the thunder hammer's), and ck3's two containers.
        var fx = new[]
        {
            Hit("ability_melee_ck1", "primary_target_actions", "fist"),
            Hit("ability_melee_ck3", "primary_target_actions", "fist"),
            Hit("ability_melee_ck3", "primary_target_actions", "chainsword"),
            Hit("ability_melee_x1", "primary_target_actions", "hammer", "hit_sim"),
        };
        var t = Triggers(fx, "wargear", Sounds);
        Assert.Equal(["play_t_fist_hit"], t.MeleeHitEvents("wargear", "ability_melee_ck1", false));
        Assert.Equal(["play_t_fist_hit", "play_t_chain_hit"], t.MeleeHitEvents("wargear", "ability_melee_ck3", false));
        Assert.Equal(["play_t_hammer_hit"], t.MeleeHitEvents("wargear", "ability_melee_x1", false));
    }

    static SoundBinding Direct(string ability, string slot, string evt, float delayMs = 0f, params string[] upgrades) =>
        new($"ability_data/ability_definitions/{ability}/{slot}/hit_audio/actions/post_sound_event", "hit_audio", ability, "", upgrades, evt, "", false, false, false, delayMs, "self", new Dictionary<string, float>());

    [Fact]
    public void An_abilitys_own_sound_command_in_its_target_actions_is_a_hit_sound_too()
    {
        // The combat knife: a container (hit_sim) and a hit_audio post_sound_event in the primary actions, a container in the secondary.
        var fx = new[] { Hit("ability_melee_ck1", "primary_target_actions", "fist", "hit_sim"), Hit("ability_melee_ck1", "secondary_target_actions", "kick") };
        var t = Triggers(fx, "sm_mp_melee_combat_knife", Sounds);
        var file = new SoundFile("sm_mp_melee_combat_knife", [
            Direct("ability_melee_ck1", "primary_target_actions", "play_t_direct"),
            Direct("ability_melee_ck1", "primary_target_actions", "play_t_later", delayMs: 250f),                // delayed: not at the moment of the hit
            Direct("ability_melee_ck1", "primary_target_actions", "play_t_perk", 0f, "mp_perk_x"),               // upgraded entry
            Direct("ability_melee_ck1", "primary_target_actions", "play_or_direct"),                              // the Orks' event
            Direct("ability_melee_ck2", "primary_target_actions", "play_t_other"),
            Direct("ability_melee_ck1", "world_hit_actions", "play_t_world"),
        ]);
        t = new SoundTriggers
        {
            Files = new Dictionary<string, SoundFile>(StringComparer.OrdinalIgnoreCase) { [file.Name] = file }, ContainerSounds = t.ContainerSounds, ClipTags = t.ClipTags, Notes = [],
            MeleeAbilityContainers = t.MeleeAbilityContainers, ContainerEventChoices = t.ContainerEventChoices,
        };
        Assert.Equal(["play_t_fist_hit", "play_t_direct"], t.MeleeHitEvents("sm_mp_melee_combat_knife", "ability_melee_ck1", false));
        Assert.Equal(["play_t_kick_hit"], t.MeleeHitEvents("sm_mp_melee_combat_knife", "ability_melee_ck1", true));
        Assert.Equal(["play_t_other"], t.MeleeHitEvents("sm_mp_melee_combat_knife", "ability_melee_ck2", false));
    }

    [Fact]
    public void Upgraded_entries_world_hits_and_swings_are_not_hit_sounds()
    {
        var fx = new[]
        {
            Hit("ability_melee_x1", "primary_target_actions", "hammer", "hit_fx", "mp_perk_x"),
            Hit("ability_melee_x1", "world_hit_actions", "kick"),
            Hit("ability_melee_x1", "start_actions", "kick"),
            new FxBinding("x/primary_target_actions/hit_fx/actions/trigger_material_effect", "hit_fx", "", [], null, new FxMaterialTrigger(Hits + "fist", "m")),   // outside an ability
        };
        Assert.Empty(SoundTriggers.MeleeAbilityHits(fx));
    }

    [Fact]
    public void A_marine_never_takes_the_orks_same_named_file_and_an_ork_does()
    {
        var fx = new[] { Hit("ability_melee_x1", "primary_target_actions", "chainsword") };
        // The archive keeps base names: the Orks' file comes first and holds a play_or_ event.
        var choices = new Dictionary<string, IReadOnlyList<string>> { ["chainsword"] = ["play_or_choppa_hit", "play_t_chain_hit"] };
        var marine = Triggers(fx, "sm_mp_melee_chainsword", new Dictionary<string, string> { ["chainsword"] = "play_or_choppa_hit" }, choices);
        Assert.Equal(["play_t_chain_hit"], marine.MeleeHitEvents("sm_mp_melee_chainsword", "ability_melee_x1", false));
        var ork = Triggers(fx, "ork_coop_boy_choppa", new Dictionary<string, string> { ["chainsword"] = "play_or_choppa_hit" }, choices);
        Assert.Equal(["play_or_choppa_hit"], ork.MeleeHitEvents("ork_coop_boy_choppa", "ability_melee_x1", false));
        // Only an Ork event in the data: a marine gets nothing (no borrowed sound).
        var only = Triggers(fx, "sm_mp_melee_chainsword", new Dictionary<string, string> { ["chainsword"] = "play_or_choppa_hit" }, new Dictionary<string, IReadOnlyList<string>> { ["chainsword"] = ["play_or_choppa_hit"] });
        Assert.Empty(only.MeleeHitEvents("sm_mp_melee_chainsword", "ability_melee_x1", false));
        // Without the per-file list (a Build from documents) the single known event is checked the same way.
        Assert.Empty(Triggers(fx, "sm_mp_melee_chainsword", new Dictionary<string, string> { ["chainsword"] = "play_or_choppa_hit" }).MeleeHitEvents("sm_mp_melee_chainsword", "ability_melee_x1", false));
    }

    [Fact]
    public void A_strike_maps_to_its_ability_from_the_same_lists_the_simulation_builds_its_strikes_from()
    {
        var sword = MeleeTuning.PowerSword;
        Assert.Equal("ability_melee_x1", sword.AbilityOf(heavy: false, 1));
        Assert.Equal("ability_melee_x4", sword.AbilityOf(false, 4));
        Assert.Equal("ability_melee_y", sword.AbilityOf(true, 1));
        Assert.Equal("ability_melee_y4", sword.AbilityOf(true, 4));
        Assert.Equal("", sword.AbilityOf(true, 5));   // the Y,Y finisher is a blast strike without an ability
        Assert.Equal("", sword.AbilityOf(false, 0));
        Assert.Equal("", sword.AbilityOf(false, 9));
        var hammer = MeleeTuning.ThunderHammer;
        Assert.Equal("ability_melee_y3", hammer.AbilityOf(true, 3));
        Assert.Equal("ability_displace_slam", hammer.AbilityOf(true, 4));   // the slams
        Assert.Equal("ability_displace_slam", hammer.AbilityOf(true, 5));
        var knife = MeleeTuning.CombatKnife;
        Assert.Equal("ability_melee_ck3", knife.AbilityOf(false, 3));
        Assert.Equal("", knife.AbilityOf(true, 4));   // CK Y4, the foot stomp
        Assert.Equal("ability_displace_kick", MeleeTuning.Displace.AbilityOf(false, 1));
        Assert.Equal("ability_displace_slam", MeleeTuning.Displace.AbilityOf(true, 1));
    }

    [Fact]
    public void The_shooters_hit_confirm_defaults_to_the_data_event()
    {
        Assert.Equal(HitConfirmMode.Data, ImpactSounds.DefaultHitConfirm);
        Assert.NotEqual(HitConfirmMode.Soft, ImpactSounds.DefaultHitConfirm);
    }

    /// <summary>The data these rules rest on, read from the install (skipped without one): the real wargear files' hit events per ability; no original bytes are kept.</summary>
    [MeleeInstallPinTests.InstallFact]
    public void The_installs_wargear_gives_the_hit_events_described()
    {
        string game = GameInstall.TryLocate()!.Dir;
        string[] wargear = ["sm_mp_melee_combat_knife", "sm_mp_melee_chainsword", "sm_mp_melee_powersword", "sm_mp_melee_power_axe", "csm_mp_melee_chain_axe", "sm_mp_melee_2hand_thunder_hammer", "csm_mp_melee_2hand_power_mace"];
        var t = SoundTriggers.Load(game, wargear, [], (IReadOnlyList<SmFormats.Havok.BehaviorGraph>?)null);
        Assert.Contains("play_sm_fist_hit", t.MeleeHitEvents("sm_mp_melee_combat_knife", "ability_melee_ck1", false));   // its hit_audio command
        Assert.Contains("play_sm_fist_hit", t.MeleeHitEvents("sm_mp_melee_combat_knife", "ability_melee_ck1", true));    // the further targets
        Assert.Contains("play_sm_chain_sword_hit", t.MeleeHitEvents("sm_mp_melee_chainsword", "ability_melee_x1", false));
        Assert.Contains("play_sm_kick_hit", t.MeleeHitEvents("sm_mp_melee_chainsword", "ability_melee_y", false));
        Assert.Contains("play_sm_power_sword_hit", t.MeleeHitEvents("sm_mp_melee_powersword", "ability_melee_x1", false));
        Assert.Contains("play_sm_thunder_hammer_hit", t.MeleeHitEvents("sm_mp_melee_2hand_thunder_hammer", "ability_melee_x1", false));
        Assert.All(wargear, w => Assert.DoesNotContain(t.MeleeAbilityContainers.Keys.Where(k => k.Wargear == w).SelectMany(k => t.MeleeHitEvents(w, k.Ability, k.Secondary)), e => e.StartsWith("play_or_")));
    }
}
