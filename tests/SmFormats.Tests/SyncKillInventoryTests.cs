using System.Numerics;
using SmFormats.Havok;
using SmTool.Dump;
using static SmFormats.Havok.SyntheticHavokWriter;

namespace SmFormats.Tests;

/// <summary>
/// executions-inventory (smtool sync-kills, docs/executions-coverage.md): the sync-move finder, the interaction payload and alignment tag reading,
/// the transitions into a move, the victim reactions, the clip-name pairing and the interaction-bone alignment. Synthetic packfiles only.
/// </summary>
public class SyncKillInventoryTests
{
    // Events: 0 BTN_Melee_Sync, 1 PORT_CS_Sync, 2 ACTN_Hit_Primary_Target, 3 BRANCH_Melee_End.
    static readonly string[] Events = ["BTN_Melee_Sync", "PORT_CS_Sync", "ACTN_Hit_Primary_Target", "BRANCH_Melee_End"];

    static StructuredGraph Attacker()
    {
        var grind = new SequencerNode("Grind Sequence",
            new BlendNode("Blend", [new(1f, new ClipNode("c1", "SM_SYN_SwordGrind_1m")), new(3.5f, new ClipNode("c35", "SM_SYN_SwordGrind_3.5m"))]),
            [new Tag("Interaction", 0.1f, 0.1f, new InteractionValue(1)), new Tag("Event", 0.8f, 0.8f, 2), new Tag("AttackAlignment", 0.05f, 0.1f),
             new Tag("Action", 0.83f, 0.83f, "ACTN_give_health_50"), new Tag("Branch", 0.95f, 1f, 3)]);
        var swing = new SequencerNode("Swing Sequence", new ClipNode("s", "SM_MEL_CS_XATK_Combo1_3.5m"), []);
        // The conduit leaves for the grind with no event (the original's conduits: the executable picks the branch).
        var sync = new StateMachineNode("Sync Machine", [new StateFixture(0, "CONDUIT - Ork Sync", new ClipNode("pose", "SM_Idle"), [new TransitionFixture(1)]),
            new StateFixture(1, "Ork Grind", grind)]);
        var root = new StateMachineNode("Root", [
            new StateFixture(0, "Chainsword Attack", swing, [new TransitionFixture(1, EventId: 0)]),
            new StateFixture(1, "Chainsword Sync Kill", sync)]);
        return new StructuredGraph("SM_MEL_ChainSword", Events, [], root);
    }

    static StructuredGraph Victim()
    {
        var death = new SequencerNode("Victim Grind", new ClipNode("v", "BOY_SYN_SwordGrind_3.5m"), [new Tag("Branch", 0.1f, 1f, 3)]);
        var root = new StateMachineNode("Base", [new StateFixture(0, "Idle", new ClipNode("i", "BOY_Idle")), new StateFixture(1, "SYNC KILL", death)],
            Wildcards: [new TransitionFixture(1, EventId: 1)]);
        return new StructuredGraph("Base_MELEE", Events, [], root);
    }

    [Fact]
    public void Finds_the_sync_move_with_its_clips_tags_and_interaction_event()
    {
        var pack = new HavokPackfile(BuildBehaviorStructure([Attacker()]));
        var graphs = HavokBehaviorDetailReader.Read(pack);
        var moves = SyncKillInventory.Moves(pack, graphs);
        var move = Assert.Single(moves);
        Assert.Equal("Chainsword Sync Kill / Ork Grind", move.StatePath);
        Assert.Equal(["SM_SYN_SwordGrind_1m", "SM_SYN_SwordGrind_3.5m"], move.Clips.Select(c => c.Clip));
        Assert.Equal([1f, 3.5f], move.Clips.Select(c => c.Weight ?? 0f));
        var interaction = Assert.Single(move.Tags, t => t.Kind == "Interaction");
        Assert.Equal("PORT_CS_Sync", interaction.Name);
        Assert.Equal(0.1f, interaction.Start, 4);
        Assert.Contains(move.Tags, t => t.Kind == "Event" && t.Name == "ACTN_Hit_Primary_Target" && Math.Abs(t.Start - 0.8f) < 1e-4);
        Assert.Contains(move.Tags, t => t.Kind == "Action" && t.Name == "ACTN_give_health_50");
        var align = Assert.Single(move.Tags, t => t.Kind == "AttackAlignment");
        Assert.NotNull(align.Fields);
        Assert.Equal(["PORT_CS_Sync"], SyncKillInventory.SentEvents(moves));

        var entries = SyncKillInventory.Entries(graphs[0], move, pack);
        Assert.Contains(entries, e => e.From == "Chainsword Attack" && e.To == "Chainsword Sync Kill" && e.Event == "BTN_Melee_Sync");
        Assert.Contains(entries, e => e.From == "CONDUIT - Ork Sync" && e.To == "Ork Grind" && e.Event is null);
    }

