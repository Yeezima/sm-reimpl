using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using SmSim.Extras.Musou.Ashfall;
using SmSim.Extras.Warfront.Fortress;
using static SmSim.Extras.Warfront.Fortress.WarfrontFortressMap;

namespace SmClient.Extras.Warfront.Dress;

/// <summary>
/// The dressing of the Ork fortress as data (slice "dress", part 1; every size ours): which rule each level part gets (<see cref="Classify"/>, <see cref="DressRule"/>) and the pieces on and beside them: scrap spikes and merlons on the walls,
/// the gatehouse towers and the skull board over the gate, rubble and bent girders at the breach (nothing in its 8 m gap), glyph boards on the street blocks and the shell, scrap towers on the plaza pillars, wrecks on the cover walls,
/// totems with ground sigils at the elite posts, a ring of banners and the boss dais in the plaza. Visual only: no piece collides, none stands in the gate's, the breach's or the arena openings' span, and nothing is added to the level or its hash.
/// Kit models (the bands' own set, <see cref="AshfallKit"/>) are the rubble at the breach and the conquered relics against the boundary.
/// </summary>
public static class WarfrontFortressDressPlan
{
    /// <summary>The rule of a level part, or null when the dressing has none (a test fails on a null: a later map change cannot bring a grey box back).</summary>
    public static DressRule? Classify(AshfallPart part)
    {
        var (mn, mx) = Aabb(part);
        float dx = mx.X - mn.X, dy = mx.Y - mn.Y;
        switch (part.Kind)
        {
            case AshfallKind.Ground: return DressRule.Ground;
            case AshfallKind.Boundary: return DressRule.Boundary;
            case AshfallKind.Wall:
                if (mn.Z >= 10f) return DressRule.GateLintel;
                return mx.Z >= 30f ? DressRule.RockCliff : DressRule.CompoundWall;
            case AshfallKind.Stronghold:
                if (dx <= 4.5f && dy <= 4.5f && mx.Z >= 10f) return DressRule.ScrapTower;
                return MathF.Min(dx, dy) <= ShellWall + 0.5f ? DressRule.ArenaShell : DressRule.StreetBlock;
            case AshfallKind.Cover: return DressRule.WreckCover;
            default: return null;
        }
    }

    /// <summary>The axis-aligned bounds of a part's corners.</summary>
    public static (Vector3 Min, Vector3 Max) Aabb(AshfallPart part)
    {
        var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
        foreach (var c in part.Corners) { mn = Vector3.Min(mn, c); mx = Vector3.Max(mx, c); }
        return (mn, mx);
    }

    /// <summary>The breach's span (the 8 m gap, its full depth and height) and the margin the dressing keeps from it (metres).</summary>
    public static (Vector3 Min, Vector3 Max) BreachGap => (new Vector3(BreachX0, GateSouth, 0f), new Vector3(BreachX1, GateNorth, WallTop));
    /// <summary>The distance every piece and kit item keeps from the breach's gap, and from the gate's and the arena openings' spans (m; ours).</summary>
    public const float OpeningMargin = 0.5f;

    // ---- the pieces, built by the helpers below
    sealed class Acc
    {
        public readonly List<DressPiece> Pieces = [];
        public void Box(DressMat m, float cx, float cy, float z, float sx, float sy, float sz, float yaw = 0f, float shade = 0.5f) => Pieces.Add(new(DressShape.Box, m, new(cx, cy, z), new(sx, sy, sz), yaw, null, shade));
        public void Spike(DressMat m, float cx, float cy, float z, float d, float len, Vector3? axis = null, float shade = 0.5f) => Pieces.Add(new(DressShape.Spike, m, new(cx, cy, z), new(d, d, len), 0f, axis, shade));
        public void Pole(DressMat m, float cx, float cy, float z, float d, float len, Vector3? axis = null, float shade = 0.5f) => Pieces.Add(new(DressShape.Pole, m, new(cx, cy, z), new(d, d, len), 0f, axis, shade));
        public void Disc(DressMat m, float cx, float cy, float z, float d, float t, float shade) => Pieces.Add(new(DressShape.Disc, m, new(cx, cy, z), new(d, d, t), 0f, null, shade));

