using System.Numerics;
using SmNet.Tests.Extras.Musou.Campaign;
using SmNet.Tests.Extras.Warfront.Chaos.Breach;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront.Chaos.Finale;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Warfront.Chaos.Finale;

/// <summary>Chaos Warfront C5: the duel's isolation. Each test tries the interference and sees it fail: a spectator's input and harm, a grenade in flight, the Guardsmen, the retainers, the other enemies, the breaches, the drops and buffs, the picker, outside damage to the Lord; and the entry's fresh life.</summary>
public class DuelIsolationTests(ITestOutputHelper output)
{
    [Fact]
    public void Bodyguard_lord_is_shielded_and_home_and_the_snapshot_starts_the_duel()
    {
        var (w, b) = FinaleRig.Fortress();
        var log = new List<string>();
        w.WarfrontChaosLog = log.Add;
        ushort lord = b.BossId;
        Assert.NotEqual(0, lord);
        Assert.True(b.FortressBossOrksMade > 0);   // the Chosen on the approaches
        FinaleRig.Steps(w, 30);
        Assert.False(FinaleRig.Roster(w).Admits(lord, FinaleRig.Human));   // shielded through the bodyguard fight
        float before = FinaleRig.Lord(w, b).Health;
        Assert.False(w.Musou!.Damage(lord, FinaleRig.Human, 500f, Vector3.Zero, out _));
        Assert.Equal(before, FinaleRig.Lord(w, b).Health);
        Assert.Equal(WarfrontDuelPhase.Bodyguard, w.WarfrontChaosDuel!.Phase);
        FinaleRig.KillChosen(w, b);
        var d = w.WarfrontChaosDuel!;
        Assert.Equal(WarfrontDuelPhase.Duel, d.Phase);
        Assert.Equal(FinaleRig.Human, w.WarfrontDuelist);
        Assert.True(FinaleRig.Roster(w).Admits(lord, FinaleRig.Human));
        Assert.False(FinaleRig.Roster(w).Admits(lord, 2));
        Assert.False(w.Musou.Leashed(lord));
        foreach (var l in log) output.WriteLine(l);
        Assert.Contains(log, l => l.Contains("snapshot"));
        Assert.Contains(log, l => l.Contains("entry"));
    }

    [Fact]
    public void SpectatorInputIgnoredAndUndamageable()
    {
        var (w, b, d) = FinaleRig.Duel(humans: 2, config: BreachRig.Chaos());
        Assert.Equal(1, w.WarfrontDuelist);   // a tie: the earlier join
        var p2 = w.Players[2];
        Assert.True(p2.Alive);
        Assert.True(w.Tfw!.Field.Hub.Contains(p2.Position));   // aboard
        Assert.True(w.Tfw.Warfront!.CoopHeld!(2));
        Assert.False(w.Tfw.Warfront.CoopHeld!(1));
        float health = p2.Health, shield = p2.Shield;
        w.MusouLandAttack(new MusouAttack(b.BossId, 2, 80f, true, false, p2.Position));
        Assert.Equal(health, w.Players[2].Health);
        Assert.Equal(shield, w.Players[2].Shield);
        FinaleRig.Steps(w, 30, id => new PlayerInput(w.Tick, 0f, 0f, 0f, id == 2 ? InputButtons.Grenade | InputButtons.Fire : InputButtons.None));
        Assert.DoesNotContain(w.Projectiles, q => q.Owner == 2);
        Assert.True(w.Tfw.Field.Hub.Contains(w.Players[2].Position));
    }

    [Fact]
    public void InFlightGrenadeRemoved()
    {
        var (w, b) = FinaleRig.Fortress(humans: 2, config: BreachRig.Chaos());
        for (int t = 0; t < 45 && !w.Projectiles.Any(q => q.Owner == 2); t++)
            w.Step(id => new PlayerInput(w.Tick, 0f, 0f, 0f, id == 2 && t < 2 ? InputButtons.Grenade : InputButtons.None, Pitch: 0.6f));
        Assert.Contains(w.Projectiles, q => q.Owner == 2);   // one in the air
        FinaleRig.KillChosen(w, b, ticks: 1);
        Assert.Equal(WarfrontDuelPhase.Duel, w.WarfrontChaosDuel!.Phase);
        Assert.DoesNotContain(w.Projectiles, q => q.Owner == 2);
        Assert.DoesNotContain(w.Areas, a => a.Owner == 2);
    }

    [Fact]
    public void GuardsWithdrawn()
    {
        var (w, b) = FinaleRig.Fortress(guardsmen: true);
        output.WriteLine($"Guardsmen before: {w.TfwGuards!.Count}");
        FinaleRig.KillChosen(w, b);
        Assert.Equal(0, w.TfwGuards.Count);
        FinaleRig.Steps(w, 60);
        Assert.Equal(0, w.TfwGuards.Count);
    }

    [Fact]
    public void RetainersBenched()
    {
        var (w, b) = FinaleRig.Fortress();
        var rules = w.Tfw!.Warfront!;
        bool standing = true;
        int benched = 0, steps = 0, asked = 0;
        rules.AnyRetainerStanding = () => standing;
        rules.SquadStep = () => steps++;
        rules.SquadTargets = into => { asked++; return 0; };
        w.WarfrontDuelBenchRetainers = () => { benched++; standing = false; };
        FinaleRig.Steps(w, 10);
        Assert.True(steps > 0);   // they fight in the bodyguard fight
        FinaleRig.KillChosen(w, b, ticks: 1);
        Assert.Equal(1, benched);
        int s0 = steps, a0 = asked;
        standing = true;   // one comes back (a reconnect's squad): benched again
        FinaleRig.Steps(w, 30);
        Assert.Equal(2, benched);
        Assert.Equal(s0, steps);   // no squad tick in the duel
        Assert.Equal(a0, asked);   // and no squad Marine is the Lord's target
    }

