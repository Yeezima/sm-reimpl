using SmSim.Extras.Musou.Campaign;

namespace SmClient;

/// <summary>
/// Musou Mode pass 2's frames (M0: stubs, so M6 to M11 each fill their own without touching <see cref="TfwFrame"/>). <see cref="TfwClientState"/> already reads tags 11 to 14; these draw them.
/// </summary>
public partial class NetworkView
{
    /// <summary>Tag 11 (<c>_tfwState.NewFire</c>, taken in <see cref="TfwGuardsFrame"/>): barrages, Thunderhawk runs and the class-change smoke (M8, M9).</summary>
    void TfwFireFrame(in TfwWarView war, double delta) { }

    // Tag 13, the elites' health bars, and tag 11's class-change smoke: NetworkView.TfwClass.cs (M7, M11).

    /// <summary>Tag 12: the shown score and its panels (M6b).</summary>
    void TfwScoreFrame(in TfwWarView war) { }

    /// <summary>Tag 14: the reward cards (M6).</summary>
    void TfwRewardFrame(in TfwWarView war) { }
}
