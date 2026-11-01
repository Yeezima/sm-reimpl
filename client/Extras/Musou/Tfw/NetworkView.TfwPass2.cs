using SmSim.Extras.Musou.Campaign;

namespace SmClient;

/// <summary>
/// Musou Mode pass 2's frames (M0: stubs, so M6 to M11 each fill their own without touching <see cref="TfwFrame"/>). <see cref="TfwClientState"/> already reads tags 11 to 14; these draw them.
/// </summary>
public partial class NetworkView
{
    /// <summary>Tag 11 (<c>_tfwState.NewFire</c>, taken in <see cref="TfwGuardsFrame"/>): barrages, Thunderhawk runs and the class-change smoke (M8, M9).</summary>
    void TfwFireFrame(in TfwWarView war, double delta) { }

    /// <summary>Tag 13: the elites' health bars (M11).</summary>
    void TfwBarsFrame() { }
}
