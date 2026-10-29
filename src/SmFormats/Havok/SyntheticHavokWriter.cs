using System.Numerics;
using System.Text;

namespace SmFormats.Havok;

/// <summary>
/// Builds small synthetic hk_2010 32-bit packfiles following our documented interpretation (docs/discovery.md):
/// a root container with a combiner TOC, one skeleton, and spline-compressed animations with bindings.
/// For tests only; passing proves internal consistency, not compatibility with original files.
/// </summary>
public static class SyntheticHavokWriter
{
    /// <param name="TranslationX">Null: static translation only. Otherwise a linear spline on X between the two values
    /// over the clip (16-bit quantized), with Y and Z static from <paramref name="Translation"/>.</param>
    public sealed record Track(Vector3 Translation, Quaternion Rotation, (float From, float To)? TranslationX = null);
    /// <param name="Motion">Extracted motion samples (X, Y, Z, W), written as an hkaDefaultAnimatedReferenceFrame (up +Z, forward +Y); null: none.</param>
    /// <param name="Additive">Written as the binding's blend hint (byte @40: 1 additive, 0 normal).</param>
    /// <param name="BlendHint">Overrides the blend hint byte (to build broken files).</param>
    public sealed record Clip(string Name, float Duration, int Frames, Track[] Tracks, Vector4[]? Motion = null, bool Additive = false, byte? BlendHint = null);
    public sealed record Bone(string Name, short Parent, Vector3 Translation, Quaternion Rotation);

    public static byte[] Build(IReadOnlyList<Bone> bones, IReadOnlyList<Clip> clips)
    {
        var d = new DataBuilder();
        string[] classes = ["hkRootLevelContainer", "hkCombinerTOC", "hkaSkeleton", "hkaAnimationContainer", "hkaAnimationBinding", "hkaSplineCompressedAnimation",
            "hkaDefaultAnimatedReferenceFrame"];

        // Root container first (as in the game), then the TOC, skeleton container, and one container per clip.
        int root = d.Object("hkRootLevelContainer", 12);
        int toc = d.Object("hkCombinerTOC", 20);
        int skeleton = d.Object("hkaSkeleton", 84);
        d.String(skeleton + 8, bones[0].Name);
        d.Array(skeleton + 12, bones.Count, 2, 2, (at, i) => d.I16(at, bones[i].Parent));
        d.Array(skeleton + 24, bones.Count, 8, 4, (at, i) => d.String(at, bones[i].Name));
        d.Array(skeleton + 36, bones.Count, 48, 16, (at, i) => d.Transform(at, bones[i].Translation, bones[i].Rotation));
        int skeletonContainer = d.Object("hkaAnimationContainer", 68);
        d.Array(skeletonContainer + 8, 1, 4, 4, (at, _) => d.Pointer(at, skeleton));

        var clipContainers = new List<int>();
        foreach (var clip in clips)
        {
            int anim = WriteAnimation(d, clip);
            int binding = d.Object("hkaAnimationBinding", 44);
            d.String(binding + 8, bones[0].Name);
            d.Pointer(binding + 12, anim);
            d.Array(binding + 16, clip.Tracks.Length, 2, 2, (at, i) => d.I16(at, (short)i));
            d.U8(binding + 40, clip.BlendHint ?? (byte)(clip.Additive ? 1 : 0));
            int container = d.Object("hkaAnimationContainer", 68);
            d.Array(container + 20, 1, 4, 4, (at, _) => d.Pointer(at, anim));
            d.Array(container + 32, 1, 4, 4, (at, _) => d.Pointer(at, binding));
            clipContainers.Add(container);
        }

        // TOC: one entry for the skeleton source file (count 1), then one per clip, in variant order.
        var tocEntries = new List<(string Name, string Type)> { ("Rig", "hkxr") };
        tocEntries.AddRange(clips.Select(c => (c.Name, "hkxa")));
        d.Array(toc + 8, tocEntries.Count, 40, 4, (at, i) =>
        {
            d.CharArray(at, tocEntries[i].Name);
            d.CharArray(at + 12, tocEntries[i].Type);
            d.I32(at + 32, 1);
        });
        var variants = new List<(string Name, string Class, int Object)> { ("TOC", "hkCombinerTOC", toc), ("Animation Container", "hkaAnimationContainer", skeletonContainer) };
        variants.AddRange(clipContainers.Select(c => ("Animation Container", "hkaAnimationContainer", c)));
        d.Array(root, variants.Count, 12, 4, (at, i) =>
        {
            d.String(at, variants[i].Name);
            d.String(at + 4, variants[i].Class);
            d.Pointer(at + 8, variants[i].Object);
        });
        return Assemble(classes, d);
    }

    static int WriteAnimation(DataBuilder d, Clip clip)
    {
        int tracks = clip.Tracks.Length;
        var block = new List<byte>();
        foreach (var t in clip.Tracks)
        {
            byte quant = 1 | 1 << 2;                                 // 16-bit positions, THREECOMP40 rotations
            byte posFlags = (byte)(t.TranslationX is null ? 0x07 : 0x10 | 0x06);
            block.AddRange([quant, posFlags, 0x0F, 0x00]);
        }
        while (block.Count % 4 != 0) block.Add(0);
        int maskSize = block.Count;
        foreach (var t in clip.Tracks)
        {
            if (t.TranslationX is { } x)
            {
                int items = 1, degree = 1;
                block.AddRange(BitConverter.GetBytes((ushort)items)); block.Add((byte)degree);
                block.AddRange(new byte[] { 0, 0, (byte)(clip.Frames - 1), (byte)(clip.Frames - 1) });
                Align(block, 4);
                block.AddRange(BitConverter.GetBytes(Math.Min(x.From, x.To))); block.AddRange(BitConverter.GetBytes(Math.Max(x.From, x.To)));
                block.AddRange(BitConverter.GetBytes(t.Translation.Y));
                block.AddRange(BitConverter.GetBytes(t.Translation.Z));
                block.AddRange(BitConverter.GetBytes(x.From <= x.To ? (ushort)0 : (ushort)65535));
                block.AddRange(BitConverter.GetBytes(x.From <= x.To ? (ushort)65535 : (ushort)0));
                Align(block, 4);
            }
            else
            {
                block.AddRange(BitConverter.GetBytes(t.Translation.X)); block.AddRange(BitConverter.GetBytes(t.Translation.Y)); block.AddRange(BitConverter.GetBytes(t.Translation.Z));
            }
            block.AddRange(EncodeThreeComp40(t.Rotation));
            Align(block, 4);
        }
        Align(block, 16);

        int anim = d.Object("hkaSplineCompressedAnimation", 132);
        d.I32(anim + 8, 5);
        d.F32(anim + 12, clip.Duration);
        d.I32(anim + 16, tracks);
        d.I32(anim + 40, clip.Frames);
        d.I32(anim + 44, 1);
        d.I32(anim + 48, 256);
        d.I32(anim + 52, maskSize);
        float frameDuration = clip.Duration / Math.Max(1, clip.Frames - 1);
        d.F32(anim + 56, 255 * frameDuration);
        d.F32(anim + 60, 1f / (255 * frameDuration));
        d.F32(anim + 64, frameDuration);
        d.Array(anim + 68, 1, 4, 4, (at, _) => d.I32(at, 0));
        d.Array(anim + 116, block.Count, 1, 16, (at, i) => d.U8(at, block[i]));
        if (clip.Motion is { } motion)
        {
            int frame = d.Object("hkaDefaultAnimatedReferenceFrame", 64);
            d.F32(frame + 24, 1f);   // up (0, 0, 1, 0) @16
            d.F32(frame + 36, 1f);   // forward (0, 1, 0, 0) @32
            d.F32(frame + 48, clip.Duration);
            d.Array(frame + 52, motion.Length, 16, 16, (at, i) => { d.F32(at, motion[i].X); d.F32(at + 4, motion[i].Y); d.F32(at + 8, motion[i].Z); d.F32(at + 12, motion[i].W); });
            d.Pointer(anim + 24, frame);
        }
        return anim;
    }

