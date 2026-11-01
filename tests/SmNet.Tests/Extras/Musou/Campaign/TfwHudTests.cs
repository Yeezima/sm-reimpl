using System.Numerics;
using SmNet.Extras.Musou;
using SmNet.Extras.Musou.Campaign;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Musou.Campaign;

/// <summary>TF7b: tag 7's own block, what the World puts in it, the stations, the order keys and the HUD's words.</summary>
public class TfwHudTests
{
    static readonly TfwLaneState Lane = new(1, 0.25f, 100f, 110f, 1, 0f, WarRules.LordPresent);
    static readonly TfwOwnView Own = new(3, 17, 22, 2, 4, true, TfwRetainerState.Reviving, MarineOrder.Attack, 171, 57, 6, 2, 1, Deployed: true);
    static TfwWarView War(TfwRunPhase phase, TfwOwnView own = default, byte supplies = 4) => new(phase, TfwLane.B, 120f, 1f, 5, supplies, 9, Lane, Lane, Lane, own);

    [Fact]
    public void TheOwnBlockRoundTrips()
    {
        var war = War(TfwRunPhase.Pickup, Own);
        var bytes = TfwWire.WriteWar(war);
        Assert.Equal(85, TfwWire.WarBytes);
        Assert.Equal(TfwWire.WarBytes, bytes.Length);
        Assert.Equal(war, TfwWire.ReadWar(bytes));
    }

    [Theory]
    [InlineData(4, 11)]    // the revive's tenths
    [InlineData(5, 4)]     // an unknown flag
    [InlineData(6, 4)]     // the retainer
    [InlineData(7, 3)]     // the order
    [InlineData(13, 4)]    // positions secured
    public void AnOwnBlockOutOfRangeIsRefused(int offset, byte value)
    {
        var bytes = TfwWire.WriteWar(War(TfwRunPhase.Battle, Own));
        bytes[TfwWire.WarBytes - 14 + offset] = value;
        Assert.Null(TfwWire.ReadWar(bytes));
    }

    [Theory]
    [InlineData(4, "EMERGENCY EXTRACTION IN 2 S: IT COSTS 2 SUPPLIES")]
    [InlineData(1, "EMERGENCY EXTRACTION IN 2 S: IT COSTS 1 SUPPLY")]
    [InlineData(0, "EMERGENCY EXTRACTION IN 2 S")]
    public void AfterAWonBattleTheExtractionPromptNeverSaysTheWarIsLost(byte supplies, string prompt) =>
        Assert.Equal(prompt, TfwHudModel.Prompt(War(TfwRunPhase.Battle, new TfwOwnView { ExtractionSeconds = 2, BattleWon = true }, supplies), false));

    [Fact]
    public void BothFlagsRoundTripAlone()
    {
        foreach (var own in new[] { new TfwOwnView { BattleWon = true }, new TfwOwnView { Deployed = true }, new TfwOwnView { BattleWon = true, Deployed = true }, default })
            Assert.Equal(own, TfwWire.ReadWar(TfwWire.WriteWar(War(TfwRunPhase.Battle, own)))!.Value.Own);
    }

    [Theory]
    [InlineData(TfwRunPhase.Battle, true, true)]
    [InlineData(TfwRunPhase.Pickup, true, true)]
    [InlineData(TfwRunPhase.FinalAssault, true, true)]
    [InlineData(TfwRunPhase.Battle, false, false)]    // joined after the landing: the core's respawn runs
    [InlineData(TfwRunPhase.Hub, true, false)]
    [InlineData(TfwRunPhase.Lost, true, false)]
    public void TheRunHoldsARespawnOnlyInTheField(TfwRunPhase phase, bool deployed, bool held) =>
        Assert.Equal(held, TfwHudModel.RunHoldsRespawn(War(phase, new TfwOwnView { Deployed = deployed })));

