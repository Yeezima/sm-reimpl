using System.Numerics;
using SmNet.Extras.Musou;
using SmSim.Extras.Musou;

namespace SmNet.Tests.Extras.Musou;

/// <summary>
/// Corpse arcs (the owner, 2026-11-08: "completely remove the corkscrewing corpses flying in the air in musou mode. corpses should never fly in a way that is not a natural looking straight line
/// parabolic arc away from the explosion"): a thrown body leaves away from the blast, flies one parabola whose ground track is a straight line, and tips over about one fixed horizontal axis by at
/// most a quarter turn, never spinning about its own long axis.
/// </summary>
public class MusouFlingTests
{
    static IEnumerable<(Vector3 Push, Vector3 V)> Throws()
    {
        var pushes = new[] { new Vector3(1f, 0f, 0.3f), new Vector3(0f, -1f, 0.3f), Vector3.Normalize(new Vector3(-0.6f, 0.8f, 0.3f)) };
        foreach (var push in pushes)
            for (ushort id = 1; id <= 300; id++)
                foreach (var type in new[] { MusouEnemyType.Boy, MusouEnemyType.Gretchin, MusouEnemyType.Nob, MusouEnemyType.Warboss })
                    yield return (push, MusouGoreRules.BlastThrow(push, id, 500u + id, type));
    }

    [Fact]
    public void A_throw_leaves_away_from_the_blast_and_up()
    {
        foreach (var (push, v) in Throws())
        {
            var away = Vector2.Normalize(new Vector2(push.X, push.Y));
            var flat = new Vector2(v.X, v.Y);
            Assert.True(v.Z > 0f);
            Assert.True(Vector2.Dot(Vector2.Normalize(flat), away) >= MathF.Cos((MusouGoreRules.ThrowSpreadDegrees + 0.01f) * MathF.PI / 180f), "within the spread of straight away");
        }
    }

    [Fact]
    public void The_tumble_is_one_horizontal_axis_square_to_the_flight_head_leading_away_and_capped()
    {
        foreach (var (_, v) in Throws())
        {
            var t = MusouFling.Tumble(v);
            Assert.Equal(0f, t.Z, 5);   // horizontal: never a spin about the body's own (upright) long axis
            Assert.InRange(MathF.Abs(Vector3.Dot(Vector3.Normalize(t), Vector3.Normalize(new Vector3(v.X, v.Y, 0f)))), 0f, 1e-4f);   // square to the flight: no roll, no corkscrew
            Assert.InRange(t.Length(), 0f, MusouFling.TumbleMaxRate + 1e-4f);
            var head = Vector3.Transform(Vector3.UnitZ, Quaternion.CreateFromAxisAngle(Vector3.Normalize(t), 0.3f));
            Assert.True(Vector2.Dot(new Vector2(head.X, head.Y), new Vector2(v.X, v.Y)) > 0f, "the head tips the way it flies, away from the blast");
            for (float s = 0f; s <= 10f; s += 0.25f) Assert.InRange(MusouFling.TumbleAngle(t, s), 0f, MusouFling.TumbleMaxAngle + 1e-5f);
        }
        Assert.Equal(Vector3.Zero, MusouFling.Tumble(new Vector3(0f, 0f, 12f)));
    }

    [Fact]
    public void The_flight_is_one_parabola_with_a_straight_monotonic_ground_track()
    {
        foreach (var (_, v0) in Throws())
        {
            var p = Vector3.Zero; var v = v0;
            var dir = Vector2.Normalize(new Vector2(v0.X, v0.Y));
            float along = 0f, peak = 0f; bool falling = false;
            const float dt = 1f / 60f;
            for (int i = 0; i < 600 && (i == 0 || p.Z > 0f); i++)
            {
                MusouFling.Fly(ref p, ref v, dt);
                var flat = new Vector2(p.X, p.Y);
                float now = Vector2.Dot(flat, dir);
                Assert.True(now > along, "horizontal travel only ever grows");
                Assert.InRange(MathF.Abs(dir.X * flat.Y - dir.Y * flat.X), 0f, 1e-3f);   // on one straight line
                Assert.Equal(new Vector2(v0.X, v0.Y), new Vector2(v.X, v.Y));   // the flat velocity never changes in the air
                if (p.Z < peak - 1e-5f) falling = true;
                else Assert.False(falling, "one arc: once it falls it never rises again");
                peak = MathF.Max(peak, p.Z);
                along = now;
            }
            Assert.InRange(peak, v0.Z * v0.Z / (2f * MusouFling.Gravity) - 0.2f, v0.Z * v0.Z / (2f * MusouFling.Gravity) + 0.2f);
        }
    }
}
