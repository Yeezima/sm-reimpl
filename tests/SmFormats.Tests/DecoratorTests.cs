using SmContent;
using Xunit;

namespace SmFormats.Tests;

/// <summary>Fidelity gap 10: the kill feed's picture labels and the world decorators' rules and sprite tables. No install bytes.</summary>
public class DecoratorTests
{
    // The frame labels of the feed movie's weapon_icon_mc (HudEventLog.gfx).
    static readonly string[] MovieLabels =
    [
        "bolter", "heavy_bolter", "laz_cannon", "plasma_cannon", "plasma_gun", "plasma_pistol", "stalker", "power_axe", "thunder_hammer", "knife", "chainsword",
        "storm_bolter", "bolt_pistol", "grenade", "krakgrenades", "melta", "grenade_launcher", "powersword", "reactiveblast", "jumppack", "bullrush", "stomp", "dreadnought",
    ];

    [Fact]
    public void Feed_icons_are_the_movies_labels_each_once_inside_the_atlas()
    {
        Assert.Equal(MovieLabels.OrderBy(x => x), FeedIcons.All.Select(i => i.Label).OrderBy(x => x));
        foreach (var icon in FeedIcons.All) Assert.True(icon.Sprite.InsideTexture, icon.Label);
        // 23 distinct rectangles but the krak grenade, which mirrors the frag grenade's.
        Assert.Equal(FeedIcons.All.Count - 1, FeedIcons.All.Select(i => (i.X, i.Y)).Distinct().Count());
        Assert.Equal(("grenade", "krakgrenades"), (FeedIcons.Find("grenade")!.Label, FeedIcons.Find("KRAKGRENADES")!.Label));
        Assert.True(FeedIcons.Find("krakgrenades")!.FlipX && FeedIcons.Find("knife")!.FlipY);
        Assert.Null(FeedIcons.Find("nothing"));
    }

    [Theory]
    [InlineData("sm_mp_ranged_bolter", "bolter", null, "bolter")]
    [InlineData("csm_mp_exotic_las_cannon", "laz_cannon", "", "laz_cannon")]
    [InlineData("sm_mp_core_abil_jump_evade", "bullrush", null, "bullrush")]
    [InlineData("mp_perk_shield_break_damage", null, "reactiveblast", "reactiveblast")]
    [InlineData("mp_perk_jump_pack_explodes_on_death", "", "jumppack", "jumppack")]
    [InlineData("sm_mp_melee_x", "knife", "stomp", "stomp")]            // killed_by_weapon_type wins where the file sets it
    [InlineData("sm_mp_exotic_las_cannon", "lascannon", null, "laz_cannon")]
    [InlineData("default", null, null, "bolter")]                        // no data: from the name
    [InlineData("sm_mp_eq_melta_bomb", "plasma_grenade", null, null)]    // no picture in the movie
    [InlineData("whatever", null, null, null)]
    public void Feed_label_follows_the_kill_type_then_the_weapon_type_then_the_name(string wargear, string? type, string? killedBy, string? expected) =>
        Assert.Equal(expected, HudRules.FeedLabel(wargear, type, killedBy));

    [Fact]
    public void Art_answers_the_feed_label_from_its_two_fields()
    {
        var art = new UiArt(new Dictionary<string, byte[]>(), new Dictionary<string, string> { ["sm_mp_ranged_melta"] = "melta" }, null,
            new Dictionary<string, string> { ["mp_perk_better_stomp"] = "stomp" });
        Assert.Equal("melta", art.FeedLabel("sm_mp_ranged_melta"));
        Assert.Equal("stomp", art.FeedLabel("mp_perk_better_stomp"));
        Assert.Equal("stomp", art.KilledByType("MP_PERK_BETTER_STOMP"));
        Assert.Null(art.FeedLabel(""));
        Assert.Null(UiArt.Empty.FeedLabel("x"));
    }

