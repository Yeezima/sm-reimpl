using System.Numerics;
using SmContent;
using SmNet;
using SmSim;

namespace SmNet.Tests;

/// <summary>
/// Added content E1 (docs/added-content.md): the content switch. A faithful config (<see cref="ContentExtras.None"/>) hashes byte for byte as
/// before the switch existed (golden hashes captured on main a4f560c before the change); every extras flag changes the hash, so a server and a
/// client with different profiles refuse each other, and the refusal names the server's profile; <c>--content</c> parsing; the faithful match
/// tuning has no Ork class.
/// </summary>
public class ContentProfileTests
{
    static readonly WeaponSpec Rifle = new("rifle", WeaponSlot.Ranged, TriggerKind.FullAuto, 20f, 0.15f, 30, 270, 0.5f, 250f, 245f, 250f, 1f, 2.5f, 1f, 0.5f, 0.5f);
    static readonly WeaponSpec Pistol = new("pistol", WeaponSlot.Ranged, TriggerKind.SemiAuto, 45f, 0.3f, 8, 64, 1.2f, 120f, 30f, 60f, 0.5f, 3f, 1f, 0.6f, 0.6f);
    static EvadeMove Move(string name, Vector2 dir) => new(name, Enumerable.Range(0, 11).Select(i => dir * (0.4f * i)).ToArray(), 1f, 0.6f, 0.6f, 0.7f, 0.7f, 1f);
    static readonly EvadeSpec Roll = new("roll", Move("F", Vector2.UnitY), Move("B", -Vector2.UnitY), Move("L", -Vector2.UnitX), Move("R", Vector2.UnitX)) { MinInterval = 0.8f };
    static ClassLoadout Class(string name, Team side, string role) =>
        new(name, 0, 1, SimConfig.PlaceholderMeleeSteps) { PrimaryChoices = [0], PistolChoices = [1], Side = side, Role = role, Evade = 0 };

    /// <summary>A synthetic config with weapons, two side classes, an evade and hit volumes (no install). Do not change it: its hash is pinned.</summary>
    static readonly SimConfig Golden = new()
    {
        Weapons = [Rifle, Pistol],
        Classes = [Class("sm - tactical", Team.SpaceMarines, "tactical"), Class("csm - tactical", Team.Chaos, "tactical")],
        Evades = [Roll],
        HitVolumes = [HitVolume.Sphere("Head", HitZone.Head, new(0f, 0f, 1.9f), 0.2f), HitVolume.Capsule("Spine", HitZone.Body, new(0f, 0f, 0.4f), new(0f, 0f, 1.5f), 0.35f)],
        RespawnTime = 4f,
    };

    /// <summary>Captured on main a4f560c before E1 (the same config, the same code path, no <see cref="SimConfig.Extras"/> yet).</summary>
    const ulong GoldenHash = 0x6696D7978B3D933CUL, DefaultHash = 0xA57B097C29D78EABUL;

    // ---------------------------------------------------------------- the hash

    [Fact]
    public void A_faithful_config_hashes_as_before_the_switch()
    {
        Assert.Equal(ContentExtras.None, Golden.Extras);   // the default
        Assert.Equal(GoldenHash, Golden.Hash());
        Assert.Equal(GoldenHash, (Golden with { Extras = ContentExtras.None }).Hash());
        Assert.Equal(DefaultHash, new SimConfig().Hash());
        Assert.Equal(DefaultHash, new SimConfig { Extras = ContentExtras.None }.Hash());
    }

    [Fact]
    public void Every_extras_flag_changes_the_hash()
    {
        ulong[] hashes =
        [
            Golden.Hash(),
            (Golden with { Extras = ContentExtras.Orks }).Hash(),
            (Golden with { Extras = ContentExtras.Gore }).Hash(),
            (Golden with { Extras = ContentExtras.Orks | ContentExtras.Gore }).Hash(),
        ];
        Assert.Equal(hashes.Length, hashes.Distinct().Count());
        // The flags are hashed alone (the rest of the config as before): equal configs with equal flags hash alike.
        Assert.Equal(hashes[1], (Golden with { Extras = ContentExtras.Orks }).Hash());
        Assert.Equal(GoldenHash, (Golden with { Extras = ContentExtras.Gore } with { Extras = ContentExtras.None }).Hash());
    }

