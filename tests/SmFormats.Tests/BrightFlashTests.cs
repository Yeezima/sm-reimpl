using SmContent;

namespace SmFormats.Tests;

/// <summary>musou/autocannon-flash (owner 2026-11-15): which sprites of the autocannon's hit and blast effects the flash-only draw keeps (names as in the data; no original bytes).</summary>
public class BrightFlashTests
{
    [Theory]
    [InlineData(true, false, "1 frame flash", true)]
    [InlineData(true, false, "ground flash", true)]
    [InlineData(true, false, "flash", true)]
    [InlineData(true, false, "streaks", true)]
    [InlineData(true, false, "fire", false)]
    [InlineData(true, false, "flare", false)]
    [InlineData(true, false, "hot cloud", false)]
    [InlineData(true, false, "inner fire", false)]
    [InlineData(false, false, "flash", false)]        // an alpha sprite named flash is not a flash
    [InlineData(true, true, "flash", false)]          // a distortion sprite never
    [InlineData(false, false, "smoke ball", false)]
    [InlineData(false, false, "mid debris", false)]
    public void Only_additive_flash_and_streak_sprites_stay(bool additive, bool distortion, string name, bool kept) =>
        Assert.Equal(kept, BrightFlash.Keeps(additive, distortion, name));

    [Fact]
    public void The_autocannon_hit_and_blast_draw_only_the_flash_and_an_Ork_hit_keeps_blood_not_smoke()
    {
        string root = Root();
        string fx = File.ReadAllText(Path.Combine(root, "client", "NetworkView.Fx.cs"));
        Assert.Contains("if (dust == 0f) impact.BrightFlash = true;", fx);   // the world and body hit through FxShot
        Assert.Contains("played.BrightFlash = flashOnly;", fx);              // the blast
        string dust = File.ReadAllText(Path.Combine(root, "client", "Extras", "Musou", "Arsenal", "Autocannon", "NetworkView.AutocannonDust.cs"));
        Assert.Contains("flashOnly: true);", dust);                          // the terrain blast
        string blood = File.ReadAllText(Path.Combine(root, "client", "Extras", "Musou", "Gore", "NetworkView.OrkBlood.cs"));
        Assert.Contains("smoke: !NoShotSmoke(gun)", blood);                  // a bolt weapon's or the autocannon's hit on an Ork: the blood, no smoke cards
        Assert.Contains("noDust: NoShotGrit(gun, p.Name)", blood);
    }

    static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "SmReimpl.slnx"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("SmReimpl.slnx");
    }
}
