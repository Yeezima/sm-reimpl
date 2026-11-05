using System.Numerics;

namespace SmSim.Extras.Musou.Ashfall;

// Props (kit dressing, draft 1; all ours): landmarks and cover added beyond version 5, each with a simple solid graybox proxy appended to the level's parts.
internal static partial class AshfallDressPass
{
    /// <summary>
    /// A prop: the model, the collision kind, the item's box (length, depth, height, metres), how many, and the proxy boxes in the item's local frame
    /// (u0, v0, u1, v1, z0, z1; null = one box filling the item). Heights of Rubble are 0.9..2.5 m and of Cover never 0.4..0.9 m (the kit's dead band).
    /// </summary>
    sealed record PropDef(string Asset, AshfallKind Kind, float L, float D, float H, int Count, (float U0, float V0, float U1, float V1, float Z0, float Z1)[]? Boxes = null, bool AxisOnly = false);

    static readonly PropDef[] PropList =
    [
        new("AC-027", AshfallKind.Wall, 6f, 5f, 7f, 4),                    // shattered saint monument: a solid mass
        new("AC-032", AshfallKind.Rubble, 4f, 3f, 2.2f, 8),                // statue fragment cluster
        new("AC-031", AshfallKind.Cover, 3.6f, 0.8f, 1.8f, 12),            // broken relief slab, standing
        new("AC-029", AshfallKind.Wall, 5f, 3.5f, 6f, 5),                  // ruined civic shrine fragment
        new("AC-030", AshfallKind.Wall, 12f, 5f, 6f, 4),                   // collapsed buttress mass
        new("AC-002", AshfallKind.Wall, 8f, 2f, 7f, 5,                     // Gothic arch remnant: two piers and a lintel, the opening between them stays walkable (4 m wide, 5.5 m tall)
            [(-4f, -1f, -2f, 1f, 0f, 7f), (2f, -1f, 4f, 1f, 0f, 7f), (-2f, -1f, 2f, 1f, 5.5f, 7f)], AxisOnly: true),
        new("AC-013", AshfallKind.Cover, 3f, 3f, 2f, 7),                   // monument plinth fragment
        new("AC-008", AshfallKind.Perch, 6f, 3f, 4.5f, 6),                 // elevated firing perch: a block with a standable top, reached by a jump only
        new("AC-007", AshfallKind.Wall, 6f, 4f, 6f, 3),                    // exterior stair access ruin: a non-walkable backdrop piece (no stair of its own)
        new("AC-018", AshfallKind.Rubble, 3.2f, 2.4f, 1.8f, 10),           // chokepoint debris barrier
        new("AC-012", AshfallKind.Cover, 5f, 2.4f, 1.4f, 3),               // crater edge cover segment (sparingly: the model has no clear flat footprint)
    ];

    static List<AshfallDressItem> Props(AshfallBuilder b, AshfallPlacer p)
    {
        var items = new List<AshfallDressItem>();
        var centres = new List<Vector2>();
        // Round robin over the definitions, so a short supply of room costs every kind a little; each placement takes the first acceptable candidate of that definition's own hash order, the owner's red zones first.
        var left = PropList.Select(d => d.Count).ToArray();
        var cands = new List<(uint Key, float X, float Y)>[PropList.Length];
        var next = new int[PropList.Length];
        for (int d = 0; d < PropList.Length; d++)
        {
            var list = new List<(uint Key, float X, float Y)>();
            uint i = 0;
            for (float x = -218f; x <= 218f; x += 4f)
                for (float y = -218f; y <= 218f; y += 4f)
                {
                    uint key = AshfallBuilder.Mix(70000u + (uint)d, i++);
                    list.Add(((AshfallMap.InZone(x, y) ? 0u : 0x80000000u) | (key >> 1), x, y));
                }
            list.Sort((a, c) => a.Key != c.Key ? a.Key.CompareTo(c.Key) : a.X != c.X ? a.X.CompareTo(c.X) : a.Y.CompareTo(c.Y));   // total order: List.Sort is unstable
            cands[d] = list;
        }
        var sameKind = new List<Vector2>[PropList.Length];
        for (int d = 0; d < PropList.Length; d++) sameKind[d] = [];
        bool progress = true;
        while (progress)
        {
            progress = false;
            for (int d = 0; d < PropList.Length; d++)
            {
                if (left[d] == 0) continue;
                while (next[d] < cands[d].Count)
                {
                    var (key, cx, cy) = cands[d][next[d]++];
                    if (!TryPlace(b, p, PropList[d], key, cx, cy, centres, sameKind[d], items)) continue;
                    left[d]--;
                    progress = true;
                    break;
                }
            }
        }
        return items;
    }

    static bool TryPlace(AshfallBuilder b, AshfallPlacer p, PropDef def, uint key, float cx, float cy, List<Vector2> centres, List<Vector2> sameKind, List<AshfallDressItem> items)
    {
        uint h = AshfallBuilder.Mix(key, 4711u);
        int yaw = def.AxisOnly ? 6 * (int)(h % 4) : 3 * (int)(h % 8);   // 0, 45, ... 315 degrees (a quarter turn only for the arch)
        var (s, c) = AshfallBuilder.Trig(yaw);
        float ex = MathF.Abs(c) * def.L / 2f + MathF.Abs(s) * def.D / 2f, ey = MathF.Abs(s) * def.L / 2f + MathF.Abs(c) * def.D / 2f;
        var r = new Vector4(cx - ex, cy - ey, cx + ex, cy + ey);
        if (r.X < -222f || r.Y < -222f || r.Z > 222f || r.W > 222f) return false;
        if (!p.Clear(r, 3f) || !AshfallPlacer.Flat(r)) return false;
        float z = AshfallMap.SurfaceAt(cx, cy);
        if (AshfallMap.SurfaceAt(r.X, r.Y) != z || AshfallMap.SurfaceAt(r.Z, r.W) != z || AshfallMap.SurfaceAt(r.X, r.W) != z || AshfallMap.SurfaceAt(r.Z, r.Y) != z) return false;
        var here = new Vector2(cx, cy);
        if (centres.Any(q => Vector2.DistanceSquared(q, here) < 100f) || sameKind.Any(q => Vector2.DistanceSquared(q, here) < 900f)) return false;   // 10 m from any prop, 30 m from one of its own kind
        int first = b.Parts.Count;
        var boxes = def.Boxes ?? [(-def.L / 2f, -def.D / 2f, def.L / 2f, def.D / 2f, 0f, def.H)];
        var ax = new Vector2(c, s); var ay = new Vector2(-s, c);
        foreach (var (u0, v0, u1, v1, z0, z1) in boxes)
        {
            var m = here + ax * ((u0 + u1) / 2f) + ay * ((v0 + v1) / 2f);
            b.Oriented(def.Kind, m.X, m.Y, z + z0, z + z1, u1 - u0, v1 - v0, yaw);
        }
        p.Claims.Add(r);
        centres.Add(here);
        sameKind.Add(here);
        var model = AshfallKit.Get(def.Asset);
        items.Add(new AshfallDressItem(def.Asset, new Vector3(cx, cy, z), Yaw(yaw), new Vector3(def.L, def.D, def.H), Swap(model), AshfallDressRole.Prop, first));
        return true;
    }
}
