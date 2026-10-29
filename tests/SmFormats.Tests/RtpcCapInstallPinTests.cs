using SmContent;
using SmFormats.Audio;

namespace SmFormats.Tests;

/// <summary>
/// sound/footsteps-2 review: pins which nodes of the multiplayer banks the RTPC volume cap (<see cref="RtpcVolumeCap"/>) changes, read from the player's install
/// (read-only, no media loaded; install build 7412325). Only node IDs are compared, no original bytes. Skipped when no install is found (CI).
/// </summary>
public class RtpcCapInstallPinTests
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
    public void Only_these_nodes_have_volume_curves_above_0_dB()
    {
        string dir = Path.Combine(Game.Value!, "streamingaudio");
        using var lib = WwiseLibrary.Open(dir, AudioCatalog.PackagesFor(dir, "mp_tdm_09"), out _);
        var nodes = lib.Banks.SelectMany(b => b.Bank.Objects).OfType<HircNode>().DistinctBy(n => n.Id).ToList();
        Assert.True(nodes.Count > 29_000, $"{nodes.Count} nodes: not the full multiplayer set");
        // The convention the cap rests on: no node's own volume is above 0 dB.
        Assert.Equal(0f, nodes.Max(n => n.Base.Volume.Base));

        var positive = nodes.Where(n => n.Base.Rtpcs.Any(r => r.ParamId == 0 && r.Points.Count > 0 && r.Points.Max(p => p.Y) > 0f))
            .Select(n => n.Id).Order().ToArray();
        // Peak mode shifts every one of these curves: the footstep body switch 33fc8aae and its twin 0e8a0bb4, the sole switches 2267bf67 / 1989adf7 /
        // 3fa5e622 / 10747387, the Ork grenade fuse 07d5afc8, the Ork nob footstep 3a4aa71a and the Dreadnought's 28161f71 / 358b32f6.
        Assert.Equal(new uint[] { 0x07d5afc8, 0x0e8a0bb4, 0x10747387, 0x1989adf7, 0x2267bf67, 0x28161f71, 0x33fc8aae, 0x358b32f6, 0x3a4aa71a, 0x3fa5e622 }, positive);

        // Node mode changes only those whose own volume plus the curve's top goes above 0 (the last three stay under it).
        var nodeMode = nodes.Where(n => positive.Contains(n.Id) && n.Base.Rtpcs.Where(r => r.ParamId == 0 && r.Points.Count > 0)
                .Sum(r => r.Points.Max(p => p.Y)) + n.Base.Volume.Base > 0f)
            .Select(n => n.Id).Order().ToArray();
        Assert.Equal(new uint[] { 0x07d5afc8, 0x0e8a0bb4, 0x10747387, 0x1989adf7, 0x2267bf67, 0x33fc8aae, 0x3fa5e622 }, nodeMode);
    }
}
