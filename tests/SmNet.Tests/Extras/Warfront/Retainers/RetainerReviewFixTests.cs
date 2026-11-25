using System.Numerics;
using SmNet.Tests.Extras.Musou.Campaign;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront.Retainers;
using SmSim.Extras.Warfront.Squad;
using Rig = SmNet.Tests.Extras.Warfront.Wiring.WarfrontSquadWiringTests.Rig;

namespace SmNet.Tests.Extras.Warfront.Retainers;

/// <summary>
/// The T2 review fixes (lead 39, 2026-11-25): retainers never capture, a leaver's squad holds whatever orders came before and every battle lands on Follow, a Hold with a point gives ring places, a retainer's
/// melee reach is a human's (the cleave trim and Warfront's cleave bonus), and a retainer stuns a boss and is hurt (not killed) by a trap as a human is.
/// </summary>
public class RetainerReviewFixTests
{
    [Fact]
    public void A_retainer_standing_alone_on_a_position_never_captures_it_and_the_battle_knows_its_id()
    {
        var r = Rig.Make(marines: 2);
        int id = r.IdOf(1, 0);
        Assert.Contains(id, r.Battle.WarfrontRetainerIds);
        Assert.Contains(r.IdOf(1, 1), r.Battle.WarfrontRetainerIds);
        Assert.DoesNotContain(1, r.Battle.WarfrontRetainerIds);
        var at = r.Battle.Layout.Positions[0];
        Assert.True(r.W.TfwRespawnAt(id, at, 0f));
        for (int t = 0; t < 30 * 30; t++) { r.Clear(); r.W.Step(_ => default); }
        Assert.NotEqual(TfwPositionState.Secured, r.Battle.Objectives[0].State);
        Assert.Equal(0f, r.Battle.Objectives[0].Progress);
        TfwRig.Secure(r.W, r.Battle, 0);   // the human on it secures it as before
    }

    [Fact]
    public void A_leavers_squad_holds_after_his_own_orders_his_next_order_is_heard_and_a_new_battle_lands_on_Follow()
    {
        var r = Rig.Make(marines: 2);
        Assert.True(r.Ret.Order(1, new TfwCommandRecord(3, TfwCommand.Attack, 0)));   // a client's small counter
        Assert.True(r.Ret.Order(1, new TfwCommandRecord(4, TfwCommand.Follow, 0)));
        Assert.True(r.Wi.HoldSquad(1));   // he leaves: the server's own order (was refused by the 16-bit compare against 60 000)
        Assert.Equal(SquadOrder.Hold, r.Ret.OrderOf(1));
        Assert.True(r.Ret.Order(1, new TfwCommandRecord(5, TfwCommand.Attack, 0)));   // his next order after the server's
        Assert.Equal(SquadOrder.Attack, r.Ret.OrderOf(1));
        r.Ret.BeginBattle();
        Assert.Equal(SquadOrder.Follow, r.Ret.OrderOf(1));   // a redeploy lands on Follow
        Assert.Equal((ushort)0, r.Ret.Orders.Echo(1));
    }

    [Fact]
    public void A_hold_on_a_point_gives_each_retainer_its_own_ring_place()
    {
        var r = Rig.Make(marines: 2);
        var point = r.Where(1) + new Vector3(10f, 0f, 0f);
        Assert.True(r.Ret.Order(1, new TfwCommandRecord(7, TfwCommand.Hold, 0, point)));
        var a = r.W.Tfw!.MarineOf(r.IdOf(1, 0))!.HoldAt;
        var b = r.W.Tfw!.MarineOf(r.IdOf(1, 1))!.HoldAt;
        Assert.NotEqual(a, b);
        Assert.Equal(WarfrontRetainerRules.LandRadius, Vector3.Distance(a, point), 3);
        Assert.Equal(WarfrontRetainerRules.LandRadius, Vector3.Distance(b, point), 3);
        Assert.Equal(point, WarfrontRetainers.HoldPlace(point, 0, 1));   // one alone holds the point itself
    }

    [Fact]
    public void A_retainers_melee_reach_is_a_humans_with_the_trim_and_the_cleave_bonus_but_its_damage_stays_base()
    {
        Assert.True(WarfrontRetainerRules.CleaveBonus);
        Assert.True(WarfrontRetainerRules.HumanMeleeReach);
        Assert.False(WarfrontRetainerRules.HumanOnlyScales);   // owner 2026-11-25: "Base player damage only" (no x1.2 ranged, no fall-off waiver, no free pierce)
        var r = Rig.Make(marines: 1);
        Assert.True(r.W.WarfrontCleaveBonus(r.IdOf(1, 0)));
        Assert.True(r.W.WarfrontCleaveBonus(1));
    }

    [Fact]
    public void A_retainers_melee_stuns_a_boss_as_a_humans_does()
    {
        Assert.True(WarfrontRetainerRules.StunsBosses);
        var r = Rig.Make(marines: 1);
        var musou = r.W.Musou!;
        ushort boss = r.Ork(MusouEnemyType.Warboss, r.Body(1, 0).Position + new Vector3(3f, 0f, 0f), 1e5f);
        for (int t = 0; t < 300 && (!musou.TryGet(boss, out var b) || b.State == MusouEnemyState.Spawning); t++) r.Stand(1);   // out of its spawn
        musou.Frozen = true;
        int before = musou.BossStuns;
        Assert.True(musou.StaggerMelee(boss, new Vector2(1f, 0f), 1f, r.IdOf(1, 0)));
        Assert.Equal(before + 1, musou.BossStuns);
        Assert.False(musou.StaggerMelee(boss, new Vector2(1f, 0f), 1f, TfwContracts.PuppetIdLast) && musou.BossStuns > before + 1);   // an unregistered puppet id (an AI Marine) adds no stun
    }

    [Fact]
    public void A_trap_hurts_a_retainer_as_a_human_instead_of_killing_it_outright()
    {
        Assert.True(WarfrontRetainerRules.TrapsHurtLikeHumans);
        var r = Rig.Make(marines: 1, deploy: false);
        r.W.TfwMeksOn = true;
        r.Battle = TfwRig.Deploy(r.W);   // not Rig.Deploy: its clear would kill the Scrap Meks too
        r.Stand(3);
        var meks = Assert.IsType<MusouMeks>(r.W.TfwMeks);
        meks.Builds = false;
        r.W.Musou!.Frozen = true;
        int id = r.IdOf(1, 0);
        var spot = new Vector3(30f, -90f, 0f);   // the Musou trap test's spot on the test arena
        Assert.NotNull(meks.PlaceBuilt(0, MusouContraptionKind.Trap, spot));
        Assert.True(r.W.TfwRespawnAt(id, spot + new Vector3(-1f, 0f, 0f), 0f));
        float before = r.Body(1, 0).Health + r.Body(1, 0).Shield;
        for (int t = 0; t < 3 && r.Body(1, 0).Health + r.Body(1, 0).Shield == before; t++) r.W.Step(_ => default);
        var after = r.Body(1, 0);
        Assert.True(after.Alive, "a trap hurts a retainer, it does not kill it outright");
        Assert.True(after.Health + after.Shield < before, "the trap hurt it");
    }
}