    [Fact]
    public void Decorator_sprites_lie_inside_their_atlases_and_the_icons_are_wide_wings()
    {
        foreach (var s in DecoratorSprites.All) Assert.True(s.InsideTexture, $"{s.Texture} {s.X},{s.Y}");
        Assert.Equal(DecoratorSprites.EagleAlly, DecoratorSprites.Icon(chaos: false, enemy: false));
        Assert.Equal(DecoratorSprites.StarEnemy, DecoratorSprites.Icon(chaos: true, enemy: true));
        Assert.Equal(new[] { "decorator_i1", "decorator_i3", "decorator_i10" }, DecoratorSprites.Textures);
    }

    [Theory]
    [InlineData(DecoratorRelation.None, false, DecoratorRelation.None, false, false, "neutral")]
    [InlineData(DecoratorRelation.None, false, DecoratorRelation.Ally, true, false, "neutral_flash_ally")]
    [InlineData(DecoratorRelation.None, false, DecoratorRelation.Enemy, true, false, "neutral_flash_enemy")]
    [InlineData(DecoratorRelation.None, false, DecoratorRelation.Enemy, false, false, "neutral")]               // a frozen partial capture does not blink
    [InlineData(DecoratorRelation.None, false, DecoratorRelation.Ally, false, true, "neutral_flash_ally_combat")]
    [InlineData(DecoratorRelation.Ally, false, DecoratorRelation.Ally, false, false, "ally_sm")]
    [InlineData(DecoratorRelation.Ally, true, DecoratorRelation.Ally, false, false, "ally_chaos")]
    [InlineData(DecoratorRelation.Ally, true, DecoratorRelation.Ally, true, false, "ally_chaos_flash")]          // an enemy is uncapping it
    [InlineData(DecoratorRelation.Enemy, false, DecoratorRelation.Enemy, true, false, "enemy_sm_flash")]
    [InlineData(DecoratorRelation.Enemy, true, DecoratorRelation.Enemy, false, true, "enemy_chaos_combat_flash")]
    [InlineData(DecoratorRelation.Ally, false, DecoratorRelation.Ally, false, true, "ally_sm_combat_flash")]
    public void Point_labels_follow_owner_race_activity_and_combat(DecoratorRelation owner, bool chaos, DecoratorRelation side, bool working, bool contested, string label) =>
        Assert.Equal(label, DecoratorRules.PointLabel(owner, chaos, side, working, contested));

    [Fact]
    public void Every_point_label_is_one_of_the_movies_states()
    {
        // The anim_mc frame labels of Decorator.gfx's DominationPoint that a Seize Ground point can show.
        var movie = new HashSet<string>
        {
            "neutral", "enemy_chaos", "enemy_sm", "ally_chaos", "ally_sm", "neutral_flash_ally", "neutral_flash_enemy", "enemy_chaos_flash", "enemy_sm_flash",
            "ally_chaos_flash", "ally_sm_flash", "ally_sm_combat_flash", "enemy_chaos_combat_flash", "neutral_flash_ally_combat", "neutral_flash_enemy_combat",
            "enemy_sm_combat_flash", "ally_chaos_combat_flash",
        };
        var relations = new[] { DecoratorRelation.None, DecoratorRelation.Ally, DecoratorRelation.Enemy };
        foreach (var owner in relations) foreach (var side in relations) foreach (var chaos in new[] { false, true })
        foreach (var working in new[] { false, true }) foreach (var contested in new[] { false, true })
            Assert.Contains(DecoratorRules.PointLabel(owner, chaos, side, working, contested), movie);
    }

