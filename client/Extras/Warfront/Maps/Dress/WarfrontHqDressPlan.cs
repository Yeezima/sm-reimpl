using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using SmSim.Extras.Musou.Ashfall;
using static SmSim.Extras.Warfront.Hq.WarfrontHqMap;

namespace SmClient.Extras.Warfront.Dress;

/// <summary>
/// The HQ's exterior dressing as data (slice "dress", part 2; every size ours): the Imperial retaining wall and gate (masonry, buttresses, a gate frame with the aquila board, banners, crenellations on the parapet), the approach ramp (the road's skin is drawn from
/// the constants by <c>WarfrontHqDress</c>; craters and wrecks stand beside it in the ditch, none within <see cref="FootClear"/> m of the foot), the Orks' assembly ground (war totems and wrecks, all at least <see cref="FootClear"/> m from the foot), the courtyard
/// (the cover walls as the kit's own barrier, lamp posts, supply crates and kit relics against the walls) and the outside (ground, the spire far behind, stacks, the wasteland's ruins and smoke to the south). The keep, the tunnels, the cross passage and the chamber are NOT dressed
/// (<see cref="DressRule.NotDressed"/>: W13d may move a door or passage there). Visual only: no piece collides, nothing is added to the level, the map hash is never read, and no piece or kit item stands on the ramp's fan, at the wall's foot or in the gate.
/// </summary>
public static class WarfrontHqDressPlan
{
    /// <summary>No prop within this distance of the ramp's foot (the foot's centre is (0, <see cref="RampSouth"/>)): the fan, the cleared assembly ground and the wall foot stay free (W13c, amendment 2; m).</summary>
    public const float FootClear = 40f;

    /// <summary>The rule of an HQ level part, or null when the dressing has none (a test fails on a null).</summary>
    public static DressRule? Classify(AshfallPart part)
    {
        var (mn, mx) = WarfrontFortressDressPlan.Aabb(part);
        float dx = mx.X - mn.X;
        switch (part.Kind)
        {
            case AshfallKind.Ground: return DressRule.Ground;
            case AshfallKind.Boundary: return DressRule.Boundary;
            case AshfallKind.Stair: return DressRule.ParapetStair;
            case AshfallKind.Cover:
                if (mn.Z >= WallTop - 0.1f) return DressRule.Parapet;
                return mx.Y <= KeepSouth ? DressRule.CourtCover : DressRule.NotDressed;
            case AshfallKind.Wall:
                if (mn.X >= CurtainX - 0.1f || mx.X <= -CurtainX + 0.1f) return DressRule.RockCliff;   // the curtain rock either side (the masses beside the courtyard, the tunnels and the chamber)
                if (mn.Y >= TunnelSouth - 0.1f) return DressRule.NotDressed;                               // the cave masses: tunnels, cross passage, chamber
                if (mx.Y <= WallNorth + 0.1f && mn.Y >= RampNorth - ChamferLeg - 0.1f) return DressRule.ImperialWall;   // the fortress wall and the wall-foot chamfers
                return DressRule.NotDressed;
            case AshfallKind.Stronghold:
            case AshfallKind.Floor:
                return DressRule.NotDressed;   // the keep: its walls, pillars and roof
            default: return null;
        }
    }

    /// <summary>The cover walls' kit skin (the kit's masonry and sandbag hybrid barrier, fitted to each cover part's own box).</summary>
    public const string CoverModel = "AC-017";

    sealed class Acc
    {
        public readonly List<DressPiece> Pieces = [];
        public readonly Dictionary<string, List<DressPiece>> Fallbacks = new();
        public string? Into;
        List<DressPiece> Cur => Into is null ? Pieces : (Fallbacks.TryGetValue(Into, out var l) ? l : Fallbacks[Into] = []);
        public void Box(DressMat m, float cx, float cy, float z, float sx, float sy, float sz, float yaw = 0f, float shade = 0.5f) => Cur.Add(new(DressShape.Box, m, new(cx, cy, z), new(sx, sy, sz), yaw, null, shade));
        public void Spike(DressMat m, float cx, float cy, float z, float d, float len, Vector3? axis = null, float shade = 0.5f) => Cur.Add(new(DressShape.Spike, m, new(cx, cy, z), new(d, d, len), 0f, axis, shade));
        public void Pole(DressMat m, float cx, float cy, float z, float d, float len, Vector3? axis = null, float shade = 0.5f) => Cur.Add(new(DressShape.Pole, m, new(cx, cy, z), new(d, d, len), 0f, axis, shade));
        public void Disc(DressMat m, float cx, float cy, float z, float d, float t, float shade) => Cur.Add(new(DressShape.Disc, m, new(cx, cy, z), new(d, d, t), 0f, null, shade));
    }

