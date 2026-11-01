using SmSim.Extras.Musou.Campaign;

namespace SmClient;

/// <summary>
/// Musou Mode pass 2's frames (M0: stubs, so M6 to M11 each fill their own without touching <see cref="TfwFrame"/>). <see cref="TfwClientState"/> already reads tags 11 to 14; these draw them.
/// </summary>
public partial class NetworkView
{
    /// <summary>Tag 13: the elites' health bars (M11).</summary>
    void TfwBarsFrame() { }

    /// <summary>Tag 12: the shown score and its panels (M6b).</summary>
    void TfwScoreFrame(in TfwWarView war) { }

    /// <summary>Tag 14: the reward cards (M6).</summary>
    void TfwRewardFrame(in TfwWarView war) { }
}
