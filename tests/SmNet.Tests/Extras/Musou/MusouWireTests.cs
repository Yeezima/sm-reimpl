using System.Numerics;
using SmNet.Extras.Musou;
using SmSim;
using SmSim.Extras.Musou;

namespace SmNet.Tests.Extras.Musou;

/// <summary>
/// Added content "Musou" (MU0): the layouts of its three envelope sections (<see cref="MusouWire"/>: tag 3 entities, 10 bytes each; tag 4 events, 16 bytes
/// each; tag 5 the director, 44 bytes): round trips within the quantisation, every full payload fits one envelope section, malformed payloads are refused,
/// and the version is the simulation's.
/// </summary>
public class MusouWireTests
{
    static readonly MusouBox Box = new(new Vector3(-60f, -80f, -5f), new Vector3(60f, 80f, 15f));

    [Fact]
    public void The_tags_and_the_version_are_musous_own()
    {
        Assert.Equal((3, 4, 5), (MusouWire.EntitiesTag, MusouWire.EventsTag, MusouWire.DirectorTag));
        Assert.True(MusouWire.Owns(3) && MusouWire.Owns(4) && MusouWire.Owns(5) && MusouWire.Owns(6));   // MU-D: 6, the drops
        Assert.False(MusouWire.Owns(1) || MusouWire.Owns(2) || MusouWire.Owns(7) || MusouWire.Owns(0));
        Assert.Equal(MusouSim.Version, MusouWire.Version);
        Assert.Equal(119, MusouWire.MaxEntities);
        Assert.Equal(74, MusouWire.MaxEvents);
    }

    [Fact]
    public void Entities_round_trip_within_half_a_step_and_a_full_payload_fits_one_section()
    {
        var rng = new Random(3);
        var types = Enum.GetValues<MusouEnemyType>();
        var states = Enum.GetValues<MusouEnemyState>();
        var list = Enumerable.Range(1, 200).Select(i => new MusouEntity((ushort)i,
            new Vector3(rng.NextSingle() * 120f - 60f, rng.NextSingle() * 160f - 80f, rng.NextSingle() * 20f - 5f),
            rng.NextSingle() * 20f - 10f, types[i % types.Length], states[i % states.Length])).ToList();
        var bytes = MusouWire.WriteEntities(list, Box);
        Assert.Equal(1 + MusouWire.MaxEntities * MusouWire.EntityBytes, bytes.Length);
        Assert.True(bytes.Length <= Protocol.MaxExtrasPayloadBytes);
        var back = MusouWire.ReadEntities(bytes, Box);
        Assert.NotNull(back);
        Assert.Equal(MusouWire.MaxEntities, back.Length);
        var step = MusouWire.PositionStep(Box);
        for (int i = 0; i < back.Length; i++)
        {
            var (a, b) = (list[i], back[i]);
            Assert.Equal((a.Id, a.Type, a.State), (b.Id, b.Type, b.State));
            var d = Vector3.Abs(a.Position - b.Position);
            Assert.True(d.X <= step.X && d.Y <= step.Y && d.Z <= step.Z, $"{a.Position} -> {b.Position}");
            float yaw = MathF.Abs(MathF.IEEERemainder(a.Yaw - b.Yaw, 2f * MathF.PI));
            Assert.True(yaw <= MathF.PI / 256f + 1e-4f, $"yaw {a.Yaw} -> {b.Yaw}");
        }
        // The whole envelope message with this section stays within the 1200-byte budget.
        Assert.True(Protocol.EnvelopeBytes(new ExtrasUpdate(1, [(MusouWire.EntitiesTag, bytes)])) <= Protocol.MaxExtrasMessageBytes);
        Assert.Equal(bytes.Length, Protocol.Write(new ExtrasUpdate(1, [(MusouWire.EntitiesTag, bytes)])).Length - Protocol.ExtrasHeaderBytes - Protocol.ExtrasSectionHeaderBytes);
    }

    [Fact]
    public void Positions_outside_the_box_clamp_to_its_faces()
    {
        var bytes = MusouWire.WriteEntities([new MusouEntity(7, new Vector3(1000f, -1000f, float.NaN), 0f, MusouEnemyType.Nob, MusouEnemyState.Seek)], Box);
        var e = MusouWire.ReadEntities(bytes, Box)![0];
        Assert.Equal(Box.Max.X, e.Position.X, 3);
        Assert.Equal(Box.Min.Y, e.Position.Y, 3);
        Assert.Equal(Box.Min.Z, e.Position.Z, 3);
    }

    [Fact]
    public void Events_round_trip_positions_and_death_pushes()
    {
        MusouEvent[] events =
        [
            new(MusouEventKind.Death, 900, 12, 3, (byte)MusouDeathKind.Melee, Vector3.Normalize(new Vector3(0.3f, -0.8f, 0.2f))),
            new(MusouEventKind.Hit, 901, 13, 4, 2, new Vector3(10.5f, -20.25f, 1.75f)),
            new(MusouEventKind.Strike, 902, 14, 1, 0, new Vector3(-59f, 79f, 0f)),
            new(MusouEventKind.Tracer, uint.MaxValue, 511, 0, 0, new Vector3(0f, 0f, 14f)),
            new(MusouEventKind.Telegraph, 903, 1, 0, (byte)MusouBossAbility.GroundSlam, new Vector3(5f, 5f, 0f)),
        ];
        var bytes = MusouWire.WriteEvents(events, Box);
        Assert.Equal(1 + events.Length * MusouWire.EventBytes, bytes.Length);
        var back = MusouWire.ReadEvents(bytes, Box)!;
        Assert.Equal(events.Length, back.Length);
        var step = MusouWire.PositionStep(Box);
        for (int i = 0; i < events.Length; i++)
        {
            var (a, b) = (events[i], back[i]);
            Assert.Equal((a.Kind, a.Tick, a.Enemy, a.Player, a.Value), (b.Kind, b.Tick, b.Enemy, b.Player, b.Value));
            var tolerance = a.Kind == MusouEventKind.Death ? new Vector3(1f / short.MaxValue) : step;
            var d = Vector3.Abs(a.Vector - b.Vector);
            Assert.True(d.X <= tolerance.X && d.Y <= tolerance.Y && d.Z <= tolerance.Z, $"{a.Kind}: {a.Vector} -> {b.Vector}");
        }
        var many = Enumerable.Range(0, 100).Select(i => events[i % events.Length]).ToList();
        var full = MusouWire.WriteEvents(many, Box);
        Assert.Equal(1 + MusouWire.MaxEvents * MusouWire.EventBytes, full.Length);
        Assert.True(full.Length <= Protocol.MaxExtrasPayloadBytes);
    }

