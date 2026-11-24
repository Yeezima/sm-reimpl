using SmClient.Extras.Warfront;
using SmContent.Customization;
using SmSim.Extras.Warfront.Squad;

namespace SmNet.Tests.Extras.Warfront.ClientHooks;

/// <summary>W5c hooks 1 and 2 without Godot: the rows the Librarian's step shows, and what a row's action stores.</summary>
public class WarfrontSquadClassRowTests
{
    [Fact]
    public void Three_rows_one_chosen_and_their_actions_name_the_class()
    {
        var rows = WarfrontSquadClassOption.ChoiceRows(SquadClass.Devastator).Single().Rows;
        Assert.Equal(3, rows.Count);
        Assert.Equal([false, false, true], rows.Select(r => r.Selected).ToArray());
        Assert.Equal([SquadClass.Assault, SquadClass.Tactical, SquadClass.Devastator], rows.Select(r => WarfrontSquadClassOption.OfAction(r.Action)!.Value).ToArray());
    }

    [Fact]
    public void A_row_action_stores_the_option_and_the_default_removes_it()
    {
        var profile = new CustomizationProfile(1, Selection.CustomSlot(1), [], []);
        var assault = WarfrontSquadClassOption.ChoiceRows(SquadClass.Tactical).Single().Rows[0];
        var stored = WarfrontSquadClassOption.WithSquadClass(profile, WarfrontSquadClassOption.OfAction(assault.Action)!.Value);
        Assert.Equal(SquadClass.Assault, WarfrontSquadClassOption.SquadClassOf(stored));
        var back = WarfrontSquadClassOption.WithSquadClass(stored, SquadClass.Tactical);
        Assert.Equal(SquadClass.Tactical, WarfrontSquadClassOption.SquadClassOf(back));
        Assert.False(back.Extras.ContainsKey(WarfrontSquadClassOption.ExtrasKey));
    }

    [Fact]
    public void Other_actions_are_not_squad_actions() => Assert.Null(WarfrontSquadClassOption.OfAction("support:artillery"));
}
