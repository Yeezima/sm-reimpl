using SmContent;
using Xunit;

namespace SmFormats.Tests;

public class ChaosModelTests
{
    // The M17 class-name mapping ("csm - havoc" -> havoc_01) is gone (owner, 2026-10-28): a class's look is its data default, see
    // CustomizationProfileTests.A_chaos_havocs_and_raptors_default_look_follows_the_data_not_the_class_name.

    [Fact]
    public void ArmourFilesAreOnlyThosePresent()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sm-chaos-" + Guid.NewGuid().ToString("N"));
        string packages = Path.Combine(dir, "data", "packages");
        Directory.CreateDirectory(packages);
        try
        {
            foreach (var part in new[] { "torso", "helmet", "legl" })
                File.WriteAllBytes(Path.Combine(packages, $"csm_armour_mp_raptor_01_{part}.oppc"), []);
            File.WriteAllBytes(Path.Combine(packages, "csm_armour_mp_basic_torso.oppc"), []);
            var files = CharacterModel.ChaosArmourFiles(dir, "raptor_01").Select(Path.GetFileName).ToList();
            Assert.Equal(["csm_armour_mp_raptor_01_torso.oppc", "csm_armour_mp_raptor_01_legl.oppc", "csm_armour_mp_raptor_01_helmet.oppc"], files);
            Assert.Empty(CharacterModel.ChaosArmourFiles(dir, "havoc_01"));
            Assert.Throws<FileNotFoundException>(() => CharacterModel.LoadChaos(dir, "havoc_01"));
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class ChaosTexturePackageTests
{
    [Fact]
    public void TexturePackagesAreTheSiblingVariantsBestFirst()
    {
        var packages = CharacterModel.ChaosTexturePackages(Path.Combine("d", "csm_armour_mp_basic_torso.oppc")).Select(Path.GetFileName).ToList();
        Assert.Equal(["csm_armour_mp_basic_torso_wg.oppc", "csm_armour_mp_basic_torso_tt1.oppc", "csm_armour_mp_basic_torso_tt2.oppc"], packages);
    }
}
