namespace SmContent;

/// <summary>
/// fx/melee-presentation: the idle hum of a melee weapon in hand, from the wargear's own sound commands (client presentation only).
/// Evidence (Verified-local, <c>smtool baf-dump wargear.oppc --entry ^(sm|csm)_mp_melee --grep _event_name</c>, <c>smtool audio-event</c>): the thunder hammer
/// (<c>play_sm_thunder_hammer_idle</c> / <c>stop_…</c>), the daemon maul (<c>play_ch_power_mace_idle</c>) and the power axe (<c>play_sm_power_axe_idle</c>) post their
/// hum in <c>on_deploy_actions</c> and stop it in <c>on_stow_actions</c>; the power axe's and the Chaos chain axe's <c>while_deployed_actions</c> name
/// <c>play_sm_power_sword_idle</c>, an event that is in no bank of this install. The Space Marine power sword's own <c>on_deploy_actions</c> names the axe's hum (a copy in
/// the data); the Chaos sword binds none; the chain axe's only hum is the missing sword event (not reproducible).
/// </summary>
public static class MeleeIdleSounds
{
    /// <summary>The events of a hum: <see cref="Start"/> when the weapon comes into hand, <see cref="Stop"/> when it leaves; <see cref="GainDb"/> an extra level (ours, quiet).</summary>
    /// <param name="Start">The event posted when the weapon comes into hand.</param>
    /// <param name="Stop">The event posted when it leaves (empty: none found).</param>
    /// <param name="GainDb">An extra level on the hum (ours).</param>
    /// <param name="Substitute">True when the weapon's own file names no usable hum and another weapon's is used (ours, kind substitute).</param>
    public sealed record Hum(string Start, string Stop, float GainDb, bool Substitute);

    /// <summary>Ours: how much quieter than the axe's own level a power sword's hum is (the owner asked for it quiet).</summary>
    public const float SwordGainDb = -6f;
    /// <summary>The file whose hum stands in for a power sword that binds none of its own.</summary>
    public const string SwordSubstituteFile = "sm_mp_melee_power_axe";

    /// <summary>Whether a wargear file is a power sword (both races).</summary>
    public static bool IsPowerSword(string? wargear) => wargear?.Contains("powersword", StringComparison.OrdinalIgnoreCase) == true;

    static bool IsHum(string name) => name.EndsWith("_idle", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The hum a file's own commands give: the first <c>*_idle</c> start event of its <c>on_deploy_actions</c> (else <c>while_deployed_actions</c>) that
    /// <paramref name="hasEvent"/> knows, and its stop event (the <c>stop_</c> twin the file names in <c>on_stow_actions</c>, <c>while_deployed_actions</c> or
    /// <c>lifetime_actions</c>, else none). Null when the file names no hum that resolves.
    /// </summary>
    public static Hum? Own(SoundFile? file, Func<string, bool> hasEvent)
    {
        if (file is null) return null;
        string? start = file.Of("on_deploy_actions").Concat(file.Of("while_deployed_actions")).Select(b => b.StartEvent)
            .FirstOrDefault(e => e.Length > 0 && IsHum(e) && hasEvent(e));
        if (start is null) return null;
        string twin = "stop_" + (start.StartsWith("play_", StringComparison.OrdinalIgnoreCase) ? start[5..] : start);
        string stop = file.Of("on_stow_actions").Concat(file.Of("while_deployed_actions")).Concat(file.Of("lifetime_actions"))
            .SelectMany(b => new[] { b.StartEvent, b.StopEvent }).FirstOrDefault(e => e.Equals(twin, StringComparison.OrdinalIgnoreCase) && hasEvent(e)) ?? "";
        return new Hum(start, stop, 0f, false);
    }

    /// <summary>
    /// The hum of a melee wargear in hand: its own (<see cref="Own"/>), a power sword's quieter by <see cref="SwordGainDb"/>; a power sword with none of its own
    /// (the Chaos sword) uses the axe's (<see cref="SwordSubstituteFile"/>), marked as a substitute. Null: not reproducible.
    /// </summary>
    public static Hum? For(string? wargear, Func<string, SoundFile?> fileOf, Func<string, bool> hasEvent)
    {
        if (string.IsNullOrEmpty(wargear)) return null;
        var own = Own(fileOf(wargear), hasEvent);
        if (!IsPowerSword(wargear)) return own;
        if (own is not null) return own with { GainDb = SwordGainDb };
        return Own(fileOf(SwordSubstituteFile), hasEvent) is { } axe ? axe with { GainDb = SwordGainDb, Substitute = true } : null;
    }
}
