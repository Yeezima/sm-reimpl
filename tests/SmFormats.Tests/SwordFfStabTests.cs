using SmContent;
using SmFormats.Baf;
using SmFormats.Havok;
using SmSim;

namespace SmFormats.Tests;

/// <summary>
/// sim/sword-ff-stab (owner, 2026-11-14): the power sword's F + F finisher is the X,F stab (the heavy list's strike 2: clip, timing, no blast) with the charge's Furious Charge hit
/// (<c>ability_bullrush_01</c> under <c>mp_perk_improved_bullrush</c>: 100 + 50, stagger); the axe and the hammer keep their blasts. Synthetic graphs and wargear, plus a pin on the install
/// (skipped without one).
/// </summary>
public class SwordFfStabTests
{
    static BehaviorTag Branch(string name, float start, float end) => new("Branch", start, end, name, null);
    static BehaviorTag Event(string name, float at) => new("Event", at, at, name, null);
    static BehaviorTag Action(string name, float at) => new("Action", at, at, name, null);

    static (string, object) Damage(float general, float shield = 0f) => ("do_damage", new (string, object)[]
    {
        ("damage_general", general), ("damage_armourshield", shield),
        ("damage_material_multipliers", new (string, object)[] { ("default_body", 1f) }),
        ("armourshield_damage_material_multipliers", new (string, object)[] { ("default_body", 1f) }),
    });

    static (string, object) Trigger(string type) => ("trigger_reaction", new (string, object)[] { ("reaction_by_intensity", new (string, object)[]
    {
        ("entry", new (string, object)[] { ("intensity", 0f), ("reaction_type", type), ("reaction_distance", new (string, object)[] { ("distance_backward", 0f), ("duration", 0f) }) }),
    }) });

    static (string, object) Entry(string? upgrade, params (string, object)[] commands) => ("entry", new (string, object)[]
    {
        ("required_upgrade_name", new (string, object)[] { ("required_list", upgrade is null ? Array.Empty<(string, object)>() : new (string, object)[] { ("upgrade_name", upgrade) }) }),
        ("action", new (string, object)[] { ("commands", commands) }),
    });

    static (string, object)[] Ability(float damage) =>
    [
        ("primary_target_actions", new (string, object)[] { ("hit_sim", new (string, object)[] { ("actions", new (string, object)[] { Damage(damage) }) }) }),
        ("ability_metadata", new (string, object)[] { ("generic_metadata", new (string, object)[] { ("max_range", 4f) }) }),
    ];

    /// <summary>The charge as on the install: 75 + 25 stun by default, 100 + 50 stagger under Furious Charge.</summary>
    static (string, object)[] Bullrush() =>
    [
        ("primary_target_actions", new (string, object)[] { ("hit_sim", new (string, object)[] { ("actions", new (string, object)[]
        {
            ("actions_by_upgrade", new (string, object)[] { ("actions", new (string, object)[]
            {
                Entry(null, Damage(75f, 25f), Trigger("reaction_mp_stun")),
                Entry("mp_perk_improved_bullrush", Damage(100f, 50f), Trigger("reaction_mp_stagger")),
            }) }),
        }) }) }),
        ("ability_metadata", new (string, object)[] { ("generic_metadata", new (string, object)[] { ("max_range", 4f) }) }),
    ];

    static (string, object) Blast(string action, float radius, float damage, float shield) => (action, new (string, object)[] { ("commands", new (string, object)[]
    {
        ("area_of_effect", new (string, object)[]
        {
            ("hit_actions", new (string, object)[] { ("commands", new (string, object)[] { ("do_damage", new (string, object)[] { ("damage_general", damage), ("damage_armourshield", shield) }) }) }),
            ("shape", new (string, object)[] { ("radius", radius) }),
            ("ignore_self", true), ("target_filter", "target_all"),
        }),
    }) });

