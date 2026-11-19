namespace SmContent;

/// <summary>What pressing the old MUSOU MODE row did (fix/musou-local-server; the row went to WARFRONT on 2026-11-19, the rules stay as Musou's dev-switch tests).</summary>
public enum MusouPress
{
    /// <summary>No server executable was found next to the game: nothing can start.</summary>
    NoServer,
    /// <summary>No server runs: start one (this PC only, <c>--content musou</c>); the Host panel joins it by itself when it is ready.</summary>
    Start,
    /// <summary>A Musou server of this client is ready: join it.</summary>
    Join,
    /// <summary>A Musou server of this client is still starting (it reads the game files first): wait, never join early and never start a second one.</summary>
    Wait,
    /// <summary>A server of another profile runs (the Host panel's): stop it first.</summary>
    OtherServer,
}

/// <summary>The engine-free rules of the old MUSOU MODE button and of the STOP MUSOU SERVER row under it (the owner, 2026-11-01; replaced by WARFRONT, see WarfrontLocal; kept for the tests of the shared code).</summary>
public static class MusouLocal
{
    /// <param name="hasServerExe">A server executable was found (next to the client or given with <c>--server-exe</c>).</param>
    /// <param name="running">A server started by this client runs.</param>
    /// <param name="ready">It printed its listening line.</param>
    /// <param name="musouProfile">The running server's profile is Musou.</param>
    public static MusouPress OnPress(bool hasServerExe, bool running, bool ready, bool musouProfile)
    {
        if (!hasServerExe) return MusouPress.NoServer;
        if (!running) return MusouPress.Start;
        if (!musouProfile) return MusouPress.OtherServer;
        return ready ? MusouPress.Join : MusouPress.Wait;
    }

    /// <summary>The old STOP MUSOU SERVER row showed only while a Musou server started by this client runs (starting or ready).</summary>
    public static bool ShowStopRow(bool running, bool musouProfile) => running && musouProfile;

    /// <summary>Musou Mode's first run builds its walking graph before the server listens: 3 s on 12 cores, 72 s on one busy core (measured), and an interrupted build starts over, so this start may run longer than a match server's.</summary>
    public const double StartLimitSeconds = 600;
    /// <summary>What the player reads while it starts.</summary>
    public const string PreparingText = "Preparing the battlefield (the first run maps it, this can take a few minutes)...";

    /// <summary>
    /// Most players in one Musou match (co-op, the owner 2026-11-06; the server's cap is <c>TfwCoop.MaxHumans</c> in the added content, which a test holds equal to this). The front end's
    /// texts use it; the server enforces it.
    /// </summary>
    public const int MaxPlayers = 4;
    /// <summary>The note under the Host panel in Musou and Warfront (the old MUSOU MODE page had it too).</summary>
    public const string CoopNote = "Up to 4 players fight together as Space Marines against the Orks. Host a local lobby (LAN / Internet) and your friends join it by address.";

    public const string StartingText = "The Musou server is starting (it reads the game files first, this can take a minute). You join when it is ready.";
    public const string StoppedText = "Musou server stopped.";
}
