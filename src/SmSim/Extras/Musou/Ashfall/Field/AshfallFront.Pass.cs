using System.Numerics;

namespace SmSim.Extras.Musou.Ashfall;

/// <summary>
/// The front pilot's gate B (docs/musou-ashfall-graybox.md "Front pilot"): after every standing rule of the kit pass, the battlefield measure on the band's final collision. Where a formation rule fails (an army route or a planned route
/// narrower than the army's width, a point without two formation-capable approaches, a protected area with collision in it, the through road under its width), the items at the pinch points are left out, nearest first, round after round
/// (up to <see cref="FrontRounds"/>); the authored pieces are never touched. A band that still fails stops the build with an <see cref="AshfallGateException"/> naming the rules.
/// </summary>
internal static partial class AshfallDraft5Pass
{
    /// <summary>The most rounds of gate B's clean-up (ours).</summary>
    const int FrontRounds = 8;
    /// <summary>The items left out at one pinch point in a round (the nearest; ours).</summary>
    const int FrontPerPinch = 3;

    static void EnforceFront(Ctx c, AshfallLevel g5, AshfallFieldPlan plan)
    {
        float min = plan.Params.MinArmyWidth;
        var left = new List<string>();
        List<AshfallGateRule> rules = [];
        int rounds = 0;
        for (; ; rounds++)
        {
            var authored = new HashSet<int>();
            foreach (var l in c.Log) if (l.Item >= 0 && AshfallStairAccess.IsAuthored(l.Group, l.Family, l.Asset)) authored.Add(l.Item);
            var level = new AshfallLevel(c.Parts, g5.Points, g5.Areas, g5.Spawns, 0UL) { Layout = plan.Layout };
            var dressing = new AshfallDressing(c.Items, [], 0u) { ItemParts = c.ItemParts };
            var bf = new AshfallBattlefield(plan.Layout, level, dressing, plan);
            var through = AshfallFront.ThroughRoad(bf.Grid, min);
            rules = [.. bf.Constraints.Where(AshfallFront.IsFormationRule).Select(k => new AshfallGateRule(k.Name, k.Passed, k.Value, k.Threshold)), .. through];
            if (rules.All(r => r.Passed) || rounds >= FrontRounds) break;

            // The pinch points of the failed rules, each with the reach the items are taken from.
            var spots = new List<(Vector2 At, float Reach, string Why)>();
            foreach (var l in bf.Links) if (l.Width < min) spots.Add((l.Pinch, min / 2f + 2f, $"army route {l.From} to {l.To}"));
            foreach (var r in bf.RouteWidths) foreach (var (at, w) in r.Pinches) if (w < min) spots.Add((at, min / 2f + 2f, $"route {r.Route}"));
            if (!through[0].Passed)
                for (float y = -AshfallMap.Half + 2f; y <= AshfallMap.Half - 2f; y += 4f)
                    if (bf.Grid.WidthAcross(new Vector2(0f, y), Vector2.UnitY) < min) spots.Add((new Vector2(0f, y), min / 2f + 2f, "the through road"));
            foreach (var a in bf.Approaches.Where(a => a.Count < plan.Params.ApproachCount))
            {
                var pt = g5.Points.First(q => q.Name == a.Point);
                float ring = pt.Radius + plan.Params.ObjectiveClear + 6f;
                for (int deg = 0; deg < 360; deg += 30) { float rad = deg * MathF.PI / 180f; spots.Add((new Vector2(pt.Center.X, pt.Center.Y) + new Vector2(MathF.Cos(rad), MathF.Sin(rad)) * ring, min / 2f, $"point {a.Point}'s approaches")); }
            }
            var remove = new SortedSet<int>();
            foreach (var (at, reach, why) in spots)
            {
                var near = new List<(float D, int Item)>();
                for (int i = 0; i < c.Items.Count; i++)
                {
                    var it = c.Items[i];
                    if (it.Role != AshfallDressRole.Prop || authored.Contains(i) || c.ItemParts[i].Count == 0) continue;
                    float d = RectDistance(ItemRect(it), at);
                    if (d <= reach) near.Add((d, i));
                }
                foreach (var (_, i) in near.OrderBy(n => n.D).ThenBy(n => n.Item).Take(FrontPerPinch))
                    if (remove.Add(i)) left.Add($"{(c.Log.FirstOrDefault(l => l.Item == i) is { } pl ? $"{pl.Group} ({pl.Asset})" : $"item {i}")}: at a pinch of {why} ({at.X:0}, {at.Y:0})");
            }
            // Collision in a protected area: the items whose parts stand there.
            foreach (var (name, blocked, _) in bf.ProtectedBlocked.Where(p => p.Blocked > 0))
            {
                var area = plan.Assembly.Where(z => z.Name == name).Select(z => (Vector4?)z.Rect).FirstOrDefault();
                for (int i = 0; i < c.Items.Count; i++)
                {
                    var it = c.Items[i];
                    if (it.Role != AshfallDressRole.Prop || authored.Contains(i) || c.ItemParts[i].Count == 0) continue;
                    var r = ItemRect(it);
                    bool meets = area is { } z ? r.X < z.Z && r.Z > z.X && r.Y < z.W && r.W > z.Y
                        : name.EndsWith(" open zone", StringComparison.Ordinal) ? plan.ClearZones.Any(q => name == $"{q.Name} open zone" && RectDistance(r, q.Centre) < q.Radius)
                        : name.StartsWith("corridor ", StringComparison.Ordinal) && plan.Corridors.Any(k => name == $"corridor {k.Name}" && AshfallField.RectMeetsBox(r, k.Box()));
                    if (meets && remove.Add(i)) left.Add($"{(c.Log.FirstOrDefault(l => l.Item == i) is { } pl ? $"{pl.Group} ({pl.Asset})" : $"item {i}")}: collision in {name} ({blocked} cells)");
                }
            }
            if (remove.Count == 0) break;
            RemoveItems(c, remove);
        }
        var failed = rules.Where(r => !r.Passed).ToList();
        c.Flags.Add($"front gate B (the final collision: {rules.Count} formation rules): {(failed.Count == 0 ? "all pass" : $"{failed.Count} fail")}; {left.Count} items left out in {rounds} rounds{(left.Count > 0 ? ": " + string.Join("; ", left.Take(30)) : "")}");
        foreach (var r in rules) c.Flags.Add("front gate B rule " + r);
        if (failed.Count > 0)
            throw new AshfallGateException($"front band {plan.Band}, seed {plan.Params.LayoutSeed}: gate B (the final collision) failed after {rounds} rounds: {string.Join(" | ", failed)}");
    }

    /// <summary>An item's drawn box on the ground plane (axis bounds of its turned footprint).</summary>
    static Vector4 ItemRect(AshfallDressItem it)
    {
        float s = MathF.Sin(it.Yaw), co = MathF.Cos(it.Yaw);
        float ex = MathF.Abs(co) * it.Size.X / 2f + MathF.Abs(s) * it.Size.Y / 2f, ey = MathF.Abs(s) * it.Size.X / 2f + MathF.Abs(co) * it.Size.Y / 2f;
        return new Vector4(it.Position.X - ex, it.Position.Y - ey, it.Position.X + ex, it.Position.Y + ey);
    }

    static float RectDistance(Vector4 r, Vector2 p)
    {
        float dx = MathF.Max(MathF.Max(r.X - p.X, p.X - r.Z), 0f), dy = MathF.Max(MathF.Max(r.Y - p.Y, p.Y - r.W), 0f);
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}
