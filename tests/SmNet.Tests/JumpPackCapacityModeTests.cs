using SmSim;

namespace SmNet.Tests;

/// <summary>
/// Night batch 2026-11-06 (owner: "in classic pvp mode return the maximum jump pack fuel capacity to exactly the same as the original game"): Classic PvP's pack holds the
/// data's <c>max_power</c> (100), Musou Mode keeps the owner's 300 (feel/jump-pack). Synthetic spec: MaxPower 300 with DataMaxPower 100, as the content reader builds it.
/// </summary>
public class JumpPackCapacityModeTests
{
    static readonly SimConfig Classic = JumpPackDashTests.Config with { JumpPacks = [JumpPackDashTests.Config.JumpPacks[0] with { MaxPower = 300f, DataMaxPower = 100f }] };
    static readonly SimConfig Musou = Classic with { Extras = ContentExtras.Musou };

    [Fact]
    public void Classic_pvp_holds_the_data_capacity_and_spawns_full()
    {
        Assert.False(Classic.JumpPackOwnerCapacity);
        Assert.Equal(100f, Loadouts.JumpPack(Classic, 0)!.MaxPower);
        var s = Combat.Respawned(new PlayerState(1, default, default, 0f, true) { Class = 0 }, Classic);
        Assert.Equal(100f, s.JumpPower);
        Assert.Equal(100f, Loadouts.JumpPack(Classic, s)!.MaxPower);
        Assert.False((Classic with { Extras = ContentExtras.Orks | ContentExtras.Gore }).JumpPackOwnerCapacity);   // extended Classic PvP too
    }

    [Fact]
    public void Musou_mode_keeps_the_owner_capacity()
    {
        Assert.True(Musou.JumpPackOwnerCapacity);
        Assert.Equal(300f, Loadouts.JumpPack(Musou, 0)!.MaxPower);
        var s = Combat.Respawned(new PlayerState(1, default, default, 0f, true) { Class = 0 }, Musou);
        Assert.Equal(300f, s.JumpPower);
    }

    [Fact]
    public void A_pack_without_data_capacity_is_left_alone()
    {
        var c = Classic with { JumpPacks = [Classic.JumpPacks[0] with { DataMaxPower = 0f }] };
        Assert.Equal(300f, Loadouts.JumpPack(c, 0)!.MaxPower);
    }
}