        /// <summary>A piece placed in the frame of a wall face: origin on the face, <paramref name="n"/> the face's outward normal; <paramref name="along"/> along the face, <paramref name="proud"/> out from it.</summary>
        public void OnFace(DressMat m, Vector2 origin, Vector2 n, float along, float proud, float z, float sAlong, float sOut, float sz, float shade = 0.5f)
        {
            var t = new Vector2(n.Y, -n.X);   // along the face (a quarter turn clockwise of the normal)
            var p = origin + t * along + n * proud;
            Box(m, p.X, p.Y, z, sAlong, sOut, sz, MathF.Atan2(-n.X, n.Y), shade);
        }

        /// <summary>A glyph board on a face: a red board with the Ork skull (bone, black eyes, nose and teeth, two tusks), facing out of the face by <paramref name="n"/>.</summary>
        public void Glyph(Vector2 origin, Vector2 n, float along, float z, float width, float shade)
        {
            float h = width * 0.62f;
            OnFace(DressMat.ClothRed, origin, n, along, 0.16f, z, width, 0.3f, h, shade);
            OnFace(DressMat.Bone, origin, n, along, 0.42f, z + h * 0.18f, width * 0.46f, 0.3f, h * 0.62f);
            OnFace(DressMat.ClothBlack, origin, n, along - width * 0.11f, 0.62f, z + h * 0.46f, width * 0.12f, 0.2f, h * 0.2f);
            OnFace(DressMat.ClothBlack, origin, n, along + width * 0.11f, 0.62f, z + h * 0.46f, width * 0.12f, 0.2f, h * 0.2f);
            OnFace(DressMat.ClothBlack, origin, n, along, 0.62f, z + h * 0.34f, width * 0.05f, 0.2f, h * 0.12f);
            for (int k = -2; k <= 2; k++) OnFace(DressMat.ClothBlack, origin, n, along + k * width * 0.075f, 0.62f, z + h * 0.2f, width * 0.03f, 0.2f, h * 0.14f);
            foreach (float s in new[] { -1f, 1f })
            {
                var t = new Vector2(n.Y, -n.X);
                var p = origin + t * (along + s * width * 0.2f) + n * 0.6f;
                Spike(DressMat.Bone, p.X, p.Y, z + h * 0.3f, width * 0.07f, h * 0.3f, new Vector3(0f, 0f, -1f) + new Vector3(n.X, n.Y, 0f) * 0.25f);
            }
        }
    }

