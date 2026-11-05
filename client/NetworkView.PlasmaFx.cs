using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmContent;
using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// fix/plasma-visuals (owner, 2026-11-06: "make a pass on the visual effects of plasma gun, plasma cannon, and plasma pistol ... match exactly the original game"; client presentation only,
/// no simulation or protocol change). The shots themselves (muzzle, tracer, the charged projectile's trail, impacts) already played the data's commands (<see cref="FxShot"/>); what the
/// plasma wargear files also bind and nothing played, now does, at the markers they name, attached:
/// <list type="bullet">
/// <item><b>The charge build</b>: each charge state's <c>charging_action</c> effects (the plasma gun's <c>plasma_charging_medium</c> at the muzzle, then at the charged stage
/// <c>plasma_charging_ready</c> and <c>plasma_gun_innate_secondary</c> in the chamber; the pistol's <c>plasma_charging</c>, its ready glow and arcs; the cannon's ready glow) while
/// that state lasts, for every player (others: the state by the time since their charge flag rose). This replaces the placeholder sphere for these weapons.</item>
/// <item><b>The idle chamber glow</b>: <c>while_deployed_actions</c> behind the fire-mode upgrades (<c>plasma_gun_primary_fire_mode</c> ...: <c>plasma_gun_innate</c> in the
/// chamber, the cannon's <c>plasma_cannon_innate</c>), the primary mode's while the weapon is in hand, the secondary mode's while it charges (<b>interpretation</b>: the game sets
/// those upgrades itself; which one is set when is not in the files).</item>
/// <item><b>Heat</b> (own player only: the others' heat is not sent): <c>common_data/heat</c>'s <c>high_heat</c> effects (the yellow warning light, the chamber's high-heat glow)
/// while the heat is at <c>high_heat_amount</c> or more, <c>overheat</c>'s (the red light for 3 s, the overheat blast at the clip, the cool-down glow) when it overheats and
/// <c>overheat_complete</c>'s (the green light for 2 s) when it recovers. <c>overheat_delay</c> is 0 s on every multiplayer weapon: its effects are not played.</item>
/// </list>
/// A state's effects loop while it lasts (<b>ours</b>: the player's repeat) and stop at its end: <c>destroy</c> at once, <c>stop_emitting</c> fading over
/// <see cref="PlasmaStopFade"/> s (ours: the player draws particles as a function of age and cannot stop emitting alone). Test aids: <c>--no-plasma-fx</c> (before), <c>--log-plasma-fx</c>.
/// </summary>
public partial class NetworkView
{
    /// <summary>The fade of a stopped <c>stop_emitting</c> effect (ours).</summary>
    const double PlasmaStopFade = 0.25;

    sealed class PlasmaFxTrack
    {
        public string Wargear = "";
        public string Mode = "", Stage = "";
        public bool High, WasCharging, Seen;
        public double ChargeStart = double.NaN;
        public readonly List<(FxEffect Effect, string Stop)> Innate = new(), Charge = new(), HighFx = new();
    }

    readonly Dictionary<int, PlasmaFxTrack> _plasmaFx = new();
    readonly List<PlayerState> _plasmaFxAll = new();
    readonly List<int> _plasmaFxGone = new();
    readonly HashSet<int> _plasmaOverheated = new();
    int _plasmaFxStarted, _plasmaFxNoMarker;
    bool? _noPlasmaFx, _logPlasmaFx;
    bool NoPlasmaFx => _noPlasmaFx ??= CmdFlag("no-plasma-fx");
    bool LogPlasmaFx => _logPlasmaFx ??= CmdFlag("log-plasma-fx");

    /// <summary>Whether a weapon's own files bind charge-build effects (the placeholder charge sphere is then not shown for it).</summary>
    bool HasDataChargeFx(PlayerState s) =>
        !NoPlasmaFx && _config is not null && Loadouts.InHand(_config, s) is { } w && w.Name.Contains("plasma", StringComparison.OrdinalIgnoreCase)
        && _catalogue?.Weapon(w.Name)?.Bindings.Any(b => b.IsDefault && b.Event == "charging_action" && b.Play is not null) == true;

