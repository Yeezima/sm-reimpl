using System;
using System.Collections.Generic;
using Godot;

namespace SmClient;

/// <summary>
/// Chunks of the ground thrown up and outward by a ground pound (M41). Each is a <see cref="RigidBody3D"/> with a small convex stone-like chunk (a jittered box, four shapes) that
/// flies out of the impact, falls and bounces on the level (the static world the ragdolls and the dropped magazines use, <see cref="Ragdoll.WorldLayer"/>), then shrinks away.
/// **Ours**, all of it: the install's effects package has no debris mesh (its mesh elements are a shell casing and an alarm light; the effect's own debris is sprites), so the chunks
/// are procedural, tinted by the surface the pound hit. <see cref="MinCount"/> to <see cref="MaxCount"/> per impact (M44, the owner's request for a much heavier impact: 20 to 35, a few of them
/// big flat slabs, thrown faster and spinning), at most <see cref="MaxLive"/> at once (the oldest go first). They live on render layer 2 with the characters, so decals do not paint them
/// (<c>NetworkView.SetUpDecalLayers</c>). After <see cref="LifetimeSeconds"/> a chunk stops and sinks into the ground while it shrinks (<see cref="ShrinkSeconds"/>).
/// </summary>
public sealed partial class GroundShards : Node3D
{
    public const int MinCount = 28, MaxCount = 45, MaxLive = 120;   // feel/jump-pack (the owner: "intensify the effect"): 20 to 35, 90 live in M44
    /// <summary>Seconds a chunk lies about before it sinks and shrinks away (over <see cref="ShrinkSeconds"/>, <see cref="SinkDepth"/> m down).</summary>
    public const float LifetimeSeconds = 4f, ShrinkSeconds = 1.2f, SinkDepth = 0.35f;
    /// <summary>Half-sizes of a chunk (m), launch speed along the surface and away from it (m/s), spin (rad/s), and where around the centre they start (m).</summary>
    public const float MinSize = 0.06f, MaxSize = 0.22f, MinOut = 3.5f, MaxOut = 10f, MinUp = 5f, MaxUp = 13f, MaxSpin = 12f, MinRadius = 0.3f, MaxRadius = 2.0f;
    /// <summary>A slab: this share of the chunks is big and flat (half-size <see cref="MinSlab"/> to <see cref="MaxSlab"/> m, a third as thick), thrown at <see cref="SlabSpeed"/> of the speed.</summary>
    public const float SlabShare = 0.15f, MinSlab = 0.3f, MaxSlab = 0.5f, SlabThickness = 0.35f, SlabSpeed = 0.6f;
    /// <summary>Render layer of the chunks: layer 2 with the characters, so that decals (layer 1 only) do not paint them.</summary>
    public const uint CharacterRenderLayer = 2;
    public const float Mass = 1.5f, Friction = 0.8f, Bounce = 0.28f;

    /// <summary>One physics material for every chunk.</summary>
    static readonly PhysicsMaterial Physics = new() { Friction = Friction, Bounce = Bounce };
    readonly List<(RigidBody3D Body, MeshInstance3D Mesh, float Age, Vector3 Size)> _live = new();
    static ArrayMesh[]? _meshes;
    static Vector3[][]? _points;
    readonly Dictionary<string, StandardMaterial3D[]> _materials = new();
    int _seed = 31;
    public int Live => _live.Count;
    public int Spawned { get; private set; }
    public int Removed { get; private set; }

