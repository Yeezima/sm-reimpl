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
    // executions X1 (2026-11-25): only All moves (the Executions flag and its block; was 0xD3D3D5F4E244F644UL default, 0xE4DA81CF210372A5UL golden); None, Musou, Orks and Autocannon unchanged.
    // executions X4b (2026-11-26): only All moves again (ExecutionRules.Version 4, the choice's rules version 3; was 0x2D6F77197B9D1D0EUL default, 0x9A898BAE74506DA3UL golden).
    // executions X3 (2026-11-25): only All moves again (rules version 2, the choice's rules version 2, the Classic slide cap, the rows' wire order; was 0x3B6DD00CD4831F41UL default, 0x8F9FE72D5DA84CFAUL golden).
    static readonly ulong[] DefaultHashes = [0x6F2F6B2B1A7A41F0UL, 0xDDE054AF69FDCB76UL, 0x4F982DFE8D7DFB35UL, 0x7D67C86AF2876E46UL, 0x78B229870916C651UL];   // sim/stab-key-only (2026-11-23): the Classic profiles move with the cancel table's stab section (was 0xAA40C2EE9AE0EE1DUL, 0x060CA7F5E6F22ADAUL, -, 0xA10EA8713099B3ACUL, 0xF191506BEDCC889AUL); Musou unchanged
    static readonly ulong[] GoldenHashes = [0x97D7B66E9373C943UL, 0x219524FB705C9C1DUL, 0x34C7A7A7F47F1EBCUL, 0x0FA1B3FD7ACCB8FFUL, 0x32F6578236623CB6UL];   // sim/stab-key-only (2026-11-23; was 0x3BFCC79FC8AFA398UL, 0xE5E891D63F520B73UL, -, 0xD738DD13CE0294F0UL, 0x0150161B1A80E701UL); Musou unchanged

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
