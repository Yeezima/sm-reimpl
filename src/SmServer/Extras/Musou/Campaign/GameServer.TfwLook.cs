using SmNet;
using SmSim;
using SmSim.Extras.Musou.Campaign;

namespace SmServer;

/// <summary>
/// A retainer wears its player's armour colours (Musou Mode pass 2, item 1; the owner: "your retainer allies should have the same armor color scheme as the player's selected custom loadout").
/// A retainer is a player without a peer, so the core knows no look for it: it is read from its owner's peer, through the hook <c>ExtrasPuppetAppearance</c>, in both places the core tells a look:
/// the scoreboard's appearance version of the row (the snapshot path: a client asks for a look only when a row names a version it does not know) and the answer to an appearance query. The
/// retainer reports its owner's version, race and published code, so a change of the owner's look (a class change in the Stormbird promotes the waiting one) reaches the retainer with the same
/// version bump. An emblem needs nothing more: the owner shows it, so the blob is served and kept for the owner already. Lords and their retainers keep the default look.
/// </summary>
public sealed partial class GameServer
{
    /// <summary>The look of a retainer: its owner's, when the owner is a Space Marine with a look of its own; else left alone (the default look, version 0).</summary>
    void TfwPuppetAppearance(int playerId, ref (ushort Version, Race Race, AppearanceCode Code)? look)
    {
        if (World.Tfw is not { } run || !TfwContracts.IsPuppet(playerId) || run.MarineOf(playerId) is not { Role: MarineRole.Retainer } retainer) return;
        if (PeerOf(retainer.Owner) is not { } owner || owner.Appearance.Race != Race.SpaceMarines || owner.Appearance.Version == 0) return;
        look = (owner.Appearance.Version, Race.SpaceMarines, Published(owner.Appearance));
    }
}