    /// <summary>Throws a handful of chunks from the ground at <paramref name="point"/> (Godot space) whose surface faces <paramref name="normal"/>; <paramref name="surface"/> picks the tint.</summary>
    /// <param name="counts">feel/jump-pack: another count range (the sword slash's smaller burst), else <see cref="MinCount"/> to <see cref="MaxCount"/>.</param>
    /// <param name="speedScale">feel/jump-pack: scales every launch speed (the slash's chunks fly lower).</param>
    /// <param name="forward">feel/jump-pack: a horizontal direction (Godot space) the chunks are thrown along within ±70 degrees (the slash's cone), else all around.</param>
    public int Spawn(Vector3 point, Vector3 normal, string surface, int seed, (int Min, int Max)? counts = null, float speedScale = 1f, Vector3? forward = null)
    {
        EnsureShapes();
        var rng = new Random(seed * 7919 + _seed++);
        var (min, max) = counts ?? (MinCount, MaxCount);
        int count = min + rng.Next(Math.Max(0, max - min) + 1);
        var up = normal.LengthSquared() < 1e-6f ? Vector3.Up : normal.Normalized();
        var t1 = (MathF.Abs(up.Y) > 0.99f ? Vector3.Right : Vector3.Up).Cross(up).Normalized();
        var t2 = up.Cross(t1);
        var tints = Tints(surface);
        float R() => (float)rng.NextDouble();
        for (int i = 0; i < count; i++)
        {
            while (_live.Count >= MaxLive) Remove(0);
            bool slab = R() < SlabShare;
            float size = slab ? Mathf.Lerp(MinSlab, MaxSlab, R()) : Mathf.Lerp(MinSize, MaxSize, R() * R());
            var extent = slab ? new Vector3(size, size * SlabThickness, size) : Vector3.One * size;
            float speed = (slab ? SlabSpeed : 1f) * speedScale;
            int variant = rng.Next(_meshes!.Length);
            float angle = (i + R() * 0.8f) / count * MathF.Tau;
            var around = t1 * MathF.Cos(angle) + t2 * MathF.Sin(angle);
            if (forward is { } f && (f - up * f.Dot(up)) is var flat && flat.LengthSquared() > 1e-6f)
            {
                // The slash's cone: spread within ±70 degrees of the swing's heading.
                var ahead = flat.Normalized();
                float turn = Mathf.DegToRad(((i + R() * 0.8f) / Math.Max(1, count) - 0.5f) * 140f);
                around = (ahead * MathF.Cos(turn) + up.Cross(ahead) * MathF.Sin(turn)).Normalized();
            }
            var body = new RigidBody3D
            {
                Name = "Shard" + Spawned, Mass = Mass * (extent.X * extent.Y * extent.Z / (MaxSize * MaxSize * MaxSize)), CollisionLayer = 0, CollisionMask = Ragdoll.WorldLayer, ContinuousCd = true, CanSleep = true,
                LinearDamp = 0.05f, AngularDamp = 0.5f, PhysicsMaterialOverride = Physics,
            };
            var mesh = new MeshInstance3D { Name = "Mesh", Mesh = _meshes[variant], Scale = extent, MaterialOverride = tints[rng.Next(tints.Length)], CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Layers = CharacterRenderLayer };
            body.AddChild(mesh);
            var points = new Vector3[_points![variant].Length];
            for (int k = 0; k < points.Length; k++) points[k] = _points[variant][k] * extent;
            body.AddChild(new CollisionShape3D { Name = "Hull", Shape = new ConvexPolygonShape3D { Points = points } });
            AddChild(body);
            float radius = Mathf.Lerp(MinRadius, MaxRadius, R());
            body.GlobalTransform = new Transform3D(new Basis(new Vector3(R() - 0.5f, R() - 0.5f, R() - 0.5f).Normalized(), R() * MathF.Tau), point + up * (extent.Y + 0.08f) + around * radius);
            body.LinearVelocity = (around * Mathf.Lerp(MinOut, MaxOut, R()) + up * Mathf.Lerp(MinUp, MaxUp, R())) * speed;
            body.AngularVelocity = new Vector3(R() * 2f - 1f, R() * 2f - 1f, R() * 2f - 1f) * MaxSpin * speed;
            _live.Add((body, mesh, 0f, extent));
            Spawned++;
        }
        return count;
    }

    void Remove(int index)
    {
        var body = _live[index].Body;
        _live.RemoveAt(index);
        Removed++;
        if (IsInstanceValid(body)) { RemoveChild(body); body.QueueFree(); }
    }

