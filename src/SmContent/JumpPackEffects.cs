using SmFormats.Baf;

namespace SmContent;

/// <summary>The effects of the landing entry that applies from <see cref="MinDrop"/> metres of drop (<c>distance_from_apex_to_land</c>).</summary>
public sealed record JumpLandingEffects(float MinDrop, IReadOnlyList<FxPlay> Plays);

/// <summary>
/// The effects a jump pack's wargear file plays on touching the ground (M41), read from the default variants (no required upgrade; perk variants such as
/// <c>jumppack_land_extreme_mp</c> are not read):
/// <list type="bullet">
/// <item><see cref="Pound"/>: the <c>play_fx</c> commands of the triggered action <c>actn_gp_hit</c> (the ground pound's impact; <c>jumppack_land_heavy</c> at scale 1 on the install).</item>
/// <item><see cref="Landings"/>: those of each <c>action_land_regular_by_height</c> entry (<c>jumppack_land_normal</c> from a 15 m drop, <c>jumppack_land_light</c> from 0).</item>
/// </list>
/// Which entry a landing takes is the simulation's rule (<see cref="LandingFor"/>: the largest <c>distance_from_apex_to_land</c> not above the drop).
/// </summary>
public sealed record JumpPackEffects(string Name, IReadOnlyList<FxPlay> Pound, IReadOnlyList<JumpLandingEffects> Landings)
{
    // ---- M44: the flight's effects (the default jump_info entry's actions; markers on the worn pack) ----
    /// <summary><c>action_ascending</c>: the thrust from the lift-off to the apex (<c>jumppack_thrust</c> at <c>fx_l_jet</c> / <c>fx_r_jet</c>, scale 0.5; Chaos: <c>chaos_jumppack_thrust</c> at the inside centres).</summary>
    public IReadOnlyList<FxPlay> Ascending { get; init; } = [];
    /// <summary><c>action_apex</c>: at the apex (<c>jumppack_apex_combo</c> at the inside centres).</summary>
    public IReadOnlyList<FxPlay> Apex { get; init; } = [];
    /// <summary><c>action_descending_regular</c>: the fall (<c>jumppack_innate_no_power_combo</c> at the inside centres).</summary>
    public IReadOnlyList<FxPlay> Descending { get; init; } = [];
    /// <summary><c>action_descending_smash</c>: the ground pound's dive (<c>jumppack_decend_thrust</c> at the jets).</summary>
    public IReadOnlyList<FxPlay> Dive { get; init; } = [];
    /// <summary><c>action_idle_by_power</c>: the pack's pilot glow by power, ascending <c>min_power_level</c> (0 no power, 30, 40 low, 55 full).</summary>
    public IReadOnlyList<JumpLandingEffects> IdleByPower { get; init; } = [];
    /// <summary><c>action_smash_target_point</c>: the ground pound's target marker at the hit point (<c>mp_assault_land_reticule</c>, scale 1.5; Chaos <c>mp_chaos_assault_land_reticule</c>).</summary>
    public IReadOnlyList<FxPlay> SmashTarget { get; init; } = [];

    /// <summary>The idle effects for <paramref name="power"/>: the <see cref="IdleByPower"/> entry with the largest level not above it (interpretation of <c>min_power_level</c>).</summary>
    public IReadOnlyList<FxPlay> IdleFor(float power)
    {
        JumpLandingEffects? best = null;
        foreach (var l in IdleByPower) if (l.MinDrop <= power + DropTolerance && (best is null || l.MinDrop > best.MinDrop)) best = l;
        return best?.Plays ?? [];
    }

    /// <summary>Every flight effect name (thrust, apex, fall, dive, idle, target marker), for preloading (no collision descriptions run).</summary>
    public IEnumerable<string> FlightNames => Ascending.Concat(Apex).Concat(Descending).Concat(Dive).Concat(IdleByPower.SelectMany(l => l.Plays)).Concat(SmashTarget)
        .Select(p => p.Name).Where(n => n.Length > 0).Distinct(StringComparer.Ordinal);

    /// <summary>
    /// M44: the jump-pack dash's effects in a jump evade file (<c>sm_mp_core_abil_jump_evade</c>): the triggered action <c>fx_jump_evade</c>'s
    /// <c>play_fx</c> commands (<c>jumppack_thrust</c> at <c>fx_l_jet</c> / <c>fx_r_jet</c>, scale 0.6, lifetime 0.4 s); empty for other files.
    /// </summary>
    public static IReadOnlyList<FxPlay> ReadDash(BafDocument doc) =>
        doc.Root.Table(DashAction) is { } action ? FxBinding.Collect(action).Where(b => b.IsDefault && b.Play is { Name.Length: > 0 }).Select(b => b.Play!).ToList() : [];

