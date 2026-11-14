using System.Numerics;

namespace SmSim.Extras.Musou.Ashfall;

/// <summary>
/// Draft 13's enforcement (owner, 2026-11-14: "ensure that there are no dead end stairs, no stairs with blocked entrances, and no weird things like a wall in front of a door"; docs/musou-ashfall-graybox.md "Draft 13 heights"):
/// on the final collision of the heights layout, a flight that leads nowhere (<see cref="AshfallHighGround"/>), a stair that cannot be reached at both ends (<see cref="AshfallStairAccess"/>) and a door with another item in front
/// of it (<see cref="AshfallDoorClearance"/>) are put right the way the stair-access rule does it: the other item found in the way is left out, or else the item that holds the stair or the door. The stronghold's authored pieces
/// are checked and reported, never changed. Draft 12's city and the other layouts are untouched.
/// </summary>
internal static partial class AshfallDraft5Pass
{
    /// <summary>The most rounds of the enforcement (each leaves out what is found; ours).</summary>
    const int HeightsRounds = 8;

    static void EnforceHighGround(Ctx c, AshfallLevel g5, AshfallFieldPlan plan)
    {
        var start = new Vector2((plan.Assembly[0].Rect.X + plan.Assembly[0].Rect.Z) / 2f, (plan.Assembly[0].Rect.Y + plan.Assembly[0].Rect.W) / 2f);
        var left = new List<string>();
        int rounds = 0, deadLast = 0, doorsLast = 0, failLast = 0;
        for (; rounds < HeightsRounds; rounds++)
        {
            var authored = new HashSet<int>();
            foreach (var l in c.Log) if (l.Item >= 0 && AshfallStairAccess.IsAuthored(l.Group, l.Family, l.Asset)) authored.Add(l.Item);
            var level = new AshfallLevel(c.Parts, g5.Points, g5.Areas, g5.Spawns, 0UL) { Layout = plan.Layout };
            var dressing = new AshfallDressing(c.Items, [], 0u) { ItemParts = c.ItemParts };
            var access = AshfallStairAccess.Check(level, dressing, authored.Contains, start);
            var fails = access.Where(s => s.Problem is not null && !s.Authored).ToList();
            var dead = AshfallHighGround.Analyse(level, dressing, access, null).DeadEnds.Where(d => !access[d.Stair].Authored).ToList();
            var doors = AshfallDoorClearance.Find(level, dressing).Where(d => d.Blocker >= 0 && !(authored.Contains(d.Item) && authored.Contains(d.Blocker))).ToList();
            failLast = fails.Count; deadLast = dead.Count; doorsLast = doors.Count;
            var remove = new SortedSet<int>();
            string Name(int k) => c.Log.FirstOrDefault(l => l.Item == k) is { } pl ? $"{pl.Group} ({pl.Asset})" : $"item {k}";
            foreach (var f in fails)
            {
                int k = f.Blocker >= 0 && !authored.Contains(f.Blocker) ? f.Blocker : f.Ramp.Item;
                if (k >= 0 && !authored.Contains(k) && remove.Add(k)) left.Add($"{Name(k)}: {(k == f.Ramp.Item ? f.Problem : "in the way of a stair")}");
            }
            foreach (var (s, why) in dead)
            {
                int k = access[s].Ramp.Item;
                if (k >= 0 && !authored.Contains(k) && remove.Add(k)) left.Add($"{Name(k)}: a flight that leads nowhere ({why})");
            }
            foreach (var d in doors)
            {
                int k = d.Blocker >= 0 && !authored.Contains(d.Blocker) ? d.Blocker : d.Item;
                if (!authored.Contains(k) && remove.Add(k)) left.Add($"{Name(k)}: {(k == d.Item ? "its door is walled in" : "in front of a door of " + Name(d.Item))}");
            }
            if (remove.Count == 0) break;
            RemoveItems(c, remove);
        }
        c.Flags.Add($"draft 13 high ground (no flight that leads nowhere, no stair with a blocked foot or landing, no item within {AshfallDoorClearance.Depth:0} m in front of a door; the authored pieces exempt): {left.Count} items left out in {rounds} rounds" +
            $"{(left.Count > 0 ? ": " + string.Join("; ", left.Take(30)) : "")}{(deadLast + doorsLast + failLast > 0 ? $"; still open after {HeightsRounds} rounds: {deadLast} dead ends, {doorsLast} doors, {failLast} stairs" : "")}");
    }
}
