using SmContent;
using SmMapmaker;
using SmNet;
using SmSim;

namespace SmServer;

/// <summary>Mapmaker (added content, docs/mapmaker.md "Playing a map"): the server plays a custom map file compiled by the shared core, the same way every client does.</summary>
public sealed partial class GameServer
{
    static partial void ExtrasCustomMap(string file, ref string? name, ref GameMap? map, ref string? error)
    {
        CustomMap compiled;
        try { compiled = CustomMaps.Load(file); }
        catch (MapException e) { error = $"cannot play '{file}': {e.Message}"; return; }
        var c = compiled.Content;
        name = compiled.Name;
        map = new GameMap(compiled.Name, compiled.ToCollisionWorld(), c.SpawnPoints(), compiled.Hash, c.SimControlPoints(), c.SimStartAreas(), c.SimBlockers()) { AddedContent = compiled };
        Console.WriteLine($"custom map {compiled.Name} (\"{compiled.Document.Name}\", {Path.GetFileName(file)}): {compiled.SolidCount} solid pieces, {c.StartAreas.Count} start areas, {c.ControlPoints.Count} capture points, {c.Blockers.Count} spawn doors, {c.KillRegions.Count} kill zones; map hash {compiled.Hash:X16}");
        foreach (var note in c.Notes) Console.WriteLine("note: " + note);
    }
}
