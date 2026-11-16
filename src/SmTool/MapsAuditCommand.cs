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
        if (rest.SkipWhile(a => a != "--lighting").Skip(1).FirstOrDefault() is { } lightMap)
        {
            // the map's authored lighting (l_lights): sun, environment regions (fog gradient, ambient) and the light count
            var lighting = MapScene.Load(gameDir, lightMap).Lighting;
            if (lighting is null) { Console.WriteLine($"{lightMap}: no lighting layer"); return 0; }
            if (lighting.Sun is { } sun) Console.WriteLine($"sun colour {sun.Colour.X:0.00}/{sun.Colour.Y:0.00}/{sun.Colour.Z:0.00} power {sun.Power:0.0}");
            foreach (var e in lighting.Environments.OrderBy(e => e.Priority))
                Console.WriteLine($"region {e.Name} priority {e.Priority} size {e.Size.X:0}x{e.Size.Y:0}x{e.Size.Z:0}"
                    + (e.Sun is { } es ? $"; sun {es.Colour.X:0.00}/{es.Colour.Y:0.00}/{es.Colour.Z:0.00} x{es.Power:0.0}" : "")
                    + (e.Ambient is { } a ? $"; ambient up {a.Upper.X:0.00}/{a.Upper.Y:0.00}/{a.Upper.Z:0.00} low {a.Lower.X:0.00}/{a.Lower.Y:0.00}/{a.Lower.Z:0.00}" : "")
                    + (e.Fog is { } f ? $"; fog near {f.NearColour.X:0.00}/{f.NearColour.Y:0.00}/{f.NearColour.Z:0.00}/{f.NearColour.W:0.00} far {f.FarColour.X:0.00}/{f.FarColour.Y:0.00}/{f.FarColour.Z:0.00}/{f.FarColour.W:0.00} range {f.Range:0}" : ""));
            Console.WriteLine($"{lighting.Lights.Count} placed lights");
            return 0;
        }
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
