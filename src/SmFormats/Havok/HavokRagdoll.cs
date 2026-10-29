using System.Numerics;

namespace SmFormats.Havok;

/// <summary>A collision primitive of a ragdoll body, in model space (the rig's frame at its rest pose).</summary>
public enum RagdollShapeKind : byte { Sphere, Capsule, Box, Hull }

/// <summary>
/// One primitive in model space. Sphere: <see cref="A"/> is the centre. Capsule: segment <see cref="A"/>–<see cref="B"/>.
/// Box: <see cref="A"/> is the centre, <see cref="B"/> the half extents along the box's axes (the convex radius added),
/// <see cref="Rotation"/> turns the box's axes into model space. Hull (<c>hkpConvexVerticesShape</c>): <see cref="Planes"/>
/// (unit normal xyz, offset w; inside where n·p + w ≤ 0; the file's planes already lie the convex radius outside the
/// vertices) and <see cref="Vertices"/> (the inner core points); <see cref="A"/> and <see cref="B"/> are the file's stored
/// bounding box (centre, half extents plus radius) in the body's frame turned by <see cref="Rotation"/>.
/// <see cref="Radius"/>: sphere and capsule radius, or the convex radius of a box or hull.
/// </summary>
public sealed record RagdollShape(RagdollShapeKind Kind, Vector3 A, Vector3 B, float Radius, Quaternion Rotation,
    IReadOnlyList<Vector4>? Planes = null, IReadOnlyList<Vector3>? Vertices = null);

/// <summary>A ragdoll rigid body: its name (e.g. <c>Ragdoll_RIG_Head</c>), rest transform and primitives (model space).</summary>
public sealed record RagdollBody(string Name, Vector3 Origin, Quaternion Rotation, IReadOnlyList<RagdollShape> Shapes);

/// <summary>
/// Reads a character packfile's ragdoll (<c>hkaRagdollInstance</c> and its <c>hkpRigidBody</c>s) into model-space primitives
/// at the bodies' rest transforms. Layout per docs/discovery.md ("Ragdoll and hit shapes"), established on the game's own
/// files (hk_2010.2.0-r1, 32-bit); no Havok SDK code. Field offsets: ragdoll instance rigid bodies @8 (pointer array);
/// rigid body shape @16, name @120, rest rotation as three column vectors @240/@256/@272 and translation @288; shapes: convex
/// radius @16; sphere; capsule vertices @32/@48; box half extents @32; convex-translate child @24 and translation @32;
/// convex vertices: bounding box half extents @32 and centre @48, vertices @64 (blocks of four transposed points, 48 bytes),
/// vertex count @76, planes @88 (16 bytes each); list children @24 (16-byte entries, shape first); MOPP tree child @52.
/// Unknown shape classes are skipped with a note.
/// </summary>
public static partial class HavokRagdoll
{
    const int MaxDepth = 8, MaxBodies = 256, MaxPlanes = 512, MaxVertices = 4096;
    /// <summary>Most shape objects visited in one ragdoll (lists fan out; the marine has 32).</summary>
    public const int MaxShapes = 256;

