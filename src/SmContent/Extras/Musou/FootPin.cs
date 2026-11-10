using System;
using System.Numerics;

namespace SmContent.Extras.Musou;

/// <summary>
/// The power spear's planted feet held where they stand in the world while a strike is shown in a match (added content, Musou Mode only; owner, 2026-11-11: the step forward "with no
/// floating", and no sliding; <c>docs/claw-spear-animations.md</c> section 3f). The strike's keys plant the feet in the body's frame (<see cref="PoseChannelKind.Foot"/>), but the simulation's
/// lunge moves the body under them, so a foot planted by the keys slid along the ground with the body. Each frame the overlay hands its keyed target here: a foot the clip has on the ground is
/// pinned to its spot in the world when it lands and held there; one the clip lifts follows the keys; a pinned foot left more than <see cref="MaxBehind"/> from its keyed spot or out of the leg's reach (a long lunge)
/// takes a quick catch-up step to it (<see cref="StepTime"/>, lifted <see cref="StepLift"/>), as does every pinned foot when the strike ends. Godot-free (System.Numerics; the client converts).
/// **Ours** (presentation only: nothing in the simulation changes).
/// </summary>
public sealed class FootPin
{
    /// <summary>How far a pinned foot may stand from its keyed spot before it steps after the body (m; ours: about half a stride, within the leg's reach).</summary>
    public const float MaxBehind = 0.35f;
    /// <summary>How long a catch-up step takes (s; ours: a quick shuffle).</summary>
    public const float StepTime = 0.18f;
    /// <summary>How high a catch-up step lifts the foot at its middle (m; ours).</summary>
    public const float StepLift = 0.10f;
    /// <summary>A pinned foot further than this from its keyed spot is put straight onto it (m; about the leg's reach: a teleport, a respawn or a big correction, not a step; review 2026-11-11, was 2.0).</summary>
    public const float SnapBeyond = 1.0f;
    /// <summary>A foot whose keyed lift is below this is on the ground (m; the clips' planted keys have lift 0).</summary>
    public const float PlantedBelow = 1e-3f;

    enum Phase : byte { Free, Pinned, Stepping }

    readonly Phase[] _phase = new Phase[2];
    /// <summary>The pinned spot, or the step's start, in the world.</summary>
    readonly Vector3[] _world = new Vector3[2];
    readonly float[] _u = new float[2];
    readonly bool[] _lift = new bool[2];

    /// <summary>Whether <paramref name="foot"/> (0 left, 1 right) is pinned now (test aid).</summary>
    public bool IsPinned(int foot) => _phase[foot] == Phase.Pinned;
    /// <summary>Whether <paramref name="foot"/> is taking a catch-up step now (test aid).</summary>
    public bool IsStepping(int foot) => _phase[foot] == Phase.Stepping;

    /// <summary>Forgets every pin at once (the overlay stopped, a new character).</summary>
    public void Release() { for (int i = 0; i < 2; i++) _phase[i] = Phase.Free; }

    /// <summary>The pin with no reach limit (the synthetic tests of the pin alone).</summary>
    public Vector3 Place(int foot, Vector3 keyed, bool planted, bool active, Matrix4x4 toWorld, float dt) => Place(foot, keyed, planted, active, toWorld, dt, keyed, float.PositiveInfinity);

    /// <summary>
    /// Where <paramref name="foot"/> (0 left, 1 right) stands this frame, in the skeleton's space.
    /// </summary>
    /// <param name="keyed">The foot's target as the clip keys it (the stance's spot plus the clip's offset), skeleton space.</param>
    /// <param name="planted">The clip has the foot on the ground (its lift below <see cref="PlantedBelow"/>).</param>
    /// <param name="active">Pinning is on (a spear strike shown in a match); off, a pinned foot steps back onto its keyed spot and is let go.</param>
    /// <param name="toWorld">The skeleton's transform to the world this frame (row vectors, System.Numerics).</param>
    /// <param name="dt">Seconds since the last call.</param>
    /// <param name="hip">The leg's hip (the thigh's origin) this frame, skeleton space.</param>
    /// <param name="reach">How far the leg reaches from the hip (m; the leg solve's own limit): a pinned spot beyond it is stepped after, as the foot could only be dragged there.</param>
    public Vector3 Place(int foot, Vector3 keyed, bool planted, bool active, Matrix4x4 toWorld, float dt, Vector3 hip, float reach)
    {
        if (!Matrix4x4.Invert(toWorld, out var toLocal)) return keyed;
        switch (_phase[foot])
        {
            case Phase.Free:
                if (active && planted) { _phase[foot] = Phase.Pinned; _world[foot] = Vector3.Transform(keyed, toWorld); }
                return keyed;
            case Phase.Pinned:
            {
                var local = Vector3.Transform(_world[foot], toLocal);
                local.Z = keyed.Z;   // the ground under the body
                var off = local - keyed;
                float behind = off.Length();
                if (behind > SnapBeyond) { _phase[foot] = Phase.Free; return keyed; }
                if (active && planted && behind <= MaxBehind && Vector3.Distance(local, hip) <= reach) return local;
                // A catch-up step (the body went on, or the strike ended) or the clip's own step (the keys lift the foot: they carry it, no extra lift).
                _phase[foot] = Phase.Stepping; _u[foot] = 0f; _lift[foot] = planted;
                return local;
            }
            default:
            {
                _u[foot] = MathF.Min(1f, _u[foot] + MathF.Max(0f, dt) / StepTime);
                float u = _u[foot], e = u * u * (3f - 2f * u);
                var from = Vector3.Transform(_world[foot], toLocal);
                from.Z = keyed.Z;
                var at = Vector3.Lerp(from, keyed, e);
                if (_lift[foot]) at.Z += StepLift * MathF.Sin(MathF.PI * u);
                if (u >= 1f)
                {
                    _phase[foot] = active && planted ? Phase.Pinned : Phase.Free;
                    _world[foot] = Vector3.Transform(keyed, toWorld);
                    return keyed;
                }
                return at;
            }
        }
    }
}