    /// <summary>A mesh stored once (local space) and placed by subparts.</summary>
    /// <param name="MaterialIndices">Per-triangle 8-bit material indices in the storage (M25a), or none.</param>
    /// <param name="Materials">The storage's materials' userData values (M25a), or none.</param>
    public sealed record CollisionMesh(Vector3[] Vertices, (int A, int B, int C)[] Triangles, byte[]? MaterialIndices = null, int[]? Materials = null);
    /// <param name="StorageField">Overrides the storage index written to extrusion.w (to build inconsistent files).</param>
    /// <param name="TriangleField">Overrides the triangle count written to the subpart (to build inconsistent files).</param>
    /// <param name="UserData">The subpart's userData (M25a: its surface material; -1 with per-triangle materials, as the game writes).</param>
    public sealed record CollisionPlacement(int Mesh, Vector3 Translation, Quaternion Rotation, Vector3 Scale, float? StorageField = null, int? TriangleField = null,
        int UserData = 4);
    /// <param name="RepeatChild">The list shape names its child twice (a fan-out a reader must not follow twice).</param>
    /// <param name="ShareTreeWithPrevious">The root entry points at the previous variant's tree (its own is still written).</param>
    public sealed record CollisionVariant(string Name, CollisionMesh[] Meshes, CollisionPlacement[] Placements, bool InListShape = true, bool RepeatChild = false,
        bool ShareTreeWithPrevious = false);

    /// <summary>
    /// A map region's collision file as documented in docs/discovery.md: one root variant per layer, each a MOPP tree
    /// (no MOPP code) over a list shape (or directly) over a storage extended mesh shape whose subparts name their mesh
    /// storage in extrusion.w.
    /// </summary>
    public static byte[] BuildWorldCollision(IReadOnlyList<CollisionVariant> variants)
    {
        var d = new DataBuilder();
        string[] classes = ["hkRootLevelContainer", "hkpMoppBvTreeShape", "hkpMoppCode", "hkpListShape", "hkpStorageExtendedMeshShape", "hkpStorageExtendedMeshShapeMeshSubpartStorage"];
        int root = d.Object("hkRootLevelContainer", 12);
        var tops = new List<int>();
        foreach (var v in variants)
        {
            int top = d.Object("hkpMoppBvTreeShape", 64);
            int code = d.Object("hkpMoppCode", 48);
            d.Pointer(top + 20, code);
            int mopp = top;
            if (v.InListShape)
            {
                int list = d.Object("hkpListShape", 112);
                d.Pointer(top + 52, list);
                mopp = d.Object("hkpMoppBvTreeShape", 64);
                d.Array(list + 24, v.RepeatChild ? 2 : 1, 16, 16, (at, _) => d.Pointer(at, mopp));
                int innerCode = d.Object("hkpMoppCode", 48);
                d.Pointer(mopp + 20, innerCode);
            }
            int mesh = d.Object("hkpStorageExtendedMeshShape", 256);
            d.Pointer(mopp + 52, mesh);
            var storages = v.Meshes.Select(m =>
            {
                int s = d.Object("hkpStorageExtendedMeshShapeMeshSubpartStorage", 112);
                d.Array(s + 8, m.Vertices.Length, 16, 16, (at, i) => { d.F32(at, m.Vertices[i].X); d.F32(at + 4, m.Vertices[i].Y); d.F32(at + 8, m.Vertices[i].Z); });
                d.Array(s + 32, 4 * m.Triangles.Length, 2, 4, (at, i) =>
                {
                    var t = m.Triangles[i / 4];
                    d.I16(at, (short)((i % 4) switch { 0 => t.A, 1 => t.B, 2 => t.C, _ => 0 }));
                });
                if (m.MaterialIndices is { } indices) d.Array(s + 56, indices.Length, 1, 4, (at, i) => d.U8(at, indices[i]));
                if (m.Materials is { } materials) d.Array(s + 68, materials.Length, 12, 4, (at, i) => d.I32(at + 8, materials[i]));
                return s;
            }).ToArray();
            int triangleOffset = 0;
            d.Array(mesh + 184, v.Placements.Length, 112, 16, (at, i) =>
            {
                var p = v.Placements[i];
                var m = v.Meshes[p.Mesh];
                d.U8(at + 1, 1);
                if (m.Materials is { Length: > 0 } materials) { d.I16(at + 8, 1); d.I16(at + 10, (short)materials.Length); }
                d.I32(at + 16, p.UserData);
                d.I32(at + 20, p.TriangleField ?? m.Triangles.Length);
                d.I32(at + 28, m.Vertices.Length);
                d.I32(at + 36, 16);
                d.I32(at + 40, triangleOffset);
                d.I16(at + 44, 8);
                d.U8(at + 46, 2);
                d.F32(at + 60, p.StorageField ?? p.Mesh);
                d.F32(at + 64, p.Translation.X); d.F32(at + 68, p.Translation.Y); d.F32(at + 72, p.Translation.Z); d.F32(at + 76, 1);
                d.F32(at + 80, p.Rotation.X); d.F32(at + 84, p.Rotation.Y); d.F32(at + 88, p.Rotation.Z); d.F32(at + 92, p.Rotation.W);
                d.F32(at + 96, p.Scale.X); d.F32(at + 100, p.Scale.Y); d.F32(at + 104, p.Scale.Z);
                triangleOffset += m.Triangles.Length;
            });
            d.Array(mesh + 240, storages.Length, 4, 4, (at, i) => d.Pointer(at, storages[i]));
            tops.Add(top);
        }
        d.Array(root, variants.Count, 12, 4, (at, i) =>
        {
            d.String(at, variants[i].Name);
            d.String(at + 4, "hkpMoppBvTreeShape");
            d.Pointer(at + 8, variants[i].ShareTreeWithPrevious && i > 0 ? tops[i - 1] : tops[i]);
        });
        return Assemble(classes, d);
    }

