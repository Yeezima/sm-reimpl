using SmContent;
using SmFormats.Fx;

namespace SmFormats.Tests;

/// <summary>
/// M41 (review fixes): the dropper and the effect's collision life, malformed collision descriptions, the pound sound's once-per-pound rule and the ring texture's pixels.
/// Synthetic data only.
/// </summary>
public class GroundEffectsRulesTests
{
    // ---- droppers and the collision life ----

    [Fact]
    public void A_spray_with_a_collision_response_and_a_collider_holding_descriptions_is_a_dropper()
    {
        var fx = FxSystem.Read("test_dropper", SyntheticFxWriter.DropperSystem());
        var spray = Assert.IsType<FxSpray>(Assert.Single(fx.Emitters).Elements[0]);
        Assert.True(spray.IsDropper);
        Assert.False(Assert.IsType<FxSpray>(FxSystem.Read("t", SyntheticFxWriter.TracerSystem()).Emitters[0].Elements[0]).IsDropper);   // no response, no descriptions
    }

    [Fact]
    public void The_collision_life_is_that_of_the_started_sprites_not_of_the_decals()
    {
        var fx = FxSystem.Read("test_dropper", SyntheticFxWriter.DropperSystem());
        // The combo (0.1 s, plus its epsilon 0.05) holds a decal of 8 s (left out) and a dust ring of 2 s (plus 0.05).
        Assert.Equal(0.15f + 2.05f, fx.CollisionLife, 4);
        Assert.Equal(0f, FxSystem.Read("t", SyntheticFxWriter.TracerSystem()).CollisionLife);   // nothing starts on a collision
    }

    [Fact]
    public void Malformed_collision_descriptions_are_skipped_and_noted_not_thrown()
    {
        var fx = FxSystem.Read("test_malformed", SyntheticFxWriter.MalformedCollisionSystem(3));
        var holder = Assert.IsType<FxRing>(fx.Emitters[0].Elements[0]);
        // The number in the list is skipped; the unknown class is kept (and noted) with its own descriptions.
        var mystery = Assert.IsType<FxUnknownElement>(Assert.Single(holder.OnCollision));
        Assert.Equal("inside the mystery", Assert.Single(mystery.OnCollision).Name);
        Assert.Contains(fx.Notes, n => n.Contains("FXFutureDescription"));
        // The chain of three is read to its end.
        var link = fx.Emitters[0].Elements[1];
        var names = new List<string>();
        for (var e = link; e is not null; e = e.OnCollision.FirstOrDefault()) names.Add(e.Name);
        Assert.Equal(["link3", "link2", "link1", "end"], names);
        Assert.True(fx.CollisionLife > 0f);
    }

    [Fact]
    public void A_deep_chain_of_collision_descriptions_reads_and_an_absurd_one_is_refused_by_the_file_reader()
    {
        // Each link nests several BOD values (struct, list, item): the document reader's own limit (64) refuses a chain far past the install's deepest tree (11 levels) before the model's.
        var fx = FxSystem.Read("test_deep", SyntheticFxWriter.MalformedCollisionSystem(8));
        int depth = 0;
        for (var e = fx.Emitters[0].Elements[1]; e is not null; e = e.OnCollision.FirstOrDefault()) depth++;
        Assert.Equal(9, depth);
        Assert.True(fx.CollisionLife > 0f);
        // The loaders (FxLibrary.Systems, the client's CompileSystems) catch this and note the effect as not parsing.
        Assert.Throws<InvalidDataException>(() => FxSystem.Read("test_absurd", SyntheticFxWriter.MalformedCollisionSystem(60)));
    }

    // ---- the pound sound ----

    /// <summary>Runs states through the rule: (pounding, grounded) per step; returns the number of posts.</summary>
    static int Posts(params (bool Pounding, bool Grounded)[] steps)
    {
        int posts = 0;
        bool sounded = false;
        var prev = (Pounding: false, Grounded: true);
        foreach (var s in steps)
        {
            var (post, now) = PoundSoundRules.Step(s.Pounding, s.Grounded, prev.Pounding, prev.Grounded, sounded);
            if (post) posts++;
            sounded = now;
            prev = s;
        }
        return posts;
    }

    static readonly (bool, bool) Air = (false, false), Ground = (false, true), Dive = (true, false), Recover = (true, true);

    [Fact]
    public void A_pound_seen_to_land_sounds_once_at_the_impact_and_not_again_when_it_ends()
    {
        // Own player: the dive, then the recovery for several ticks, then the pound is over (the bug: a second post here).
        Assert.Equal(1, Posts(Air, Dive, Dive, Recover, Recover, Recover, Ground, Ground));
        // The first post is at the first grounded pounding state, not at the end.
        var (post, sounded) = PoundSoundRules.Step(true, true, true, false, false);
        Assert.True(post); Assert.True(sounded);
        (post, sounded) = PoundSoundRules.Step(true, true, true, true, sounded);
        Assert.False(post); Assert.True(sounded);
        (post, sounded) = PoundSoundRules.Step(false, true, true, true, sounded);   // the end
        Assert.False(post); Assert.False(sounded);
    }

    [Fact]
    public void A_remote_pound_with_the_flag_and_the_ground_sounds_once_too()
    {
        // Slim records: the pound flag stays on from the dive to the end of the recovery; a snapshot gap may skip states.
        Assert.Equal(1, Posts(Air, Dive, Recover, Ground));
        Assert.Equal(1, Posts(Air, Dive, Recover, Recover, Recover, Ground));
        Assert.Equal(1, Posts(Dive, Recover));   // the player first seen pounding in the air, then on the ground
    }

