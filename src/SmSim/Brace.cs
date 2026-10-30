namespace SmSim;

/// <summary>Where a player is in the heavy bolter's braced stance (<see cref="Brace"/>).</summary>
public enum BracePhase : byte
{
    /// <summary>Not braced (the weapon's primary fire mode).</summary>
    None = 0,
    /// <summary>Planting the feet: the graph's "Heavy Bolter Switch Mode" state (<c>SM_RNG_TB_Idle_to_BraceIdle</c>) up to <c>BRANCH_Idle_to_Brace</c>.</summary>
    Entering = 1,
    /// <summary>Braced: <c>MP_SM_HB_Brace</c>'s "Brace Fire" (the secondary fire mode; the braced ability fires).</summary>
    Braced = 2,
    /// <summary>Leaving the stance: "Exit Brace Fire" (<c>SM_RNG_TB_BraceIdle_to_idle</c>) up to <c>BRANCH_Exit_Brace</c>.</summary>
    Exiting = 3,
}

/// <summary>
/// The heavy bolter's braced fire (wargear <c>ability_ranged_braced</c>, the secondary fire mode whose <c>upgrade_granted</c> is
/// <c>mp_fire_mode_heavy_bolter_braced_fire</c>; <c>SmContent.WeaponTuning</c>): the braced shot's <see cref="ShotCooldown"/> (0.1 s
/// against the primary's 0.233) and <see cref="HeatPerShot"/> (1.8 against 3; Heavy Bolter Coolant 0.7). Damage, range and
/// fall-off are the primary's on the install (the reader notes a difference). <see cref="EnterTime"/> and <see cref="ExitTime"/>
/// come from the behaviour graphs (<c>SmContent.BraceTuning</c>); 0 until set (the stance then changes at once).
/// </summary>
/// <param name="ShotCooldown">The braced ability's <c>basic_weapon_tuning</c> <c>shot_cooldown</c>, seconds.</param>
/// <param name="HeatPerShot">The braced ability's <c>fire_sim</c> <c>apply_heat</c>, heat units per shot.</param>
public sealed record BraceSpec(float ShotCooldown, float HeatPerShot)
{
    /// <summary>
    /// Read with the Heavy Bolter Expertise perk's upgrade string (<see cref="ExpertiseUpgrade"/>; M43 variant): the graph then sets
    /// <c>FLT_Brace_Entry_Time</c> (the entry clip's playback speed) to its perk value (2 on the install).
    /// </summary>
    public bool Expertise { get; init; }

    /// <summary>The braced fire mode's upgrade string (<c>common_data/fire_mode_secondary/entry/upgrade_granted</c>: <c>mp_fire_mode_heavy_bolter_braced_fire</c>); the exit's speed modifier keys on it.</summary>
    public string FireMode { get; init; } = "";

    /// <summary>Seconds from the Reload press to braced fire (the entry clip's <c>BRANCH_Idle_to_Brace</c> at its playback speed).</summary>
    public float EnterTime { get; init; }

    /// <summary>Seconds from the Reload press until the stance is left (the exit clip's <c>BRANCH_Exit_Brace</c> at its playback speed).</summary>
    public float ExitTime { get; init; }

    /// <summary>The upgrade string of Heavy Bolter Expertise (<c>mp_perk_weap_heavy_bolter_braced_fire_time</c>; the graphs' <c>BOOL_UPGRADE_</c> variable names it).</summary>
    public const string ExpertiseUpgrade = "mp_perk_weap_heavy_bolter_braced_fire_time";

    /// <summary>Writes every field for a config hash (null: a single false).</summary>
    public static void Write(BinaryWriter w, BraceSpec? b)
    {
        w.Write(b is not null);
        if (b is null) return;
        w.Write(b.ShotCooldown); w.Write(b.HeatPerShot); w.Write(b.Expertise); w.Write(b.FireMode ?? ""); w.Write(b.EnterTime); w.Write(b.ExitTime);
    }
}

