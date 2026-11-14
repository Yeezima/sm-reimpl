using SmSim.Extras.Musou;
using Xunit;

namespace SmNet.Tests.Extras.Musou;

/// <summary>sim/kick-push (owner 2026-11-14): the Devastator's and Havoc's kick pushes 40 % as far in Classic; Musou's enemies keep their own knockback constants
/// (moved here from <c>KickPushTests</c> at integration: a core test file may not name an Extras namespace, <c>ExtrasBoundaryTests</c>).</summary>
public class KickPushMusouTests
{
    [Fact]
    public void Musou_enemies_keep_their_own_knockback()
    {
        Assert.Equal(2f, new MusouCombatRules().BlastKnockback);   // Musou's enemies take rules' constants, never the step's reaction entry
    }
}