    [Fact]
    public void The_director_round_trips_exactly()
    {
        var d = new MusouDirectorView(3, 5, MusouPhase.Break, 600, 160, 42, 77, 3123.5f, 4000f, 4, Box);
        var bytes = MusouWire.WriteDirector(d);
        Assert.Equal(MusouWire.DirectorBytes, bytes.Length);
        Assert.Equal(d, MusouWire.ReadDirector(bytes));
        // Non-finite numbers are written as 0 (and refused when read).
        var odd = MusouWire.ReadDirector(MusouWire.WriteDirector(d with { BossHealth = float.NaN }));
        Assert.Equal(0f, odd!.Value.BossHealth);
    }

    [Fact]
    public void Malformed_payloads_are_refused()
    {
        var entity = MusouWire.WriteEntities([new MusouEntity(1, Vector3.Zero, 0f, MusouEnemyType.Boy, MusouEnemyState.Seek)], Box);
        Assert.Null(MusouWire.ReadEntities([], Box));
        Assert.Null(MusouWire.ReadEntities(entity[..^1], Box));                              // short
        Assert.Null(MusouWire.ReadEntities([.. entity, 0], Box));                            // long
        var zeroId = (byte[])entity.Clone(); zeroId[1] = 0; zeroId[2] = 0;
        Assert.Null(MusouWire.ReadEntities(zeroId, Box));                                    // id 0 is "none"
        var badState = (byte[])entity.Clone(); badState[10] = 0x0F;
        Assert.Null(MusouWire.ReadEntities(badState, Box));                                  // state 15 unknown
        var badType = (byte[])entity.Clone(); badType[10] = 0xF1;
        Assert.Null(MusouWire.ReadEntities(badType, Box));                                   // type 15 unknown
        Assert.Throws<ArgumentException>(() => MusouWire.WriteEntities([new MusouEntity(1, Vector3.Zero, 0f, (MusouEnemyType)16, MusouEnemyState.Seek)], Box));

        var ev = MusouWire.WriteEvents([new MusouEvent(MusouEventKind.Hit, 1, 1, 1, 1, Vector3.Zero)], Box);
        Assert.Null(MusouWire.ReadEvents(ev[..^1], Box));
        var badKind = (byte[])ev.Clone(); badKind[1] = 0;
        Assert.Null(MusouWire.ReadEvents(badKind, Box));

        var dir = MusouWire.WriteDirector(new MusouDirectorView(1, 5, MusouPhase.Wave, 0, 80, 80, 0, 0f, 0f, 3, Box));
        Assert.Null(MusouWire.ReadDirector(dir[..^1]));
        var badPhase = (byte[])dir.Clone(); badPhase[2] = 99;
        Assert.Null(MusouWire.ReadDirector(badPhase));
        var nan = (byte[])dir.Clone(); BitConverter.TryWriteBytes(nan.AsSpan(20), float.NaN);
        Assert.Null(MusouWire.ReadDirector(nan));

        // MU2: an id twice, an id beyond the pool, an inverted, flat or huge box.
        var twice = MusouWire.WriteEntities([new MusouEntity(4, Vector3.Zero, 0f, MusouEnemyType.Boy, MusouEnemyState.Seek), new MusouEntity(4, Vector3.One, 0f, MusouEnemyType.Boy, MusouEnemyState.Seek)], Box);
        Assert.Null(MusouWire.ReadEntities(twice, Box));
        var beyond = MusouWire.WriteEntities([new MusouEntity(MusouSim.Capacity + 1, Vector3.Zero, 0f, MusouEnemyType.Boy, MusouEnemyState.Seek)], Box);
        Assert.Null(MusouWire.ReadEntities(beyond, Box));
        var d = new MusouDirectorView(1, 5, MusouPhase.Wave, 0, 80, 80, 0, 0f, 0f, 3, Box);
        Assert.Null(MusouWire.ReadDirector(MusouWire.WriteDirector(d with { Box = new MusouBox(Box.Max, Box.Min) })));
        Assert.Null(MusouWire.ReadDirector(MusouWire.WriteDirector(d with { Box = new MusouBox(Vector3.Zero, new Vector3(10f, 10f, 0f)) })));
        Assert.Null(MusouWire.ReadDirector(MusouWire.WriteDirector(d with { Box = new MusouBox(new Vector3(-3e38f), new Vector3(3e38f)) })));
    }

    [Theory]
    [InlineData(0f, 0)]
    [InlineData(-0.001f, 0)]
    [InlineData(MathF.PI, 128)]
    [InlineData(-MathF.PI / 2f, 192)]
    [InlineData(4f * MathF.PI + 0.01f, 0)]
    public void Yaw_quantises_to_a_turn_in_256_steps(float yaw, int expected) => Assert.Equal(expected, MusouWire.QuantiseYaw(yaw));
}
