using SmContent;

namespace SmFormats.Tests;

/// <summary>
/// fix/fx-hud-leaks (polish item 27): a full effect player drops the lowest priority at or below the new effect's first, and the oldest only among equal priorities. The old pick,
/// "lower priority, or older", dropped an older effect at 0 before a younger one at -1 (the weight pass's puffs). Synthetic effects only.
/// </summary>
public class FxEvictionTests
{
    sealed record Fx(string Name, int Priority, double Age, bool Ambient = false);

    static int Pick(IReadOnlyList<Fx> effects, int priority) => FxEviction.Victim(effects, priority, static e => (e.Ambient, e.Priority, e.Age));

    /// <summary>The pick as it was before the fix (FxPlayer.cs:298 at main ce8aa368), to show the case it got wrong.</summary>
    static int OldPick(IReadOnlyList<Fx> e, int priority)
    {
        int victim = -1;
        for (int i = 0; i < e.Count; i++)
            if (!e[i].Ambient && e[i].Priority <= priority && (victim < 0 || e[i].Priority < e[victim].Priority || e[i].Age > e[victim].Age)) victim = i;
        return victim;
    }

    [Fact]
    public void The_lowest_priority_goes_before_an_older_effect_of_a_higher_one()
    {
        Fx[] effects =
        [
            new("hit 0 (5 s)", 0, 5.0),
            new("puff -1 (0.1 s)", -1, 0.1),
            new("hit 0 (9 s)", 0, 9.0),
            new("tracer 1 (20 s)", 1, 20.0),
            new("ambient -2 (50 s)", -2, 50.0, Ambient: true),
        ];
        Assert.Equal("puff -1 (0.1 s)", effects[Pick(effects, 0)].Name);
        Assert.Equal("hit 0 (9 s)", effects[OldPick(effects, 0)].Name);   // the bug: the older 0 went first
        Assert.Equal("puff -1 (0.1 s)", effects[Pick(effects, 2)].Name);   // a higher new effect may drop the tracer, but the puff still goes first
        Assert.Equal("puff -1 (0.1 s)", effects[Pick(effects, -1)].Name);
    }

    [Fact]
    public void Among_equal_priorities_the_oldest_goes()
    {
        Fx[] effects = [new("a", 1, 0.5), new("b", 1, 3.25), new("c", 1, 1.0), new("d", 2, 10.0)];
        Assert.Equal("b", effects[Pick(effects, 1)].Name);
        Assert.Equal("b", effects[Pick(effects, 3)].Name);
        // A delayed effect (negative age, not started) is the youngest.
        Fx[] delayed = [new("waiting", 0, -0.4), new("started", 0, 0.0)];
        Assert.Equal("started", delayed[Pick(delayed, 0)].Name);
        // Equal priority and age: the first in the list, as before.
        Fx[] tied = [new("first", 0, 1.0), new("second", 0, 1.0)];
        Assert.Equal(0, Pick(tied, 0));
    }

    [Fact]
    public void The_pick_is_the_same_in_any_order()
    {
        Fx[] effects = [new("p0 old", 0, 4.0), new("p-1 young", -1, 0.2), new("p-1 old", -1, 2.0), new("p0 oldest", 0, 8.0), new("p1", 1, 30.0), new("amb", -3, 99.0, Ambient: true)];
        foreach (var order in Permutations(effects))
        {
            Assert.Equal("p-1 old", order[Pick(order, 0)].Name);
            Assert.Equal("p-1 old", order[Pick(order, 1)].Name);
            Assert.Equal("p-1 old", order[Pick(order, -1)].Name);
        }
    }

    [Fact]
    public void Ambient_and_higher_effects_are_never_dropped()
    {
        Fx[] effects = [new("amb", -1, 50.0, Ambient: true), new("p2", 2, 9.0), new("p1", 1, 1.0)];
        Assert.Equal(-1, Pick(effects, 0));    // nothing at or below 0 but the ambient one: refused
        Assert.Equal("p1", effects[Pick(effects, 1)].Name);
        Assert.Equal("p1", effects[Pick(effects, 5)].Name);
        Assert.Equal(-1, Pick(Array.Empty<Fx>(), 0));
    }

