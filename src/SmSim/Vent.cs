namespace SmSim;

/// <summary>
/// A plasma weapon's heat vent (wargear <c>ability_utility</c> of type <c>heat_vent</c>; <c>SmContent.WeaponTuning</c>): heat
/// vented per second by the weapon's heat fraction (<c>heat_vented</c>: 45 on every MP plasma weapon; the plasma pistol's
/// <c>ability_utility_upgraded</c>, which the Plasma Heat Sink perk selects, 70), the multiplier while overheated
/// (<c>overheat_heat_venting_multiplier</c>: 1, the plasma cannon 0.6) and the presentation stages' heat fractions
/// (<c>vent_stages</c>: low from 0.01, high from 0.5; the "medium" stage's 0 is never reached).
/// </summary>
/// <param name="Rates">(heat fraction, heat per second) from <c>heat_vented</c>, fractions ascending.</param>
/// <param name="OverheatMultiplier">The rate's factor while the weapon is overheated.</param>
/// <param name="LowFrom">The low-heat stage's <c>heat_percent</c> (its sounds and effects play from this fraction).</param>
/// <param name="HighFrom">The high-heat stage's <c>heat_percent</c>.</param>
public sealed record VentSpec(IReadOnlyList<DistancePoint> Rates, float OverheatMultiplier, float LowFrom, float HighFrom)
{
    /// <summary>The wargear ability the values come from (<c>ability_utility</c>, or <c>ability_utility_upgraded</c> with the perk).</summary>
    public string Ability { get; init; } = "ability_utility";

    /// <summary>Writes every field for a config hash (null: a single false).</summary>
    public static void Write(BinaryWriter w, VentSpec? v)
    {
        w.Write(v is not null);
        if (v is null) return;
        w.Write(v.Rates.Count); foreach (var p in v.Rates) { w.Write(p.Distance); w.Write(p.Value); }
        w.Write(v.OverheatMultiplier); w.Write(v.LowFrom); w.Write(v.HighFrom); w.Write(v.Ability);
    }
}

/// <summary>What a vent shows (<see cref="Vent.StageOf"/>): the wargear's <c>vent_stages</c> and the graph's vent states.</summary>
public enum VentStage : byte { NoHeat = 0, Low = 1, High = 2, Overheat = 3 }

/// <summary>
/// Venting a plasma weapon (fidelity gap 7). Evidence (spec/behavior.md "Brace and vent"): the UI text "%ABILITY_RELOAD% to Vent Heat"
/// and the graph: the plasma gun's, pistol's and cannon's states go to their Vent state <c>on BTN_Reload</c> and <c>if
/// BOOL_Wargear_Overheated</c>; the Vent state leaves when <c>BOOL_Wargear_Vent_NoHeat</c>, on Sprint, on the evade and on Fire
/// (the pistol's on <c>EVT_Plasma_Pistol_Fire</c>), and its blend keeps the locomotion graph (the player walks while venting). The owner's recollection
/// (2026-10-28; an estimate to check in a playtest): venting cooled the gun on top of normal cooling, about 6 s to cool fully, and everything but walking
/// at normal walk speed stopped it. Rules:
/// <list type="bullet">
/// <item>A vent runs with a vent weapon in hand while the player is free (<see cref="Free"/>); it starts on a Reload press edge (a charge
/// that Reload cancels is cancelled, interpretation) or by itself while the weapon is overheated, and only with heat above 0.</item>
/// <item>Each tick of a vent the heat falls by the rate at the tick's heat fraction (<see cref="RateAt"/>) times the overheat multiplier
/// while overheated, on top of the passive cooling (owner's recollection); the overheat ends at the recovery amount as when cooling.
/// The time since the last heat gain keeps counting (the cooling's own delay is unchanged). With the data's 45 heat/s a full gun cools in about 2 s,
/// not the owner's 6 s (spec "Brace and vent").</item>
/// <item>It ends at heat 0, when the player is no longer free, or when Fire is down with the weapon not overheated (the shot then
/// leaves as usual; while overheated Fire is ignored and the vent runs on).</item>
/// </list>
/// The plasma cannon's overheat also slows its carrier (<see cref="OverheatSpeed"/>).
/// </summary>
public static class Vent
{
    /// <summary>
    /// Heat vented per second at <paramref name="fraction"/> of the overheat amount: the entry with the smallest fraction at or above it
    /// (the table's top entry above every one, its lowest below every one; **interpretation**: on the install all entries are equal);
    /// 0 without entries.
    /// </summary>
    public static float RateAt(VentSpec v, float fraction)
    {
        if (v.Rates.Count == 0) return 0f;
        foreach (var p in v.Rates)
            if (p.Distance + 1e-6f >= fraction) return p.Value;
        return v.Rates[^1].Value;
    }

