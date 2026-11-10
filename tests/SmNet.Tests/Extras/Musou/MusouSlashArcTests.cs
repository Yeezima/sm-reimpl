using System.Numerics;
using SmNet.Extras.Musou;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Arsenal;
using SmNet.Tests.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Musou;

/// <summary>
/// Musou Mode's swing arc (owner, 2026-11-10: "a flat curved line that matches the cleave of the weapon swings hitbox"; <see cref="MusouSlashArcRules"/>, <see cref="MusouCleaveShape"/>):
/// the arc's reach, span and count are the shape of the server's own cleave. Each case swings in a real World (the war and the 5-wave run, a plain and a track-to-hit step, no weapon, a data
/// weapon and the power spear) with Boys placed just inside and just outside the shape's reach and edges: the server strikes exactly those inside, so the drawn line cannot drift from the hitbox.
/// </summary>
public class MusouSlashArcTests
{
    const int Human = 1;
    const string Hammer = "sm_mp_melee_2hand_thunder_hammer";
    static readonly MeleeStep Light = new(1f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 1f, 10f, 3.5f);
    static readonly MeleeStep Tracked = Light with { TrackToHit = true };
    static readonly MusouRules Still = MusouRules.Default with { PlayerDamageScale = 0f, Types = [.. MusouRules.Default.Types.Select(t => t with { Speed = 0f })] };

    /// <summary>A Space Marine whose melee is <paramref name="step"/>: with no melee choice (<paramref name="wargear"/> null, the class's own steps) or one combo of that name.</summary>
    static SimConfig Config(MeleeStep step, string? wargear, ContentExtras extras = ContentExtras.Musou)
    {
        var c = new ClassLoadout("sm - tactical", 0, -1, [step])
        {
            PrimaryChoices = [0], EquipmentChoices = [0], Side = Team.SpaceMarines, Role = "tactical",
            MeleeChoices = wargear is null ? [] : [new MeleeCombo(wargear, [step])],
        };
        return MusouSimTests.Base with { Classes = [c, c with { Name = "csm - tactical", Side = Team.Chaos }], Extras = extras, MusouRules = Still, RespawnTime = 60f };
    }

    static void Tick(World w, InputButtons buttons = InputButtons.None) => w.Step(id => new PlayerInput(w.Tick, 0f, 0f, w.Players[id].Yaw, buttons));

    /// <summary>Musou Mode's war on the synthetic arena, the human standing in the open at (0, -60) facing +Y.</summary>
    static World War(SimConfig config)
    {
        var map = TfwTestArena.Build();
        var w = new World(config, map.Collision, map.Spawns) { Rules = new MatchRules { CountdownTime = 0.1f, TimeLimit = 0f, ScoreLimit = int.MaxValue / 2 } };
        Assert.True(w.StartTfw(null, TfwTestArena.Field));
        w.Tfw!.Idle = true;
        w.Spawn(Human, 0);
        Tick(w);
        for (int k = 0; k < 60 && w.State != MatchState.Playing; k++) Tick(w);
        Assert.Equal(MatchState.Playing, w.State);
        w.TfwRespawnAt(Human, new Vector3(0f, -60f, 0f), 0f);
        Tick(w);
        w.Musou!.Frozen = true;
        return w;
    }

    /// <summary>The 5-wave run on flat ground, its director held (no wave comes).</summary>
    static World Skirmish(SimConfig config)
    {
        var w = new World(config) { Rules = MusouSimTests.Endless };
        Assert.True(w.StartMusou(null));
        w.MusouDirector!.Hold = true;
        w.Spawn(Human, w.AssignClass(0));
        Tick(w);
        Assert.Equal(MatchState.Playing, w.State);
        w.Musou!.Frozen = true;
        return w;
    }

    /// <summary>The point <paramref name="metres"/> from <paramref name="feet"/> at <paramref name="degrees"/> off heading <paramref name="yaw"/> (positive to the left; yaw 0 faces +Y, its left is -X).</summary>
    static Vector3 Polar(Vector3 feet, float yaw, float degrees, float metres)
    {
        float a = yaw + degrees * MathF.PI / 180f;
        return feet + new Vector3(-MathF.Sin(a) * metres, MathF.Cos(a) * metres, 0f);
    }