    static BafDocument Wargear() => BafDocument.Read(SyntheticBafWriter.Build(
        ("ability_data", new (string, object)[] { ("ability_definitions", new (string, object)[]
        {
            ("ability_melee_x1", Ability(65f)), ("ability_melee_y", Ability(25f)), ("ability_bullrush_01", Bullrush()),
        }) }),
        ("action_data", new (string, object)[] { ("triggered_actions", new (string, object)[]
        {
            Blast("displace_quickstomp", 5f, 25f, 25f), Blast("displace_footstomp", 7f, 25f, 50f),
        }) })));

    static BehaviorSequencer Strike(string name, string clip, float hit, params BehaviorTag[] extra) => new(name, [clip],
        [Event("ACTN_Hit_Targets_Multiplayer", hit), Branch("BRANCH_Melee_Combo", 0.4f, 0.6f), Branch("BRANCH_Melee_Run", 0.65f, 1f), .. extra], [1f]);

    static MeleeTuning Read(int stabHeavy)
    {
        var map = new MeleeTuning.WeaponMap("sword", "MP_Sword", ["X1"], ["ability_melee_x1"])
        {
            HeavySequencers = ["Y1", "Y4"], HeavyAbilities = ["ability_melee_y", "ability_melee_y4"], LoopLights = false, LightHeavyNext = [0],
            FinisherSequencers = ["YY"], FinisherAbilities = [""], YNext = [3, 3, 0], StabHeavy = stabHeavy, ExtendedRules = true,
        };
        var graph = new BehaviorGraph("MP_Sword", [],
        [
            Strike("X1", "X_3.5m", 0.2f),
            Strike("Y1", "Y_3.5m", 0.2f, Branch("BRANCH_Melee_Y_Combo", 0.5f, 0.8f)),
            new BehaviorSequencer("Y4", ["Quick_3.5m"], [Action("DISPLACE_QuickStomp", 0.45f), Branch("BRANCH_Melee_Y_Combo", 0.5f, 0.8f), Branch("BRANCH_Melee_Run", 0.7f, 1f)], [1f]),
            new BehaviorSequencer("YY", ["Stomp_3.5m"], [Action("DISPLACE_FootStomp", 0.3f), Branch("BRANCH_Melee_Run", 0.8f, 1f)], [1f]),
        ]);
        return MeleeTuning.From([graph], _ => 2f, Wargear(), map);
    }

    [Fact]
    public void Where_no_stab_is_applied_the_thrust_is_marked_for_an_early_release_and_with_the_stab_it_is_not()
    {
        // sim/stab-release (owner 2026-11-20, "release early too"): Musou Mode and Warfront read inside WithoutSwordStab.
        using (MeleeTuning.WithoutSwordStab())
        {
            var t = Read(2);
            Assert.True(t.Heavy[1].ThrustRelease);
            Assert.False(t.Heavy[2].ThrustRelease);
        }
        var classic = Read(2);
        Assert.False(classic.Heavy[1].ThrustRelease);
        Assert.True(classic.Heavy[3].Stab);   // sim/stab-key-only: the stab is the strike after the finisher
        Assert.False(Read(0).Heavy[1].ThrustRelease);   // a weapon with no stab at all
    }

