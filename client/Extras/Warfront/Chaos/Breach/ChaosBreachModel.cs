using System;
using System.Collections.Generic;
using SmNet.Extras.Warfront;
using SmSim.Extras.Warfront.Chaos;

namespace SmClient.Extras.Warfront.Chaos.Breach;

/// <summary>
/// The warp breach's look and sound numbers on the client (Chaos Warfront C4c; spec "Added content: Warfront Chaos: warp breaches (C4)"). The effect and the sound events are the install's
/// (original asset loaded); every time, size and colour here is ours (estimate) unless its line says otherwise.
/// </summary>
public static class ChaosBreachLook
{
    /// <summary>The standing breach: the install's portal without its caster (looping in the data; docs/warfront-chaos-roster.md "Warp breach assets").</summary>
    public const string Effect = "chaos_portal_no_psyker";
    /// <summary>The fallback when the first is missing from the install's effect library (looping in the data; drawn far smaller, its own data is a sky gate).</summary>
    public const string FallbackEffect = "chaos_warp_gate_open";
    public const float FallbackScale = 0.05f;
    /// <summary>The effect's origin above the breach's foot (m; ours, an interpretation: the data's "delay to open" ring and "bright core" sit 2 m below the origin, so the origin floats 2 m up).</summary>
    public const float EffectHeight = 2f;
    /// <summary>The hum while a breach is open (the install's event; the data plays it inside the effect's "center energy" element, which the effect player does not sound).</summary>
    public const string HumEvent = "play_chaos_portal_no_psyker";
    /// <summary>The crackle at random 2 to 5 s while open (the install's event; the cadence ours).</summary>
    public const string ArcsEvent = "play_chaos_portal_arcs";
    public const float ArcsMinSeconds = 2f, ArcsMaxSeconds = 5f;
    /// <summary>The closure's sound (the install's portal explosion; the data has no closing sound by name: interpretation).</summary>
    public const string ClosureEvent = "play_chaos_portal_explode";
    /// <summary>The closure replays the breach effect from this age (s) for <see cref="ClosureFlashLife"/> s: its "buildup energy" flash at 0.94 to 1 s of the 5 s core and the smoke after it (an interpretation of "the flash and explode elements").</summary>
    public const float ClosureFlashFrom = 0.85f, ClosureFlashLife = 1.6f;
    /// <summary>The implosion (a shrinking sphere, ours): its start radius (m) and time (s); then the shockwave ring out to <see cref="ShockwaveRadius"/> m over <see cref="ShockwaveSeconds"/> s.</summary>
    public const float ImplosionRadius = 2.5f, ImplosionSeconds = 0.4f, ShockwaveRadius = 8f, ShockwaveSeconds = 0.5f;
    /// <summary>How long a closed breach's marker shows <see cref="ClosedText"/> (s).</summary>
    public const double ClosedShownSeconds = 3.0;
    /// <summary>The breach list is sent once a second; with none for this long the breaches are taken as gone (a lost end of battle, a rejoin).</summary>
    public const double StaleSeconds = 3.0;
    /// <summary>The marker over the centre (m above it) and its texts.</summary>
    public const float MarkerHeight = 3.5f;
    public const string OpenText = "WARP BREACH", ClosedText = "BREACH CLOSED";
    /// <summary>The marker's red-violet and the closed grey (ours), as RGB 0..1.</summary>
    public static readonly (float R, float G, float B) MarkerColour = (0.86f, 0.18f, 0.62f), ClosedColour = (0.55f, 0.55f, 0.58f);
    /// <summary>The breach's glow and the closure's sphere and ring (ours): violet.</summary>
    public static readonly (float R, float G, float B) WarpColour = (0.62f, 0.30f, 1.0f);

    /// <summary>The marker's text for an open breach at <paramref name="metres"/> from the viewer.</summary>
    public static string OpenLine(float metres) => $"{OpenText}  {(float.IsFinite(metres) ? (int)MathF.Round(MathF.Max(0f, metres)) : 0)} m";

