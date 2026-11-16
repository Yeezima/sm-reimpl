using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;

namespace SmClient;

/// <summary>
/// The owner's Thunderhawk and Classic PvP asset kit (owner 2026-11-14): seven static, untextured GLBs, each scaled to one unit along its longest side. They are the owner's own models; he cleared them for the
/// public repository and for tester builds. Committed in <c>client/assets/owner-kit/thunderhawk-classic/models/</c> (<c>.gdignore</c> keeps the editor from importing them), read as plain files at run time;
/// <c>tools/release/release.ps1</c> copies them beside the executable (<c>assets\owner-kit\thunderhawk-classic\models\</c>). Only the troop bay's wall assets (<see cref="TfwBay"/>) read them. When no folder has
/// a file the bay draws a plain box stand-in (<i>placeholder demonstration</i>), never an error. Folders in order: env <c>SM_OWNER_KIT</c>, beside the executable, the project's committed copy, then
/// <c>%LOCALAPPDATA%\sm-reimpl\owner-kits\thunderhawk-classic\</c>; each may hold the files directly or in a <c>models</c> folder. <c>--bay-plain</c> turns the wall assets off.
/// </summary>
public static class OwnerKit
{
    public const string KitName = "thunderhawk-classic";

    /// <summary>The wall assets: off with <c>--bay-plain</c> (the floor pads come back).</summary>
    public static bool BayEnabled { get; } = !OS.GetCmdlineUserArgs().Contains("--bay-plain");

    static readonly Dictionary<string, Mesh?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static IEnumerable<string> Roots()
    {
        if (System.Environment.GetEnvironmentVariable("SM_OWNER_KIT") is { Length: > 0 } e) yield return e;
        yield return Path.Combine(Path.GetDirectoryName(OS.GetExecutablePath()) ?? "", "assets", "owner-kit", KitName);
        yield return Path.Combine(ProjectSettings.GlobalizePath("res://"), "assets", "owner-kit", KitName);
        yield return Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "sm-reimpl", "owner-kits", KitName);
    }

    public static string? Find(string file)
    {
        foreach (var root in Roots())
            foreach (var path in new[] { Path.Combine(root, "models", file), Path.Combine(root, file) })
                if (File.Exists(path)) return path;
        return null;
    }

    /// <summary>The mesh of a kit GLB (one mesh, Y up, front +Z, one unit along its longest side), read once; null when the file is missing or unreadable.</summary>
    public static Mesh? Mesh(string file, List<string>? notes = null)
    {
        if (Cache.TryGetValue(file, out var known)) return known;
        Mesh? mesh = null;
        try
        {
            if (Find(file) is { } path)
            {
                var doc = new GltfDocument();
                var state = new GltfState();
                if (doc.AppendFromFile(path, state) == Error.Ok && doc.GenerateScene(state) is { } root)
                {
                    mesh = root.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>().FirstOrDefault()?.Mesh;
                    if (mesh is not null) mesh = Faceted(mesh);
                    root.Free();
                }
                else notes?.Add($"owner kit: {file} could not be read");
            }
            else notes?.Add($"owner kit: {file} is not in any kit folder; a drawn stand-in is used");
        }
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            notes?.Add($"owner kit: {file} failed ({e.GetType().Name}: {e.Message})");
        }
        Cache[file] = mesh;
        return mesh;
    }

    /// <summary>The kit has no normals: split into plain triangles with flat normals, which is how hard-surface props read.</summary>
    static Mesh Faceted(Mesh source)
    {
        var tool = new SurfaceTool();
        tool.CreateFrom(source, 0);
        tool.Deindex();
        tool.GenerateNormals();
        return tool.Commit();
    }

    public static Aabb Bounds(Mesh? mesh, Vector3 fallback) => mesh?.GetAabb() ?? new Aabb(-fallback / 2f, fallback);

    /// <summary>Dark worn steel (the kit is untextured; the look is ours).</summary>
    public static StandardMaterial3D Steel() => new()
    {
        AlbedoColor = new Color(0.30f, 0.31f, 0.34f), Metallic = 0.3f, Roughness = 0.62f, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };

    public static StandardMaterial3D Brass() => new() { AlbedoColor = new Color(0.55f, 0.42f, 0.2f), Metallic = 0.9f, Roughness = 0.42f };
}