    public override void _Process(double delta)
    {
        for (int i = _live.Count - 1; i >= 0; i--)
        {
            var (body, mesh, age, size) = _live[i];
            age += (float)delta;
            _live[i] = (body, mesh, age, size);
            if (!IsInstanceValid(body) || age >= LifetimeSeconds + ShrinkSeconds || body.GlobalPosition.Y < Ragdoll.LostBelowY) { Remove(i); continue; }
            if (age > LifetimeSeconds && IsInstanceValid(mesh))
            {
                // Settled: stop the body and sink it while it shrinks (M44: the chunks stay a few seconds, then go into the ground).
                float k = Math.Clamp((age - LifetimeSeconds) / ShrinkSeconds, 0f, 1f);
                if (!body.Freeze) { body.Freeze = true; body.FreezeMode = RigidBody3D.FreezeModeEnum.Kinematic; }
                mesh.Scale = size * (1f - 0.7f * k);
                body.GlobalPosition += Vector3.Down * (SinkDepth / ShrinkSeconds * (float)delta);
                if (k >= 1f) { Remove(i); continue; }
            }
        }
    }

    /// <summary>Removes every chunk (the view is torn down).</summary>
    public void Clear() { while (_live.Count > 0) Remove(_live.Count - 1); }

    // ---- the chunk shapes ----

    /// <summary>Four jittered boxes (a different non-uniform squash and corner jitter each), as meshes of flat-shaded triangles and as the hull points of their corners (unit size).</summary>
    static void EnsureShapes()
    {
        if (_meshes is not null) return;
        var rng = new Random(4242);
        _meshes = new ArrayMesh[4]; _points = new Vector3[4][];
        int[][] quads = [[0, 1, 3, 2], [4, 6, 7, 5], [0, 4, 5, 1], [2, 3, 7, 6], [0, 2, 6, 4], [1, 5, 7, 3]];   // corner bits: x = 1, y = 2, z = 4 (outward windings checked by GenerateNormals + CullDisabled)
        for (int m = 0; m < 4; m++)
        {
            var corners = new Vector3[8];
            var squash = new Vector3(0.7f + (float)rng.NextDouble() * 0.6f, 0.5f + (float)rng.NextDouble() * 0.6f, 0.7f + (float)rng.NextDouble() * 0.6f);
            for (int c = 0; c < 8; c++)
            {
                var corner = new Vector3((c & 1) != 0 ? 1f : -1f, (c & 2) != 0 ? 1f : -1f, (c & 4) != 0 ? 1f : -1f);
                var jitter = new Vector3((float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f) * 0.55f;
                corners[c] = (corner + jitter) * squash;
            }
            var st = new SurfaceTool();
            st.Begin(Mesh.PrimitiveType.Triangles);
            foreach (var q in quads)
            {
                // Two triangles per face; wound so that the normal faces away from the centre.
                Vector3 a = corners[q[0]], b = corners[q[1]], c = corners[q[2]], d = corners[q[3]];
                var centre = (a + b + c + d) * 0.25f;
                var normal = (b - a).Cross(c - a);
                bool flip = normal.Dot(centre) < 0f;
                foreach (var tri in new[] { new[] { a, b, c }, new[] { a, c, d } })
                {
                    var wound = flip ? new[] { tri[0], tri[2], tri[1] } : tri;
                    var n = (wound[1] - wound[0]).Cross(wound[2] - wound[0]).Normalized();
                    foreach (var v in wound) { st.SetNormal(n); st.AddVertex(v); }
                }
            }
            _meshes[m] = st.Commit();
            _points[m] = corners;
        }
    }

    /// <summary>Greys, browns and tans by the surface the pound hit (ours: a few shades of one colour each; the level's own texture is not sampled).</summary>
    StandardMaterial3D[] Tints(string surface)
    {
        if (_materials.TryGetValue(surface, out var made)) return made;
        var (r, g, b) = surface switch
        {
            "dirt" => (0.30f, 0.23f, 0.16f),
            "gravel" => (0.36f, 0.33f, 0.28f),
            "sand" => (0.52f, 0.44f, 0.30f),
            "metal" => (0.27f, 0.28f, 0.30f),
            _ => (0.38f, 0.36f, 0.33f),   // stone and the default
        };
        var list = new StandardMaterial3D[3];
        for (int i = 0; i < list.Length; i++)
        {
            float k = 0.8f + 0.2f * i;
            list[i] = new StandardMaterial3D { AlbedoColor = new Color(r * k, g * k, b * k), Roughness = 0.9f, Metallic = surface == "metal" ? 0.4f : 0f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
        }
        return _materials[surface] = list;
    }
}
