using SmNet;
using SmSim;

namespace SmNet.Tests;

/// <summary>
/// fix/dead-melee-direction: a player who died during a directional melee attack kept <see cref="PlayerState.MeleeDirection"/> with no attack, the reader refuses that own record,
/// and every snapshot to that player was thrown away until the client gave up after 5 s ("Disconnected (no snapshot from the server)").
/// </summary>
public class DeadSnapshotTests
{
    static PlayerState Attacking(EvadeDirection direction) =>
        new PlayerState(1, default, default, 0f, true) { Class = 0, Primary = 0, Pistol = 1, MeleeChoice = 0, MeleeStep = 1, MeleeTime = 0.1f, MeleeDirection = direction };

    static PlayerState RoundTrip(PlayerState s) => Assert.IsType<Snapshot>(Protocol.Read(Protocol.Write(new Snapshot(1, 1, 1, [s], Self: 1)))).Players[0];

    public static TheoryData<EvadeDirection> Directions()
    {
        var data = new TheoryData<EvadeDirection>();
        foreach (var d in Enum.GetValues<EvadeDirection>()) data.Add(d);
        return data;
    }

    [Theory, MemberData(nameof(Directions))]
    public void Death_during_a_directional_attack_ends_the_direction(EvadeDirection direction)
    {
        var dead = Combat.Killed(Attacking(direction), new SimConfig());
        Assert.Equal(EvadeDirection.Forward, dead.MeleeDirection);
        Assert.False(RoundTrip(dead).Alive);
    }

    [Theory, MemberData(nameof(Directions))]
    public void A_record_with_a_direction_but_no_attack_on_the_wire_stays_readable(EvadeDirection direction)
    {
        var attacking = Attacking(direction);
        Assert.Equal(EvadeDirection.Forward, RoundTrip(attacking with { MeleeStep = 0, MeleeTime = 0f }).MeleeDirection);    // no attack
        Assert.Equal(EvadeDirection.Forward, RoundTrip(attacking with { Reaction = ReactionKind.Stun, ReactionTicks = 3 }).MeleeDirection);    // a reaction takes the melee field
        Assert.Equal(EvadeDirection.Forward, RoundTrip(attacking with { Rush = RushPhase.Rush, RushTicks = 3 }).MeleeDirection);   // the charge too
    }

    [Theory, MemberData(nameof(Directions))]
    public void A_real_attack_keeps_its_direction(EvadeDirection direction) => Assert.Equal(direction, RoundTrip(Attacking(direction)).MeleeDirection);
}
