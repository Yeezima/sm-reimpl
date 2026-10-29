using System.Numerics;
using SmContent;
using SmFormats.Baf;
using SmFormats.Havok;
using SmSim;

namespace SmFormats.Tests;

/// <summary>
/// Protocol 30 reader (<see cref="JumpPackAttackTuning"/>): the dash attack (ability_dash_attack, the "Jump Dash Attack" sequencers) and the sword
/// slash (ability_melee_dash, the "Melee Dash Impact" sequencer, the smash-landing split). Synthetic files shaped like the install's (no original bytes).
/// </summary>
public class JumpPackAttackTuningTests
{
    static BehaviorTag Branch(string name, float start) => new("Branch", start, 1f, name, null);
    static BehaviorTag Event(string name, float start) => new("Event", start, start, name, null);

    static (string, object) Trigger(string type, float intensity = 0f, float backward = 0f, float duration = 0f) => ("trigger_reaction", new (string, object)[] { ("reaction_by_intensity", new (string, object)[]
    {
        ("entry", new (string, object)[] { ("intensity", intensity), ("reaction_type", type),
            ("reaction_distance", new (string, object)[] { ("distance_backward", backward), ("duration", duration) }) }),
    }) });

    static (string, object) Entry(string? upgrade, params (string, object)[] commands) => ("entry", new (string, object)[]
    {
        ("required_upgrade_name", new (string, object)[] { ("required_list", upgrade is null ? Array.Empty<(string, object)>() : new (string, object)[] { ("upgrade_name", upgrade) }) }),
        ("action", new (string, object)[] { ("commands", commands) }),
    });

    static (string, object) Damage(float damage, float shield) => ("do_damage", new (string, object)[] { ("damage_general", damage), ("damage_armourshield", shield), ("damagetype", "melee") });

    static (string, object) Heal(float health) => ("target_self_action", new (string, object)[] { ("action", new (string, object)[] { ("commands", new (string, object)[]
    {
        ("do_heal", new (string, object)[] { ("health", health), ("armourshield", 0f) }),
    }) }) });

    /// <summary>The pack's wargear: the dash attack as on the install (default 75 + 25 interrupt, Furious Charge 100 + 50 stun), the slash (60, interrupt, a heal-on-hit entry), the smash split at 8.</summary>
    static BafDocument Wargear(bool slash = true) => BafDocument.Read(SyntheticBafWriter.Build(("ability_data", new (string, object)[] { ("ability_definitions", Abilities(slash)) })));

    static (string, object)[] Abilities(bool slash) =>
    [
        ("ability_jumppack", new (string, object)[] { ("jump_info", new (string, object)[] { ("info", new (string, object)[]
        {
            ("action_land_smash_by_height", new (string, object)[]
            {
                ("landing_action_entry", new (string, object)[] { ("distance_from_apex_to_land", 0f) }),
                ("landing_action_entry", new (string, object)[] { ("distance_from_apex_to_land", 8f) }),
            }),
        }) }) }),
        ("ability_dash_attack", new (string, object)[]
        {
            ("primary_target_actions", new (string, object)[] { ("hit_sim", new (string, object)[] { ("actions", new (string, object)[]
            {
                ("actions_by_upgrade", new (string, object)[] { ("actions", new (string, object)[]
                {
                    Entry(null, Damage(75f, 25f), Trigger("reaction_mp_melee")),
                    Entry("mp_perk_improved_bullrush", Damage(100f, 50f), Trigger("reaction_mp_stun", 0.1f, 1.5f, 0.4f)),   // knockback here to pin the copy (the install's are 0)
                }) }),
            }) }) }),
            ("secondary_target_actions", new (string, object)[] { ("hit_sim", new (string, object)[] { ("actions", new (string, object)[] { Damage(30f, 0f), Trigger("reaction_mp_melee") }) }) }),
            ("ability_metadata", new (string, object)[] { ("generic_metadata", new (string, object)[] { ("max_range", 4f) }) }),
            ("min_time_between_activations", 1f),
        }),
        .. slash ? new (string, object)[] { ("ability_melee_dash", new (string, object)[]
        {
            ("primary_target_actions", new (string, object)[] { ("hit_sim", new (string, object)[] { ("actions", Array.Empty<(string, object)>()) }) }),
            ("secondary_target_actions", new (string, object)[] { ("hit_sim", new (string, object)[] { ("actions", new (string, object)[]
            {
                Damage(60f, 0f), Trigger("reaction_mp_melee"),
                ("actions_by_upgrade", new (string, object)[] { ("actions", new (string, object)[] { Entry("mp_perk_weap_chainsword_heal_on_hit", Heal(30f)) }) }),
            }) }) }),
            ("ability_metadata", new (string, object)[] { ("generic_metadata", new (string, object)[] { ("max_range", 4f) }) }),
        }) } : [],
    ];

