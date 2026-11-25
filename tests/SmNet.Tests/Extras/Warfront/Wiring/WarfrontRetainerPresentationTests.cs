using System.Numerics;
using SmNet.Extras.Musou;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront.Retainers;
using SmSim.Extras.Warfront.Squad;

namespace SmNet.Tests.Extras.Warfront.Wiring;

/// <summary>
/// Warfront Phase B T5 follow-up: a retainer's hit event ranks with a human's in Musou's event window (only when the Warfront server gives the window its retainer test; Musou Mode's order is unchanged), and
/// the look a retainer shows follows the registry's current owner (re-bound when he returns) and a Librarian's squad shows only the owner's colours (<see cref="RetainerLook"/>).
/// </summary>
public class WarfrontRetainerPresentationTests
{
    const ushort Human = 3, Retainer = TfwContracts.PuppetIdFirst, AiMarine = TfwContracts.PuppetIdFirst + 1;

    static MusouEvent Hit(ushort player, ushort enemy) => new(MusouEventKind.Hit, 1, enemy, player, 1, Vector3.Zero);
    static MusouEvent Tracer(ushort enemy) => new(MusouEventKind.Tracer, 1, enemy, 0, 0, Vector3.Zero);

    /// <summary>The players of the copies sent first under a cap of 2 (the retainer's hit names a later enemy than the AI Marine's, so the tie-break alone would put the AI Marine's first).</summary>
    static ushort[] FirstTwo(MusouEventWindow window) =>
        window.Step(1, [Tracer(9), Hit(AiMarine, 7), Hit(Retainer, 8), Hit(Human, 6)], cap: 2).Select(e => e.Player).ToArray();

    [Fact]
    public void A_retainers_hit_ranks_with_a_humans_before_an_AI_Marines_and_the_tracers_and_without_Warfront_the_order_is_unchanged()
    {
        Assert.Equal([Human, Retainer], FirstTwo(new MusouEventWindow { WarfrontRetainer = id => id == Retainer }));   // Warfront: rank 3 with the human's
        Assert.Equal([Human, AiMarine], FirstTwo(new MusouEventWindow()));   // Musou Mode and every other server (no retainer test): as before
        Assert.Equal([Human, AiMarine], FirstTwo(new MusouEventWindow { WarfrontRetainer = id => id == Retainer + 100 }));   // a puppet that is no retainer keeps the AI Marines' rank
        Assert.Equal(4, MusouEventWindow.Rank(Hit(Retainer, 8)));   // the static rank (Musou's pins) is unchanged
        Assert.Equal(3, MusouEventWindow.Rank(Hit(Human, 6)));
        var old = new MusouEventWindow { OldHitRank = true, WarfrontRetainer = id => id == Retainer };
        Assert.Equal(MusouEventKind.Tracer, old.Step(1, [Tracer(9), Hit(Retainer, 8)], cap: 1)[0].Kind);   // the old-order test aid keeps every hit after the tracers
    }

    [Fact]
    public void A_retainer_wears_the_look_of_its_current_owner_after_a_rebind_not_the_spawns_owner()
    {
        var r = WarfrontSquadWiringTests.Rig.Make(marines: 2);
        int id = r.IdOf(1, 0);
        Assert.Equal(MarineRole.Squad, r.W.Tfw!.MarineOf(id)!.Role);
        Assert.Equal(1, RetainerLook.Of(r.Ret, r.Wi.Rosters, id)!.Value.Owner);
        Assert.True(r.Ret.Rebind(1UL, 22));   // the owner came back under id 22
        Assert.Equal(22, RetainerLook.Of(r.Ret, r.Wi.Rosters, id)!.Value.Owner);
        Assert.Equal(1, r.W.Tfw.MarineOf(id)!.Owner);   // the spawn's owner, which TfwLook read before
        Assert.Null(RetainerLook.Of(r.Ret, r.Wi.Rosters, 1));   // a human is no retainer: the look path leaves him alone
    }

    [Fact]
    public void The_clients_drawn_arc_and_the_servers_strike_agree_on_the_cleave_bonus_for_a_retainer_and_a_human()
    {
        var r = WarfrontSquadWiringTests.Rig.Make(marines: 1);
        int id = r.IdOf(1, 0);
        Assert.Equal(r.W.WarfrontCleaveBonus(id), SmClient.Extras.Warfront.Squad.WarfrontSquadModel.CleaveBonus(puppet: true, retainer: true));
        Assert.Equal(r.W.WarfrontCleaveBonus(1), SmClient.Extras.Warfront.Squad.WarfrontSquadModel.CleaveBonus(puppet: false, retainer: false));
    }

    [Fact]
    public void A_Librarians_squad_wears_only_the_owners_colours_and_a_loadout_with_the_owners_pieces_wears_them()
    {
        var cfg = WarfrontSquadWiringTests.Cfg();
        var lib = cfg.Classes[0] with { Name = LibrarianRules.ClassName, Role = "librarian" };
        cfg = cfg with { Classes = [.. cfg.Classes, lib] };
        var r = WarfrontSquadWiringTests.Rig.Make(marines: 2, deploy: false, config: cfg);
        r.W.Spawn(1, cfg.Classes.Count - 1);
        r.Wi.StepAboard();
        r.Wi.SetSquadClass(1, SquadClass.Assault);
        r.Deploy();
        int id = r.IdOf(1, 0);
        Assert.False(RetainerLook.Of(r.Ret, r.Wi.Rosters, id)!.Value.OwnPieces);   // a Librarian's squad: the class's own pieces

        Assert.True(RetainerLook.OwnPieces(new SquadLoadout { Armour = ["class:tactical", "head:3"] }));
        Assert.False(RetainerLook.OwnPieces(new SquadLoadout { Armour = ["class:assault"] }));
        var owner = AppearanceCode.Default.WithPiece(ArmourSlot.Head, 3).WithPalette(ArmourSlot.Torso, 10, 20, 30);
        Assert.Equal(owner, RetainerLook.CodeOf(owner, ownPieces: true));
        var colours = RetainerLook.CodeOf(owner, ownPieces: false);
        Assert.Equal(AppearanceCode.DefaultIndex, colours.Piece(ArmourSlot.Head));
        Assert.Equal((byte)20, colours.Palette(ArmourSlot.Torso, 1));
    }
}
