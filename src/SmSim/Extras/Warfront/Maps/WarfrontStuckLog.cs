using System.Globalization;
using System.Numerics;

namespace SmSim.Extras.Warfront.Maps;

/// <summary>What a failsafe did to a unit.</summary>
public enum WarfrontStuckAction : byte { Relocated = 0, Removed = 1 }

/// <summary>One stuck-unit event: the slot, the unit's type name, where it stood, what the failsafe did and which failsafe (the stranded one or the position-clearing one).</summary>
public sealed record WarfrontStuckEvent(string Slot, string Type, Vector3 Position, WarfrontStuckAction Action, string Failsafe);

/// <summary>
/// The stuck-unit log (plan section 2, "Pilot gaps handled here"): the one place the stranded and stuck failsafes report to when they relocate or remove an Ork, so a slot's trouble spots are numbers and not guesses. The failsafes live in Musou
/// files this slice does not touch; the node rules (W3) call <see cref="Record"/> from their hook seams. A log keeps its events, counts them per slot and per type, writes one server line each and a summary for the report.
/// Thread-safe; not part of any hash or wire.
/// </summary>
public sealed class WarfrontStuckLog(Action<string>? sink = null)
{
    readonly object _gate = new();
    readonly List<WarfrontStuckEvent> _events = [];
    readonly Action<string>? _sink = sink;

    /// <summary>The line written for one event (the server's log): <c>warfront stuck: slot wf_b2, Boss Ork at (x, y, z): removed by the stranded failsafe</c>.</summary>
    public static string Line(WarfrontStuckEvent e) =>
        string.Create(CultureInfo.InvariantCulture, $"warfront stuck: slot {e.Slot}, {e.Type} at ({e.Position.X:0.0}, {e.Position.Y:0.0}, {e.Position.Z:0.0}): {(e.Action == WarfrontStuckAction.Removed ? "removed" : "relocated")} by the {e.Failsafe} failsafe");

    /// <summary>Records an event, writes its line to the sink and returns the line.</summary>
    public string Record(string slot, string type, Vector3 position, WarfrontStuckAction action, string failsafe = "stranded")
    {
        var e = new WarfrontStuckEvent(slot, type, position, action, failsafe);
        string line = Line(e);
        lock (_gate) _events.Add(e);
        _sink?.Invoke(line);
        return line;
    }

    /// <summary>Every event so far, in order.</summary>
    public IReadOnlyList<WarfrontStuckEvent> Events { get { lock (_gate) return [.. _events]; } }

    /// <summary>Events of a slot.</summary>
    public int CountFor(string slot) { lock (_gate) return _events.Count(e => e.Slot == slot); }

    /// <summary>The report block: a line per slot with its relocated and removed counts and the types, slots in name order; "none" when empty.</summary>
    public string Summary()
    {
        var all = Events;
        if (all.Count == 0) return "warfront stuck units: none";
        var lines = all.GroupBy(e => e.Slot).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g =>
            string.Create(CultureInfo.InvariantCulture, $"warfront stuck units, slot {g.Key}: {g.Count()} ({g.Count(e => e.Action == WarfrontStuckAction.Relocated)} relocated, {g.Count(e => e.Action == WarfrontStuckAction.Removed)} removed; {string.Join(", ", g.GroupBy(e => e.Type).OrderBy(t => t.Key, StringComparer.Ordinal).Select(t => $"{t.Key} x{t.Count()}"))})"));
        return string.Join('\n', lines);
    }
}
