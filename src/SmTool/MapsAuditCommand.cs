using SmContent;

namespace SmTool;

/// <summary>
/// <c>smtool maps-audit &lt;game-dir|auto&gt;</c>: the install's map archives (<c>data/chunks/mp_*</c>, <c>cp_*</c>) grouped by map name,
/// and for every map whose region archive the loader reads (<see cref="MapContent.Available"/>): spawns, control points, start areas, blockers, kill regions and the
/// loader's notes (read-only; classic-maps-all, 2026-11-15). Prints no machine paths.
/// </summary>
public static class MapsAuditCommand
{
    public static int Run(string gameDir, string[] rest)
    {
        if (gameDir == "auto") gameDir = GameInstall.Locate();
        string chunks = Path.Combine(gameDir, "data", "chunks");
        var groups = new List<string>();
        foreach (var pattern in new[] { "mp_*.oppc", "cp_*.oppc" })
            groups.AddRange(Directory.GetFiles(chunks, pattern).Select(f => Path.GetFileName(f)!));
        foreach (var g in groups.GroupBy(f => f.Split("__")[0]).OrderBy(g => g.Key, StringComparer.Ordinal))
            Console.WriteLine($"archive group {g.Key,-22} {string.Join(" ", g.Select(f => f[g.Key.Length..].Replace(".oppc", "")))}");
        Console.WriteLine();
        Console.WriteLine("roster (multiplayer_map): name, display name, game types, playable");
        foreach (var e in MapCatalog.Read(gameDir)) Console.WriteLine($"  {e.Name,-14} {e.DisplayName,-18} {string.Join(",", e.GameTypes)}  playable {e.Playable}");
        Console.WriteLine();
        foreach (var map in MapContent.Available(gameDir))
        {
            try
            {
                var m = MapContent.Load(gameDir, map);
                var layers = string.Join(",", m.Layers.Select(l => $"{l.Name}:{l.TriangleCount}"));
                Console.WriteLine($"{map,-22} region {m.Region,-10} spawns {m.Spawns.Count,3} points {m.ControlPoints.Count} starts {m.StartAreas.Count} blockers {m.Blockers.Count} kill {m.KillRegions.Count} props {m.Props.Count} hash {m.Hash:X16}");
                Console.WriteLine($"    layers {layers}");
                var lay = m.Layers.First(l => l.Name == MapContent.CharacterLayer);
                Console.WriteLine($"    character layer bounds x {lay.Vertices.Min(v => v.X):0}..{lay.Vertices.Max(v => v.X):0} y {lay.Vertices.Min(v => v.Y):0}..{lay.Vertices.Max(v => v.Y):0} z {lay.Vertices.Min(v => v.Z):0}..{lay.Vertices.Max(v => v.Z):0}");
                foreach (var p in m.ControlPoints) Console.WriteLine($"    point {p.Name} at {p.Position.X:0.#},{p.Position.Y:0.#},{p.Position.Z:0.#} r {p.Scale.X / 2f:0.#}");
                foreach (var a in m.StartAreas) Console.WriteLine($"    start {a.Side} at {a.Position.X:0.#},{a.Position.Y:0.#},{a.Position.Z:0.#} size {a.Scale.X:0.#}x{a.Scale.Y:0.#}");
                foreach (var n in m.Notes) Console.WriteLine($"    note: {n}");
            }
            catch (Exception e) { Console.WriteLine($"{map,-22} FAILED: {e.Message}"); }
        }
        return 0;
    }
}