    /// <summary>One swing; the enemies the server's cleave struck (marker 3 hits) and the swinger's state at the first of them.</summary>
    static (HashSet<int> Struck, PlayerState AtHit) Swing(World w)
    {
        var struck = new HashSet<int>();
        PlayerState? atHit = null;
        for (int t = 0; t < 14; t++)
        {
            Tick(w, t < 3 ? InputButtons.Melee : InputButtons.None);
            foreach (var e in w.Musou!.Events)
                if (e.Kind == MusouEventKind.Hit && e.Value == 3) { struck.Add(e.Enemy); atHit ??= w.Players[Human]; }
        }
        Assert.NotNull(atHit);
        return (struck, atHit.Value);
    }

    public static TheoryData<bool, bool, string?> Swings => new()
    {
        { true, false, null }, { true, true, null }, { false, false, null }, { false, true, null },
        { true, true, Hammer }, { false, false, Hammer },
        { true, false, ArsenalRules.PowerSpear }, { true, true, ArsenalRules.PowerSpear }, { false, false, ArsenalRules.PowerSpear }, { false, true, ArsenalRules.PowerSpear },
    };

    /// <summary>The server's cleave strikes the Boys inside the arc's shape and none outside it: 0.6 m either side of its reach, 6 degrees either side of its edges, and straight behind.</summary>
    [Theory]
    [MemberData(nameof(Swings))]
    public void The_servers_cleave_strikes_exactly_inside_the_arcs_shape(bool war, bool tracked, string? wargear)
    {
        var step = tracked ? Tracked : Light;
        var config = Config(step, wargear);
        var shape = MusouCleaveShape.Of(config, step, war, wargear);
        // The numbers the line is drawn with (spec "Melee slash arc: one per swing"): the spear's sweep is its own everywhere; any other weapon the cleave's (the core's 4 m melee range + 0.5,
        // + 1 m on a track-to-hit step; in the war x1.5 x1.3 and 120 degrees each side, in the 5-wave run the core's 60-degree cone x1.5). In the war every reach x0.6 last (owner 2026-11-11).
        if (wargear == ArsenalRules.PowerSpear) { Assert.Equal(war ? 9f : 15f, shape.Reach, 3); Assert.Equal(150f, shape.HalfAngleDegrees); Assert.Equal(12, shape.Most); }
        else
        {
            Assert.Equal((tracked ? 5.5f : 4.5f) * (war ? 1.5f * 1.3f * 0.6f : 1f), shape.Reach, 3);
            Assert.Equal(war ? 120f : 90f, shape.HalfAngleDegrees, 3);
            Assert.Equal(war ? 40 : 8, shape.Most);
        }

        var w = war ? War(config) : Skirmish(config);
        var (feet, yaw) = (w.Players[Human].Position, w.Players[Human].Yaw);
        float edge = shape.HalfAngleDegrees, reach = shape.Reach;
        var placed = new List<(ushort Id, float Degrees, float Metres)>();
        foreach (float degrees in new[] { 0f, edge - 6f, -(edge - 6f), edge + 6f, -(edge + 6f), 180f })
            foreach (float metres in new[] { reach - 0.6f, reach + 0.6f })
                placed.Add((w.Musou!.Spawn(MusouEnemyType.Boy, Polar(feet, yaw, degrees, metres), 0f, 1000f), degrees, metres));
        var (struck, atHit) = Swing(w);

        var forward = new Vector2(-MathF.Sin(atHit.Yaw), MathF.Cos(atHit.Yaw));
        foreach (var (id, degrees, metres) in placed)
        {
            Assert.True(w.Musou!.TryGet(id, out var e));
            var flat = new Vector2(e.Position.X - atHit.Position.X, e.Position.Y - atHit.Position.Y);
            bool inside = flat.Length() <= shape.Reach && Vector2.Dot(Vector2.Normalize(flat), forward) >= shape.MinCos;
            Assert.True(inside == struck.Contains(id), $"{wargear ?? "no weapon"}, {(war ? "war" : "5-wave run")}{(tracked ? ", track-to-hit" : "")}: Boy at {degrees:F0} deg, {metres:F2} m: inside the arc {inside}, struck {struck.Contains(id)}");
        }
        Assert.Equal(3, struck.Count);   // straight ahead and just inside both edges, all within the reach
    }

