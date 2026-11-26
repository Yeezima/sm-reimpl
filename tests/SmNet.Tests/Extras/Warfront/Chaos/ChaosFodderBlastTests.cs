using System.Numerics;
using SmNet.Tests.Extras.Musou.Campaign;
using SmNet.Tests.Extras.Warfront.Chaos.Breach;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Arsenal;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Warfront.Chaos;

/// <summary>
/// C2 review (lead 39), in the World: the autocannon's own blast kills a Chaos Cultist in one (the owner's 2026-11-11 rule, by kind), the flag that says so is off again after the blast, and a plain bolt
/// blast does not one-shot him (his 110 health is over the bolt blast's).
/// </summary>
public class ChaosFodderBlastTests(ITestOutputHelper output)
{
    /// <summary>A hit-scan gun of the key that does almost no damage itself, so the blast decides.</summary>
    static WeaponSpec Gun(string key) => new(key, WeaponSlot.Ranged, TriggerKind.SemiAuto, 1f, 0.5f, 10, 90, 1f, 250f, 250f, 250f, 1f, 1f, 1f, 1f, 1f);

    static (bool Dead, float Health, bool Flag, int Blasts, string Shots) ShootCultist(string key)
    {
        var baseConfig = WarfrontChaosRosterTests.ChaosConfig;
        var config = baseConfig with
        {
            Weapons = [baseConfig.Weapons[0], Gun(key) with { Slot = WeaponSlot.Pistol }],   // in the rig pistol's place: the gun the war's rig hands the human (its primary is none)
        };
        var (w, battle) = BreachRig.Lane(config);
        var musou = w.Musou!;
        musou.Frozen = true;
        var at = battle.Layout.LandingZone;
        Assert.True(w.TfwRespawnAt(BreachRig.Human, at, 0f));
        TfwRig.KillAll(w);
        BreachRig.Tick(w);
        TfwRig.KillAll(w);
        ushort cultist = musou.Spawn(MusouEnemyType.Boy, at + new Vector3(0f, 10f, 0f), MathF.PI, musou.Rules.Of(MusouEnemyType.Boy).Health);
        Assert.NotEqual(0, cultist);
        int blasts = w.TfwBoltBlastsOut;
        var shots = new List<string>();
        for (int k = 0; k < 6; k++)
        {
            BreachRig.Tick(w, k == 1 ? InputButtons.Fire : InputButtons.None, 0f, -0.03f);
            foreach (var s in w.LastShots) shots.Add($"{config.Weapons[s.Weapon].Name} {s.Hit} {s.Origin}->{s.End}");
        }
        bool alive = musou.TryGet(cultist, out var e) && e.Alive;
        string seen = $"primary {w.Players[BreachRig.Human].Primary}; shots: {string.Join("; ", shots)}";
        return (!alive, alive ? e.Health : 0f, musou.FodderBlast, w.TfwBoltBlastsOut - blasts, seen);
    }

    [Fact]
    public void An_autocannon_blast_one_shots_a_cultist_and_a_bolt_blast_does_not()
    {
        var cannon = ShootCultist(ArsenalRules.Autocannon);
        output.WriteLine($"autocannon: dead {cannon.Dead}, blasts {cannon.Blasts}; {cannon.Shots}");
        Assert.True(cannon.Blasts > 0, "the round struck and blasted: " + cannon.Shots);
        Assert.True(cannon.Dead, "the autocannon's own blast kills a Cultist in one");
        Assert.False(cannon.Flag, "the flag is off after the blast");
        var bolt = ShootCultist("sm_mp_ranged_bolter");
        output.WriteLine($"bolter: dead {bolt.Dead}, health {bolt.Health:0.0}, blasts {bolt.Blasts}; {bolt.Shots}");
        Assert.True(bolt.Blasts > 0, "the bolt round struck and blasted: " + bolt.Shots);
        Assert.False(bolt.Dead, "a bolt blast does not one-shot a Cultist");
        Assert.False(bolt.Flag);
    }
}
