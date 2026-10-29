using System.Numerics;
using SmNet;
using SmServer;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// M20: hit volumes from the rig's ragdoll (<see cref="SimConfig.HitVolumes"/>): geometry of spheres, capsules, boxes and
/// hulls turned by the player's yaw, equivalence with the placeholder shapes, the config hash, and the hit-registration audit
/// against the original marine's volumes (spec/behavior.md; typed in, the hulls replaced by boxes around them).
/// </summary>
public class HitVolumeTests(ITestOutputHelper output)
{
    /// <summary>
    /// The original marine's volumes (values from spec/behavior.md, read from the install by <c>HitVolumeTuning</c>): the head
    /// sphere, limb capsules and hand spheres exactly; the torso, backpack, pelvis and shoulder-pad hulls as the bounding boxes
    /// the file stores for them (slightly larger than the hulls; the hull planes are not typed in). Exhaust stacks and feet left out.
    /// </summary>
    internal static readonly IReadOnlyList<HitVolume> OriginalLike =
    [
        HitVolume.Sphere("Head", HitZone.Head, new Vector3(0f, -0.038f, 2.133f), 0.178f),
        HitVolume.Box("Pelvis", HitZone.Body, new Vector3(0.001f, -0.005f, 1.282f), new Vector3(0.244f, 0.329f, 0.322f), new Quaternion(0f, -0.707f, 0f, 0.707f)),
        HitVolume.Box("Chest", HitZone.Body, new Vector3(0.005f, -0.063f, 1.787f), new Vector3(0.347f, 0.343f, 0.378f), new Quaternion(0.046f, -0.706f, -0.046f, 0.706f)),
        HitVolume.Box("Chest #2", HitZone.Body, new Vector3(0f, -0.453f, 1.939f), new Vector3(0.658f, 0.177f, 0.473f), new Quaternion(0.046f, -0.706f, -0.046f, 0.706f)),
        HitVolume.Box("R_ShoulderPad", HitZone.Body, new Vector3(0.451f, -0.114f, 1.883f), new Vector3(0.219f, 0.277f, 0.272f), new Quaternion(0.996f, -0.001f, 0.090f, -0.022f)),
        HitVolume.Box("L_ShoulderPad", HitZone.Body, new Vector3(-0.428f, -0.108f, 1.897f), new Vector3(0.210f, 0.320f, 0.282f), new Quaternion(0.090f, -0.009f, 0.996f, 0.003f)),
        HitVolume.Capsule("L_Thigh", HitZone.Body, new Vector3(-0.174f, -0.027f, 1.081f), new Vector3(-0.193f, -0.023f, 0.876f), 0.183f),
        HitVolume.Capsule("R_Thigh", HitZone.Body, new Vector3(0.173f, -0.027f, 1.081f), new Vector3(0.193f, -0.023f, 0.876f), 0.183f),
        HitVolume.Capsule("L_Calf", HitZone.Body, new Vector3(-0.228f, -0.024f, 0.512f), new Vector3(-0.247f, -0.078f, 0.263f), 0.230f),
        HitVolume.Capsule("R_Calf", HitZone.Body, new Vector3(0.228f, -0.024f, 0.512f), new Vector3(0.246f, -0.078f, 0.263f), 0.230f),
        HitVolume.Capsule("L_UpperArm", HitZone.Body, new Vector3(-0.403f, -0.089f, 1.721f), new Vector3(-0.443f, -0.088f, 1.610f), 0.127f),
        HitVolume.Capsule("R_UpperArm", HitZone.Body, new Vector3(0.403f, -0.095f, 1.722f), new Vector3(0.442f, -0.097f, 1.611f), 0.127f),
        HitVolume.Capsule("L_Forearm", HitZone.Body, new Vector3(-0.507f, -0.048f, 1.423f), new Vector3(-0.539f, -0.014f, 1.326f), 0.117f),
        HitVolume.Capsule("R_Forearm", HitZone.Body, new Vector3(0.507f, -0.062f, 1.423f), new Vector3(0.540f, -0.031f, 1.324f), 0.117f),
        HitVolume.Sphere("L_Hand", HitZone.Body, new Vector3(-0.577f, 0.047f, 1.154f), 0.119f),
        HitVolume.Sphere("R_Hand", HitZone.Body, new Vector3(0.578f, 0.026f, 1.149f), 0.119f),
    ];

