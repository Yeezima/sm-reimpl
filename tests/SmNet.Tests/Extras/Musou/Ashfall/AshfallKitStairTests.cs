using System.Numerics;
using SmSim.Extras.Musou.Ashfall;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Musou.Ashfall;

/// <summary>
/// The owner's stair pass (2026-11-09: "fix the greybox collision for the rest of the 3d models that have stairs - you can't walk up all the stairs currently"; docs/musou-ashfall-graybox.md "Draft 12 city layout", "Kit stairs"):
/// every stair-bearing kit model, at every size and turn it stands at on the two kit maps (the city <c>tfw_war</c> and the field <c>tfw_war_field</c>), walked up and down every flight by the player's capsule with the game's movement
/// on its real collision (alone, then in the level). The stronghold's pieces are taken from draft 10 as authored (the owner decides on them) and draft 10 itself is the frozen rollback.
/// </summary>
public class AshfallKitStairTests(ITestOutputHelper output)
{
    /// <summary>Notes that name a visible flight a player cannot use (the draft-10 stair-access rule's list, the access check's unreached flights).</summary>
    static bool Dead(string note) =>
        note.Contains("is open") || note.Contains("foot stands") || note.Contains("no landing") || note.Contains("stalls") || note.Contains("lifts") || note.Contains("does not arrive") || note.Contains("degrees after") || note.Contains("no flight or the ground");

    public static TheoryData<AshfallLayout> Layouts => new() { AshfallLayout.City, AshfallLayout.Field };

    [Theory]
    [MemberData(nameof(Layouts))]
    public void Every_stair_model_walks_every_flight_at_every_size_it_stands_at(AshfallLayout layout)
    {
        var d = AshfallMap.BuildDraft5(layout);
        var placed = d.Placements.Where(p => p.Item >= 0 && AshfallKitCollision.Stairs(p.Asset).Count > 0 && !AshfallStairAccess.IsAuthored(p.Group, p.Family, p.Asset)).ToList();
        Assert.NotEmpty(placed);
        var bad = new List<string>();
        var perModel = new SortedDictionary<string, (int Placed, int Kept)>(StringComparer.Ordinal);
        foreach (var p in placed)
        {
            var it = d.Dressing.Items[p.Item];
            int yaw = ((int)MathF.Round(it.Yaw / (MathF.PI / 12f)) % 24 + 24) % 24;
            var r = AshfallMap.KitStairPlan(p.Asset, it.Size, yaw, p.Family);
            var dead = r.Notes.Where(Dead).Concat(r.Unreached).ToList();
            if (dead.Count > 0) bad.Add($"{p.Group} ({p.Asset}, {it.Size.X:0.0} x {it.Size.Y:0.0} x {it.Size.Z:0.0}): {string.Join("; ", dead)}");
            var (n, k) = perModel.GetValueOrDefault(p.Asset);
            perModel[p.Asset] = (n + 1, k + r.Kept);
        }
        foreach (var (id, (n, k)) in perModel) output.WriteLine($"{layout} {id}: {n} placed, {k} flights walked");
        foreach (var b in bad) output.WriteLine(b);
        Assert.Empty(bad);
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public void Every_stair_of_the_level_is_walked_up_and_down_on_the_final_collision(AshfallLayout layout)
    {
        var res = AshfallStairAccess.Check(AshfallMap.BuildDraft5(layout));
        output.WriteLine($"{layout}: {res.Count} stairs");
        foreach (var r in res.Where(r => r.Problem is not null)) output.WriteLine($"{(r.Authored ? "[authored] " : "")}item {r.Ramp.Item} at ({r.Ramp.Low.X:0.0}, {r.Ramp.Low.Y:0.0}): {r.Problem}");
        Assert.Empty(res.Where(r => r.Problem is not null && !r.Authored).Select(r => r.Problem));
        Assert.Contains(AshfallMap.BuildDraft5(layout).Dressing.Flags, f => f.StartsWith("draft 12 stair access") && !f.Contains("still fail"));
    }

    [Fact]
    public void The_kit_models_flights_at_their_design_size()
    {
        // the report of the pass: each stair-bearing model alone at its catalogue size (models the maps never place included)
        foreach (var id in new[] { "AC-005", "AC-006", "AC-007", "AC-008", "AC-010", "AC-037", "AC-044", "AC-046", "AC-047", "AC-049", "AC-050", "AC-051", "AC-052", "AC-053", "AC-076" })
        {
            var m = AshfallKit.Get(id);
            Assert.True(AshfallKitCollision.Stairs(id).Count > 0, id);
            if (m.DesignScale <= 0f) { output.WriteLine($"{id}: no catalogue size"); continue; }
            var r = AshfallMap.KitStairPlan(id, new Vector3(m.DesignSize.X, m.DesignSize.Z, m.DesignSize.Y), 0);
            output.WriteLine($"{id} ({m.Name}): {r.Found} found, {r.Kept} walked; {string.Join("; ", r.Notes.Concat(r.Unreached))}");
        }
        // the fixed ones: the two-storey ruin (entry ramp onto its plinth), the defensive ruin (landing slabs on its broken floor), the perches, platforms and stair towers
        foreach (var (id, size) in new[] { ("AC-051", new Vector3(16f, 14f, 13f)), ("AC-005", new Vector3(18f, 13f, 13f)), ("AC-008", new Vector3(12f, 6f, 6f)), ("AC-050", new Vector3(8f, 8f, 5f)) })
        {
            var r = AshfallMap.KitStairPlan(id, size, 0);
            Assert.True(r.Kept >= 1, $"{id}: {string.Join("; ", r.Notes)}");
        }
    }
}
