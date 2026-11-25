using System.Numerics;
using System.Text.RegularExpressions;
using SmNet.Tests.Extras.Musou.Campaign;
using SmSim;
using SmSim.Extras.Musou.Arsenal;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Musou;
using SmSim.Extras.Warfront.Chaos;

namespace SmNet.Tests.Extras.Warfront.Chaos.Breach;

/// <summary>Chaos Warfront C4: which detonations close a warp breach (owner 2026-11-25: humans only; the plasma cannon counts). Synthetic wargear under the install's keys.</summary>
public class BreachFilterTests
{
    static readonly SimConfig Config = new()
    {
        Weapons =
        [
            BreachRig.Gun("sm_mp_ranged_grenade_launcher"), BreachRig.Gun("csm_mp_ranged_grenade_launcher"), BreachRig.Gun("sm_mp_exotic_plasma_cannon"), BreachRig.Gun("csm_mp_exotic_plasma_cannon"),
            BreachRig.Gun("test_rocket_launcher"), BreachRig.Gun("sm_mp_ranged_bolter"), BreachRig.Gun("sm_mp_exotic_auto_cannon"), BreachRig.Gun("sm_mp_ranged_plasma_gun"), BreachRig.Gun("mystery_gun"),
        ],
        Thrown =
        [
            BreachRig.Item("sm_mp_eq_frag_grenade"), BreachRig.Item("csm_mp_eq_frag_grenade"), BreachRig.DamagingKrak, BreachRig.Item("sm_mp_eq_melta_bomb"), BreachRig.Item(ArsenalRules.RemoteDetonators),
            BreachRig.Blind, BreachRig.Stim,
        ],
    };

    static int W(string key) => Config.Weapons.ToList().FindIndex(w => w.Name == key);
    static int T(string key) => Config.Thrown.ToList().FindIndex(t => t.Name == key);

    [Fact]
    public void FragKrakMeltaDemoGlRocketPlasmaCount()
    {
        var f = new WarfrontBreachFilter();
        foreach (string key in new[] { "sm_mp_eq_frag_grenade", "csm_mp_eq_frag_grenade", "sm_mp_eq_melta_bomb", ArsenalRules.RemoteDetonators })
            Assert.Equal(new BreachHit(true, WarfrontBreachWhy.Explosive, key), f.Classify(1, T(key), true, Config));
        Assert.True(f.Classify(1, 2, true, Config).Counts, "a krak that deals damage counts (the MP file's krak is the blind grenade)");
        foreach (string key in new[] { "sm_mp_ranged_grenade_launcher", "csm_mp_ranged_grenade_launcher", "sm_mp_exotic_plasma_cannon", "csm_mp_exotic_plasma_cannon", "test_rocket_launcher" })
            Assert.True(f.Classify(1, W(key), false, Config).Counts, key);
        // every stage of the plasma cannon: the verdict is by the weapon alone
        var e = new DetonationEvent(1, 7, 1, W("sm_mp_exotic_plasma_cannon"), 2, Vector3.Zero, 3f, 0, 0f, 0f, 0);
        Assert.True(f.Classify(e, Config).Counts);
        // the grenade launcher's remote detonation is a detonation of the same weapon
        Assert.True(f.Classify(e with { Weapon = W("sm_mp_ranged_grenade_launcher"), Stage = 0 }, Config).Counts);
    }

    [Fact]
    public void BlindStimExcluded()
    {
        var f = new WarfrontBreachFilter();
        Assert.Equal(new BreachHit(false, WarfrontBreachWhy.Blind, "sm_mp_eq_krak_grenade"), f.Classify(1, 5, true, Config));
        Assert.Equal(new BreachHit(false, WarfrontBreachWhy.Stim, "sm_mp_eq_stimpack"), f.Classify(1, 6, true, Config));
    }

