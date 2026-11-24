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
        catch (Exception e) when (e is not (StackOverflowException or AccessViolationException or InvalidProgramException))
        {
            // A damaged or hostile file (out of memory, a number out of range, bad JSON in a prefab): a clean refusal, exit code 2, never a crash.
            error = $"cannot play '{file}': the map could not be compiled ({e.GetType().Name}: {e.Message})"; return;
        }
        var c = compiled.Content;
        name = compiled.Name;
        map = new GameMap(compiled.Name, compiled.ToCollisionWorld(), c.SpawnPoints(), compiled.Hash, c.SimControlPoints(), c.SimStartAreas(), c.SimBlockers()) { AddedContent = compiled };
        Console.WriteLine($"custom map {compiled.Name} (\"{compiled.Document.Name}\", {Path.GetFileName(file)}): {compiled.SolidCount} solid pieces, {c.StartAreas.Count} start areas, {c.ControlPoints.Count} capture points, {c.Blockers.Count} spawn doors, {c.KillRegions.Count} kill zones; map hash {compiled.Hash:X16}");
        if (compiled.Decor is { Placements.Count: > 0 } decor)
            Console.WriteLine($"custom map {compiled.Name}: {decor.Placements.Count} SM1 / model placements, {decor.Triangles:N0} solid triangles from this install ({sw.Elapsed.TotalSeconds:0.0} s)");
        // Edited markers act by their exact shape (docs/mapmaker.md "Edit mode in play"): one line each, with the box round it, so a log shows which volumes are exact.
        foreach (var (volumeName, v) in compiled.Volumes.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            Console.WriteLine($"custom map {compiled.Name}: exact shape {volumeName}: {v.Indices.Length / 3} triangles, box x {v.Min.X:0.##}..{v.Max.X:0.##}, y {v.Min.Y:0.##}..{v.Max.Y:0.##}, z {v.Min.Z:0.##}..{v.Max.Z:0.##}");
        foreach (var note in c.Notes) Console.WriteLine("note: " + note);
    }
}
