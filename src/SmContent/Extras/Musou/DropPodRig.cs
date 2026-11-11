using System.Numerics;
using SmFormats.Bod;
using SmFormats.Obpk;

namespace SmContent.Extras.Musou;

/// <summary>
/// One door of the install's drop pod (Musou Mode's drop pod assault, docs/drop-pod-assault.md): the mesh block that draws it, its pose in the pod's own space when closed (the nodes of the
/// pod's object file composed: the door's rig node, then the mesh's node) and the point and direction it falls outward from.
/// </summary>
/// <param name="Block">The index of the door's block in the pod mesh (its <c>StaticMeshVisual</c>'s mesh id).</param>
/// <param name="Closed">The block's vertices to the pod's space (game space, Z up), row-vector convention.</param>
/// <param name="Hinge">The door's foot on the ground ring, in the pod's space.</param>
/// <param name="Outward">The unit direction from the pod's axis to the door, flat.</param>
public readonly record struct DropPodDoor(int Block, Matrix4x4 Closed, Vector3 Hinge, Vector3 Outward);

/// <summary>
/// The drop pod's rig as the install's <c>DropPod.O3d</c> holds it (the campaign chunk <c>part_01__s_004_r350_airlock</c>; a singleplayer-content dependency): five <c>RIG_DoorNN</c> nodes on a ring round the
/// pod, each parenting the node (<c>geo_DP_DoorNN_LODH</c>) a door block hangs from, the blocks named in the object's <c>Visuals</c> by <c>RefNode</c>. Read at run time from the player's install; only
/// the numbers it composes are used, none is kept. The Euler order of the nodes' rotations (degrees) is **an interpretation**: X first, then Y, then Z (judged on the closed pod's picture).
/// </summary>
public static class DropPodRig
{
    /// <summary>The object file's name in the chunk.</summary>
    public const string ObjectName = "DropPod";

    /// <summary>Reads the doors from the chunk's object file; empty when it names none (the caller then draws the pod without moving doors).</summary>
    public static IReadOnlyList<DropPodDoor> Read(string chunkPath)
    {
        BodDocument? doc = null;
        using (var archive = ObpkArchive.Open(chunkPath))
            archive.ReadEntries(e => e.FileName.Equals(ObjectName, StringComparison.OrdinalIgnoreCase) && archive.GroupName(e).Equals("o3d", StringComparison.OrdinalIgnoreCase), (_, data) => doc ??= BodDocument.Read(data));
        return doc is null ? [] : From(doc.Root);
    }

    /// <summary>The doors of an object file's root (separate from <see cref="Read"/> for the tests).</summary>
    public static IReadOnlyList<DropPodDoor> From(BodValue root)
    {
        var nodes = new Dictionary<string, (string? Parent, Matrix4x4 Local)>(StringComparer.Ordinal);
        var visuals = new List<(string Node, int Block)>();
        Collect(root, null, nodes, visuals);
        var doors = new List<DropPodDoor>();
        foreach (var (node, block) in visuals)
        {
            if (!nodes.ContainsKey(node)) continue;
            var closed = World(node, nodes);
            string? rig = nodes[node].Parent;
            if (rig is null || !rig.StartsWith("RIG_Door", StringComparison.Ordinal) || !nodes.ContainsKey(rig)) continue;
            var at = World(rig, nodes).Translation;
            var flat = new Vector3(at.X, at.Y, 0f);
            var outward = flat.LengthSquared() > 1e-6f ? Vector3.Normalize(flat) : Vector3.UnitY;
            doors.Add(new DropPodDoor(block, closed, new Vector3(at.X, at.Y, 0f), outward));
        }
        return doors;
    }

    static Matrix4x4 World(string node, Dictionary<string, (string? Parent, Matrix4x4 Local)> nodes)
    {
        var m = nodes[node].Local;
        for (string? p = nodes[node].Parent; p is not null && nodes.TryGetValue(p, out var up); p = up.Parent) m *= up.Local;
        return m;
    }

    static void Collect(BodValue v, string? parent, Dictionary<string, (string? Parent, Matrix4x4 Local)> nodes, List<(string Node, int Block)> visuals)
    {
        switch (v)
        {
            case BodRef r: Collect(r.Value, parent, nodes, visuals); break;
            case BodList l: foreach (var item in l.Items) Collect(item.Value, parent, nodes, visuals); break;
            case BodStruct s:
                if (s["RefNode"] is BodString refNode && s["MeshID"] is BodInt id && refNode.Text is { } rn) visuals.Add((rn, id.Value));
                string? name = (s["Name"] as BodString)?.Text;
                bool node = name is not null && s["Position"] is BodArray && s["Rotation"] is BodArray;
                if (node)
                {
                    var p = Vec(s["Position"]); var r = Vec(s["Rotation"]);
                    // Row vectors: scale (none), then X, Y, Z rotations in turn, then the translation.
                    var local = Matrix4x4.CreateRotationX(r.X * MathF.PI / 180f) * Matrix4x4.CreateRotationY(r.Y * MathF.PI / 180f) * Matrix4x4.CreateRotationZ(r.Z * MathF.PI / 180f) * Matrix4x4.CreateTranslation(p);
                    nodes[name!] = (parent, local);
                }
                foreach (var f in s.Fields) Collect(f.Value, node ? name : parent, nodes, visuals);
                break;
        }
    }

    static Vector3 Vec(BodValue? v)
    {
        if (v is not BodArray a || a.Items.Count < 3) return Vector3.Zero;
        float F(BodValue x) => x is BodFloat f ? f.Value : x is BodInt i ? i.Value : 0f;
        return new Vector3(F(a.Items[0]), F(a.Items[1]), F(a.Items[2]));
    }
}