    /// <summary>The ragdoll's bodies (empty when the packfile has no <c>hkaRagdollInstance</c>).</summary>
    public static IReadOnlyList<RagdollBody> Read(HavokPackfile pack, List<string>? notes = null)
    {
        notes ??= [];
        var instance = pack.OfClass("hkaRagdollInstance").FirstOrDefault();
        if (instance is null) return [];
        var classes = pack.Objects.ToDictionary(o => o.Offset, o => o.ClassName);
        var (bodiesAt, count) = pack.Array(instance.Offset + 8, 4);
        if (count > MaxBodies) throw new InvalidDataException($"Implausible ragdoll body count {count}.");
        var bodies = new List<RagdollBody>(count);
        int visited = 0;
        for (int b = 0; b < count; b++)
        {
            int body = pack.Pointer(bodiesAt!.Value + 4 * b) ?? throw new InvalidDataException($"Ragdoll body {b}: null pointer.");
            if (!classes.TryGetValue(body, out var cls) || cls != "hkpRigidBody") throw new InvalidDataException($"Ragdoll body {b} is {cls ?? "no object"}, not hkpRigidBody.");
            string name = pack.String(body + 120) ?? $"body{b}";
            var c0 = Vector(pack, body + 240); var c1 = Vector(pack, body + 256); var c2 = Vector(pack, body + 272); var t = Vector(pack, body + 288);
            var m = new Matrix4x4(c0.X, c0.Y, c0.Z, 0, c1.X, c1.Y, c1.Z, 0, c2.X, c2.Y, c2.Z, 0, 0, 0, 0, 1);   // rows = images of the local axes
            if (!Finite(c0) || !Finite(c1) || !Finite(c2) || !Finite(t) || MathF.Abs(m.GetDeterminant() - 1f) > 1e-3f
                || MathF.Abs(Vector3.Dot(c0, c1)) > 1e-3f || MathF.Abs(Vector3.Dot(c0, c2)) > 1e-3f || MathF.Abs(Vector3.Dot(c1, c2)) > 1e-3f)
                throw new InvalidDataException($"Ragdoll body '{name}': the rest transform is not a rotation and translation.");
            var rotation = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(m));
            var shapes = new List<RagdollShape>();
            int shape = pack.Pointer(body + 16) ?? throw new InvalidDataException($"Ragdoll body '{name}': no shape.");
            Collect(shape, Vector3.Zero, 0);
            bodies.Add(new RagdollBody(name, t, rotation, shapes));

            Vector3 World(Vector3 local) => Vector3.Transform(local, rotation) + t;
            void Collect(int s, Vector3 shift, int depth)
            {
                if (depth > MaxDepth) throw new InvalidDataException($"Ragdoll body '{name}': shapes nested deeper than {MaxDepth}.");
                if (++visited > MaxShapes) throw new InvalidDataException($"Ragdoll: more than {MaxShapes} shape objects.");
                string kind = classes.TryGetValue(s, out var k) ? k : "?";
                float radius = pack.ReadSingle(s + 16);
                switch (kind)
                {
                    case "hkpSphereShape":
                        shapes.Add(new RagdollShape(RagdollShapeKind.Sphere, World(shift), World(shift), Positive(radius), rotation));
                        break;
                    case "hkpCapsuleShape":
                        shapes.Add(new RagdollShape(RagdollShapeKind.Capsule, World(Vector(pack, s + 32) + shift), World(Vector(pack, s + 48) + shift), Positive(radius), rotation));
                        break;
                    case "hkpBoxShape":
                    {
                        var half = Vector(pack, s + 32);
                        CheckBox(half, radius);
                        shapes.Add(new RagdollShape(RagdollShapeKind.Box, World(shift), half + new Vector3(radius), radius, rotation));
                        break;
                    }
                    case "hkpConvexVerticesShape":
                        shapes.Add(Hull(s, shift, radius));
                        break;
                    case "hkpConvexTranslateShape":
                        Collect(pack.Pointer(s + 24) ?? throw new InvalidDataException($"Ragdoll body '{name}': translate shape without a child."), shift + Vector(pack, s + 32), depth + 1);
                        break;
                    case "hkpMoppBvTreeShape":
                        Collect(pack.Pointer(s + 52) ?? throw new InvalidDataException($"Ragdoll body '{name}': MOPP tree without a child."), shift, depth + 1);
                        break;
                    case "hkpListShape":
                    {
                        var (children, n) = pack.Array(s + 24, 16);
                        for (int i = 0; i < n; i++)
                            if (pack.Pointer(children!.Value + 16 * i) is { } child) Collect(child, shift, depth + 1);
                        break;
                    }
                    default:
                        notes.Add($"ragdoll body '{name}': shape {kind} not read");
                        break;
                }
            }

            RagdollShape Hull(int s, Vector3 shift, float radius)
            {
                var half = Vector(pack, s + 32);
                CheckBox(half, radius);
                int vertexCount = pack.ReadInt32(s + 76);
                var (blocksAt, blockCount) = pack.Array(s + 64, 48);
                var (planesAt, planeCount) = pack.Array(s + 88, 16);
                if (vertexCount is < 4 or > MaxVertices || blockCount * 4 < vertexCount || planeCount is < 4 or > MaxPlanes)
                    throw new InvalidDataException($"Ragdoll body '{name}': hull with {vertexCount} vertices in {blockCount} blocks and {planeCount} planes.");
                var vertices = new Vector3[vertexCount];
                for (int i = 0; i < vertexCount; i++)
                {
                    int block = blocksAt!.Value + 48 * (i / 4), j = 4 * (i % 4);
                    var v = World(new Vector3(pack.ReadSingle(block + j), pack.ReadSingle(block + 16 + j), pack.ReadSingle(block + 32 + j)) + shift);
                    vertices[i] = Finite(v) ? v : throw new InvalidDataException($"Ragdoll body '{name}': hull vertex {i} is not finite.");
                }
                var origin = World(shift);   // where the hull's local origin lands
                var planes = new Vector4[planeCount];
                for (int i = 0; i < planeCount; i++)
                {
                    int at = planesAt!.Value + 16 * i;
                    var n = Vector(pack, at); float w = pack.ReadSingle(at + 12);
                    if (!Finite(n) || !float.IsFinite(w) || MathF.Abs(n.Length() - 1f) > 1e-3f) throw new InvalidDataException($"Ragdoll body '{name}': hull plane {i} is not a unit plane.");
                    var nw = Vector3.Transform(n, rotation);
                    planes[i] = new Vector4(nw, w - Vector3.Dot(nw, origin));
                }
                return new RagdollShape(RagdollShapeKind.Hull, World(Vector(pack, s + 48) + shift), half + new Vector3(radius), radius, rotation, planes, vertices);
            }

            void CheckBox(Vector3 half, float radius)
            {
                if (!Finite(half) || half.X < 0 || half.Y < 0 || half.Z < 0 || !float.IsFinite(radius) || radius < 0)
                    throw new InvalidDataException($"Ragdoll body '{name}': implausible box or hull bounds.");
            }
            float Positive(float r) => float.IsFinite(r) && r > 0f ? r : throw new InvalidDataException($"Ragdoll body '{name}': radius {r}.");
        }
        return bodies;
    }

    static Vector3 Vector(HavokPackfile pack, int offset) => new(pack.ReadSingle(offset), pack.ReadSingle(offset + 4), pack.ReadSingle(offset + 8));
    static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
