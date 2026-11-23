using System.Collections.Generic;
using Godot;
using SmSim.Extras.Musou.Arsenal;

namespace SmClient;

public partial class ModelViewer
{
    /// <summary>
    /// remove-spear-claw (owner 2026-11-23: "turn off the power spear and power claw - remove them from the game for now"): <c>--old-arsenal-spear-claw</c> on the client's own arguments, read the way
    /// every client switch is (<see cref="OS.GetCmdlineUserArgs"/>), turns the restore switch on for this process (<see cref="ArsenalRules.RestoreSpearClaw"/>, the environment variable, which also
    /// reaches a server the Host panel starts). The variable <c>SM_OLD_ARSENAL_SPEAR_CLAW=1</c> set outside works as before. The server needs the same switch: the config hash covers it.
    /// </summary>
    partial void ExtrasStart(Dictionary<string, string?> args)
    {
        if (!args.ContainsKey(ArsenalRules.SpearClawSwitch.TrimStart('-'))) return;
        ArsenalRules.RestoreSpearClaw();
        GD.Print($"musou arsenal: the claw and the spear restored ({ArsenalRules.SpearClawSwitch})");
    }
}
