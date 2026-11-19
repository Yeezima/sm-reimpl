using System.Numerics;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Warfront.Pins;

/// <summary>
/// W0 pins (Warfront): the config hash of the default config and of a synthetic golden config for five content profiles, captured on the untouched baseline (commit 5428594e). The golden config is
/// a copy of <c>ContentProfileTests</c>' (do not change it: the hashes depend on every value). The two faithful (None) values equal that class's own literals.
/// </summary>
public class ConfigHashPins(ITestOutputHelper output)
{
    static readonly WeaponSpec Rifle = new("rifle", WeaponSlot.Ranged, TriggerKind.FullAuto, 20f, 0.15f, 30, 270, 0.5f, 250f, 245f, 250f, 1f, 2.5f, 1f, 0.5f, 0.5f);
    static readonly WeaponSpec Pistol = new("pistol", WeaponSlot.Ranged, TriggerKind.SemiAuto, 45f, 0.3f, 8, 64, 1.2f, 120f, 30f, 60f, 0.5f, 3f, 1f, 0.6f, 0.6f);
    static EvadeMove Move(string name, Vector2 dir) => new(name, Enumerable.Range(0, 11).Select(i => dir * (0.4f * i)).ToArray(), 1f, 0.6f, 0.6f, 0.7f, 0.7f, 1f);
    static readonly EvadeSpec Roll = new("roll", Move("F", Vector2.UnitY), Move("B", -Vector2.UnitY), Move("L", -Vector2.UnitX), Move("R", Vector2.UnitX)) { MinInterval = 0.8f };
    static ClassLoadout Class(string name, Team side, string role) =>
        new(name, 0, 1, SimConfig.PlaceholderMeleeSteps) { PrimaryChoices = [0], PistolChoices = [1], Side = side, Role = role, Evade = 0 };

    static readonly SimConfig Golden = new()
    {
        Weapons = [Rifle, Pistol],
        Classes = [Class("sm - tactical", Team.SpaceMarines, "tactical"), Class("csm - tactical", Team.Chaos, "tactical")],
        Evades = [Roll],
        HitVolumes = [HitVolume.Sphere("Head", HitZone.Head, new(0f, 0f, 1.9f), 0.2f), HitVolume.Capsule("Spine", HitZone.Body, new(0f, 0f, 0.4f), new(0f, 0f, 1.5f), 0.35f)],
        RespawnTime = 4f,
    };

    static readonly ContentExtras[] Profiles = [ContentExtras.None, ContentExtras.All, ContentExtras.Musou, ContentExtras.Orks, ContentExtras.Autocannon];

    // Captured on 5428594e and recaptured on 2026-11-19 on main 1809fa31 (main's own changes moved them; the same five values come out of this tree and of an untouched main, which is the proof that Warfront changes no Classic or Musou hash): index order None, All, Musou, Orks, Autocannon.
    static readonly ulong[] DefaultHashes = [0x6CF1391DEE14706DUL, 0x85685DCCFA9091C1UL, 0x4F982DFE8D7DFB35UL, 0x269BE5D81FAD7628UL, 0x23513B5EBB9CC435UL];
    static readonly ulong[] GoldenHashes = [0x76BEC6D1DE5B69CFUL, 0x0BB19A7F7AEB1E8DUL, 0x34C7A7A7F47F1EBCUL, 0x65A6E84CCA3BC06EUL, 0xC4710FBD0987DBD1UL];

    static ulong[] HashesOf(SimConfig config) => [.. Profiles.Select(p => (config with { Extras = p }).Hash())];

    [Fact]
    public void The_default_config_hashes_as_before_Warfront_for_each_profile()
    {
        var actual = HashesOf(new SimConfig());
        output.WriteLine($"default (None, All, Musou, Orks, Autocannon): {string.Join(", ", actual.Select(h => $"0x{h:X16}UL"))}");
        Assert.Equal(DefaultHashes, actual);
    }

    [Fact]
    public void The_golden_config_hashes_as_before_Warfront_for_each_profile()
    {
        var actual = HashesOf(Golden);
        output.WriteLine($"golden (None, All, Musou, Orks, Autocannon): {string.Join(", ", actual.Select(h => $"0x{h:X16}UL"))}");
        Assert.Equal(GoldenHashes, actual);
    }
}
