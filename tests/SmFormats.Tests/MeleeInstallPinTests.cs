using System.Security.Cryptography;
using SmContent;
using SmSim;

namespace SmFormats.Tests;

/// <summary>
/// Weapons C review: the knife, chainsword and Ork choppa combos read from the player's install (read-only) hash exactly as on main f0cea6d
/// before Weapons C (SHA-256 of <see cref="Variants.Bytes(MeleeCombo)"/>, first 16 hex digits; install build 7412325), so the new reader
/// rules (<see cref="MeleeTuning.WeaponMap.ExtendedRules"/>) cannot drift onto the old maps. Skipped when no install is found (CI). No
/// original bytes: only hashes of what the readers build.
/// </summary>
public class MeleeInstallPinTests
{
    static readonly Lazy<string?> Game = new(() => GameInstall.TryLocate()?.Dir);

    /// <summary>A fact that is skipped unless the game install is found on this machine.</summary>
    public sealed class InstallFactAttribute : FactAttribute
    {
        public InstallFactAttribute()
        {
            if (Game.Value is null) Skip = "Space Marine install not found (the pins need the player's install; never in CI)";
        }
    }

    /// <summary>
    /// feel/melee-lock: the pins cover what the readers built before the reach data (<see cref="MeleeStep.Reach"/>), which is stripped here and checked apart;
    /// feel/locks-sprint-stop: so is the evade branch (<see cref="MeleeStep.EvadeAt"/>); feel/charge-directions: the charge's side steer is put back to the old margin's value.
    /// </summary>
    static string H(MeleeCombo m) => Convert.ToHexString(SHA256.HashData(Variants.Bytes(Strip(m))))[..16];
    static MeleeStep Strip(MeleeStep s) => s with { Reach = null, EvadeAt = float.PositiveInfinity };
    static MeleeCombo Strip(MeleeCombo m) => m with
    {
        // feel/charge-directions (protocol 39): RushSpec.SideSteer took the old sprint margin's place in the bytes; the pins were taken with its 0.25 there.
        Rush = m.Rush is { } r ? r with { SideSteer = 0.25f } : null,
        Steps = m.Steps.Select(Strip).ToArray(), Heavy = m.Heavy.Select(Strip).ToArray(),
        Variants = m.Variants.Select(v => v with { Spec = Strip(v.Spec) }).ToArray(),
    };
    static string H(MeleeTuning t) => H(new MeleeCombo(t.Weapon, t.Steps) { Heavy = t.Heavy });

    [InstallFact]
    public void The_old_maps_read_as_on_main_before_weapons_c()
    {
        string game = Game.Value!;
        var pins = new Dictionary<string, string>
        {
            ["sm_mp_melee_chainsword"] = "C84D65D726CD73F6", ["sm_mp_melee_chainsword_gold"] = "A708E4C930F3CBB1", ["sm_mp_melee_combat_knife"] = "0B87423990AD7522",
            ["csm_mp_melee_chainsword"] = "2412B60887E0B844", ["csm_mp_melee_chainsword_gold"] = "46CC7B88A8794A03", ["csm_mp_melee_combat_knife"] = "481797B04A35966A",
        };
        foreach (var (mode, choppa) in new (OrkBalanceMode Mode, string Pin)[] { (OrkBalanceMode.Data, "BB009BC364BCABBF"), (OrkBalanceMode.Parity, "7111F9962A212BF2") })
        {
            var classes = MpClasses.Load(game, ork: mode);
            var combos = classes.Loadouts.SelectMany(l => l.MeleeChoices).GroupBy(m => m.Wargear).ToDictionary(g => g.Key, g => g.First());
            foreach (var (wargear, pin) in pins) Assert.Equal((wargear, pin), (wargear, H(combos[wargear])));
            // feel/melee-lock: every attack of these combos carries its reach (an alignment window and root motion).
            foreach (var wargear in pins.Keys.Append("ork_coop_boy_choppa"))
                Assert.All(combos[wargear].Steps.Concat(combos[wargear].Heavy), s => Assert.True(s.Reach is { Variants.Count: > 0 } r && r.Variants.Any(v => v.Forward.Count > 0), $"{wargear}: an attack without reach"));
            Assert.Equal(("choppa", mode, choppa), ("choppa", mode, H(combos["ork_coop_boy_choppa"])));
            // feel/locks-sprint-stop: every attack of the marines' knives and chainswords has its BRANCH_Evade, after its hit and inside the attack.
            foreach (var wargear in pins.Keys)
                Assert.All(combos[wargear].Steps.Concat(combos[wargear].Heavy), s => Assert.True(float.IsFinite(s.EvadeAt) && s.EvadeAt <= s.Duration, $"{wargear}: an attack without BRANCH_Evade"));
        }
        var ork = RigProfiles.ByName("ork")!;
        Assert.Equal("35C27C6512A6E79F", H(MeleeTuning.Load(game, ork)));
        Assert.Equal("F37D57C04CDFF958", H(MeleeTuning.Load(game, ork, MeleeTuning.OrkChoppaSet2)));
        Assert.Equal("80B029F3BA556BFE", H(MeleeTuning.Load(game)));
    }
}