    [Fact]
    public void TheWorldReportsWhoIsInTheField()
    {
        var w = TfwRig.War();
        Assert.False(w.TfwOwn(TfwRig.Human).Deployed);                       // the hub
        TfwRig.Deploy(w);
        Assert.True(w.TfwOwn(TfwRig.Human).Deployed);
        w.Spawn(2, w.AssignClass(0));                                        // joins mid-battle
        TfwRig.Steps(w, 2);
        Assert.False(w.TfwOwn(2).Deployed);
        Assert.True(w.TfwKillForTests(2));
        TfwRig.Steps(w, 2);
        var joiner = w.Tfw!.War.View() with { Own = w.TfwOwn(2) };
        Assert.False(TfwHudModel.RunHoldsRespawn(joiner));
        Assert.True(w.Players[2].RespawnLeft > TfwBattleRules.RespawnHold);  // not held: the core's timer runs
        Assert.True(w.TfwKillForTests(TfwRig.Human));
        TfwRig.Steps(w, 2);
        Assert.True(TfwHudModel.RunHoldsRespawn(w.Tfw.War.View() with { Own = w.TfwOwn(TfwRig.Human) }));
    }

    [Fact]
    public void TheWorldFillsTheOwnBlock()
    {
        var w = TfwRig.War();
        var run = w.Tfw!;
        Assert.Equal(default, w.TfwOwn(TfwRig.Human) with { Order = MarineOrder.Follow });   // the hub: nothing under way
        TfwRig.Command(w, TfwCommand.Deploy, TfwLane.B);
        TfwRig.Steps(w, 2);
        Assert.Equal(5, w.TfwOwn(TfwRig.Human).TravelSeconds);
        TfwRig.Seconds(w, TfwBattleRules.TravelSeconds);
        var own = w.TfwOwn(TfwRig.Human);
        Assert.Equal((byte)w.TfwGuards!.Alive, own.Guards);
        Assert.Equal(6, own.Squads);
        Assert.Equal(1, own.Reserves);                                       // 7 squads at Imperial 100
        Assert.Equal(TfwRetainerState.Ready, own.Retainer);
        Assert.False(own.BattleWon);
        run.Enqueue(TfwRig.Human, new TfwCommandRecord(7, TfwCommand.Hold, 0, new Vector3(1f, 2f, 0f)));
        run.Enqueue(TfwRig.Human, new TfwCommandRecord(8, TfwCommand.RequestPickup, 0));
        TfwRig.Steps(w, 2);
        own = w.TfwOwn(TfwRig.Human);
        Assert.Equal(MarineOrder.Hold, own.Order);
        Assert.Equal(20, own.PickupSeconds);
    }

    [Fact]
    public void AFallenRetainerShowsItsRecovery()
    {
        var w = TfwRig.War();
        TfwRig.Deploy(w);
        Assert.True(w.TfwKillForTests(w.Tfw!.RetainerOf(TfwRig.Human)!.Id));
        TfwRig.Steps(w, 2);
        var own = w.TfwOwn(TfwRig.Human);
        Assert.Equal(TfwRetainerState.Recovering, own.Retainer);
        Assert.InRange(own.RecoverySeconds, 170, 180);
    }

    [Fact]
    public void TheStationsAreTheirPads()
    {
        Assert.Equal(TfwStation.Destination, TfwHudModel.StationAt(TfwTestArena.Hub.DestinationStation + new Vector3(1f, 0.5f, 0f), TfwTestArena.Hub));
        Assert.Equal(TfwStation.Equipment, TfwHudModel.StationAt(TfwTestArena.Hub.EquipmentStation, TfwTestArena.Hub));
        Assert.Equal(TfwStation.None, TfwHudModel.StationAt(TfwTestArena.Hub.Centre, TfwTestArena.Hub));
        Assert.Equal(TfwStation.None, TfwHudModel.StationAt(TfwTestArena.Hub.DestinationStation + new Vector3(TfwMap.StationRadius + 0.1f, 0f, 0f), TfwTestArena.Hub));
        Assert.Equal("E: DESTINATION", TfwHudModel.StationPrompt(TfwStation.Destination, "E"));
        Assert.Equal("B: EQUIPMENT", TfwHudModel.StationPrompt(TfwStation.Equipment, "B"));
    }

    [Fact]
    public void OrderKeysActOnThePress()
    {
        var keys = new TfwOrderKeys();
        Assert.Equal(TfwOrderPress.Follow | TfwOrderPress.Attack, keys.Update(true, false, true, false, 0.1f, true));
        Assert.Equal(TfwOrderPress.None, keys.Update(true, false, true, false, 0.1f, true));   // held
        Assert.Equal(TfwOrderPress.Hold, keys.Update(false, true, false, false, 0.1f, true));
    }

