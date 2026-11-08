using SmNet;
using SmSim;
using SmSim.Extras.Musou.Campaign;

namespace SmServer;

/// <summary>
/// The allied pair wears the host's armour colours (Musou Mode pass 2, item 1, first for the retainers: "your retainer allies should have the same armor color scheme as the player's selected custom
/// loadout"; since the owner's pair of 2026-11-08 the two Marines belong to no one player, so they wear the host's, <see cref="TfwRun.HostOf"/>). A Marine of the pair is a player without a peer,
/// so the core knows no look for it: it is read from the host's peer, through the hook <c>ExtrasPuppetAppearance</c>, in both places the core tells a look: the scoreboard's appearance version of
/// the row (the snapshot path: a client asks for a look only when a row names a version it does not know) and the answer to an appearance query. The Marine reports the host's version, race and
/// published code, so a change of the host's look reaches it with the same version bump. An emblem needs nothing more: the host shows it, so the blob is served and kept for the host already.
/// </summary>
public sealed partial class GameServer
{
    /// <summary>The look of a Marine of the pair: the host's, when the host is a Space Marine with a look of its own; else left alone (the default look, version 0).</summary>
    void TfwPuppetAppearance(int playerId, ref (ushort Version, Race Race, AppearanceCode Code)? look)
    {
        if (World.Tfw is not { } run || !TfwContracts.IsPuppet(playerId) || run.MarineOf(playerId) is null) return;
        if (PeerOf(TfwRun.HostOf(World.Players.Keys)) is not { } host || host.Appearance.Race != Race.SpaceMarines || host.Appearance.Version == 0) return;
        look = (host.Appearance.Version, Race.SpaceMarines, Published(host.Appearance));
    }
}
