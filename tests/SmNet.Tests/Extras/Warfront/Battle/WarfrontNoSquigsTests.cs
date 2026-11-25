using SmNet.Tests.Extras.Musou.Campaign;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront;

namespace SmNet.Tests.Extras.Warfront.Battle;

/// <summary>
/// Warfront without Squigs (owner 2026-11-25: "temporarily remove squigs from warfront mode"; <see cref="WarfrontRules.NoSquigs"/>): a Warfront battle of several minutes spawns no
/// Bomb Squig on any path (the drop director, its capture aid), while the same war without the Warfront flag (Musou Mode) still drops them.
/// </summary>
public class WarfrontNoSquigsTests
{
    static SimConfig NoWarfront(SimConfig c) => c with { Extras = c.Extras & ~ContentExtras.Warfront };

    /// <summary>
    /// A battle of <paramref name="seconds"/>, the field cleared every second so the human lives and the pool has room; returns the drop cues, the Squigs dropped, the most Squigs
    /// alive at once and the most Rokkit Boys alive at once.
    /// </summary>
    static (int Cues, int Dropped, int MaxAlive, int Rokkits) Battle(SimConfig config, float seconds, float demoSeconds = 0f)
    {
        var w = TfwRig.War(config: config);
        w.Tfw!.Guardsmen = false;
        var battle = TfwRig.Deploy(w);
        battle.WeirdboyzOn = false;
        w.TfwS2DemoSeconds = demoSeconds;
        int cues = 0, maxAlive = 0, rokkits = 0;
        for (int t = 0; t < (int)(seconds * 30f); t++)
        {
            w.Step(_ => default);
            cues += w.Musou!.Events.Count(e => e.Kind == MusouEventKind.Telegraph && e.Value == MusouSquigRules.DropCue);
            maxAlive = Math.Max(maxAlive, w.Musou.CountAlive(MusouEnemyType.BombSquig));
            rokkits = Math.Max(rokkits, w.Musou.CountAlive(MusouEnemyType.RokkitBoy));
            if (t % 30 == 29) TfwRig.KillAll(w);
        }
        return (cues, w.TfwSquigsDropped, maxAlive, rokkits);
    }

    [Fact]
    public void The_switch_is_on_and_the_warfront_run_carries_its_rules()
    {
        Assert.True(WarfrontRules.NoSquigs);
        Assert.NotNull(TfwRig.War(config: WarfrontBattleTests.WarfrontConfig).Tfw!.Warfront);
    }

    [Fact]
    public void A_warfront_battle_of_several_minutes_drops_no_squig()
    {
        var (cues, dropped, maxAlive, _) = Battle(WarfrontBattleTests.WarfrontConfig, 2f * MusouSquigRules.DropMaxSeconds + 10f);
        Assert.Equal(0, cues);
        Assert.Equal(0, dropped);
        Assert.Equal(0, maxAlive);
    }

    [Fact]
    public void Musou_mode_still_drops_squigs_in_the_same_battle()
    {
        var (cues, dropped, maxAlive, _) = Battle(NoWarfront(WarfrontBattleTests.WarfrontConfig), 2f * MusouSquigRules.DropMaxSeconds + 10f);
        Assert.True(dropped >= 1, $"Musou Mode dropped {dropped} Squigs");
        Assert.True(cues >= 1);
        Assert.True(maxAlive >= 1);
    }

    [Fact]
    public void The_capture_aid_lands_no_squig_in_warfront_but_its_rokkit_still_comes()
    {
        var (cues, _, maxAlive, rokkits) = Battle(WarfrontBattleTests.WarfrontConfig, 10f, demoSeconds: 2f);
        Assert.Equal(0, cues);
        Assert.Equal(0, maxAlive);
        Assert.True(rokkits >= 1, "the aid's Rokkit Boy still comes");

        var (musouCues, _, musouAlive, _) = Battle(NoWarfront(WarfrontBattleTests.WarfrontConfig), 10f, demoSeconds: 2f);
        Assert.True(musouCues >= 1 && musouAlive >= 1, "Musou Mode's aid still lands its Squig");
    }
}
