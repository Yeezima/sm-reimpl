using System.Collections.Generic;
using SmNet;
using SmNet.Extras.Musou;
using SmSim;
using SmSim.Extras.Musou;

namespace SmClient;

/// <summary>
/// Added content "Musou" in the networked view (design section D), only when the match runs <see cref="ContentExtras.Musou"/>. MU0: the routed sections
/// (tags 3 to 5, from the dispatcher <c>NetworkView.Extras.cs</c>) are decoded and the latest director kept; nothing is drawn (MU2: the crowd and its
/// interpolation; MU3: the HUD and the effects).
/// </summary>
public partial class NetworkView
{
    MusouDirectorView? _musouDirector;
    int _musouSectionsRead, _musouSectionsMalformed;

    bool MusouActive => _config is { } c && c.Extras.HasFlag(ContentExtras.Musou);

    /// <summary>The latest director the server sent (null: none yet, or not a Musou match).</summary>
    public MusouDirectorView? MusouDirector => _musouDirector;

    /// <summary>This feature's part of the frame hook: <paramref name="sections"/> are this frame's sections with Musou's tags, oldest first.</summary>
    void MusouFrame(PlayerState me, IReadOnlyList<PlayerState> others, double delta, IReadOnlyList<ExtrasSection> sections)
    {
        if (!MusouActive) return;
        foreach (var section in sections)
        {
            bool ok;
            if (section.Tag == MusouWire.DirectorTag)
            {
                var director = MusouWire.ReadDirector(section.Payload);
                if (director is not null) _musouDirector = director;
                ok = director is not null;
            }
            else
            {
                // Entities and events are decoded over the latest box known (before any director: the length checks alone; MU2 holds them until one arrives).
                var box = _musouDirector?.Box ?? new MusouBox(default, System.Numerics.Vector3.One);
                ok = section.Tag == MusouWire.EntitiesTag ? MusouWire.ReadEntities(section.Payload, box) is not null : MusouWire.ReadEvents(section.Payload, box) is not null;
            }
            if (ok) _musouSectionsRead++; else _musouSectionsMalformed++;
        }
    }
}