    /// <summary>The dressing plan of the fortress level: the rule of every part, the pieces, the kit items and the parts the kit look must not draw.</summary>
    public static DressPlan Build(AshfallLevel level, IReadOnlyList<WarfrontArenaPost> posts)
    {
        var rules = new List<DressRule>();
        var hidden = new List<int>();
        var parts = new List<(DressRule Rule, Vector3 Min, Vector3 Max)>();
        for (int i = 0; i < level.Parts.Count; i++)
        {
            var rule = Classify(level.Parts[i]);
            if (rule is null) { rules.Add(DressRule.Ground); continue; }   // a part without a rule is the test's failure; the drawing keeps the kit's ground look for it
            rules.Add(rule.Value);
            var (mn, mx) = Aabb(level.Parts[i]);
            parts.Add((rule.Value, mn, mx));
            if (rule.Value is not (DressRule.Ground or DressRule.Boundary)) hidden.Add(i);
        }
        var a = new Acc();
        const float Top = WallTop;

        // ---- the south wall (y -100 outside .. -92 inside): spikes on the top and the face, merlons on the inner edge, the gatehouse, the breach
        for (float x = -98f; x <= 98.1f; x += 4.5f)
        {
            if (MathF.Abs(x) < GateWidth / 2f + 3f || (x > BreachX0 - 5f && x < BreachX1 + 5f)) continue;
            float u = DressHash.Unit(11, (uint)(x * 10f + 2000f));
            a.Spike(DressMat.Iron, x, GateSouth + 0.7f, Top, 0.6f, 2.2f + 1.4f * u, new Vector3(0f, -0.3f, 1f), u);
            a.Box(DressMat.Plated, x, GateNorth + 0.6f, Top, 2.4f, 1.2f, 1.2f + 0.5f * u, 0f, u);   // a merlon on the inner edge
        }
        for (float x = -95f; x <= 95f; x += 9f)
        {
            if (MathF.Abs(x) < 12f || (x > BreachX0 - 7f && x < BreachX1 + 7f)) continue;
            a.Spike(DressMat.Iron, x, GateSouth, 10f, 0.7f, 2.8f, new Vector3(0f, -1f, 0.3f), 0.3f);
        }
        // the gate: two gatehouse towers on the wall top, the skull board between them, a frame of beams on the face round the 12 m opening
        foreach (float sx in new[] { -1f, 1f })
        {
            float tx = sx * 11.5f, ty = (GateSouth + GateNorth) / 2f;
            a.Box(DressMat.Plated, tx, ty, Top, 8f, 7.6f, 12f, 0f, 0.35f);
            a.Box(DressMat.Iron, tx, ty, Top + 12f, 5.6f, 5.6f, 0.8f);
            a.Spike(DressMat.Glow, tx, ty, Top + 12.8f, 3f, 3.6f, null, 0.6f);
            foreach (float cx in new[] { -1f, 1f })
                foreach (float cy in new[] { -1f, 1f })
                    a.Spike(DressMat.Iron, tx + cx * 2.6f, ty + cy * 2.6f, Top + 12.8f, 0.6f, 2.6f, new Vector3(cx * 0.35f, cy * 0.35f, 1f));
            a.Box(DressMat.Iron, sx * (GateWidth / 2f + 1.3f), GateSouth - 0.25f, 0f, 2.6f, 0.5f, GateClear);   // the jamb beam on the wall's face
        }
        a.Box(DressMat.Iron, 0f, GateSouth - 0.25f, GateClear, GateWidth + 5.2f, 0.5f, 1.3f);   // the head beam on the lintel's face (no lower than the lintel)
        a.Glyph(new(0f, GateSouth - 0.3f), new(0f, -1f), 0f, Top + 0.4f, 11f, 0.5f);
        // the breach: bent girders out of the broken ends (leaning away from the gap), rubble shards on the stumps; nothing between x -68 and -60
        foreach (var (edge, away) in new[] { (BreachX0 - 0.7f, -1f), (BreachX1 + 0.7f, 1f) })
        {
            for (int k = 0; k < 3; k++)
            {
                float y = GateSouth + 1.5f + k * 2.6f, z = 9f + k * 2f;
                a.Pole(DressMat.Iron, edge, y, z, 0.35f, 6f + k, new Vector3(away * 0.45f, 0.1f, 1f));
            }
            a.Box(DressMat.Plated, edge + away * 2.4f, (GateSouth + GateNorth) / 2f, Top, 1.8f, 2.6f, 2.2f, 0.4f, 0.8f);
            a.Box(DressMat.Plated, edge + away * 4.6f, (GateSouth + GateNorth) / 2f + 0.8f, Top, 1.4f, 2f, 1.3f, -0.3f, 0.2f);
        }
        // hanging banners flat on the wall's face (not in the gate's or the breach's span)
        foreach (float sx in new[] { -1f, 1f })
            foreach (float x0 in new[] { 22f, 34f, 46f, 58f, 70f, 82f, 94f })
            {
                float x = sx * x0;
                if (x > BreachX0 - 8f && x < BreachX1 + 8f) continue;
                a.Box(DressMat.ClothRed, x, GateSouth - 0.12f, 3.2f, 2.6f, 0.14f, 7.2f, 0f, DressHash.Unit(21, (uint)(x + 200f)));
                a.Box(DressMat.ClothBlack, x, GateSouth - 0.22f, 5.2f, 1.2f, 0.1f, 1.8f);
            }

        // ---- the north wall behind the arena (y 101 .. 130) and the cliffs' faces
        for (float x = -98f; x <= 98.1f; x += 6f)
            a.Spike(DressMat.Iron, x, ShellNorth + 0.8f, Top, 0.7f, 2.4f, new Vector3(0f, -0.25f, 1f), DressHash.Unit(31, (uint)(x + 200f)));
        foreach (float sx in new[] { -1f, 1f })
        {
            a.Box(DressMat.ClothRed, sx * 75f, ShellNorth - 0.1f, 4f, 3f, 0.14f, 10f, 0f, 0.3f);
            a.Box(DressMat.ClothBlack, sx * 75f, ShellNorth - 0.2f, 8.5f, 1.4f, 0.1f, 2f);
            foreach (float y in new[] { 20f, 60f, 98f })
                a.Glyph(new(sx * C, y), new(-sx, 0f), 0f, 9f, 11f, DressHash.Unit(41, (uint)(y + sx * 100f + 300f)));   // a big board on each cliff's inner face
        }

        // ---- the street blocks: chimneys, roof spikes, a glyph board on every face
        foreach (var (rule, mn, mx) in parts.Where(p => p.Rule == DressRule.StreetBlock))
        {
            float cx = (mn.X + mx.X) / 2f, cy = (mn.Y + mx.Y) / 2f, dx = mx.X - mn.X, dy = mx.Y - mn.Y;
            uint s = (uint)(MathF.Abs(cx) * 7f + (cy + 200f) * 13f);
            a.Pole(DressMat.Iron, mn.X + 3f, mn.Y + 3f, BlockTop, 1.5f, 4.5f + 3f * DressHash.Unit(51, s), null, 0.4f);
            a.Pole(DressMat.Iron, mx.X - 3f, mx.Y - 3f, BlockTop, 1.2f, 3.5f + 2f * DressHash.Unit(52, s), null, 0.6f);
            a.Spike(DressMat.Iron, mn.X + 3f, mn.Y + 3f, BlockTop + 4.5f, 1.8f, 1.2f);
            for (float x = mn.X + 2f; x < mx.X - 1f; x += 4f) { a.Spike(DressMat.Iron, x, mn.Y + 0.5f, BlockTop, 0.5f, 1.8f, new Vector3(0f, -0.2f, 1f)); a.Spike(DressMat.Iron, x, mx.Y - 0.5f, BlockTop, 0.5f, 1.8f, new Vector3(0f, 0.2f, 1f)); }
            float h1 = DressHash.Unit(53, s), h2 = DressHash.Unit(54, s);
            a.Glyph(new(cx, mn.Y), new(0f, -1f), 0f, 5f, MathF.Min(8f, dx - 6f), h1);
            a.Glyph(new(cx, mx.Y), new(0f, 1f), 0f, 5f, MathF.Min(8f, dx - 6f), h2);
            a.Glyph(new(mn.X, cy), new(-1f, 0f), 0f, 5f, MathF.Min(8f, dy - 6f), h2);
            a.Glyph(new(mx.X, cy), new(1f, 0f), 0f, 5f, MathF.Min(8f, dy - 6f), h1);
        }

        // ---- the arena shell: spikes along the top, boards on every long face, frames of beams round the openings (no lintel)
        foreach (var (rule, mn, mx) in parts.Where(p => p.Rule == DressRule.ArenaShell))
        {
            bool alongX = mx.X - mn.X >= mx.Y - mn.Y;
            float len = alongX ? mx.X - mn.X : mx.Y - mn.Y;
            for (float t = 1.5f; t < len - 1f; t += 3f)
            {
                float x = alongX ? mn.X + t : (mn.X + mx.X) / 2f, y = alongX ? (mn.Y + mx.Y) / 2f : mn.Y + t;
                a.Spike(DressMat.Iron, x, y, ShellTop, 0.7f, 2.2f, null, DressHash.Unit(61, (uint)(x * 3f + y * 7f + 900f)));
            }
            var faces = alongX
                ? new[] { (new Vector2((mn.X + mx.X) / 2f, mn.Y), new Vector2(0f, -1f), len), (new Vector2((mn.X + mx.X) / 2f, mx.Y), new Vector2(0f, 1f), len) }
                : new[] { (new Vector2(mn.X, (mn.Y + mx.Y) / 2f), new Vector2(-1f, 0f), len), (new Vector2(mx.X, (mn.Y + mx.Y) / 2f), new Vector2(1f, 0f), len) };
            foreach (var (origin, n, flen) in faces)
            {
                int count = Math.Max(1, (int)(flen / 15f));
                for (int k = 0; k < count; k++)
                    a.Glyph(origin, n, (k - (count - 1) / 2f) * (flen / count), 5.2f, 7f, DressHash.Unit(62, (uint)(origin.X * 5f + origin.Y * 11f + k * 3f + 800f)));
            }
        }
        float so = SouthOpening / 2f, sd = SideOpening / 2f;
        foreach (float sx in new[] { -1f, 1f })
        {
            a.Box(DressMat.Iron, sx * (so + 0.7f), ShellSouth - 0.25f, 0f, 1.4f, 0.5f, ShellTop);   // the south opening's beams: outer face
            a.Box(DressMat.Iron, sx * (so + 0.7f), ShellSouth + ShellWall + 0.25f, 0f, 1.4f, 0.5f, ShellTop);   // and the plaza face
            foreach (float yy in new[] { PlazaCentreY - sd - 0.7f, PlazaCentreY + sd + 0.7f })
            {
                a.Box(DressMat.Iron, sx * (ShellHalf + 0.25f), yy, 0f, 0.5f, 1.4f, ShellTop);
                a.Box(DressMat.Iron, sx * (ShellHalf - ShellWall - 0.25f), yy, 0f, 0.5f, 1.4f, ShellTop);
            }
        }

        // ---- the plaza: scrap towers on the four pillars, wrecks on the cover walls, the boss dais, the ring of banners
        foreach (var (rule, mn, mx) in parts.Where(p => p.Rule == DressRule.ScrapTower))
        {
            float cx = (mn.X + mx.X) / 2f, cy = (mn.Y + mx.Y) / 2f, t = mx.Z;
            a.Box(DressMat.Iron, cx, cy, 3.5f, 3.7f, 3.7f, 0.5f); a.Box(DressMat.Iron, cx, cy, 7.5f, 3.7f, 3.7f, 0.5f);
            a.Box(DressMat.Iron, cx, cy, t, 4.4f, 4.4f, 0.7f);
            a.Spike(DressMat.Glow, cx, cy, t + 0.7f, 2.6f, 3.4f, null, 0.5f);
            foreach (float qx in new[] { -1f, 1f })
                foreach (float qy in new[] { -1f, 1f })
                    a.Spike(DressMat.Iron, cx + qx * 1.9f, cy + qy * 1.9f, t + 0.7f, 0.5f, 2.4f, new Vector3(qx * 0.35f, qy * 0.35f, 1f));
            float face = cx < 0f ? mx.X : mn.X, sgn = cx < 0f ? 1f : -1f;   // the face toward the Warboss's spot
            a.Box(DressMat.ClothRed, face + sgn * 0.12f, cy, 4.6f, 0.12f, 2.2f, 6.2f, 0f, 0.7f);
        }
        foreach (var (rule, mn, mx) in parts.Where(p => p.Rule == DressRule.WreckCover))
        {
            float cx = (mn.X + mx.X) / 2f, cy = (mn.Y + mx.Y) / 2f, dx = mx.X - mn.X, dy = mx.Y - mn.Y, h = mx.Z;
            bool alongX = dx >= dy;
            float u = DressHash.Unit(71, (uint)(MathF.Abs(cx) * 9f + (cy + 300f) * 5f));
            var along = alongX ? Vector3.UnitX : Vector3.UnitY;
            float wd = MathF.Min(1.5f, MathF.Min(dx, dy) + 0.3f);
            // a wheel lying on its side on the wreck, an antenna and a bent plate (all inside the wreck's own footprint)
            var wp = new Vector3(cx, cy, h) + along * (dx >= dy ? dx : dy) * 0.28f;
            a.Pole(DressMat.Iron, wp.X, wp.Y, wp.Z, MathF.Min(1.4f, wd), 0.5f, null, 0.3f);
            var ap = new Vector3(cx, cy, h) - along * (dx >= dy ? dx : dy) * 0.36f;
            a.Pole(DressMat.Iron, ap.X, ap.Y, ap.Z, 0.14f, 2.2f + u, new Vector3(0.1f, 0f, 1f));
            a.Box(DressMat.Wreck, cx - along.X * 0.5f, cy - along.Y * 0.5f, h, alongX ? 2.2f : wd * 0.8f, alongX ? wd * 0.8f : 2.2f, 0.3f, 0f, u);
        }
        var boss = new Vector2(0f, PlazaCentreY);
        a.Disc(DressMat.Dais, boss.X, boss.Y, 0f, 13f, 0.02f, 0.9f);
        a.Disc(DressMat.Dais, boss.X, boss.Y, 0f, 12f, 0.04f, 0.1f);
        a.Disc(DressMat.Dais, boss.X, boss.Y, 0f, 8.4f, 0.06f, 0.9f);
        a.Disc(DressMat.Dais, boss.X, boss.Y, 0f, 7.6f, 0.08f, 0.1f);
        a.Disc(DressMat.Dais, boss.X, boss.Y, 0f, 2.4f, 0.10f, 0.9f);
        for (int k = 0; k < 8; k++)
        {
            float ang = k * MathF.PI / 4f + MathF.PI / 8f, r = 9.6f;
            a.Box(DressMat.Dais, boss.X + MathF.Cos(ang) * r, boss.Y + MathF.Sin(ang) * r, 0.04f, 3.4f, 0.5f, 0.06f, ang, 0.9f);
        }
        for (int k = 0; k < 24; k++)
        {
            float ang = k * MathF.PI / 12f, r = 42.5f;
            float x = boss.X + MathF.Cos(ang) * r, y = boss.Y + MathF.Sin(ang) * r;
            if ((MathF.Abs(y - PlazaCentreY) < 9f && MathF.Abs(x) > 30f) || (MathF.Abs(x) < 11f && y < PlazaCentreY - 25f)) continue;   // never in an opening's line
            float yaw = MathF.Atan2(-(boss.X - x), boss.Y - y);   // the cloth's face toward the dais
            a.Pole(DressMat.Iron, x, y, 0f, 0.35f, 10f, null, 0.4f);
            a.Box(DressMat.ClothRed, x, y, 3.6f, 2.4f, 0.1f, 5.6f, yaw, DressHash.Unit(81, (uint)k));
            a.Box(DressMat.ClothBlack, x, y, 6.6f, 1.1f, 0.08f, 1.6f, yaw);
            a.Spike(DressMat.Iron, x, y, 10f, 0.8f, 1.6f);
        }

        // ---- elite posts: a ground sigil and a totem beside it (a pole with a crossbar, three skulls, a flag, a spike)
        foreach (var post in posts.Where(p => p.Group == "elite"))
        {
            float px = post.Position.X, py = post.Position.Y, side = px < 0f ? -1f : 1f, tx = px + side * 2.8f;
            a.Disc(DressMat.Dais, px, py, 0f, 4.6f, 0.02f, 0.9f);
            a.Disc(DressMat.Dais, px, py, 0f, 3.9f, 0.04f, 0.1f);
            a.Disc(DressMat.Dais, px, py, 0f, 1.4f, 0.06f, 0.9f);
            a.Pole(DressMat.Iron, tx, py, 0f, 0.5f, 11f, null, 0.3f);
            a.Box(DressMat.Iron, tx, py, 8f, 0.3f, 3.6f, 0.3f);
            foreach (float k in new[] { -1.4f, 0f, 1.4f })
            {
                a.Box(DressMat.Bone, tx, py + k, 8.5f, 0.6f, 0.7f, 0.8f);
                a.Box(DressMat.ClothBlack, tx - side * 0.33f, py + k - 0.18f, 8.8f, 0.1f, 0.18f, 0.2f);
                a.Box(DressMat.ClothBlack, tx - side * 0.33f, py + k + 0.18f, 8.8f, 0.1f, 0.18f, 0.2f);
            }
            a.Box(DressMat.ClothRed, tx, py, 4.6f, 0.1f, 2.6f, 3.2f, 0f, 0.6f);
            a.Spike(DressMat.Iron, tx, py, 11f, 0.8f, 2.2f);
        }

        // ---- the kit's own rubble at the breach (beside the gap, never in it) and the conquered relics against the boundary
        var items = new List<AshfallDressItem>();
        void Item(string id, float x, float y, float z, float yaw, float sx, float sy, float sz) => items.Add(new AshfallDressItem(id, new Vector3(x, y, z), yaw, new Vector3(sx, sy, sz), false, AshfallDressRole.Decoration, -1));
        Item("AC-019", -77f, GateSouth - 3.5f, 0f, 0.2f, 8f, 5f, 4f);
        Item("AC-023", -73f, GateNorth + 3f, 0f, 0.5f, 4f, 3f, 1.8f);
        Item("AC-026", -72f, (GateSouth + GateNorth) / 2f, Top, 0.3f, 6f, 3f, 2f);
        Item("AC-021", -53.5f, GateSouth - 3.5f, 0f, 0.15f, 6f, 5f, 3f);
        Item("AC-019", -51f, GateNorth + 3.5f, 0f, -0.2f, 8f, 5f, 4f);
        Item("AC-026", -55.5f, (GateSouth + GateNorth) / 2f, Top, -0.3f, 6f, 3f, 2f);
        Item("AC-030", -144f, -165f, 0f, MathF.PI / 2f, 24f, 12f, 16f);
        Item("AC-030", 144f, -128f, 0f, -MathF.PI / 2f, 24f, 12f, 16f);
        Item("AC-027", -143f, -122f, 0f, MathF.PI / 2f, 8f, 6f, 8f);
        Item("AC-032", 143f, -172f, 0f, -MathF.PI / 2f, 5f, 4f, 2.5f);
        Item("AC-021", -88f, ShellNorth - 3f, 0f, 0f, 6f, 5f, 3f);
        Item("AC-021", 88f, ShellNorth - 3f, 0f, 0f, 6f, 5f, 3f);
        Item("AC-023", -97f, GateSouth - 4f, 0f, 0.1f, 4f, 3f, 1.8f);
        Item("AC-023", 97f, GateSouth - 4f, 0f, -0.1f, 4f, 3f, 1.8f);

        return new DressPlan(rules, a.Pieces, items) { HiddenParts = hidden };
    }