/// <summary>
/// The heavy bolter's braced stance (fidelity gap 7). Evidence (spec/behavior.md "Brace and vent"): the weapon's
/// <c>ability_switch_mode</c> (<c>switch_fire_mode</c>), the UI text "%ABILITY_RELOAD% to Change Stance", and the graph: every
/// heavy bolter state goes to "Heavy Bolter Switch Mode" <c>on BTN_Reload</c>, and "Brace Fire" goes to "Exit Brace Fire"
/// <c>on BTN_Reload</c>. Rules:
/// <list type="bullet">
/// <item>A Reload press edge with a brace weapon in hand starts the entry (<see cref="BracePhase.Entering"/>) when the player is on
/// the ground and free (<see cref="CanStart"/>: no melee, throw, evade, switch, charge, pending shot, jump-pack jump, reaction or
/// charge; the sprint ends: the graph's sprint state also goes to Switch Mode on Reload).</item>
/// <item>Entering, the player neither moves nor fires; no sprint, evade, melee, throw, switch or aim mode (the entry clip is a whole-body
/// sequence with no locomotion; its modifiers disallow aim and weapon switching). The stance is reached after
/// <see cref="BraceSpec.EnterTime"/>.</item>
/// <item>Braced, the player does not walk (no locomotion in <c>MP_SM_HB_Brace</c>; the body only turns, by its turn clips): the stick, sprint and
/// evade are ignored, and so are melee (the braced state sets melee mode off and the ranged-to-melee transitions need
/// <c>!BOOL_Heavy_Bolter_Secondary</c>) and weapon switching (a disable-weapon-switch modifier). Fire shoots the braced shot
/// (<see cref="Braced"/>); aim mode and the thrown item work (the brace graph's own Use Equipment state returns to Brace Fire).
/// A Reload press starts the exit.</item>
/// <item>Exiting, the player may walk (the exit blends the locomotion graph on the legs) but not fire, sprint, melee, throw, switch or
/// aim; a Jump press that starts an evade (<see cref="Evade.StartsFromRest"/>: the class has a non-dash evade, its spacing is over, on the ground)
/// ends the exit at once and the evade starts (the exit's own "Heavy Bolter Evade"); a Jump press that starts none changes nothing. The stance is left
/// after <see cref="BraceSpec.ExitTime"/>.</item>
/// <item>Reactions (owner's recollection, 2026-10-28: "stuns and hits did not knock you out"): a player in any brace phase does not react (no stun,
/// stagger, interrupt or knockback; <see cref="Reaction.CanReact"/>), the damage still applies, like hyper armour. Braced: the owner's description;
/// entering and exiting: ours ("you could not cancel the brace once initiated until you were fully braced"). So nothing cancels a transition but
/// death; Reload during the entry is ignored, and once braced Reload starts the exit. Death and respawn clear the stance.</item>
/// </list>
/// Which of these the original enforces in code (not in the graph) is not known; the rules beyond the graph are tagged "ours" in the spec.
/// </summary>
public static class Brace
{
    /// <summary>Most ticks a transition may last (<see cref="PlayerState.BraceTicks"/> travels in 6 bits of the own record, protocol 29).</summary>
    public const int MaxTicks = 63;

    /// <summary>The stance this tick (<see cref="Begin"/>).</summary>
    public readonly record struct Tick(BracePhase Phase, int Ticks)
    {
        /// <summary>The feet are planted: entering or braced (no walking, sprint, evade or melee).</summary>
        public bool Planted => Phase is BracePhase.Entering or BracePhase.Braced;
        /// <summary>Between modes: no firing, throwing or aim mode.</summary>
        public bool Changing => Phase is BracePhase.Entering or BracePhase.Exiting;
    }

    /// <summary>
    /// Whether a player may start bracing this tick (state at the tick's start; see the class summary): also never with a jump pack
    /// (<paramref name="c"/>: the class's pack; no MP pack class carries a heavy bolter, the guard keeps a config that gives one a brace weapon out of the stance).
    /// </summary>
    public static bool CanStart(PlayerState s, SimConfig c) => CanStart(s) && Loadouts.JumpPack(c, s) is null;

    /// <summary>Whether a player's stance negates reactions (any phase; owner's recollection for the braced stance, ours for the transitions).</summary>
    public static bool ReactionImmune(PlayerState s) => s.Brace != BracePhase.None;

    /// <summary>The state part of <see cref="CanStart(PlayerState, SimConfig)"/>.</summary>
    public static bool CanStart(PlayerState s) => s.Alive && s.Grounded && s.MeleeStep == 0 && !s.Throwing && s.EvadeTicks == 0 && s.SwitchLeft <= 0f
        && !s.Charging && s.PendingShot == 0 && s.Jump == JumpPhase.None && s.Reaction == ReactionKind.None && s.Rush == RushPhase.None;

    /// <summary>Ticks a transition of <paramref name="seconds"/> takes: the tick in which the elapsed time reaches it (the press tick counts 1; at least 1).</summary>
    public static int TicksFor(float seconds, float tickSeconds) =>
        Math.Max(1, (int)MathF.Ceiling((float.IsFinite(seconds) ? MathF.Max(0f, seconds) : 0f) / tickSeconds - 1e-3f));   // a thousandth of a tick: the graphs' frame-aligned times

