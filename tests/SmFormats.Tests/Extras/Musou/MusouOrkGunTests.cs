using SmContent;
using SmContent.Extras.Musou;
using SmFormats.Baf;
using SmSim;
using SmSim.Extras.Musou;

namespace SmFormats.Tests.Extras.Musou;

/// <summary>
/// The Musou Orks' guns (<see cref="MusouOrkGunRules"/>, <see cref="MusouGunFxPlanner"/>, <see cref="MusouOrkGunEffects"/>): which type carries which gun, which rounds show a tracer, the distance culls
/// and the rate caps, and which effects a gun's wargear gives a round. Synthetic files shaped like the install's (names and values invented).
/// </summary>
public class MusouOrkGunTests
{
    // ---------------------------------------------------------------- rules

    [Fact]
    public void Only_the_shoota_boy_and_the_rokkit_boy_carry_a_gun_and_the_shoota_is_the_ork_class_shoota()
    {
        Assert.Equal(OrkWeaponMounts.Shoota, MusouOrkGunRules.WargearOf(MusouEnemyType.ShootaBoy));
        Assert.Equal("ork_coop_boy_rokkit_launcha", MusouOrkGunRules.WargearOf(MusouEnemyType.RokkitBoy));   // expansion S2
        foreach (var t in Enum.GetValues<MusouEnemyType>().Where(t => t is not (MusouEnemyType.ShootaBoy or MusouEnemyType.RokkitBoy))) Assert.Null(MusouOrkGunRules.WargearOf(t));
    }

    [Fact]
    public void Each_ork_shows_one_tracer_in_every_tracer_every_rounds()
    {
        for (ushort id = 1; id < 200; id++)
        {
            var rounds = Enumerable.Range(0, MusouOrkGunRules.TracerEvery * 4).Where(r => MusouOrkGunRules.TracerRound(id, r)).ToList();
            Assert.Equal(4, rounds.Count);
            for (int k = 1; k < rounds.Count; k++) Assert.Equal(MusouOrkGunRules.TracerEvery, rounds[k] - rounds[k - 1]);
        }
        // Not every Ork starts on the same round: a crowd's tracers do not flash together.
        var firsts = Enumerable.Range(1, 60).Select(id => Enumerable.Range(0, MusouOrkGunRules.TracerEvery).First(r => MusouOrkGunRules.TracerRound((ushort)id, r))).Distinct().Count();
        Assert.True(firsts > 1);
    }

    // ---------------------------------------------------------------- the planner

    static MusouGunFx[] Plan(MusouGunFxPlanner planner, params MusouGunShot[] shots)
    {
        var result = new MusouGunFx[shots.Length];
        planner.Plan(shots, result);
        return result;
    }

    /// <summary>A round of an Ork whose tracer round it is (his round counter at 0).</summary>
    static ushort TracerOrk(int start = 1) => (ushort)Enumerable.Range(start, 500).First(id => MusouOrkGunRules.TracerRound((ushort)id, 0));
    static ushort PlainOrk(int start = 1) => (ushort)Enumerable.Range(start, 500).First(id => !MusouOrkGunRules.TracerRound((ushort)id, 0));

    [Fact]
    public void A_near_round_gets_flash_and_impact_and_its_tracer_on_a_tracer_round()
    {
        var fx = Plan(new MusouGunFxPlanner(), new MusouGunShot(TracerOrk(), 10, 10f, false), new MusouGunShot(PlainOrk(), 10, 12f, true));
        Assert.Equal(MusouGunFx.Flash | MusouGunFx.Tracer | MusouGunFx.Impact, fx[0]);
        Assert.Equal(MusouGunFx.Flash | MusouGunFx.Impact, fx[1]);
    }

    [Fact]
    public void Distance_culls_impact_then_flash_then_everything()
    {
        ushort id = TracerOrk();
        float between = (MusouOrkGunRules.ImpactMetres + MusouOrkGunRules.FlashMetres) / 2f, beyond = (MusouOrkGunRules.FlashMetres + MusouOrkGunRules.TracerMetres) / 2f;
        Assert.Equal(MusouGunFx.Flash | MusouGunFx.Tracer, Plan(new MusouGunFxPlanner(), new MusouGunShot(id, 1, between, false))[0]);
        Assert.Equal(MusouGunFx.Tracer, Plan(new MusouGunFxPlanner(), new MusouGunShot(id, 1, beyond, false))[0]);
        var far = new MusouGunFxPlanner();
        Assert.Equal(MusouGunFx.None, Plan(far, new MusouGunShot(id, 1, MusouOrkGunRules.TracerMetres + 1f, false))[0]);
        Assert.Equal(MusouGunFx.None, Plan(far, new MusouGunShot(id, 2, float.NaN, false))[0]);
        Assert.Equal(2, far.TooFar);
    }

