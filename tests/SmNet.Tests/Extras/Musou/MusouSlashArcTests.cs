using System.Numerics;
using SmNet.Extras.Musou;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;
using SmNet.Tests.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Musou;

/// <summary>
/// Musou Mode's swing arc (owner, 2026-11-10: "a flat curved line that matches the cleave of the weapon swings hitbox"; <see cref="MusouSlashArcRules"/>, <see cref="MusouCleaveShape"/>):
/// the arc's reach and span are the cleave's own for every melee weapon, and the server's cleave strikes exactly the enemies inside that shape, so the drawn line cannot drift from the hitbox.
/// </summary>
public class MusouSlashArcTests
{
    static readonly MeleeStep Light = new(1f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 1f, 10f, 3.5f);
    static readonly MeleeStep Tracked = Light with { TrackToHit = true };
    static readonly MusouRules Still = MusouRules.Default with { PlayerDamageScale = 0f, Types = [.. MusouRules.Default.Types.Select(t => t with { Speed = 0f })] };

    static SimConfig Config()
    {
        var c = new ClassLoadout("sm - tactical", 0, -1, [Light]) { PrimaryChoices = [0], EquipmentChoices = [0], Side = Team.SpaceMarines, Role = "tactical" };
        return MusouSimTests.Base with { Classes = [c, c with { Name = "csm - tactical", Side = Team.Chaos }], Extras = ContentExtras.Musou, MusouRules = Still, RespawnTime = 60f };
    }

    /// <summary>The server's own cleave volume (<c>World.MusouCleave</c>), written out: the shape every weapon's arc must equal.</summary>
    static (float Reach, float HalfDegrees) ServerVolume(SimConfig config, MeleeStep step, bool war)
    {
        var rules = (config.MusouRules ?? MusouRules.Default).Combat;
        float range = rules.CleaveRange(config.MeleeRange, step.TrackToHit, war);
        float half = MathF.Min(180f, config.MeleeConeDegrees * rules.CleaveConeScale);
        if (war) { range *= TfwCombatRules.Default.CleaveReachScale; half = TfwCombatRules.Default.CleaveHalfAngleDegrees; }
        return (range, half);
    }

    [Theory]
    [InlineData("sm_mp_melee_chainsword")]
    [InlineData("sm_mp_melee_chainsword_gold")]
    [InlineData("csm_mp_melee_chain_axe")]
    [InlineData("sm_mp_melee_combat_knife")]
    [InlineData("sm_mp_melee_powersword")]
    [InlineData("sm_mp_melee_power_axe")]
    [InlineData("sm_mp_melee_2hand_thunder_hammer")]
    [InlineData("csm_mp_melee_2hand_power_mace")]
    [InlineData(null)]
    public void The_arcs_reach_and_span_are_the_cleaves_for_every_weapon(string? wargear)
    {
        var config = Config();
        foreach (bool war in new[] { true, false })
            foreach (var step in new[] { Light, Tracked })
            {
                var (reach, half) = ServerVolume(config, step, war);
                var shape = MusouCleaveShape.Of(config, step, war, wargear);
                Assert.Equal(reach, shape.Reach, 4);
                Assert.Equal(half, shape.HalfAngleDegrees, 4);
                Assert.Equal(2f * half, shape.SpanDegrees, 4);
                // Through the enemies struck, within the cleave: never past the reach, never nearer than the floor; unknown, or the test aid's share 1: the hitbox's edge.
                Assert.Equal(reach, MusouSlashArcRules.Radius(shape, float.NaN), 4);
                Assert.Equal(reach, MusouSlashArcRules.Radius(shape, 3f, share: 1f), 4);
                Assert.Equal(3f, MusouSlashArcRules.Radius(shape, 3f), 4);
                Assert.Equal(MusouSlashArcRules.RadiusMin, MusouSlashArcRules.Radius(shape, 0.4f), 4);
                Assert.Equal(reach, MusouSlashArcRules.Radius(shape, reach + 5f), 4);
                // From one edge of the cone to the other, either way round.
                float edge = half * MathF.PI / 180f;
                Assert.Equal(-edge, MusouSlashArcRules.AngleAt(0f, shape, 1), 4);
                Assert.Equal(edge, MusouSlashArcRules.AngleAt(1f, shape, 1), 4);
                Assert.Equal(edge, MusouSlashArcRules.AngleAt(0f, shape, -1), 4);
                Assert.Equal(0f, MusouSlashArcRules.AngleAt(0.5f, shape, -1), 4);
            }
        // The war: 240 degrees wide (120 to each side) at 1.3 times the cleave's reach.
        var warShape = MusouCleaveShape.Of(config, Light, war: true, wargear);
        Assert.Equal(240f, warShape.SpanDegrees, 3);
        Assert.Equal((config.MeleeRange + 0.5f) * 1.5f * 1.3f, warShape.Reach, 3);
    }