    /// <param name="Value">Event index (Event, Branch), action name (Action), distance (AttackDistance), a <see cref="ModifierValue"/>
    /// (Modifier) or a <see cref="PropertyValue"/> (EntityProperty).</param>
    public sealed record Tag(string Kind, float Start, float End, object? Value = null);
    /// <summary>A Modifier tag's modifier: an object of <paramref name="Class"/> with its name @32.</summary>
    public sealed record ModifierValue(string Class, string Name);
    /// <summary>An EntityProperty tag's data: an object of <paramref name="Class"/> with the mode @28.</summary>
    public sealed record PropertyValue(string Class, int Mode);
    /// <param name="Animations">One clip, or several blended children.</param>
    /// <param name="SelfReferencingBlend">The blend also lists itself as a child, twice (a corrupt file).</param>
    public sealed record Sequencer(string Name, string[] Animations, Tag[] Tags, float PlaybackSpeed = 1f, bool SelfReferencingBlend = false);
    public sealed record Graph(string Name, string[] Events, Sequencer[] Sequencers);

    /// <summary>
    /// Behavior graphs with sequencers and tags as documented for <see cref="HavokBehavior"/>: per graph, the graph
    /// object, its sequencers, clip or blender generators and tags, then its string data with the event names.
    /// </summary>
    public static byte[] BuildBehavior(IReadOnlyList<Graph> graphs)
    {
        var d = new DataBuilder();
        var classes = new List<string> { "hkRootLevelContainer", "hkbBehaviorGraph", "hkbBehaviorGraphStringData", "hkbSequencerGenerator",
            "hkbClipGenerator", "hkbBlenderGenerator", "hkbBlenderGeneratorChild" };
        int root = d.Object("hkRootLevelContainer", 12);
        var graphObjects = new List<int>();
        foreach (var graph in graphs)
        {
            int g = d.Object("hkbBehaviorGraph", 96);
            d.String(g + 32, graph.Name + ".hkb");
            graphObjects.Add(g);
            foreach (var s in graph.Sequencers)
            {
                int seq = d.Object("hkbSequencerGenerator", 96);
                d.String(seq + 32, s.Name);
                int generator;
                if (s.Animations.Length == 1 && !s.SelfReferencingBlend) generator = Clip(d, s.Animations[0], s.PlaybackSpeed);
                else
                {
                    generator = d.Object("hkbBlenderGenerator", 160);
                    var children = s.Animations.Select(a =>
                    {
                        int child = d.Object("hkbBlenderGeneratorChild", 96);
                        d.Pointer(child + 32, Clip(d, a, s.PlaybackSpeed));
                        return child;
                    }).ToList();
                    if (s.SelfReferencingBlend)
                        for (int k = 0; k < 2; k++)
                        {
                            int loop = d.Object("hkbBlenderGeneratorChild", 96);
                            d.Pointer(loop + 32, generator);
                            children.Add(loop);
                        }
                    d.Array(generator + 64, children.Count, 4, 4, (at, i) => d.Pointer(at, children[i]));
                }
                d.Pointer(seq + 52, generator);
                var tags = s.Tags.Select(t =>
                {
                    string cls = $"hkb{t.Kind}SequenceTag";
                    if (!classes.Contains(cls)) classes.Add(cls);
                    int tag = d.Object(cls, 64);
                    d.F32(tag + 32, t.Start); d.F32(tag + 36, t.End);
                    switch (t.Value)
                    {
                        case int e: d.I32(tag + 52, e); break;
                        case string action: d.String(tag + 52, action); break;
                        case float distance: d.F32(tag + 56, distance); break;
                        case ModifierValue m:
                        {
                            if (!classes.Contains(m.Class)) classes.Add(m.Class);
                            int modifier = d.Object(m.Class, 64);
                            d.String(modifier + 32, m.Name);
                            d.Pointer(tag + 52, modifier);
                            break;
                        }
                        case PropertyValue p:
                        {
                            if (!classes.Contains(p.Class)) classes.Add(p.Class);
                            int data = d.Object(p.Class, 32);
                            d.I32(data + 28, p.Mode);
                            d.Pointer(tag + 52, data);
                            break;
                        }
                    }
                    return tag;
                }).ToArray();
                d.Array(seq + 40, tags.Length, 4, 4, (at, i) => d.Pointer(at, tags[i]));
            }
            int strings = d.Object("hkbBehaviorGraphStringData", 64);
            d.Array(strings + 8, graph.Events.Length, 4, 4, (at, i) => d.String(at, graph.Events[i]));
        }
        d.Array(root, graphObjects.Count, 12, 4, (at, i) =>
        {
            d.String(at, "hkbBehaviorGraph");
            d.String(at + 4, "hkbBehaviorGraph");
            d.Pointer(at + 8, graphObjects[i]);
        });
        return Assemble([.. classes], d);

        static int Clip(DataBuilder d, string animation, float speed)
        {
            int clip = d.Object("hkbClipGenerator", 96);
            d.String(clip + 32, animation + "_clip");
            d.String(clip + 40, animation + ".hkxa");
            d.F32(clip + 60, speed);
            return clip;
        }
    }

    // ---- behaviour graph structure (M27; HavokBehaviorDetailReader) ----

