using System.Collections.Generic;
using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// The Three-Front War on the client (TF0: the contract and stubs). The map is <c>Tfw/NetworkView.TfwMap.cs</c> (TF1); TF7 fills <see cref="TfwFrame"/>.
/// </summary>
public partial class NetworkView
{
    /// <summary>Once a frame, with this frame's sections of tags 7 to 10 (the dispatcher <c>NetworkView.Extras.cs</c>). TF0: nothing yet.</summary>
    void TfwFrame(PlayerState me, IReadOnlyList<PlayerState> others, double delta, List<ExtrasSection> sections) { }
}