    /// <summary>
    /// The stance for this tick from the state at its start: <paramref name="spec"/> is the in-hand weapon's (null: no stance);
    /// <paramref name="reloadPress"/> a Reload press edge that is not a remote detonation's; <paramref name="evadeStarts"/> a Jump press
    /// edge that starts an evade this tick (<see cref="Evade.StartsFromRest"/>). Nothing else interrupts a transition (reactions are negated in the stance).
    /// </summary>
    public static Tick Begin(PlayerState s, BraceSpec? spec, bool reloadPress, bool evadeStarts, SimConfig c)
    {
        float tickSeconds = c.TickSeconds;
        if (spec is null || !s.Alive) return default;
        switch (s.Brace)
        {
            case BracePhase.None:
                return reloadPress && CanStart(s, c) ? Advance(BracePhase.Entering, 1, spec.EnterTime, tickSeconds) : default;
            case BracePhase.Entering:
                return Advance(BracePhase.Entering, s.BraceTicks + 1, spec.EnterTime, tickSeconds);
            case BracePhase.Braced:
                return reloadPress ? Advance(BracePhase.Exiting, 1, spec.ExitTime, tickSeconds) : new Tick(BracePhase.Braced, 0);
            case BracePhase.Exiting:
                if (evadeStarts) return default;   // the exit's evade: the stance is left now (and the evade starts this tick)
                return Advance(BracePhase.Exiting, s.BraceTicks + 1, spec.ExitTime, tickSeconds);
            default:
                return default;
        }
    }

    /// <summary>A transition after <paramref name="ticks"/> ticks: over (entering: braced; exiting: none) once they reach its time.</summary>
    static Tick Advance(BracePhase phase, int ticks, float seconds, float tickSeconds)
    {
        if (ticks < TicksFor(seconds, tickSeconds)) return new Tick(phase, ticks);
        return phase == BracePhase.Entering ? new Tick(BracePhase.Braced, 0) : default;
    }

    /// <summary>
    /// The input as the stance lets it act (<paramref name="input"/> unchanged outside it): planted (entering, braced), no stick;
    /// in any phase no Sprint, Jump (an exit's evade has ended the stance before), melee, heavy melee or weapon switching; entering or exiting, no Fire, Grenade or Aim; Reload never (it was the
    /// stance's own press). The yaw and pitch are kept (the braced body turns in place).
    /// </summary>
    public static PlayerInput Mask(PlayerInput input, Tick tick)
    {
        if (tick.Phase == BracePhase.None) return input;
        var cleared = InputButtons.Reload | InputButtons.Melee | InputButtons.MeleeHeavy | InputButtons.Switch | InputButtons.SlotPistol | InputButtons.SlotPrimary | InputButtons.SlotSecond | InputButtons.Sprint;
        // Jump too: during an exit a Jump that starts an evade has already ended the stance (Begin), so one left here starts nothing.
        cleared |= InputButtons.Jump;
        if (tick.Changing) cleared |= InputButtons.Fire | InputButtons.Grenade | InputButtons.Aim;
        var masked = input with { Buttons = input.Buttons & ~cleared };
        return tick.Planted ? masked with { MoveX = 0f, MoveY = 0f } : masked;
    }

    /// <summary>The weapon as braced fire shoots it: the braced shot's cooldown and heat per shot (the rest is the primary's).</summary>
    public static WeaponSpec Braced(WeaponSpec w) => w.Brace is not { } b ? w : w with
    {
        ShotCooldown = b.ShotCooldown,
        Heat = w.Heat is { } h ? h with { PerShot = b.HeatPerShot } : null,
        BurstLength = 0, BurstInterval = 0f,
    };

    /// <summary>Whether a brace is usable: finite cooldown above 0, heat at least 0, transitions within <see cref="MaxTicks"/> ticks.</summary>
    public static bool SpecOk(BraceSpec? b, float tickSeconds) => b is null || (float.IsFinite(b.ShotCooldown) && b.ShotCooldown > 0f
        && float.IsFinite(b.HeatPerShot) && b.HeatPerShot >= 0f
        && float.IsFinite(b.EnterTime) && b.EnterTime >= 0f && TicksFor(b.EnterTime, tickSeconds) <= MaxTicks
        && float.IsFinite(b.ExitTime) && b.ExitTime >= 0f && TicksFor(b.ExitTime, tickSeconds) <= MaxTicks);
}
