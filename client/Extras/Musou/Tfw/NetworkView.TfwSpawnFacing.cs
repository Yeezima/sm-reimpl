using System.Numerics;

namespace SmClient;

/// <summary>
/// musou/spawn-facing (owner, 2026-11-09): a unit spawns facing the map's centre. A death and respawn already takes the server's yaw as the view (NetworkView.cs, <c>!_wasAlive</c>); the
/// landing from the Stormbird and a redeploy move a living player far at once, so there the view takes the server's yaw the same frame, before the input carries the old one back.
/// </summary>
public partial class NetworkView
{
    /// <summary>A jump of the own body at least this far in one frame (the landing from the Stormbird) is a spawn.</summary>
    const float SpawnJumpMetres = 15f;
    Vector3? _spawnFacingLast;

    void TfwSpawnFacingFrame(SmSim.PlayerState me)
    {
        var last = _spawnFacingLast;
        _spawnFacingLast = me.Alive ? me.Position : null;
        if (!MusouActive || last is not { } was || !me.Alive || !_wasAlive) return;
        if (Vector3.DistanceSquared(was, me.Position) >= SpawnJumpMetres * SpawnJumpMetres) { _yaw = me.Yaw; _pitch = 0f; }
    }
}
