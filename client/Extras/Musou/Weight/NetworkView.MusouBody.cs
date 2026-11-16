using System.Collections.Generic;
using Godot;
using SmNet.Extras.Musou;
using SmSim;

namespace SmClient;

/// <summary>
/// Musou weight pass MW3, weight in the body: a landing, a sprint stop's plant, a melee step's plant and a roll's end lower the pelvis for a moment with the feet held where
/// they are (the knees bend) and lean the spine against the stop (<see cref="MusouWeightRules.SettleOf"/>, <see cref="CharacterViewer.MusouSettle"/>), so the body takes the
/// weight instead of ending in the next stance at once. Allies at half. Switch <c>--no-musou-weight-body</c>.
/// </summary>
public partial class NetworkView
{
    static readonly bool NoMusouWeightBody = CmdFlag("no-musou-weight-body");
    readonly Dictionary<int, bool> _musouWasRolling = new();
    int _musouSettles;

    void MusouSettleFrame()
    {
        if (NoMusouWeightBody) return;
        foreach (var c in _musouContacts)
            if (MusouWeightRules.SettleOf(c) is { } st && ViewerOf(c.PlayerId) is { } v) { v.MusouSettle(st.Depth, st.LeanDegrees, st.Down, st.Back); _musouSettles++; }
        // A roll's end on the ground (not a jump pack's dash: its landing is a landing).
        foreach (var (_, s, viewer, own) in _musouMarines)
        {
            bool rolling = Evade.Evading(s) && Loadouts.Evade(_config!, s) is { Dash: false };
            if (_musouWasRolling.GetValueOrDefault(s.PlayerId) && !rolling && s.Grounded)
            {
                var r = MusouWeightRules.RollEnd;
                float k = own ? 1f : 0.5f;
                viewer.MusouSettle(r.Depth * k, 0f, r.Down, r.Back);
                _musouSettles++;
                if (own) MusouRollEndDip();   // MW6 (Weight/NetworkView.MusouImpact.cs)
            }
            _musouWasRolling[s.PlayerId] = rolling;
        }
    }

    CharacterViewer? ViewerOf(int id) => _client is not null && id == _client.PlayerId ? _local : _remotes.GetValueOrDefault(id);
}