    [Fact]
    public void A_client_with_another_profile_is_refused_naming_the_servers_profile()
    {
        var link = new LinkConditions(TimeSpan.FromMilliseconds(20), TimeSpan.Zero, 0);
        // A faithful server: an extended client is refused, the refusal names "faithful"; a faithful client joins.
        var h = new Harness(link, seed: 1, clients: 0, Golden);
        var extended = h.AddClient(config: Golden with { Extras = ContentExtras.Orks | ContentExtras.Gore }, nonce: 1);
        var faithful = h.AddClient(nonce: 2);
        h.Run(TimeSpan.FromSeconds(1));
        Assert.Equal(ClientStatus.Rejected, extended.Status);
        Assert.Equal(RejectReason.ConfigMismatch, extended.RejectedBecause);
        Assert.Contains("content profile faithful", extended.RejectDetail);
        Assert.Contains("--content faithful", extended.RejectDetail);
        Assert.Equal(ClientStatus.Connected, faithful.Status);

        // An extended server: a faithful client is refused with the profile, its flags and the extras wire version.
        var e = new Harness(link, seed: 2, clients: 0, Golden with { Extras = ContentExtras.All });
        var plain = e.AddClient(config: Golden, nonce: 3);
        var same = e.AddClient(nonce: 4);
        e.Run(TimeSpan.FromSeconds(1));
        Assert.Equal(RejectReason.ConfigMismatch, plain.RejectedBecause);
        Assert.Contains($"content profile extended (orks,gore,loyalist-barks; extras wire {ContentExtras.WireVersion})", plain.RejectDetail);
        Assert.Contains("--content extended", plain.RejectDetail);
        Assert.Contains("--ork-data", plain.RejectDetail);   // the other usual cause (Ork O5 review)
        Assert.Equal(ClientStatus.Connected, same.Status);
    }

    [Fact]
    public void The_refusal_fits_the_reject_message()
    {
        // Protocol.Write cuts a Reject's detail at 200 characters: every profile's refusal must fit whole.
        for (uint flags = 0; flags <= (uint)ContentExtras.All; flags++)
            Assert.InRange(SmServer.GameServer.ConfigMismatchDetail((ContentExtras)flags).Length, 1, 200);
    }

    // ---------------------------------------------------------------- profiles

    [Theory]
    [InlineData("faithful", ContentExtras.None)]
    [InlineData(" Faithful ", ContentExtras.None)]
    [InlineData("orks", ContentExtras.Orks)]
    [InlineData("gore", ContentExtras.Gore)]
    [InlineData("orks,gore", ContentExtras.Orks | ContentExtras.Gore)]
    [InlineData("GORE, Orks", ContentExtras.Orks | ContentExtras.Gore)]
    [InlineData("orks,orks", ContentExtras.Orks)]
    public void Profiles_parse(string text, ContentExtras expected)
    {
        bool ok = ContentProfiles.TryParse(text, out var extras, out string error);
        if (expected != ContentExtras.None && !ContentExtras.Compiled)
        {
            // A build without extras (faithful-build.cmd) refuses any added content.
            Assert.False(ok);
            Assert.Contains("SmExtras=false", error);
            Assert.Equal(ContentExtras.None, extras);
            return;
        }
        Assert.True(ok, error);
        Assert.Equal("", error);
        Assert.Equal(expected, extras);
        // The name parses back to the same flags.
        Assert.True(ContentProfiles.TryParse(ContentProfiles.Name(extras), out var again, out _));
        Assert.Equal(extras, again);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("none")]
    [InlineData("orks,")]
    [InlineData(",gore")]
    [InlineData("elves")]
    [InlineData("orks,elves")]
    public void Bad_profiles_are_refused(string? text)
    {
        Assert.False(ContentProfiles.TryParse(text, out var extras, out string error));
        Assert.Equal(ContentExtras.None, extras);
        Assert.StartsWith("--content", error);
        Assert.Contains("faithful", error);
    }

    [Fact]
    public void Extended_is_every_compiled_extra_and_the_default()
    {
        Assert.Equal(ContentExtras.Orks | ContentExtras.Gore | ContentExtras.LoyalistBarks, ContentExtras.All);
        Assert.NotEqual(default, ContentExtras.ActionKeyUsers & ContentExtras.Musou);   // T shouts in Musou Mode
        Assert.Equal(ContentProfiles.Extended, ContentProfiles.DefaultName);   // the owner's default (2026-10-28): a one-line change
        bool ok = ContentProfiles.TryParse("extended", out var extended, out string error);
        if (ContentExtras.Compiled)
        {
            Assert.True(ok, error);
            Assert.Equal(ContentExtras.All, extended);
            Assert.Equal(ContentExtras.All, ContentProfiles.Default);
        }
        else
        {
            Assert.False(ok);
            Assert.Contains("only --content faithful", error);
            Assert.Equal(ContentExtras.None, ContentProfiles.Default);   // a faithful build runs faithful without --content
        }
        Assert.Equal("faithful", ContentProfiles.Name(ContentExtras.None));
        Assert.Equal("extended", ContentProfiles.Name(ContentExtras.All));
        Assert.Equal("orks", ContentProfiles.Name(ContentExtras.Orks));
        Assert.Equal("gore", ContentProfiles.Name(ContentExtras.Gore));
        Assert.Equal("faithful", ContentProfiles.Describe(ContentExtras.None));
        Assert.Equal($"orks (orks; extras wire {ContentExtras.WireVersion})", ContentProfiles.Describe(ContentExtras.Orks));
    }

