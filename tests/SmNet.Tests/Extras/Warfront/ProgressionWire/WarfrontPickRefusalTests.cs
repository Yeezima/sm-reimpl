using SmClient.Extras.Warfront.Progression;
using SmNet.Extras.Warfront;
using SmServer;
using SmSim;
using SmSim.Extras.Warfront;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront.Campaign;
using SmSim.Extras.Warfront.Progression;
using SmNet.Tests.Extras.Warfront.Battle;
using Xunit;

namespace SmNet.Tests.Extras.Warfront.ProgressionWire;

/// <summary>The server's Pick decision (<c>GameServer.WarfrontPickFromWire</c>, what W5a's command handler calls) on a real World: every refusal leaves cards, pending picks and protection as they were.</summary>
public class WarfrontPickRefusalTests
{
    static WarfrontProgressionRuntime Rt(World w) => w.Tfw!.Warfront!.Progression;
    static WarfrontPlayerState Pl(World w, int id) => Rt(w).Campaign.Player((ulong)id)!;
    static void Stand(World w, int ticks) { for (int k = 0; k < ticks; k++) w.Step(_ => default); }

    static (int Cards, int Pending, bool Protected) State(World w) => (Pl(w, 1).Cards.Count, Pl(w, 1).PendingPicks, Rt(w).IsProtected(1));

    [Fact]
    public void Refusals_change_nothing_and_a_second_pick_pays_nothing()
    {
        var w = WarfrontBattleTests.War(2);   // in the hub: offers open at once
        Stand(w, 2);
        Assert.False(GameServer.WarfrontPickFromWire(w, 1, 0, 0));   // no offer stands, nothing to answer
        Rt(w).Campaign.AddXp(1, WarfrontCampaign.XpForLevel(3));   // two picks pending
        Stand(w, 2);
        var offer = w.WarfrontOffer(1)!;
        Assert.NotNull(offer);
        var before = State(w);
        Assert.Equal((0, 2, true), before);
        int wire = offer.PickIndex & 0xFF;
        Assert.False(GameServer.WarfrontPickFromWire(w, 1, 0, (wire + 1) & 0xFF));   // stale
        Assert.False(GameServer.WarfrontPickFromWire(w, 1, 4, wire));                  // slot 4
        Assert.False(GameServer.WarfrontPickFromWire(w, 1, 255, wire));
        Assert.False(GameServer.WarfrontPickFromWire(w, 1, -1, wire));
        Assert.False(GameServer.WarfrontPickFromWire(w, 2, 0, wire));                  // another player has no offer
        Assert.Equal(before, State(w));
        Assert.True(GameServer.WarfrontPickFromWire(w, 1, 1, wire));
        Assert.Equal(1, Pl(w, 1).Cards.Count);
        Assert.Equal(1, Pl(w, 1).PendingPicks);
        Assert.False(GameServer.WarfrontPickFromWire(w, 1, 1, wire));                  // the same Pick twice: the old index is stale, or no offer stands
        Assert.Equal(1, Pl(w, 1).Cards.Count);
        Assert.Equal(1, Pl(w, 1).PendingPicks);
    }

    [Fact]
    public void A_pick_index_past_255_is_compared_modulo_256()
    {
        var w = WarfrontBattleTests.War(2);
        Stand(w, 2);
        var p = Pl(w, 1);
        Rt(w).Campaign.AddXp(1, WarfrontCampaign.XpForLevel(258));   // 257 picks earned
        for (int i = 0; i < 256; i++) Assert.True(Rt(w).Campaign.TryTakePick(1, (int)WarfrontCard.Damage));   // taken without a step, so no offer opens in between
        Stand(w, 2);
        var offer = w.WarfrontOffer(1)!;
        Assert.Equal(256, offer.PickIndex);
        int cards = p.Cards.Count, pending = p.PendingPicks;
        Assert.False(GameServer.WarfrontPickFromWire(w, 1, 0, 1));      // 257 mod 256
        Assert.False(GameServer.WarfrontPickFromWire(w, 1, 0, 255));
        Assert.Equal((cards, pending, true), (p.Cards.Count, p.PendingPicks, Rt(w).IsProtected(1)));
        Assert.True(GameServer.WarfrontPickFromWire(w, 1, 0, 0));       // 256 mod 256
        Assert.Equal(cards + 1, p.Cards.Count);
    }

    [Fact]
    public void A_hidden_picker_names_the_offer_and_its_countdown_in_the_banner()
    {
        var m = new WarfrontProgressModel { PickerHidden = true };
        m.Apply(new WarfrontProgressWire(2, 1500, 1500, 3500, 1, new byte[5], 0, 0));
        m.Apply(new WarfrontOfferWire(true, 0, new[] { WarfrontCard.Damage, WarfrontCard.MaxHp, WarfrontCard.MaxArmor, WarfrontCard.Retainer }, 200, false));
        Assert.True(m.BannerShowing);
        Assert.Equal("CARD PICK: CARD 1 IS TAKEN IN 20 S", m.BannerText);
        m.PickerHidden = false;
        Assert.False(m.BannerShowing);
    }
}
