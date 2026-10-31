namespace SmServer;

/// <summary>
/// The server's added-content hooks (docs/added-content.md): a C# partial method has one implementation, so each hook is a dispatcher here that calls every
/// feature in a fixed order; each feature returns at once unless its own flag is set in the match's config.
/// </summary>
public sealed partial class GameServer
{
    /// <summary>Once a server tick after the pickups (only with added content and a client connected): the loyalist barks' shouts, then Musou's sections.</summary>
    partial void ExtrasSend()
    {
        SendLoyalistBarks();
        SendMusou();
    }

    /// <summary>At the end of the constructor (only with added content): Musou's set-up.</summary>
    partial void ExtrasCreated()
    {
        CreateMusou();
    }
}