    // ---------------------------------------------------------------- Musou (MU0: a mode of its own, never in extended)

    [Fact]
    public void Musou_is_its_own_profile_never_part_of_extended()
    {
        Assert.Equal(8u, (uint)ContentExtras.Musou);
        Assert.False(ContentExtras.All.HasFlag(ContentExtras.Musou));
        Assert.Equal(ContentExtras.All | ContentExtras.Musou, ContentExtras.Known);
        Assert.Equal("musou", ContentProfiles.Musou);
        Assert.Equal("musou", ContentProfiles.Name(ContentExtras.Musou));
        Assert.Equal($"musou (musou; extras wire {ContentExtras.WireVersion})", ContentProfiles.Describe(ContentExtras.Musou));
        Assert.Contains("musou", ContentProfiles.Expected);
        bool ok = ContentProfiles.TryParse(" Musou ", out var musou, out string error);
        if (ContentExtras.Compiled)
        {
            Assert.True(ok, error);
            Assert.Equal(ContentExtras.Musou, musou);
            Assert.True(ContentProfiles.TryParse("extended", out var extended, out _));
            Assert.False(extended.HasFlag(ContentExtras.Musou));
            Assert.False(ContentProfiles.Default.HasFlag(ContentExtras.Musou));
        }
        else
        {
            Assert.False(ok);
            Assert.Contains("SmExtras=false", error);
            Assert.Equal(ContentExtras.None, musou);
        }
        Assert.InRange(SmServer.GameServer.ConfigMismatchDetail(ContentExtras.Musou).Length, 1, 200);   // the refusal fits the reject message
    }

    [Theory]
    [InlineData("musou,orks")]
    [InlineData("orks,musou")]
    [InlineData("musou,gore,loyalist-barks")]
    [InlineData("musou,musou,gore")]
    public void Musou_with_any_other_flag_is_refused(string text)
    {
        Assert.False(ContentProfiles.TryParse(text, out var extras, out string error));
        Assert.Equal(ContentExtras.None, extras);
        Assert.StartsWith("--content", error);
        Assert.Contains("musou", error);
    }

    [Fact]
    public void Musou_hashes_apart_from_every_other_profile()
    {
        ulong musou = (Golden with { Extras = ContentExtras.Musou }).Hash();
        Assert.NotEqual(GoldenHash, musou);
        Assert.NotEqual((Golden with { Extras = ContentExtras.All }).Hash(), musou);
        Assert.NotEqual((Golden with { Extras = ContentExtras.LoyalistBarks }).Hash(), musou);
        Assert.Equal(musou, (Golden with { Extras = ContentExtras.Musou }).Hash());
    }

    // ---------------------------------------------------------------- the Ork gate (needs the player's install; skipped without it)

    static readonly Lazy<string?> Game = new(() => GameInstall.TryLocate()?.Dir);

    /// <summary>A fact that is skipped unless the game install is found on this machine (never in CI).</summary>
    public sealed class InstallFactAttribute : FactAttribute
    {
        public InstallFactAttribute()
        {
            if (Game.Value is null) Skip = "Space Marine install not found (the Ork gate needs the player's install; never in CI)";
        }
    }

    [InstallFact]
    public void The_faithful_match_tuning_has_no_ork_and_hashes_as_the_plain_load()
    {
        string game = Game.Value!;
        var plain = GameplayTuning.Load(game).Config;
        var faithful = GameplayTuning.LoadForMatch(game, ContentExtras.None).Config;
        Assert.DoesNotContain(faithful.Classes, c => c.Race == Race.Orks || c.Name.StartsWith("ork", StringComparison.Ordinal));
        Assert.Empty(faithful.Bodies);
        Assert.Equal(ContentExtras.None, faithful.Extras);
        Assert.Equal(plain.Hash(), faithful.Hash());

        var orks = GameplayTuning.LoadForMatch(game, ContentExtras.Orks).Config;
        Assert.Contains(orks.Classes, c => c.Race == Race.Orks);
        Assert.Equal(ContentExtras.Orks, orks.Extras);
        Assert.NotEqual(faithful.Hash(), orks.Hash());
        // Without its flags the Ork config is the O5 match config exactly (Load with MatchOrk): the flags are all E1 adds.
        Assert.Equal(GameplayTuning.Load(game, GameplayTuning.MatchOrk()).Config.Hash(), (orks with { Extras = ContentExtras.None }).Hash());

        // Gore alone: no Ork class either, but another hash than faithful.
        var gore = GameplayTuning.LoadForMatch(game, ContentExtras.Gore).Config;
        Assert.DoesNotContain(gore.Classes, c => c.Race == Race.Orks);
        Assert.NotEqual(faithful.Hash(), gore.Hash());
    }
}
