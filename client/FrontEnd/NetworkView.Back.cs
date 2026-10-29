using Godot;
using SmNet;

namespace SmClient;

/// <summary>
/// What the front end needs to know about the connection (a partial of <see cref="NetworkView"/> in its own file, so the networked view's files stay untouched):
/// whether it is still connecting or has failed, so a Join that goes nowhere has a way back to the menu (<c>ModelViewer.BackToFrontEnd</c>).
/// </summary>
public partial class NetworkView
{
    /// <summary>The client is still waiting for the server's answer.</summary>
    public bool StillConnecting => _error == "" && _client?.Status == ClientStatus.Connecting;

    /// <summary>The join failed or the server is gone (a load error, a rejection, no answer or a disconnect); <see cref="FailureText"/> says what.</summary>
    public bool ConnectionFailed => _error != "" || _client?.Status is ClientStatus.Rejected or ClientStatus.Disconnected or ClientStatus.NoAnswer;

    /// <summary>One line for the menu after a failed join; empty while all is well.</summary>
    public string FailureText => ConnectionFailed ? StatusLine().Text : "";
}
