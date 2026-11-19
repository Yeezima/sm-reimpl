using SmClient.Customizer.Model;
using SmNet;

namespace SmClient;

/// <summary>
/// The ONLINE WARFRONT row (owner 2026-11-19): the project's one shared, always-on Warfront campaign (UDP <see cref="Port"/>, up to 4 players drop in and out) on the same host as the public Classic server
/// (<see cref="JoinTargets.PublicServer"/>; no second address is written anywhere). The row joins it with nothing to type. The server is not live yet, so the row is hidden: <see cref="EnabledByDefault"/> is
/// off until the owner's word; the test aids <c>SM_WF_ONLINE=1</c> (environment) and <see cref="ForceForTest"/> (self-test) show it.
/// </summary>
public static class WarfrontOnline
{
    /// <summary>Whether the page shows the row. OFF until the VM's Warfront campaign is live (flip this constant then).</summary>
    public const bool EnabledByDefault = false;
    /// <summary>The campaign's UDP port on the public host.</summary>
    public const int Port = 27016;
    public const string Row = "Online Warfront";
    public const string RowTooltip = "Joins the project's shared online campaign: up to 4 players, nothing to type";
    public const string NotReachableText = "The online campaign is not reachable right now. Try again later.";

    /// <summary>Self-test: shows the row whatever the gate says.</summary>
    public static bool ForceForTest { get; set; }

    public static bool Enabled => EnabledByDefault || ForceForTest || System.Environment.GetEnvironmentVariable("SM_WF_ONLINE") == "1";

    /// <summary>The public host (the part of <see cref="JoinTargets.PublicServer"/> before its port) with this campaign's port.</summary>
    public static string Address
    {
        get
        {
            string host = JoinTargets.PublicServer;
            int colon = host.LastIndexOf(':');
            return (colon > 0 ? host[..colon] : host) + ":" + Port;
        }
    }

    /// <summary>The line a failed join shows: a server that never answered is "not reachable" in plain words; a refusal keeps its own plain text (<see cref="ConnectFailure.RejectText"/>).</summary>
    public static string? Message(string? startMessage) =>
        startMessage == ConnectFailure.NoAnswerText(Address) ? NotReachableText : startMessage;
}
