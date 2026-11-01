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
        return new TfwOwnView(
            Seconds(run.TravelTicks, rate), Seconds(run.PickupTicks, rate), Seconds(run.ReviveTicks(id), rate), Seconds(run.ExtractionTicks, rate),
            (byte)Math.Clamp(MathF.Ceiling(run.ReviveProgress(id, rate) * 10f), 0f, 10f), battle?.Won ?? false, TfwRetainerOf(id, run), run.Orders.Of(id),
            (ushort)Math.Clamp(MathF.Ceiling(run.RecoveryLeft(id)), 0f, ushort.MaxValue),
            (byte)Math.Clamp(guards?.Alive ?? 0, 0, byte.MaxValue), (byte)Math.Clamp(guards?.SquadsAlive ?? 0, 0, byte.MaxValue),
            (byte)Math.Clamp(battle?.Reserves ?? 0, 0, byte.MaxValue), (byte)Math.Clamp(battle?.Secured ?? 0, 0, WarRules.Positions), run.InField.Contains(id), Seconds(run.Fire.StrikeTicksLeft(id, Tick), rate));
    }

    TfwRetainerState TfwRetainerOf(int id, TfwRun run)
    {
        if (run.RetainerOf(id) is { } m && _players.TryGetValue(m.Id, out var p) && p.Alive) return m.ReviveTicks > 0 ? TfwRetainerState.Reviving : TfwRetainerState.Ready;
        return run.RecoveryLeft(id) > 0f ? TfwRetainerState.Recovering : TfwRetainerState.None;
    }
}
