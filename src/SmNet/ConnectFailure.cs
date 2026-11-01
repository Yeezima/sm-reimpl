namespace SmNet;

/// <summary>
/// fix/connect-timeout (the owner's report of the tester release: "i just sit here like this forever"): what a join that goes nowhere tells the player.
/// A client whose Hellos get no answer gives up after <see cref="DefaultTimeout"/> (ours) instead of showing "Connecting" forever; a server that refuses
/// says why in plain words. Engine-free; the networked view shows these lines with Esc back to the menu.
/// </summary>
public static class ConnectFailure
{
    /// <summary>
    /// How long a connecting client waits for any answer to its Hellos (ours; spec "Connect timeout"). A server of this version answers the first Hello at once
    /// (a Challenge); 10 s covers a slow or lossy link many times over, and the map load a Challenge starts is not counted.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Whether a wait that began at <paramref name="since"/> has run out at <paramref name="now"/> (strictly longer than <paramref name="timeout"/>).</summary>
    public static bool TimedOut(TimeSpan now, TimeSpan since, TimeSpan timeout) => now - since > timeout;

    /// <summary>The line for a server that never answered.</summary>
    public static string NoAnswerText(string target) =>
        $"No answer from {target}. Is a server running there, and is it the same version as yours?";

    /// <summary>The line for a refusal: the reason in plain words, then the server's own detail when it sent one.</summary>
    public static string RejectText(RejectReason? reason, string? detail)
    {
        string plain = reason switch
        {
            RejectReason.ProtocolMismatch => "The server runs another version of the game. Everyone in a match needs the same version",
            RejectReason.ContentMismatch => "The server's game files differ from yours",
            RejectReason.ConfigMismatch => "Enable extended mode before reconnecting to the server",
            RejectReason.MapMismatch => "The server's map could not be loaded here, or differs from yours",
            RejectReason.ServerFull => "The server is full",
            null => "The server refused to let you in",
            _ => $"The server refused to let you in ({reason})",
        };
        return reason == RejectReason.ConfigMismatch || string.IsNullOrWhiteSpace(detail) ? plain + "." : $"{plain} ({detail.Trim()}).";   // the owner, 2026-11-02: that one line alone
    }
}