    [Fact]
    public void OtherEnemiesRemovedWithoutXp()
    {
        var (w, b) = FinaleRig.Fortress();
        FinaleRig.Steps(w, 150);
        int before = FinaleRig.Alive(w);
        output.WriteLine($"alive before the snapshot: {before}");
        Assert.True(before > 5);
        var stats = w.Tfw!.Warfront!.Progression.Campaign.Stats;
        FinaleRig.KillChosen(w, b, ticks: 1);
        int kills = stats.Player(1)?.Kills ?? 0;
        FinaleRig.Steps(w, 60);
        Assert.Equal(1, FinaleRig.Alive(w));   // the Lord alone
        Assert.Equal(kills, stats.Player(1)?.Kills ?? 0);   // no credit for the removed
        Assert.Equal(MusouEnemyType.Warboss, FinaleRig.Lord(w, b).Type);
    }

    [Fact]
    public void BreachesClosedSilently()
    {
        var (w, b) = FinaleRig.Fortress();
        FinaleRig.Steps(w, 30);
        var breaches = b.ChaosBreaches;
        Assert.NotNull(breaches);
        int closures = breaches!.Closures.Count;
        FinaleRig.KillChosen(w, b);
        Assert.False(breaches.AnyStanding);
        Assert.Equal(closures, breaches.Closures.Count);   // silent: no closure event
    }

    [Fact]
    public void DropsCleared_and_DuelistPowerupsCleared()
    {
        var (w, b) = FinaleRig.Fortress();
        w.MusouPowerupDrops = true;
        for (int k = 0; k < 40 && w.MusouDrops.Count == 0; k++) { TfwRig.KillAll(w, b.BossId); FinaleRig.Steps(w, 5); }
        output.WriteLine($"drops before: {w.MusouDrops.Count}");
        FinaleRig.KillChosen(w, b, ticks: 1);
        Assert.Empty(w.MusouDrops.Where(d => d.State == MusouDropState.Live));
        Assert.Equal(0f, w.Players[1].FireBoostLeft);
    }

    [Fact]
    public void PickerHeldWholeDuel()
    {
        var (w, b, d) = FinaleRig.Duel();
        var rt = w.Tfw!.Warfront!.Progression;
        rt.Xp.Capture((int)SmSim.Extras.Warfront.Campaign.WarfrontCampaign.XpForLevel(3), 1);
        int duelTicks = 0;
        for (int t = 0; t < 20 * 30 && d.Phase == WarfrontDuelPhase.Duel; t++)
        {
            w.Step(_ => default);
            if (d.Phase != WarfrontDuelPhase.Duel) break;
            duelTicks++;
            Assert.Null(w.WarfrontOffer(1));   // held: no offer, no protection
            Assert.False(rt.IsProtected(1));
        }
        output.WriteLine($"duel ticks checked: {duelTicks}");
        Assert.True(duelTicks > 30 * 3);
        Assert.True(rt.Campaign.Player(1)!.PendingPicks > 0);   // kept for after the duel
    }

    [Fact]
    public void OnlyDuelistDamagesLord()
    {
        var (w, b, d) = FinaleRig.Duel(humans: 2);
        ushort lord = b.BossId;
        var musou = w.Musou!;
        float h = FinaleRig.Lord(w, b).Health;
        Assert.False(musou.Damage(lord, 2, 100f, Vector3.Zero, out _));   // a spectator's hit
        Assert.False(musou.Damage(lord, SmSim.Extras.Warfront.WarfrontBattleRules.SquadIdFirst, 100f, Vector3.Zero, out _));   // a retainer's
        Assert.False(musou.Damage(lord, 0, 100f, Vector3.Zero, out _));   // a source without a player (a lingering effect, a strike)
        Assert.False(musou.StaggerMelee(lord, Vector2.UnitX, 2f, 2));
        Assert.False(musou.KnockDown(lord, Vector2.UnitX, 2f, 2, 0f));
        Assert.Equal(h, FinaleRig.Lord(w, b).Health);
        Assert.True(musou.Damage(lord, 1, 100f, Vector3.Zero, out _));   // the duelist's
        Assert.True(FinaleRig.Lord(w, b).Health < h);
        float after = FinaleRig.Lord(w, b).Health;
        w.TfwShellForTests(FinaleRig.Lord(w, b).Position, caller: 2);   // an orbital shell of another player
        Assert.Equal(after, FinaleRig.Lord(w, b).Health);
    }

    [Fact]
    public void EntryFullHealthArmourAmmoCharges()
    {
        var (w, b) = FinaleRig.Fortress();
        var p = w.Players[1];
        w.MusouLandAttack(new MusouAttack(b.BossOrks[0], 1, 60f, true, false, p.Position));
        Assert.True(w.Players[1].Health + w.Players[1].Shield < p.Health + p.Shield);
        FinaleRig.KillChosen(w, b, ticks: 1);
        var q = w.Players[1];
        var body = Loadouts.Body(w.Config, q);
        Assert.Equal(body.HealthMax, q.Health);
        Assert.Equal(body.ShieldMax, q.Shield);
        Assert.True(q.Clip > 0);
        Assert.Equal(p.Class, q.Class);
        Assert.True(FinaleRig.Flat(q.Position, b.FortressPlan!.BossSpot) <= b.FortressPlan.ArenaRadius);   // on the plaza
    }

    [Fact(Skip = "Executions X3 is not on this base: the WarfrontDuelExecutions seam is a no-op until it lands (the lead wires ExecutionsAbortAll and the overshield there)")]
    public void ExecutionsAborted_and_EntryClearsOvershield() { }
}