    [Fact]
    public void The_stab_is_the_x_y_strike_without_its_blast_and_with_the_furious_charge_hit_appended_after_the_stomp_finisher()
    {
        // sim/stab-key-only (owner 2026-11-23): the finisher stays the data's stomp; the stab is appended after it and no chain names it.
        var t = Read(2);
        Assert.Equal(4, t.Heavy.Count);
        var quick = t.Heavy[1];
        var stab = t.Heavy[3];
        Assert.Equal(4, MeleeTuning.StabStepOf(new MeleeTuning.WeaponMap("sword", "MP_Sword", ["X1"], ["ability_melee_x1"]) { HeavySequencers = ["Y1", "Y4"], FinisherSequencers = ["YY"], StabHeavy = 2 }));
        Assert.NotNull(t.Heavy[2].Stomp);                                                     // F + F: the stomp again
        Assert.Equal("Stomp_3.5m", t.HeavyAnimations[2]);
        Assert.False(t.Heavy[2].Stab);
        Assert.All(t.Heavy, h => Assert.NotEqual(4, h.YNext));                                // no Y chain names the stab
        Assert.All(t.Steps, l => Assert.NotEqual(4, l.HeavyNext));                            // nor a light attack's follow-up
        Assert.Equal(t.HeavyAnimations[1], t.HeavyAnimations[3]);                             // the same clip
        Assert.Equal((quick.Duration, quick.HitTime, quick.MoveAt), (stab.Duration, stab.HitTime, stab.MoveAt));   // the same timing
        Assert.Null(stab.Stomp);                                                              // no blast
        Assert.Equal((100f, 50f), (stab.Damage, stab.ShieldDamage));                          // the perk's entry, though no perk is granted
        Assert.Equal((0f, 0f), (stab.SecondaryDamage, stab.SecondaryShieldDamage));
        Assert.Equal(ReactionKind.Stagger, stab.PrimaryReaction.Kind);
        Assert.Equal((0, 0f, 0f), (stab.YNext, stab.YComboOpen, stab.YComboClose));           // it ends the chain
        Assert.NotNull(quick.Stomp);                                                          // the X,F strike keeps its own blast
        Assert.Equal(3, quick.YNext);                                                         // and still chains into the finisher
    }

    [Fact]
    public void Without_a_stab_the_finisher_stays_its_own_stomp()
    {
        var t = Read(0);
        Assert.NotNull(t.Heavy[2].Stomp);
        Assert.Equal("Stomp_3.5m", t.HeavyAnimations[2]);
        Assert.Equal(0f, t.Heavy[2].Damage);
    }

    [Fact]
    public void Only_the_power_sword_map_has_a_stab_and_it_names_the_x_f_strike()
    {
        Assert.Equal(2, MeleeTuning.PowerSword.StabHeavy);
        Assert.Equal("Sequencer27", MeleeTuning.PowerSword.HeavySequencers[MeleeTuning.PowerSword.StabHeavy - 1]);
        Assert.Equal(0, MeleeTuning.PowerAxe.StabHeavy);
        Assert.Equal(0, MeleeTuning.ThunderHammer.StabHeavy);
        Assert.Equal(0, MeleeTuning.Displace.StabHeavy);
    }

    [MeleeInstallPinTests.InstallFact]
    public void On_the_install_the_sword_finisher_is_the_stomp_and_the_stab_after_it_is_the_x_f_clip_at_100_plus_50_stagger_and_the_axe_keeps_its_slam()
    {
        string game = GameInstall.TryLocate()!.Dir;
        var sword = MeleeTuning.Load(game, MeleeTuning.PowerSword);
        Assert.Equal(6, sword.Heavy.Count);
        Assert.NotNull(sword.Heavy[4].Stomp);                       // sim/stab-key-only: F + F is the data's footstomp again
        Assert.False(sword.Heavy[4].Stab);
        Assert.Equal(sword.HeavyAnimations[1], sword.HeavyAnimations[5]);
        var stab = sword.Heavy[5];
        Assert.True(stab.Stab);
        Assert.All(sword.Heavy, h => Assert.NotEqual(6, h.YNext));
        Assert.Null(stab.Stomp);
        Assert.Equal((100f, 50f, ReactionKind.Stagger), (stab.Damage, stab.ShieldDamage, stab.PrimaryReaction.Kind));
        Assert.Equal((sword.Heavy[1].Duration, sword.Heavy[1].HitTime), (stab.Duration, stab.HitTime));
        var axe = MeleeTuning.Load(game, MeleeTuning.PowerAxe);
        Assert.NotNull(axe.Heavy[4].Stomp);
    }

