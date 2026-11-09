using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Godot;
using SmContent;

namespace SmClient;

/// <summary>
/// Expansion S4's models (owner, 2026-11-08: "Yes, use these"): SM1 props from the player's install that stand for the Scrap Meks' contraptions, read on a worker and built one a frame on the main
/// thread. *Original asset loaded* when a model reads; the contraption keeps its shape placeholder when it does not. Campaign chunks are read only for these props (Musou Mode depends on them:
/// docs/discovery.md "Musou S4 models"); the beacon is the multiplayer set's banner, the spikes a multiplayer map's, the drop rok an Exterminatus arena's.
/// </summary>
public sealed class MekModels
{
    /// <summary>One model: the chunk it is read from (data/chunks) and its mesh name.</summary>
    public sealed record Part(string Chunk, string Mesh);

    public static readonly Part ShieldGenerator = new("part_01__s_004_r300_orktrench", "ork_elektrik_tower_healthy");
    public static readonly Part ShieldWreck = new("part_01__s_004_r400_inner_wall", "ork_elektrik_tower_wreck");
    public static readonly Part Dome = new("part_02_04__s_009_r_200_socket", "shield_weird_boy");
    public static readonly Part Beacon = new("ebp_sets__mp_versus_set", "waaaghbanner");
    public static readonly Part Turret = new("part_01__s_004_r300_orktrench", "ork_turret_healthy");
    public static readonly Part TurretWreck = new("part_01__s_004_r300_orktrench", "ork_turret_wreck");
    public static readonly Part Trap = new("part_01__s_004_r400_inner_wall", "ork_junkpile_d_healthy");
    public static readonly Part Spikes = new("mp_dlc2_map4__wrld", "Spikes_A");
    public static readonly Part Rok = new("cp_maps__cp_arena_desert_b", "ork_rok");

    static readonly Part[] All = [ShieldGenerator, ShieldWreck, Dome, Beacon, Turret, TurretWreck, Trap, Spikes, Rok];

    /// <summary>A built model: its mesh in Godot axes (as the model viewer builds it) and its bounds there.</summary>
    public sealed record Built(ArrayMesh Mesh, Aabb Bounds);

    readonly Task<List<(Part Part, StaticModel? Model, string? Failure)>> _reads;
    readonly Dictionary<Part, Built> _built = new();
    readonly Dictionary<string, Texture2D?> _textures = new(StringComparer.OrdinalIgnoreCase);
    int _next;

    public List<string> Notes { get; } = new();
    /// <summary>Every model built or given up.</summary>
    public bool Done { get; private set; }

    /// <param name="parts">The models to read (default: the Scrap Meks' set); the capture banners pass their own (Tfw/TfwBanners.cs).</param>
    public MekModels(string gameDir, IReadOnlyList<Part>? parts = null)
    {
        _reads = Task.Run(() =>
        {
            var list = new List<(Part, StaticModel?, string?)>();
            foreach (var part in parts ?? All)
            {
                string archive = Path.Combine(gameDir, "data", "chunks", part.Chunk + ".oppc");
                try { list.Add((part, File.Exists(archive) ? StaticModel.Load(archive, part.Mesh, new List<string>()) : null, File.Exists(archive) ? null : "no " + part.Chunk)); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or ArgumentException) { list.Add((part, null, ex.Message)); }
            }
            return list;
        });
    }

    /// <summary>The model of a part (null: not built yet, or it does not load).</summary>
    public Built? Get(Part part) => _built.GetValueOrDefault(part);

    /// <summary>Builds at most one model a call once the reads are done; true when one was built this call.</summary>
    public bool Step()
    {
        if (Done || !_reads.IsCompleted) return false;
        if (_reads.IsFaulted) { Notes.Add("musou s4 models: " + _reads.Exception?.GetBaseException().Message); Done = true; return false; }
        var reads = _reads.Result;
        if (_next >= reads.Count) { Done = true; return false; }
        var (part, model, failure) = reads[_next++];
        if (model is null) { Notes.Add($"musou s4 models: {part.Mesh} does not load ({failure}); its placeholder stays"); return false; }
        var notes = new List<string>();
        var mesh = new ArrayMesh();
        var blocks = new Dictionary<int, Godot.Collections.Array>();
        Aabb? bounds = null;
        foreach (var surface in model.Surfaces)
        {
            if (model.Mesh.Blocks[surface.BlockIndex] is not { } block || block.Positions.Length == 0 || surface.Range.IndexCount < 3) continue;
            if (!blocks.TryGetValue(surface.BlockIndex, out var shared))
            {
                blocks[surface.BlockIndex] = shared = ModelViewer.BlockArrays(block);
                foreach (var p in shared[(int)Mesh.ArrayType.Vertex].AsVector3Array()) bounds = bounds is { } b ? b.Expand(p) : new Aabb(p, Vector3.Zero);
            }
            var indices = new int[surface.Range.IndexCount - surface.Range.IndexCount % 3];
            for (int i = 0; i < indices.Length; i++) indices[i] = block.Indices[surface.Range.StartIndex + i];
            var arrays = shared.Duplicate();
            arrays[(int)Mesh.ArrayType.Index] = indices;
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, ModelViewer.MaterialFor(surface, _textures, notes));
        }
        model.ReleaseTextures();
        if (mesh.GetSurfaceCount() == 0 || bounds is not { } box) { Notes.Add($"musou s4 models: {part.Mesh} has no triangles; its placeholder stays"); return false; }
        _built[part] = new Built(mesh, box);
        Notes.Add($"musou s4 models: {part.Mesh} from {part.Chunk} ({mesh.GetSurfaceCount()} surfaces, bounds {box.Position} size {box.Size}; original asset loaded)");
        return true;
    }

    /// <summary>Godot axes to the game's (Z up): the inverse of the networked view's game space.</summary>
    static readonly Basis GodotToGame = new(new Vector3(1f, 0f, 0f), new Vector3(0f, 0f, 1f), new Vector3(0f, -1f, 0f));

    /// <summary>
    /// A node in game axes holding <paramref name="model"/> scaled by <paramref name="scale"/> (per Godot axis): standing on its own origin (<paramref name="foot"/>: the props are authored with the
    /// ground at their origin, what lies below it buried), else its middle on the node's origin.
    /// </summary>
    public static Node3D Place(Built model, Vector3 scale, bool foot, string name)
    {
        var b = model.Bounds;
        var centre = (b.Position + b.Size * 0.5f) * scale;
        var offset = foot ? Vector3.Zero : -centre;
        var node = new Node3D { Name = name, Transform = new Transform3D(GodotToGame * Basis.FromScale(scale), GodotToGame * offset) };
        node.AddChild(new MeshInstance3D { Mesh = model.Mesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.On });
        return node;
    }

    /// <summary>The uniform scale that makes <paramref name="model"/> <paramref name="height"/> metres tall above its origin (its ground).</summary>
    public static float ToHeight(Built model, float height) => model.Bounds.End.Y > 1e-3f ? height / model.Bounds.End.Y : model.Bounds.Size.Y > 1e-3f ? height / model.Bounds.Size.Y : 1f;

    /// <summary>The uniform scale that makes <paramref name="model"/>'s widest flat extent <paramref name="width"/> metres.</summary>
    public static float ToWidth(Built model, float width) => MathF.Max(model.Bounds.Size.X, model.Bounds.Size.Z) is > 1e-3f and var w ? width / w : 1f;
}