    static readonly BehaviorGraph Base = new(JumpPackAttackTuning.DashGraph, [],
    [
        new("Dash Attack Sequencer00", [JumpPackAttackTuning.DashClip], [Branch("BRANCH_HitReact", 0.3488f), Branch("BRANCH_toIdle", 0.3846f), Branch("BRANCH_Evade_End", 0.6615f),
            Branch("BRANCH_ToRanged", 0.6308f), Branch("BRANCH_Evade", 0.6308f), new("WallCollisionTest", 0.1385f, 1f, null, null)], [1f]),
        new("InterruptSequencer0", [JumpPackAttackTuning.DashHitClip], [Branch("BRANCH_Evade_End", 1f), Branch("BRANCH_Evade", 0.9524f), Branch("BRANCH_Run", 0.9048f)], [1f]),
        new("InterruptSequencer01", [JumpPackAttackTuning.DashCollisionClip], [Branch("BRANCH_Evade_End", 0.9688f), Branch("BRANCH_Evade", 0.625f), Branch("BRANCH_Run", 0.25f)], [1f]),
        new("Dash Atk to Idle Sequence", [JumpPackAttackTuning.DashStopClip], [Branch("BRANCH_Evade_End", 0.625f), Branch("BRANCH_Evade", 0.3125f), Branch("BRANCH_Melee_Combo", 0.3125f), Branch("BRANCH_Run", 0.3125f)], [1f]),
    ]);

    static readonly BehaviorGraph Jump = new(JumpPackTuning.Graph, [],
    [
        // feel/jump-pack-2: the clip generators' crops as on the install (the flight plays 0 to 0.4667 s, the impact from 0.4667 s).
        new("Melee Dash Flight Sequence", [JumpPackAttackTuning.SlashClip], [Branch("BRANCH_Dash_Flight", 0f)], [1f]) { Crops = [(0f, 1.2667f)] },
        new("Melee Dash Impact Sequence", [JumpPackAttackTuning.SlashClip], [Branch("BRANCH_Run", 0.3684f), Event("ACTN_Hit_Targets_Multiplayer", 0.0769f), Branch("BRANCH_Jump_To_Idle", 0.8077f)], [1f]) { Crops = [(0.4667f, 0f)] },
    ]);

    static float Length(string clip) => clip switch
    {
        JumpPackAttackTuning.DashClip => 2.1667f, JumpPackAttackTuning.DashHitClip => 0.7f, JumpPackAttackTuning.DashCollisionClip => 1.0667f,
        JumpPackAttackTuning.DashStopClip => 1.4333f, JumpPackAttackTuning.SlashClip => 1.7333f, _ => throw new KeyNotFoundException(clip),
    };

    /// <summary>The lunge: 18.7 m over the clip (evenly here); the others a little; the hit clip has no extracted motion.</summary>
    static HavokExtractedMotion? Motion(string clip) => clip == JumpPackAttackTuning.DashHitClip ? null : new(Vector3.UnitZ, Vector3.UnitY, Length(clip),
        [.. Enumerable.Range(0, 11).Select(k => new Vector4(0f, (clip == JumpPackAttackTuning.DashClip ? 18.7f : 1f) * k / 10f + 2f, 0f, 0f))]);

