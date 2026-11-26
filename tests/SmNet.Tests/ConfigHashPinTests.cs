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
    const ulong FaithfulHash = 0x7C6D16FC36F573EAUL;   // recaptured with sim/hammer-180 (2026-11-25: the thunder hammer's and the maul's Dir 180 strikes, light 4 to 7, their turn strikes at the clips' own motion; 0xAD337E7E63CC8698UL before); earlier: recaptured with sim/stab-key-only (2026-11-23: the sword's F + F finisher is the stomp again, the stab its own strike, the cancel table's stab section; 0xD289435C8262FCEFUL before); earlier: recaptured with the owner's cancel default (2026-11-19; again 2026-11-23, the 11-23 export) (the shipped cancel table's hash joins every Classic config hash; Musou's does not) (0x373BFF6EB6DBB857UL before); earlier: recaptured with classic-stab-on-t (2026-11-17: MeleeStep.Stab on the sword's stab finisher; 0x40E5ADF50CD19EBDUL before); earlier: sim/sword-ff-stab (2026-11-14): the Classic power sword's F + F finisher is the X,F thrust at Furious Charge damage (0x5277E16C8E7F6260 before)

    /// <summary>
    /// The extended profile's (the Ork, gore, the loyalist barks and, since claude/autocannon-pass of 2026-11-11, Classic PvP's autocannon; the playtest default): recaptured with the autocannon's flag and
    /// its weapon appended to the Devastator's and the Havoc's lists (0x2AAB9E068C454594 before, captured on main 61c152b7). A ship with it needs the VM's Classic server updated.
    /// Recaptured with claude/autocannon-pass-2 (2026-11-11): the Classic autocannon's clip of 120 (the owner's "120 clip size", 400 before) and the drops' amount for it (the heavy bolter's)
    /// (0x7556F708CE2FD9A2 before).
    /// Recaptured with claude/autocannon-accuracy (2026-11-14): the Classic autocannon's scatter is the Stalker's (0x479768E19E322CCA before).
    /// </summary>
    const ulong ExtendedHash = 0xA66493C16F87F872UL;   // recaptured with the execution cues' crossover rows (2026-11-27: ExecutionRules.Version 5, ExecutionRows.DataVersion 3; 0x20280DD523DCB96DUL before); recaptured with executions X4b (2026-11-26: ExecutionRules.Version 4, ExecutionChoice.RulesVersion 3; 0x5468003C5990A7B9UL before); earlier: recaptured at the series-37 integration (2026-11-25: sim/hammer-180 gave 0x789B769536191C15UL and executions X1-X4 gave 0x14958A774371F1FCUL, each from 0xE68D320C1848BBD4UL); earlier: recaptured with executions X4 (rules version 3), before that with executions X3 (2026-11-25: rules version 2, the choice's rules version 2, the Classic slide cap and the rows' wire order; 0xD90E88DB764CA355UL before); earlier: recaptured with executions X1 (2026-11-25: the Executions flag and its hash block; 0xE68D320C1848BBD4UL before); earlier: recaptured with sim/stab-key-only (2026-11-23; 0x13BC162F035A3D07UL before); earlier: recaptured with the owner's cancel default (2026-11-19; again 2026-11-23, the 11-23 export) (the shipped cancel table's hash joins every Classic config hash; Musou's does not) (0xC5456B1648DE53E7UL before); earlier: recaptured with classic-stab-on-t (2026-11-17; 0x25C0E0FF4304DC7EUL before); earlier: sim/sword-ff-stab (2026-11-14) (0x4470226340FFAB58 before)

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