    [Fact]
    public void Mixed_priorities_and_ages_drop_in_priority_order_then_age()
    {
        // Dropping one at a time from a full list for new effects of priority 3 empties it in priority order, oldest first within each.
        var live = new List<Fx>
        {
            new("p1 young", 1, 0.3), new("p0 young", 0, 0.1), new("p-1 old", -1, 6.0), new("p2", 2, 12.0), new("p0 old", 0, 7.0),
            new("p-1 young", -1, 0.05), new("p1 old", 1, 9.0), new("amb", 0, 40.0, Ambient: true),
        };
        var dropped = new List<string>();
        for (int v; (v = Pick(live, 3)) >= 0; live.RemoveAt(v)) dropped.Add(live[v].Name);
        Assert.Equal(["p-1 old", "p-1 young", "p0 old", "p0 young", "p1 old", "p1 young", "p2"], dropped);
        Assert.Equal("amb", Assert.Single(live).Name);
    }

    [Fact]
    public void Drops_before_compares_the_priority_first()
    {
        Assert.True(FxEviction.DropsBefore(-1, 0.1, 0, 9.0));
        Assert.False(FxEviction.DropsBefore(0, 9.0, -1, 0.1));
        Assert.True(FxEviction.DropsBefore(0, 9.0, 0, 5.0));
        Assert.False(FxEviction.DropsBefore(0, 5.0, 0, 5.0));
    }

