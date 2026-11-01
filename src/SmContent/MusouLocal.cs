namespace SmContent;

/// <summary>What pressing MUSOU MODE does (fix/musou-local-server).</summary>
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

/// <summary>The engine-free rules of the MUSOU MODE button and of the STOP MUSOU SERVER row under it (the owner, 2026-11-01).</summary>
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

    /// <summary>STOP MUSOU SERVER shows only while a Musou server started by this client runs (starting or ready).</summary>
    public static bool ShowStopRow(bool running, bool musouProfile) => running && musouProfile;

    public const string StartingText = "The Musou server is starting (it reads the game files first, this can take a minute). You join when it is ready.";
    public const string StoppedText = "Musou server stopped.";
}
