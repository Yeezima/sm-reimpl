using SmSim.Extras.Musou.Campaign;

namespace SmClient;

/// <summary>
/// Musou Mode pass 2's frames (M0: stubs, so M6 to M11 each fill their own without touching <see cref="TfwFrame"/>). <see cref="TfwClientState"/> already reads tags 11 to 14; these draw them.
/// </summary>
public partial class NetworkView
{
    // Tag 11's shells and the far war: TfwFireView.cs (M8, M9). Tag 11's class-change smoke: NetworkView.TfwClass.cs (M7, M11).
    // Tags 12, 14 and 15: NetworkView.TfwRewards.cs (M6, M6b).
}