    /// <summary>A generator or state machine node of <see cref="BuildBehaviorStructure"/>. A node instance used twice is written once.</summary>
    public abstract record GraphNode(string Name);
    /// <param name="Mode">Playback mode byte @74.</param>
    /// <param name="EnforcedDurationVariable">The graph variable index bound to <c>enforcedDuration</c> (the clip is stretched to that value), or null.</param>
    public sealed record ClipNode(string Name, string Animation, float PlaybackSpeed = 1f, byte Mode = 1, int? EnforcedDurationVariable = null) : GraphNode(Name);
    /// <param name="ParameterVariable">The graph variable index bound to <c>blendParameter</c>, or null.</param>
    public sealed record BlendNode(string Name, BlendChildFixture[] Children, int? ParameterVariable = null, float BlendParameter = 1f, short Flags = 0x11)
        : GraphNode(Name);
    /// <param name="BoneWeights">An <c>hkbBoneWeightArray</c> for this child, or null.</param>
    public sealed record BlendChildFixture(float Weight, GraphNode Generator, float[]? BoneWeights = null);
    public sealed record SequencerNode(string Name, GraphNode Generator, Tag[] Tags) : GraphNode(Name);
    public sealed record ModifierGeneratorNode(string Name, GraphNode Generator) : GraphNode(Name);
    public sealed record ReferenceNode(string Name, string File) : GraphNode(Name);
    /// <param name="Chooser">Start-state chooser entries (state id, condition), or null for none.</param>
    public sealed record StateMachineNode(string Name, StateFixture[] States, int StartStateId = 0, TransitionFixture[]? Wildcards = null,
        (int StateId, ConditionFixture? Condition)[]? Chooser = null) : GraphNode(Name);
    public sealed record StateFixture(int Id, string Name, GraphNode Generator, TransitionFixture[]? Transitions = null);
    /// <param name="BlendDuration">Writes an <c>hkbBlendingTransitionEffect</c> with this duration (seconds); null: no effect.</param>
    public sealed record TransitionFixture(int ToStateId, int EventId = -1, short BranchEventId = -1, short Priority = 0, float? BlendDuration = null,
        string EffectName = "Blend", ConditionFixture? Condition = null);
    public abstract record ConditionFixture;
    public sealed record ExpressionFixture(string Expression) : ConditionFixture;
    public sealed record ThresholdFixture(int Variable, float Min, float Max) : ConditionFixture;
    public sealed record BoolFixture(int[] True, int[] False) : ConditionFixture;
    public sealed record NegateFixture(ConditionFixture Condition) : ConditionFixture;
    /// <param name="Kind">0 all, 1 any.</param>
    public sealed record MultiFixture(int Kind, ConditionFixture[] Conditions) : ConditionFixture;
    public sealed record StructuredGraph(string Name, string[] Events, string[] Variables, GraphNode Root);

    /// <summary>
    /// Behaviour graphs with state machines, blends (weights, bone masks, bound blend parameters), sequencers, clips, modifier
    /// and reference generators, transitions (events, branch events, priorities, conditions, blending effects) and start-state
    /// choosers, at the offsets documented for <see cref="HavokBehaviorDetailReader"/>. Per graph: the graph object, its nodes,
    /// then its string data (events @8, variables @32).
    /// </summary>
    public static byte[] BuildBehaviorStructure(IReadOnlyList<StructuredGraph> graphs)
    {
        var d = new DataBuilder();
        var classes = new List<string> { "hkRootLevelContainer", "hkbBehaviorGraph", "hkbBehaviorGraphStringData" };
        int Obj(string cls, int size) { if (!classes.Contains(cls)) classes.Add(cls); return d.Object(cls, size); }
        int root = d.Object("hkRootLevelContainer", 12);
        var graphObjects = new List<int>();
        foreach (var graph in graphs)
        {
            var written = new Dictionary<GraphNode, int>(ReferenceEqualityComparer.Instance);
            int g = Obj("hkbBehaviorGraph", 96);
            d.String(g + 32, graph.Name + ".hkb");
            graphObjects.Add(g);
            d.Pointer(g + 76, Node(graph.Root));
            int strings = Obj("hkbBehaviorGraphStringData", 64);
            d.Array(strings + 8, graph.Events.Length, 4, 4, (at, i) => d.String(at, graph.Events[i]));
            d.Array(strings + 32, graph.Variables.Length, 4, 4, (at, i) => d.String(at, graph.Variables[i]));

            int Node(GraphNode node)
            {
                if (written.TryGetValue(node, out int done)) return done;
                int at;
                switch (node)
                {
                    case ClipNode c:
                        at = Obj("hkbClipGenerator", 96);
                        d.String(at + 40, c.Animation + ".hkxa");
                        d.F32(at + 60, c.PlaybackSpeed);
                        d.I16(at + 72, -1);
                        d.U8(at + 74, c.Mode);
                        if (c.EnforcedDurationVariable is int enforced) Binding(at, "enforcedDuration", enforced);
                        break;
                    case BlendNode b:
                    {
                        at = Obj("hkbBlenderGenerator", 160);
                        written[node] = at;
                        d.F32(at + 44, b.BlendParameter);
                        d.I16(at + 56, -1);
                        d.I16(at + 58, b.Flags);
                        if (b.ParameterVariable is int v) Binding(at, "blendParameter", v);
                        var children = b.Children.Select(ch =>
                        {
                            int child = Obj("hkbBlenderGeneratorChild", 48);
                            d.Pointer(child + 32, Node(ch.Generator));
                            d.F32(child + 40, ch.Weight);
                            d.F32(child + 44, 1f);
                            if (ch.BoneWeights is { } w)
                            {
                                int mask = Obj("hkbBoneWeightArray", 48);
                                d.Array(mask + 28, w.Length, 4, 4, (p, i) => d.F32(p, w[i]));
                                d.Pointer(child + 36, mask);
                            }
                            return child;
                        }).ToList();
                        d.Array(at + 64, children.Count, 4, 4, (p, i) => d.Pointer(p, children[i]));
                        break;
                    }
                    case SequencerNode s:
                    {
                        at = Obj("hkbSequencerGenerator", 96);
                        written[node] = at;
                        d.Pointer(at + 52, Node(s.Generator));
                        var tags = s.Tags.Select(t =>
                        {
                            int tag = Obj($"hkb{t.Kind}SequenceTag", 64);
                            d.F32(tag + 32, t.Start); d.F32(tag + 36, t.End);
                            if (t.Value is int e) d.I32(tag + 52, e);
                            else if (t.Value is string action) d.String(tag + 52, action);
                            else if (t.Value is ModifierValue mv)
                            {
                                int modifier = Obj(mv.Class, 64);
                                d.String(modifier + 32, mv.Name);
                                d.Pointer(tag + 52, modifier);
                            }
                            return tag;
                        }).ToArray();
                        d.Array(at + 40, tags.Length, 4, 4, (p, i) => d.Pointer(p, tags[i]));
                        break;
                    }
                    case ModifierGeneratorNode m:
                        at = Obj("hkbModifierGenerator", 80);
                        written[node] = at;
                        d.Pointer(at + 44, Node(m.Generator));
                        break;
                    case ReferenceNode r:
                        at = Obj("hkbBehaviorReferenceGenerator", 64);
                        d.String(at + 40, r.File);
                        break;
                    case StateMachineNode sm:
                    {
                        at = Obj("hkbStateMachine", 256);
                        written[node] = at;
                        d.I32(at + 56, sm.StartStateId);
                        var states = sm.States.Select(st =>
                        {
                            int info = Obj("hkbStateMachineStateInfo", 128);
                            d.Pointer(info + 52, Node(st.Generator));
                            d.String(info + 56, st.Name);
                            d.I32(info + 60, st.Id);
                            d.F32(info + 64, 1f);
                            d.U8(info + 68, 1);
                            if (st.Transitions is { Length: > 0 } ts) d.Pointer(info + 48, Transitions(ts));
                            return info;
                        }).ToList();
                        d.Array(at + 92, states.Count, 4, 4, (p, i) => d.Pointer(p, states[i]));
                        if (sm.Wildcards is { Length: > 0 } wild) d.Pointer(at + 104, Transitions(wild));
                        if (sm.Chooser is { } chooser)
                        {
                            int ch = Obj("hkbConditionChooser", 48);
                            var entries = chooser.Select(e =>
                            {
                                int entry = Obj("hkbConditionChooserConditionEntry", 16);
                                d.I32(entry + 8, e.StateId);
                                if (e.Condition is { } c) d.Pointer(entry + 12, Condition(c));
                                return entry;
                            }).ToList();
                            d.Array(ch + 8, entries.Count, 4, 4, (p, i) => d.Pointer(p, entries[i]));
                            d.Pointer(at + 52, ch);
                        }
                        break;
                    }
                    default: throw new ArgumentException($"Unknown node {node.GetType().Name}.");
                }
                written[node] = at;
                d.String(at + 32, node.Name);
                return at;
            }

            void Binding(int node, string member, int variable)
            {
                int set = Obj("hkbVariableBindingSet", 32);
                d.Array(set + 8, 1, 32, 4, (p, _) => { d.String(p, member); d.I32(p + 20, variable); });
                d.I32(set + 20, -1);
                d.Pointer(node + 8, set);
            }

            int Transitions(TransitionFixture[] list)
            {
                int array = Obj("hkbStateMachineTransitionInfoArray", 32);
                var effects = list.Select(t =>
                {
                    if (t.BlendDuration is not float duration) return (int?)null;
                    int e = Obj("hkbBlendingTransitionEffect", 96);
                    d.String(e + 32, t.EffectName);
                    d.F32(e + 64, duration);
                    return e;
                }).ToList();
                var conditions = list.Select(t => t.Condition is { } c ? Condition(c) : (int?)null).ToList();
                d.Array(array + 8, list.Length, 44, 4, (p, i) =>
                {
                    var t = list[i];
                    if (effects[i] is int e) d.Pointer(p, e);
                    if (conditions[i] is int c) d.Pointer(p + 4, c);
                    d.I32(p + 8, t.EventId);
                    d.I32(p + 12, t.ToStateId);
                    d.I16(p + 24, t.Priority);
                    d.I16(p + 26, t.BranchEventId);
                });
                return array;
            }

            int Condition(ConditionFixture c)
            {
                switch (c)
                {
                    case ExpressionFixture e: { int at = Obj("hkbExpressionCondition", 16); d.String(at + 8, e.Expression); return at; }
                    case ThresholdFixture t: { int at = Obj("hkbThresholdCondition", 32); d.F32(at + 8, t.Min); d.F32(at + 12, t.Max); d.I32(at + 20, t.Variable); return at; }
                    case BoolFixture b:
                    {
                        int at = Obj("hkbBoolCondition", 32);
                        d.Array(at + 8, b.True.Length, 4, 4, (p, i) => d.I32(p, b.True[i]));
                        d.Array(at + 20, b.False.Length, 4, 4, (p, i) => d.I32(p, b.False[i]));
                        return at;
                    }
                    case NegateFixture n: { int at = Obj("hkbNegateCondition", 16); d.Pointer(at + 8, Condition(n.Condition)); return at; }
                    case MultiFixture m:
                    {
                        int at = Obj("hkbMultiCondition", 32);
                        d.I32(at + 8, m.Kind);
                        var parts = m.Conditions.Select(Condition).ToList();
                        d.Array(at + 12, parts.Count, 4, 4, (p, i) => d.Pointer(p, parts[i]));
                        return at;
                    }
                    default: throw new ArgumentException($"Unknown condition {c.GetType().Name}.");
                }
            }
        }
        d.Array(root, graphObjects.Count, 12, 4, (at, i) =>
        {
            d.String(at, "hkbBehaviorGraph");
            d.String(at + 4, "hkbBehaviorGraph");
            d.Pointer(at + 8, graphObjects[i]);
        });
        return Assemble([.. classes], d);
    }

