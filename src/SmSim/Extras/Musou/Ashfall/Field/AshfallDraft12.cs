using System.Numerics;

namespace SmSim.Extras.Musou.Ashfall;

/// <summary>
/// Draft 12, the city layout (docs/musou-ashfall-graybox.md "Draft 12 city layout"): the stair rules it adds to drafts 9 and 10. Draft 10 and the field are untouched (their levels keep their hashes).
/// </summary>
internal static partial class AshfallDraft5Pass
{
    /// <summary>The steepest entry ramp (degrees; ours: an easy walk, well under the motor's 60 and the flights' 55).</summary>
    const float EntryDegrees = 25f;

    /// <summary>The farthest the plinth's edge may lie in front of a raised flight's foot (m; the kit's two-storey ruin: about 2.5 m to its door's step).</summary>
    const float EntryReach = 8f;

    /// <summary>
    /// Draft 12: an entry ramp for a flight whose foot stands on a raised floor (the kit's two-storey ruin, AC-051: its ground floor is a 1.1 m plinth whose door steps rise 0.29 and 0.57 m, over the 0.4 m step, so no player
    /// gets in and the flight to its upper floor is out of reach). From the foot, straight out against the climb, to where the plinth's floor ends (its door); from there a ramp of <see cref="EntryDegrees"/> down to the ground,
    /// as wide as the flight. Built as one of the model's stairs (raw frame, <see cref="RampCorners"/>): the model's boxes cut under it, the space at its foot cleared, its foot and landing zones kept, walked up and down on
    /// the item alone. Null when the way out is walled (a raised floor with no door that way) or the walk fails.
    /// </summary>
    static (Vector3[] Corners, List<WBox> Boxes, WBox Foot, WBox Land, string Note)? EntryRamp(AshfallDressItem item, int yaw, AshfallRamp flight, AshfallStairs.Ramp r, List<WBox> cur, List<Vector3[]> kept)
    {
        float g = item.Position.Z;
        if (r.LowZ <= g + AshfallStairs.Step + 0.05f) return null;
        var solids = new AshfallStairs.Solids(cur.Select(AsBox)) { Ground = (_, _) => g };
        var outward = -r.Dir;
        // the plinth's edge: the first point out from the foot where its floor drops; the way there clear to head height
        float edge = -1f;
        for (float a = 0.1f; a <= EntryReach; a += 0.1f)
        {
            var p = r.Low + outward * a;
            if (solids.Blocked(p, r.LowZ + AshfallStairs.Step + 0.05f, r.LowZ + AshfallStairs.Headroom)) return null;
            if (solids.FloorUnder(p, r.LowZ + AshfallStairs.Step) < r.LowZ - 0.15f) { edge = a - 0.1f; break; }
        }
        if (edge < 0f) return null;
        float rise = r.LowZ - g, run = MathF.Max(rise / MathF.Tan(EntryDegrees * MathF.PI / 180f), 1.5f);
        // the ramp in the model's raw frame: from the ground (edge + run out) up to the plinth (edge), across the flight's own span
        var m = AshfallKit.Get(item.Asset);
        var (sn, co) = AshfallBuilder.Trig(yaw);
        Vector3 Raw(Vector2 w, float z)
        {
            float dx = w.X - item.Position.X, dy = w.Y - item.Position.Y, lx = co * dx + sn * dy, ly = -sn * dx + co * dy;
            return new Vector3((lx / item.Size.X + 0.5f) * m.RawSize.X + m.RawMin.X, (z - g) / item.Size.Z * m.RawSize.Y + m.RawMin.Y, (0.5f - ly / item.Size.Y) * m.RawSize.Z + m.RawMin.Z);
        }
        var hi = Raw(r.Low + outward * edge, r.LowZ);
        var lo = Raw(r.Low + outward * (edge + run), g);
        AshfallRamp rp = flight.Dir switch
        {
            0 or 1 => new AshfallRamp(new Vector3(MathF.Min(hi.X, lo.X), m.RawMin.Y, flight.Min.Z), new Vector3(MathF.Max(hi.X, lo.X), hi.Y, flight.Max.Z), flight.Dir, lo.Y, hi.Y),
            _ => new AshfallRamp(new Vector3(flight.Min.X, m.RawMin.Y, MathF.Min(hi.Z, lo.Z)), new Vector3(flight.Max.X, hi.Y, MathF.Max(hi.Z, lo.Z)), flight.Dir, lo.Y, hi.Y),
        };
        var corners = RampCorners(item, yaw, rp, out bool ok);
        if (!ok || AshfallStairs.Of(new AshfallPart(AshfallKind.Stair, corners), 0, 0) is not { } entry) return null;
        Vector3 Loc(Vector3 v) { float dx = v.X - item.Position.X, dy = v.Y - item.Position.Y; return new Vector3(co * dx + sn * dy, -sn * dx + co * dy, v.Z); }
        Vector3 Wor(Vector3 l) => new(item.Position.X + co * l.X - sn * l.Y, item.Position.Y + sn * l.X + co * l.Y, l.Z);
        var lc = corners.Select(Loc).ToList();
        var fp = new Vector4(lc.Min(v => v.X), lc.Min(v => v.Y), lc.Max(v => v.X), lc.Max(v => v.Y));
        var lLow = Loc(new Vector3(entry.Low, 0f)); var lHigh = Loc(new Vector3(entry.High, 0f));
        bool alongX = MathF.Abs(lHigh.X - lLow.X) > MathF.Abs(lHigh.Y - lLow.Y);
        float a0 = alongX ? lLow.X : lLow.Y, a1 = alongX ? lHigh.X : lHigh.Y, sign = MathF.Sign(a1 - a0);
        float PlaneAt(float a) => entry.LowZ + (entry.HighZ - entry.LowZ) * Math.Clamp((a - a0) / (a1 - a0), 0f, 1f);
        var cut = Cut(cur, item, yaw, fp, alongX, a0, a1, PlaneAt);
        float w = entry.Width + 2f * AshfallStairs.SideMargin;
        WBox Zone(float along, float depth, float z)
        {
            float cx = alongX ? along : (fp.X + fp.Z) / 2f, cy = alongX ? (fp.Y + fp.W) / 2f : along;
            var wc = Wor(new Vector3(cx, cy, 0f));
            float z0 = z + AshfallStairs.Step + 0.05f, z1 = z + AshfallStairs.Headroom;
            return new WBox(new Vector3(wc.X, wc.Y, (z0 + z1) / 2f), alongX ? new Vector3(depth / 2f, w / 2f, (z1 - z0) / 2f) : new Vector3(w / 2f, depth / 2f, (z1 - z0) / 2f), yaw);
        }
        var foot = Zone(a0 - sign * AshfallStairs.FootApproach / 2f, AshfallStairs.FootApproach, entry.LowZ);
        var land = Zone(a1 + sign * AshfallStairs.Approach / 2f, AshfallStairs.Approach, entry.HighZ);
        // the ramp stays off the roadways (the army corridors keep their width; its foot's zone may lie on a road, which nothing else stands on either)
        if (t_cityRoads is { } roads)
            foreach (var v in corners)
                foreach (var rd in roads)
                    if (AshfallAtmosphere.Inside(rd, new Vector2(v.X, v.Y), 0f)) return null;
        float f0 = MathF.Min(a0, a0 - sign * AshfallStairs.Approach), f1 = MathF.Max(a0, a0 - sign * AshfallStairs.Approach);
        cut = ClearBand(cut, item, yaw, alongX ? new Vector4(f0, fp.Y, f1, fp.W) : new Vector4(fp.X, f0, fp.Z, f1), entry.LowZ + AshfallStairs.Step, entry.LowZ + AshfallStairs.CorridorHeadroom);
        string? why = Check(item, cut, corners, entry, kept);
        if (why is not null)
        {
            var carved = Carve(cut, item, yaw, fp, alongX, a0, a1, PlaneAt);
            if (Check(item, carved, corners, entry, kept) is not null) return null;
            cut = carved;
        }
        return (corners, cut, foot, land, $"draft 12: an entry ramp onto its {rise:0.0} m plinth ({run:0.0} m, {EntryDegrees:0} degrees)");
    }

