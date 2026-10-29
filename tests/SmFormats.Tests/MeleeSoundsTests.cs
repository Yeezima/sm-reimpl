using SmContent;

namespace SmFormats.Tests;

/// <summary>
/// fx/melee-presentation: the melee weapons' hum (<see cref="MeleeIdleSounds"/>) and charge impact (<see cref="SoundTriggers.ChargeHitContainer"/>) rules, on synthetic bindings
/// shaped like the install's (names invented where they would be the game's).
/// </summary>
public class MeleeSoundsTests
{
    static SoundBinding Sound(string trigger, string start, string stop = "") =>
        new($"x/{trigger}/commands/post_sound_event", trigger, "", "", [], start, stop, false, false, false, 0f, "", new Dictionary<string, float>());

    static SoundFile File(string name, params SoundBinding[] b) => new(name, b);

    static readonly HashSet<string> Banks = ["play_t_axe_idle", "stop_t_axe_idle", "play_t_hammer_idle", "stop_t_hammer_idle", "play_t_deploy"];

    [Fact]
    public void A_deploy_hum_that_resolves_starts_and_its_stow_twin_stops_it()
    {
        var hammer = File("hammer", Sound("while_deployed_actions", "play_t_hammer_idle", "stop_t_hammer_idle"), Sound("on_deploy_actions", "play_t_deploy"),
            Sound("on_deploy_actions", "play_t_hammer_idle"), Sound("on_stow_actions", "stop_t_hammer_idle"));
        var hum = MeleeIdleSounds.Own(hammer, Banks.Contains);
        Assert.Equal(new MeleeIdleSounds.Hum("play_t_hammer_idle", "stop_t_hammer_idle", 0f, false), hum);
    }

    [Fact]
    public void An_event_in_no_bank_is_not_played_and_a_file_with_none_has_no_hum()
    {
        // The chain axe's only hum names an event no bank holds: not reproducible.
        var chain = File("chain", Sound("while_deployed_actions", "play_t_sword_idle", "stop_t_sword_idle"), Sound("on_deploy_actions", "play_t_deploy"));
        Assert.Null(MeleeIdleSounds.Own(chain, Banks.Contains));
        Assert.Null(MeleeIdleSounds.Own(null, Banks.Contains));
        // The deploy command alone is not a hum.
        Assert.Null(MeleeIdleSounds.Own(File("plain", Sound("on_deploy_actions", "play_t_deploy")), Banks.Contains));
        // A hum with no stop twin in the banks starts and has no stop.
        var noStop = MeleeIdleSounds.Own(File("axe", Sound("on_deploy_actions", "play_t_axe_idle")), e => e == "play_t_axe_idle");
        Assert.Equal("", noStop!.Stop);
    }

    [Fact]
    public void A_power_sword_hums_quietly_with_its_own_or_the_axes_hum_and_the_substitute_is_marked()
    {
        var files = new Dictionary<string, SoundFile>(StringComparer.OrdinalIgnoreCase)
        {
            ["sm_mp_melee_powersword"] = File("sm_mp_melee_powersword", Sound("on_deploy_actions", "play_t_axe_idle"), Sound("on_stow_actions", "stop_t_axe_idle")),
            ["csm_mp_melee_powersword"] = File("csm_mp_melee_powersword", Sound("on_deploy_actions", "play_t_deploy")),
            [MeleeIdleSounds.SwordSubstituteFile] = File(MeleeIdleSounds.SwordSubstituteFile, Sound("on_deploy_actions", "play_t_axe_idle"), Sound("on_stow_actions", "stop_t_axe_idle")),
        };
        SoundFile? Of(string n) => files.GetValueOrDefault(n);
        var space = MeleeIdleSounds.For("sm_mp_melee_powersword", Of, Banks.Contains)!;
        Assert.Equal(("play_t_axe_idle", "stop_t_axe_idle", MeleeIdleSounds.SwordGainDb, false), (space.Start, space.Stop, space.GainDb, space.Substitute));
        var chaos = MeleeIdleSounds.For("csm_mp_melee_powersword", Of, Banks.Contains)!;
        Assert.Equal(("play_t_axe_idle", MeleeIdleSounds.SwordGainDb, true), (chaos.Start, chaos.GainDb, chaos.Substitute));
        Assert.True(MeleeIdleSounds.SwordGainDb < 0f);
        // Another weapon never borrows: no file, no substitute.
        Assert.Null(MeleeIdleSounds.For("csm_mp_melee_chain_axe", Of, Banks.Contains));
        Assert.Null(MeleeIdleSounds.For(null, Of, Banks.Contains));
        // Without the axe's hum in the banks the Chaos sword has none.
        Assert.Null(MeleeIdleSounds.For("csm_mp_melee_powersword", Of, _ => false));
    }

    static FxBinding Hit(string path, string ability, string container, params string[] upgrades) =>
        new(path, "", ability, upgrades, null, new FxMaterialTrigger(container, "hit_material"));