    /// <summary>A ragdoll shape for <see cref="BuildRagdoll"/> (body-local values, as the file stores them).</summary>
    public abstract record RagdollShapeFixture;
    public sealed record SphereFixture(float Radius) : RagdollShapeFixture;
    public sealed record CapsuleFixture(Vector3 A, Vector3 B, float Radius) : RagdollShapeFixture;
    public sealed record BoxFixture(Vector3 Half, float Radius) : RagdollShapeFixture;
    /// <param name="Planes">Unit normal xyz and offset w (inside: n·p + w ≤ 0), body-local.</param>
    public sealed record HullFixture(Vector3 Half, Vector3 Centre, float Radius, Vector3[] Vertices, Vector4[] Planes) : RagdollShapeFixture;
    public sealed record TranslateFixture(Vector3 Translation, RagdollShapeFixture Child) : RagdollShapeFixture;
    public sealed record ListFixture(RagdollShapeFixture[] Children) : RagdollShapeFixture;
    public sealed record MoppFixture(RagdollShapeFixture Child) : RagdollShapeFixture;
    /// <summary>A shape object of another class (a reader skips it).</summary>
    public sealed record OtherShapeFixture(string ClassName) : RagdollShapeFixture;
    /// <param name="Columns">The rest rotation's three column vectors (null: from <paramref name="Rotation"/>), to build broken files.</param>
    /// <param name="Physics">Mass, inertia and material (null: left zero, which <see cref="HavokRagdoll.ReadPhysics"/> refuses).</param>
    /// <param name="Parent">The parent body (its ragdoll skeleton bone's parent), -1 for the root.</param>
    public sealed record RagdollBodyFixture(string Name, Vector3 Translation, Quaternion Rotation, RagdollShapeFixture Shape, Vector3[]? Columns = null,
        RagdollPhysicsFixture? Physics = null, short Parent = -1);

    /// <summary>A body's physics as the file stores it (the reader inverts inertia and mass; damping and gravity are half floats).</summary>
    public sealed record RagdollPhysicsFixture(float Mass, Vector3 Inertia, Vector3 CenterOfMass, float Friction = 0.8f, float Restitution = 0.8f,
        float LinearDamping = 0f, float AngularDamping = 0.05f, float GravityFactor = 1f, uint FilterInfo = 0, byte Motion = 3);