    [Fact]
    public void AKeyHeldWhileDisabledDoesNotActLater()
    {
        var keys = new TfwOrderKeys();
        keys.Update(false, true, false, true, 0.1f, false);
        Assert.Equal(TfwOrderPress.None, keys.Update(false, true, false, true, 2f, true));
        Assert.Equal(0f, keys.PickupHold);
    }

    [Fact]
    public void PickupNeedsOneSecondHeld_OnceAPress()
    {
        var keys = new TfwOrderKeys();
        Assert.Equal(TfwOrderPress.None, keys.Update(false, false, false, true, 0.6f, true));
        Assert.Equal(0.6f, keys.PickupHold, 3);
        Assert.Equal(TfwOrderPress.None, keys.Update(false, false, false, false, 0.1f, true));   // let go: starts over
        Assert.Equal(0f, keys.PickupHold);
        Assert.Equal(TfwOrderPress.None, keys.Update(false, false, false, true, 0.6f, true));
        Assert.Equal(TfwOrderPress.Pickup, keys.Update(false, false, false, true, 0.41f, true));
        Assert.Equal(TfwOrderPress.None, keys.Update(false, false, false, true, 5f, true));      // still held
        Assert.Equal(TfwOrderPress.None, keys.Update(false, false, false, false, 0.1f, true));
        Assert.Equal(TfwOrderPress.Pickup, keys.Update(false, false, false, true, 1f, true));
    }

    [Fact]
    public void ThePromptsFollowTheRun()
    {
        Assert.Equal("", TfwHudModel.Prompt(War(TfwRunPhase.Hub), true));
        Assert.Contains("LANDS IN 3", TfwHudModel.Prompt(War(TfwRunPhase.Travel, new TfwOwnView { TravelSeconds = 3 }), true));
        Assert.Contains("PICKUP IN 17", TfwHudModel.Prompt(War(TfwRunPhase.Pickup, new TfwOwnView { PickupSeconds = 17 }), true));
        Assert.Contains("IS COMING: 22", TfwHudModel.Prompt(War(TfwRunPhase.Battle, new TfwOwnView { ReviveSeconds = 22 }), false));
        Assert.Contains("REVIVES YOU: 40%", TfwHudModel.Prompt(War(TfwRunPhase.Battle, new TfwOwnView { ReviveSeconds = 20, ReviveTenths = 4 }), false));
        Assert.Contains("EMERGENCY EXTRACTION IN 2", TfwHudModel.Prompt(War(TfwRunPhase.Battle, new TfwOwnView { ExtractionSeconds = 2 }), false));
        Assert.Contains("THE WAR IS LOST IN 2", TfwHudModel.Prompt(War(TfwRunPhase.Battle, new TfwOwnView { ExtractionSeconds = 2 }, supplies: 1), false));
        Assert.Contains("IT COSTS 2 SUPPLIES", TfwHudModel.Prompt(War(TfwRunPhase.Battle, new TfwOwnView { ExtractionSeconds = 2 }), false));
        Assert.Contains("FREE PICKUP", TfwHudModel.Prompt(War(TfwRunPhase.Battle, new TfwOwnView { BattleWon = true }), true));
        Assert.Equal("", TfwHudModel.Prompt(War(TfwRunPhase.Battle), true));
    }

    [Fact]
    public void TheMapSaysWhyADeployCannotBeAsked()
    {
        Assert.True(TfwHudModel.CanDeploy(War(TfwRunPhase.Hub), true));
        Assert.False(TfwHudModel.CanDeploy(War(TfwRunPhase.Hub), false));
        Assert.False(TfwHudModel.CanDeploy(War(TfwRunPhase.Battle), true));
        Assert.False(TfwHudModel.CanDeploy(War(TfwRunPhase.Hub) with { Transport = 0 }, true));
        Assert.Equal("NO TRANSPORT LEFT", TfwHudModel.DeployNote(War(TfwRunPhase.Hub) with { Transport = 0 }, true));
        Assert.Equal("", TfwHudModel.DeployNote(War(TfwRunPhase.Hub), true));
    }

