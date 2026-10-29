using SmFormats.Havok;
using static SmFormats.Havok.SyntheticHavokWriter;

namespace SmFormats.Tests;

/// <summary>Behavior graph reading: graphs, event names, sequencers with clip or blend, and their timing tags.</summary>
public class BehaviorTests
{
    static IReadOnlyList<BehaviorGraph> Read(params Graph[] graphs) => HavokBehavior.Read(new HavokPackfile(BuildBehavior(graphs)));

    [Fact]
    public void Graphs_sequencers_clips_blends_and_tags_are_read()
    {
        var graphs = Read(
            new Graph("MP_Test_Melee", ["BTN_Melee_Attack", "BRANCH_Melee_Combo", "ACTN_Hit_Targets_Multiplayer"],
            [
                new Sequencer("CK 1 Sequence", ["SM_A_1m", "SM_A_3.5m", "SM_A_7m"],
                [
                    new Tag("Event", 0.2f, 0.2f, 2),
                    new Tag("Branch", 0.35f, 0.55f, 1),
                    new Tag("Action", 0.1f, 0.3f, "SWING_X1"),
                    new Tag("AttackDistance", 0f, 0.15f, 3.5f),
                ]),
                new Sequencer("Single", ["SM_B"], [new Tag("Branch", 1f, 1f, 7)]),   // event index out of range
            ]),
            new Graph("Other", ["E0"], [new Sequencer("S", ["SM_C"], [new Tag("Event", 0.5f, 0.5f, 0)])]));

        Assert.Equal(["MP_Test_Melee", "Other"], graphs.Select(g => g.Name));
        var melee = graphs[0];
        Assert.Equal(3, melee.Events.Count);
        var ck1 = melee.Sequencers[0];
        Assert.Equal("CK 1 Sequence", ck1.Name);
        Assert.Equal(["SM_A_1m", "SM_A_3.5m", "SM_A_7m"], ck1.Animations);
        Assert.Equal(new BehaviorTag("Event", 0.2f, 0.2f, "ACTN_Hit_Targets_Multiplayer", null), ck1.Tags[0]);
        Assert.Equal(new BehaviorTag("Branch", 0.35f, 0.55f, "BRANCH_Melee_Combo", null), ck1.Tags[1]);
        Assert.Equal(new BehaviorTag("Action", 0.1f, 0.3f, "SWING_X1", null), ck1.Tags[2]);
        Assert.Equal(3.5f, ck1.Tags[3].Distance);
        Assert.Equal(["SM_B"], melee.Sequencers[1].Animations);
        Assert.Null(melee.Sequencers[1].Tags[0].Name);
        // Each graph resolves event indices through its own names.
        Assert.Equal("E0", graphs[1].Sequencers[0].Tags[0].Name);
        Assert.Equal([1f, 1f, 1f], ck1.PlaybackSpeeds);
    }

    [Fact]
    public void A_blend_that_refers_to_itself_is_read_once_not_endlessly()
    {
        var graphs = Read(new Graph("G", [], [new Sequencer("Loop", ["SM_A", "SM_B"], [], PlaybackSpeed: 1.25f, SelfReferencingBlend: true)]));
        var s = graphs[0].Sequencers[0];
        Assert.Equal(["SM_A", "SM_B"], s.Animations);
        Assert.Equal([1.25f, 1.25f], s.PlaybackSpeeds);
    }
}