    const float HelmetTop = 2.133f + 0.178f;   // 2.311 m

    static HitVolume Cube(string name, Vector3 centre, float half) => HitVolume.Hull(name, HitZone.Body,
        [new(1, 0, 0, -centre.X - half), new(-1, 0, 0, centre.X - half), new(0, 1, 0, -centre.Y - half), new(0, -1, 0, centre.Y - half), new(0, 0, 1, -centre.Z - half), new(0, 0, -1, centre.Z - half)],
        Enumerable.Range(0, 8).Select(i => centre + new Vector3((i & 1) == 0 ? -half : half, (i & 2) == 0 ? -half : half, (i & 4) == 0 ? -half : half)).ToArray());

    // ---------------------------------------------------------------- geometry

    [Fact]
    public void Every_kind_is_hit_where_it_stands_turned_by_the_yaw()
    {
        // One of each kind, 1 m to the player's right (+X) at 1 m height; turned by yaw +π/2 (a quarter left), local +X
        // points along world +Y.
        foreach (var volume in new[]
        {
            HitVolume.Sphere("s", HitZone.Body, new Vector3(1f, 0, 1f), 0.2f),
            HitVolume.Capsule("c", HitZone.Body, new Vector3(1f, 0, 0.8f), new Vector3(1f, 0, 1.2f), 0.2f),
            HitVolume.Box("b", HitZone.Body, new Vector3(1f, 0, 1f), new Vector3(0.2f, 0.2f, 0.2f), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.3f)),
            Cube("h", new Vector3(1f, 0, 1f), 0.2f),
        })
        {
            var list = new[] { volume };
            var feet = new Vector3(5f, 5f, 0f);
            float yaw = MathF.PI / 2;
            // From far along world +Y, looking back down −Y at the volume's world spot (5, 6, 1).
            var (zone, distance) = HitVolumes.Raycast(list, new Vector3(5f, 20f, 1f), -Vector3.UnitY, feet, yaw, 100f);
            Assert.Equal(HitZone.Body, zone);
            Assert.InRange(distance, 13.7f, 13.81f);   // 14 − ~0.2 (the near face or surface)
            // At yaw 0 the volume stands at (6, 5, 1) instead: the same ray misses.
            Assert.Equal(HitZone.None, HitVolumes.Raycast(list, new Vector3(5f, 20f, 1f), -Vector3.UnitY, feet, 0f, 100f).Zone);
            Assert.Equal(HitZone.Body, HitVolumes.Contains(list, new Vector3(5f, 6f, 1f), feet, yaw));
            Assert.Equal(HitZone.None, HitVolumes.Contains(list, new Vector3(6f, 5f, 1f), feet, yaw));
            // A ray starting inside hits at 0.
            Assert.Equal(0f, HitVolumes.Raycast(list, new Vector3(5f, 6f, 1f), Vector3.UnitX, feet, yaw, 100f).Distance);
            // Out of range.
            Assert.Equal(HitZone.None, HitVolumes.Raycast(list, new Vector3(5f, 20f, 1f), -Vector3.UnitY, feet, yaw, 13f).Zone);
        }
        Assert.Equal(new Vector3(0, 1, 0), HitVolumes.Rotate(Vector3.UnitX, MathF.PI / 2), new Vec3Comparer());
        var p = new Vector3(0.3f, -0.7f, 1.1f);
        Assert.Equal(p, HitVolumes.ToLocal(HitVolumes.ToWorld(p, new Vector3(3, 4, 5), 1.234f), new Vector3(3, 4, 5), 1.234f), new Vec3Comparer());
    }

    sealed class Vec3Comparer : IEqualityComparer<Vector3>
    {
        public bool Equals(Vector3 a, Vector3 b) => Vector3.Distance(a, b) < 1e-5f;
        public int GetHashCode(Vector3 v) => 0;
    }

    [Fact]
    public void The_hull_ray_enters_at_the_nearest_face_and_the_nearest_volume_wins()
    {
        var hull = Cube("h", new Vector3(0, 0, 1f), 0.25f);
        var list = new[] { HitVolume.Sphere("head", HitZone.Head, new Vector3(0, 0, 1.5f), 0.2f), hull };
        // Diagonal ray into the cube's +X face.
        var origin = new Vector3(3f, 0f, 1f + 0.1f * 3f);
        var direction = Vector3.Normalize(new Vector3(-1f, 0f, -0.1f));
        var (zone, distance) = HitVolumes.Raycast(list, origin, direction, Vector3.Zero, 0f, 100f);
        Assert.Equal(HitZone.Body, zone);
        Assert.Equal(2.75f / MathF.Abs(direction.X), distance, 1e-4f);
        // A ray through both: the nearer one (the head from above, the body from below).
        Assert.Equal(HitZone.Head, HitVolumes.Raycast(list, new Vector3(0, 0, 5f), -Vector3.UnitZ, Vector3.Zero, 0f, 100f).Zone);
        Assert.Equal(HitZone.Body, HitVolumes.Raycast(list, new Vector3(0, 0, -5f), Vector3.UnitZ, Vector3.Zero, 0f, 100f).Zone);
        Assert.False(HitVolume.Hull("bad", HitZone.Body, [new(2, 0, 0, 0), new(0, 1, 0, 0), new(0, 0, 1, 0), new(-1, 0, 0, 0)], hull.Vertices!).IsValid);   // not a unit normal
        Assert.True(hull.IsValid);
        Assert.Equal((0.75f, 1.25f), HitVolumes.Extent([hull]));
    }

    [Fact]
    public void The_placeholder_volumes_as_a_list_behave_exactly_as_the_placeholder_shapes()
    {
        var placeholder = new SimConfig();
        var listed = placeholder with { HitVolumes = HitVolumes.Placeholder(placeholder) };
        var rng = new Random(20);
        int hits = 0;
        for (int i = 0; i < 5000; i++)
        {
            var origin = new Vector3(rng.NextSingle() * 6 - 3, rng.NextSingle() * 6 - 3, rng.NextSingle() * 3);
            var target = new Vector3(rng.NextSingle() - 0.5f, rng.NextSingle() - 0.5f, rng.NextSingle() * 2.4f);
            var direction = Vector3.Normalize(target - origin);
            var a = Combat.HitShapes(origin, direction, Vector3.Zero, placeholder, 100f);
            var b = Combat.HitShapes(origin, direction, Vector3.Zero, listed, 100f, yaw: rng.NextSingle() * 6f);   // upright shapes: yaw makes no difference
            Assert.Equal(a.Zone, b.Zone);
            if (a.Zone != HitZone.None) { hits++; Assert.Equal(a.Distance, b.Distance, 1e-4f); }
            Assert.Equal(Combat.InsideHitShapes(origin, Vector3.Zero, placeholder), Combat.InsideHitShapes(origin, Vector3.Zero, listed));
        }
        Assert.True(hits > 1000);
        Assert.Equal(HitVolumes.CentreHeight(placeholder), (placeholder.BodyBottom + placeholder.BodyTop) / 2f);
        Assert.Equal(2, HitVolumes.Of(placeholder).Count);
    }

    [Fact]
    public void The_config_hash_covers_the_volumes()
    {
        var a = new SimConfig { HitVolumes = OriginalLike };
        Assert.NotEqual(new SimConfig().Hash(), a.Hash());
        Assert.Equal(a.Hash(), new SimConfig { HitVolumes = OriginalLike.ToArray() }.Hash());
        var moved = OriginalLike.Select((v, i) => i == 0 ? v with { A = v.A + new Vector3(0, 0, 0.01f) } : v).ToArray();
        Assert.NotEqual(a.Hash(), new SimConfig { HitVolumes = moved }.Hash());
        Assert.All(OriginalLike, v => Assert.True(v.IsValid, v.Name));
    }

    [Fact]
    public void Shots_hit_the_head_the_shoulders_and_the_backpack_as_the_target_faces()
    {
        var c = new SimConfig { HitVolumes = OriginalLike, HealthMax = 1e6f, ShieldMax = 1e6f };
        var eye = new Vector3(0, 0, c.EyeHeight);
        (HitZone, float) Shoot(Vector3 localPoint, float targetYaw)
        {
            var feet = new Vector3(0, 10f, 0);
            var world = HitVolumes.ToWorld(localPoint, feet, targetYaw);
            return Combat.HitTest(eye, Vector3.Normalize(world - eye), feet, c, 100f, targetYaw);
        }
        float facing = MathF.PI;   // facing the shooter (−Y)
        Assert.Equal(HitZone.Head, Shoot(new Vector3(0, -0.038f, HelmetTop - 0.03f), facing).Item1);   // the helmet top
        // (The face below 2.18 m is not tested here: the stand-in box around the torso hull reaches in front of it; with the
        // install's hulls the face is head, docs/discovery.md.)
        Assert.Equal(HitZone.Body, Shoot(new Vector3(0.44f, -0.11f, 1.95f), facing).Item1);            // right shoulder pad (old placeholders: missed)
        Assert.Equal(HitZone.Body, Shoot(new Vector3(-0.44f, -0.11f, 1.95f), facing).Item1);           // left shoulder pad
        Assert.Equal(HitZone.None, Shoot(new Vector3(0, -0.038f, HelmetTop + 0.3f), facing).Item1);    // 0.3 m above the helmet
        // Facing away: the backpack (top 2.6 m behind the head) takes a shot aimed just over the helmet.
        Assert.Equal(HitZone.Body, Shoot(new Vector3(0, -0.45f, 2.45f), 0f).Item1);
        // The placeholder volumes missed the drawn shoulder pad entirely, and took the inner shoulder beside the neck as head
        // (their head sphere sat at 1.95 m, radius 0.25): the flaws this fixes.
        var old = new SimConfig();
        HitZone OldZone(Vector3 local)
        {
            var point = HitVolumes.ToWorld(local, new Vector3(0, 10f, 0), facing);
            return Combat.HitTest(eye, Vector3.Normalize(point - eye), new Vector3(0, 10f, 0), old, 100f).Zone;
        }
        Assert.Equal(HitZone.None, OldZone(new Vector3(0.44f, -0.11f, 1.95f)));
        Assert.Equal(HitZone.Head, OldZone(new Vector3(0.2f, -0.05f, 1.85f)));
        Assert.Equal(HitZone.Body, Shoot(new Vector3(0.2f, -0.05f, 1.85f), facing).Item1);   // the inner shoulder: body now
        Assert.Equal(HitZone.None, OldZone(new Vector3(0f, -0.038f, HelmetTop - 0.03f)));    // and the old volumes ended below the helmet top
    }

    // ---------------------------------------------------------------- hit-registration audit with the original volumes

    /// <summary>
    /// As <c>AimTests.Shots_aimed_at_the_drawn_target_through_the_crosshair_hit</c>, with the original volumes: the shooter's
    /// crosshair is on a point of the target's model as drawn (interpolated position and yaw at sample time), the shot goes
    /// from the eye through it. The helmet top and the shoulder pads hit (as head and as body); 0.3 m above the helmet misses.
    /// </summary>
    [Theory]
    [InlineData(50, "helmet top")]
    [InlineData(150, "helmet top")]
    [InlineData(150, "right shoulder pad")]
    [InlineData(150, "left shoulder pad")]
    [InlineData(150, "above helmet")]
    public void Shots_at_the_drawn_helmet_top_and_shoulder_pads_hit_and_above_the_helmet_miss(int rttMs, string where)
    {
        var aim = where switch
        {
            "helmet top" => new Vector3(0f, -0.038f, HelmetTop - 0.03f),
            "right shoulder pad" => new Vector3(0.44f, -0.11f, 1.95f),
            "left shoulder pad" => new Vector3(-0.44f, -0.11f, 1.95f),
            _ => new Vector3(0f, -0.038f, HelmetTop + 0.3f),
        };
        var expected = where switch { "helmet top" => HitZone.Head, "above helmet" => HitZone.None, _ => HitZone.Body };
        var config = new SimConfig
        {
            HitVolumes = OriginalLike, HealthMax = 1e7f, ShieldMax = 1e7f, Damage = 22f, ShotCooldown = 0.133f, ClipSize = 1000, ReserveSize = 1000,
            RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f,
        };
        var h = new Harness(new LinkConditions(TimeSpan.FromMilliseconds(rttMs / 2.0), TimeSpan.FromMilliseconds(5), 0), 33, 2, config,
            new ServerOptions { PeerTimeout = TimeSpan.FromMinutes(5) });
        var hits = new List<HitEvent>();
        uint last = uint.MaxValue;
        void Observe() { if (h.Server.World.Tick != last) { last = h.Server.World.Tick; hits.AddRange(h.Server.World.LastHits); } }
        h.Run(TimeSpan.FromSeconds(1.5), null, Observe);
        var shooter = h.Clients[0]; var target = h.Clients[1];
        int sid = shooter.PlayerId, tid = target.PlayerId;
        var sPos = h.Server.World.Players[sid].Position;
        uint start = shooter.Tick + 30;

        PlayerInput Input(int i, uint t)
        {
            if (i == 1)
            {
                // Facing the shooter and strafing across the line of fire, reversing every 1.5 s.
                var u = sPos - target.Predicted.Position;
                return new PlayerInput(t, (t / 45) % 2 == 0 ? 1f : -1f, 0, MathF.Atan2(-u.X, u.Y), InputButtons.None);
            }
            if (t < start) return new PlayerInput(t, 0, 0, 0, InputButtons.None);
            var seen = shooter.RemotePlayers().Players.Where(p => p.PlayerId == tid).ToList();
            if (seen.Count == 0) return new PlayerInput(t, 0, 0, 0, InputButtons.None);
            var point = HitVolumes.ToWorld(aim, seen[0].Position, seen[0].Yaw);
            var to = point - Combat.EyePosition(shooter.Predicted, config);
            return new PlayerInput(t, 0, 0, MathF.Atan2(-to.X, to.Y), InputButtons.Fire, MathF.Atan2(to.Z, new Vector2(to.X, to.Y).Length())).WithShotDirection(to);
        }
        var shots = new List<uint>();
        h.Run(TimeSpan.FromSeconds(8), Input, () =>
        {
            Observe();
            foreach (var s in h.Server.World.LastShots) if (s.Shooter == sid && (shots.Count == 0 || shots[^1] != s.Tick)) shots.Add(s.Tick);
        });
        var mine = hits.Where(x => x.Shooter == sid && x.Target == tid).GroupBy(x => x.Tick).ToDictionary(g => g.Key, g => g.First().Zone);
        int hit = shots.Count(mine.ContainsKey), right = shots.Count(s => mine.TryGetValue(s, out var z) && z == expected);
        output.WriteLine($"RTT {rttMs} ms, aim at {where}: {hit}/{shots.Count} hits, {right} in the expected zone {expected}");
        Assert.True(shots.Count >= 40, $"only {shots.Count} shots");
        if (expected == HitZone.None) Assert.True(hit <= shots.Count * 0.03, $"{hit} hits above the helmet");
        else Assert.True(right >= shots.Count * 0.97, $"{right}/{shots.Count} in zone {expected}");
    }
}
