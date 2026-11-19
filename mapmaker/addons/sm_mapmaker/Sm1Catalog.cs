using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SmContent;
using SmMapmaker;

namespace SmMapmakerEditor;

/// <summary>
/// The library's index of every Classic PvP map's definitions (owner 2026-11-19: browse them all without loading a map). Built once in the background,
/// one map at a time, from the cache folder when it is valid (<see cref="Sm1Index"/>), else from the install; the dock polls <see cref="Revision"/>.
/// Holds names only: a map's meshes are loaded when something from it is drawn (<see cref="MapmakerPlugin.RequestSm1Load"/>).
/// </summary>
public static class Sm1Catalog
{
    static readonly object Gate = new();
    static readonly Dictionary<string, Sm1IndexEntry> Entries = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, string> Failed = new(StringComparer.OrdinalIgnoreCase);
    static IReadOnlyList<MapEntry> _maps = [];
    static bool _started;
    static string? _wanted;
    static string? _problem;
    static int _revision;

    /// <summary>Counts every change (a map listed, indexed or failed); the dock rebuilds its list when this moves.</summary>
    public static int Revision => Volatile.Read(ref _revision);

    /// <summary>The maps the install lists, in display-name order (empty until the catalog is read).</summary>
    public static IReadOnlyList<MapEntry> Maps { get { lock (Gate) return _maps; } }

    /// <summary>A sentence when the index cannot be built (no install), else null.</summary>
    public static string? Problem { get { lock (Gate) return _problem; } }

    public static Sm1IndexEntry? Get(string map) { lock (Gate) return Entries.GetValueOrDefault(map); }
    public static string? FailureOf(string map) { lock (Gate) return Failed.GetValueOrDefault(map); }
    public static (int Done, int Total) Progress { get { lock (Gate) return (Entries.Count + Failed.Count, _maps.Count); } }
    public static string DisplayName(string map) { lock (Gate) return _maps.FirstOrDefault(m => m.Name.Equals(map, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? map; }

    /// <summary>Asks for this map to be indexed next (the owner picked it in the filter).</summary>
    public static void Want(string map) { lock (Gate) { if (!Entries.ContainsKey(map) && !Failed.ContainsKey(map)) _wanted = map; } }

    /// <summary>Starts the background build once (main thread: it asks Godot for the install folder).</summary>
    public static void Start()
    {
        lock (Gate) { if (_started) return; _started = true; }
        string? dir = null;
        try { dir = Sm1Source.GameDir(); } catch (Exception) { }
        if (dir is null) { lock (Gate) _problem = "The Space Marine install was not found, so the SM1 definitions cannot be listed."; Bump(); return; }
        string cache = Sm1Index.DefaultCacheDir;
        _ = Task.Run(() => Work(dir, cache));
    }

    static void Bump() => Interlocked.Increment(ref _revision);

    static void Work(string gameDir, string cache)
    {
        try
        {
            var maps = Sm1Index.Maps(gameDir);
            lock (Gate) { _maps = maps; if (maps.Count == 0) _problem = "The install lists no multiplayer maps."; }
            Bump();
            // Cached maps first (milliseconds), so the list is mostly full at once; then the rest, the one the owner asked for before the others.
            foreach (var m in maps)
                if (Sm1Index.TryRead(cache, m.Name, Sm1Index.Stamp(gameDir, m.Name)) is { } hit) { lock (Gate) Entries[m.Name] = hit; Bump(); }
            while (true)
            {
                MapEntry? next;
                lock (Gate)
                {
                    next = maps.FirstOrDefault(m => m.Name == _wanted && !Entries.ContainsKey(m.Name) && !Failed.ContainsKey(m.Name))
                        ?? maps.FirstOrDefault(m => !Entries.ContainsKey(m.Name) && !Failed.ContainsKey(m.Name));
                }
                if (next is null) break;
                try { var entry = Sm1Index.Load(gameDir, next, cache); lock (Gate) Entries[next.Name] = entry; }
                catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException)) { lock (Gate) Failed[next.Name] = e.Message; }
                Bump();
                GC.Collect();   // each map's meshes and textures were only read for their names
            }
        }
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException))
        {
            lock (Gate) _problem = "The SM1 definitions could not be listed: " + e.Message;
            Bump();
        }
    }
}