    [Fact]
    public void The_dash_attack_reads_the_ability_and_the_four_clips_and_stuns_by_the_owners_decision()
    {
        var notes = new List<string>();
        var d = JumpPackAttackTuning.ReadDashAttack("pack", Wargear(), [Base], Length, Motion, null, notes)!;
        var c = d.Charge;
        // The data's default entry (75 + 25, an interrupt); the owner's estimate makes the primary hit a stun.
        Assert.Equal((75f, 25f, ReactionKind.Stun), (c.Primary.Damage, c.Primary.ShieldDamage, c.PrimaryReaction.Kind));
        // The stun is the ability's own stun entry (intensity, knockback and duration as written), not the interrupt with its kind swapped.
        Assert.Equal((0.1f, 1.5f, 0.4f), (c.PrimaryReaction.MinIntensity, c.PrimaryReaction.Backward, c.PrimaryReaction.Duration));
        Assert.Contains(notes, n => n.Contains("owner's estimate") && n.Contains("Interrupt"));
        Assert.Equal((30f, ReactionKind.Interrupt, 4f, 1f), (c.Secondary.Damage, c.SecondaryReaction.Kind, c.Range, c.MinInterval));
        // The lunge ends at BRANCH_toIdle (0.833 s); no input ends it before; contacts throughout; no hyper armour. feel/jump-pack-2 (the owner's request):
        // walls from the first tick (the data's WallCollisionTest is at 0.300 s, in the note) and up to 60 degrees off head-on.
        Assert.Equal((0.8333f, 0.8333f, 0.8333f, 0.8333f), (MathF.Round(c.Rush.End, 4), MathF.Round(c.Rush.MoveAt, 4), MathF.Round(c.Rush.EvadeAt, 4), MathF.Round(c.Rush.MeleeAt, 4)));
        Assert.Equal((0.8333f, 0f, 0f, 60f), (MathF.Round(c.ContactUntil, 4), c.WallFrom, c.HyperArmourUntil, c.WallHitAngle));
        Assert.Contains(notes, n => n.Contains("the data's WallCollisionTest 0.300 s"));
        // The wall stop is the clip the bull rush plays when it connects.
        Assert.Equal(RushTuning.CollisionClip, c.Collision.Animation);
        Assert.Equal(3f, d.MaxHeight);
        // The states after it: the hit (no motion: stands), the wall stop, the run-out, with the shared exits.
        // The data's branch times (feel/jump-pack-2: the simulation scales the after-states' by SimConfig.LockTime, Rush.Begin).
        Assert.Equal((0.7f, 0.6334f, 0.6667f, 0.7f), (MathF.Round(d.Hit.End, 4), MathF.Round(d.Hit.MoveAt, 4), MathF.Round(d.Hit.EvadeAt, 4), MathF.Round(d.Hit.MeleeAt, 4)));
        Assert.Equal(Vector2.Zero, d.Hit.At(0.5f));
        Assert.Contains(notes, n => n.Contains(JumpPackAttackTuning.DashHitClip) && n.Contains("stands still"));
        Assert.Equal((0.8958f, 0.4479f, 0.4479f), (MathF.Round(d.Stop.End, 4), MathF.Round(d.Stop.MoveAt, 4), MathF.Round(d.Stop.MeleeAt, 4)));
        Assert.Equal((1.0334f, 0.2667f), (MathF.Round(c.Collision.End, 4), MathF.Round(c.Collision.MoveAt, 4)));
        // Furious Charge (its perk entry under a perk read): the data's 100 + 50 and stun.
        using (UpgradeScope.Grant(["mp_perk_improved_bullrush"]))
        {
            var perk = JumpPackAttackTuning.ReadDashAttack("pack", Wargear(), [Base], Length, Motion, null, [])!;
            Assert.Equal((100f, 50f, ReactionKind.Stun, 1.5f), (perk.Charge.Primary.Damage, perk.Charge.Primary.ShieldDamage, perk.Charge.PrimaryReaction.Kind, perk.Charge.PrimaryReaction.Backward));
        }
        // No lunge clip: no dash attack.
        Assert.Null(JumpPackAttackTuning.ReadDashAttack("pack", Wargear(), [new BehaviorGraph(Base.Name, [], Base.Sequencers.Skip(1).ToArray())], Length, Motion, null, notes));
    }

    [Fact]
    public void The_slash_reads_the_secondary_profile_the_impact_timing_and_the_eight_metre_split()
    {
        var notes = new List<string>();
        var s = JumpPackAttackTuning.ReadSlash("pack", Wargear(), [Jump], Length, null, notes)!;
        Assert.Equal((60f, 0f, ReactionKind.Interrupt, 4f), (s.Damage.Damage, s.Damage.ShieldDamage, s.Reaction.Kind, s.Range));
        // feel/jump-pack (the owner's request, 2026-10-29): the slash frees the player at 0.3 s (the data's run branch 0.6385 s stays in the note).
        Assert.Equal((0.1333f, 0.3f, 8f, 0f), (MathF.Round(s.HitTime, 4), MathF.Round(s.Recovery, 4), s.MinPoundHeight, s.Heal));
        Assert.Contains(notes, n => n.Contains("run branch 0.63"));
        Assert.Equal((40f, 40f, 10f, 6f, 0.2f), (s.DiveSpeed, s.DiveMinDown, s.DiveReach, s.LungeSpeed, s.LungeTime));
        Assert.Equal(1.7333f, s.ClipLength);
        // feel/jump-pack-2: where the two sequencers play the clip (the client poses the dive's wind-up and the impact's strike by them).
        Assert.Equal((0.4667f, 0.4666f), (MathF.Round(s.ImpactClipStart, 4), MathF.Round(s.FlightClipEnd, 4)));
        Assert.Contains(notes, n => n.Contains("the flight plays") && n.Contains("the impact from 0.467"));
        // Every target the same profile: no stun, no knockback.
        var step = s.Step;
        Assert.Equal((60f, 60f, ReactionKind.Interrupt, ReactionKind.Interrupt, 0f), (step.Damage, step.SecondaryDamage, step.PrimaryReaction.Kind, step.SecondaryReaction.Kind, step.PrimaryReaction.Backward));
        using (UpgradeScope.Grant(["mp_perk_weap_chainsword_heal_on_hit"]))
            Assert.Equal(30f, JumpPackAttackTuning.ReadSlash("pack", Wargear(), [Jump], Length, null, [])!.Heal);
        // Without the ability every press in flight pounds.
        Assert.Null(JumpPackAttackTuning.ReadSlash("pack", Wargear(slash: false), [Jump], Length, null, notes));
        Assert.Contains(notes, n => n.Contains("every press in flight pounds"));
    }
}
