using Godot;
using SmNet.Extras.Musou;

namespace SmClient;

/// <summary>
/// The corpse floor for the Marines in Musou Mode (the owner, 2026-11-09: "same as guardsmen and allied space marines. they should always leave a corpse for at least 10 seconds before
/// disappearing"): a Marine's ragdoll younger than <see cref="MusouCorpseRules.FloorSeconds"/> stays although its player respawned elsewhere or left the snapshot (a battle's end dismisses its
/// Marines), and a full ragdoll cap grows rather than drop it. A Marine revived where he lay (within <see cref="RevivedMetres"/> of his body) takes his body with him, so there are never two of him.
/// The Orks' and the Guardsmen's floor is in <see cref="CrowdView"/> and <see cref="MusouRagdolls"/>.
/// </summary>
public partial class NetworkView
{
    /// <summary>A player alive again within this of his corpse (m, flat) was revived on it, not respawned (ours).</summary>
    const float RevivedMetres = 4f;

    partial void ExtrasCorpseHold(Ragdoll rd, Vector3? aliveAt, ref bool hold)
    {
        if (!MusouActive || !MusouCorpseRules.Hold(_connectedFor - rd.StartedAt, CrowdView.OldCorpses)) return;
        if (aliveAt is { } at && _gameSpace is not null && IsInstanceValid(rd))
        {
            var body = _gameSpace.Transform.AffineInverse() * rd.FocusPoint;   // Godot to game space (Z up)
            if (new Vector2(body.X - at.X, body.Y - at.Y).Length() <= RevivedMetres) return;
        }
        hold = true;
    }
}
