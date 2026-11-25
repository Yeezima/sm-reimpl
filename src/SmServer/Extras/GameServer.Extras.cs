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
        WeatherSend();   // weather (Extras/Weather): the state to a client that has not heard it, on a change and every two seconds
        if (World.Config.Extras.HasFlag(ContentExtras.Executions)) ExecutionsSend();   // EXECUTIONS-HOOK: envelope tag 50 (Start, End, the Classic offer, Warfront's overshield list), only under the executions flag
    }

    /// <summary>At the end of the constructor (only with added content): Musou's set-up, or on the war's map the war's.</summary>
    partial void ExtrasCreated()
    {
        CreateMusou();
        CreateTfw();
        WarfrontCreated();   // WARFRONT-HOOK
        WeatherCreated();   // weather (Extras/Weather): Classic PvP in the extended profile (W3): the host's choice, None unless asked
        if (World.Config.Extras.HasFlag(ContentExtras.Executions)) ExecutionsCreated();   // EXECUTIONS-HOOK: the executions' server set-up (X3), only under the executions flag
    }

    /// <summary>A client's command (message kind 17): the weather tag (43) under <c>ContentExtras.Weather</c>, every other tag only under <c>ContentExtras.CommandUsers</c> (Musou Mode's).</summary>
    partial void ExtrasCommand(int player, byte tag, byte[] payload)
    {
        bool weatherHandled = false;
        WeatherCommand(player, tag, payload, ref weatherHandled);   // weather (Extras/Weather): command tag 43, the developer tools' gate inside
        if (weatherHandled) return;
        if ((World.Config.Extras & ContentExtras.CommandUsers) == 0) return;   // a weather-flag profile reads weather's tag 43 alone (ContentExtras.ReadsCommands); every other tag is Musou Mode's
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
    partial void WarfrontSnapshotRecords(int self, System.Numerics.Vector3 viewer, int points, ref PlayerState[] records, ref int lean);
    partial void WarfrontScoreRank(int id, ref int rank);

    /// <summary>A recipient's snapshot records (protocol 94): Warfront orders them and sends its AI Marines lean.</summary>
    partial void ExtrasSnapshotRecords(int self, System.Numerics.Vector3 viewer, int points, ref PlayerState[] records, ref int lean) => WarfrontSnapshotRecords(self, viewer, points, ref records, ref lean);   // WARFRONT-HOOK

    /// <summary>A scoreboard row's rank (protocol 94): Warfront's drop pod Marines last.</summary>
    partial void ExtrasScoreRank(int id, ref int rank) => WarfrontScoreRank(id, ref rank);   // WARFRONT-HOOK

    // ---- Executions (docs/added-content.md section 13): each call above is marked // EXECUTIONS-HOOK and gated on ContentExtras.Executions; X3 implements them in Extras/Executions/GameServer.Executions.cs. ----

    /// <summary>Once a server tick (only with the executions flag): the tag-50 sections (<c>SmNet.Extras.Executions.ExecutionWire</c>).</summary>
    partial void ExecutionsSend();
    /// <summary>At the end of the constructor (only with the executions flag).</summary>
    partial void ExecutionsCreated();

        partial void ExtrasRestartDue(ref bool due, ref int code) => WarfrontRestartDue(ref due, ref code);   // WARFRONT-HOOK
}