    [Fact]
    public void A_pound_whose_impact_was_never_seen_sounds_once_when_it_ends()
    {
        Assert.Equal(1, Posts(Air, Dive, Dive, Ground));      // the dive straight to the end
        Assert.Equal(1, Posts(Air, Dive, Air));               // lost in the air (ended by a death or a gap)
    }

    [Fact]
    public void Each_pound_sounds_once_and_a_pound_needs_no_prior_ground()
    {
        Assert.Equal(2, Posts(Air, Dive, Recover, Recover, Ground, Air, Dive, Recover, Ground));
        Assert.Equal(0, Posts(Air, Air, Ground, Ground));       // no pound, no sound
        Assert.Equal(1, Posts(Recover, Recover, Ground));       // a player first seen already recovering
    }

    // ---- the ring texture ----

    [Fact]
    public void The_ring_radius_is_clamped_to_one_to_thirty_metres()
    {
        Assert.Equal(7f, PoundRingTexture.ClampRadius(7f));
        Assert.Equal(1f, PoundRingTexture.ClampRadius(0.2f));
        Assert.Equal(30f, PoundRingTexture.ClampRadius(500f));
        Assert.Equal(0f, PoundRingTexture.ClampRadius(0f));
        Assert.Equal(0f, PoundRingTexture.ClampRadius(-3f));
        Assert.Equal(0f, PoundRingTexture.ClampRadius(float.NaN));
        Assert.Equal(0f, PoundRingTexture.ClampRadius(float.PositiveInfinity));
        Assert.Empty(PoundRingTexture.Render(float.NaN));
        Assert.Equal(PoundRingTexture.SizeFor(30f), PoundRingTexture.SizeFor(PoundRingTexture.ClampRadius(900f)));
    }

    [Fact]
    public void The_ring_pixels_are_a_dark_band_at_the_radius_and_clear_elsewhere()
    {
        const float radius = 7f;
        var data = PoundRingTexture.Render(radius);
        int n = PoundRingTexture.Pixels;
        Assert.Equal(n * n * 4, data.Length);
        float size = PoundRingTexture.SizeFor(radius);
        float RadiusOf(int px, int py) { float x = (px + 0.5f) / n * size - size / 2f, y = (py + 0.5f) / n * size - size / 2f; return MathF.Sqrt(x * x + y * y); }

        int maxAlpha = 0, maxColour = 0, bandPixels = 0, farOpaque = 0;
        float bandAlphaSum = 0f;
        for (int py = 0; py < n; py++)
            for (int px = 0; px < n; px++)
            {
                int o = (py * n + px) * 4;
                int a = data[o + 3];
                maxAlpha = Math.Max(maxAlpha, a);
                maxColour = Math.Max(maxColour, Math.Max(data[o], Math.Max(data[o + 1], data[o + 2])));
                float r = RadiusOf(px, py);
                if (MathF.Abs(r - radius) < 0.12f) { bandPixels++; bandAlphaSum += a; }
                if (MathF.Abs(r - radius) > 2.3f && a != 0) farOpaque++;
            }
        Assert.Equal(0, farOpaque);                                         // nothing at the centre or the corners
        Assert.InRange(maxAlpha, 100, (int)(PoundRingTexture.MaxAlpha * 255f) + 1);   // visible but never solid
        Assert.True(maxColour <= (int)(PoundRingTexture.MaxShade * 255f), $"colour {maxColour}");   // dark: no bright colour at all
        Assert.True(bandAlphaSum / bandPixels > 20f);                       // the band at the radius is the ring
        Assert.Equal(data, PoundRingTexture.Render(radius));                // deterministic: the same ring every time
    }

    [Fact]
    public void The_slash_mark_is_a_dark_wedge_of_the_hit_cone_toward_the_top_and_nothing_behind_or_outside()
    {
        var px = SlashMarkTexture.Render(4f, 60f);
        int n = SlashMarkTexture.Pixels;
        Assert.Equal(n * n * 4, px.Length);
        float size = SlashMarkTexture.SizeFor(4f);
        byte A(float ahead, float right)
        {
            int x = (int)((right + size / 2f) / size * n), y = (int)((-ahead + size / 2f) / size * n);
            return px[(y * n + x) * 4 + 3];
        }
        int inside = 0;
        for (float r = 0.5f; r <= 4f; r += 0.05f) if (A(r, 0f) > 0) inside++;
        Assert.True(inside > 20, $"{inside} marked samples along the heading");
        Assert.True(A(4f, 0f) > 40);                       // the rim at the reach
        for (float r = 0.5f; r <= 4.25f; r += 0.25f)
        {
            Assert.Equal(0, A(-r, 0f));                    // nothing behind the attacker
            Assert.Equal(0, A(r * 0.2f, r));              // nothing at 79 degrees off the heading (outside the 60 degree cone and its soft edge)
        }
        Assert.Equal(0, A(4.45f, 0f));                     // nothing past the reach (the rim fades out by 4.35 m)
        for (int i = 0; i < px.Length; i += 4)
        {
            Assert.True(px[i + 3] <= (byte)(255f * SlashMarkTexture.MaxAlpha) + 1);
            Assert.True(px[i] <= (byte)(255f * SlashMarkTexture.MaxShade) + 1);
        }
    }
}
