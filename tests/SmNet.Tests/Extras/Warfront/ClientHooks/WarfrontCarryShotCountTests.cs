using SmClient.Extras.Warfront.Progression;
using SmNet.Extras.Warfront;
using SmNet.Tests.Extras.Musou.Campaign;
using SmNet.Tests.Extras.Warfront.Battle;
using SmSim;
using SmSim.Extras.Warfront.Campaign;
using SmSim.Extras.Warfront.Progression;

namespace SmNet.Tests.Extras.Warfront.ClientHooks;

/// <summary>
/// W5c second pass, review item 6a without a client: the attack speed the shot timing reads (<c>Stims.AttackSpeed</c>, which sets how many shots a held trigger fires in 10 s) on the server's player with three
/// attack-speed cards, on the predicted player once the carry hook has copied the picker model's bonus into it, and on the predicted player without the hook (the bonus stays 0). A real client's shot count over 10 s
/// was not measured (see the W5c second report).
/// </summary>
public class WarfrontCarryShotCountTests
{
    [Fact]
    public void The_carried_bonus_gives_the_predicted_player_the_servers_attack_speed_and_without_it_the_counts_differ()
    {
        var w = WarfrontBattleTests.War(1);
        TfwRig.Deploy(w);
        var rt = w.Tfw!.Warfront!.Progression;
        rt.Campaign.AddXp(1UL, WarfrontCampaign.XpForLevel(4) - rt.Campaign.Player(1UL)!.Xp);
        for (int i = 0; i < 3; i++) Assert.True(rt.Campaign.TryTakePick(1UL, (int)WarfrontCard.AttackSpeed));
        for (int k = 0; k < 5; k++) w.Step(_ => default);
        var server = w.Players[1];
        Assert.Equal(0.3f, server.AttackSpeedBonus, 4);

        // The client: the Progress section the server sent (the bonus in thousandths) goes into the picker model, and the hook copies the model's value into the predicted record.
        var model = new WarfrontProgressModel();
        model.Apply(new WarfrontProgressWire(4, 6000, 6000, 9000, 0, new byte[] { 3, 0, 0, 0, 0 }, 0, 300));
        var uncarried = server with { AttackSpeedBonus = 0f };   // what the prediction holds without the hook: the field is not on the wire
        var carried = uncarried with { AttackSpeedBonus = model.AttackSpeedBonus };   // WarfrontCarry

        float serverSpeed = Stims.AttackSpeed(w.Config, server);
        Assert.Equal(serverSpeed, Stims.AttackSpeed(w.Config, carried), 5);
        Assert.NotEqual(serverSpeed, Stims.AttackSpeed(w.Config, uncarried));
        // Shots in 10 s of a held trigger are proportional to the speed: the carried count is the server's, the uncarried one is 23 % short.
        Assert.Equal(1.3f, serverSpeed, 4);
        Assert.Equal(1f, Stims.AttackSpeed(w.Config, uncarried), 4);
    }
}
