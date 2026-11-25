namespace SmNet.Extras.Musou;

/// <summary>
/// Warfront Phase B T5 follow-up (design-phase-b-1124, T5): a retainer is a World player in a puppet id, so Musou's event window ranked its hits with the AI Marines' (4), after every human's. Here a retainer's
/// hit ranks with a human's (3), ahead of the tracers, as the design's send order says. <see cref="WarfrontRetainer"/> is set by the Warfront server's squad wiring only; null (Musou Mode, every other server)
/// leaves the order as it was. Spec "Added content: Warfront: retainers as World players" (hit-event rank).
/// </summary>
public sealed partial class MusouEventWindow
{
    /// <summary>Whether a player id is a Warfront retainer (the server's <c>WarfrontRetainers.IsRetainer</c>); null outside Warfront.</summary>
    public Func<int, bool>? WarfrontRetainer { get; set; }

    partial void WarfrontHitRank(int player, ref int rank)
    {
        if (rank == 4 && WarfrontRetainer is { } isRetainer && isRetainer(player)) rank = 3;
    }
}