    /// <summary>The dressing plan of the HQ's exterior: the rule of every part, the pieces, the kit items and the parts the kit look must not draw.</summary>
    public static DressPlan Build(AshfallLevel level)
    {
        var rules = new List<DressRule>(); var hidden = new List<int>(); var items = new List<AshfallDressItem>();
        for (int i = 0; i < level.Parts.Count; i++)
        {
            var rule = Classify(level.Parts[i]) ?? DressRule.Ground;   // a part with no rule is the test's failure; the drawing keeps the kit's ground look for it
            rules.Add(rule);
            if (rule is not (DressRule.Ground or DressRule.Boundary or DressRule.NotDressed)) hidden.Add(i);
            if (rule == DressRule.CourtCover)
            {
                var (mn, mx) = WarfrontFortressDressPlan.Aabb(level.Parts[i]);
                items.Add(new AshfallDressItem(CoverModel, new Vector3((mn.X + mx.X) / 2f, (mn.Y + mx.Y) / 2f, mn.Z), 0f, new Vector3(mx.X - mn.X, mx.Y - mn.Y, mx.Z - mn.Z), false, AshfallDressRole.Decoration, -1));
            }
        }
        var a = new Acc();
        var props = new List<DressProp>();
        const float G = GateWidth / 2f, Wt = WallTop, P = PlateauZ, Fy = WallSouth;   // Fy: the wall's south face

        // ---- the retaining wall's south face: a masonry skin on the cliff under the wall (z 0..12, either side of the ramp), buttresses, banners, the gate's frame and aquila board
        foreach (float sx in new[] { -1f, 1f })
        {
            a.Box(DressMat.Masonry, sx * (RampWidth / 2f + CurtainX) / 2f, Fy - 0.15f, 0f, CurtainX - RampWidth / 2f, 0.3f, P, 0f, 0.5f);
            foreach (float x0 in new[] { 24f, 36f, 48f, 60f, 72f, 84f })   // a buttress each 12 m: from the ditch up past the parapet's foot, with a pinnacle
            {
                float x = sx * x0;
                a.Box(DressMat.Masonry, x, Fy - 0.9f, 0f, 3f, 1.8f, Wt - 1.5f, 0f, DressHash.Unit(101, (uint)(x0 + (sx > 0 ? 100 : 0))));
                a.Box(DressMat.Masonry, x, Fy - 0.6f, Wt - 1.5f, 3.4f, 1.2f, 0.6f);   // the cap course
                a.Spike(DressMat.Stone, x, Fy - 0.6f, Wt - 0.9f, 1.4f, 3.2f, null, 0.4f);
            }
            foreach (float x0 in new[] { 30f, 42f, 54f, 66f, 78f })   // an Imperial banner between the buttresses, hung from the wall's face
            {
                float x = sx * x0;
                a.Box(DressMat.ClothBlue, x, Fy - 0.12f, 14f, 2.8f, 0.12f, 9f, 0f, DressHash.Unit(102, (uint)(x0 + (sx > 0 ? 100 : 0))));
                a.Box(DressMat.Gold, x, Fy - 0.2f, 20f, 1.6f, 0.08f, 1.6f);
                a.Box(DressMat.Gold, x, Fy - 0.2f, 14.2f, 2.8f, 0.08f, 0.35f);
            }
            // the gate's pilasters, proud 0.5 m of the face and outside the 12 m opening
            a.Box(DressMat.Masonry, sx * (G + 1.4f), Fy - 0.25f, P, 2.8f, 0.5f, GateHeight + 6f, 0f, 0.3f);
            a.Spike(DressMat.Stone, sx * (G + 1.4f), Fy - 0.25f, P + GateHeight + 6f, 1.6f, 2.8f, null, 0.5f);
        }
        a.Box(DressMat.Masonry, 0f, Fy - 0.3f, P + GateHeight, GateWidth + 5.6f, 0.6f, 1.6f, 0f, 0.3f);   // the head course across the top of the opening (on the wall's face, no lower than the 10 m clear)
        a.Box(DressMat.Gold, 0f, Fy - 0.4f, P + GateHeight + 1.8f, 7f, 0.2f, 2.2f, 0f, 0.5f);               // the aquila board: a gold plate
        a.Box(DressMat.ClothBlack, 0f, Fy - 0.55f, P + GateHeight + 2.1f, 1.2f, 0.2f, 1.6f);                  // the body
        foreach (float s in new[] { -1f, 1f })
        {
            a.Box(DressMat.ClothBlack, s * 1.9f, Fy - 0.55f, P + GateHeight + 2.5f, 2.4f, 0.2f, 0.5f, s * 0.35f);
            a.Box(DressMat.ClothBlack, s * 2.4f, Fy - 0.55f, P + GateHeight + 1.9f, 1.8f, 0.2f, 0.4f, s * 0.5f);
        }

        // ---- crenellations on the parapets (the collision's parapet is 0.5 m thick and 1.2 m high: the merlons stand on it and are the same thickness)
        for (float x = -88.8f; x <= 88.9f; x += 2.4f)
        {
            a.Box(DressMat.Masonry, x, WallSouth + ParapetThickness / 2f, Wt + ParapetHeight, 1.2f, ParapetThickness, 1.4f, 0f, DressHash.Unit(103, (uint)(x * 5f + 1000f)));
            if (MathF.Abs(x) < 64f || MathF.Abs(x) > 72f)   // the inner parapet stops at the stairs' tops
                a.Box(DressMat.Masonry, x, WallNorth - ParapetThickness / 2f, Wt + ParapetHeight, 1.2f, ParapetThickness, 1.4f, 0f, DressHash.Unit(104, (uint)(x * 5f + 1000f)));
        }

        // ---- the ramp's sides (the ditch beside it, off the ramp and 40 m or more from the foot): craters and the wrecks of the first assault, with the kit's own rubble
        var items2 = new List<AshfallDressItem>();
        void Item(string id, float x, float y, float z, float yaw, float sx, float sy, float sz) => items2.Add(new AshfallDressItem(id, new Vector3(x, y, z), yaw, new Vector3(sx, sy, sz), false, AshfallDressRole.Decoration, -1));
        foreach (float sx in new[] { -1f, 1f })
        {
            a.Disc(DressMat.Dais, sx * 38f, -72f, 0.01f, 11f, 0.02f, 0.1f);   // a crater (a scorched pit: dark discs, flat)
            a.Disc(DressMat.Dais, sx * 38f, -72f, 0.03f, 6f, 0.02f, 0.0f);
            a.Disc(DressMat.Dais, sx * 58f, -84f, 0.01f, 14f, 0.02f, 0.1f);
            a.Disc(DressMat.Dais, sx * 58f, -84f, 0.03f, 8f, 0.02f, 0.0f);
            a.Box(DressMat.Wreck, sx * 28f, -80f, 0f, 7.5f, 3.4f, 2.4f, sx * 0.4f, 0.4f);   // a tank hulk
            a.Pole(DressMat.Iron, sx * 28f + sx * 3f, -80f + 1f, 2.4f, 0.5f, 5f, new Vector3(sx * 0.9f, 0.2f, 0.25f));   // its gun
            a.Box(DressMat.Wreck, sx * 46f, -62f, 0f, 8.5f, 3.6f, 2.8f, -sx * 0.8f, 0.8f);
            a.Box(DressMat.Wreck, sx * 70f, -74f, 0f, 5f, 3f, 1.8f, sx * 0.2f, 0.2f);
            Item("AC-019", sx * 46f, -78f, 0f, sx * 0.3f, 8f, 5f, 4f);
            Item("AC-020", sx * 33f, -64f, 0f, 0.4f, 3f, 2.2f, 0.5f);
            Item("AC-024", sx * 64f, -66f, 0f, 0f, 2f, 2f, 0.2f);
            Item("AC-018", sx * 62f, -56f, 0f, sx * 0.5f, 8f, 3f, 3f);
            Item("AC-020", sx * 56f, -92f, 0f, 1.1f, 3f, 2.2f, 0.5f);
        }

        // ---- the assembly ground (the Orks' side): the game's own WAAAGH totems (read from the install; a drawn totem stands in when it does not load), scrap heaps, turrets and crashed roks, plated wrecks; none within 40 m of the foot
        foreach (var (x, y) in new[] { (-70f, -150f), (70f, -150f), (-105f, -175f), (105f, -175f), (-45f, -200f), (45f, -200f), (-125f, -140f), (125f, -140f) })
        {
            props.Add(new DressProp(DressPropKeys.Waaagh, new(x, y, 0f), x > 0 ? 0.4f : -0.4f, 10f));
            a.Into = DressPropKeys.Waaagh;
            a.Pole(DressMat.Iron, x, y, 0f, 0.5f, 11f, null, 0.3f);
            a.Box(DressMat.Iron, x, y, 8f, 3.4f, 0.3f, 0.3f);
            a.Box(DressMat.Bone, x, y - 0.3f, 8.4f, 0.7f, 0.6f, 0.8f);
            a.Box(DressMat.ClothRed, x, y - 0.1f, 4.4f, 2.4f, 0.1f, 3.4f, 0f, 0.6f);
            a.Spike(DressMat.Iron, x, y, 11f, 0.8f, 2.2f);
            a.Into = null;
        }
        foreach (var (x, y, yaw) in new[] { (-30f, -175f, 0.5f), (36f, -168f, -0.3f), (-95f, -150f, 1.2f), (92f, -160f, 0.2f), (0f, -205f, 0.9f) })
        {
            a.Box(DressMat.Wreck, x, y, 0f, 9f, 4f, 3f, yaw, 0.5f);
            a.Box(DressMat.Wreck, x + 2f, y + 3.5f, 0f, 4f, 3f, 1.6f, yaw + 0.5f, 0.2f);
        }
        foreach (var (x, y) in new[] { (-55f, -190f), (55f, -190f), (-100f, -160f), (100f, -160f) }) props.Add(new DressProp(DressPropKeys.Turret, new(x, y, 0f), x > 0 ? 2.6f : 0.5f, 4.5f));
        foreach (var (x, y, h) in new[] { (-130f, -195f, 28f), (132f, -192f, 24f), (-22f, -100f, 0f) }) if (h > 0f) props.Add(new DressProp(DressPropKeys.Rok, new(x, y, 0f), x > 0 ? 2.2f : 0.9f, h));
        foreach (var (x, y) in new[] { (-60f, -170f), (60f, -172f), (-85f, -195f), (86f, -198f), (-20f, -190f), (22f, -194f), (-115f, -160f), (118f, -158f), (-52f, -168f), (52f, -170f) }) props.Add(new DressProp(DressPropKeys.Junkpile, new(x, y, 0f), DressHash.Unit(121, (uint)(x + y + 800f)) * 6.28f, 3.4f));
        foreach (float sx in new[] { -1f, 1f })
            foreach (var (x, y) in new[] { (50f, -88f), (66f, -74f), (84f, -60f) }) props.Add(new DressProp(DressPropKeys.Junkpile, new(sx * x, y, 0f), DressHash.Unit(122, (uint)(x + y + 800f)) * 6.28f, 3.0f));   // in the ditch beside the ramp, off it
        a.Into = DressPropKeys.Junkpile;
        foreach (var pr in props.Where(p => p.Key == DressPropKeys.Junkpile)) a.Box(DressMat.Wreck, pr.Base.X, pr.Base.Y, 0f, 4.5f, 3.5f, pr.Height * 0.6f, pr.Yaw, 0.5f);
        a.Into = null;

        // ---- the rock masses beside the wall (the cliffs, x 90 to 150): a broken skyline (ledges and fallen blocks on the top edges), ledges standing out of the south face, and the kit's boulders at the foot, all out of the 40 m line
        foreach (float sx in new[] { -1f, 1f })
        {
            for (int i = 0; i < 8; i++)   // the top of the south edge
            {
                uint h = DressHash.Mix(131u + (uint)(sx > 0 ? 7 : 0), (uint)i);
                float x = sx * (94f + i * 7f + (h % 5u)), w = 6f + (h >> 4) % 9u, d = 5f + (h >> 8) % 8u, ht = 3f + (h >> 12) % 10u;
                a.Box(DressMat.Rock, x, RampNorth + d / 2f, 40f, w, d, ht, ((h >> 16) % 5u - 2f) * 0.12f, DressHash.Unit(132, h));
            }
            for (int i = 0; i < 20; i++)   // the inner edge (the courtyard side, the keep and the caves behind it)
            {
                uint h = DressHash.Mix(141u + (uint)(sx > 0 ? 9 : 0), (uint)i);
                float y = WallSouth + 4f + i * 12f + (h % 5u), w = 5f + (h >> 4) % 7u, d = 6f + (h >> 8) % 8u, ht = 3f + (h >> 12) % 11u;
                a.Box(DressMat.Rock, sx * (CurtainX + d / 2f), y, 40f, d, w, ht, ((h >> 16) % 5u - 2f) * 0.1f, DressHash.Unit(142, h));
            }
            for (int i = 0; i < 9; i++)   // ledges out of the south face (over the ditch, 14 to 34 m up)
            {
                uint h = DressHash.Mix(151u + (uint)(sx > 0 ? 11 : 0), (uint)i);
                float x = sx * (96f + (i % 5) * 11f + (h % 4u)), w = 7f + (h >> 4) % 6u, d = 3f + (h >> 8) % 4u, z0 = 14f + (i % 3) * 8f + (h >> 12) % 4u;
                a.Box(DressMat.Rock, x, RampNorth - d / 2f, z0, w, d, 4f + (h >> 16) % 4u, ((h >> 20) % 5u - 2f) * 0.08f, DressHash.Unit(152, h));
            }
        }
        foreach (float sx in new[] { -1f, 1f })
        {
            items2.Add(new AshfallDressItem("AC-030", new Vector3(sx * 118f, -66f, 0f), sx > 0 ? -0.2f : 0.2f, new Vector3(24f, 12f, 16f), false, AshfallDressRole.Decoration, -1));
            items2.Add(new AshfallDressItem("AC-019", new Vector3(sx * 100f, -54f, 0f), 0.4f, new Vector3(8f, 5f, 4f), false, AshfallDressRole.Decoration, -1));
            items2.Add(new AshfallDressItem("AC-021", new Vector3(sx * 138f, -58f, 0f), -0.3f, new Vector3(6f, 5f, 3f), false, AshfallDressRole.Decoration, -1));
        }

        // ---- the courtyard: lamp posts and supply crates against the curtain walls, kit relics in the corners by the keep (off the routes from the gate to the doors)
        foreach (float sx in new[] { -1f, 1f })
        {
            foreach (float y in new[] { -30f, -10f, 10f, 30f })
            {
                a.Pole(DressMat.Iron, sx * 88.2f, y, P, 0.3f, 4.2f, null, 0.4f);
                a.Box(DressMat.Glow, sx * 88.2f, y, P + 4.2f, 0.7f, 0.7f, 0.9f, 0f, 0.5f);
            }
            foreach (float y in new[] { -24f, -8f, 8f, 24f }) { props.Add(new DressProp(DressPropKeys.SmBanner, new(sx * 86.5f, y, P), sx > 0 ? MathF.PI / 2f : -MathF.PI / 2f, 9f)); a.Into = DressPropKeys.SmBanner; a.Pole(DressMat.Iron, sx * 86.5f, y, P, 0.3f, 9f, null, 0.4f); a.Box(DressMat.ClothBlue, sx * 86.5f, y, P + 3f, 0.1f, 2.4f, 5.5f, 0f, 0.5f); a.Box(DressMat.Gold, sx * 86.4f, y, P + 6.4f, 0.08f, 1.2f, 1.2f); a.Into = null; }
            a.Box(DressMat.Wreck, sx * 87.5f, 0f, P, 2.2f, 2.2f, 1.6f, 0f, 0.2f);
            a.Box(DressMat.Wreck, sx * 87.5f, 2.6f, P, 2.2f, 2.2f, 1.6f, 0f, 0.6f);
            a.Box(DressMat.Wreck, sx * 87.5f, 1.2f, P + 1.6f, 1.8f, 1.8f, 1.3f, 0.3f, 0.4f);
            a.Box(DressMat.Wreck, sx * 87.5f, -22f, P, 2.2f, 2.2f, 1.6f, 0f, 0.8f);
            Item("AC-028", sx * 83f, 34f, P, 0f, 8f, 6f, 4f);
            Item("AC-032", sx * 74f, -36f, P, 0.5f, 5f, 4f, 2.5f);
        }
        items.AddRange(items2);
        return new DressPlan(rules, a.Pieces, items) { HiddenParts = hidden, Props = props, Fallbacks = a.Fallbacks.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<DressPiece>)kv.Value) };
    }

    // ---- the outside: the spire far behind the keep, the stacks, the wasteland's ruins and smoke to the south
    /// <summary>Where the outside ground begins (below the map's ground) and how it rises beyond 250 m of the map's edge (m; ours, as the fortress's).</summary>
    public const float OutsideLow = -1.5f;

    /// <summary>The outside ground's height at (x, y): flat for 250 m past the map's box, then rising at 6 degrees.</summary>
    public static float OutsideHeight(float x, float y)
    {
        float d = MathF.Max(MathF.Abs(x) - HalfWidth, MathF.Max(SouthEdge - y, y - NorthEdge));
        return OutsideLow + 0.105f * MathF.Max(0f, d - 250f);
    }

    /// <summary>The far scenery (client, ours): the spire (a captured cathedral) directly behind the keep, stacks at the sides, a ruined skyline of low blocks and tilted smoke plumes on the Orks' side (south).</summary>
    public static IReadOnlyList<DressPiece> Outside()
    {
        var a = new Acc();
        float g = OutsideLow;
        a.Box(DressMat.Stone, 0f, 480f, g, 70f, 110f, 90f - g, 0f, 0.45f);
        a.Box(DressMat.Stone, 0f, 430f, g, 36f, 36f, 230f - g, 0f, 0.5f);
        a.Spike(DressMat.Stone, 0f, 430f, 228f, 40f, 172f, null, 0.4f);
        foreach (float sx in new[] { -1f, 1f }) a.Box(DressMat.Stone, sx * 34f, 480f, g, 10f, 70f, 40f - g, 0f, 0.6f);
        a.Box(DressMat.Glow, 0f, 411f, 150f, 3f, 0.4f, 24f);
        for (int side = -1; side <= 1; side += 2)
            for (int i = 0; i < 9; i++)
            {
                uint h = DressHash.Mix(0x7A11u, (uint)(i * 2 + (side > 0 ? 1 : 0)));
                float x = side * (230f + 20f * i + (h % 90u)), y = -200f + 80f * i + ((h >> 8) % 60u) - 30f;
                float r = 5f + (h >> 16) % 5u, top = 80f + (h >> 20) % 60u, z0 = OutsideHeight(x, y) - 2f;
                a.Pole(DressMat.Stone, x, y, z0, r * 2f, top, null, 0.2f);
                a.Pole(DressMat.Iron, x, y, z0 + top, r * 2f + 2.4f, 3f, null, 0.3f);
                a.Spike(DressMat.Glow, x, y, z0 + top + 3f, r * 1.4f, 5f);
            }
        // the wasteland to the south: a low ruined skyline (the far massing's blocks, mirrored and cut down to a third) and smoke plumes (inverted cones, leaning with the wind)
        foreach (var b in WarfrontFarMassingPlan.Blocks(430f, 1250f, 0x57u))
        {
            if (MathF.Abs(b.X) < 30f) continue;
            float x = b.X * 1.4f, y = -b.Y - 40f;
            a.Box(DressMat.Stone, x, y, OutsideHeight(x, y) - 1f, b.SizeX, b.SizeY, b.Height * 0.33f + 4f, b.Yaw, DressHash.Unit(105, (uint)(b.X * 3f + b.Y)));
        }
        for (int i = 0; i < 7; i++)
        {
            uint h = DressHash.Mix(0x5303u, (uint)i);
            float x = -520f + i * 170f + (h % 60u), y = -620f - ((h >> 8) % 400u), top = 330f + (h >> 16) % 160u;
            a.Spike(DressMat.Smoke, x, y, OutsideHeight(x, y) + top, 90f + (h >> 4) % 60u, top, new Vector3(0.18f, 0.05f, -1f), 0.5f);
            a.Spike(DressMat.Glow, x, y, OutsideHeight(x, y), 14f, 12f);   // the fire at its foot
        }
        return a.Pieces;
    }
}