    /// <summary>The charge toward the last stage with a shot (0..1), 1 while the released shot is pending, negative otherwise: the plasma charge arc's value (Hud.Plasma.cs).</summary>
    static float PlasmaChargeToFull(PlayerState me, WeaponSpec weapon)
    {
        if (weapon.Charge is not { Stages.Count: > 0 } charge) return -1f;
        if (me.Charging)
        {
            float last = 0f;
            foreach (var st in charge.Stages) if (st.Shot is not null) last = MathF.Max(last, st.MinTime * charge.MinTimeMultiplier);
            return last > 0f ? Math.Clamp(me.ChargeTime / last, 0f, 1f) : 1f;
        }
        return me.PendingShot > 0 ? 1f : -1f;
    }

    /// <summary>Called once per frame from <see cref="UpdateFx"/>: the plasma weapons' state effects for every player shown.</summary>
    void FxPlasma(PlayerState me)
    {
        if (_fx is null || _config is null || _client is null || _catalogue is null) return;
        if (NoPlasmaFx) { if (_plasmaFx.Count > 0) { foreach (var t in _plasmaFx.Values) StopPlasma(t); _plasmaFx.Clear(); } return; }
        var all = _plasmaFxAll;
        all.Clear();
        all.Add(me);
        for (int i = 0; i < _lastOthers.Count; i++) all.Add(_lastOthers[i]);
        foreach (var t in _plasmaFx.Values) t.Seen = false;
        for (int i = 0; i < all.Count; i++) PlasmaFxFor(all[i], all[i].PlayerId == _client.PlayerId);
        _plasmaFxGone.Clear();
        foreach (var (id, t) in _plasmaFx) if (!t.Seen) _plasmaFxGone.Add(id);
        foreach (var id in _plasmaFxGone) { StopPlasma(_plasmaFx[id]); _plasmaFx.Remove(id); }
    }

    void PlasmaFxFor(PlayerState s, bool own)
    {
        int id = s.PlayerId;
        var character = own ? _local : _remotes.GetValueOrDefault(id);
        var weapon = s.Alive ? Loadouts.InHand(_config!, s) : null;
        var w = weapon is not null && weapon.Name.Contains("plasma", StringComparison.OrdinalIgnoreCase) ? _catalogue!.Weapon(weapon.Name) : null;
        _plasmaFx.TryGetValue(id, out var t);
        if (w is null || weapon is null || character is null || s.SwitchLeft > 0f)
        {
            if (t is not null) { StopPlasma(t); _plasmaFx.Remove(id); }
            return;
        }
        if (t is not null && !string.Equals(t.Wargear, w.Wargear, StringComparison.OrdinalIgnoreCase)) { StopPlasma(t); t = null; }
        if (t is null) _plasmaFx[id] = t = new PlasmaFxTrack { Wargear = w.Wargear };
        t.Seen = true;

        // The charge state (others: by the time since their charge flag rose).
        bool charging = s.Charging;
        if (charging && !t.WasCharging) t.ChargeStart = _connectedFor;
        t.WasCharging = charging;
        string stage = "";
        if (charging && weapon.Charge is { Stages.Count: > 0 } charge)
        {
            float time = own ? s.ChargeTime : (float)Math.Max(0.0, _connectedFor - t.ChargeStart);
            int reached = Combat.ChargeStage(charge, time, firing: false);
            if (reached > 0) stage = charge.Stages[reached - 1].Name;
        }
        if (stage != t.Stage)
        {
            Stop(t.Charge);
            t.Stage = stage;
            if (stage.Length > 0) Start(t.Charge, w, id, b => b.IsDefault && b.Event == "charging_action" && b.ChargeStage == stage, "charge " + stage);
        }

        // The chamber's idle glow by fire mode (interpretation: primary in hand, secondary while charging).
        string mode = charging ? "_secondary_fire_mode" : "_primary_fire_mode";
        if (mode != t.Mode)
        {
            Stop(t.Innate);
            t.Mode = mode;
            Start(t.Innate, w, id, b => b.Event == "while_deployed_actions" && b.RequiredUpgrades.Any(u => u.EndsWith(mode, StringComparison.OrdinalIgnoreCase)), "deployed" + mode);
        }

        // Heat (own only).
        if (own && weapon.Heat is { } heat)
        {
            bool high = !s.Overheated && heat.HighHeat > 0f && s.Heat >= heat.HighHeat;
            if (high != t.High)
            {
                Stop(t.HighFx);
                if (high) Start(t.HighFx, w, id, b => b.IsDefault && b.Event == "high_heat" && b.Path.Contains("/heat", StringComparison.Ordinal), "high heat");
                t.High = high;
            }
            // Kept outside the track (a switch away and back, as the bots do when overheated, must not replay the overheat).
            bool was = _plasmaOverheated.Contains(id);
            if (s.Overheated && !was) { _plasmaOverheated.Add(id); Start(null, w, id, b => b.IsDefault && b.Event == "overheat" && b.Path.StartsWith("common_data/heat", StringComparison.Ordinal), "overheat"); }
            if (!s.Overheated && was) { _plasmaOverheated.Remove(id); Start(null, w, id, b => b.IsDefault && b.Event == "overheat_complete", "overheat complete"); }
        }

        // Hidden with the character (the camera hides the own one up close).
        bool hidden = !character.Visible;
        foreach (var list in new[] { t.Innate, t.Charge, t.HighFx })
            foreach (var (e, _) in list) e.Hidden = hidden;
    }

