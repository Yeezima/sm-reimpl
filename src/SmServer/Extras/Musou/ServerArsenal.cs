using SmSim.Extras.Musou.Arsenal;

namespace SmServer;

/// <summary>
/// The command line's side of the spear and claw removal (remove-spear-claw, owner 2026-11-23: "turn off the power spear and power claw - remove them from the game for now"): what <c>Program.cs</c> asks of
/// the added content without naming it. <c>--old-arsenal-spear-claw</c> restores both (<see cref="ArsenalRules.SpearClawOffered"/>), set as the environment variable before any supervisor starts a child,
/// so Warfront stages and rotation children inherit it. The clients need the same switch: the config hash covers the Assault's melee list.
/// </summary>
public static class ServerArsenal
{
    /// <summary>Turns the restore switch on when <paramref name="restore"/> is set, with a line in the log.</summary>
    public static void Apply(bool restore)
    {
        if (!restore) return;
        ArsenalRules.RestoreSpearClaw();
        Console.WriteLine($"musou arsenal: the lightning claw and the power spear restored ({ArsenalRules.SpearClawSwitch})");
    }
}
