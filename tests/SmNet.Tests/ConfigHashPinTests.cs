using SmContent;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// The match configs of the player's install, by config hash, for the faithful and the extended profiles (W0, Musou's arsenal base, docs/new-weapons-design.md section 8: the lead's
/// "Classic and the faithful build byte-for-byte unchanged"). Goldens captured on main 61c152b7, before W0's hooks; a Musou-only change must never move them. Core folder, so it also runs
/// in <c>faithful-build.cmd</c> (the extended profile only where added content is compiled).
/// <para>
/// An install fact: the real match configs come from the player's install, so it is skipped without one (never in CI). The same pins without the install, as plain facts that run in CI,
/// are <c>MusouIsolationTests</c>' (a synthetic config and the default config, faithful and extended, captured before any Musou hook) and <c>ContentProfileTests</c>' (a synthetic golden
/// config); this test adds the install's own configs, which only a machine with the install can build. Musou's own hash moves with every Musou rules version, so it is not pinned here;
/// <c>ArsenalBaseTests</c> (SmFormats.Tests) checks that <c>Arsenal.Append</c> returns a Musou config itself while every arsenal entry is a stub.
/// </para>
/// </summary>
public class ConfigHashPinTests(ITestOutputHelper output)
{
    /// <summary>The faithful profile's match config (Annihilation and Seize Ground, no added content), captured on main 61c152b7.</summary>
    const ulong FaithfulHash = 0x5277E16C8E7F6260UL;

    /// <summary>The extended profile's (the Ork, gore, the loyalist barks; the playtest default), captured on main 61c152b7.</summary>
    const ulong ExtendedHash = 0x2AAB9E068C454594UL;   // the VM's Classic server reports the same

    static readonly Lazy<string?> Game = new(() => GameInstall.TryLocate()?.Dir);

    [ContentProfileTests.InstallFact]
    public void The_faithful_and_extended_match_configs_hash_as_before()
    {
        string game = Game.Value!;
        ulong faithful = GameplayTuning.LoadForMatch(game, ContentExtras.None).Config.Hash();
        output.WriteLine($"faithful 0x{faithful:X16}UL");
        ulong? extended = null;
        if (ContentExtras.Compiled)
        {
            Assert.True(ContentProfiles.TryParse(ContentProfiles.Extended, out var flags, out _));
            extended = GameplayTuning.LoadForMatch(game, flags).Config.Hash();
            output.WriteLine($"extended 0x{extended:X16}UL");
        }
        Assert.Equal(FaithfulHash, faithful);
        if (extended is { } e) Assert.Equal(ExtendedHash, e);
    }
}