    [Fact]
    public void TheWordsOfTheFrontsAndTheHq()
    {
        Assert.Equal("HQ BESIEGED", TfwHudModel.FrontText(-1));
        Assert.Equal("NODE 0", TfwHudModel.FrontText(0));
        Assert.Equal("STRONGHOLD", TfwHudModel.FrontText(3));
        Assert.Equal(0, TfwHudModel.Pips(-1));
        Assert.Equal(4, TfwHudModel.Pips(3));
        Assert.Equal("HQ HOLDS", TfwHudModel.HqText(War(TfwRunPhase.Hub)));
        Assert.Equal("HQ BESIEGED: B", TfwHudModel.HqText(War(TfwRunPhase.Hub) with { B = Lane with { Front = -1 } }));
        Assert.Equal("WAR TIME x0.25", TfwHudModel.ScaleText(0.25f));
        Assert.Equal("12:05", TfwHudModel.ClockText(725.9f));
        Assert.Contains("(WOUNDED)", TfwHudModel.LordText(TfwLane.A, WarRules.LordWounded));
        Assert.Contains("AUREX", TfwHudModel.LordText(TfwLane.A, WarRules.LordPresent));
    }

    [Fact]
    public void TheRetainerLineFollowsItsState()
    {
        Assert.Equal("RETAINER: HOLD", TfwHudModel.RetainerText(new TfwOwnView { Retainer = TfwRetainerState.Ready, Order = MarineOrder.Hold }));
        Assert.Equal("RETAINER: REVIVING YOU", TfwHudModel.RetainerText(new TfwOwnView { Retainer = TfwRetainerState.Reviving }));
        Assert.Contains("3:00", TfwHudModel.RetainerText(new TfwOwnView { Retainer = TfwRetainerState.Recovering, RecoverySeconds = 180 }));
    }

    [Fact]
    public void Each_position_is_named_by_a_letter_with_its_state_for_the_hud_and_the_world()
    {
        TfwObjective O(int i, TfwPositionState s, float p = 0f) => new((byte)i, s, p, default);
        Assert.Equal(["A", "B  DEPOT", "C", "COMMANDER"], Enumerable.Range(0, 4).Select(i => TfwHudModel.ObjectiveName(i, false)));
        Assert.Equal("WARBOSS", TfwHudModel.ObjectiveName(3, true));
        Assert.Equal("A  HELD", TfwHudModel.ObjectiveLabel(O(0, TfwPositionState.Held), false));
        Assert.Equal("B  DEPOT  CLEARING", TfwHudModel.ObjectiveLabel(O(1, TfwPositionState.Clearing), false));
        Assert.Equal("C  SECURING 42%", TfwHudModel.ObjectiveLabel(O(2, TfwPositionState.Securing, 0.42f), false));
        Assert.Equal("A  SECURED", TfwHudModel.ObjectiveLabel(O(0, TfwPositionState.Secured, 1f), false));
        Assert.Equal("A  CLEARING - 7 ORKS", TfwHudModel.ObjectiveLabel(O(0, TfwPositionState.Clearing), false, 7));
        Assert.Equal("A  CLEARING - 1 ORK", TfwHudModel.ObjectiveLabel(O(0, TfwPositionState.Clearing), false, 1));
        Assert.Equal("A  HELD", TfwHudModel.ObjectiveLabel(O(0, TfwPositionState.Held), false, 7));   // only a position being cleared says it
    }

    [Fact]
    public void ASourceStopsWithItsPosition()
    {
        TfwObjective O(int i, TfwPositionState s) => new((byte)i, s, 0f, default);
        TfwObjective[] list = [O(0, TfwPositionState.Secured), O(1, TfwPositionState.Held), O(2, TfwPositionState.Locked), O(3, TfwPositionState.Locked)];
        Assert.False(TfwHudModel.SourceActive(0, list, false, false));
        Assert.True(TfwHudModel.SourceActive(1, list, false, false));
        Assert.False(TfwHudModel.SourceActive(3, list, false, false));         // the HQ sends from the commander's coming
        list[3] = O(3, TfwPositionState.Held);
        Assert.True(TfwHudModel.SourceActive(3, list, false, false));
        list[1] = O(1, TfwPositionState.Securing);
        Assert.False(TfwHudModel.SourceActive(1, list, false, false));         // a capture pauses it
        Assert.True(TfwHudModel.SourceActive(0, list, false, true));
        Assert.False(TfwHudModel.SourceActive(3, list, true, false));
    }
}
