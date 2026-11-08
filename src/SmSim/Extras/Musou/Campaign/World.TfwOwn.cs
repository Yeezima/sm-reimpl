using SmSim.Extras.Musou.Campaign;

namespace SmSim;

/// <summary>What one player's HUD shows of the run (TF7b; tag 7's own block, <see cref="TfwOwnView"/>).</summary>
public sealed partial class World
{
    /// <summary>The name of the lord of <paramref name="lane"/> (open question 7's placeholders), for the war map.</summary>
    public static string TfwLordName(TfwLane lane) => $"Brother-Captain {TfwLords[(int)lane].Name}";

    static byte Seconds(int ticks, int rate) => (byte)Math.Clamp((ticks + rate - 1) / rate, 0, byte.MaxValue);

    /// <summary><paramref name="id"/>'s block this tick (default outside a war): whole seconds rounded up, so the record changes about once a second but for the revive's tenths and the Guardsmen.</summary>
    public TfwOwnView TfwOwn(int id)
    {
        if (_tfw is not { } run) return default;
        int rate = Math.Max(1, Config.TickRate);
        var battle = run.Battle;
        var guards = battle is null ? null : TfwGuards;
        var allies = TfwAlliesOf(id, run);
        return new TfwOwnView(
            Seconds(run.TravelTicks, rate), Seconds(run.PickupTicks, rate), Seconds(run.ReviveTicks(id), rate), Seconds(run.ExtractionTicks, rate),
            (byte)Math.Clamp(MathF.Ceiling(run.ReviveProgress(id, rate) * 10f), 0f, 10f), battle?.Won ?? false, allies, MarineOrder.Follow,   // no player orders the pair (2026-11-08)
            (ushort)(allies == TfwRetainerState.Recovering ? Math.Clamp(MathF.Ceiling(TfwAllyBackIn()), 0f, ushort.MaxValue) : 0f),
            (byte)Math.Clamp(guards?.Alive ?? 0, 0, byte.MaxValue), (byte)Math.Clamp(guards?.SquadsAlive ?? 0, 0, byte.MaxValue),
            (byte)Math.Clamp(battle?.Reserves ?? 0, 0, byte.MaxValue), (byte)Math.Clamp(battle?.Secured ?? 0, 0, WarRules.Positions), run.InField.Contains(id),
            Seconds((int)Math.Min(run.Fire.OrbitalTicksLeft(id, Tick), int.MaxValue), rate));   // contracts 25: the orbital strike's cooldown
    }

    /// <summary>The allied pair as <paramref name="id"/>'s HUD shows it (the wire's retainer state, kept): one up (reviving this player or not), both down and coming back, or none.</summary>
    TfwRetainerState TfwAlliesOf(int id, TfwRun run)
    {
        if (run.Marines.Count == 0) return TfwRetainerState.None;
        if (!TfwAllyUp()) return TfwRetainerState.Recovering;
        return run.ReviveProgress(id, Math.Max(1, Config.TickRate)) > 0f ? TfwRetainerState.Reviving : TfwRetainerState.Ready;
    }
}