    [Fact]
    public void A_frame_full_of_rounds_is_held_to_the_bursts_nearest_first()
    {
        var planner = new MusouGunFxPlanner();
        // 100 Orks' rounds in one frame, all their tracer rounds, given farthest first.
        var shots = Enumerable.Range(1, 2000).Where(id => MusouOrkGunRules.TracerRound((ushort)id, 0)).Take(100)
            .Select((id, k) => new MusouGunShot((ushort)id, 5, 30f - k * 0.25f, false)).ToArray();
        var fx = Plan(planner, shots);
        Assert.Equal((int)MusouOrkGunRules.FlashBurst, fx.Count(f => f.HasFlag(MusouGunFx.Flash)));
        Assert.Equal((int)MusouOrkGunRules.TracerBurst, fx.Count(f => f.HasFlag(MusouGunFx.Tracer)));
        Assert.Equal((int)MusouOrkGunRules.ImpactBurst, fx.Count(f => f.HasFlag(MusouGunFx.Impact)));
        // The nearest ones got them: the last given are the nearest.
        var nearest = Enumerable.Range(0, shots.Length).OrderBy(i => shots[i].Distance).Take((int)MusouOrkGunRules.FlashBurst).ToHashSet();
        for (int i = 0; i < shots.Length; i++) Assert.Equal(nearest.Contains(i), fx[i].HasFlag(MusouGunFx.Flash));
        Assert.True(planner.OverRate >= 100 - (int)MusouOrkGunRules.FlashBurst);
    }

    [Fact]
    public void The_buckets_refill_at_their_rates_and_no_further_than_their_bursts()
    {
        var planner = new MusouGunFxPlanner();
        MusouGunShot[] Volley(uint tick) => Enumerable.Range(1, 60).Select(id => new MusouGunShot((ushort)id, tick, 5f, true)).ToArray();
        Plan(planner, Volley(1));   // empties every bucket
        planner.Refill(1f / MusouOrkGunRules.FlashesPerSecond);   // one flash's worth
        var fx = Plan(planner, Volley(2));
        Assert.Equal(1, fx.Count(f => f.HasFlag(MusouGunFx.Flash)));
        planner.Refill(1000f);   // a long pause: back to the bursts, no more
        fx = Plan(planner, Volley(3));
        Assert.Equal((int)MusouOrkGunRules.FlashBurst, fx.Count(f => f.HasFlag(MusouGunFx.Flash)));
        Assert.Equal((int)MusouOrkGunRules.ImpactBurst, fx.Count(f => f.HasFlag(MusouGunFx.Impact)));
        Assert.True(fx.Count(f => f.HasFlag(MusouGunFx.Tracer)) <= (int)MusouOrkGunRules.TracerBurst);
        // Over a long fight the effects a second hold to the rates.
        var steady = new MusouGunFxPlanner();
        long before = 0;
        for (int frame = 0; frame < 600; frame++)
        {
            steady.Refill(1f / 60f);
            Plan(steady, Volley((uint)(10 + frame)));
            if (frame == 59) before = steady.Flashes;
        }
        float perSecond = (steady.Flashes - before) / 9f;
        Assert.InRange(perSecond, MusouOrkGunRules.FlashesPerSecond * 0.95f, MusouOrkGunRules.FlashesPerSecond * 1.05f);
    }

    [Fact]
    public void The_tracer_cadence_counts_every_round_drawn_or_not()
    {
        var planner = new MusouGunFxPlanner();
        ushort id = TracerOrk();
        var marks = new List<bool>();
        for (int r = 0; r < MusouOrkGunRules.TracerEvery * 3; r++)
        {
            planner.Refill(1000f);
            // Every other round far away (nothing drawn), the rest near: the near tracers still fall on the Ork's own cadence.
            float distance = r % 2 == 0 ? 5f : MusouOrkGunRules.TracerMetres + 10f;
            var fx = Plan(planner, new MusouGunShot(id, (uint)r, distance, false));
            marks.Add(fx[0].HasFlag(MusouGunFx.Tracer));
            Assert.Equal(distance <= MusouOrkGunRules.TracerMetres && MusouOrkGunRules.TracerRound(id, r), marks[^1]);
        }
        Assert.Equal(MusouOrkGunRules.TracerEvery * 3, planner.Rounds);
    }

    [Fact]
    public void Rounds_in_one_frame_count_in_tick_order()
    {
        var planner = new MusouGunFxPlanner();
        ushort id = TracerOrk();
        // Two of his rounds in one frame, the later first in the list: the earlier (tick 7) is round 0, his tracer round.
        var fx = Plan(planner, new MusouGunShot(id, 8, 5f, false), new MusouGunShot(id, 7, 5f, false));
        Assert.False(fx[0].HasFlag(MusouGunFx.Tracer));
        Assert.True(fx[1].HasFlag(MusouGunFx.Tracer));
    }

