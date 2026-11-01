using System.Collections.Generic;
using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// The Three-Front War on the client. The map is <c>Tfw/NetworkView.TfwMap.cs</c> (TF1), the Guardsmen <c>Tfw/NetworkView.TfwGuards.cs</c> (TF7a); TF7b adds the HUD to <see cref="TfwFrame"/>.
/// </summary>
public partial class NetworkView
{
    /// <summary>Once a frame, with this frame's sections of tags 7 to 10 (the dispatcher <c>NetworkView.Extras.cs</c>). Runs after <see cref="MusouFrame"/>, which reads the director these need.</summary>
    void TfwFrame(PlayerState me, IReadOnlyList<PlayerState> others, double delta, List<ExtrasSection> sections)
    {
        if (!MusouActive || _client is null) return;
        TfwGuardsFrame(sections, delta);
    }
}