    /// <summary>The seconds to the next crackle for a roll in 0..1.</summary>
    public static double ArcsDelay(double roll) => ArcsMinSeconds + (ArcsMaxSeconds - ArcsMinSeconds) * Math.Clamp(roll, 0.0, 1.0);
}

/// <summary>Where a tag 51 section goes: the breaches (kinds 1 and 2), C5's finale (kinds 16 to 31, <c>WarfrontChaosDuelSection</c>), or nowhere (empty or an unknown kind).</summary>
public enum ChaosSectionRoute : byte { Refused = 0, Breach = 1, Duel = 2 }

/// <summary>What the marker over a breach shows.</summary>
public enum ChaosBreachMarkerKind : byte { Hidden = 0, Open = 1, Closed = 2 }

/// <summary>A closure as the client shows it: the breach, who closed it, where (its last known centre; null when the list never named it) and when it arrived.</summary>
public readonly record struct ChaosBreachClosureShown(byte Breach, ushort Closer, uint Tick, System.Numerics.Vector3? Centre, double At);

/// <summary>
/// The Chaos section's breach state on the client (Chaos Warfront C4c; tag 51, <see cref="WarfrontChaosWire"/>): the latest breach list, the closures (the first of each (tick, id) pair: the server
/// sends each three times) and what each marker shows. Pure and Godot-free (tested in <c>ChaosBreachModelTests</c>); the networked view draws from it.
/// </summary>
public sealed class ChaosBreachModel
{
    readonly List<WarfrontChaosWire.Breach> _breaches = new();
    readonly HashSet<(uint Tick, byte Id)> _seen = new();
    readonly Dictionary<byte, ChaosBreachClosureShown> _closed = new();
    readonly Dictionary<byte, System.Numerics.Vector3> _centres = new();

    /// <summary>The latest list (empty: no breach, or none any more).</summary>
    public IReadOnlyList<WarfrontChaosWire.Breach> Breaches => _breaches;
    /// <summary>When the latest list arrived (seconds of the caller's clock; negative infinity: never).</summary>
    public double ListAt { get; private set; } = double.NegativeInfinity;
    /// <summary>Sections refused (malformed or an unknown kind), closures dropped as repeats, closures taken.</summary>
    public int Dropped { get; private set; }
    public int Repeats { get; private set; }
    public int ClosuresTaken { get; private set; }

