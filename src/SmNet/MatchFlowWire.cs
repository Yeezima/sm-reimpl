using SmSim;

namespace SmNet;

/// <summary>
/// How the match start travels in the existing match block (no layout change, protocol unchanged): the wire state has two bits, all
/// four taken, so <see cref="MatchState.Countdown"/> is sent as <see cref="MatchState.Warmup"/> with the ticks until play in
/// <see cref="MatchView.TicksLeft"/>; plain warm-up always sends 0 there. A client reads it back with <see cref="IsCountdown"/>.
/// </summary>
public static class MatchFlowWire
{
    /// <summary>The state the match block carries for the world's state.</summary>
    public static MatchState ToWire(MatchState state) => state == MatchState.Countdown ? MatchState.Warmup : state;

    /// <summary>Whether the match block says the match-begins countdown is running.</summary>
    public static bool IsCountdown(this MatchView match) => match.State == MatchState.Warmup && match.TicksLeft > 0 && match.TicksLeft != uint.MaxValue;

    /// <summary>The state a client acts on: <see cref="MatchState.Countdown"/> while the countdown runs, else the block's state.</summary>
    public static MatchState Effective(this MatchView match) => match.IsCountdown() ? MatchState.Countdown : match.State;

    /// <summary>
    /// Whether the server drops the attack buttons of the input for tick <paramref name="tick"/>, as far as a client knows: the snapshot of
    /// tick <paramref name="snapshotTick"/> carried a countdown with <c>TicksLeft</c> ticks to go, and the server's steps
    /// <c>snapshotTick + 1 .. snapshotTick + TicksLeft</c> are the locked ones (play begins at the end of the last of them).
    /// </summary>
    public static bool Locks(this MatchView match, uint snapshotTick, uint tick) =>
        match.IsCountdown() && tick > snapshotTick && tick <= (ulong)snapshotTick + match.TicksLeft;

    /// <summary>The input a client predicts with: the attack buttons dropped when the server will drop them for that tick (<see cref="Locks"/>).</summary>
    public static PlayerInput Predicted(this MatchView match, uint snapshotTick, PlayerInput input) =>
        match.Locks(snapshotTick, input.Tick) ? MatchFlow.Locked(input) : input;
}

/// <summary>
/// fix/connect-timeout: the match flow's screens (countdown, splash, NEXT MAP, the summary) are shown only once a snapshot with a match state has arrived.
/// Before that (connecting, or a server without match rules) the client's match is the default block (<see cref="MatchState.None"/>), not an ended match.
/// </summary>
public static class MatchFlowGate
{
    /// <summary>Whether <paramref name="match"/> came from a snapshot that carried a match state.</summary>
    public static bool HasMatchState(MatchView match) => match.State != MatchState.None;
}