    [Fact]
    public void Flash_states_blink_in_the_movies_loops()
    {
        Assert.True(DecoratorRules.IsFlash("ally_sm_flash") && DecoratorRules.IsFlash("neutral_flash_enemy") && !DecoratorRules.IsFlash("ally_sm"));
        Assert.True(DecoratorRules.IsCombat("enemy_sm_combat_flash") && !DecoratorRules.IsCombat("enemy_sm_flash"));
        // Starts bright, ends near 10 %, then the loop restarts (flash: 30 frames = 1 s; combat flash: 10 frames).
        Assert.InRange(DecoratorRules.FlashAlpha(0.0, false), 0.99f, 1f);
        Assert.InRange(DecoratorRules.FlashAlpha(0.95, false), 0.09f, 0.11f);
        Assert.InRange(DecoratorRules.FlashAlpha(1.0, false), 0.99f, 1f);
        Assert.InRange(DecoratorRules.FlashAlpha(0.1, false), 0.80f, 0.82f);     // frame 4: 208 / 256
        Assert.InRange(DecoratorRules.FlashAlpha(0.0, true), 0.99f, 1f);
        Assert.InRange(DecoratorRules.FlashAlpha(0.3, true), 0.09f, 0.11f);
        Assert.InRange(DecoratorRules.FlashAlpha(1.0 / 3.0 + 0.001, true), 0.99f, 1f);
        float last = 2f;
        for (double t = 0; t < 0.9; t += 1.0 / 30.0) { float a = DecoratorRules.FlashAlpha(t, false); Assert.True(a <= last + 1e-6f); last = a; }
    }

    [Theory]
    [InlineData(0f, 1, 0f)]
    [InlineData(1f, 99, 1f)]
    [InlineData(0.5f, 50, 0.5f)]
    [InlineData(-1f, 1, 0f)]
    [InlineData(3f, 99, 1f)]
    public void Capture_bar_frames_run_from_empty_to_full(float progress, int frame, float fill)
    {
        Assert.Equal(frame, DecoratorRules.CaptureBarFrame(progress));
        Assert.Equal(fill, DecoratorRules.CaptureBarFill(frame), 3);
        Assert.Equal(0f, DecoratorRules.CaptureBarFill(0));
        Assert.Equal(1f, DecoratorRules.CaptureBarFill(100));
    }

    [Fact]
    public void Player_decorators_show_the_aimed_friend_or_foe_in_sight_within_range()
    {
        // ui/hud-3 (owner 2026-10-30): a name only over the player under the crosshair, enemy red and friend yellow.
        Assert.True(DecoratorRules.PlayerVisible(DecoratorRelation.Ally, 100f, lineOfSight: true));
        Assert.True(DecoratorRules.PlayerVisible(DecoratorRelation.Enemy, 80f, lineOfSight: true));
        Assert.False(DecoratorRules.PlayerVisible(DecoratorRelation.Ally, 30f, lineOfSight: false));
        Assert.False(DecoratorRules.PlayerVisible(DecoratorRelation.Enemy, 40f, lineOfSight: false));
        Assert.False(DecoratorRules.PlayerVisible(DecoratorRelation.Ally, 130f, lineOfSight: true));
        Assert.False(DecoratorRules.PlayerVisible(DecoratorRelation.Enemy, 130f, lineOfSight: true));
        Assert.False(DecoratorRules.PlayerVisible(DecoratorRelation.None, 5f, lineOfSight: true));
    }

    [Fact]
    public void Player_decorators_shrink_and_fade_with_distance()
    {
        Assert.Equal(1f, DecoratorRules.PlayerScale(5f));
        Assert.Equal(1f, DecoratorRules.PlayerScale(10f));
        Assert.Equal(0.55f, DecoratorRules.PlayerScale(DecoratorRules.AllyRange), 3);
        Assert.True(DecoratorRules.PlayerScale(60f) < DecoratorRules.PlayerScale(30f));
        Assert.Equal(1f, DecoratorRules.PlayerAlpha(DecoratorRelation.Ally, 50f));
        Assert.Equal(0f, DecoratorRules.PlayerAlpha(DecoratorRelation.Ally, DecoratorRules.AllyRange));
        Assert.Equal(1f, DecoratorRules.PlayerAlpha(DecoratorRelation.Enemy, 80f));
        Assert.Equal(0f, DecoratorRules.PlayerAlpha(DecoratorRelation.Enemy, DecoratorRules.AllyRange));
    }
}
