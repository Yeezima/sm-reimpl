using SmNet.Extras.Warfront;
using SmSim.Extras.Warfront.Coop;

namespace SmServer;

/// <summary>
/// Warfront's co-op rules on the server (slice W12): the glue between <see cref="WarfrontCoop"/> and the wire. Plain members: W5a's routing (the <c>WarfrontSend</c>, <c>WarfrontCommand</c> and Deploy-handler seams) calls them,
/// those lines are listed in the W12 report. Nothing here runs unless the run has the rules (<see cref="WarfrontCoopRules"/> not null).
/// </summary>
public sealed partial class GameServer
{
    /// <summary>The run's co-op rules (set by W5a's run set-up beside the campaign; null outside a Warfront run).</summary>
    internal WarfrontCoop? WarfrontCoopRules { get; set; }

    /// <summary>The Coop section (envelope tag 46) for one client's key: null when there are no rules.</summary>
    internal byte[]? WarfrontCoopSectionFor(ulong key) =>
        WarfrontCoopRules is { } r ? WarfrontCoopWire.Encode(WarfrontCoopWire.FromView(r.ViewFor(key))) : null;

    /// <summary>A Coop command (tag 42). Malformed bytes are ignored. <paramref name="candidates"/> are the watchable targets in order (living humans, then standing Marines). Returns whether anything changed.</summary>
    internal bool WarfrontCoopCommand(ulong key, ReadOnlySpan<byte> payload, IReadOnlyList<WarfrontWatch> candidates)
    {
        if (WarfrontCoopRules is not { } r || !WarfrontCoopWire.TryDecodeCommand(payload, out var c)) return false;
        switch (c.Kind)
        {
            case WarfrontCoopWire.CommandKind.ConfirmClass: return r.ConfirmClass(key);
            case WarfrontCoopWire.CommandKind.Watch:
                // only a spectating joiner steers here; the fallen player's own spectating uses the same step through W3's state (the caller passes the candidates for either)
                var before = r.WatchOf(key);
                return r.Step(key, c.Step, candidates) != before;
        }
        return false;
    }

    /// <summary>Before <c>TryDeploy</c> (W5a's Deploy handler): the refusal text for the Stage or Hub status line, or null when the squad may deploy.</summary>
    internal string? WarfrontCoopDeployGate() => WarfrontCoopRules?.DeployRefusal();
}