    /// <summary>
    /// Starts the weapon's effects the filter picks at their markers, attached: into <paramref name="into"/> as loops (a state's effects), or once with the data's lifetime when
    /// <paramref name="into"/> is null (an event's: the overheat, its end). A marker the weapon model lacks plays nothing (only the muzzle falls back to the barrel).
    /// </summary>
    void Start(List<(FxEffect Effect, string Stop)>? into, WeaponEffects w, int id, Func<FxBinding, bool> pick, string why)
    {
        foreach (var b in w.Bindings)
        {
            if (b.Play is not { Name.Length: > 0 } p || !pick(b)) continue;
            string marker = p.Marker;
            bool muzzle = marker.Length == 0 || marker.Equals(w.MuzzleMarker, StringComparison.OrdinalIgnoreCase);
            if (!muzzle && MarkerWorld(id, marker) is null) { _plasmaFxNoMarker++; if (LogPlasmaFx) GD.Print($"t {_connectedFor:F1} s: P{id} {w.Wargear} {why}: no marker '{marker}' for {p.Name}"); continue; }
            if (FxPlace(id, marker) is not { } place) continue;
            Func<(Vector3, Basis)?> follow = () => FxPlace(id, marker);
            var e = _fx!.Play(p.Name, place.Position, place.Axes, p.Scale <= 0f ? 1f : p.Scale, null, follow, into is null ? p.Lifetime : 0f, 0.0, repeat: into is not null, priority: 1,
                              tint: RaceTint(w.Wargear, p.Name));
            if (e is null) continue;
            e.StopOnLost = true;
            into?.Add((e, p.OnStop));
            _plasmaFxStarted++;
            if (LogPlasmaFx) GD.Print($"t {_connectedFor:F1} s: P{id} {w.Wargear} {why}: {p.Name} x{(p.Scale <= 0f ? 1f : p.Scale):0.##} at {marker}{(into is null ? "" : " (loop)")}");
        }
    }

    /// <summary>Ends a state's effects: <c>destroy</c> at once, else (<c>stop_emitting</c>) a short fade.</summary>
    static void Stop(List<(FxEffect Effect, string Stop)> list)
    {
        foreach (var (e, stop) in list)
        {
            e.Repeat = false;
            e.Life = Math.Min(e.Life, Math.Max(0.0, e.Age) + (stop == "destroy" ? 0.0 : PlasmaStopFade));
        }
        list.Clear();
    }

    static void StopPlasma(PlasmaFxTrack t) { Stop(t.Innate); Stop(t.Charge); Stop(t.HighFx); t.Mode = t.Stage = ""; t.High = false; }

    string PlasmaFxLog() => $"plasma fx: started {_plasmaFxStarted}, missing markers {_plasmaFxNoMarker}, players {_plasmaFx.Count}";
}
