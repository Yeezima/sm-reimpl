using SmClient.Customizer.Model;
using SmNet;

namespace SmClient;

/// <summary>
/// The ONLINE WARFRONT row (owner 2026-11-19): the project's one shared, always-on Warfront campaign (UDP <see cref="Port"/>, up to 4 players drop in and out) on the same host as the public Classic server
/// (<see cref="JoinTargets.PublicServer"/>; no second address is written anywhere). The row joins it with nothing to type. The VM's campaign is live since 2026-11-20, so <see cref="EnabledByDefault"/> is on (the
/// owner's ship of 2026-11-20); the test aids <c>SM_WF_ONLINE=1</c> (environment) and <see cref="ForceForTest"/> (self-test) show it, <see cref="HideForTest"/> hides it for the self-test's row lists.
/// </summary>
public static class WarfrontOnline
{
    /// <summary>Whether the page shows the row. On since the 2026-11-20 ship (the VM's Warfront campaign is live on UDP 27016).</summary>
    public const bool EnabledByDefault = true;
    /// <summary>The campaign's UDP port on the public host.</summary>
    public const int Port = 27016;
    public const string Row = "Online Warfront";
    public const string RowTooltip = "Joins the project's shared online campaign: up to 4 players, nothing to type";
    public const string NotReachableText = "The online campaign is not reachable right now. Try again later.";

    /// <summary>Self-test: shows the row whatever the gate says.</summary>
    public static bool ForceForTest { get; set; }

    /// <summary>Self-test: hides the row (unless <see cref="ForceForTest"/>) so the page's other row lists stay as they were checked.</summary>
    public static bool HideForTest { get; set; }

    public static bool Enabled => ForceForTest || (!HideForTest && (EnabledByDefault || System.Environment.GetEnvironmentVariable("SM_WF_ONLINE") == "1"));

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
