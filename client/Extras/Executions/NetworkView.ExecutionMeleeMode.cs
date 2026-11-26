using SmSim;

namespace SmClient;

/// <summary>
/// Executions and the melee camera (owner 2026-11-26, decision series-37-pre-ship-executions "Melee cam": "Ignore them"): while the own finisher holds the player, Fire, Aim
/// and Reload do nothing to melee mode (<see cref="SmClient.Customizer.Model.MeleeMode.Inputs.Locked"/>), so he comes out of the finisher still in the free melee camera and the
/// next fresh press after the End locks it as usual. The lock is the same one the execution orbit camera uses (<see cref="OwnExecuting"/>: the own live run from its Start until
/// its End or its own end tick), so the melee mode and the orbit's return (<c>ExecutionOrbitLeave</c>) agree on when the finisher is over. Only with
/// <see cref="ContentExtras.Executions"/>.
/// </summary>
public partial class NetworkView
{
    partial void ExtrasMeleeModeLocked(PlayerState me, ref bool locked)
    {
        if (ExecutionsOn && me.Alive && OwnExecuting(out _)) locked = true;
    }
}
