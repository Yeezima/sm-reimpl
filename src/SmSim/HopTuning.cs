namespace SmSim;

/// <summary>
/// sim/hop-rules (protocol 72; the owner, 2026-11-14, three requests for the jump-pack classes, Assault Marine and Raptor):
/// (1) "you should be able to full jump as normal when you are charging off of a ledge" (Classic PvP and Musou Mode): a Jump press that ends a bull rush held in the air
/// opens the air jump (<see cref="PlayerState.JumpAirOpen"/>) until the body lands or another jump-pack phase or a hop starts, so the long press (the hold past the hop's decision tick)
/// lifts the pack in the air as on the ground, in Classic PvP too (<see cref="SimConfig.JumpPackAirJumps"/> alone allowed it in Musou Mode);
/// (2) "melee swing > hop > melee swing has a delay ... keep the cooldown on light combo attack melee swings but make a hop reduce it": the start of a hop (the dash) cuts the melee wait still running
/// (<see cref="PlayerState.MeleeLock"/>) to <see cref="LockKeep"/> of what is left, never below <see cref="LockFloor"/> (<see cref="HopLock"/>), so swing > hop > swing flows but hop-spamming cannot beat the
/// hop's own spacing nor drop the wait under the floor; (3) "aerial tackle/flying tackle should cancel a hop": a Jump press in any phase of the dash attack ends it and starts the hop in the same tick, past the
/// hop's spacing (<see cref="SimConfig.HopRules"/>). Every number is ours.
/// </summary>
public static class HopTuning
{
    /// <summary>(2) The share of the melee wait still running that a hop leaves (ours: half).</summary>
    public const float LockKeep = 0.5f;

    /// <summary>(2) The least wait a hop leaves (seconds; ours): a wait already shorter is not touched, a longer one is not cut below this, so swing > hop > swing never exceeds about four swings a second.</summary>
    public const float LockFloor = 0.25f;

    /// <summary>(2) The melee wait after a hop starts: <paramref name="remaining"/> cut to <see cref="LockKeep"/> of itself but not below <see cref="LockFloor"/> (unchanged when it is already at or under the floor).</summary>
    public static float HopLock(float remaining) =>
        remaining <= LockFloor ? remaining : MathF.Max(LockFloor, remaining * LockKeep);
}

public sealed partial record SimConfig
{
    /// <summary>
    /// sim/hop-rules (protocol 72): the ledge jump after a charge, the hop's shortening of the melee wait and the hop that cancels the aerial tackle (<see cref="HopTuning"/>). False (restore switch
    /// <c>--old-hop-rules</c> on the server and every client; config hash): the protocol 71 rules. Hashed only when false.
    /// </summary>
    public bool HopRules { get; init; } = true;
}