    [Fact]
    public void A_read_inside_WithoutSwordStab_keeps_the_stomp_and_the_scope_ends_with_it()
    {
        using (MeleeTuning.WithoutSwordStab())
        {
            Assert.False(MeleeTuning.SwordStabOn);
            var t = Read(2);
            Assert.NotNull(t.Heavy[2].Stomp);
            Assert.Equal("Stomp_3.5m", t.HeavyAnimations[2]);
        }
        Assert.True(MeleeTuning.SwordStabOn);
        Assert.NotNull(Read(2).Heavy[2].Stomp);   // sim/stab-key-only: Classic keeps the stomp finisher too
        Assert.Equal(4, Read(2).Heavy.Count);     // and adds the stab after it
        using (MeleeTuning.WithoutSwordStab()) Assert.Equal(3, Read(2).Heavy.Count);   // Musou and Warfront: no stab strike
    }

    static MeleeCombo Sword(SimConfig c) =>
        c.Classes.First(k => k.Name == "sm - assault marine").MeleeChoices.First(m => m.Wargear == "sm_mp_melee_powersword");

    /// <summary>The real path: the sword in the Assault Marine's melee choices of the match tuning, per content profile.</summary>
    [MeleeInstallPinTests.InstallFact]
    public void Classic_and_faithful_matches_stomp_on_F_F_and_stab_only_on_the_key_and_Musou_Mode_keeps_the_stomp_blast_with_no_stab()
    {
        string game = GameInstall.TryLocate()!.Dir;
        foreach (var extras in new[] { ContentExtras.None, ContentExtras.Orks })
        {
            var heavy = Sword(GameplayTuning.LoadForMatch(game, extras).Config).Heavy;
            Assert.NotNull(heavy[4].Stomp);                                // sim/stab-key-only: the finisher is the stomp
            Assert.Equal(6, Combat.StabIndex(heavy));                      // the key's strike, after it
            var stab = heavy[5];
            Assert.Null(stab.Stomp);
            Assert.Equal((100f, 50f, ReactionKind.Stagger), (stab.Damage, stab.ShieldDamage, stab.PrimaryReaction.Kind));
        }
        var musouHeavy = Sword(GameplayTuning.LoadForMatch(game, ContentExtras.Musou).Config).Heavy;
        Assert.Equal(0, Combat.StabIndex(musouHeavy));                     // Musou and Warfront: no stab strike at all
        Assert.Equal(5, musouHeavy.Count);
        var musou = musouHeavy[4];
        Assert.NotNull(musou.Stomp);                                   // the data's footstomp blast: the Librarian's crowd clear
        Assert.Equal(0f, musou.Damage);
        var classic = Sword(GameplayTuning.Load(game).Config).Heavy[5];
        Assert.True(classic.Stab);                                     // the scope is gone after the Musou read
        Assert.NotEqual(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Variants.Bytes(Sword(GameplayTuning.LoadForMatch(game, ContentExtras.Musou).Config)))),
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Variants.Bytes(Sword(GameplayTuning.Load(game).Config)))));
    }

    /// <summary>The stab is drawn with the clip of the X,F strike (what a sword user sees in Musou Mode at light strike 1, then F: the data clip of heavy strike 2; the spear's pose clips draw only for the spear).</summary>
    [MeleeInstallPinTests.InstallFact]
    public void The_stab_plays_the_clip_of_the_light_F_strike_in_the_animation_tables()
    {
        string game = GameInstall.TryLocate()!.Dir;
        var classes = GameplayTuning.Load(game).Classes!;
        var clips = classes.MeleeHeavyAnimationsByWeapon["sm_mp_melee_powersword"];
        Assert.Equal(clips[1], clips[5]);                              // sim/stab-key-only: the stab is heavy strike 6
        Assert.Contains("PS_XATK_Combo4", clips[5]);
        Assert.DoesNotContain("PS_XATK_Combo4", clips[4]);             // the finisher draws the stomp clip again
    }
}
