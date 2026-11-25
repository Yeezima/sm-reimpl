namespace SmSim.Extras.Warfront.Chaos.Finale;

/// <summary>
/// The Chaos finale's numbers (Chaos Warfront C5; brief 2026-11-24 rev 1, design "Finale"): the bodyguard fight, then the duel relay against the Chaos Lord. Every value here that is not the brief's is
/// ours, an estimate for the playtest (spec rows "Chaos Warfront: the finale"; the refine register lists them).
/// </summary>
public static class WarfrontDuelRules
{
    /// <summary>The finale's own rules version (its log line); <see cref="WarfrontRules.Version"/> carries the run's.</summary>
    public const int Version = 1;

    /// <summary>The pause between one challenger's end and the next one's entry (design: 3 s).</summary>
    public const float HandoffSeconds = 3f;

    /// <summary>The entry spot: this far from the Lord's spot toward the plaza's gate (ours: inside his charge's reach of 6 to 15 m, short of the leap's 18 m).</summary>
    public const float EntryDistance = 15f;

    /// <summary>The duelist past the plaza's radius by this much is put back on the entry spot without a heal (ours; never a loss).</summary>
    public const float PlazaPullBack = 2f;

    /// <summary>The Lord past the plaza's radius by this much is put back on his spot without a heal (ours; his charge can carry him out).</summary>
    public const float LordPullBack = 10f;

    // ---- the Lord in the duel ----------------------------------------------------------------------------------------------------------------

    /// <summary>The slam's telegraph and both recoveries are this many times the Warboss's (design: "readable openings"; ours: 1.5). The slam's wind-up goes 0.8 s to 1.2 s.</summary>
    public const float SlamWindUpScale = 1.5f, RecoveryScale = 1.5f;

    /// <summary>The warp leap: the duelist beyond <see cref="LeapBeyond"/> m for <see cref="LeapAfterSeconds"/> s (design), a marker at his position for <see cref="LeapMarkerSeconds"/> s, then the Lord lands there
    /// with a slam of <see cref="LeapRadius"/> m (the slam's damage), and <see cref="LeapCooldownSeconds"/> s before the next (marker, radius and cooldown ours).</summary>
    public const float LeapBeyond = 18f, LeapAfterSeconds = 4f, LeapMarkerSeconds = 1f, LeapRadius = 4f, LeapCooldownSeconds = 8f;

    /// <summary>The hybrid bolt: only beyond <see cref="BoltBeyond"/> m (design), a volley of the Chaos Marine bolter row's rounds and period (data row, ours), each round its damage times <see cref="BoltDamageScale"/>
    /// (the bosses' danger, <see cref="SmSim.Extras.Musou.MusouBossRules.DangerScale"/>).</summary>
    public const float BoltBeyond = 12f, BoltDamageScale = SmSim.Extras.Musou.MusouBossRules.DangerScale;

    // ---- the wire ----------------------------------------------------------------------------------------------------------------------------

    /// <summary>The duel state goes on every change and this often a second (ours).</summary>
    public const int StateHz = 2;
    /// <summary>Each of the Lord's telegraphs is sent this many times, this many ticks apart (as the breach closures).</summary>
    public const int TelegraphRepeats = 3, TelegraphRepeatTicks = 3;
}
