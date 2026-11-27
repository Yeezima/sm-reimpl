using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SmContent;
using SmContent.Extras.Executions;
using SmSim.Extras.Executions;

namespace SmClient;

/// <summary>
/// Executions: the head cut (owner 2026-11-27, card "Decap": "Search, then build ours"; <see cref="ExecutionDecapRules"/>; docs/executions-cues.md section 6). On the cut cue of a decapitating row
/// (C1 on a marine, O5 on an Ork Boy) the victim's proxy loses its head and a loose head is tossed along the blade's exit:
/// <list type="bullet">
/// <item>Ork Boy meshes with a headless body in the install (<see cref="ExecutionDecapRules.OrkStumpMeshes"/>): the proxy's body swaps to that block (<c>GEO_Wounded_Neck</c>, original asset).</item>
/// <item>Marines: the head pieces (helmet or bare head: pieces mostly on the head bones) are hidden and a dark neck cap (ours) closes the collar.</item>
/// <item>The loose head (ours): the triangles of the proxy's own meshes on the head bones, frozen in the cut frame's pose, as a rigid body with a sphere against the map. It lies with the
/// corpse, sinks with it and goes when the proxy goes (at the latest <see cref="ExecutionDecapRules.HeadMaxSeconds"/>).</item>
/// <item>The neck wound: the data's head point effects (<see cref="ExecutionDecapRules.NeckWound"/>) at the neck, following it; the geyser is the row's own cue.</item>
/// </list>
/// Drawn on every client from the shared clock (the cut is a cue of <see cref="ExecutionCueTimeline"/>), only where execution gore draws (<see cref="ExecutionDecapRules.Allowed"/>). A proxy
/// handed back to the pool gets its head and body back first. <c>--no-execution-decap</c> turns the cut off alone (a test aid).
/// </summary>
public partial class NetworkView
{
    static readonly bool NoExecDecap = CmdFlag("no-execution-decap");
    /// <summary>Test aid <c>--execution-decap-cam</c>: while a victim proxy lives, the camera stands level beside the pair (3.6 m off the line between them), for the head-cut clips.</summary>
    static readonly bool ExecDecapCam = CmdFlag("execution-decap-cam");
    Vector3? _execDecapCamFocus;
    object? _execDecapCamFor;

    bool ExecDecapSideCamera()
    {
        if (_camera is null) return false;
        foreach (var p in _execProxies.Values.OrderByDescending(x => x.CreatedAt))   // the newest move
        {
            if (p.Viewer is not { } v || !IsInstanceValid(v) || !v.Visible) continue;
            if (!ReferenceEquals(_execDecapCamFor, p)) { _execDecapCamFor = p; _execDecapCamFocus = null; }
            var victim = v.GlobalPosition + Vector3.Up * 1.2f * p.Scale;
            var attacker = ExecCharacter(p.Run.Executor) is { } c && IsInstanceValid(c) ? c.GlobalPosition + Vector3.Up * 1.2f : victim + Vector3.Forward;
            var mid = (victim + attacker) * 0.5f;
            _execDecapCamFocus ??= mid;
            var focus = _execDecapCamFocus.Value;
            var line = attacker - victim; line.Y = 0f;
            var side = line.LengthSquared() > 1e-4f ? line.Normalized().Cross(Vector3.Up) : Vector3.Right;
            var eye = focus + side * 3.6f + Vector3.Up * 0.5f;
            _camera.GlobalTransform = new Transform3D(Basis.LookingAt((focus - eye).Normalized(), Vector3.Up), eye);
            return true;
        }
        _execDecapCamFocus = null;
        return false;
    }

    sealed class ExecDecap
    {
        public required (int, uint) Key;
        public RigidBody3D? Head;
        public readonly List<MeshInstance3D> Hidden = new();
        public (MeshInstance3D Instance, Mesh Mesh, Skin Skin, Material?[] Overrides)? Swapped;
        public Node3D? Cap;
        public double CutAt;
        public Vector3? RestAt;
    }

    readonly Dictionary<(int, uint), ExecDecap> _execDecaps = new();
    readonly List<(int, uint)> _execDecapDrop = new();
    Task<Dictionary<string, (StaticModel Model, ObjectTree Tree)>>? _execStumpRead;
    readonly Dictionary<string, PieceAsset?> _execStumps = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Cuts made (the self-test reads it).</summary>
    internal int ExecutionDecapCount { get; private set; }
    /// <summary>Loose heads alive now.</summary>
    internal int ExecutionLooseHeads => _execDecaps.Values.Count(d => d.Head is { } h && IsInstanceValid(h));