    [Theory]
    [InlineData(int.MinValue, 0)]
    [InlineData(-5, 0)]
    [InlineData(-1, 0)]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 3)]
    [InlineData(3, 4)]
    [InlineData(4, 5)]
    [InlineData(9, 5)]
    [InlineData(int.MaxValue, 5)]
    public void The_counters_keep_minus_one_apart_from_zero(int priority, int slot)
    {
        Assert.Equal(slot, FxEviction.Slot(priority));
        Assert.InRange(FxEviction.Slot(priority), 0, FxEviction.Slots - 1);
    }

    [Fact]
    public void The_effect_player_uses_the_pick_and_the_slots()
    {
        string player = File.ReadAllText(Path.Combine(Root(), "client", "FxPlayer.cs"));
        Assert.Contains("FxEviction.Victim(_effects, priority, origin.DistanceTo(_camPos), _readVictim ??=", player);
        Assert.Contains("Refused[FxEviction.Slot(priority)]++", player);
        Assert.Contains("Evicted[FxEviction.Slot(_effects[victim].Priority)]++", player);
        Assert.Contains("new long[FxEviction.Slots]", player);
        Assert.DoesNotContain("|| _effects[i].Age > _effects[victim].Age", player);
    }

    sealed record Placed(string Name, int Priority, float Metres, double Age, bool Ambient = false);

    static int PickNear(IReadOnlyList<Placed> effects, int priority, float newMetres) =>
        FxEviction.Victim(effects, priority, newMetres, static e => (e.Ambient, e.Priority, e.Metres, e.Age));

    [Fact]
    public void Within_one_priority_the_farthest_goes_then_the_oldest()
    {
        Placed[] same = [new("a young", 1, 40.5f, 0.2), new("a old", 1, 41.5f, 5.0), new("near", 1, 3f, 99.0)];
        Assert.Equal("a old", same[PickNear(same, 1, 0f)].Name);           // within one distance step: the oldest
        Placed[] apart = [new("near old", 1, 3f, 99.0), new("far young", 1, 40f, 0.1)];
        Assert.Equal("far young", apart[PickNear(apart, 1, 0f)].Name);     // the farthest goes before an older, nearer one
    }

    [Fact]
    public void A_new_own_weapon_effect_evicts_a_distant_crowd_effect_never_the_reverse()
    {
        Placed[] full = [new("crowd far", FxPriority.Crowd, 70f, 1.0), new("crowd near", FxPriority.Crowd, 8f, 6.0), new("ally", FxPriority.Ally, 15f, 9.0), new("boss", FxPriority.Special, 30f, 9.0)];
        Assert.Equal("crowd far", full[PickNear(full, FxPriority.Own, 1f)].Name);
        Placed[] own = [new("own muzzle", FxPriority.Own, 1f, 0.1), new("own impact", FxPriority.Own, 20f, 0.2), new("hit", FxPriority.Hit, 5f, 0.3)];
        Assert.Equal(-1, PickNear(own, FxPriority.Crowd, 90f));   // the reverse: refused, nothing dropped
        Assert.Equal(2, PickNear(own, FxPriority.Hit, 5f));   // a hit tier effect may only take another hit tier one, never an own one
        Assert.True(PickNear(own, FxPriority.Own, 5f) >= 0);
    }

    [Fact]
    public void Tiers_run_from_the_own_weapon_down_to_the_crowd_and_the_pick_follows_them()
    {
        Assert.True(FxPriority.Own > FxPriority.Hit && FxPriority.Hit > FxPriority.Special && FxPriority.Special > FxPriority.Ally && FxPriority.Ally > FxPriority.Crowd && FxPriority.Crowd > FxPriority.Puff);
        var live = new List<Placed> { new("own", FxPriority.Own, 1f, 3.0), new("hit", FxPriority.Hit, 2f, 3.0), new("boss", FxPriority.Special, 2f, 3.0), new("ally", FxPriority.Ally, 2f, 3.0), new("crowd", FxPriority.Crowd, 2f, 3.0), new("puff", FxPriority.Puff, 2f, 3.0) };
        var dropped = new List<string>();
        for (int v; (v = PickNear(live, FxPriority.Own, 0f)) >= 0; live.RemoveAt(v)) dropped.Add(live[v].Name);
        Assert.Equal(["puff", "crowd", "ally", "boss", "hit", "own"], dropped);
    }

    [Fact]
    public void A_new_effect_farther_than_every_effect_of_its_priority_is_the_one_refused()
    {
        Placed[] live = [new("a", 0, 10f, 5.0), new("b", 0, 20f, 1.0)];
        Assert.Equal(-1, PickNear(live, 0, 80f));    // the new one would be the first to go: nothing nearer is dropped for it
        Assert.Equal(1, PickNear(live, 0, 12f));     // nearer than the farthest: the farthest goes
        Assert.Equal(1, PickNear(live, 1, 80f));     // a higher priority is not refused for its distance
    }

    [Fact]
    public void The_sources_use_the_tiers()
    {
        string client = Path.Combine(Root(), "client");
        string fx = File.ReadAllText(Path.Combine(client, "NetworkView.Fx.cs"));
        Assert.Contains("own ? FxPriority.Own : FxPriority.Ally", fx);   // the own muzzle
        Assert.Contains("own ? FxPriority.Own :", fx);                    // the own impact
        Assert.Contains("FxPriority.Hit", File.ReadAllText(Path.Combine(client, "NetworkView.HitFeedback.cs")));
        Assert.Contains("own ? FxPriority.Own : FxPriority.Ally", File.ReadAllText(Path.Combine(client, "NetworkView.Bullets.cs")));
        Assert.Contains("FxPriority.Special", File.ReadAllText(Path.Combine(client, "Extras", "Musou", "Tfw", "NetworkView.TfwDropPod.cs")));
        Assert.Contains("= SmContent.FxPriority.Special", File.ReadAllText(Path.Combine(client, "Extras", "Musou", "NetworkView.MusouWeirdboy.cs")));
    }

    static IEnumerable<Fx[]> Permutations(Fx[] items)
    {
        if (items.Length <= 1) { yield return items; yield break; }
        for (int i = 0; i < items.Length; i++)
        {
            var rest = items.Where((_, j) => j != i).ToArray();
            foreach (var tail in Permutations(rest)) yield return [items[i], .. tail];
        }
    }

    static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "SmReimpl.slnx"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("SmReimpl.slnx");
    }
}