    /// <summary>The most rounds of the stair-access enforcement (each round leaves out what blocks or holds an unreachable stair; ours).</summary>
    const int AccessRounds = 6;

    /// <summary>
    /// Draft 12 (owner, 2026-11-09: "no stairs inaccessible or blocked by walls or other objects"): the stair-access check (<see cref="AshfallStairAccess"/>) on the level as placed; for each stair that fails, the other item found in its
    /// way is left out, or, when none is, the item that holds the stair (a flight no one can reach is not kept). Repeated until every stair outside the authored pieces passes. The stronghold, the compound's walls and the bridges are
    /// never touched (the keep AC-044's dead flights stay as authored).
    /// </summary>
    static void EnforceStairAccess(Ctx c, AshfallLevel g5, AshfallFieldPlan plan)
    {
        var start = new Vector2((plan.Assembly[0].Rect.X + plan.Assembly[0].Rect.Z) / 2f, (plan.Assembly[0].Rect.Y + plan.Assembly[0].Rect.W) / 2f);
        var left = new List<string>();
        int rounds = 0, last = 0;
        for (; rounds < AccessRounds; rounds++)
        {
            var authored = new HashSet<int>();
            foreach (var l in c.Log) if (l.Item >= 0 && AshfallStairAccess.IsAuthored(l.Group, l.Family, l.Asset)) authored.Add(l.Item);
            var level = new AshfallLevel(c.Parts, g5.Points, g5.Areas, g5.Spawns, 0UL) { Layout = plan.Layout };
            var dressing = new AshfallDressing(c.Items, [], 0u) { ItemParts = c.ItemParts };
            var fails = AshfallStairAccess.Check(level, dressing, authored.Contains, start).Where(s => s.Problem is not null && !s.Authored).ToList();
            last = fails.Count;
            if (fails.Count == 0) break;
            var remove = new SortedSet<int>();
            foreach (var f in fails)
            {
                int k = f.Blocker >= 0 && !authored.Contains(f.Blocker) ? f.Blocker : f.Ramp.Item;
                if (k < 0 || authored.Contains(k)) continue;
                if (remove.Add(k))
                {
                    var name = c.Log.FirstOrDefault(l => l.Item == k) is { } pl ? $"{pl.Group} ({pl.Asset})" : $"item {k}";
                    left.Add(k == f.Ramp.Item ? $"{name}: {f.Problem}" : $"{name}: in the way of a stair ({f.Problem})");
                }
            }
            if (remove.Count == 0) break;
            RemoveItems(c, remove);
        }
        c.Flags.Add($"draft 12 stair access (every stair reachable at both ends on the final collision: foot and landing open, the foot reached from the streets or from a reached flight, the flight walked up and down; the authored pieces exempt): " +
            $"{left.Count} items left out in {rounds} rounds{(left.Count > 0 ? ": " + string.Join("; ", left.Take(20)) : "")}{(last > 0 ? $"; {last} stairs still fail after {AccessRounds} rounds" : "")}");
    }

