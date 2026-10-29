using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmContent;

namespace SmClient;

/// <summary>
/// M25b: the one effect mesh the weapons use, the ejected shell casing (<c>shell_casing</c> in <c>master_fx.oppc</c>, with its original diffuse texture):
/// a pool of mesh instances placed by the effects' <c>FXMeshDescription</c> elements each frame. Other effect meshes (chunk debris from another package) are not drawn.
/// </summary>
public sealed partial class FxPlayer
{
    const int MaxCasings = 24;
    ArrayMesh? _casing;
    float _casingUnit = 1f;
    readonly MeshInstance3D[] _casingNodes = new MeshInstance3D[MaxCasings];
    int _casingsUsed;
    public string MeshStatus { get; private set; } = "none";

    /// <summary>Reads the casing model from the effects package: inflates the package up to it (seconds on a busy machine), so a frame loop gives it to <see cref="Initialize"/> instead.</summary>
    public static StaticModel ReadCasing(string gameDir, List<string> notes) =>
        StaticModel.Load(global::System.IO.Path.Combine(gameDir, FxLibrary.Package), "shell_casing", notes);

    void LoadCasing(string gameDir, StaticModel? preloaded, string? preloadError)
    {
        var notes = new List<string>();
        try
        {
            if (preloadError is not null) { MeshStatus = "casing mesh not loaded: " + preloadError; return; }
            var model = preloaded ?? ReadCasing(gameDir, notes);
            var mesh = new ArrayMesh();
            var textures = new Dictionary<string, Texture2D?>(StringComparer.OrdinalIgnoreCase);
            Aabb? bounds = null;
            foreach (var surface in model.Surfaces)
            {
                var block = model.Mesh.Blocks[surface.BlockIndex];
                if (block.Positions.Length == 0 || surface.Range.IndexCount < 3) continue;
                var arrays = ModelViewer.BlockArrays(block);
                var indices = new int[surface.Range.IndexCount - surface.Range.IndexCount % 3];
                for (int i = 0; i < indices.Length; i++) indices[i] = block.Indices[surface.Range.StartIndex + i];
                arrays[(int)Mesh.ArrayType.Index] = indices;
                mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
                mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, ModelViewer.MaterialFor(surface, textures, notes));
                foreach (var p in arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array()) bounds = bounds is { } b ? b.Expand(p) : new Aabb(p, Vector3.Zero);
            }
            if (mesh.GetSurfaceCount() == 0 || bounds is not { } box) { MeshStatus = "casing mesh empty"; return; }
            // The mesh's unit is not documented: a casing is a few centimetres, so a model longer than half a metre is taken to be in centimetres.
            float extent = MathF.Max(box.Size.X, MathF.Max(box.Size.Y, box.Size.Z));
            _casingUnit = extent > 0.5f ? 0.01f : 1f;
            _casing = mesh;
            for (int i = 0; i < MaxCasings; i++)
            {
                _casingNodes[i] = new MeshInstance3D { Name = "Casing" + i, Mesh = mesh, Visible = false, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Layers = FxBatch.FxRenderLayer };
                AddChild(_casingNodes[i]);
            }
            MeshStatus = $"shell casing mesh {extent:F2} long (read as {(extent > 0.5f ? "centimetres" : "metres")})";
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            MeshStatus = "casing mesh not loaded: " + ex.Message;
        }
    }

    void DrawMesh(FxEffect e, CMesh n, in Ctx c, double age, uint seed)
    {
        if (_casing is null || !n.Mesh.Equals("shell_casing", StringComparison.OrdinalIgnoreCase) || _casingsUsed >= MaxCasings) return;
        if (!LeafTime(n, age, seed, out float t, out float dur)) return;
        float scale = c.S * At(n.Scale, t, 1f) * _casingUnit;
        if (scale <= 1e-5f) return;
        // Tumbling about a random axis while it flies (rad/s: ours).
        var axis = new Vector3(R(seed, 21) - 0.5f, R(seed, 22) - 0.5f, R(seed, 23) - 0.5f);
        axis = axis.LengthSquared() < 1e-4f ? Vector3.Right : axis.Normalized();
        var basis = c.B * new Basis(axis, R(seed, 24) * MathF.Tau + (float)age * (6f + 10f * R(seed, 25))) * Basis.FromScale(Vector3.One * scale);
        var node = _casingNodes[_casingsUsed++];
        node.Transform = new Transform3D(basis, c.Pos);
        node.Visible = true;
    }

    void BeginMeshes() => _casingsUsed = 0;

    void FlushMeshes()
    {
        for (int i = _casingsUsed; i < MaxCasings; i++) if (_casingNodes[i] is { } node && node.Visible) node.Visible = false;
    }
}