    [Fact]
    public void BoltAndAutocannonSplashNeverAppear()
    {
        var f = new WarfrontBreachFilter();
        foreach (string key in new[] { "sm_mp_ranged_bolter", "sm_mp_exotic_auto_cannon", "sm_mp_ranged_plasma_gun" })
            Assert.Equal(new BreachHit(false, WarfrontBreachWhy.NotAnExplosive, key), f.Classify(1, W(key), false, Config));
        // by construction: the bolt and autocannon blasts are MusouBlast calls, never a detonation (only a projectile's Detonate makes a DetonationEvent)
        var blasts = Body(File.ReadAllText(Path.Combine(Root(), "src/SmSim/Extras/Musou/Campaign/World.TfwMelee.cs")), "void TfwBoltBlasts(");
        Assert.Contains("MusouBlast(", blasts);
        Assert.DoesNotContain("Detonate(", blasts);
        Assert.Equal(["src/SmSim/Extras/Musou/Arsenal/World.Charge.cs", "src/SmSim/Extras/Musou/World.MusouCombat.cs", "src/SmSim/Projectiles.cs"], DetonateCallers());
    }

    [Fact]
    public void A_bolt_blast_in_a_chaos_battle_leaves_the_detonations_empty()
    {
        var (w, _) = BreachRig.Lane(BreachRig.Chaos());
        int before = w.TfwBoltBlastsOut;
        // the human's hit-scan rifle into the crowd at the first position: the war's bolt blasts (if the weapon makes them) are no detonation
        var target = w.Tfw!.Battle!.Layout.Positions[0];
        var from = target + new Vector3(0f, -20f, 0f);
        Assert.True(w.TfwRespawnAt(BreachRig.Human, from, 0f));
        for (int k = 0; k < 60; k++)
        {
            BreachRig.Tick(w, InputButtons.Fire, BreachRig.YawTo(from, target), -0.05f);
            Assert.Empty(w.LastDetonations);
        }
        Assert.True(w.TfwBoltBlastsOut >= before);
    }

    [Fact]
    public void EnemyArtilleryDropPodOrbitalExcluded()
    {
        var f = new WarfrontBreachFilter();
        Assert.Equal(WarfrontBreachWhy.NotAPlayer, f.Classify(0, 0, true, Config).Why);
        Assert.Equal(WarfrontBreachWhy.NotAPlayer, f.Classify(-3, 0, true, Config).Why);
        Assert.False(f.Classify(-1035, W("sm_mp_ranged_grenade_launcher"), false, Config).Counts);
        // the barrages, the orbital strike's shells and the drop pod are not projectiles: no Detonate in their files
        foreach (string file in new[] { "src/SmSim/Extras/Musou/Campaign/World.TfwFire.cs", "src/SmSim/Extras/Musou/Campaign/World.TfwDropPod.cs", "src/SmSim/Extras/Musou/Campaign/TfwFire.cs" })
            Assert.DoesNotMatch(@"(?<![\w.])Detonate\(", File.ReadAllText(Path.Combine(Root(), file)));
    }

    [Fact]
    public void RetainerGrenadeExcluded()
    {
        var f = new WarfrontBreachFilter();
        for (int id = TfwContracts.PuppetIdFirst; id <= TfwContracts.PuppetIdLast; id++)
            Assert.Equal(new BreachHit(false, WarfrontBreachWhy.Puppet, null), f.Classify(id, 0, true, Config));
    }

    [Fact]
    public void UnknownKeyExcludedAndLoggedOnce()
    {
        var lines = new List<string>();
        var f = new WarfrontBreachFilter { Log = lines.Add };
        for (int k = 0; k < 3; k++) Assert.Equal(new BreachHit(false, WarfrontBreachWhy.Unknown, "mystery_gun"), f.Classify(1, W("mystery_gun"), false, Config));
        Assert.Single(lines);
        Assert.Contains("mystery_gun", lines[0]);
        Assert.Equal(WarfrontBreachWhy.BadIndex, f.Classify(1, 99, false, Config).Why);
        Assert.Equal(WarfrontBreachWhy.BadIndex, f.Classify(1, -1, true, Config).Why);
        Assert.Equal(["mystery_gun"], WarfrontBreachFilter.Unlisted(Config));
    }