    /// <summary>Leaves items out of the placed level (their parts, their log lines and their structures), keeping the order of everything else.</summary>
    static void RemoveItems(Ctx c, ISet<int> remove)
    {
        var owned = new int[c.Parts.Count];
        Array.Fill(owned, -1);
        for (int i = 0; i < c.ItemParts.Count; i++) { var (f, n) = c.ItemParts[i]; for (int q = f; q < f + n; q++) owned[q] = i; }
        var partMap = new int[c.Parts.Count];
        var parts = new List<AshfallPart>();
        for (int q = 0; q < c.Parts.Count; q++)
        {
            if (owned[q] >= 0 && remove.Contains(owned[q])) { partMap[q] = -1; continue; }
            partMap[q] = parts.Count; parts.Add(c.Parts[q]);
        }
        var itemMap = new int[c.Items.Count];
        var items = new List<AshfallDressItem>(); var ranges = new List<(int, int)>();
        for (int i = 0; i < c.Items.Count; i++)
        {
            if (remove.Contains(i)) { itemMap[i] = -1; continue; }
            itemMap[i] = items.Count; items.Add(c.Items[i]);
            var (f, n) = c.ItemParts[i];
            ranges.Add(n == 0 ? (f, 0) : (partMap[f], n));
        }
        var log = c.Log.Select(l => l.Item < 0 ? l : itemMap[l.Item] >= 0 ? l with { Item = itemMap[l.Item] } : l with { Item = -1, Outcome = l.Outcome + "; draft 12: left out (stair access)" }).ToList();
        var structures = c.Structures.Where(st => st.Parts.All(q => partMap[q] >= 0)).Select(st => st with { Parts = [.. st.Parts.Select(q => partMap[q])] }).ToList();
        c.Parts.Clear(); c.Parts.AddRange(parts);
        c.Items.Clear(); c.Items.AddRange(items);
        c.ItemParts.Clear(); c.ItemParts.AddRange(ranges);
        c.Log.Clear(); c.Log.AddRange(log);
        c.Structures.Clear(); c.Structures.AddRange(structures);
        c.Placed.Clear(); c.Placed.AddRange(items.Where(it => it.Role == AshfallDressRole.Prop).Select(it => (it.Asset, new Vector2(it.Position.X, it.Position.Y))));
    }
}