    public static TheoryData<bool, string?> Counts => new() { { true, null }, { false, null }, { true, ArsenalRules.PowerSpear }, { false, ArsenalRules.PowerSpear } };

    /// <summary>A crowd inside the shape: the server strikes the shape's count of them (40 in the war, 8 in the 5-wave run, 12 for the spear), which the client's predicted pick also takes.</summary>
    [Theory]
    [MemberData(nameof(Counts))]
    public void The_servers_cleave_takes_the_shapes_count(bool war, string? wargear)
    {
        var config = Config(Light, wargear);
        var shape = MusouCleaveShape.Of(config, Light, war, wargear);
        var w = war ? War(config) : Skirmish(config);
        var (feet, yaw) = (w.Players[Human].Position, w.Players[Human].Yaw);
        int crowd = shape.Most + 4;
        for (int k = 0; k < crowd; k++)
        {
            float degrees = (k % 9 - 4) * (shape.HalfAngleDegrees - 10f) / 4f;
            float metres = 1.2f + (shape.Reach - 2f) * k / crowd;
            w.Musou!.Spawn(MusouEnemyType.Boy, Polar(feet, yaw, degrees, metres), 0f, 1000f);
        }
        var (struck, _) = Swing(w);
        Assert.Equal(shape.Most, struck.Count);
    }

    /// <summary>
    /// Only the spear's name sets a shape of its own, and only with Musou's content (SpearRules.Sweep, the one function the server's cleave reads too); the arsenal's other names and the data's
    /// weapons keep the cleave's.
    /// </summary>
    [Fact]
    public void Only_the_spear_sets_its_own_shape_and_only_in_Musou()
    {
        var config = Config(Light, null);
        var plain = MusouCleaveShape.Of(config, Light, war: true);
        Assert.Equal(240f, plain.SpanDegrees, 3);
        foreach (var name in new[] { Hammer, "sm_mp_melee_powersword", ArsenalRules.LightningClaw, ArsenalRules.Autocannon })
            Assert.Equal(plain, MusouCleaveShape.Of(config, Light, war: true, name));
        Assert.Equal(300f, MusouCleaveShape.Of(config, Light, war: true, ArsenalRules.PowerSpear.ToUpperInvariant()).SpanDegrees, 3);   // case ignored, as the server's
        var none = Config(Light, null, ContentExtras.None);
        Assert.Equal(MusouCleaveShape.Of(none, Light, war: true), MusouCleaveShape.Of(none, Light, war: true, ArsenalRules.PowerSpear));
        float reach = 1f, half = 2f;
        int most = 3;
        Assert.False(SpearRules.Sweep(Hammer, ref reach, ref half, ref most));
        Assert.Equal((1f, 2f, 3), (reach, half, most));
        Assert.True(SpearRules.Sweep(ArsenalRules.PowerSpear, ref reach, ref half, ref most));
        Assert.Equal((SpearRules.ReachMetres, SpearRules.HalfAngleDegrees, SpearRules.MostOrks), (reach, half, most));
        Assert.Equal(SpearRules.MinCos, new MusouCleaveShape.Shape(reach, half, most).MinCos);
    }