    [Fact]
    public void A_short_result_buffer_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new MusouGunFxPlanner().Plan([new MusouGunShot(1, 1, 1f, false)], []));
    }

    // ---------------------------------------------------------------- the effects a gun's wargear gives

    static (string, object)[] PlayFx(string name, string marker = "", bool attached = false, string target = "", string direction = "negative_hit_direction") =>
    [
        ("$ref", "action_commands\\play_fx"), ("fx_package", "test_fx"), ("fx_name", name), ("scale", 1f), ("length", 1f), ("attach_to_marker", attached),
        ("trigger_point", "self"), ("marker", new (string, object)[] { ("marker", marker) }), ("trigger_point_target", target),
        ("marker_target", Array.Empty<(string, object)>()), ("rotation_source", "trigger_point"), ("trigger_direction", direction), ("scale_if_from_player", false),
        ("on_stop_policy", "stop_emitting"), ("lifetime", 0f), ("apply_only_if_on_player", false),
    ];

    const string Container = "material_effect_container\\weapon_hits\\orks\\ranged_weapons\\test_gun_coop";

    /// <summary>A shoota-shaped gun: the muzzle flash in fire_fx, the tracer (to the hit point, on the muzzle) and the hit container in hit_fx, a melee bash with a container of its own.</summary>
    static byte[] Gun() => SyntheticBafWriter.Build(
        ("common_data", new (string, object)[] { ("slot", "ranged") }),
        ("ability_data", new (string, object)[] { ("ability_definitions", new (string, object)[]
        {
            ("ability_melee_light", new (string, object)[] { ("primary_target_actions", new (string, object)[] { ("hit_fx", new (string, object)[] { ("actions", new (string, object)[]
                { ("trigger_material_effect", new (string, object)[] { ("$ref", "action_commands\\trigger_material_effect"), ("effect_container", "material_effect_container\\weapon_hits\\orks\\melee_weapons\\test_fist"), ("material_source", "hit_material") }) }) }) }) }),
            ("ability_ranged_primary", new (string, object)[] { ("delivery", new (string, object)[]
            {
                ("fire_fx", new (string, object)[] { ("actions", new (string, object)[] { ("play_fx", PlayFx("test_gun_muzzle", "fx_muzzle")) }) }),
                ("hit_fx", new (string, object)[] { ("actions", new (string, object)[]
                {
                    ("trigger_material_effect", new (string, object)[] { ("$ref", "action_commands\\trigger_material_effect"), ("effect_container", Container), ("material_source", "hit_material") }),
                    ("play_fx", PlayFx("test_gun_tracer", "fx_muzzle", attached: true, target: "hitpoint")),
                }) }),
                ("muzzle_marker", "fx_muzzle"), ("max_range", 100f),
            }) }),
        }) }));

    static byte[] Hits() => SyntheticBafWriter.Build(("fx", new (string, object)[]
    {
        ("default", new (string, object)[] { ("default", new (string, object)[] { ("play_fx", PlayFx("test_gun_dmg", direction: "hitpoint_normal")), ("play_fx", PlayFx("test_gun_decal")) }) }),
        ("broken_ceramite", new (string, object)[] { ("default", new (string, object)[] { ("play_fx", PlayFx("test_gun_dmg_armour")) }) }),
    }));

    static EffectCatalogue Catalogue() => EffectCatalogue.Build(
        new Dictionary<string, BafDocument> { [OrkWeaponMounts.Shoota] = BafDocument.Read(Gun()) }, null,
        new Dictionary<string, IReadOnlyList<BafDocument>> { ["test_gun_coop"] = [BafDocument.Read(Hits())] }, null, null);

    [Fact]
    public void A_round_plays_the_wargears_muzzle_tracer_and_hit_container()
    {
        var cat = Catalogue();
        var w = cat.Weapon(OrkWeaponMounts.Shoota)!;
        Assert.Equal("test_gun_muzzle", w.Muzzle?.Name);
        Assert.Equal("fx_muzzle", w.MuzzleMarker);
        Assert.Equal("test_gun_tracer", w.Tracer?.Name);
        Assert.True(w.Tracer!.HasTarget);
        Assert.Equal(Container, w.HitMaterial?.Container);   // the ranged hit's container, not the melee bash's
        // A world hit by its surface (the default class), a player hit by the player's surface.
        Assert.Equal(["test_gun_dmg", "test_gun_decal"], cat.Resolve(w.HitMaterial, ShotHit.World, 0).Select(p => p.Name));
        Assert.Equal(["test_gun_dmg_armour"], cat.Resolve(w.HitMaterial, ShotHit.Body, -1, armourUp: false).Select(p => p.Name));
        Assert.Empty(cat.Resolve(w.HitMaterial, ShotHit.None, 0));
    }

    [Fact]
    public void Every_effect_a_gun_can_play_is_compiled()
    {
        Assert.Equal(new[] { "test_gun_decal", "test_gun_dmg", "test_gun_dmg_armour", "test_gun_muzzle", "test_gun_tracer" }, MusouOrkGunEffects.EffectNames(Catalogue()).Order(StringComparer.Ordinal));
        // A catalogue without the gun's wargear gives nothing to compile (the guns are then drawn without effects).
        Assert.Empty(MusouOrkGunEffects.EffectNames(EffectCatalogue.Build(new Dictionary<string, BafDocument>(), null, new Dictionary<string, IReadOnlyList<BafDocument>>(), null, null)));
    }
}