    /// <param name="Hinge">An <c>hkpLimitedHingeConstraintData</c> (else <c>hkpRagdollConstraintData</c>).</param>
    /// <param name="ClassName">Another constraint data class (a reader skips it).</param>
    /// <param name="BadAtom">Write this atom type in place of the transforms atom's (a reader refuses it).</param>
    public sealed record RagdollJointFixture(string Name, int BodyA, int BodyB, Vector3 PivotA, Quaternion FrameA, Vector3 PivotB, Quaternion FrameB,
        float Min, float Max, float Cone = 0f, float PlaneMin = 0f, float PlaneMax = 0f, float Friction = 20f, float Tau = 0.8f, bool Hinge = false,
        string? ClassName = null, short? BadAtom = null);

    /// <summary>The ragdoll-to-animation mapper: the animation skeleton, simple mappings (ragdoll bone, animation bone, transform), chains and unmapped animation bones.</summary>
    public sealed record RagdollMapperFixture(Bone[] AnimationBones, (int Ragdoll, int Animation, Vector3 Translation, Quaternion Rotation)[] Mappings,
        (int RagdollStart, int RagdollEnd, int AnimationStart, int AnimationEnd)[]? Chains = null, int[]? Unmapped = null);

    /// <summary>
    /// A ragdoll as documented for <see cref="HavokRagdoll"/>: an <c>hkaRagdollInstance</c> whose rigid bodies (@8) carry a
    /// shape (@16), a name (@120), a rest transform (rotation columns @240/@256/@272, translation @288) and physics, with shape
    /// objects at the documented offsets; its skeleton (@44: one bone per body, parents from the fixtures, reference pose from
    /// the rest transforms) and bone-to-body map (@32, identity); optional constraints (@20) and a skeleton mapper from the
    /// ragdoll skeleton onto an animation skeleton.
    /// </summary>
    /// <param name="boneToBody">The bone-to-body map (null: identity), to build broken files.</param>
    public static byte[] BuildRagdoll(IReadOnlyList<RagdollBodyFixture> bodies, IReadOnlyList<RagdollJointFixture>? joints = null, RagdollMapperFixture? mapper = null,
        int[]? boneToBody = null)
    {
        var d = new DataBuilder();
        var classes = new List<string> { "hkRootLevelContainer", "hkaRagdollInstance", "hkpRigidBody", "hkpSphereShape", "hkpCapsuleShape", "hkpBoxShape",
            "hkpConvexVerticesShape", "hkpConvexTranslateShape", "hkpListShape", "hkpMoppBvTreeShape", "hkaSkeleton", "hkpConstraintInstance",
            "hkpRagdollConstraintData", "hkpLimitedHingeConstraintData", "hkaSkeletonMapper" };
        foreach (var c in bodies.SelectMany(b => Others(b.Shape)).Concat((joints ?? []).Select(j => j.ClassName).OfType<string>()).Distinct())
            if (!classes.Contains(c)) classes.Add(c);
        d.Object("hkRootLevelContainer", 12);
        int instance = d.Object("hkaRagdollInstance", 48);
        var bodyAt = new int[bodies.Count];
        for (int i = 0; i < bodies.Count; i++)
        {
            var b = bodies[i];
            int body = bodyAt[i] = d.Object("hkpRigidBody", 576);
            d.String(body + 120, b.Name);
            var columns = b.Columns ?? [Vector3.Transform(Vector3.UnitX, b.Rotation), Vector3.Transform(Vector3.UnitY, b.Rotation), Vector3.Transform(Vector3.UnitZ, b.Rotation)];
            for (int k = 0; k < 3; k++) Vec(body + 240 + 16 * k, columns[k]);
            Vec(body + 288, b.Translation); d.F32(body + 300, 1f);
            d.Pointer(body + 16, Shape(b.Shape));
            if (b.Physics is { } p)
            {
                d.I32(body + 44, unchecked((int)p.FilterInfo));
                d.U8(body + 140, 1); d.F32(body + 144, p.Friction); d.F32(body + 148, p.Restitution);
                d.U8(body + 232, p.Motion);
                Vec(body + 368, p.CenterOfMass);
                Half(body + 404, p.LinearDamping); Half(body + 406, p.AngularDamping); Half(body + 510, p.GravityFactor);
                Vec(body + 416, new Vector3(1f / p.Inertia.X, 1f / p.Inertia.Y, 1f / p.Inertia.Z)); d.F32(body + 428, 1f / p.Mass);
            }
        }
        d.Array(instance + 8, bodies.Count, 4, 4, (at, i) => d.Pointer(at, bodyAt[i]));
        d.Array(instance + 32, boneToBody?.Length ?? bodies.Count, 4, 4, (at, i) => d.I32(at, boneToBody?[i] ?? i));
        int ragdollSkeleton = Skeleton(bodies.Select(b =>
        {
            if (b.Parent < 0) return new Bone(b.Name, -1, b.Translation, b.Rotation);
            var parent = bodies[b.Parent];
            var inverse = Quaternion.Conjugate(parent.Rotation);
            return new Bone(b.Name, b.Parent, Vector3.Transform(b.Translation - parent.Translation, inverse), Quaternion.Normalize(inverse * b.Rotation));
        }).ToArray());
        d.Pointer(instance + 44, ragdollSkeleton);

        var jointList = joints ?? [];
        var jointAt = new int[jointList.Count];
        for (int j = 0; j < jointList.Count; j++)
        {
            var f = jointList[j];
            int c = jointAt[j] = d.Object("hkpConstraintInstance", 96);
            d.Pointer(c + 20, bodyAt[f.BodyA]); d.Pointer(c + 24, bodyAt[f.BodyB]);
            d.U8(c + 28, 1); d.U8(c + 29, 1);
            d.String(c + 40, f.Name);
            int data;
            if (f.ClassName is { } other) data = d.Object(other, 64);
            else if (f.Hinge)
            {
                data = d.Object("hkpLimitedHingeConstraintData", 256);
                Atoms(data, (16, 2), (160, 23), (176, 18), (196, 17), (208, 14), (224, 12), (228, 5));
                d.U8(data + 198, 1); d.I32(data + 200, 1); d.F32(data + 204, f.Friction);
                d.U8(data + 210, 1); d.F32(data + 212, f.Min); d.F32(data + 216, f.Max); d.F32(data + 220, f.Tau);
            }
            else
            {
                data = d.Object("hkpRagdollConstraintData", 352);
                Atoms(data, (16, 2), (160, 23), (176, 19), (256, 17), (268, 15), (288, 16), (308, 16), (328, 5));
                d.U8(data + 258, 1); d.I32(data + 260, 3); d.F32(data + 264, f.Friction);
                d.U8(data + 270, 1); d.U8(data + 271, 0); d.U8(data + 272, 1); d.F32(data + 276, f.Min); d.F32(data + 280, f.Max); d.F32(data + 284, f.Tau);
                d.U8(data + 290, 1); d.F32(data + 296, -100f); d.F32(data + 300, f.Cone); d.F32(data + 304, f.Tau);
                d.U8(data + 310, 1); d.U8(data + 312, 1); d.U8(data + 313, 1); d.F32(data + 316, f.PlaneMin); d.F32(data + 320, f.PlaneMax); d.F32(data + 324, f.Tau);
            }
            if (f.ClassName is null)
            {
                Frame(data + 32, f.FrameA); Vec(data + 80, f.PivotA);
                Frame(data + 96, f.FrameB); Vec(data + 144, f.PivotB);
                if (f.BadAtom is { } bad) d.I16(data + 16, bad);
            }
            d.Pointer(c + 12, data);
        }
        d.Array(instance + 20, jointList.Count, 4, 4, (at, i) => d.Pointer(at, jointAt[i]));

        if (mapper is not null)
        {
            int animation = Skeleton(mapper.AnimationBones);
            int m = d.Object("hkaSkeletonMapper", 128);
            d.Pointer(m + 16, ragdollSkeleton); d.Pointer(m + 20, animation);
            d.Array(m + 24, mapper.Mappings.Length, 64, 16, (at, i) =>
            {
                var (r, a, t, q) = mapper.Mappings[i];
                d.I16(at, (short)r); d.I16(at + 2, (short)a); d.Transform(at + 16, t, q);
            });
            var chains = mapper.Chains ?? [];
            d.Array(m + 36, chains.Length, 112, 16, (at, i) =>
            {
                var (rs, re, s, e) = chains[i];
                d.I16(at, (short)rs); d.I16(at + 2, (short)re); d.I16(at + 4, (short)s); d.I16(at + 6, (short)e);
                d.Transform(at + 16, Vector3.Zero, Quaternion.Identity); d.Transform(at + 64, Vector3.Zero, Quaternion.Identity);
            });
            var unmapped = mapper.Unmapped ?? [];
            d.Array(m + 48, unmapped.Length, 2, 2, (at, i) => d.I16(at, (short)unmapped[i]));
            d.Transform(m + 64, Vector3.Zero, Quaternion.Identity);
            d.U8(m + 112, 1);
        }
        return Assemble(classes.ToArray(), d);

        int Skeleton(Bone[] bones)
        {
            int skeleton = d.Object("hkaSkeleton", 84);
            d.String(skeleton + 8, bones[0].Name);
            d.Array(skeleton + 12, bones.Length, 2, 2, (at, i) => d.I16(at, bones[i].Parent));
            d.Array(skeleton + 24, bones.Length, 8, 4, (at, i) => d.String(at, bones[i].Name));
            d.Array(skeleton + 36, bones.Length, 48, 16, (at, i) => d.Transform(at, bones[i].Translation, bones[i].Rotation));
            return skeleton;
        }
        void Atoms(int data, params (int At, short Type)[] atoms) { foreach (var (at, type) in atoms) d.I16(data + at, type); }
        void Frame(int at, Quaternion q)
        {
            Vec(at, Vector3.Transform(Vector3.UnitX, q)); Vec(at + 16, Vector3.Transform(Vector3.UnitY, q)); Vec(at + 32, Vector3.Transform(Vector3.UnitZ, q));
        }
        void Half(int at, float v) => d.I16(at, (short)(BitConverter.SingleToInt32Bits(v) >> 16));
        void Vec(int at, Vector3 v) { d.F32(at, v.X); d.F32(at + 4, v.Y); d.F32(at + 8, v.Z); }
        int Shape(RagdollShapeFixture f)
        {
            switch (f)
            {
                case SphereFixture s: { int o = d.Object("hkpSphereShape", 32); d.F32(o + 16, s.Radius); return o; }
                case CapsuleFixture c: { int o = d.Object("hkpCapsuleShape", 64); d.F32(o + 16, c.Radius); Vec(o + 32, c.A); Vec(o + 48, c.B); return o; }
                case BoxFixture b: { int o = d.Object("hkpBoxShape", 48); d.F32(o + 16, b.Radius); Vec(o + 32, b.Half); return o; }
                case HullFixture h:
                {
                    int o = d.Object("hkpConvexVerticesShape", 112);
                    d.F32(o + 16, h.Radius); Vec(o + 32, h.Half); Vec(o + 48, h.Centre);
                    int blocks = (h.Vertices.Length + 3) / 4;
                    d.Array(o + 64, blocks, 48, 16, (at, k) =>
                    {
                        for (int j = 0; j < 4; j++)
                        {
                            var v = h.Vertices[Math.Min(4 * k + j, h.Vertices.Length - 1)];   // the last block repeats its last point
                            d.F32(at + 4 * j, v.X); d.F32(at + 16 + 4 * j, v.Y); d.F32(at + 32 + 4 * j, v.Z);
                        }
                    });
                    d.I32(o + 76, h.Vertices.Length);
                    d.Array(o + 88, h.Planes.Length, 16, 16, (at, k) => { var p = h.Planes[k]; d.F32(at, p.X); d.F32(at + 4, p.Y); d.F32(at + 8, p.Z); d.F32(at + 12, p.W); });
                    return o;
                }
                case TranslateFixture t: { int o = d.Object("hkpConvexTranslateShape", 48); int child = Shape(t.Child); d.Pointer(o + 24, child); Vec(o + 32, t.Translation); return o; }
                case ListFixture l:
                {
                    int o = d.Object("hkpListShape", 96);
                    var children = l.Children.Select(Shape).ToArray();
                    d.Array(o + 24, children.Length, 16, 16, (at, k) => d.Pointer(at, children[k]));
                    return o;
                }
                case MoppFixture m: { int o = d.Object("hkpMoppBvTreeShape", 64); int child = Shape(m.Child); d.Pointer(o + 52, child); return o; }
                case OtherShapeFixture x: return d.Object(x.ClassName, 32);
                default: throw new ArgumentException($"Unknown fixture {f.GetType().Name}.");
            }
        }
        static IEnumerable<string> Others(RagdollShapeFixture f) => f switch
        {
            OtherShapeFixture x => [x.ClassName],
            TranslateFixture t => Others(t.Child),
            MoppFixture m => Others(m.Child),
            ListFixture l => l.Children.SelectMany(Others),
            _ => [],
        };
    }