    [Fact]
    public void The_charge_impact_is_the_primary_hit_container_of_the_bull_rush_or_the_dash_attack_not_the_upgraded_one_or_a_swing()
    {
        string smash = "material_effect_container\\weapon_hits\\space_marines\\melee_weapons\\body_smash";
        var rush = new[]
        {
            Hit("ability_data/ability_definitions/ability_melee_x1/primary_target_actions/hit_fx/actions/trigger_material_effect", "ability_melee_x1", "x\\power_sword"),
            Hit("ability_data/ability_definitions/ability_bullrush_01/secondary_target_actions/hit_fx/actions/trigger_material_effect", "ability_bullrush_01", "x\\kick"),
            Hit("ability_data/ability_definitions/ability_bullrush_01/primary_target_actions/hit_sim/actions/trigger_material_effect", "ability_bullrush_01", "x\\upgraded", "mp_perk_improved_bullrush"),
            Hit("ability_data/ability_definitions/ability_bullrush_01/primary_target_actions/hit_sim/actions/trigger_material_effect", "ability_bullrush_01", smash),
        };
        Assert.Equal(smash, SoundTriggers.ChargeHitContainer(rush));
        var dash = new[] { Hit("ability_data/ability_definitions/ability_dash_attack/primary_target_actions/hit_sim/actions/trigger_material_effect", "ability_dash_attack", smash) };
        Assert.Equal(smash, SoundTriggers.ChargeHitContainer(dash));
        Assert.Null(SoundTriggers.ChargeHitContainer(rush.Take(2)));
        Assert.Null(SoundTriggers.ChargeHitContainer([]));
    }

    [Fact]
    public void A_charge_hit_sound_is_the_containers_sfx_event_by_file()
    {
        var sounds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["body_smash"] = "play_t_smash" };
        var triggers = new SoundTriggers
        {
            Files = new Dictionary<string, SoundFile>(), ContainerSounds = sounds, ClipTags = new Dictionary<string, IReadOnlyList<(string Tag, float At)>>(), Notes = [],
            ChargeHitContainers = new Dictionary<string, string> { ["sm_mp_melee_powersword"] = "material_effect_container\\weapon_hits\\space_marines\\melee_weapons\\body_smash" },
        };
        Assert.Equal("play_t_smash", triggers.ChargeHitSound("sm_mp_melee_powersword"));
        Assert.Null(triggers.ChargeHitSound("sm_mp_melee_combat_knife"));
        Assert.Null(triggers.ChargeHitSound(null));
    }

    /// <summary>The data these rules rest on, read from the install (skipped without one): the hum events and the charge container of the real wargear files. No original bytes are kept.</summary>
    [MeleeInstallPinTests.InstallFact]
    public void The_installs_wargear_gives_the_hums_and_the_charge_impact_described()
    {
        string game = GameInstall.TryLocate()!.Dir;
        var docs = new Dictionary<string, SmFormats.Baf.BafDocument>(StringComparer.OrdinalIgnoreCase);
        string[] wanted = ["sm_mp_melee_powersword", "csm_mp_melee_powersword", "sm_mp_melee_power_axe", "csm_mp_melee_chain_axe", "sm_mp_melee_2hand_thunder_hammer",
            "csm_mp_melee_2hand_power_mace", "sm_mp_abil_jumppack"];
        using (var archive = SmFormats.Obpk.ObpkArchive.Open(Path.Combine(game, GameplayTuning.WargearPackage)))
            archive.ReadEntries(e => archive.GroupName(e) == "attr_pc" && wanted.Contains(e.FileName, StringComparer.OrdinalIgnoreCase), (e, d) => docs.TryAdd(e.FileName, SmFormats.Baf.BafDocument.Read(d)));
        SoundFile? Of(string n) => docs.TryGetValue(n, out var d) ? new SoundFile(n, SoundBinding.Collect(d.Root)) : null;
        // Every event the data names resolves for this check, except the sword event that no bank of the install holds.
        bool Resolves(string e) => e != "play_sm_power_sword_idle" && e != "stop_sm_power_sword_idle";
        Assert.Equal("play_sm_thunder_hammer_idle", MeleeIdleSounds.For("sm_mp_melee_2hand_thunder_hammer", Of, Resolves)?.Start);
        Assert.Equal("play_ch_power_mace_idle", MeleeIdleSounds.For("csm_mp_melee_2hand_power_mace", Of, Resolves)?.Start);
        Assert.Equal("play_sm_power_axe_idle", MeleeIdleSounds.For("sm_mp_melee_power_axe", Of, Resolves)?.Start);
        Assert.Equal("play_sm_power_axe_idle", MeleeIdleSounds.For("sm_mp_melee_powersword", Of, Resolves)?.Start);
        Assert.True(MeleeIdleSounds.For("csm_mp_melee_powersword", Of, Resolves) is { Substitute: true });
        Assert.Null(MeleeIdleSounds.For("csm_mp_melee_chain_axe", Of, Resolves));
        foreach (var name in new[] { "sm_mp_melee_powersword", "sm_mp_abil_jumppack" })
            Assert.EndsWith("body_smash", SoundTriggers.ChargeHitContainer(FxBinding.Collect(docs[name].Root)));
    }
}
