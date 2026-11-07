using SmContent;

namespace SmFormats.Tests;

/// <summary>
/// The scoreboard's icons (the owner, 2026-11-07): the shoulder emblem a player had when it first appeared on the scoreboard, kept while it stays, taken again only after it left and
/// came back (<see cref="JoinIcons{TIcon}"/>).
/// </summary>
public class JoinIconsTests
{
    sealed record Icon(string Name);

    static readonly Icon Eagle = new("eagle"), Skull = new("skull"), Fist = new("fist");

    [Fact]
    public void The_icon_is_the_one_a_player_had_when_it_joined_and_a_later_change_is_ignored()
    {
        var icons = new JoinIcons<Icon>();
        var wears = new Dictionary<int, Icon> { [1] = Eagle, [2] = Skull };
        Assert.Equal([1, 2], icons.Observe([1, 2], id => wears.GetValueOrDefault(id)));
        wears[1] = Fist;   // a new look in the match (the in-match customizer)
        Assert.Empty(icons.Observe([1, 2], id => wears.GetValueOrDefault(id)));
        Assert.True(icons.TryGet(1, out var one));
        Assert.Same(Eagle, one);
    }

    [Fact]
    public void A_player_who_leaves_and_returns_shows_its_new_emblem()
    {
        var icons = new JoinIcons<Icon>();
        var wears = new Dictionary<int, Icon> { [1] = Eagle, [2] = Skull };
        icons.Observe([1, 2], id => wears.GetValueOrDefault(id));
        icons.Observe([2], id => wears.GetValueOrDefault(id));   // 1 left
        Assert.False(icons.TryGet(1, out _));
        wears[1] = Fist;   // a new emblem picked in the lobby
        Assert.Equal([1], icons.Observe([1, 2], id => wears.GetValueOrDefault(id)));   // back before the next game
        Assert.True(icons.TryGet(1, out var back));
        Assert.Same(Fist, back);
        Assert.True(icons.TryGet(2, out var two));
        Assert.Same(Skull, two);
    }

    [Fact]
    public void A_player_whose_look_is_not_known_yet_takes_the_first_one_known()
    {
        var icons = new JoinIcons<Icon>();
        var wears = new Dictionary<int, Icon>();
        Assert.Empty(icons.Observe([5], id => wears.GetValueOrDefault(id)));
        Assert.False(icons.TryGet(5, out _));
        wears[5] = Skull;
        Assert.Equal([5], icons.Observe([5], id => wears.GetValueOrDefault(id)));
        wears[5] = Eagle;
        icons.Observe([5], id => wears.GetValueOrDefault(id));
        Assert.True(icons.TryGet(5, out var icon));
        Assert.Same(Skull, icon);
    }

    [Fact]
    public void Clear_forgets_everyone()
    {
        var icons = new JoinIcons<Icon>();
        icons.Observe([1], _ => Eagle);
        icons.Clear();
        Assert.Equal(0, icons.Count);
        icons.Observe([1], _ => Fist);
        Assert.True(icons.TryGet(1, out var icon));
        Assert.Same(Fist, icon);
    }
}
