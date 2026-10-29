using System.Numerics;
using SmContent;
using SmFormats.Baf;
using SmFormats.Havok;
using SmSim;

namespace SmFormats.Tests;

/// <summary>
/// M29: the charge (bull rush) from synthetic data: the ability's default hits, the rush and collision clips' motion and branch times;
/// M32: the charger's hyper armour (half of the rush's attack part; the clip's whole-clip CollisionMode is noted, not used).
/// </summary>
public class RushTuningTests
{
    static BehaviorTag Branch(string name, float start) => new("Branch", start, 1f, name, null);

    static (string, object) Trigger(string type) => ("trigger_reaction", new (string, object)[] { ("reaction_by_intensity", new (string, object)[]
    {
        ("entry", new (string, object)[] { ("intensity", 0f), ("reaction_type", type) }),
    }) });

    static (string, object) Entry(bool upgraded, float damage, float shield, string reaction) => ("entry", new (string, object)[]
    {
        ("required_upgrade_name", new (string, object)[] { ("required_list", upgraded ? new (string, object)[] { ("upgrade_name", "perk") } : Array.Empty<(string, object)>()) }),
        ("action", new (string, object)[] { ("commands", new (string, object)[]
        {
            ("do_damage", new (string, object)[] { ("damage_general", damage), ("damage_armourshield", shield) }),
            Trigger(reaction),
        }) }),
    });

    static BafDocument Wargear() => BafDocument.Read(SyntheticBafWriter.Build(("ability_data", new (string, object)[] { ("ability_definitions", new (string, object)[]
    {
        (RushTuning.Ability, new (string, object)[]
        {
            ("primary_target_actions", new (string, object)[] { ("hit_sim", new (string, object)[] { ("actions", new (string, object)[]
            {
                ("actions_by_upgrade", new (string, object)[] { ("actions", new (string, object)[] { Entry(false, 75f, 25f, "reaction_mp_stun"), Entry(true, 100f, 50f, "reaction_mp_stagger") }) }),
            }) }) }),
            ("secondary_target_actions", new (string, object)[] { ("hit_sim", new (string, object)[] { ("actions", new (string, object)[]
            {
                ("do_damage", new (string, object)[] { ("damage_general", 30f) }), Trigger("reaction_mp_melee"),
            }) }) }),
            ("ability_metadata", new (string, object)[] { ("generic_metadata", new (string, object)[] { ("max_range", 4f) }) }),
            ("min_time_between_activations", 1f),
        }),
    }) })));

    static readonly BehaviorGraph Graph = new(RushTuning.Graph, [],
    [
        new("Sequencer05", [RushTuning.RushClip, "SM_BUL_Hit_M_2.5m"], [Branch("BRANCH_Sprint_Finished", 0.7143f), Branch("BRANCH_Attack2Idle", 0.4f), Branch("BRANCH_Evade", 0.3f),
            new("WallCollisionTest", 0.2f, 1f, null, null), Branch("BRANCH_Sprint_Finished", 0.7778f), new("EntityProperty", 0f, 1f, "CollisionMode", null, 1)], [1f, 1f]),
        new("InterruptSequencer00", [RushTuning.CollisionClip], [Branch("BRANCH_Sprint_Finished", 0.9688f), Branch("BRANCH_Sprint_Evade", 0.625f), Branch("BRANCH_To_Melee", 0.25f)], [1f]),
    ]);

    static float Length(string clip) => clip == RushTuning.CollisionClip ? 1.0667f : 1.6667f;

    static HavokExtractedMotion Motion(string clip) => new(Vector3.UnitZ, Vector3.UnitY, Length(clip),
        [.. Enumerable.Range(0, 11).Select(k => new Vector4(0f, (clip == RushTuning.CollisionClip ? 0.7f : 14f) * k / 10f + 1f, 0f, 0f))]);

    [Fact]
    public void The_charge_reads_the_default_hits_and_the_two_clips()
    {
        var notes = new List<string>();
        var r = RushTuning.Read("knife", Wargear(), [Graph], Length, Motion, null, notes)!;
        Assert.Equal((75f, 25f, ReactionKind.Stun), (r.Primary.Damage, r.Primary.ShieldDamage, r.PrimaryReaction.Kind));   // the default entry, not the perk's
        Assert.Equal((30f, ReactionKind.Interrupt, 4f, 1f), (r.Secondary.Damage, r.SecondaryReaction.Kind, r.Range, r.MinInterval));
        Assert.Equal((1.6667f, 1.1905f, 0.6667f, 0.5f, 1.1905f), (r.Rush.ClipLength, MathF.Round(r.Rush.End, 4), MathF.Round(r.Rush.MoveAt, 4), MathF.Round(r.Rush.EvadeAt, 4), MathF.Round(r.Rush.MeleeAt, 4)));
        Assert.Equal((0.6667f, 0.3333f), (MathF.Round(r.ContactUntil, 4), MathF.Round(r.WallFrom, 4)));
        Assert.Equal(0.3333f, MathF.Round(r.HyperArmourUntil, 4));   // M32: half of BRANCH_Attack2Idle's 0.667 s
        // The rush ticks it covers at 30 Hz (Rush.HyperArmour: ticks x 1/30 s within it, 1e-5 s tolerance): the press tick and nine more.
        Assert.Equal(10, Enumerable.Range(1, 30).Count(n => n * (1f / 30f) <= r.HyperArmourUntil + 1e-5f));
        Assert.Contains(notes, n => n.Contains("CollisionMode 1 over 0.000-1.000") && n.Contains("not used"));
        Assert.Equal(new Vector2(0f, 14f), r.Rush.At(r.Rush.ClipLength));   // relative to the first sample
        Assert.Equal((1.0334f, 0.6667f, 0.2667f, 1.0334f), (MathF.Round(r.Collision.End, 4), MathF.Round(r.Collision.EvadeAt, 4), MathF.Round(r.Collision.MeleeAt, 4), MathF.Round(r.Collision.MoveAt, 4)));
        Assert.Null(RushTuning.Read("knife", Wargear(), [new BehaviorGraph(RushTuning.Graph, [], [Graph.Sequencers[0]])], Length, Motion, null, notes));
        Assert.Contains(notes, n => n.Contains(RushTuning.CollisionClip));
    }
}