    /// <summary>Where the outside ground begins (the level's ground top is 0; the boundary's outer wall stands on it): its height below the map's ground, and how far it reaches (m; ours).</summary>
    public const float OutsideLow = -1.5f, OutsideReach = 1200f;

    /// <summary>The outside ground's height at (x, y): flat out to 250 m of the map's edge, then rising at 6 degrees (about 0.105), so the far things stand above the near ground (ours).</summary>
    public static float OutsideHeight(float x, float y)
    {
        float d = MathF.Max(MathF.Abs(x) - HalfWidth, MathF.Max(SouthEdge - y, y - NorthEdge));
        return OutsideLow + 0.105f * MathF.Max(0f, d - 250f);
    }

    /// <summary>The spire behind the arena, directly north of it (a nave, a tower, a pyramid spire: a captured cathedral) and the manufactorum stacks at the edge, on the outside ground (client scenery, ours).</summary>
    public static IReadOnlyList<DressPiece> Outside()
    {
        var a = new Acc();
        float g = OutsideLow;
        a.Box(DressMat.Stone, 0f, 262f, g, 70f, 110f, 90f - g, 0f, 0.45f);
        a.Box(DressMat.Stone, 0f, 215f, g, 36f, 36f, 230f - g, 0f, 0.5f);
        a.Spike(DressMat.Stone, 0f, 215f, 228f, 40f, 172f, null, 0.4f);
        foreach (float sx in new[] { -1f, 1f }) a.Box(DressMat.Stone, sx * 34f, 262f, g, 10f, 70f, 40f - g, 0f, 0.6f);   // buttress masses along the nave
        a.Box(DressMat.Glow, 0f, 197f, 150f, 3f, 0.4f, 24f);   // a lit slit in the tower's south face: a light high up that the plaza sees
        for (int side = -1; side <= 1; side += 2)
            for (int i = 0; i < 9; i++)
            {
                uint h = DressHash.Mix(0x57AC0u, (uint)(i * 2 + (side > 0 ? 1 : 0)));
                float x = side * (230f + 18f * i + (h % 90u)), y = -170f + 62f * i + ((h >> 8) % 50u) - 25f;
                float r = 5f + (h >> 16) % 5u, top = 80f + (h >> 20) % 60u, z0 = OutsideHeight(x, y) - 2f;
                a.Pole(DressMat.Stone, x, y, z0, r * 2f, top, null, 0.2f);
                a.Pole(DressMat.Iron, x, y, z0 + top, r * 2f + 2.4f, 3f, null, 0.3f);   // the crown
                a.Spike(DressMat.Glow, x, y, z0 + top + 3f, r * 1.4f, 5f);
            }
        return a.Pieces;
    }

    const float C = CompoundHalf;
}
