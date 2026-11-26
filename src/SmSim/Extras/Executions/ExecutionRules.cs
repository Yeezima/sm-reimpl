namespace SmSim.Extras.Executions;

/// <summary>
/// The executions' rules (executions X1; owner-approved brief 2026-11-25, design state/design-executions-1125.md; docs/added-content.md section 13). Every value is ours or the owner's
/// (spec/behavior.md "Added content: Executions"), none is from the original's data. <see cref="Version"/> and every tunable here are written into the config hash's executions block
/// (<c>SimConfig.ExtrasHash</c>, <c>SimConfig.Extras.cs</c>) only when the match has <see cref="ContentExtras.Executions"/>, so raising one never touches the faithful or Musou hashes.
/// Raise <see cref="Version"/> for any executions-only change to the simulation or the wire (tag 50, <c>ExecutionWire</c>).
/// </summary>
public static class ExecutionRules
{
    /// <summary>The executions version in the config hash (1: X1, the contract and the switch; 2: X3, the state machine, Classic PvP Extended and the 36-byte Start; 3: X4, Warfront on Orks: the pick, the held Ork, the invulnerable move, the reward and its overshield, the cooldown; 4: X4b, owner 2026-11-26: Warfront on Chaos (the Chaos battle's
    /// kinds, the Chosen eligible, the Lord only by the duelist under the duel's gate, the finale's refusal), and the combat knife on the chainsword's rows for every non-Ork victim).</summary>
    public const int Version = 4;

    /// <summary>
    /// Classic PvP Extended: how long after a confirmed lethal melee kill the killer may tap Execute for that kill (seconds; ours, the architect's recommendation, uncontested by the owner 2026-11-25).
    /// The default of <see cref="SimConfig.ExecutionWindowSeconds"/>, which a match may tune (hashed).
    /// </summary>
    public const float ClassicWindowSeconds = 0.8f;

    /// <summary>Warfront: the cooldown after a successful execution, counted from its completion (seconds; the owner's brief, "recommended start: animation completion"). An interrupted execution starts none.</summary>
    public const float WarfrontCooldownSeconds = 10f;

    /// <summary>
    /// Warfront: the overshield a successful execution fills, in armour bars of the executor's body (<c>ShieldMax</c>): 1, so the armour reads 200 % in all, never 300 % (owner's brief).
    /// A later success refreshes it to this cap; it never stacks.
    /// </summary>
    public const float OvershieldBars = 1f;

    /// <summary>
    /// Classic PvP Extended: the longest forward slide of the killer onto the corpse (metres; <b>ours</b>, lead 40 2026-11-25, provisional, the owner tunes the feel). Classic plays only the
    /// Chaos Marine rows, each a single clip (gaps 1.15 to 1.46 m, no distance variants), so with Warfront's 1 m (<see cref="ExecutionAlign.MaxSlide"/>) a corpse beyond about 2.3 m, most melee kills, could
    /// never be finished. The slide moves at <see cref="ExecutionAlign.SlideSpeed"/> (6 m/s: 2.5 m in about 13 ticks, 0.42 s) and passes the same terrain checks. Hashed.
    /// </summary>
    public const float ClassicMaxSlide = 2.5f;

    /// <summary>The window in simulation ticks (rounded; at least 1) for <paramref name="c"/>'s tick rate and <see cref="SimConfig.ExecutionWindowSeconds"/>.</summary>
    public static int WindowTicks(SimConfig c) => Math.Max(1, (int)MathF.Round(c.ExecutionWindowSeconds * c.TickRate));

