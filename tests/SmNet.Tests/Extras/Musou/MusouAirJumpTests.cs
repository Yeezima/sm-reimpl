using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Musou;

/// <summary>
/// sim/air-jumps (owner, 2026-11-06: "only in musou mode should the assault marine/raptor have extra/infinite jumps"): Musou Mode keeps the jump-pack classes' full
/// jump in the air (feel/melee-cancels, protocol 54: a Space hold while falling, after an apex, in a charge held in the air); Classic PvP does not (MeleeCancelTests).
/// </summary>
public class MusouAirJumpTests(ITestOutputHelper output)
{
    static readonly SimConfig Musou = MeleeCancelTests.Pack with { Extras = ContentExtras.Musou };

    [Fact]
    public void Only_musou_allows_the_air_jump()
    {
        Assert.True(Musou.JumpPackAirJumps);
        Assert.False(MeleeCancelTests.Pack.JumpPackAirJumps);
        Assert.False((MeleeCancelTests.Pack with { Extras = ContentExtras.Orks | ContentExtras.Gore | ContentExtras.LoyalistBarks }).JumpPackAirJumps);   // extended: Classic PvP with extras
    }

    [Fact]
    public void In_musou_holding_space_while_falling_starts_the_jump_pack()
    {
        var phases = MeleeCancelTests.HoldWhileFalling(Musou, out float vz);
        output.WriteLine(string.Join(" ", phases));
        Assert.Contains(JumpPhase.Ascent, phases);
        Assert.True(vz > 3f, $"launch vz {vz}");
    }

    [Fact]
    public void In_musou_a_second_full_jump_follows_the_apex_and_the_hop_still_works()
    {
        var (phases, hopped) = MeleeCancelTests.JumpThenAirPresses(Musou);
        output.WriteLine(string.Join(" ", phases));
        Assert.True(MeleeCancelTests.Launches(phases) >= 2, "the ground jump and one in the air");
        Assert.True(hopped);
    }

    [Fact]
    public void In_musou_space_in_a_charge_held_in_the_air_jumps() =>
        Assert.Contains(JumpPhase.Ascent, MeleeCancelTests.ChargeHeldInTheAir(Musou));
}