    /// <summary>Inverse of the THREECOMP40 decoding: drop the largest component, keep its sign in bit 38.</summary>
    public static byte[] EncodeThreeComp40(Quaternion q)
    {
        q = Quaternion.Normalize(q);
        float[] c = [q.X, q.Y, q.Z, q.W];
        int dropped = 0;
        for (int i = 1; i < 4; i++) if (MathF.Abs(c[i]) > MathF.Abs(c[dropped])) dropped = i;
        const float fractal = 0.000345436f;
        ulong v = 0;
        int k = 0;
        for (int i = 0; i < 4; i++)
        {
            if (i == dropped) continue;
            ulong q12 = (ulong)Math.Clamp((int)MathF.Round(c[i] / fractal) + 2047, 0, 4095);
            v |= q12 << (12 * k++);
        }
        v |= (ulong)dropped << 36;
        if (c[dropped] < 0) v |= 1UL << 38;
        return [(byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24), (byte)(v >> 32)];
    }

    static void Align(List<byte> b, int a) { while (b.Count % a != 0) b.Add(0); }

    static byte[] Assemble(string[] classes, DataBuilder d)
    {
        // __classnames__: u32 signature, 0x09, name, NUL; padded with 0xFF.
        var names = new List<byte>();
        var nameOffsets = new Dictionary<string, int>();
        foreach (var c in classes)
        {
            names.AddRange(BitConverter.GetBytes(0x12345678)); names.Add(0x09);
            nameOffsets[c] = names.Count;
            names.AddRange(Encoding.ASCII.GetBytes(c)); names.Add(0);
        }
        while (names.Count % 16 != 0) names.Add(0xFF);

        var data = d.Bytes;
        int local = data.Count;
        foreach (var (src, dst) in d.Pointers) { data.AddRange(BitConverter.GetBytes(src)); data.AddRange(BitConverter.GetBytes(dst)); }
        while (data.Count % 16 != 0) data.Add(0xFF);
        int global = data.Count, virt = data.Count;
        foreach (var (offset, cls) in d.Objects) { data.AddRange(BitConverter.GetBytes(offset)); data.AddRange(BitConverter.GetBytes(0)); data.AddRange(BitConverter.GetBytes(nameOffsets[cls])); }
        while (data.Count % 16 != 0) data.Add(0xFF);
        int end = data.Count;

        var file = new List<byte>();
        file.AddRange([0x57, 0xE0, 0xE0, 0x57, 0x10, 0xC0, 0xC0, 0x10]);
        file.AddRange(BitConverter.GetBytes(0)); file.AddRange(BitConverter.GetBytes(8));
        file.AddRange([4, 1, 0, 1]);
        file.AddRange(BitConverter.GetBytes(3)); file.AddRange(BitConverter.GetBytes(2)); file.AddRange(BitConverter.GetBytes(0));
        file.AddRange(BitConverter.GetBytes(0)); file.AddRange(BitConverter.GetBytes(nameOffsets["hkRootLevelContainer"]));
        var version = new byte[16]; Encoding.ASCII.GetBytes("hk_2010.2.0-r1").CopyTo(version, 0); version[15] = 0xFF;
        file.AddRange(version);
        file.AddRange(BitConverter.GetBytes(0)); file.AddRange(BitConverter.GetBytes(-1));

        int namesAt = 0x40 + 3 * 0x30, dataAt = namesAt + names.Count;
        void Section(string tag, int abs, int l, int g, int v, int e)
        {
            var t = new byte[20]; Encoding.ASCII.GetBytes(tag).CopyTo(t, 0); t[19] = 0xFF;
            file.AddRange(t); file.AddRange(BitConverter.GetBytes(abs));
            foreach (var x in new[] { l, g, v, e, e, e }) file.AddRange(BitConverter.GetBytes(x));
        }
        Section("__classnames__", namesAt, names.Count, names.Count, names.Count, names.Count);
        Section("__types__", dataAt, 0, 0, 0, 0);
        Section("__data__", dataAt, local, global, virt, end);
        file.AddRange(names);
        file.AddRange(data);
        return file.ToArray();
    }