    /// <summary>Every MP weapon and item key of docs/customizer-and-perks-data.md section 1.1 (both sides), Musou's arsenal and the Dreadnought's wargear: each is decided by the table.</summary>
    [Fact]
    public void EveryWarfrontWeaponAndThrownIsClassified()
    {
        string doc = File.ReadAllText(Path.Combine(Root(), "docs/customizer-and-perks-data.md"));
        int start = doc.IndexOf("### 1.1", StringComparison.Ordinal), end = doc.IndexOf("### 1.2", StringComparison.Ordinal);
        Assert.True(start > 0 && end > start);
        var keys = new HashSet<string>();
        foreach (Match m in Regex.Matches(doc[start..end], @"`((?:eq|ranged|exotic|pistol|melee)_[a-z0-9_]+)`")) { keys.Add("sm_mp_" + m.Groups[1].Value); keys.Add("csm_mp_" + m.Groups[1].Value); }
        Assert.True(keys.Count >= 40, $"the doc's table gave {keys.Count} keys");
        foreach (string k in ArsenalRules.Names) keys.Add(k);
        foreach (string k in new[] { DreadnoughtRules.CannonWargear, DreadnoughtRules.MeltaWargear, "dread_csm_assault_cannon", "dread_melee_default" }) keys.Add(k);
        var missing = keys.Where(k => !WarfrontBreachFilter.IsListed(k)).ToList();
        Assert.True(missing.Count == 0, "not in the breach table: " + string.Join(", ", missing));
        var all = new SimConfig { Weapons = keys.Select(BreachRig.Gun).ToArray(), Thrown = keys.Select(k => BreachRig.Item(k)).ToArray() };
        Assert.Empty(WarfrontBreachFilter.Unlisted(all));
        // and the counting ones are exactly the explosives
        var counts = WarfrontBreachFilter.Listed.Where(k => new WarfrontBreachFilter().Classify(1, 0, false, new SimConfig { Weapons = [BreachRig.Gun(k)] }).Counts).OrderBy(k => k).ToList();
        Assert.All(counts, k => Assert.Matches("frag|krak|melta_bomb|demolition_charge|grenade_launcher|plasma_cannon", k));
    }

    [Fact]
    public void DemolitionRingSpotsDoNotCountCentreDoes()
    {
        // the Remote Detonators: a charge thrown down by a breach goes off after its fuse; the centre is the one detonation, the ring's spots are blasts only
        var charge = BreachRig.Item(ArsenalRules.RemoteDetonators, BreachRig.Straight with { Splash = null, DetonateOnCollision = false, Sticky = true, DetonateWhenLifetimeOver = false, Lifetime = 30f });
        var (w, battle) = BreachRig.Lane(BreachRig.Chaos([charge]));
        var breach = battle.ChaosBreaches!.All[0];
        var at = breach.Position + new Vector3(0f, -2.5f, 0f);
        BreachRig.Throw(w, at, 0f, -1.2f, seconds: 0.5f);
        var ids = new HashSet<int>();
        int detonations = 0;
        for (int k = 0; k < (int)((DemolitionChargeRules.FuseSeconds + 2f) * BreachRig.Rate); k++)
        {
            TfwRig.KillAll(w);
            BreachRig.Tick(w);
            foreach (var d in w.LastDetonations) if (d.Thrown && ids.Add(d.Projectile)) detonations++;
        }
        Assert.Equal(1, detonations);
        Assert.True(w.ChargeSpots > 0, "the ring went off");
        Assert.Equal(WarfrontBreachState.Closed, breach.State);
        Assert.Single(battle.ChaosBreaches.Closures);
    }

    internal static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "SmReimpl.slnx"))) d = d.Parent;
        return d!.FullName;
    }

    static string Body(string text, string signature)
    {
        int i = text.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(i >= 0, signature);
        int open = text.IndexOf('{', i), depth = 0, k = open;
        for (; k < text.Length; k++) { if (text[k] == '{') depth++; else if (text[k] == '}' && --depth == 0) break; }
        return text[open..(k + 1)];
    }

    /// <summary>The files of src/SmSim that call the World's Detonate (a projectile's detonation; the Squigs' <c>musou.Detonate</c> is the enemy pool's own and makes none).</summary>
    static List<string> DetonateCallers()
    {
        string root = Root();
        var call = new Regex(@"(?<![\w.])Detonate\(", RegexOptions.Compiled);
        return Directory.EnumerateFiles(Path.Combine(root, "src/SmSim"), "*.cs", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Where(rel => !rel.Split('/').Any(p => p is "bin" or "obj"))
            .Where(rel => File.ReadAllLines(Path.Combine(root, rel)).Any(l => !l.TrimStart().StartsWith("//") && call.IsMatch(l) && !l.Contains("void Detonate(") && !l.Contains("bool Detonate(")))
            .OrderBy(x => x, StringComparer.Ordinal).ToList();
    }
}