    [Fact]
    public void A_plain_swing_outside_a_sync_state_is_not_a_move()
    {
        var pack = new HavokPackfile(BuildBehaviorStructure([Attacker()]));
        Assert.DoesNotContain(SyncKillInventory.Moves(pack, HavokBehaviorDetailReader.Read(pack)), m => m.Sequencer == "Swing Sequence");
    }

    [Fact]
    public void The_victim_reaction_is_the_state_the_sent_event_enters()
    {
        var pack = new HavokPackfile(BuildBehaviorStructure([Victim()]));
        var reactions = SyncKillInventory.Reactions(pack, HavokBehaviorDetailReader.Read(pack), new HashSet<string> { "PORT_CS_Sync" });
        var r = Assert.Single(reactions);
        Assert.Equal("*", r.From);
        Assert.Equal("SYNC KILL", r.StatePath);
        Assert.Equal("BOY_SYN_SwordGrind_3.5m", Assert.Single(r.Sequencers).Clips.Single().Clip);
        Assert.Empty(SyncKillInventory.Reactions(pack, HavokBehaviorDetailReader.Read(pack), new HashSet<string> { "PORT_Other" }));
    }

    [Theory]
    [InlineData("SM_SYN_SwordGrind_3.5m", "BOY_SYN_SwordGrind_3.5m")]
    [InlineData("SM_SYN_ATT_F_ORKSLAM_7M", "BOY_SYN_ATT_F_ORKSLAM_3.5M")]
    [InlineData("SM_SYN_TH_OrkBoy_headPOP1M", "BOY_SYN_TH_OrkBoy_headPOP")]
    [InlineData("SM_SYN_Strgle_Nob_Clash_02m", "NOB_SYN_Strgle_Nob_Clash_07m")]
    [InlineData("SM_2HM_SYN_Str_CSM_Clash_1m", "CSM_2HM_SYN_Str_CSM_Clash_3.5m")]
    [InlineData("SM_SYNC_Rending_4m", "BL_SYNC_Rending_4m")]
    [InlineData("SM_SYN_Chaos_Success", "SM2_SYN_Chaos_Success")]
    public void Victim_halves_pair_by_name(string attacker, string victim) =>
        Assert.Equal(SyncKillInventory.Stem(attacker), SyncKillInventory.Stem(victim));

    [Fact]
    public void Different_moves_do_not_pair() =>
        Assert.NotEqual(SyncKillInventory.Stem("SM_SYN_SwordGrind_3.5m"), SyncKillInventory.Stem("BOY_SYN_ATT_F_ORKGRIND_01_3.5M"));

    [Theory]
    [InlineData("SM_SYN_SwordGrind_3.5m", true)]
    [InlineData("SM_SYNC_Rending_4m", true)]
    [InlineData("SM_MEL_SYNC_MISS", true)]
    [InlineData("SM_NAV_Turret_SyncOn", false)]
    [InlineData("SM_MEL_CS_XATK_Combo1_3.5m", false)]
    public void Sync_clip_names(string clip, bool sync) => Assert.Equal(sync, SyncKillInventory.IsSyncClip(clip));

    [Fact]
    public void Alignment_puts_the_victim_on_the_shared_mark_facing_back()
    {
        // The attacker's mark 3.65 m ahead facing back (180); the victim's mark at its own feet facing forward: the victim stands 3.65 m ahead, turned 180.
        var (start, yaw) = SyncKillInventory.Align(new(new Vector2(0.09f, 3.65f), 180f), new(Vector2.Zero, 0f));
        Assert.Equal(180f, Math.Abs(yaw), 3);
        Assert.Equal(0.09f, start.X, 3);
        Assert.Equal(3.65f, start.Y, 3);
        // The mark at the attacker's end spot (6.9 m ahead) and 3.39 m behind the victim: the victim starts 3.5 m ahead.
        var (start2, yaw2) = SyncKillInventory.Align(new(new Vector2(0f, 6.895f), 0f), new(new Vector2(0.069f, -3.394f), -180f));
        Assert.Equal(180f, Math.Abs(yaw2), 3);
        Assert.Equal(3.501f, start2.Y, 2);
    }

    [Theory]
    [InlineData(190f, -170f)]
    [InlineData(-180f, 180f)]
    [InlineData(540f, 180f)]
    [InlineData(-45f, -45f)]
    public void Angles_normalize(float deg, float expected) => Assert.Equal(expected, SyncKillInventory.NormalizeDeg(deg), 3);
}
