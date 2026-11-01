using SmNet;
using SmSim;

namespace SmServer;

/// <summary>
/// The server's added-content hooks (docs/added-content.md): a C# partial method has one implementation, so each hook is a dispatcher here that calls every
/// feature in a fixed order; each feature returns at once unless its own flag is set in the match's config.
/// </summary>
public sealed partial class GameServer
{
    /// <summary>Once a server tick after the pickups (only with added content and a client connected): the loyalist barks' shouts, then Musou's sections, then the war's.</summary>
    partial void ExtrasSend()
    {
        SendLoyalistBarks();
        SendMusou();
        SendTfw();
    }

    /// <summary>At the end of the constructor (only with added content): Musou's set-up, or on the war's map the war's.</summary>
    partial void ExtrasCreated()
    {
        CreateMusou();
        CreateTfw();
    }

    /// <summary>A client's command (message kind 17, only under <c>ContentExtras.CommandUsers</c>): Musou Mode's.</summary>
    partial void ExtrasCommand(int player, byte tag, byte[] payload) => OnTfwCommand(player, tag, payload);

    /// <summary>A scoreboard name: Musou Mode's AI Marines.</summary>
    partial void ExtrasPlayerName(int id, ref string name) => TfwPlayerName(id, ref name);

    /// <summary>The look of a player without a peer: Musou Mode's retainers wear their owner's.</summary>
    partial void ExtrasPuppetAppearance(int playerId, ref (ushort Version, Race Race, AppearanceCode Code)? look) => TfwPuppetAppearance(playerId, ref look);

    /// <summary>A map generated in code: Musou Mode's.</summary>
    static partial void ExtrasGeneratedMap(ContentExtras extras, string install, ref string? name, ref GameMap? map, ref string? error) => TfwGeneratedMap(extras, install, ref name, ref map, ref error);
}
