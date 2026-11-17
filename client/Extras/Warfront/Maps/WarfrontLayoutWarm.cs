using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SmContent.Extras.Musou;
using SmContent.Extras.Warfront.Maps;
using SmSim.Extras.Musou.Ashfall;
using SmSim.Extras.Warfront.Maps;

namespace SmClient;

/// <summary>
/// The client's layout warm-up (slice W5d; client only, no wire, no rules version). A lane slot's band needs its own layout and its two neighbours' (the massing copies). Generating one is 19 to 27 s; reading its cache file is well under a second; the
/// server's and <c>smtool warfront-maps --prebuild</c>'s files hold the same bytes under another process kind (<see cref="WarfrontSharedCache"/>). So: the Stage flow names the next map while the client is still on the old connection, this starts ONE background
/// task for the slot and its two neighbours (the slot first), each read from any kind's file and generated only when none exists, into a static holder that survives the view's rebuild at the rejoin. The main thread only ever takes
/// from it (never generates). At most <see cref="MaxHeld"/> levels are kept; <see cref="DropAll"/> at the landing.
/// </summary>
public static class WarfrontLayoutWarm
{
    public const int MaxHeld = 3;

    sealed class Entry(WarfrontSlot slot)
    {
        public readonly WarfrontSlot Slot = slot;
        public volatile bool Done;
        public (AshfallLevel Level, AshfallDressing Dressing)? Built;
        public string Note = "";
        public long Ms;
    }

    static readonly Dictionary<string, Entry> Held = [];
    static readonly object Gate = new();

    /// <summary>The slot and its neighbours in the same lane (band - 1 and band + 1), the slot first.</summary>
    public static IReadOnlyList<WarfrontSlot> Needed(WarfrontSlot slot)
    {
        var list = new List<WarfrontSlot> { slot };
        foreach (int node in new[] { slot.Node - 1, slot.Node + 1 })
            if (node is >= 1 and <= AshfallFront.BandCount) list.Add(WarfrontSlots.Of(slot.Lane, node));
        return list;
    }

    /// <summary>Starts (once) the background work for <paramref name="slot"/> and its neighbours; anything else held is dropped. Returns at once.</summary>
    public static void Warm(WarfrontSlot slot, bool cacheOff)
    {
        List<Entry> fresh = [];
        lock (Gate)
        {
            var needed = Needed(slot);
            foreach (string stale in Held.Keys.Where(k => needed.All(s => s.Name != k)).ToList()) Held.Remove(stale);
            foreach (var s in needed)
                if (!Held.ContainsKey(s.Name)) { var e = new Entry(s); Held[s.Name] = e; fresh.Add(e); }
        }
        if (fresh.Count == 0) return;
        GD.Print($"warfront: warming {string.Join(", ", fresh.Select(e => e.Slot.Name))} on a background task (the main thread only takes the result)");
        Task.Run(() =>
        {
            if (cacheOff) AshfallLayoutCache.Disabled = true;
            foreach (var e in fresh) Process(e, cacheOff);
        });
    }

    static void Process(Entry e, bool cacheOff)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            (AshfallLevel Level, AshfallDressing Dressing)? built = cacheOff ? null : WarfrontSharedCache.TryRead(WarfrontLayoutCache.Folder, e.Slot, AshfallLayoutCache.ProcessKind, out e.Note);
            if (built is null) { built = WarfrontLayoutCache.Build(e.Slot, out string note); e.Note = note; }
            e.Built = built;
        }
        catch (Exception ex) when (ex is not (StackOverflowException or AccessViolationException or OutOfMemoryException))
        {
            e.Note = $"{e.Slot.Name} could not be built ({ex.GetType().Name}: {ex.Message})";
        }
        e.Ms = watch.ElapsedMilliseconds;
        e.Done = true;
        GD.Print($"warfront: warm {e.Slot.Name}: {e.Note} ({e.Ms} ms, background)");
    }

    /// <summary>Whether the slot's own layout (or its failure) is in.</summary>
    public static bool OwnReady(WarfrontSlot slot) { lock (Gate) return Held.TryGetValue(slot.Name, out var e) && e.Done; }

    /// <summary>Whether the slot and both neighbours are in.</summary>
    public static bool AllReady(WarfrontSlot slot) { lock (Gate) return Needed(slot).All(s => Held.TryGetValue(s.Name, out var e) && e.Done); }

    /// <summary>The finished layout of a slot, never waiting and never generating; null when it is not in (yet) or failed.</summary>
    public static (AshfallLevel Level, AshfallDressing Dressing)? TryGet(WarfrontSlot slot)
    {
        lock (Gate) return Held.TryGetValue(slot.Name, out var e) && e.Done ? e.Built : null;
    }

    /// <summary>The slot's layout, waiting for the background task when it has not finished (a join that skipped the Preparing section, a test aid; logged: this is a stall). Null when it could not be built.</summary>
    public static (AshfallLevel Level, AshfallDressing Dressing)? WaitFor(WarfrontSlot slot, bool cacheOff, out long waitedMs)
    {
        var watch = Stopwatch.StartNew();
        Warm(slot, cacheOff);
        Entry? entry;
        lock (Gate) Held.TryGetValue(slot.Name, out entry);
        while (entry is { Done: false }) System.Threading.Thread.Sleep(20);
        waitedMs = watch.ElapsedMilliseconds;
        return entry?.Built;
    }

    /// <summary>Waits (a worker thread only: the map load's stage 1) until the slot's neighbours are in or <paramref name="capSeconds"/> have passed; returns how many neighbours are in.</summary>
    public static int WaitForNeighbours(WarfrontSlot slot, double capSeconds, out long waitedMs)
    {
        var watch = Stopwatch.StartNew();
        var others = Needed(slot).Skip(1).ToList();
        while (watch.Elapsed.TotalSeconds < capSeconds)
        {
            bool all; lock (Gate) all = others.All(s => Held.TryGetValue(s.Name, out var e) && e.Done);
            if (all) break;
            System.Threading.Thread.Sleep(50);
        }
        waitedMs = watch.ElapsedMilliseconds;
        return others.Count(s => TryGet(s) is not null);
    }

    /// <summary>The landing: the held layouts go (the view keeps its own), as do the memos of the slots and of the layout cache, off the main thread (a build in progress holds their locks).</summary>
    public static void DropAll()
    {
        List<WarfrontSlot> slots;
        lock (Gate) { slots = [.. Held.Values.Select(e => e.Slot)]; Held.Clear(); }
        Task.Run(() => { WarfrontLayoutCache.ForgetAll(); foreach (var s in slots) WarfrontSlots.Release(s); });
    }
}