    /// <summary>
    /// Whether the player only stands or walks this tick (owner's recollection: everything but walking stops the vent; standing still counts: ours):
    /// <paramref name="busy"/> is false (no reaction or charge, melee, throw or Grenade press, evade, weapon switch under way or asked, jump-pack jump, dive or
    /// recovery, pending charged shot, aim mode), and no sprint: the Sprint bit applied
    /// (<see cref="Combat.SprintApplies"/>) with the stick forward on the ground (the graph's vent state leaves on
    /// Sprint and the sprint state does not vent: an overheated player who keeps sprinting only cools passively).
    /// </summary>
    public static bool Free(bool busy, PlayerInput input, PlayerState s, SimConfig c) =>
        !busy && !(Combat.SprintApplies(s, input, c) && input.Sanitized().MoveY > 0f && s.Grounded);

    /// <summary>
    /// Whether the in-hand weapon vents this tick: it has a vent and heat; the player is <paramref name="free"/>; a vent was running,
    /// <paramref name="reloadPress"/> asks for one or the weapon is overheated; and Fire is not down on a weapon that may fire.
    /// </summary>
    public static bool Runs(WeaponSpec? weapon, PlayerState s, bool free, bool reloadPress, bool fireDown) =>
        weapon is { Vent: not null, Heat: not null } && free && s.Heat > 0f
        && (s.Venting || reloadPress || s.Overheated) && !(fireDown && !s.Overheated);

    /// <summary>One tick of venting on top of this tick's cooling (<see cref="Combat.Cool"/>'s result goes in; see the class summary).</summary>
    public static (float Heat, bool Overheated) Step(VentSpec v, HeatSpec h, float heat, bool overheated, float dt)
    {
        float rate = RateAt(v, h.Overheat > 0f ? heat / h.Overheat : 0f) * (overheated ? v.OverheatMultiplier : 1f);
        heat = Math.Clamp(heat - rate * dt, 0f, h.Overheat);
        if (overheated && heat <= h.Recovery) overheated = false;
        return (heat, overheated);
    }

    /// <summary>The stage a vent at this heat shows: overheat while overheated, high from <see cref="VentSpec.HighFrom"/>, low from <see cref="VentSpec.LowFrom"/>.</summary>
    public static VentStage StageOf(VentSpec v, HeatSpec h, float heat, bool overheated)
    {
        if (overheated) return VentStage.Overheat;
        float f = h.Overheat > 0f ? heat / h.Overheat : 0f;
        return f + 1e-6f >= v.HighFrom ? VentStage.High : f > 0f && f + 1e-6f >= v.LowFrom ? VentStage.Low : VentStage.NoHeat;
    }

    /// <summary>
    /// The carrier's speed factor from a weapon's overheat (the plasma cannon: <c>actions/overheat</c> applies <c>speed_maximum</c> × 0.5 for
    /// 1.5 s): <see cref="HeatSpec.OverheatSpeed"/> while overheated and less than <see cref="HeatSpec.OverheatSpeedTime"/> after the heat gain that
    /// overheated it (the time since the last gain counts from the overheat: nothing adds heat while overheated); 1 otherwise. The holstered
    /// weapon's counts too (the modifier is on the player).
    /// </summary>
    public static float OverheatSpeed(HeatSpec? h, bool overheated, float since) =>
        h is { OverheatSpeedTime: > 0f } && overheated && since + 1e-5f < h.OverheatSpeedTime ? h.OverheatSpeed : 1f;

    /// <summary>Whether a vent is usable: 1 to 255 rate points with fractions ascending and finite non-negative values, a finite non-negative multiplier and stage fractions.</summary>
    public static bool SpecOk(VentSpec? v) => v is null || (v.Rates is { Count: > 0 and <= 255 } && v.Ability is not null
        && v.Rates.All(p => float.IsFinite(p.Distance) && p.Distance >= 0f && float.IsFinite(p.Value) && p.Value >= 0f)
        && v.Rates.Zip(v.Rates.Skip(1)).All(q => q.Second.Distance >= q.First.Distance)
        && float.IsFinite(v.OverheatMultiplier) && v.OverheatMultiplier >= 0f
        && float.IsFinite(v.LowFrom) && v.LowFrom >= 0f && float.IsFinite(v.HighFrom) && v.HighFrom >= 0f);
}
