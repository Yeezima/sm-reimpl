using System;
using System.Collections.Generic;
using Godot;
using SmNet.Extras.Musou;

namespace SmClient;

/// <summary>
/// Musou weight pass MW2: each ground contact of this frame (<see cref="_musouContacts"/>, MW1) plays the install's dust and grit (<see cref="MusouPuffRules"/>) at the contact,
/// on the effect player's GPU spray path in Musou Mode (musou/gpu-particles). The dust is drawn although the no-smoke rule is on (<see cref="FxEffect.KeepSmoke"/>, the owner's
/// "small dust allowed", 2026-11-10), as the blood mist is; no decals. Lowest cull priority, so a full fight drops them before shots, hits and gore. Switch
/// <c>--no-musou-puffs</c>; the counts go in <c>--log-musou-weight</c>'s lines.
/// </summary>
public partial class NetworkView
{
    static readonly bool NoMusouPuffs = CmdFlag("no-musou-puffs");
    readonly MusouPuff[] _musouPuffScratch = new MusouPuff[2];
    readonly Dictionary<int, (int Sequence, double SkidAt)> _musouPuffState = new();
    int _musouPuffsPlayed, _musouPuffsSkipped;

    void MusouPuffsFrame()
    {
        if (NoMusouPuffs || _musouContacts.Count == 0 || !FxActive || _fx is null) return;
        bool gpu = _fx.GpuSprays;
        foreach (var c in _musouContacts)
        {
            var st = _musouPuffState.GetValueOrDefault(c.PlayerId);
            if (c.Kind == MusouContactKind.Skid)
            {
                if (_connectedFor - st.SkidAt < MusouPuffRules.SkidPuffEvery) { _musouPuffsSkipped++; continue; }
                st.SkidAt = _connectedFor;
            }
            if (c.Kind is MusouContactKind.Step or MusouContactKind.Skid) st.Sequence++;
            _musouPuffState[c.PlayerId] = st;
            string surface = MusouSurfaceAt(c.At);
            int n = MusouPuffRules.Of(c, surface, gpu, st.Sequence, _musouPuffScratch);
            if (n == 0) { _musouPuffsSkipped++; continue; }
            var at = new Vector3(c.At.X, c.At.Y + 0.03f, c.At.Z);
            for (int i = 0; i < n; i++)
            {
                var p = _musouPuffScratch[i];
                if (p.Scale <= 0.01f) continue;
                if (_fx.Play(p.Effect, at, AxesFromY(Vector3.Up), p.Scale, priority: p.Priority) is { } e)
                {
                    e.KeepSmoke = true;
                    e.NoDecals = true;
                    e.SoftGround = true;
                    _musouPuffsPlayed++;
                }
            }
        }
    }
}
