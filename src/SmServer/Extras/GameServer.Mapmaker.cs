using SmContent;
using SmMapmaker;
using SmNet;
using SmSim;

namespace SmServer;

/// <summary>Mapmaker (added content, docs/mapmaker.md "Playing a map"): the server plays a custom map file compiled by the shared core, the same way every client does.</summary>
public sealed partial class GameServer
{
    static partial void ExtrasCustomMap(string file, string? install, ref string? name, ref GameMap? map, ref string? error)
    {
        CustomMap compiled;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // SM1 placements, prefabs and imported models are solid here (docs/mapmaker.md "SM1 pieces in play"): meshes only, from this server's install and library; a PC that lacks one refuses.
        try { compiled = CustomMaps.Load(file, CompileOptions.ForPlay(install, textures: false)); }
        catch (MapException e) { error = $"cannot play '{file}': {e.Message}"; return; }
        var c = compiled.Content;
        name = compiled.Name;
        map = new GameMap(compiled.Name, compiled.ToCollisionWorld(), c.SpawnPoints(), compiled.Hash, c.SimControlPoints(), c.SimStartAreas(), c.SimBlockers()) { AddedContent = compiled };
        Console.WriteLine($"custom map {compiled.Name} (\"{compiled.Document.Name}\", {Path.GetFileName(file)}): {compiled.SolidCount} solid pieces, {c.StartAreas.Count} start areas, {c.ControlPoints.Count} capture points, {c.Blockers.Count} spawn doors, {c.KillRegions.Count} kill zones; map hash {compiled.Hash:X16}");
        if (compiled.Decor is { Placements.Count: > 0 } decor)
            Console.WriteLine($"custom map {compiled.Name}: {decor.Placements.Count} SM1 / model placements, {decor.Triangles:N0} solid triangles from this install ({sw.Elapsed.TotalSeconds:0.0} s)");
        foreach (var note in c.Notes) Console.WriteLine("note: " + note);
    }
}