    /// <summary>The Ork stumps' files are read once on a worker at the first executions frame where the cut may draw (an O5 cut comes 1.9 s into its move: too soon to start then).</summary>
    void ExecDecapPrefetch()
    {
        if (_execStumpRead is not null || NoExecDecap || !ExecutionDecapRules.Allowed(MatchExtras, NoExecGore)) return;
        string archive = RigProfiles.OrkBoy.ArchivePath(_gameDir);
        _execStumpRead = Task.Run(() =>
        {
            var read = new Dictionary<string, (StaticModel, ObjectTree)>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in ExecutionDecapRules.OrkStumpMeshes)
            {
                var notes = new List<string>();
                try { if (ObjectTree.Load(archive, name, notes) is { } tree) read[name] = (StaticModel.Load(archive, name, notes), tree); }
                catch (Exception e) when (e is not OutOfMemoryException) { GD.Print($"execution decap: {name}: {e.GetType().Name}: {e.Message}"); }
            }
            return read;
        });
    }

    /// <summary>The headless body of an Ork mesh (built on the main thread once), or null when the install has none or its read has not finished.</summary>
    PieceAsset? ExecStump(string mesh)
    {
        if (_execStumps.TryGetValue(mesh, out var built)) return built;
        if (_execStumpRead is not { IsCompleted: true } read || read.IsFaulted || Orks() is not { } orks) return null;
        PieceAsset? asset = null;
        if (read.Result.TryGetValue(mesh, out var m))
        {
            var neck = m.Tree.Visuals.FirstOrDefault(v => v.Renderable && v.IsSkinned && v.MeshId >= 0 && v.MeshId < m.Model.Mesh.Blocks.Count
                                                          && v.RefNode.Contains(ExecutionDecapRules.StumpNode, StringComparison.OrdinalIgnoreCase));
            if (neck is not null)
            {
                var hidden = Enumerable.Range(0, m.Model.Mesh.Blocks.Count).Where(b => b != neck.MeshId).ToHashSet();
                asset = orks.Pieces.Build("decap_" + mesh, new CharacterPiece("decap_" + mesh, m.Model, null) { Hidden = hidden }, vertexColours: true);
            }
        }
        return _execStumps[mesh] = asset;
    }

    /// <summary>The cut cue: hides the proxy's head, tosses the loose head and opens the neck wound. Returns the log's result.</summary>
    string ExecDecapCut(ExecCueRun cr)
    {
        if (NoExecDecap) return "decap off (--no-execution-decap)";
        if (!ExecutionDecapRules.Allowed(MatchExtras, NoExecGore)) return "gore off";
        if (ExecutionDecapRules.RowFor(cr.Row.Id) is not { } row) return "no decap row";
        var key = (cr.Run.Executor, cr.Run.StartTick);
        if (_execDecaps.ContainsKey(key)) return "already cut";
        if (!_execProxies.TryGetValue(key, out var proxy) || proxy.Viewer is not { Skeleton: { } sk } viewer || !IsInstanceValid(viewer) || _gameSpace is null) return "no proxy";
        if (proxy.StandIn) return "stand-in proxy: left as today";
        int headBone = -1;
        foreach (var n in ExecutionDecapRules.HeadBones) { headBone = ExecFindBone(sk, n); if (headBone >= 0) break; }
        if (headBone < 0) return "no head bone: left as today";
        var head = new bool[sk.GetBoneCount()];
        for (int b = 0; b < head.Length; b++)
            for (int x = b; x >= 0; x = sk.GetBoneParent(x)) if (x == headBone) { head[b] = true; break; }

        var pieces = sk.GetChildren().OfType<MeshInstance3D>().Where(m => m.Visible && m.Mesh is ArrayMesh && m.Skin is not null && m.Name != "Bones").ToList();
        var shares = pieces.Select(p => ExecHeadShare(p, sk, head)).ToList();
        int headPieces = shares.Count(s => s >= ExecutionDecapRules.HeadPieceShare);
        string? orkMesh = proxy.Assets is { Pieces.Count: > 0 } a ? ExecutionDecapRules.OrkMeshOf(a.Pieces[0].Name) : null;
        var kind = ExecutionDecapRules.BodyFor(row, orkMesh, headPieces);
        PieceAsset? stump = null;
        if (kind == ExecutionDecapBody.OrkStump && (stump = ExecStump(orkMesh!)) is null) return $"no headless body for {orkMesh} (not read yet or none): left as today";
        if (kind == ExecutionDecapBody.None) return $"{(orkMesh is not null ? $"Ork mesh {orkMesh} has no headless body" : "no head piece")}: left as today";

        // The loose head from the cut frame's pose: whole head pieces (a marine), or the head triangles of the body (an Ork) before it swaps.
        var headPose = sk.GetBoneGlobalPose(headBone);
        var g = sk.GlobalTransform * headPose;
        // Baked in world axes round the neck (a Havok bone frame may be mirrored, and a physics body cannot carry a mirror: the head would draw inside out).
        var toWorld = new Transform3D(Basis.Identity, -g.Origin) * sk.GlobalTransform;
        var mesh = new ArrayMesh();
        for (int i = 0; i < pieces.Count; i++)
        {
            bool whole = shares[i] >= ExecutionDecapRules.HeadPieceShare;
            if (kind == ExecutionDecapBody.HeadPieces && !whole) continue;
            if (kind == ExecutionDecapBody.OrkStump && shares[i] <= 0f) continue;
            ExecHeadSurfaces(pieces[i], sk, head, toWorld, whole, mesh, sk.GlobalTransform.Basis.Determinant() < 0f);
        }
        if (mesh.GetSurfaceCount() == 0) return "nothing on the head bones: left as today";

        var d = new ExecDecap { Key = key, CutAt = _connectedFor };
        int bareStump = 0;
        if (kind == ExecutionDecapBody.HeadPieces)
        {
            for (int i = 0; i < pieces.Count; i++) if (shares[i] >= ExecutionDecapRules.HeadPieceShare) { pieces[i].Visible = false; d.Hidden.Add(pieces[i]); }
            d.Cap = ExecNeckCap(sk, headBone);
        }
        else
        {
            var body = pieces.OrderByDescending(p => p.Mesh!.GetSurfaceCount()).First(p => ExecHeadShare(p, sk, head) is > 0f and < 1f);
            d.Swapped = (body, body.Mesh!, body.Skin!, Enumerable.Range(0, body.GetSurfaceOverrideMaterialCount()).Select(body.GetSurfaceOverrideMaterial).ToArray());
            body.Mesh = stump!.Mesh;
            body.Skin = stump.Skin;
            // The stump's own two-layer material draws as bare metal-grey here (boy_melee's stump names the Nob's textures; the blend is a provisional approximation, spec "Two-layer
            // blend on skinned meshes"): every stump surface wears the body's own skin material instead (ours). The cut end shows skin; the neck wound's blood covers it.
            var orig = d.Swapped.Value.Mesh;
            int biggest = 0, most = -1;
            for (int s = 0; s < orig.GetSurfaceCount(); s++) { int n = (orig as ArrayMesh)?.SurfaceGetArrayLen(s) ?? 0; if (n > most) { most = n; biggest = s; } }
            var skinMat = orig.GetSurfaceCount() > 0 ? d.Swapped.Value.Overrides.ElementAtOrDefault(biggest) ?? orig.SurfaceGetMaterial(biggest) : null;
            for (int s = 0; s < body.GetSurfaceOverrideMaterialCount(); s++) { body.SetSurfaceOverrideMaterial(s, skinMat); bareStump++; }
        }

        // The rigid head (no scale on a physics body: the proxy's scale goes on the mesh and the sphere).
        var scale = g.Basis.Scale.Abs();
        float s1 = (scale.X + scale.Y + scale.Z) / 3f;
        var overlay = pieces.Select(p => p.MaterialOverlay).FirstOrDefault(o => o is not null);
        var drawn = new MeshInstance3D { Name = "Head", Mesh = mesh, MaterialOverlay = overlay };
        var centre = mesh.GetAabb().GetCenter();
        var rb = new RigidBody3D
        {
            Name = $"ExecutionHead{cr.Run.Target}", Mass = ExecutionDecapRules.HeadMass, CollisionLayer = 0, CollisionMask = Ragdoll.WorldLayer, ContinuousCd = true, CanSleep = true,
            PhysicsMaterialOverride = new PhysicsMaterial { Friction = 0.9f, Bounce = 0.15f },
        };
        rb.AddChild(drawn);
        rb.AddChild(new CollisionShape3D { Shape = new SphereShape3D { Radius = ExecutionDecapRules.HeadRadius * s1 }, Position = centre });
        _gameSpace.AddChild(rb);
        rb.GlobalTransform = new Transform3D(Basis.Identity, g.Origin);
        var away = (ExecVictimAt(cr, head: true) ?? g.Origin) - (ExecAttackerAt(cr) ?? g.Origin);
        var (lin, ang) = ExecutionDecapRules.Toss(new System.Numerics.Vector3(away.X, away.Y, away.Z), cr.Run.StartTick ^ (uint)cr.Run.Executor);
        rb.LinearVelocity = new Vector3(lin.X, lin.Y, lin.Z);
        rb.AngularVelocity = new Vector3(ang.X, ang.Y, ang.Z);
        d.Head = rb;

        // The neck wound: the data's head point at the neck (the head bone's root), following the body.
        if (_fx is { IsReady: true } fx && _execCueFxReady)
            foreach (var (effect, size) in ExecutionDecapRules.NeckWound)
            {
                Func<(Vector3 Position, Basis Axes)?> follow = () => IsInstanceValid(sk) && sk.IsInsideTree() ? ((sk.GlobalTransform * sk.GetBoneGlobalPose(headBone)).Origin, Basis.Identity) : null;
                if (follow() is { } at && fx.Play(effect, at.Position, at.Axes, size, null, follow, 0f, 0.0, priority: 1) is { } e) cr.Effects.Add(e);
            }
        _execDecaps[key] = d;
        ExecutionDecapCount++;
        return $"head cut ({kind}{(orkMesh is not null ? " " + orkMesh : "")}; {mesh.GetSurfaceCount()} head surfaces, toss {rb.LinearVelocity.Length():F1} m/s{(bareStump > 0 ? $", {bareStump} stump surfaces on the body's skin material" : "")})";
    }

    /// <summary>The share of a piece's vertices whose heaviest bone is on the head.</summary>
    static float ExecHeadShare(MeshInstance3D piece, Skeleton3D sk, bool[] head)
    {
        if (piece.Mesh is not ArrayMesh mesh || piece.Skin is not { } skin) return 0f;
        int all = 0, on = 0;
        for (int s = 0; s < mesh.GetSurfaceCount(); s++)
        {
            var arrays = mesh.SurfaceGetArrays(s);
            var bones = arrays[(int)Mesh.ArrayType.Bones].AsInt32Array();
            var weights = arrays[(int)Mesh.ArrayType.Weights].AsFloat32Array();
            int n = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array().Length;
            if (n == 0 || bones.Length < n) continue;
            int per = bones.Length / n;
            for (int v = 0; v < n; v++) { all++; if (ExecOnHead(bones, weights, v, per, skin, sk, head)) on++; }
        }
        return all == 0 ? 0f : on / (float)all;
    }

    static int ExecBindBone(Skin skin, int bind, Skeleton3D sk)
    {
        if ((uint)bind >= (uint)skin.GetBindCount()) return -1;
        int b = skin.GetBindBone(bind);
        return b >= 0 ? b : sk.FindBone(skin.GetBindName(bind));
    }

    static bool ExecOnHead(int[] bones, float[] weights, int v, int per, Skin skin, Skeleton3D sk, bool[] head)
    {
        int best = -1; float bw = -1f;
        for (int k = 0; k < per; k++) if (weights[v * per + k] > bw) { bw = weights[v * per + k]; best = bones[v * per + k]; }
        int bone = ExecBindBone(skin, best, sk);
        return (uint)bone < (uint)head.Length && head[bone];
    }

    /// <summary>
    /// Adds a piece's head triangles (all of them for a <paramref name="whole"/> head piece) to <paramref name="into"/>, skinned in the current pose and put through <paramref name="toWorld"/>
    /// (skeleton space to the loose head's world-aligned frame). Under a mirrored skeleton node (<paramref name="mirrored"/>) the renderer turns the faces round; baked, the triangles are turned instead.
    /// </summary>
    static void ExecHeadSurfaces(MeshInstance3D piece, Skeleton3D sk, bool[] head, Transform3D toWorld, bool whole, ArrayMesh into, bool mirrored)
    {
        var mesh = (ArrayMesh)piece.Mesh!;
        var skin = piece.Skin!;
        var binds = new Transform3D[skin.GetBindCount()];
        for (int j = 0; j < binds.Length; j++) binds[j] = ExecBindBone(skin, j, sk) is int b and >= 0 ? toWorld * sk.GetBoneGlobalPose(b) * skin.GetBindPose(j) : Transform3D.Identity;
        for (int s = 0; s < mesh.GetSurfaceCount(); s++)
        {
            if (mesh.SurfaceGetPrimitiveType(s) != Mesh.PrimitiveType.Triangles) continue;
            var arrays = mesh.SurfaceGetArrays(s);
            var pos = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            var bones = arrays[(int)Mesh.ArrayType.Bones].AsInt32Array();
            var weights = arrays[(int)Mesh.ArrayType.Weights].AsFloat32Array();
            int n = pos.Length;
            if (n == 0 || bones.Length < n || weights.Length < bones.Length) continue;
            int per = bones.Length / n;
            var index = arrays[(int)Mesh.ArrayType.Index].VariantType == Variant.Type.Nil ? Enumerable.Range(0, n).ToArray() : arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
            var onHead = new bool[n];
            for (int v = 0; v < n; v++) onHead[v] = whole || ExecOnHead(bones, weights, v, per, skin, sk, head);
            var keep = new List<int>();
            for (int t = 0; t + 2 < index.Length; t += 3)
                if (whole || ExecutionDecapRules.HeadTriangle(onHead[index[t]], onHead[index[t + 1]], onHead[index[t + 2]])) { keep.Add(index[t]); keep.Add(index[t + 1]); keep.Add(index[t + 2]); }
            if (keep.Count == 0) continue;
            // The kept vertices, renumbered, skinned (linear blend) into the head bone's space.
            var map = new Dictionary<int, int>();
            var used = new List<int>();
            var newIndex = new int[keep.Count];
            for (int i = 0; i < keep.Count; i++) { if (!map.TryGetValue(keep[i], out int ni)) { ni = used.Count; map[keep[i]] = ni; used.Add(keep[i]); } newIndex[i] = ni; }
            if (mirrored)
                for (int t = 0; t + 2 < newIndex.Length; t += 3) (newIndex[t + 1], newIndex[t + 2]) = (newIndex[t + 2], newIndex[t + 1]);
            var normals = arrays[(int)Mesh.ArrayType.Normal].VariantType == Variant.Type.Nil ? null : arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
            var tangents = arrays[(int)Mesh.ArrayType.Tangent].VariantType == Variant.Type.Nil ? null : arrays[(int)Mesh.ArrayType.Tangent].AsFloat32Array();
            var uv = arrays[(int)Mesh.ArrayType.TexUV].VariantType == Variant.Type.Nil ? null : arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array();
            var uv2 = arrays[(int)Mesh.ArrayType.TexUV2].VariantType == Variant.Type.Nil ? null : arrays[(int)Mesh.ArrayType.TexUV2].AsVector2Array();
            var colours = arrays[(int)Mesh.ArrayType.Color].VariantType == Variant.Type.Nil ? null : arrays[(int)Mesh.ArrayType.Color].AsColorArray();
            var oPos = new Vector3[used.Count];
            var oNrm = normals is null ? null : new Vector3[used.Count];
            var oTan = tangents is null || tangents.Length < n * 4 ? null : new float[used.Count * 4];
            for (int i = 0; i < used.Count; i++)
            {
                int v = used[i];
                var p = Vector3.Zero; var nr = Vector3.Zero; var tg = Vector3.Zero;
                for (int k = 0; k < per; k++)
                {
                    float w = weights[v * per + k];
                    int j = bones[v * per + k];
                    if (w <= 0f || (uint)j >= (uint)binds.Length) continue;
                    p += binds[j] * pos[v] * w;
                    if (normals is not null) nr += binds[j].Basis * normals[v] * w;
                    if (oTan is not null) tg += binds[j].Basis * new Vector3(tangents![v * 4], tangents[v * 4 + 1], tangents[v * 4 + 2]) * w;
                }
                oPos[i] = p;
                if (oNrm is not null) oNrm[i] = nr.LengthSquared() > 1e-12f ? nr.Normalized() : Vector3.Up;
                if (oTan is not null)
                {
                    var tn = tg.LengthSquared() > 1e-12f ? tg.Normalized() : Vector3.Right;
                    oTan[i * 4] = tn.X; oTan[i * 4 + 1] = tn.Y; oTan[i * 4 + 2] = tn.Z; oTan[i * 4 + 3] = tangents![v * 4 + 3];
                }
            }
            var outArrays = new Godot.Collections.Array();
            outArrays.Resize((int)Mesh.ArrayType.Max);
            outArrays[(int)Mesh.ArrayType.Vertex] = oPos;
            if (oNrm is not null) outArrays[(int)Mesh.ArrayType.Normal] = oNrm;
            if (oTan is not null) outArrays[(int)Mesh.ArrayType.Tangent] = oTan;
            if (uv is not null && uv.Length >= n) outArrays[(int)Mesh.ArrayType.TexUV] = used.Select(v => uv[v]).ToArray();
            if (uv2 is not null && uv2.Length >= n) outArrays[(int)Mesh.ArrayType.TexUV2] = used.Select(v => uv2[v]).ToArray();
            if (colours is not null && colours.Length >= n) outArrays[(int)Mesh.ArrayType.Color] = used.Select(v => colours[v]).ToArray();
            outArrays[(int)Mesh.ArrayType.Index] = newIndex;
            into.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, outArrays);
            into.SurfaceSetMaterial(into.GetSurfaceCount() - 1, piece.GetSurfaceOverrideMaterial(s) ?? mesh.SurfaceGetMaterial(s));
        }
    }

    static StandardMaterial3D? _execCapMaterial;

    /// <summary>The marine's neck cap (ours; the armour has no stump): a flat dark-red disc on the head bone's root, under the skeleton so it follows the body.</summary>
    static Node3D ExecNeckCap(Skeleton3D sk, int headBone)
    {
        _execCapMaterial ??= new StandardMaterial3D { AlbedoColor = new Color(0.22f, 0.02f, 0.015f), Roughness = 0.35f, Metallic = 0f };
        var attach = new BoneAttachment3D { Name = "ExecutionNeckCap", BoneName = sk.GetBoneName(headBone) };
        attach.AddChild(new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.085f, Height = 0.06f, RadialSegments = 12, Rings = 4, Material = _execCapMaterial } });
        sk.AddChild(attach);
        return attach;
    }

    /// <summary>Once a frame: a loose head sinks with its corpse, and goes with it (or after <see cref="ExecutionDecapRules.HeadMaxSeconds"/>).</summary>
    void ExecutionDecapFrame()
    {
        if (_execDecaps.Count == 0) return;
        _execDecapDrop.Clear();
        foreach (var (key, d) in _execDecaps)
        {
            if (!_execProxies.TryGetValue(key, out var p) || p.Viewer is null) { _execDecapDrop.Add(key); continue; }
            if (_connectedFor - d.CutAt > ExecutionDecapRules.HeadMaxSeconds) { ExecDecapFreeHead(d); continue; }
            if (d.Head is not { } h || !IsInstanceValid(h)) continue;
            float lie = p.Warfront ? MusouCrowd.SinkAfter : ClassicProxyLieSeconds, sink = p.Warfront ? MusouCrowd.SinkSeconds : ProxySinkSeconds;
            float down = p.EndedAt is { } end && p.Done ? ExecutionPresentMath.Sink(_connectedFor - end, lie, sink, ProxySinkDepth) : 0f;
            if (down <= 0f) continue;
            if (d.RestAt is null) { h.Freeze = true; d.RestAt = h.GlobalPosition; }
            h.GlobalPosition = d.RestAt.Value + Vector3.Down * down;
        }
        foreach (var key in _execDecapDrop) if (_execDecaps.Remove(key, out var d)) ExecDecapRelease(d);
    }

    /// <summary>The proxy goes (or returns to the pool): its head and body come back, the loose head and the cap go.</summary>
    void ExecDecapReleaseFor(ExecProxy p)
    {
        if (_execDecaps.Remove((p.Run.Executor, p.Run.StartTick), out var d)) ExecDecapRelease(d);
    }

    void ExecDecapRelease(ExecDecap d)
    {
        foreach (var m in d.Hidden) if (IsInstanceValid(m)) m.Visible = true;
        d.Hidden.Clear();
        if (d.Swapped is { } sw && IsInstanceValid(sw.Instance))
        {
            sw.Instance.Mesh = sw.Mesh; sw.Instance.Skin = sw.Skin;
            for (int i = 0; i < sw.Overrides.Length && i < sw.Instance.GetSurfaceOverrideMaterialCount(); i++) sw.Instance.SetSurfaceOverrideMaterial(i, sw.Overrides[i]);
        }
        d.Swapped = null;
        if (d.Cap is { } cap && IsInstanceValid(cap)) cap.QueueFree();
        d.Cap = null;
        ExecDecapFreeHead(d);
    }

    void ExecDecapFreeHead(ExecDecap d)
    {
        if (d.Head is { } h && IsInstanceValid(h)) h.QueueFree();
        d.Head = null;
    }
}
