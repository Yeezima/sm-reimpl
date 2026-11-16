using System;
using System.Collections.Generic;

namespace SmClient;

/// <summary>Where a sound that fired came from, for the F7 mix editor's live list: the local player, another player (or a projectile that follows one), or the world (a fixed place: pickups, the map, the match).</summary>
public enum SoundSource : byte { Own, Other, World }

/// <summary>
/// sound/mix-expand: the live list of the F7 mix editor, the last <see cref="Capacity"/> distinct sounds that started (an event heard from one source is one row whose time, distance and count
/// are refreshed). A fixed array of structs, filled only while the panel is open (<see cref="Enabled"/>): a voice start costs a scan of the rows and allocates nothing; the panel builds
/// its rows from <see cref="Snapshot"/> a few times a second while it is open.
/// </summary>
public static class SoundMixLive
{
    public const int Capacity = 48;

    public struct Row
    {
        public string Event;
        public SoundSource Source;
        public float Distance;
        public double At;
        public int Count;
    }

    /// <summary>Set by the panel while it is open; off, <see cref="Note"/> returns at once.</summary>
    public static bool Enabled;

    static readonly Row[] Rows = new Row[Capacity];
    static int _used;

    public static void Note(string evt, SoundSource source, float distance, double at)
    {
        if (!Enabled || string.IsNullOrEmpty(evt)) return;
        for (int i = 0; i < _used; i++)
            if (Rows[i].Source == source && string.Equals(Rows[i].Event, evt, StringComparison.Ordinal))
            {
                Rows[i].Distance = distance; Rows[i].At = at; Rows[i].Count++;
                return;
            }
        int slot;
        if (_used < Capacity) slot = _used++;
        else
        {
            slot = 0;   // the stalest row goes
            for (int i = 1; i < Capacity; i++) if (Rows[i].At < Rows[slot].At) slot = i;
        }
        Rows[slot] = new Row { Event = evt, Source = source, Distance = distance, At = at, Count = 1 };
    }

    /// <summary>The rows, newest first (allocates; for the panel's refresh only).</summary>
    public static List<Row> Snapshot()
    {
        var list = new List<Row>(_used);
        for (int i = 0; i < _used; i++) list.Add(Rows[i]);
        list.Sort((a, b) => b.At.CompareTo(a.At));
        return list;
    }

    public static void Clear() { _used = 0; Array.Clear(Rows); }
}