    /// Lays out objects and their out-of-line data in one byte list, remembering pointer fields and object classes.
    sealed class DataBuilder
    {
        public List<byte> Bytes { get; } = new();
        public List<(int Src, int Dst)> Pointers { get; } = new();
        public List<(int Offset, string Class)> Objects { get; } = new();

        int Allocate(int size, int align)
        {
            while (Bytes.Count % align != 0) Bytes.Add(0);
            int at = Bytes.Count;
            Bytes.AddRange(new byte[size]);
            return at;
        }
        public int Object(string cls, int size) { int at = Allocate(size, 16); Objects.Add((at, cls)); return at; }
        public void Pointer(int field, int target) => Pointers.Add((field, target));
        public void String(int field, string s)
        {
            int at = Allocate(s.Length + 1, 1);
            for (int i = 0; i < s.Length; i++) Bytes[at + i] = (byte)s[i];
            Pointer(field, at);
        }
        /// hkArray&lt;char&gt; as in the combiner TOC: pointer, size including the NUL, capacity flags.
        public void CharArray(int field, string s)
        {
            String(field, s);
            I32(field + 4, s.Length + 1);
            I32(field + 8, unchecked((int)0x80000000) | (s.Length + 1));
        }
        public void Array(int field, int count, int stride, int align, Action<int, int> write)
        {
            I32(field + 4, count);
            I32(field + 8, unchecked((int)0x80000000) | count);
            if (count == 0) return;
            int at = Allocate(count * stride, align);
            Pointer(field, at);
            for (int i = 0; i < count; i++) write(at + i * stride, i);
        }
        public void I32(int at, int v) { var b = BitConverter.GetBytes(v); for (int i = 0; i < 4; i++) Bytes[at + i] = b[i]; }
        public void F32(int at, float v) { var b = BitConverter.GetBytes(v); for (int i = 0; i < 4; i++) Bytes[at + i] = b[i]; }
        public void I16(int at, short v) { var b = BitConverter.GetBytes(v); Bytes[at] = b[0]; Bytes[at + 1] = b[1]; }
        public void U8(int at, byte v) => Bytes[at] = v;
        public void Transform(int at, Vector3 t, Quaternion r)
        {
            F32(at, t.X); F32(at + 4, t.Y); F32(at + 8, t.Z);
            F32(at + 16, r.X); F32(at + 20, r.Y); F32(at + 24, r.Z); F32(at + 28, r.W);
            F32(at + 32, 1); F32(at + 36, 1); F32(at + 40, 1);
        }
    }
}