    /// <summary>Where a section of tag 51 goes (never throws).</summary>
    public static ChaosSectionRoute Route(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 1) return ChaosSectionRoute.Refused;
        if (WarfrontChaosWire.IsDuelKind(payload)) return ChaosSectionRoute.Duel;
        return payload[0] is WarfrontChaosWire.KindBreaches or WarfrontChaosWire.KindClosure ? ChaosSectionRoute.Breach : ChaosSectionRoute.Refused;
    }

    readonly List<ChaosBreachClosureShown> _new = new();
    readonly Dictionary<byte, WarfrontBreachState> _lastState = new();

    /// <summary>The closures the last <see cref="Apply"/> took (first copies only): from a kind 2 section, or from the list when a breach it showed standing is now Closed and no kind 2 came for it (a lost closure burst).</summary>
    public IReadOnlyList<ChaosBreachClosureShown> NewClosures => _new;

    /// <summary>
    /// C5c's switch: while set, a breach the list shows closed with no kind 2 is taken as the finale's silent close (no closure show, no marker) instead of a lost closure burst. Off: every
    /// open-to-closed change of the list shows a closure.
    /// </summary>
    public bool SilentCloses { get; set; }

    /// <summary>
    /// A breach section (kind 1 or 2) at <paramref name="now"/>: false (and nothing changed) when it does not decode. <paramref name="closure"/> is the first of <see cref="NewClosures"/> (null: none).
    /// A breach closes once per battle (its id), so the kind 2 repeats and a kind 2 arriving after the list's change are dropped as repeats.
    /// </summary>
    public bool Apply(ReadOnlySpan<byte> payload, double now, out ChaosBreachClosureShown? closure)
    {
        closure = null;
        _new.Clear();
        if (!WarfrontChaosWire.TryDecode(payload, out var s)) { Dropped++; return false; }
        if (s.Kind == WarfrontChaosWire.KindBreaches)
        {
            _breaches.Clear();
            if (s.Breaches is { } list) _breaches.AddRange(list);
            ListAt = now;
            if (_breaches.Count == 0) { ForgetBattle(); return true; }   // the battle's end: the next battle's ids start again
            foreach (var b in _breaches)
            {
                _centres[b.Id] = b.Centre;
                bool wasStanding = _lastState.TryGetValue(b.Id, out var before) && before != WarfrontBreachState.Closed;
                _lastState[b.Id] = b.State;
                if (b.State != WarfrontBreachState.Closed || !wasStanding || _closed.ContainsKey(b.Id) || SilentCloses) continue;
                var derived = new ChaosBreachClosureShown(b.Id, 0, 0, b.Centre, now);   // the closer and tick are unknown without kind 2
                _closed[b.Id] = derived;
                ClosuresTaken++;
                _new.Add(derived);
            }
            closure = _new.Count > 0 ? _new[0] : null;
            return true;
        }
        var c = s.Closed;
        if (!_seen.Add((c.Tick, c.Breach)) || _closed.ContainsKey(c.Breach)) { Repeats++; return true; }
        var shown = new ChaosBreachClosureShown(c.Breach, c.Closer, c.Tick, _centres.TryGetValue(c.Breach, out var at) ? at : null, now);
        _closed[c.Breach] = shown;
        _lastState[c.Breach] = WarfrontBreachState.Closed;
        ClosuresTaken++;
        _new.Add(shown);
        closure = shown;
        return true;
    }

    void ForgetBattle() { _seen.Clear(); _closed.Clear(); _centres.Clear(); _lastState.Clear(); }

    /// <summary>Whether no list came for <see cref="ChaosBreachLook.StaleSeconds"/> while breaches were shown.</summary>
    public bool Stale(double now) => _breaches.Count > 0 && now - ListAt > ChaosBreachLook.StaleSeconds;

    /// <summary>The frame: a stale list is dropped (true when that happened).</summary>
    public bool Tick(double now)
    {
        if (!Stale(now)) return false;
        Clear();
        return true;
    }

    /// <summary>Forgets everything (the battle left, the view changed).</summary>
    public void Clear()
    {
        _breaches.Clear();
        _new.Clear();
        ForgetBattle();
        ListAt = double.NegativeInfinity;
    }

    /// <summary>The closure shown for a breach, if one arrived.</summary>
    public ChaosBreachClosureShown? ClosureOf(byte id) => _closed.TryGetValue(id, out var c) ? c : null;

    /// <summary>
    /// What the marker over <paramref name="b"/> shows at <paramref name="now"/>: <see cref="ChaosBreachMarkerKind.Open"/> while it is opening or open; once closed, <see cref="ChaosBreachMarkerKind.Closed"/>
    /// for <see cref="ChaosBreachLook.ClosedShownSeconds"/> after its closure arrived, then hidden. A breach closed without a closure (C5's silent close, or a closure not yet arrived) is hidden.
    /// </summary>
    public ChaosBreachMarkerKind Marker(in WarfrontChaosWire.Breach b, double now)
    {
        if (b.State != WarfrontBreachState.Closed && !_closed.ContainsKey(b.Id)) return ChaosBreachMarkerKind.Open;
        return _closed.TryGetValue(b.Id, out var c) && now - c.At < ChaosBreachLook.ClosedShownSeconds ? ChaosBreachMarkerKind.Closed : ChaosBreachMarkerKind.Hidden;
    }

    /// <summary>Whether a breach is shown as standing (its effect and hum on): not closed by the list nor by an arrived closure.</summary>
    public bool Standing(in WarfrontChaosWire.Breach b) => b.State != WarfrontBreachState.Closed && !_closed.ContainsKey(b.Id);
}