    /// <summary>The line runs through the enemies struck, within the cleave: never past the reach, never nearer than the floor; unknown, or the test aid's share 1: the hitbox's edge. From one edge of the cone to the other, either way round.</summary>
    [Fact]
    public void The_arcs_radius_and_angles_follow_the_shape()
    {
        var config = Config(Light, null);
        foreach (var shape in new[] { MusouCleaveShape.Of(config, Light, war: true), MusouCleaveShape.Of(config, Tracked, war: false), MusouCleaveShape.Of(config, Light, war: true, ArsenalRules.PowerSpear) })
        {
            float reach = shape.Reach;
            Assert.Equal(reach, MusouSlashArcRules.Radius(shape, float.NaN), 4);
            Assert.Equal(reach, MusouSlashArcRules.Radius(shape, 3f, share: 1f), 4);
            Assert.Equal(3f, MusouSlashArcRules.Radius(shape, 3f), 4);
            Assert.Equal(MusouSlashArcRules.RadiusMin, MusouSlashArcRules.Radius(shape, 0.4f), 4);
            Assert.Equal(reach, MusouSlashArcRules.Radius(shape, reach + 5f), 4);
            float edge = shape.HalfAngleDegrees * MathF.PI / 180f;
            Assert.Equal(-edge, MusouSlashArcRules.AngleAt(0f, shape, 1), 4);
            Assert.Equal(edge, MusouSlashArcRules.AngleAt(1f, shape, 1), 4);
            Assert.Equal(edge, MusouSlashArcRules.AngleAt(0f, shape, -1), 4);
            Assert.Equal(0f, MusouSlashArcRules.AngleAt(0.5f, shape, -1), 4);
            Assert.InRange(MusouSlashArcRules.Segments(shape), MusouSlashArcRules.SegmentsMin, MusouSlashArcRules.SegmentsMax);
        }
        Assert.Equal(8f, MusouSlashArcRules.Radius(MusouCleaveShape.Of(config, Light, war: true, ArsenalRules.PowerSpear), 8f), 4);   // the spear's line reaches its 9 m (15 x0.6), not the war's 5.3
        Assert.True(MusouSlashArcRules.Segments(MusouCleaveShape.Of(config, Light, war: true)) >= 40);   // 240 degrees: smooth
        Assert.Equal(60, MusouSlashArcRules.Segments(MusouCleaveShape.Of(config, Light, war: true, ArsenalRules.PowerSpear)));   // 300 degrees at 5 a segment
    }

    [Fact]
    public void The_line_sweeps_holds_and_fades_without_a_pop()
    {
        Assert.Equal(0f, MusouSlashArcRules.Fade(0f), 4);
        Assert.Equal(0f, MusouSlashArcRules.Head(0f), 4);
        Assert.Equal(1f, MusouSlashArcRules.Fade(MusouSlashArcRules.SweepSeconds), 4);
        Assert.True(MusouSlashArcRules.Head(MusouSlashArcRules.SweepSeconds) > 1f);   // the whole line is lit once swept
        Assert.Equal(1f, MusouSlashArcRules.Fade(MusouSlashArcRules.SweepSeconds + MusouSlashArcRules.HoldSeconds), 4);
        Assert.Equal(0f, MusouSlashArcRules.Fade(MusouSlashArcRules.Lifetime), 4);
        float last = 1f;
        for (float age = MusouSlashArcRules.SweepSeconds + MusouSlashArcRules.HoldSeconds; age <= MusouSlashArcRules.Lifetime; age += 0.005f)
        {
            float f = MusouSlashArcRules.Fade(age);
            Assert.True(f <= last + 1e-5f && last - f < 0.06f, $"fade at {age:F3} s");
            last = f;
        }
        Assert.True(MusouSlashArcRules.Lifetime < 0.5f);   // a fast fade, not the strokes' 1.3 s
    }

    [Fact]
    public void Heavy_strikes_are_thicker_and_the_light_chain_alternates_its_direction()
    {
        Assert.True(MusouSlashArcRules.Width(heavy: true, 1) > MusouSlashArcRules.Width(heavy: false, 4));
        Assert.True(MusouSlashArcRules.Width(false, 3) > MusouSlashArcRules.Width(false, 1));
        Assert.Equal(MusouSlashArcRules.Width(false, 1 + MusouSlashArcRules.StepGrowthMost), MusouSlashArcRules.Width(false, 9), 5);   // capped
        Assert.Equal(1, MusouSlashArcRules.Direction(false, 1));
        Assert.Equal(-1, MusouSlashArcRules.Direction(false, 2));
        Assert.Equal(1, MusouSlashArcRules.Direction(false, 3));
        Assert.Equal(1, MusouSlashArcRules.Direction(true, 2));
        Assert.InRange(MusouSlashArcRules.CompatPullMetres, 0.5f, MusouSlashArcRules.RadiusMin - 0.3f);   // past an Ork's body, short of the own model in front of the line
    }
}
