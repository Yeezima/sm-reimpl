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
/// <item>Ork Boy meshes with a headless body in the install (<see cref="ExecutionDecapRules.OrkStumpMeshes"/>): the proxy's body swaps to that block (<c>GEO_Wounded_Neck</c>, original asset),
/// drawn with the look's other gear that does not hang from the head.</item>
/// <item>Marines: the head pieces (helmet or bare head: pieces mostly on the head bones) are hidden (<see cref="CharacterViewer.SetPieceHidden"/>, so a corpse made from the proxy keeps them
/// hidden) and a dark neck cap (ours) closes the collar.</item>
/// <item>The loose head (ours): the triangles of the proxy's own meshes on the head bones, frozen in the cut frame's pose, as a rigid body with a sphere against the map. It lies with the
/// corpse, sinks with it and goes when the proxy goes (at the latest <see cref="ExecutionDecapRules.HeadMaxSeconds"/>). When a Classic proxy is handed to a ragdoll (the executor killed
/// after the cut), the head and the cut body go with the ragdoll and the head is freed with it.</item>
/// <item>The neck wound: the head point of the row's weapon (<see cref="ExecutionDecapRules.NeckWoundFor"/>) at the neck, following it; the geyser is the row's own cue.</item>
/// </list>
/// Drawn on every client from the shared clock (the cut is a cue of <see cref="ExecutionCueTimeline"/>), only where execution gore draws (<see cref="ExecutionDecapRules.Allowed"/>). A proxy
/// handed back to the pool gets its head and body back first. <c>--no-execution-decap</c> turns the cut off alone (a test aid).
/// </summary>
public partial class NetworkView
{
    static readonly bool NoExecDecap = CmdFlag("no-execution-decap");
    /// <summary>
    /// Test aid <c>--execution-decap-cam</c>: while a victim proxy lives, the camera stands level beside the pair (3.6 m off the line between them); with <c>--execution-decap-cam-victim</c>
    /// it watches the victim alone from about 2.8 m out and 2.4 m up, following the body down and while it lies.
    /// </summary>
    static readonly bool ExecDecapCam = CmdFlag("execution-decap-cam") || CmdFlag("execution-decap-cam-victim");
    static readonly bool ExecDecapCamVictim = CmdFlag("execution-decap-cam-victim");
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
            var line = attacker - victim; line.Y = 0f;
            var side = line.LengthSquared() > 1e-4f ? line.Normalized().Cross(Vector3.Up) : Vector3.Right;
            Vector3 focus, eye;
            if (ExecDecapCamVictim)
            {
                // The victim's chest (its pelvis bone once down), smoothed; the eye on the far side from the attacker, a little above.
                var body = v.Skeleton is { } sk && ExecFindBone(sk, "RIG_Pelvis") is int pelvis and >= 0 ? (sk.GlobalTransform * sk.GetBoneGlobalPose(pelvis)).Origin : victim;
                _execDecapCamFocus = _execDecapCamFocus is { } f ? f.Lerp(body, 0.08f) : body;
                focus = _execDecapCamFocus.Value;
                var away = line.LengthSquared() > 1e-4f ? -line.Normalized() : Vector3.Back;
                eye = focus + (side * 0.8f + away * 0.6f).Normalized() * 2.8f + Vector3.Up * 2.4f;   // high enough to look over bystanders (a Warfront line of Guardsmen)
            }
            else
            {
                _execDecapCamFocus ??= (victim + attacker) * 0.5f;
                focus = _execDecapCamFocus.Value;
                eye = focus + side * 3.6f + Vector3.Up * 0.5f;
            }
            _camera.GlobalTransform = new Transform3D(Basis.LookingAt((focus - eye).Normalized(), Vector3.Up), eye);
            return true;
        }
        _execDecapCamFocus = null;
        return false;
    }

    sealed class ExecDecap
    {
        public required (int, uint) Key;
        public required int Target;
        public required ExecutionDecapBody Kind;
        public string? OrkMesh;
        public RigidBody3D? Head;
        public CharacterViewer? Viewer;
        public readonly List<string> HiddenNames = new();
        public (MeshInstance3D Instance, Mesh Mesh, Skin Skin, Material?[] Overrides)? Swapped;
        public Node3D? Cap;
        public double CutAt;
        public Vector3? RestAt;
        /// <summary>The proxy went to a ragdoll (Classic, executor killed after the cut): the head waits for the corpse, then belongs to it.</summary>
        public double? HandedAt;
        public Ragdoll? Corpse;
    }

    readonly Dictionary<(int, uint), ExecDecap> _execDecaps = new();
    readonly List<ExecDecap> _execDecapHanded = new();
    readonly List<(int, uint)> _execDecapDrop = new();
    Task<Dictionary<string, (StaticModel Model, ObjectTree Tree)>>? _execStumpRead;
    readonly Dictionary<string, PieceAsset?> _execStumps = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Per headless body, which of its surfaces (in the asset's order: the model's surfaces of the drawn blocks) belong to the neck block.</summary>
    readonly Dictionary<string, bool[]> _execStumpNeck = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Cuts made (the self-test reads it).</summary>
    internal int ExecutionDecapCount { get; private set; }
    /// <summary>Cuts carried to a ragdoll (the self-test reads it).</summary>
    internal int ExecutionDecapCarried { get; private set; }
    /// <summary>Loose heads alive now.</summary>
    internal int ExecutionLooseHeads => _execDecaps.Values.Concat(_execDecapHanded).Count(d => d.Head is { } h && IsInstanceValid(h));

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

    /// <summary>
    /// The headless body of an Ork mesh (built on the main thread once), or null when the install has none or its read has not finished: the neck block with the look's rigid gear
    /// (as <see cref="OrkAssets.Build"/> draws it: nearest level, not wounded) except a block hung from a head bone (review 3: the Rokkit Boy's mortar and rocket back stay on).
    /// </summary>
    PieceAsset? ExecStump(string mesh)
    {
        if (_execStumps.TryGetValue(mesh, out var built)) return built;
        if (_execStumpRead is not { IsCompleted: true } read || read.IsFaulted || Orks() is not { } orks) return null;
        PieceAsset? asset = null;
        if (read.Result.TryGetValue(mesh, out var m))
        {
            var blocks = m.Model.Mesh.Blocks;
            var neck = m.Tree.Visuals.FirstOrDefault(v => v.Renderable && v.IsSkinned && v.MeshId >= 0 && v.MeshId < blocks.Count
                                                          && v.RefNode.Contains(ExecutionDecapRules.StumpNode, StringComparison.OrdinalIgnoreCase));
            if (neck is not null)
            {
                var drawn = new HashSet<int> { neck.MeshId };
                var rigid = new Dictionary<int, RigidBlock>();
                var said = new List<string>();
                foreach (var v in m.Tree.Visuals.Where(v => v.Renderable && v.NearLevel && v.MeshId >= 0 && v.MeshId < blocks.Count && !v.IsSkinned && blocks[v.MeshId].Skin is null
                                                            && !v.RefNode.Contains("wounded", StringComparison.OrdinalIgnoreCase)))
                {
                    if (m.Tree.UnderBone(v.RefNode, orks.Rig.BoneNames.Contains) is not { } placed) continue;
                    bool onHead = ExecutionDecapRules.IsHeadBoneName(placed.Bone);
                    said.Add($"{v.RefNode} on {placed.Bone}{(onHead ? " (head: left out)" : "")}");
                    if (onHead) continue;
                    rigid[v.MeshId] = new RigidBlock(placed.Bone, placed.Local);
                    drawn.Add(v.MeshId);
                }
                var hidden = Enumerable.Range(0, blocks.Count).Where(b => !drawn.Contains(b)).ToHashSet();
                asset = orks.Pieces.Build("decap_" + mesh, new CharacterPiece("decap_" + mesh, m.Model, null) { Rigid = rigid, Hidden = hidden }, vertexColours: true);
                _execStumpNeck[mesh] = m.Model.Surfaces.Where(x => !hidden.Contains(x.BlockIndex)).Select(x => x.BlockIndex == neck.MeshId).ToArray();
                GD.Print($"execution decap: headless {mesh}: neck block {neck.MeshId} ('{neck.RefNode}'), gear: {(said.Count > 0 ? string.Join(", ", said) : "none")}");
            }
        }
        return _execStumps[mesh] = asset;
    }

    // ---------------------------------------------------------------- the head's triangles, cached per mesh (review 4: the arrays and the head test once, the skinning per cut)

    sealed class HeadSurface
    {
        public required int Surface;
        public required Vector3[] Pos;
        public Vector3[]? Normals;
        public float[]? Tangents;
        public Vector2[]? Uv, Uv2;
        public Color[]? Colours;
        public required int[] Bones;
        public required float[] Weights;
        public required int Per;
        public required int[] All, Partial;   // every triangle's corners; the head triangles' corners
    }

    sealed class HeadMesh
    {
        public float Share;
        public readonly List<HeadSurface> Surfaces = new();
    }

    readonly Dictionary<(ulong Mesh, ulong Skin), HeadMesh> _execHeadMeshes = new();

    HeadMesh ExecHeadMesh(MeshInstance3D piece, Skeleton3D sk, bool[] head)
    {
        var key = (piece.Mesh!.GetInstanceId(), piece.Skin!.GetInstanceId());
        if (_execHeadMeshes.TryGetValue(key, out var hm)) return hm;
        hm = new HeadMesh();
        var mesh = (ArrayMesh)piece.Mesh;
        var skin = piece.Skin;
        int all = 0, on = 0;
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
            var onHead = new bool[n];
            for (int v = 0; v < n; v++) { onHead[v] = ExecOnHead(bones, weights, v, per, skin, sk, head); all++; if (onHead[v]) on++; }
            var index = arrays[(int)Mesh.ArrayType.Index].VariantType == Variant.Type.Nil ? Enumerable.Range(0, n).ToArray() : arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
            var partial = new List<int>();
            for (int t = 0; t + 2 < index.Length; t += 3)
                if (ExecutionDecapRules.HeadTriangle(onHead[index[t]], onHead[index[t + 1]], onHead[index[t + 2]])) { partial.Add(index[t]); partial.Add(index[t + 1]); partial.Add(index[t + 2]); }
            Variant Get(Mesh.ArrayType a) => arrays[(int)a];
            hm.Surfaces.Add(new HeadSurface
            {
                Surface = s, Pos = pos, Bones = bones, Weights = weights, Per = per, All = index, Partial = partial.ToArray(),
                Normals = Get(Mesh.ArrayType.Normal).VariantType == Variant.Type.Nil ? null : Get(Mesh.ArrayType.Normal).AsVector3Array(),
                Tangents = Get(Mesh.ArrayType.Tangent).VariantType == Variant.Type.Nil ? null : Get(Mesh.ArrayType.Tangent).AsFloat32Array(),
                Uv = Get(Mesh.ArrayType.TexUV).VariantType == Variant.Type.Nil ? null : Get(Mesh.ArrayType.TexUV).AsVector2Array(),
                Uv2 = Get(Mesh.ArrayType.TexUV2).VariantType == Variant.Type.Nil ? null : Get(Mesh.ArrayType.TexUV2).AsVector2Array(),
                Colours = Get(Mesh.ArrayType.Color).VariantType == Variant.Type.Nil ? null : Get(Mesh.ArrayType.Color).AsColorArray(),
            });
        }
        hm.Share = all == 0 ? 0f : on / (float)all;
        return _execHeadMeshes[key] = hm;
    }

    // ---------------------------------------------------------------- the cut

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
        var watch = System.Diagnostics.Stopwatch.StartNew();
        if (ExecHeadSet(sk) is not { } hs) return "no head bone: left as today";
        var (headBone, head) = hs;

        var pieces = sk.GetChildren().OfType<MeshInstance3D>().Where(m => m.Visible && m.Mesh is ArrayMesh && m.Skin is not null && m.Name != "Bones").ToList();
        var data = pieces.Select(p => ExecHeadMesh(p, sk, head)).ToList();
        int headPieces = data.Count(h => h.Share >= ExecutionDecapRules.HeadPieceShare);
        string? orkMesh = proxy.Assets is { Pieces.Count: > 0 } a ? ExecutionDecapRules.OrkMeshOf(a.Pieces[0].Name) : null;
        var kind = ExecutionDecapRules.BodyFor(row, orkMesh, headPieces);
        PieceAsset? stump = null;
        if (kind == ExecutionDecapBody.OrkStump && (stump = ExecStump(orkMesh!)) is null) return $"no headless body for {orkMesh} (not read yet or none): left as today";
        if (kind == ExecutionDecapBody.None) return $"{(orkMesh is not null ? $"Ork mesh {orkMesh} has no headless body" : "no head piece")}: left as today";
        int bodyIndex = -1;
        if (kind == ExecutionDecapBody.OrkStump)
        {
            for (int i = 0; i < pieces.Count; i++) if (data[i].Share is > 0f and < 1f && (bodyIndex < 0 || pieces[i].Mesh!.GetSurfaceCount() > pieces[bodyIndex].Mesh!.GetSurfaceCount())) bodyIndex = i;
            if (bodyIndex < 0) return "no body with a head on it: left as today";
        }

        // The loose head from the cut frame's pose: whole head pieces (a marine), or the head triangles of the body (an Ork) before it swaps.
        var g = sk.GlobalTransform * sk.GetBoneGlobalPose(headBone);
        // Baked in world axes round the neck (a Havok bone frame may be mirrored, and a physics body cannot carry a mirror: the head would draw inside out).
        var toWorld = new Transform3D(Basis.Identity, -g.Origin) * sk.GlobalTransform;
        var mesh = new ArrayMesh();
        bool mirrored = sk.GlobalTransform.Basis.Determinant() < 0f;
        for (int i = 0; i < pieces.Count; i++)
        {
            bool whole = data[i].Share >= ExecutionDecapRules.HeadPieceShare;
            if (kind == ExecutionDecapBody.HeadPieces ? !whole : i != bodyIndex) continue;
            ExecHeadSurfaces(pieces[i], data[i], sk, toWorld, whole, mesh, mirrored);
        }
        if (mesh.GetSurfaceCount() == 0) return "nothing on the head bones: left as today";

        var d = new ExecDecap { Key = key, Target = cr.Run.Target, Kind = kind, OrkMesh = orkMesh, CutAt = _connectedFor, Viewer = viewer };
        if (kind == ExecutionDecapBody.HeadPieces)
        {
            // By name through the viewer, so a corpse made from this proxy (Ragdoll.Start copies HiddenPieces) keeps the head off (review 1).
            for (int i = 0; i < pieces.Count; i++)
                if (data[i].Share >= ExecutionDecapRules.HeadPieceShare && viewer.SetPieceHidden(pieces[i].Name, true)) d.HiddenNames.Add(pieces[i].Name);
            d.Cap = ExecNeckCap(sk, headBone);
        }
        else d.Swapped = ExecSwapStump(pieces[bodyIndex], stump!, orkMesh!);

        if (_ragdollWorld is null || !IsInstanceValid(_ragdollWorld)) EnsureRagdollWorld();   // the level the head falls on is the ragdolls' (review 5)
        var rb = ExecLooseHead(mesh, g, pieces.Select(p => p.MaterialOverlay).FirstOrDefault(o => o is not null), cr.Run.Target);
        _gameSpace.AddChild(rb);
        rb.GlobalTransform = new Transform3D(Basis.Identity, g.Origin);
        var away = (ExecVictimAt(cr, head: true) ?? g.Origin) - (ExecAttackerAt(cr) ?? g.Origin);
        var (lin, ang) = ExecutionDecapRules.Toss(new System.Numerics.Vector3(away.X, away.Y, away.Z), cr.Run.StartTick ^ (uint)cr.Run.Executor);
        rb.LinearVelocity = new Vector3(lin.X, lin.Y, lin.Z);
        rb.AngularVelocity = new Vector3(ang.X, ang.Y, ang.Z);
        d.Head = rb;

        // The neck wound: the head point of the row's weapon, at the neck (the head bone's root), following the body.
        var (point, effects) = ExecutionDecapRules.NeckWoundFor(cr.Row.Weapon);
        if (_fx is { IsReady: true } fx && _execCueFxReady)
            foreach (var (effect, size) in effects)
            {
                Func<(Vector3 Position, Basis Axes)?> follow = () => IsInstanceValid(sk) && sk.IsInsideTree() ? ((sk.GlobalTransform * sk.GetBoneGlobalPose(headBone)).Origin, Basis.Identity) : null;
                if (follow() is { } at && fx.Play(effect, at.Position, at.Axes, size, null, follow, 0f, 0.0, priority: 1) is { } e) cr.Effects.Add(e);
            }
        _execDecaps[key] = d;
        ExecutionDecapCount++;
        return $"head cut ({kind}{(orkMesh is not null ? " " + orkMesh : "")}; {mesh.GetSurfaceCount()} head surfaces, toss {rb.LinearVelocity.Length():F1} m/s, neck wound {point}; {watch.Elapsed.TotalMilliseconds:F1} ms)";
    }

    /// <summary>The head bone and the bones on the head (it and every bone under it), or null when the rig has none.</summary>
    static (int Bone, bool[] Head)? ExecHeadSet(Skeleton3D sk)
    {
        int headBone = -1;
        foreach (var n in ExecutionDecapRules.HeadBones) { headBone = ExecFindBone(sk, n); if (headBone >= 0) break; }
        if (headBone < 0) return null;
        var head = new bool[sk.GetBoneCount()];
        for (int b = 0; b < head.Length; b++)
            for (int x = b; x >= 0; x = sk.GetBoneParent(x)) if (x == headBone) { head[b] = true; break; }
        return (headBone, head);
    }

    /// <summary>
    /// Swaps an Ork body to its headless body. The stump's own two-layer material draws as bare metal-grey here (boy_melee's stump names the Nob's textures; the blend is a provisional
    /// approximation, spec "Two-layer blend on skinned meshes"): its skinned surfaces wear the body's own skin material instead (ours); the gear keeps its own. Returns what to put back.
    /// </summary>
    (MeshInstance3D, Mesh, Skin, Material?[]) ExecSwapStump(MeshInstance3D body, PieceAsset stump, string mesh)
    {
        var orig = body.Mesh!;
        var keep = (body, orig, body.Skin!, Enumerable.Range(0, body.GetSurfaceOverrideMaterialCount()).Select(body.GetSurfaceOverrideMaterial).ToArray());
        int biggest = 0, most = -1;
        for (int s = 0; s < orig.GetSurfaceCount(); s++) { int n = (orig as ArrayMesh)?.SurfaceGetArrayLen(s) ?? 0; if (n > most) { most = n; biggest = s; } }
        var skinMat = orig.GetSurfaceCount() > 0 ? keep.Item4.ElementAtOrDefault(biggest) ?? orig.SurfaceGetMaterial(biggest) : null;
        body.Mesh = stump.Mesh;
        body.Skin = stump.Skin;
        for (int s = 0; s < body.GetSurfaceOverrideMaterialCount(); s++)
        {
            // The neck block's surfaces wear the skin; the gear keeps its own material.
            bool neckSurface = !_execStumpNeck.TryGetValue(mesh, out var neck) || s >= neck.Length || neck[s];
            body.SetSurfaceOverrideMaterial(s, neckSurface ? skinMat : null);
        }
        return keep;
    }

    RigidBody3D ExecLooseHead(ArrayMesh mesh, Transform3D g, Material? overlay, int target)
    {
        var scale = g.Basis.Scale.Abs();
        float s1 = (scale.X + scale.Y + scale.Z) / 3f;
        var rb = new RigidBody3D
        {
            Name = $"ExecutionHead{target}", Mass = ExecutionDecapRules.HeadMass, CollisionLayer = 0, CollisionMask = Ragdoll.WorldLayer, ContinuousCd = true, CanSleep = true,
            PhysicsMaterialOverride = new PhysicsMaterial { Friction = 0.9f, Bounce = 0.15f },
        };
        rb.AddChild(new MeshInstance3D { Name = "Head", Mesh = mesh, MaterialOverlay = overlay });
        rb.AddChild(new CollisionShape3D { Shape = new SphereShape3D { Radius = ExecutionDecapRules.HeadRadius * s1 }, Position = mesh.GetAabb().GetCenter() });
        return rb;
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
    static void ExecHeadSurfaces(MeshInstance3D piece, HeadMesh hm, Skeleton3D sk, Transform3D toWorld, bool whole, ArrayMesh into, bool mirrored)
    {
        var mesh = (ArrayMesh)piece.Mesh!;
        var skin = piece.Skin!;
        var binds = new Transform3D[skin.GetBindCount()];
        for (int j = 0; j < binds.Length; j++) binds[j] = ExecBindBone(skin, j, sk) is int b and >= 0 ? toWorld * sk.GetBoneGlobalPose(b) * skin.GetBindPose(j) : Transform3D.Identity;
        foreach (var hs in hm.Surfaces)
        {
            var keep = whole ? hs.All : hs.Partial;
            if (keep.Length < 3) continue;
            int n = hs.Pos.Length, per = hs.Per;
            var map = new Dictionary<int, int>();
            var used = new List<int>();
            var newIndex = new int[keep.Length];
            for (int i = 0; i < keep.Length; i++) { if (!map.TryGetValue(keep[i], out int ni)) { ni = used.Count; map[keep[i]] = ni; used.Add(keep[i]); } newIndex[i] = ni; }
            if (mirrored) for (int t = 0; t + 2 < newIndex.Length; t += 3) (newIndex[t + 1], newIndex[t + 2]) = (newIndex[t + 2], newIndex[t + 1]);
            var oPos = new Vector3[used.Count];
            var oNrm = hs.Normals is null ? null : new Vector3[used.Count];
            var oTan = hs.Tangents is null || hs.Tangents.Length < n * 4 ? null : new float[used.Count * 4];
            for (int i = 0; i < used.Count; i++)
            {
                int v = used[i];
                var p = Vector3.Zero; var nr = Vector3.Zero; var tg = Vector3.Zero;
                for (int k = 0; k < per; k++)
                {
                    float w = hs.Weights[v * per + k];
                    int j = hs.Bones[v * per + k];
                    if (w <= 0f || (uint)j >= (uint)binds.Length) continue;
                    p += binds[j] * hs.Pos[v] * w;
                    if (hs.Normals is not null) nr += binds[j].Basis * hs.Normals[v] * w;
                    if (oTan is not null) tg += binds[j].Basis * new Vector3(hs.Tangents![v * 4], hs.Tangents[v * 4 + 1], hs.Tangents[v * 4 + 2]) * w;
                }
                oPos[i] = p;
                if (oNrm is not null) oNrm[i] = nr.LengthSquared() > 1e-12f ? nr.Normalized() : Vector3.Up;
                if (oTan is not null)
                {
                    var tn = tg.LengthSquared() > 1e-12f ? tg.Normalized() : Vector3.Right;
                    oTan[i * 4] = tn.X; oTan[i * 4 + 1] = tn.Y; oTan[i * 4 + 2] = tn.Z; oTan[i * 4 + 3] = hs.Tangents![v * 4 + 3];
                }
            }
            var outArrays = new Godot.Collections.Array();
            outArrays.Resize((int)Mesh.ArrayType.Max);
            outArrays[(int)Mesh.ArrayType.Vertex] = oPos;
            if (oNrm is not null) outArrays[(int)Mesh.ArrayType.Normal] = oNrm;
            if (oTan is not null) outArrays[(int)Mesh.ArrayType.Tangent] = oTan;
            if (hs.Uv is { } uv && uv.Length >= n) outArrays[(int)Mesh.ArrayType.TexUV] = used.Select(v => uv[v]).ToArray();
            if (hs.Uv2 is { } uv2 && uv2.Length >= n) outArrays[(int)Mesh.ArrayType.TexUV2] = used.Select(v => uv2[v]).ToArray();
            if (hs.Colours is { } colours && colours.Length >= n) outArrays[(int)Mesh.ArrayType.Color] = used.Select(v => colours[v]).ToArray();
            outArrays[(int)Mesh.ArrayType.Index] = newIndex;
            into.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, outArrays);
            into.SurfaceSetMaterial(into.GetSurfaceCount() - 1, piece.GetSurfaceOverrideMaterial(hs.Surface) ?? mesh.SurfaceGetMaterial(hs.Surface));
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

    // ---------------------------------------------------------------- lifetime

    /// <summary>Once a frame: a loose head sinks with its proxy corpse; a handed-over head finds its ragdoll, cuts it the same way, and goes with it.</summary>
    void ExecutionDecapFrame()
    {
        foreach (var (key, d) in _execDecaps)
        {
            if (!_execProxies.TryGetValue(key, out var p) || p.Viewer is null) continue;   // the proxy's release frees or hands it over
            if (_connectedFor - d.CutAt > ExecutionDecapRules.HeadMaxSeconds) { ExecDecapFreeHead(d); continue; }
            if (d.Head is not { } h || !IsInstanceValid(h)) continue;
            float lie = p.Warfront ? MusouCrowd.SinkAfter : ClassicProxyLieSeconds, sink = p.Warfront ? MusouCrowd.SinkSeconds : ProxySinkSeconds;
            float down = p.EndedAt is { } end && p.Done ? ExecutionPresentMath.Sink(_connectedFor - end, lie, sink, ProxySinkDepth) : 0f;
            if (down <= 0f) continue;
            if (d.RestAt is null) { h.Freeze = true; d.RestAt = h.GlobalPosition; }
            h.GlobalPosition = d.RestAt.Value + Vector3.Down * down;
        }
        for (int i = _execDecapHanded.Count - 1; i >= 0; i--)
        {
            var d = _execDecapHanded[i];
            bool gone = d.Head is not { } h || !IsInstanceValid(h) || _connectedFor - d.CutAt > ExecutionDecapRules.HeadMaxSeconds;
            if (!gone && d.Corpse is null)
            {
                foreach (var rd in _ragdolls)
                    if (rd.PlayerId == d.Target && !rd.Orphan && IsInstanceValid(rd) && rd.Character is { Skeleton: { } csk } c && IsInstanceValid(c)) { ExecDecapCorpse(d, rd, c, csk); break; }
                if (d.Corpse is null && _connectedFor - (d.HandedAt ?? _connectedFor) > 7.0) gone = true;   // no ragdoll came (a respawn, a dropped death)
            }
            else if (!gone && (!IsInstanceValid(d.Corpse) || !_ragdolls.Contains(d.Corpse!))) gone = true;   // the corpse went (the cap, the sink): the head with it
            if (!gone) continue;
            ExecDecapFreeHead(d);
            _execDecapHanded.RemoveAt(i);
        }
    }

    /// <summary>The ragdoll made from a cut proxy: its body is cut the same way (the head pieces came hidden with the copy; the cap and an Ork's stump are put on again).</summary>
    void ExecDecapCorpse(ExecDecap d, Ragdoll rd, CharacterViewer c, Skeleton3D csk)
    {
        d.Corpse = rd;
        ExecutionDecapCarried++;
        string result;
        if (d.Kind == ExecutionDecapBody.HeadPieces)
        {
            foreach (var name in d.HiddenNames) c.SetPieceHidden(name, true);
            if (ExecHeadSet(csk) is { } hs) ExecNeckCap(csk, hs.Bone);   // freed with the corpse
            result = $"{d.HiddenNames.Count} head pieces hidden, neck cap";
        }
        else
        {
            var body = csk.GetChildren().OfType<MeshInstance3D>().Where(m => m.Mesh is ArrayMesh && m.Skin is not null && m.Name != "Bones").OrderByDescending(m => m.Mesh!.GetSurfaceCount()).FirstOrDefault();
            if (body is not null && d.OrkMesh is { } om && ExecStump(om) is { } stump) { ExecSwapStump(body, stump, om); result = "headless body"; }
            else result = "no body to cut";
        }
        if (ExecCueLog) GD.Print($"EXECCUES t {_connectedFor:F2} s: head cut carried to P{d.Target}'s ragdoll ({result})");
    }

    /// <summary>The proxy goes (or returns to the pool): its head and body come back; the loose head goes, or, when the proxy was handed to a ragdoll, waits for that corpse.</summary>
    void ExecDecapReleaseFor(ExecProxy p)
    {
        if (!_execDecaps.Remove((p.Run.Executor, p.Run.StartTick), out var d)) return;
        ExecDecapRestore(d);
        if (p.RagdollHanded && d.Head is { } h && IsInstanceValid(h)) { d.HandedAt = _connectedFor; _execDecapHanded.Add(d); return; }
        ExecDecapFreeHead(d);
    }

    void ExecDecapRestore(ExecDecap d)
    {
        if (d.Viewer is { } v && IsInstanceValid(v)) foreach (var name in d.HiddenNames) v.SetPieceHidden(name, false);
        if (d.Swapped is { } sw && IsInstanceValid(sw.Instance))
        {
            sw.Instance.Mesh = sw.Mesh; sw.Instance.Skin = sw.Skin;
            for (int i = 0; i < sw.Overrides.Length && i < sw.Instance.GetSurfaceOverrideMaterialCount(); i++) sw.Instance.SetSurfaceOverrideMaterial(i, sw.Overrides[i]);
        }
        d.Swapped = null;
        if (d.Cap is { } cap && IsInstanceValid(cap)) cap.QueueFree();
        d.Cap = null;
    }

    void ExecDecapFreeHead(ExecDecap d)
    {
        if (d.Head is { } h && IsInstanceValid(h)) h.QueueFree();
        d.Head = null;
    }
}
