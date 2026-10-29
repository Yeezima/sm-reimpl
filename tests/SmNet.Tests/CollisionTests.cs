using System.Diagnostics;
using System.Numerics;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>Level collision: the triangle mesh and its BVH, the capsule controller, shots against walls, spawn points. Synthetic geometry only.</summary>
public class CollisionTests(ITestOutputHelper output)
{
    static readonly SimConfig Config = new();
    static readonly float R = Config.CapsuleRadius;

    /// <summary>Collects triangles for a synthetic level.</summary>
    sealed class Builder
    {
        readonly List<Vector3> _vertices = new();
        readonly List<int> _indices = new();

        public Builder Triangle(Vector3 a, Vector3 b, Vector3 c)
        {
            int i = _vertices.Count;
            _vertices.AddRange([a, b, c]);
            _indices.AddRange([i, i + 1, i + 2]);
            return this;
        }

        /// <summary>Quad a-b-c-d (in order around its edge).</summary>
        public Builder Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) => Triangle(a, b, c).Triangle(a, c, d);

        public Builder Floor(float z = 0f, float half = 50f) =>
            Quad(new(-half, -half, z), new(half, -half, z), new(half, half, z), new(-half, half, z));

        /// <summary>A vertical wall in the plane y = <paramref name="y"/>, spanning x and z.</summary>
        public Builder WallY(float y, float x0 = -50f, float x1 = 50f, float z0 = -1f, float z1 = 4f) =>
            Quad(new(x0, y, z0), new(x1, y, z0), new(x1, y, z1), new(x0, y, z1));

        /// <summary>A vertical wall in the plane x = <paramref name="x"/>, spanning y and z.</summary>
        public Builder WallX(float x, float y0, float y1, float z0 = -1f, float z1 = 4f) =>
            Quad(new(x, y0, z0), new(x, y1, z0), new(x, y1, z1), new(x, y0, z1));

        /// <summary>A closed axis-aligned box (12 triangles).</summary>
        public Builder Box(Vector3 min, Vector3 max)
        {
            Vector3 P(int x, int y, int z) => new(x == 0 ? min.X : max.X, y == 0 ? min.Y : max.Y, z == 0 ? min.Z : max.Z);
            Quad(P(0, 0, 0), P(1, 0, 0), P(1, 1, 0), P(0, 1, 0));
            Quad(P(0, 0, 1), P(0, 1, 1), P(1, 1, 1), P(1, 0, 1));
            Quad(P(0, 0, 0), P(0, 0, 1), P(1, 0, 1), P(1, 0, 0));
            Quad(P(0, 1, 0), P(1, 1, 0), P(1, 1, 1), P(0, 1, 1));
            Quad(P(0, 0, 0), P(0, 1, 0), P(0, 1, 1), P(0, 0, 1));
            Quad(P(1, 0, 0), P(1, 0, 1), P(1, 1, 1), P(1, 1, 0));
            return this;
        }

        /// <summary>A ramp starting at y = <paramref name="y0"/> on the floor, rising at <paramref name="degrees"/> to <paramref name="height"/>, then a plateau.</summary>
        public Builder Ramp(float y0, float degrees, float height, float halfWidth = 5f, float plateau = 20f)
        {
            float y1 = y0 + height / MathF.Tan(degrees * MathF.PI / 180f);
            Quad(new(-halfWidth, y0, 0), new(halfWidth, y0, 0), new(halfWidth, y1, height), new(-halfWidth, y1, height));
            return Quad(new(-halfWidth, y1, height), new(halfWidth, y1, height), new(halfWidth, y1 + plateau, height), new(-halfWidth, y1 + plateau, height));
        }

