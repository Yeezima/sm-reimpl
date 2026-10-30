using System.Collections.Generic;
using Godot;
using SmContent;
using SmSim;

namespace SmClient;

/// <summary>
/// fx/visuals-2 (owner's playtest of 2026-10-30, round 2, item 38; client presentation only, no wire change): no pistol tracer until the pistol is up. A shot from a lowered pistol waits for the raise in the
/// simulation (<c>RaiseDelay</c>, 0.167 s: the shot leaves when it ends, so the damage and the shot record keep their server time), but the ready posture's blend from the relaxed pose takes longer
/// (the graph's relaxed-to-ready blend, <see cref="HipRaiseSeconds"/> as the fallback), so the tracer used to start from a muzzle that was still low. The pistol's tracer now waits until the posture's blend
/// has run and starts from the raised muzzle at that moment (the muzzle is looked up again then); the impact effect waits with it. The muzzle flash and the sound stay with the shot (the owner named the tracer).
/// Shots from a pistol already up (inside the ten second ready hold) are untouched. <c>--old-pistol-tracer</c> restores the tracer at the shot.
/// </summary>
public partial class NetworkView
{
    bool OldPistolTracer => _oldPistolTracer ??= CmdFlag("old-pistol-tracer");
    bool? _oldPistolTracer;

    /// <summary>A tracer held back until the pistol is up: what <see cref="PlayTracer"/> needs, played at <see cref="At"/> (client time).</summary>
    sealed record DeferredTracer(double At, FxPlay Play, WeaponEffects Effects, Vector3 From, Vector3 End, Vector3? RayOrigin, Vector3? RayDir, bool Own, int Shooter);
    readonly List<DeferredTracer> _tracersWaiting = new();
    int _pistolTracersDeferred;

    /// <summary>The seconds left until <paramref name="shooter"/>'s pistol is in its ready position when a shot of <paramref name="spec"/> is seen now (0: it is up, or this is no pistol).</summary>
    float PistolRaiseWait(int shooter, WeaponSpec spec)
    {
        if (OldPistolTracer || HipFireAnimations.FamilyOf(spec.Name) != HipFireWeaponClass.Pistol || !_hip.TryGetValue(shooter, out var t) || t.Kind != PoseKind.Hip) return 0f;
        float blend = _hipAnims?.For(HipFireWeaponClass.Pistol)?.RelaxedToReadyBlend is { } b and > 0f ? b : (float)HipRaiseSeconds;
        float left = blend - (float)(_connectedFor - t.HipSince);
        return left is > 0.01f and <= 1f ? left : 0f;
    }

    /// <summary>Holds a pistol's tracer back for <paramref name="wait"/> seconds.</summary>
    void DeferTracer(float wait, FxPlay p, WeaponEffects w, Vector3 from, Vector3 end, Vector3? rayOrigin, Vector3? rayDir, bool own, int shooter)
    {
        _tracersWaiting.Add(new DeferredTracer(_connectedFor + wait, p, w, from, end, rayOrigin, rayDir, own, shooter));
        _pistolTracersDeferred++;
        if (_bot) GD.Print($"t {_connectedFor:F2} s: pistol tracer of P{shooter} held {wait:F3} s until the pistol is up ({_pistolTracersDeferred} held so far)");
    }

    /// <summary>Each frame: plays the held tracers that are due, from the muzzle where it is now.</summary>
    void UpdateDeferredTracers()
    {
        if (_tracersWaiting.Count > 0 && (_client is null || _client.Status != SmNet.ClientStatus.Connected || _tracersWaiting.Exists(d => d.At > _connectedFor + 2.0))) _tracersWaiting.Clear();   // disconnect, or a new connection's clock
        if (_stimTracks.Count > 0 && (_client is null || _client.Status != SmNet.ClientStatus.Connected)) _stimTracks.Clear();   // stim times are in this connection's clock
        for (int i = _tracersWaiting.Count - 1; i >= 0; i--)
        {
            var d = _tracersWaiting[i];
            if (_connectedFor < d.At) continue;
            _tracersWaiting.RemoveAt(i);
            if (_fx is null) continue;
            var from = FxPlace(d.Shooter, d.Play.Marker)?.Position ?? d.From;
            if (from.DistanceTo(d.End) < 0.4f) continue;
            PlayTracer(d.Play, d.Effects, from, d.End, d.RayOrigin, d.RayDir, d.Own, d.Shooter);
            if (_bot) GD.Print($"t {_connectedFor:F2} s: pistol tracer of P{d.Shooter} drawn from the raised muzzle");
        }
    }
}