    /// <summary>
    /// The tick a press counts at: its <paramref name="viewTick"/> (what the killer saw), never further back than <see cref="SimConfig.MaxRewindTicks"/> from <paramref name="tick"/> (the lag
    /// compensation's cap) and never after it; a view tick of 0 (none) counts as <paramref name="tick"/>. A press is inside the window of a kill at <c>killTick</c> when <c>killTick &lt;= PressTick &lt;= killTick + WindowTicks</c>.
    /// </summary>
    public static uint PressTick(SimConfig c, uint tick, uint viewTick)
    {
        if (viewTick == 0) return tick;   // X3 (lead 40): an input without a view tick (a bot, a test) counts at the current tick, not the oldest one the rewind allows
        uint floor = tick > (uint)Math.Max(0, c.MaxRewindTicks) ? tick - (uint)Math.Max(0, c.MaxRewindTicks) : 0u;
        return Math.Clamp(viewTick, floor, tick);
    }

    /// <summary>Whether a press at <paramref name="pressTick"/> (<see cref="PressTick"/>) falls inside the window of a kill at <paramref name="killTick"/>.</summary>
    public static bool InWindow(SimConfig c, uint killTick, uint pressTick) => pressTick >= killTick && pressTick - killTick <= (uint)WindowTicks(c);

    /// <summary>
    /// Classic PvP Extended (owner 2026-11-25, "Hold the death pose briefly"): how long a melee-killed victim's death pose is held before the ragdoll, in seconds: the window itself.
    /// Presentation only: the victim's respawn timer is never delayed.
    /// </summary>
    public static float DeathPoseHoldSeconds(SimConfig c) => c.ExecutionWindowSeconds;

    /// <summary>The Warfront cooldown in ticks (rounded).</summary>
    public static int CooldownTicks(SimConfig c) => (int)MathF.Round(WarfrontCooldownSeconds * c.TickRate);

    /// <summary>The overshield's cap above a body's <paramref name="shieldMax"/>.</summary>
    public static float OvershieldCap(float shieldMax) => MathF.Max(0f, shieldMax) * OvershieldBars;

    /// <summary>The armour a successful Warfront execution leaves: the full bar plus the overshield (2 x <paramref name="shieldMax"/>).</summary>
    public static float RewardShield(float shieldMax) => MathF.Max(0f, shieldMax) + OvershieldCap(shieldMax);

    /// <summary>The overshield part of <paramref name="shield"/> (0 at or under <paramref name="shieldMax"/>), clamped to the cap.</summary>
    public static float Overshield(float shield, float shieldMax) => Math.Clamp(shield - shieldMax, 0f, OvershieldCap(shieldMax));

    /// <summary>
    /// Writes the executions block of the config hash (only under <see cref="ContentExtras.Executions"/>; <c>SimConfig.Extras.cs</c> calls it): the rules here, then (X3) the
    /// choice, alignment and row-data versions and the wire order (<see cref="ExecutionRows.All"/> ids). The install's rows (<see cref="SimConfig.Executions"/>) are not hashed.
    /// </summary>
    public static void Hash(BinaryWriter w, SimConfig c) =>
        WriteBlock(w, c, ExecutionChoice.RulesVersion, ExecutionAlign.RulesVersion, ExecutionRows.DataVersion, ExecutionRows.All.Select(r => r.Id).ToArray());

    /// <summary>
    /// The executions block with the code's shared versions and the wire order given (<see cref="Hash"/> passes the real ones; the tests pass others to show each one moves the hash): this
    /// class's rules, then the choice's, the alignment's and the rows' data versions and the ids of the rows in wire order.
    /// </summary>
    public static void WriteBlock(BinaryWriter w, SimConfig c, int choiceVersion, int alignVersion, int dataVersion, IReadOnlyList<string> wireIds)
    {
        w.Write("executions");
        w.Write(Version);
        w.Write(c.ExecutionWindowSeconds);
        w.Write(WarfrontCooldownSeconds);
        w.Write(OvershieldBars);
        w.Write(ClassicMaxSlide);
        w.Write(choiceVersion); w.Write(alignVersion); w.Write(dataVersion);
        w.Write(wireIds.Count);
        foreach (var id in wireIds) w.Write(id);
    }
}