        public (Vector3[] Vertices, int[] Indices) Buffers() => (_vertices.ToArray(), _indices.ToArray());
        public StaticMesh Mesh() => new(_vertices.ToArray(), _indices.ToArray());
        public CollisionWorld World() { var m = Mesh(); return new CollisionWorld(m, m); }
    }

    static PlayerState Standing(Vector3 at, bool grounded = true) => new(1, at, Vector3.Zero, 0f, grounded);
    static PlayerInput Walk(uint tick, float x, float y, InputButtons buttons = InputButtons.None, float yaw = 0f) => new(tick, x, y, yaw, buttons);

    static PlayerState Run(PlayerState s, CollisionWorld world, int ticks, Func<uint, PlayerInput> input, SimConfig? c = null, Action<PlayerState>? each = null)
    {
        for (uint t = 0; t < ticks; t++)
        {
            s = Movement.Step(s, input(t), c ?? Config, world);
            each?.Invoke(s);
        }
        return s;
    }

    // ---------------- mesh and raycasts ----------------

    [Fact]
    public void Raycast_finds_the_nearest_hit_from_either_side()
    {
        var mesh = new Builder().Floor(0f).Floor(2f).Mesh();
        Assert.Equal(4, mesh.TriangleCount);
        Assert.Equal(new Vector3(-50, -50, 0), mesh.Min);
        Assert.Equal(new Vector3(50, 50, 2), mesh.Max);

        var down = mesh.Raycast(new Vector3(1, 2, 5), -Vector3.UnitZ, 100f);
        Assert.NotNull(down);
        Assert.Equal(3f, down.Value.Distance, 5);
        Assert.Equal(new Vector3(1, 2, 2), down.Value.Point);
        Assert.Equal(Vector3.UnitZ, down.Value.Normal);
        Assert.InRange(down.Value.Triangle, 2, 3);   // the upper floor

        // From between the floors upward: the back face of the upper floor, normal facing the origin.
        var up = mesh.Raycast(new Vector3(1, 2, 1), Vector3.UnitZ, 100f);
        Assert.Equal(1f, up!.Value.Distance, 5);
        Assert.Equal(-Vector3.UnitZ, up.Value.Normal);
        Assert.InRange(up.Value.Triangle, 2, 3);

        Assert.Null(mesh.Raycast(new Vector3(1, 2, 5), Vector3.UnitZ, 100f));             // pointing away
        Assert.Null(mesh.Raycast(new Vector3(60, 0, 5), -Vector3.UnitZ, 100f));            // beside the mesh
        Assert.Null(mesh.Raycast(new Vector3(1, 2, 5), -Vector3.UnitZ, 2.99f));            // short of it
        Assert.NotNull(mesh.Raycast(new Vector3(1, 2, 5), -Vector3.UnitZ, 3f));            // max distance inclusive
        Assert.Null(mesh.Raycast(new Vector3(1, 2, 5), -Vector3.UnitZ, float.NaN));
        Assert.Null(new StaticMesh([], []).Raycast(Vector3.Zero, Vector3.UnitX, 10f));
    }

    [Fact]
    public void Construction_validates_indices_skips_degenerates_and_hashes_content()
    {
        Vector3[] v = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(2, 0, 0)];
        Assert.Throws<ArgumentException>(() => new StaticMesh(v, [0, 1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StaticMesh(v, [0, 1, 4]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StaticMesh(v, [0, -1, 2]));
        Assert.Throws<ArgumentNullException>(() => new StaticMesh(null!, []));

        // Second triangle is collinear (zero area), third repeats a vertex: both ignored; numbering keeps the index buffer's.
        var mesh = new StaticMesh(v, [0, 1, 3, 0, 1, 2, 2, 2, 1]);
        Assert.Equal(1, mesh.TriangleCount);
        Assert.Equal(1, mesh.Raycast(new Vector3(0.2f, 0.2f, 1), -Vector3.UnitZ, 5f)!.Value.Triangle);
        Assert.Equal(new Vector3(0, 0, 0), mesh.Min);
        Assert.Equal(new Vector3(1, 1, 0), mesh.Max);

        var same = new StaticMesh(v, [0, 1, 3, 0, 1, 2, 2, 2, 1]);
        Assert.Equal(mesh.ContentHash, same.ContentHash);
        Assert.NotEqual(mesh.ContentHash, new StaticMesh(v, [0, 2, 1]).ContentHash);
        Assert.NotEqual(mesh.ContentHash, new StaticMesh([.. v[..3], new(2, 0, 1e-6f)], [0, 1, 3, 0, 1, 2, 2, 2, 1]).ContentHash);

        var level = new CollisionWorld(mesh, same);
        Assert.Equal(level.ContentHash, new CollisionWorld(same, mesh).ContentHash);   // equal content, equal hash
        Assert.NotEqual(level.ContentHash, new CollisionWorld(mesh, new StaticMesh(v, [0, 2, 1])).ContentHash);
        Assert.NotEqual(new CollisionWorld(mesh, new StaticMesh(v, [0, 2, 1])).ContentHash, new CollisionWorld(new StaticMesh(v, [0, 2, 1]), mesh).ContentHash);
    }

    [Fact]
    public void Bvh_answers_match_brute_force_on_random_triangles()
    {
        var random = new Random(1234);
        float F(float lo, float hi) => lo + (float)random.NextDouble() * (hi - lo);
        var vertices = new Vector3[3 * 3000];
        for (int t = 0; t < 3000; t++)
        {
            var c = new Vector3(F(-100, 100), F(-100, 100), F(-20, 20));
            for (int k = 0; k < 3; k++) vertices[3 * t + k] = c + new Vector3(F(-4, 4), F(-4, 4), F(-4, 4));
        }
        var indices = Enumerable.Range(0, vertices.Length).ToArray();
        var mesh = new StaticMesh(vertices, indices);
        Assert.Equal(3000, mesh.TriangleCount);

        int hits = 0;
        for (int i = 0; i < 3000; i++)
        {
            var origin = new Vector3(F(-120, 120), F(-120, 120), F(-30, 30));
            var direction = Vector3.Normalize(new Vector3(F(-1, 1), F(-1, 1), F(-1, 1)));
            float max = F(1, 300);
            var fast = mesh.Raycast(origin, direction, max);
            var slow = BruteForce(vertices, origin, direction, max);
            Assert.Equal(slow.HasValue, fast.HasValue);
            if (fast is { } f && slow is { } s)
            {
                hits++;
                Assert.Equal(s.Triangle, f.Triangle);
                Assert.Equal(s.Distance, f.Distance);
                Assert.True(Vector3.Dot(f.Normal, direction) <= 0f, "normal faces the origin");
            }
        }
        output.WriteLine($"{hits} of 3000 random rays hit");
        Assert.InRange(hits, 300, 2990);   // a meaningful mix of hits and misses
    }

    /// <summary>Every triangle, same Möller–Trumbore arithmetic and tolerance as the mesh, nearest then lowest index.</summary>
    static (float Distance, int Triangle)? BruteForce(Vector3[] v, Vector3 o, Vector3 d, float max)
    {
        (float, int)? best = null;
        for (int t = 0; t < v.Length / 3; t++)
        {
            Vector3 a = v[3 * t], e1 = v[3 * t + 1] - a, e2 = v[3 * t + 2] - a;
            var p = Vector3.Cross(d, e2);
            float det = Vector3.Dot(e1, p);
            if (MathF.Abs(det) < 1e-12f) continue;
            float inv = 1f / det;
            var s = o - a;
            float u = Vector3.Dot(s, p) * inv;
            if (u < -1e-6f || u > 1f + 1e-6f) continue;
            var q = Vector3.Cross(s, e1);
            float w = Vector3.Dot(d, q) * inv;
            if (w < -1e-6f || u + w > 1f + 1e-6f) continue;
            float dist = Vector3.Dot(e2, q) * inv;
            if (dist < 0f || dist > max) continue;
            if (best is null || dist < best.Value.Item1) best = (dist, t);
        }
        return best;
    }

    // ---------------- the capsule controller ----------------

    [Fact]
    public void Walking_on_a_flat_floor_stays_on_it_and_matches_the_flat_ground_model()
    {
        var world = new Builder().Floor().World();
        var s = Standing(Vector3.Zero);
        var flat = s;
        for (uint t = 0; t < 90; t++)
        {
            var input = Walk(t, 0.3f, 1f, t > 30 ? InputButtons.Sprint : InputButtons.None, yaw: 0.01f * t);
            s = Movement.Step(s, input, Config, world);
            flat = Movement.Step(flat, input, Config);
            Assert.True(s.Grounded);
            Assert.InRange(s.Position.Z, 0f, 1e-3f);
            Assert.Equal(0f, s.Velocity.Z);
            // Same path as the flat-ground model (sub-stepping rounds differently, hence the tolerance).
            Assert.True(Vector2.Distance(new Vector2(flat.Position.X, flat.Position.Y), new Vector2(s.Position.X, s.Position.Y)) < 1e-4f);
            Assert.Equal(flat.Velocity, s.Velocity);
        }
        Assert.True(s.Position.Y > 10f);
    }

    [Fact]
    public void A_wall_blocks_and_moving_diagonally_into_it_slides_along_it()
    {
        var world = new Builder().Floor().WallY(3f).World();
        var positions = new List<Vector3>();
        var s = Run(Standing(Vector3.Zero), world, 60, t => Walk(t, 1f, 1f), each: p => positions.Add(p.Position));
        foreach (var p in positions) Assert.True(p.Y <= 3f - R + 1e-3f, $"y {p.Y} beyond the wall");
        Assert.InRange(s.Position.Y, 3f - R - 0.01f, 3f - R + 1e-3f);
        Assert.True(s.Grounded);
        // In the last second it only slides: X keeps the stick's sideways part (5 m/s × 0.707), Y does not change.
        float slide = positions[^1].X - positions[^31].X;
        Assert.InRange(slide, 0.95f * 5f * 0.7071f, 5f * 0.7071f + 1e-3f);
        Assert.InRange(s.Velocity.Y, -1e-3f, 1e-3f);

        // Straight into the wall: blocked, no sideways drift.
        s = Run(Standing(Vector3.Zero), world, 60, t => Walk(t, 0f, 1f));
        Assert.InRange(s.Position.Y, 3f - R - 0.01f, 3f - R + 1e-3f);
        Assert.InRange(s.Position.X, -1e-3f, 1e-3f);
    }

    [Theory]
    [InlineData(0.3f, true)]
    [InlineData(0.39f, true)]
    [InlineData(0.6f, false)]
    [InlineData(1.0f, false)]
    public void Steps_up_to_the_step_height_are_climbed_and_higher_ledges_are_not(float height, bool climbed)
    {
        var world = new Builder().Floor().Box(new Vector3(-5, 2, -1), new Vector3(5, 20, height)).World();
        var s = Run(Standing(Vector3.Zero), world, 60, t => Walk(t, 0f, 1f));
        if (climbed)
        {
            Assert.InRange(s.Position.Z, height, height + 1e-3f);
            Assert.True(s.Position.Y > 6f, $"y {s.Position.Y}");
        }
        else
        {
            Assert.InRange(s.Position.Z, 0f, 1e-3f);
            Assert.InRange(s.Position.Y, 2f - R - 0.01f, 2f - R + 1e-3f);
        }
        Assert.True(s.Grounded);

        // From standing still against the step (no run-up), or walking at it slowly on the stick.
        foreach (float stick in new[] { 1f, 0.2f })
        {
            s = Run(Standing(new Vector3(0, 2f - R - 1e-3f, 0)), world, 90, t => Walk(t, 0f, stick));
            Assert.Equal(climbed, s.Position.Z > height - 1e-3f);
            Assert.True(s.Grounded);
        }
    }

    [Fact]
    public void Walking_down_stairs_stays_grounded()
    {
        var b = new Builder().Floor();
        for (int i = 0; i < 8; i++) b.Box(new Vector3(-5, 2 + i, -1), new Vector3(5, 3 + i, 2.4f - 0.3f * i));   // 0.3 m steps down
        var world = b.World();
        var s = Run(Standing(new Vector3(0, 2.5f, 2.4f)), world, 90, t => Walk(t, 0f, 1f), each: p => Assert.True(p.Grounded, $"airborne at y {p.Position.Y}"));
        Assert.True(s.Position.Y > 11f);
        Assert.InRange(s.Position.Z, 0f, 1e-3f);
    }

    [Fact]
    public void A_30_degree_ramp_is_walkable_and_a_70_degree_one_is_not()
    {
        var gentle = new Builder().Floor().Ramp(2f, 30f, 3f).World();
        float top = 2f + 3f / MathF.Tan(MathF.PI / 6f);
        var s = Run(Standing(Vector3.Zero), gentle, 90, t => Walk(t, 0f, 1f), each: p =>
        {
            Assert.True(p.Grounded, $"airborne at y {p.Position.Y}");
            // On the ramp the feet follow its surface (the round capsule bottom touches it a little ahead).
            if (p.Position.Y > 2.5f && p.Position.Y < top - 0.5f)
                Assert.InRange(p.Position.Z, (p.Position.Y - 2f) * MathF.Tan(MathF.PI / 6f) - 1e-3f, (p.Position.Y - 2f) * MathF.Tan(MathF.PI / 6f) + 0.15f);
        });
        Assert.True(s.Position.Y > top + 1f);
        Assert.InRange(s.Position.Z, 3f, 3f + 1e-3f);

        var steep = new Builder().Floor().Ramp(2f, 70f, 3f).World();
        s = Run(Standing(Vector3.Zero), steep, 90, t => Walk(t, 0f, 1f), each: p => Assert.True(p.Position.Z < 0.5f, $"climbing to {p.Position.Z}"));
        Assert.InRange(s.Position.Y, 1f, 2.5f);
        Assert.True(s.Grounded);

        // Dropped onto the steep slope, a player slides down it to the floor instead of standing on it.
        s = Run(Standing(new Vector3(0, 2.6f, 2.5f), grounded: false), steep, 60, t => Walk(t, 0f, 0f));
        Assert.True(s.Grounded);
        Assert.InRange(s.Position.Z, 0f, 1e-3f);
        Assert.True(s.Position.Y < 2f);
    }

    [Fact]
    public void Walking_off_a_ledge_falls_with_gravity_and_lands()
    {
        var world = new Builder().Floor().Box(new Vector3(-5, -5, -1), new Vector3(5, 2, 5)).World();
        var states = new List<PlayerState>();
        var s = Run(Standing(new Vector3(0, 0, 5)), world, 90, t => Walk(t, 0f, 1f), each: states.Add);
        int left = states.FindIndex(p => !p.Grounded);
        Assert.True(left > 0, "never left the ledge");
        Assert.InRange(states[left - 1].Position.Y, 2f, 2f + R + 0.2f);   // walks until the capsule's middle passes the edge
        int landed = states.FindIndex(left, p => p.Grounded);
        Assert.True(landed > left, "never landed");
        // 5 m at 20 m/s² takes about 0.71 s (21 ticks); the edge roll-off adds a little.
        Assert.InRange(landed - left, 19, 26);
        for (int i = left + 1; i < landed; i++) Assert.True(states[i].Velocity.Z < states[i - 1].Velocity.Z, "speeding up downward");
        Assert.InRange(states[landed].Position.Z, 0f, 1e-3f);
        Assert.Equal(0f, states[landed].Velocity.Z);
        Assert.True(s.Grounded);
        Assert.InRange(s.Position.Z, 0f, 1e-3f);
    }

    [Theory]
    [InlineData(10f)]
    [InlineData(30f)]
    [InlineData(80f)]
    public void Sprinting_into_a_thin_wall_never_tunnels(float sprintSpeed)
    {
        var c = Config with { SprintSpeed = sprintSpeed, MaxAcceleration = 10000f };
        // A single two-sided quad, no thickness, standing on a floor.
        var world = new Builder().Floor().WallY(3f, -2f, 2f, 0f, 3f).World();
        foreach (float x in new[] { 0f, 1.9f, -1.5f })
        {
            var previous = new Vector3(x, 0, 0);
            var s = Run(Standing(previous), world, 60, t => Walk(t, 0f, 1f, InputButtons.Sprint), c, p =>
            {
                // Never closer to the wall than the radius, and never across it within its width (it may slide round an end).
                float dx = MathF.Max(0f, MathF.Abs(p.Position.X) - 2f), dy = p.Position.Y - 3f;
                Assert.True(MathF.Sqrt(dx * dx + dy * dy) >= R - 1e-3f, $"inside the wall's reach at {p.Position}");
                bool crossed = previous.Y < 3f && p.Position.Y >= 3f;
                Assert.False(crossed && MathF.Abs((previous.X + p.Position.X) / 2) < 2f, $"through the wall from {previous} to {p.Position}");
                previous = p.Position;
            });
            if (x == 0f) Assert.InRange(s.Position.Y, 3f - R - 0.01f, 3f - R + 1e-3f);
        }
    }

    [Theory]
    [InlineData(50f, 20f)]
    [InlineData(500f, 20f)]
    [InlineData(50f, 2000f)]
    public void Long_falls_never_tunnel_through_a_floor(float height, float gravity)
    {
        var c = Config with { Gravity = gravity };
        // A thin single-quad floor with nothing under it.
        var world = new Builder().Floor(0f, 5f).World();
        var s = Standing(new Vector3(1, 1, height), grounded: false);
        int ticks = 0;
        while (!s.Grounded && ticks++ < 1000)
        {
            s = Movement.Step(s, Walk((uint)ticks, 0f, 0f), c, world);
            Assert.True(s.Position.Z >= -1e-3f, $"below the floor at {s.Position.Z} after {ticks} ticks");
        }
        Assert.True(s.Grounded);
        Assert.InRange(s.Position.Z, 0f, 1e-3f);
        Assert.Equal(0f, s.Velocity.Z);
        s = Run(s, world, 30, t => Walk(t, 0f, 0f), c);
        Assert.True(s.Grounded);
        Assert.InRange(s.Position.Z, 0f, 1e-3f);
    }

    [Fact]
    public void A_capsule_starting_inside_geometry_is_pushed_out()
    {
        var world = new Builder().Floor().WallY(3f).World();
        // Feet 0.3 m under the floor.
        var s = Movement.Step(Standing(new Vector3(0, 0, -0.3f)), Walk(0, 0, 0), Config, world);
        Assert.InRange(s.Position.Z, 0f, 1e-3f);
        Assert.True(s.Grounded);
        // Centred on the wall plane, a little on the near side: pushed back to the near side, clear of it.
        s = Movement.Step(Standing(new Vector3(0, 2.9f, 0)), Walk(0, 0, 0), Config, world);
        Assert.InRange(s.Position.Y, 3f - R - 1e-3f, 3f - R + 1e-3f);
        s = Movement.Step(Standing(new Vector3(0, 3.1f, 0)), Walk(0, 0, 0), Config, world);
        Assert.InRange(s.Position.Y, 3f + R - 1e-3f, 3f + R + 1e-3f);
    }

    [Fact]
    public void A_low_ceiling_stops_upward_motion()
    {
        var c = Config with { JumpSpeed = 8f };
        var world = new Builder().Floor().Floor(2.5f).World();
        var s = Run(Standing(Vector3.Zero), world, 40, t => Walk(t, 0f, 0f, t == 0 ? InputButtons.Jump : InputButtons.None), c,
            p => Assert.True(p.Position.Z + c.CapsuleHeight <= 2.5f + 1e-3f, $"head through the ceiling at {p.Position.Z}"));
        Assert.True(s.Grounded);
        Assert.InRange(s.Position.Z, 0f, 1e-3f);
    }

    [Fact]
    public void Movement_is_deterministic_and_the_tree_is_rebuilt_identically()
    {
        static CollisionWorld Level()
        {
            var b = new Builder().Floor().WallY(8f).Ramp(-10f, 25f, 2f).Box(new Vector3(3, -3, -1), new Vector3(6, 3, 0.3f)).Box(new Vector3(-8, 1, -1), new Vector3(-6, 3, 5));
            for (int i = 0; i < 20; i++) b.Box(new Vector3(-20 + 2 * i, -20, -1), new Vector3(-19.5f + 2 * i, -19, 1 + 0.1f * i));
            return b.World();
        }
        PlayerState[] Simulate(CollisionWorld world)
        {
            var random = new Random(99);
            var s = Standing(Vector3.Zero);
            var trace = new PlayerState[600];
            for (uint t = 0; t < trace.Length; t++)
            {
                var input = new PlayerInput(t, (float)random.NextDouble() * 2 - 1, (float)random.NextDouble() * 2 - 1, (float)random.NextDouble() * 6.28f,
                    random.Next(3) == 0 ? InputButtons.Sprint : InputButtons.None);
                s = Movement.Step(s, input, Config, world);
                trace[t] = s;
            }
            return trace;
        }
        var a = Level(); var b = Level();
        Assert.Equal(a.ContentHash, b.ContentHash);
        var first = Simulate(a);
        Assert.Equal(first, Simulate(b));
        Assert.Equal(first, Simulate(a));
        Assert.True(first.Select(p => p.Position).Distinct().Count() > 300);
    }

    [Theory]
    [InlineData(45f, 10f)]
    [InlineData(45f, 6.5f)]
    [InlineData(55f, 10f)]
    [InlineData(55f, 6.5f)]
    public void Walkable_slopes_are_walked_up_and_down_at_any_speed(float degrees, float speed)
    {
        // Up to the walkable limit (60°), whatever the speed: the per-tick rise may be well over the step height.
        var c = Config with { RunSpeed = speed, SprintSpeed = speed };
        var level = new Builder().Floor(0f, 100f).Ramp(2f, degrees, 4f, plateau: 60f).World();
        float top = 2f + 4f / MathF.Tan(degrees * MathF.PI / 180f);
        var s = Run(Standing(Vector3.Zero), level, 90, t => Walk(t, 0f, 1f), c, p => Assert.True(p.Grounded, $"airborne going up at {p.Position}"));
        Assert.True(s.Position.Y > top + 3f, $"stopped at {s.Position}");
        Assert.InRange(s.Position.Z, 4f, 4f + 1e-3f);

        // And down again, staying on it (no ballistic hops off the slope).
        s = Run(s with { Velocity = Vector3.Zero }, level, 30 * (int)MathF.Ceiling((s.Position.Y + 2f) / speed + 1f), t => Walk(t, 0f, 1f, yaw: MathF.PI), c, p =>
        {
            Assert.True(p.Grounded, $"airborne going down at {p.Position}");
            Assert.True(p.Position.Z >= -1e-3f);
        });
        Assert.True(s.Position.Y < 1f);
        Assert.InRange(s.Position.Z, 0f, 1e-3f);
    }

    [Theory]
    [InlineData(65f, 10f)]
    [InlineData(65f, 6.5f)]
    [InlineData(70f, 30f)]
    public void Slopes_steeper_than_the_limit_stay_walls_at_any_speed(float degrees, float speed)
    {
        var c = Config with { RunSpeed = speed, SprintSpeed = speed, MaxAcceleration = 1000f };
        var level = new Builder().Floor().Ramp(2f, degrees, 4f).World();
        var s = Run(Standing(Vector3.Zero), level, 120, t => Walk(t, 0f, 1f), c, p => Assert.True(p.Position.Z < 0.6f, $"climbing at {p.Position}"));
        Assert.True(s.Position.Y < 2.5f);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(10f)]
    [InlineData(-10f)]
    [InlineData(80f)]
    public void Sprinting_into_an_acute_wedge_never_ends_beyond_either_wall(float approachDegrees)
    {
        // Two thin walls meeting at 20° at the apex (0, 10); the capsule runs into the narrow end from inside.
        float half = 10f * MathF.PI / 180f;
        Vector3 apex = new(0, 10, 0);
        var left = new Vector3(-MathF.Sin(half), -MathF.Cos(half), 0) * 15f + apex;
        var right = new Vector3(MathF.Sin(half), -MathF.Cos(half), 0) * 15f + apex;
        var level = new Builder().Floor()
            .Quad(apex + new Vector3(0, 0, -1), left + new Vector3(0, 0, -1), left + new Vector3(0, 0, 3), apex + new Vector3(0, 0, 3))
            .Quad(apex + new Vector3(0, 0, -1), right + new Vector3(0, 0, -1), right + new Vector3(0, 0, 3), apex + new Vector3(0, 0, 3))
            .World();
        // Signed distance to each wall's line, positive inside the wedge.
        float Inside(Vector3 p, Vector3 end)
        {
            var d = Vector2.Normalize(new Vector2(end.X - apex.X, end.Y - apex.Y));
            var n = new Vector2(-d.Y, d.X);
            if (end.X > 0) n = -n;
            return Vector2.Dot(new Vector2(p.X - apex.X, p.Y - apex.Y), n);
        }
        var c = Config with { SprintSpeed = 30f, MaxAcceleration = 10000f };
        float yaw = approachDegrees * MathF.PI / 180f;
        var s = Run(Standing(new Vector3(0, 0, 0)), level, 120, t => Walk(t, 0f, 1f, InputButtons.Sprint, yaw), c, p =>
        {
            Assert.True(Inside(p.Position, left) >= R - 0.02f, $"beyond the left wall at {p.Position}");
            Assert.True(Inside(p.Position, right) >= R - 0.02f, $"beyond the right wall at {p.Position}");
            Assert.True(p.Position.Y < 10f);
        });
        // It got as deep as the capsule fits (where the wedge is a diameter wide) and no deeper.
        float fits = 10f - R / MathF.Sin(half);
        if (MathF.Abs(approachDegrees) <= 10f) Assert.InRange(s.Position.Y, fits - 2f, fits + 0.05f);
    }

    [Fact]
    public void A_gap_narrower_than_the_capsule_cannot_be_entered_but_a_squeezed_capsule_can_leave_along_it()
    {
        // A slot 0.8 m wide (the capsule is 0.9 m across) between two walls along Y, opening at y = 2.
        var level = new Builder().Floor().WallX(-0.4f, 2f, 20f).WallX(0.4f, 2f, 20f).World();
        foreach (float yaw in new[] { 0f, 0.3f, -0.3f })
        {
            var s = Run(Standing(new Vector3(0, 0, 0)), level, 90, t => Walk(t, 0f, 1f, InputButtons.Sprint, yaw), each: p =>
                Assert.True(p.Position.Y <= 2f - MathF.Sqrt(R * R - 0.4f * 0.4f) + 0.02f || MathF.Abs(p.Position.X) > 0.4f + R - 0.02f, $"squeezed into the slot at {p.Position}"));
            Assert.True(s.Position.Y < 2f || MathF.Abs(s.Position.X) > 0.4f + R - 0.02f, $"in the slot at {s.Position}");   // stopped, or slid round the outside
        }
        // Placed inside it (as by an old version or a bad marker): pushing into a wall goes nowhere, along the slot works.
        var squeezed = Standing(new Vector3(0, 10, 0));
        var stuck = Run(squeezed, level, 30, t => Walk(t, 1f, 0f));
        Assert.InRange(stuck.Position.X, -0.06f, 0.06f);
        var free = Run(squeezed, level, 90, t => Walk(t, 0f, -1f));
        Assert.True(free.Position.Y < 1.5f, $"still squeezed at {free.Position}");
    }

    [Fact]
    public void Falling_into_a_steep_crevice_narrower_than_the_capsule_comes_to_rest_and_can_walk_along_it()
    {
        // Two 75° walls forming a V along Y, its bottom at z = 0: too steep to stand on, too narrow to slide down.
        float run = 3f / MathF.Tan(75f * MathF.PI / 180f);
        var level = new Builder()
            .Quad(new(0, -50, 0), new(0, 50, 0), new(-run, 50, 3), new(-run, -50, 3))
            .Quad(new(0, -50, 0), new(run, -50, 3), new(run, 50, 3), new(0, 50, 0))
            .World();
        var s = Run(Standing(new Vector3(0, 0, 4f), grounded: false), level, 60, t => Walk(t, 0f, 0f), each: p => Assert.True(p.Position.Z > 0f));
        Assert.True(s.Grounded, $"still falling at {s.Position} v {s.Velocity}");
        Assert.True(s.Velocity.Z > -1f, $"falling speed builds up: {s.Velocity}");
        float rest = s.Position.Z;
        s = Run(s, level, 60, t => Walk(t, 0f, 1f));
        Assert.True(s.Position.Y > 3f, $"cannot walk along it: {s.Position}");
        Assert.InRange(s.Position.Z, rest - 0.05f, rest + 0.05f);
    }

    [Fact]
    public void A_capsule_placed_below_a_floor_is_lifted_onto_it_not_pushed_through()
    {
        // A floor at 2.369 with nothing under it; the marker's feet are 1.369 m below it, the capsule pierces it.
        var level = new Builder().Floor(2.369f, 100f).World();
        var s = Movement.Step(Standing(new Vector3(5, -55.25f, 1f)), Walk(0, 0, 0), Config, level);
        Assert.True(s.Grounded);
        Assert.InRange(s.Position.Z, 2.369f, 2.369f + 1e-3f);
        // A sloped (walkable) floor too.
        level = new Builder().Floor().Ramp(-10f, 30f, 10f, halfWidth: 20f).World();
        s = Movement.Step(Standing(new Vector3(0, 0, 5f)), Walk(0, 0, 0), Config, level);
        float surface = 10f * MathF.Tan(MathF.PI / 6f);
        Assert.True(s.Grounded);
        Assert.InRange(s.Position.Z, surface - 1e-3f, surface + 0.1f);
    }

    [Fact]
    public void Slight_overlap_with_the_floor_does_not_drop_the_capsule_onto_a_layer_below()
    {
        // Two floors 0.25 m apart (duplicate collision layers); feet start a hair inside the upper one.
        var level = new Builder().Floor(0f).Floor(-0.25f).World();
        foreach (float sink in new[] { 0f, 4e-4f, 0.01f, 0.1f })
        {
            var s = Standing(new Vector3(0.3f, 0.2f, -sink)) with { Velocity = new Vector3(-5.9f, -2.7f, 0) };
            s = Run(s, level, 10, t => Walk(t, 0f, 1f, yaw: 2f), each: p =>
            {
                Assert.True(p.Grounded);
                Assert.InRange(p.Position.Z, -1e-3f, 1e-3f);
            });
        }
    }

    [Fact]
    public void Falling_speed_is_capped()
    {
        var c = Config with { MaxFallSpeed = 60f };
        var level = new Builder().Floor(-1500f, 5f).World();
        var s = Standing(new Vector3(0, 0, 0), grounded: false);
        float fastest = 0f;
        s = Run(s, level, 300, t => Walk(t, 0f, 0f), c, p => { fastest = MathF.Max(fastest, -p.Velocity.Z); Assert.True(-p.Velocity.Z <= 60f + 1e-3f); });
        Assert.Equal(60f, fastest, 3);
        Assert.False(s.Grounded);
        // Landing at the cap speed onto a thin floor still holds.
        s = Run(s, level, 600, t => Walk(t, 0f, 0f), c);
        Assert.True(s.Grounded);
        Assert.InRange(s.Position.Z, -1500f, -1500f + 1e-2f);
    }

    [Fact]
    public void Falling_out_of_the_level_kills_and_respawns_at_a_spawn_point()
    {
        var c = Config with { RespawnTime = 0.5f, KillDepth = 20f };
        // A platform floating in a void: its lowest point is z = -1, so the kill height is -21.
        var level = new Builder().Box(new Vector3(-3, -3, -1), new Vector3(3, 3, 0)).World();
        SpawnPoint home = new(new Vector3(0, 0, 0), 1.5f);
        var world = new World(c, level, [home]);
        world.Spawn(1);
        bool died = false;
        for (int t = 0; t < 30 * 10 && !died; t++)
        {
            world.Step(_ => new PlayerInput(0, 0, 1, 0, InputButtons.None));
            var p = world.Players[1];
            if (!p.Alive)
            {
                died = true;
                Assert.True(p.Position.Z < -21f + 1e-3f && p.Position.Z > -21f - 3f, $"killed at {p.Position}");
                Assert.Equal(0f, p.Health);
                Assert.Equal(c.RespawnTime, p.RespawnLeft, 3);
            }
        }
        Assert.True(died, "never killed");
        for (int t = 0; t < 30 && !world.Players[1].Alive; t++) world.Step(_ => new PlayerInput(0, 0, 0, 0, InputButtons.None));
        var back = world.Players[1];
        Assert.True(back.Alive);
        Assert.Equal(home.Yaw, back.Yaw);
        Assert.True(Vector3.Distance(home.Position, back.Position) < 0.01f, $"respawned at {back.Position}");
    }

    [Fact]
    public void A_player_killed_by_falling_out_of_the_level_does_not_shoot_on_that_tick()
    {
        // Fire every tick (tiny cooldown, endless ammo), aiming at a target standing on a second platform; shots ignore
        // geometry here (empty weapon mesh), so only the kill can stop the shot on the kill tick.
        var c = Config with { RespawnTime = 5f, KillDepth = 20f, ShotCooldown = 0.001f, ClipSize = 10_000, ReserveSize = 0, Damage = 0.01f };   // the target must survive
        var floors = new Builder().Box(new Vector3(-3, -3, -1), new Vector3(3, 3, 0)).Box(new Vector3(-3, 37, -1), new Vector3(3, 43, 0)).World();
        var level = new CollisionWorld(floors.Character, new StaticMesh([], []));
        var world = new World(c, level, [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(0, 40, 0), 0f)]);
        world.Spawn(1); world.Spawn(2);
        int killTick = -1, hitsWhileFalling = 0;
        for (int t = 0; t < 30 * 10 && killTick < 0; t++)
        {
            var me = world.Players[1];
            var target = world.Players[2].Position + new Vector3(0, 0, 1.2f);
            // Aim from where the shot will leave: the end of this tick's fall (velocity after gravity, capped).
            var v = me.Velocity with { Z = MathF.Max(-c.MaxFallSpeed, me.Velocity.Z - (me.Grounded ? 0 : c.Gravity * c.TickSeconds)) };
            var aim = Vector3.Normalize(target - (me.Position + v * c.TickSeconds + new Vector3(0, 0, c.EyeHeight)));
            float yaw = MathF.Atan2(-aim.X, aim.Y), pitch = MathF.Asin(aim.Z);
            world.Step(id => id == 1 ? new PlayerInput(0, 0, -1, yaw, InputButtons.Fire, pitch) : new PlayerInput(0, 0, 0, 0, InputButtons.None));
            hitsWhileFalling += world.LastHits.Count(h => h.Shooter == 1);
            if (!world.Players[1].Alive)
            {
                killTick = t;
                Assert.DoesNotContain(world.LastHits, h => h.Shooter == 1);
            }
        }
        Assert.True(killTick > 0, "never killed");
        Assert.True(hitsWhileFalling > 0, "the setup never hit at all, so the kill-tick check proves nothing");
    }

    [Fact]
    public void Random_running_among_boxes_never_ends_inside_them_or_under_the_floor()
    {
        var random = new Random(5);
        var b = new Builder().Floor(0f, 30f);
        var boxes = new List<(Vector3 Min, Vector3 Max)>();
        for (int k = 0; k < 60; k++)
        {
            var c = new Vector3((float)random.NextDouble() * 40 - 20, (float)random.NextDouble() * 40 - 20, 0);
            var half = new Vector3(0.2f + (float)random.NextDouble() * 1.5f, 0.2f + (float)random.NextDouble() * 1.5f, 0);
            float height = new[] { 0.15f, 0.3f, 0.6f, 1.2f, 3f }[k % 5];
            var box = (c - half - new Vector3(0, 0, 1), c + half + new Vector3(0, 0, height));
            boxes.Add(box);
            b.Box(box.Item1, box.Item2);
        }
        // Walls round the edge so nobody walks off the floor.
        b.WallY(25f, -30, 30, -1, 5).WallY(-25f, -30, 30, -1, 5)
         .Quad(new(25, -30, -1), new(25, 30, -1), new(25, 30, 5), new(25, -30, 5)).Quad(new(-25, -30, -1), new(-25, 30, -1), new(-25, 30, 5), new(-25, -30, 5));
        var world = b.World();
        float top = Config.CapsuleHeight - R;
        for (int player = 0; player < 6; player++)
        {
            var s = Standing(new Vector3(-22 + 8 * player, -22, 0));
            var input = new PlayerInput(0, 0, 1, 0, InputButtons.None);
            for (uint t = 0; t < 1500; t++)
            {
                if (t % 20 == 0)
                    input = new PlayerInput(t, (float)random.NextDouble() * 2 - 1, (float)random.NextDouble() * 2 - 1, (float)random.NextDouble() * 6.28f,
                        random.Next(2) == 0 ? InputButtons.Sprint : InputButtons.None);
                s = Movement.Step(s, input with { Tick = t }, Config, world);
                Assert.True(s.Position.Z >= -1e-3f, $"under the floor at {s.Position}");
                Assert.True(MathF.Abs(s.Position.X) < 25f && MathF.Abs(s.Position.Y) < 25f, $"through the outer wall at {s.Position}");
                foreach (var (min, max) in boxes)
                {
                    // The capsule's straight part beside a box it overlaps vertically must stay a radius clear of it.
                    if (s.Position.Z + R >= max.Z || s.Position.Z + top <= min.Z) continue;
                    float dx = MathF.Max(0f, MathF.Max(min.X - s.Position.X, s.Position.X - max.X));
                    float dy = MathF.Max(0f, MathF.Max(min.Y - s.Position.Y, s.Position.Y - max.Y));
                    Assert.True(MathF.Sqrt(dx * dx + dy * dy) >= R - 0.01f, $"inside a box {min}..{max} at {s.Position}, tick {t}");
                }
            }
        }
    }

    // ---------------- the world: shots, spawns ----------------

    [Fact]
    public void A_shot_stops_at_a_weapon_collision_wall_between_shooter_and_target()
    {
        var character = new Builder().Floor().Mesh();
        SpawnPoint[] spawns = [new(new Vector3(0, 0, 0), 0f), new(new Vector3(0, 10, 0), MathF.PI)];
        int Shoot(StaticMesh weapons)
        {
            var world = new World(Config, new CollisionWorld(character, weapons), spawns);
            world.Spawn(1); world.Spawn(2);
            var eye = world.Players[1].Position + new Vector3(0, 0, Config.EyeHeight);
            var aim = Vector3.Normalize(world.Players[2].Position + new Vector3(0, 0, 1f) - eye);
            float yaw = MathF.Atan2(-aim.X, aim.Y), pitch = MathF.Asin(aim.Z);
            world.Step(id => id == 1 ? new PlayerInput(0, 0, 0, yaw, InputButtons.Fire, pitch, world.Tick) : new PlayerInput(0, 0, 0, MathF.PI, InputButtons.None));
            return world.LastHits.Count;
        }
        Assert.Equal(0, Shoot(new Builder().Floor().WallY(5f).Mesh()));         // wall in between
        Assert.Equal(1, Shoot(new Builder().Floor().WallY(15f).Mesh()));        // wall behind the target
        Assert.Equal(1, Shoot(new Builder().Floor().WallY(5f, 10f, 20f).Mesh()));   // wall off to the side
        // Only the weapon mesh stops shots: the same wall in the character mesh alone does not.
        var walled = new Builder().Floor().WallY(5f).Mesh();
        var world = new World(Config, new CollisionWorld(walled, character), spawns);
        world.Spawn(1); world.Spawn(2);
        var eye = world.Players[1].Position + new Vector3(0, 0, Config.EyeHeight);
        var aim = Vector3.Normalize(world.Players[2].Position + new Vector3(0, 0, 1f) - eye);
        world.Step(id => id == 1 ? new PlayerInput(0, 0, 0, MathF.Atan2(-aim.X, aim.Y), InputButtons.Fire, MathF.Asin(aim.Z), world.Tick) : new PlayerInput(0, 0, 0, MathF.PI, InputButtons.None));
        Assert.Single(world.LastHits);
    }

    [Fact]
    public void Players_spawn_and_respawn_at_the_given_points()
    {
        SpawnPoint A = new(new Vector3(10, 0, 0), 1f), B = new(new Vector3(-10, 0, 0), 2f), C = new(new Vector3(0, 10, 0), 3f);
        var level = new Builder().Floor().World();
        var world = new World(Config, level, [A, B, C]);
        var p1 = world.Spawn(1); var p2 = world.Spawn(2); var p3 = world.Spawn(3);
        Assert.Equal((A.Position, A.Yaw), (p1.Position, p1.Yaw));
        Assert.Equal((B.Position, B.Yaw), (p2.Position, p2.Yaw));
        Assert.Equal((C.Position, C.Yaw), (p3.Position, p3.Yaw));
        Assert.True(p1.Alive && p1.Grounded);
        // All taken: the rotation continues regardless.
        Assert.Equal(A.Position, world.Spawn(4).Position);

        // Occupied points are skipped while a free one exists.
        world = new World(Config, level, [A, B]);
        world.Spawn(1); world.Spawn(2); world.Remove(2);
        Assert.Equal(B.Position, world.Spawn(3).Position);   // the rotation is back at A, but player 1 stands there

        // Respawn after death uses the points too: one-shot kills, instant respawn.
        var c = Config with { Damage = 1000f, ShieldMax = 0f, RespawnTime = 0f };
        world = new World(c, level, [new(new Vector3(0, 0, 0), 0f), new(new Vector3(0, 10, 0), 0f), C]);
        world.Spawn(1); world.Spawn(2);
        var eye = new Vector3(0, 0, c.EyeHeight);
        var aim = Vector3.Normalize(new Vector3(0, 10, 1f) - eye);
        world.Step(id => id == 1 ? new PlayerInput(0, 0, 0, MathF.Atan2(-aim.X, aim.Y), InputButtons.Fire, MathF.Asin(aim.Z), world.Tick) : new PlayerInput(0, 0, 0, 0, InputButtons.None));
        Assert.True(Assert.Single(world.LastHits).Killed);
        world.Step(_ => new PlayerInput(0, 0, 0, 0, InputButtons.None));
        var back = world.Players[2];
        Assert.True(back.Alive);
        Assert.Equal(C.Position.X, back.Position.X, 3);
        Assert.Equal(C.Position.Y, back.Position.Y, 3);
    }

    [Fact]
    public void World_without_collision_keeps_the_flat_ground_and_placeholder_spawns()
    {
        var plain = new World(Config);
        var explicitNull = new World(Config, null, null);
        for (int id = 1; id <= 3; id++) Assert.Equal(plain.Spawn(id), explicitNull.Spawn(id));
        for (int i = 0; i < 60; i++)
        {
            Func<int, PlayerInput> input = id => new PlayerInput(0, 0.5f, 1, 0.3f * id, InputButtons.Sprint | InputButtons.Fire, 0.1f, plain.Tick);
            plain.Step(input); explicitNull.Step(input);
        }
        Assert.Equal(plain.Players.ToArray(), explicitNull.Players.ToArray());
        Assert.All(plain.Players.Values, p => Assert.Equal(0f, p.Position.Z));
    }

    [Fact]
    public void Config_hash_covers_the_collision_parameters()
    {
        var c = new SimConfig();
        Assert.Equal(60f, c.MaxWalkableSlope);
        Assert.NotEqual(c.Hash(), (c with { CapsuleRadius = 0.5f }).Hash());
        Assert.NotEqual(c.Hash(), (c with { CapsuleHeight = 2f }).Hash());
        Assert.NotEqual(c.Hash(), (c with { StepHeight = 0.5f }).Hash());
        Assert.NotEqual(c.Hash(), (c with { MaxWalkableSlope = 45f }).Hash());
    }

    // ---------------- performance on a map-sized synthetic level ----------------

    /// <summary>
    /// About 64,000 triangles over 4000 × 1500 × 560 m like the real map: a rolling terrain grid plus 1,600 boxes packed
    /// around the middle where 16 players run about, fire and collide.
    /// </summary>
    [Fact]
    public void Map_sized_level_builds_raycasts_and_steps_quickly()
    {
        var random = new Random(7);
        const int nx = 150, ny = 150, boxes = 1600;
        var b = new Builder();
        Vector3 Ground(int i, int j)
        {
            float x = -2000f + 4000f * i / nx, y = -750f + 1500f * j / ny;
            bool middle = MathF.Abs(x) < 150f && MathF.Abs(y) < 150f;
            return new Vector3(x, y, middle ? 0f : 280f * (1 + MathF.Sin(x * 0.003f) * MathF.Cos(y * 0.005f)) - 280f);
        }
        for (int j = 0; j < ny; j++)
            for (int i = 0; i < nx; i++) b.Quad(Ground(i, j), Ground(i + 1, j), Ground(i + 1, j + 1), Ground(i, j + 1));
        for (int k = 0; k < boxes; k++)
        {
            var c = new Vector3((float)random.NextDouble() * 200 - 100, (float)random.NextDouble() * 200 - 100, 0);
            var size = new Vector3(0.5f + (float)random.NextDouble() * 3, 0.5f + (float)random.NextDouble() * 3, 0.2f + (float)random.NextDouble() * 4);
            b.Box(new Vector3(c.X - size.X / 2, c.Y - size.Y / 2, -0.5f), new Vector3(c.X + size.X / 2, c.Y + size.Y / 2, size.Z));
        }
        var (vertices, indices) = b.Buffers();

        var sw = Stopwatch.StartNew();
        var mesh = new StaticMesh(vertices, indices);
        double buildMs = sw.Elapsed.TotalMilliseconds;
        output.WriteLine($"{mesh.TriangleCount} triangles, bounds {mesh.Min} .. {mesh.Max}; BVH build {buildMs:F0} ms");
        Assert.Equal(2 * nx * ny + 12 * boxes, mesh.TriangleCount);

        // Shots: random eye-height rays of up to 250 m from inside the busy middle.
        var rays = new (Vector3 O, Vector3 D)[20000];
        for (int i = 0; i < rays.Length; i++)
            rays[i] = (new Vector3((float)random.NextDouble() * 200 - 100, (float)random.NextDouble() * 200 - 100, 1.6f),
                       Vector3.Normalize(new Vector3((float)random.NextDouble() * 2 - 1, (float)random.NextDouble() * 2 - 1, (float)random.NextDouble() * 0.4f - 0.2f)));
        int hits = 0;
        foreach (var (o, d) in rays.Take(2000)) if (mesh.Raycast(o, d, 250f) is not null) hits++;   // warm-up
        sw.Restart();
        foreach (var (o, d) in rays) if (mesh.Raycast(o, d, 250f) is not null) hits++;
        double rayUs = sw.Elapsed.TotalMilliseconds * 1000 / rays.Length;
        output.WriteLine($"raycast {rayUs:F2} µs average over {rays.Length} rays ({hits} hits)");

        // 16 players sprinting and firing in changing directions among the boxes.
        var level = new CollisionWorld(mesh, mesh);
        var spawns = Enumerable.Range(0, 16).Select(i => new SpawnPoint(new Vector3(-60 + 8 * i, -60 + 8 * (i % 4), 3), 0f)).ToArray();
        var world = new World(Config, level, spawns);
        for (int id = 1; id <= 16; id++) world.Spawn(id);
        PlayerInput Input(int id) => new(0, MathF.Sin(world.Tick * 0.05f + id), 1f, world.Tick * 0.02f + id, InputButtons.Sprint | InputButtons.Fire, 0.05f, world.Tick);
        for (int i = 0; i < 60; i++) world.Step(Input);
        const int ticks = 300;
        sw.Restart();
        for (int i = 0; i < ticks; i++) world.Step(Input);
        double stepMs = sw.Elapsed.TotalMilliseconds / ticks;
        output.WriteLine($"16-player World.Step {stepMs:F3} ms average over {ticks} ticks");
        Assert.All(world.Players.Values, p => Assert.True(p.Position.Z > -1f, $"player {p.PlayerId} fell through at {p.Position}"));

        // Generous bounds so slow CI machines and Debug builds pass; the report quotes the measured numbers.
        Assert.True(buildMs < 3000, $"build {buildMs} ms");
        Assert.True(rayUs < 200, $"raycast {rayUs} µs");
        Assert.True(stepMs < 20, $"step {stepMs} ms");
    }
}
