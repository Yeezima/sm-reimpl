using SmContent;
using SmFormats.Baf;
using SmFormats.Obpk;
using SmSim;

namespace SmFormats.Tests;

/// <summary>
/// anim/relic-bolter-idle: the simulation takes a weapon's raise family from its wargear's <c>animation_set</c> (<see cref="MpClasses.AnimationSetOf"/>), the client its
/// animations from the names (<see cref="HipFireAnimations.FamilyOf"/> plus <see cref="Loadouts.IsPistolBolterName"/>, <c>NetworkView.AnimationFamilyOf</c>). This pins
/// that the two cannot drift: the multiplayer wargear naming <c>animation_set = "pistol"</c> is exactly the bolt pistols and the four True Grit pistol-slot
/// bolters. Skipped when no install is found (CI). Reads names and one string per file; no original bytes.
/// </summary>
public class PistolAnimationSetInstallTests
{
    static readonly Lazy<string?> Game = new(() => GameInstall.TryLocate()?.Dir);

    /// <summary>A fact that is skipped unless the game install is found on this machine.</summary>
    public sealed class InstallFactAttribute : FactAttribute
    {
        public InstallFactAttribute()
        {
            if (Game.Value is null) Skip = "Space Marine install not found (the pin needs the player's install; never in CI)";
        }
    }

    [InstallFact]
    public void The_multiplayer_wargear_with_the_pistol_animation_set_is_the_bolt_pistols_and_the_true_grit_bolters()
    {
        var sets = new SortedDictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        using (var archive = ObpkArchive.Open(Path.Combine(Game.Value!, GameplayTuning.WargearPackage)))
            archive.ReadEntries(e => archive.GroupName(e) == "attr_pc" && (e.FileName.StartsWith("sm_mp_", StringComparison.OrdinalIgnoreCase) || e.FileName.StartsWith("csm_mp_", StringComparison.OrdinalIgnoreCase)),
                (e, d) => { if (BafDocument.HasMagic(d)) sets[e.FileName] = MpClasses.AnimationSetOf(BafDocument.Read(d)); });
        Assert.True(sets.Count > 20, $"only {sets.Count} multiplayer wargear files read");

        var pistolSet = sets.Where(kv => kv.Value == "pistol").Select(kv => kv.Key).ToArray();
        string[] expected =
        [
            "csm_mp_pistol_bolt_pistol", "csm_mp_pistol_bolt_pistol_devastator", "csm_mp_pistol_bolter", "csm_mp_pistol_bolter_gold",
            "sm_mp_pistol_bolt_pistol", "sm_mp_pistol_bolt_pistol_devastator", "sm_mp_pistol_bolter", "sm_mp_pistol_bolter_gold",
        ];
        Assert.Equal(expected.Order(StringComparer.OrdinalIgnoreCase), pistolSet.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

        // The client's name rule agrees with the data for every multiplayer file: a pistol by "pistol" is a bolt pistol or a True Grit bolter by name,
        // and no True Grit bolter name lacks it (the plasma pistols name their own "plasmapistol" set and are pistols by name: both rules agree).
        foreach (var (name, set) in sets)
        {
            bool byName = Loadouts.IsPistolBolterName(name) || name.Contains("bolt_pistol", StringComparison.OrdinalIgnoreCase);
            Assert.True(byName == (set == "pistol"), $"{name}: animation_set '{set}', name rule {(byName ? "pistol" : "not pistol")}");
        }
    }
}
