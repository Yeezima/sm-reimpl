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
        LogTfwCoop();   // Musou Mode co-op: late joins in the log
        WarfrontSend();   // WARFRONT-HOOK
    }

    /// <summary>At the end of the constructor (only with added content): Musou's set-up, or on the war's map the war's.</summary>
    partial void ExtrasCreated()
    {
        CreateMusou();
        CreateTfw();
        WarfrontCreated();   // WARFRONT-HOOK
    }

    /// <summary>A client's command (message kind 17, only under <c>ContentExtras.CommandUsers</c>): Musou Mode's.</summary>
    partial void ExtrasCommand(int player, byte tag, byte[] payload)
    {
        bool warfrontHandled = false;
        WarfrontCommand(player, tag, payload, ref warfrontHandled);   // WARFRONT-HOOK
        if (warfrontHandled) return;
        if (OnLibrarianCommand(player, tag, payload)) return;   // Musou: the Librarian's cast at the press (Librarian/GameServer.TfwLibrarian.cs)
        OnTfwCommand(player, tag, payload);
    }

    /// <summary>A scoreboard name: Musou Mode's AI Marines.</summary>
    partial void ExtrasPlayerName(int id, ref string name) => TfwPlayerName(id, ref name);

    /// <summary>The look of a player without a peer: Musou Mode's retainers wear their owner's, the allied pair the host's.</summary>
    partial void ExtrasPuppetAppearance(int playerId, ref (ushort Version, Race Race, AppearanceCode Code)? look) => TfwPuppetAppearance(playerId, ref look);

    /// <summary>The players a match admits: Musou Mode's co-op takes at most 4.</summary>
    partial void ExtrasPlayerCap(ref int cap)
    {
        TfwPlayerCap(ref cap);
        WarfrontPlayerCap(ref cap);   // WARFRONT-HOOK
    }

    /// <summary>A map generated in code: Musou Mode's.</summary>
    static partial void ExtrasGeneratedMap(ContentExtras extras, string install, ref string? name, ref GameMap? map, ref string? error) => TfwGeneratedMap(extras, install, ref name, ref map, ref error);

    // ---- Warfront (docs/added-content.md section 11): each call above is marked and implemented under Extras/Warfront/; every implementation returns at once unless the match has the Warfront flag. ----

    partial void WarfrontCreated();
    partial void WarfrontSend();
    partial void WarfrontCommand(int player, byte tag, byte[] payload, ref bool handled);
    partial void WarfrontPlayerCap(ref int cap);
    partial void WarfrontRestartDue(ref bool due, ref int code);

        partial void ExtrasRestartDue(ref bool due, ref int code) => WarfrontRestartDue(ref due, ref code);   // WARFRONT-HOOK
}