    /// <summary>The server's cleave strikes the enemies inside the arc's shape and none outside it (just inside and just outside its reach and its edges).</summary>
    [Fact]
    public void The_server_cleave_strikes_inside_the_arcs_shape_and_nothing_outside()
    {
        var config = Config();
        var map = TfwTestArena.Build();
        var w = new World(config, map.Collision, map.Spawns) { Rules = new MatchRules { CountdownTime = 0.1f, TimeLimit = 0f, ScoreLimit = int.MaxValue / 2 } };
        Assert.True(w.StartTfw(null, TfwTestArena.Field));
        w.Tfw!.Idle = true;
        const int human = 1;
        w.Spawn(human, 0);
        void Tick(InputButtons buttons = InputButtons.None) => w.Step(id => new PlayerInput(w.Tick, 0f, 0f, w.Players[id].Yaw, buttons));
        Tick();
        for (int k = 0; k < 60 && w.State != MatchState.Playing; k++) Tick();
        Assert.Equal(MatchState.Playing, w.State);
        w.TfwRespawnAt(human, new Vector3(0f, -60f, 0f), 0f);
        Tick();
        w.Musou!.Frozen = true;

        var shape = MusouCleaveShape.Of(config, Light, war: true);
        var me = w.Players[human].Position;   // yaw 0: forward is +Y, positive angles to the left (-X)
        Vector3 Polar(float degrees, float metres) => me + new Vector3(-MathF.Sin(degrees * MathF.PI / 180f) * metres, MathF.Cos(degrees * MathF.PI / 180f) * metres, 0f);
        float edge = shape.HalfAngleDegrees, reach = shape.Reach;
        var placed = new List<(ushort Id, float Degrees, float Metres)>();
        foreach (float degrees in new[] { 0f, edge - 6f, -(edge - 6f), edge + 6f, -(edge + 6f), 180f })
            foreach (float metres in new[] { reach - 0.6f, reach + 0.6f })
                placed.Add((w.Musou.Spawn(MusouEnemyType.Boy, Polar(degrees, metres), 0f, 1000f), degrees, metres));

        var struck = new HashSet<int>();
        PlayerState? atHit = null;
        for (int t = 0; t < 12; t++)
        {
            Tick(t < 3 ? InputButtons.Melee : InputButtons.None);
            foreach (var e in w.Musou.Events)
                if (e.Kind == MusouEventKind.Hit && e.Value == 3) { struck.Add(e.Enemy); atHit ??= w.Players[human]; }
        }
        Assert.NotNull(atHit);
        var feet = atHit.Value.Position;
        var forward = new Vector2(-MathF.Sin(atHit.Value.Yaw), MathF.Cos(atHit.Value.Yaw));
        foreach (var (id, degrees, metres) in placed)
        {
            Assert.True(w.Musou.TryGet(id, out var e));
            var flat = new Vector2(e.Position.X - feet.X, e.Position.Y - feet.Y);
            bool inside = flat.Length() <= shape.Reach && Vector2.Dot(Vector2.Normalize(flat), forward) >= shape.MinCos;
            Assert.True(inside == struck.Contains(id), $"Boy at {degrees:F0} deg, {metres:F2} m: inside the arc {inside}, struck {struck.Contains(id)}");
        }
        Assert.Equal(3, struck.Count);   // straight ahead and just inside both edges, all within the reach
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
        var war = MusouCleaveShape.Of(Config(), Light, war: true);
        Assert.InRange(MusouSlashArcRules.Segments(war), MusouSlashArcRules.SegmentsMin, MusouSlashArcRules.SegmentsMax);
        Assert.True(MusouSlashArcRules.Segments(war) >= 40);   // 240 degrees: smooth
    }
}