    public const string DashAction = "action_data/triggered_actions/fx_jump_evade";

    public const string PoundAction = "action_data/triggered_actions/actn_gp_hit";

    /// <summary>Tolerance of the drop comparison (m), the simulation's (<c>JumpPack.LandingBlast</c>: exactly 15 m takes the 15 m entry).</summary>
    const float DropTolerance = 1e-4f;

    /// <summary>The effects for a landing after <paramref name="drop"/> metres from the highest point: the entry with the largest <c>MinDrop</c> not above it; none when no entry fits.</summary>
    public IReadOnlyList<FxPlay> LandingFor(float drop)
    {
        JumpLandingEffects? best = null;
        foreach (var l in Landings)
            if (l.MinDrop <= drop + DropTolerance && (best is null || l.MinDrop > best.MinDrop)) best = l;
        return best?.Plays ?? [];
    }

    /// <summary>The effects of the lightest landing entry (the smallest <c>distance_from_apex_to_land</c>; <c>jumppack_land_light</c>: smoke sprays and debris sprites), which the pound adds as dust (M41, interpretation).</summary>
    public IReadOnlyList<FxPlay> LightLanding => Landings.OrderBy(l => l.MinDrop).FirstOrDefault()?.Plays ?? [];

    /// <summary>Every effect name the pack can play (pound and every landing), for preloading.</summary>
    public IEnumerable<string> Names => Pound.Concat(Landings.SelectMany(l => l.Plays)).Select(p => p.Name).Where(n => n.Length > 0).Distinct(StringComparer.Ordinal);

    /// <summary>Reads a wargear file's jump pack effects; null when the file has no jump pack ability.</summary>
    public static JumpPackEffects? Read(string name, BafDocument doc)
    {
        if (doc.Root.Table(JumpPackTuning.Ability) is not { } ability) return null;
        var pound = doc.Root.Table(PoundAction) is { } action
            ? FxBinding.Collect(action).Where(b => b.IsDefault && b.Play is { Name.Length: > 0 }).Select(b => b.Play!).ToList()
            : [];
        var landings = new List<JumpLandingEffects>();
        var info = WeaponTuning.DefaultEntry(ability.Table("jump_info"), null);
        foreach (var e in info?.Table("action_land_regular_by_height")?.Entries.Where(e => e.Type == BafType.Table) ?? [])
        {
            var t = e.AsTable();
            if (t.Float("distance_from_apex_to_land") is not float drop || !float.IsFinite(drop) || drop < 0f) continue;
            var plays = t.Table("action") is { } a
                ? FxBinding.Collect(a).Where(b => b.IsDefault && b.Play is { Name.Length: > 0 }).Select(b => b.Play!).ToList()
                : [];
            landings.Add(new JumpLandingEffects(drop, plays));
        }
        IReadOnlyList<FxPlay> Plays(string action) => info?.Table(action) is { } t
            ? FxBinding.Collect(t).Where(b => b.IsDefault && b.Play is { Name.Length: > 0 }).Select(b => b.Play!).ToList() : [];
        var idle = new List<JumpLandingEffects>();
        foreach (var e in info?.Table("action_idle_by_power")?.Entries.Where(e => e.Type == BafType.Table) ?? [])
        {
            var t = e.AsTable();
            if (t.Float("min_power_level") is not float level || !float.IsFinite(level) || level < 0f) continue;
            idle.Add(new JumpLandingEffects(level, t.Table("action") is { } a ? FxBinding.Collect(a).Where(b => b.IsDefault && b.Play is { Name.Length: > 0 }).Select(b => b.Play!).ToList() : []));
        }
        return new JumpPackEffects(name, pound, landings.OrderBy(l => l.MinDrop).ToList())
        {
            Ascending = Plays("action_ascending"), Apex = Plays("action_apex"), Descending = Plays("action_descending_regular"), Dive = Plays("action_descending_smash"),
            IdleByPower = idle.OrderBy(l => l.MinDrop).ToList(), SmashTarget = Plays("action_smash_target_point"),
        };
    }
}
