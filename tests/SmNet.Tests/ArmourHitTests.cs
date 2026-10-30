using System.Numerics;
using SmServer;
using SmSim;

namespace SmNet.Tests;

/// <summary>
/// Hit feedback (protocol 22, owner's request: armour hits and flesh hits look different): every player hit says whether the
/// victim's armour shield was up before it landed, in the simulation's shot and hit events, in other clients' shot records (bit 7 of
/// the record's third byte) and in the shooter's hit block (<see cref="HitFlags.Armoured"/>), without growing the snapshot.
/// Synthetic data only.
/// </summary>
public class ArmourHitTests
{
    static readonly WeaponSpec Rifle = new("test_rifle", WeaponSlot.Ranged, TriggerKind.SemiAuto, 20f, 0.1f, 30, 90, 1f, 250f, 250f, 250f, 1f, 1f, 1f, 1f, 1f);
    /// <summary>A 30-point shield taking a rifle hit at full value: hits 1 and 2 strike armour (the second breaks it), hit 3 flesh.</summary>
    static readonly SimConfig Config = new()
    {
        Weapons = [Rifle], Classes = [new ClassLoadout("test", 0, -1, SimConfig.PlaceholderMeleeSteps) { PrimaryChoices = [0] }],
        HealthMax = 100f, ShieldMax = 30f, HealthRegenDelay = 1e6f, ShieldRegenDelay = 1e6f,
    };

    static PlayerState Player(int id, Vector3 at) => new(id, at, Vector3.Zero, 0f, true) { Alive = true };

    [Fact]
    public void Shots_and_hits_say_whether_the_victims_shield_was_up_before_the_hit()
    {
        var w = new World(Config, null, [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(0, 6, 0), MathF.PI)]);
        w.Spawn(1);
        w.Spawn(2);
        var seen = new List<(bool Shot, bool Hit, float Shield)>();
        for (int t = 0; t < 60 && seen.Count < 3; t++)
        {
            bool fire = t % 10 == 0;   // presses 0.33 s apart (semi-auto)
            w.Step(id => new PlayerInput(0, 0f, 0f, w.Players[id].Yaw, id == 2 && fire ? InputButtons.Fire : InputButtons.None));
            if (w.LastShots.Count == 0) continue;
            var shot = Assert.Single(w.LastShots);
            Assert.Equal((2, 1, ShotHit.Body), (shot.Shooter, shot.Target, shot.Hit));
            var hit = Assert.Single(w.LastHits);
            seen.Add((shot.Armoured, hit.Armoured, w.Players[1].Shield));
        }
        // 30 → 10 (armour), 10 → 0 and into health (armour: the hit that breaks it), then flesh.
        Assert.Equal(new[] { (true, true, 10f), (true, true, 0f), (false, false, 0f) }, seen);
        // The server's records for another client carry it; the shooter's hit block too.
        var records = GameServer.ShotsFor([new ShotEvent(5, 2, 0, 0, 0, Vector3.Zero, Vector3.One, ShotHit.Head, -1, 1, Armoured: true)], 3, Vector3.Zero, 5);
        Assert.True(Assert.Single(records).Armoured);
    }

    [Fact]
    public void Melee_hits_and_misses_follow_the_same_rule()
    {
        var w = new World(Config with { ShieldMax = 0f }, null, [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(0, 1.5f, 0), MathF.PI)]);
        w.Spawn(1);
        w.Spawn(2);
        bool? armoured = null;
        for (int t = 0; t < 60 && armoured is null; t++)
        {
            w.Step(id => new PlayerInput(0, 0f, 0f, w.Players[id].Yaw, id == 2 && t == 0 ? InputButtons.Melee : InputButtons.None));
            if (w.LastHits.Count > 0) armoured = w.LastHits[0].Armoured;
        }
        Assert.False(armoured);   // no shield at all: flesh
        // A shot that hits nothing or the level never strikes armour.
        Assert.False(new ShotEvent(1, 1, 0, 0, 0).Armoured);
    }

    [Fact]
    public void The_armour_bit_round_trips_in_shot_records_and_the_hit_block_without_growing_the_snapshot()
    {
        Assert.Equal((9, 12, 1194), (Protocol.ShotBytes, Protocol.HitBlockBytes, Protocol.MaxSnapshotBytes));   // spare bits only
        PlayerState[] players = [Player(3, new Vector3(10, 20, 1)), Player(4, Vector3.Zero)];
        ShotView[] shots =
        [
            new(3, 0, 0, ShotHit.Body, -1, new Vector3(12, 20, 1), 0, Armoured: true),
            new(3, 0, 0, ShotHit.Head, -1, new Vector3(12, 21, 2), 1, Armoured: true),
            new(4, 1, 2, ShotHit.Body, -1, new Vector3(1, 2, 1), 0, Armoured: false),
            new(4, 1, 0, ShotHit.World, 4, new Vector3(5, 5, 5), 0, Armoured: true),   // not a player hit: dropped
            new(4, 1, 0, ShotHit.None, -1, new Vector3(9, 9, 9), 0, Armoured: true),
        ];
        var hits = new HitView(3, 200, 50, 1, 4, HitFlags.Head | HitFlags.Armoured, 0.5f, 0f);
        var bytes = Protocol.Write(new Snapshot(5, 4, 0, players, Shots: shots, Hits: hits));
        var plain = Protocol.Write(new Snapshot(5, 4, 0, players, Shots: [.. shots.Select(s => s with { Armoured = false })], Hits: hits with { LastFlags = HitFlags.Head }));
        Assert.Equal(plain.Length, bytes.Length);
        var back = Assert.IsType<Snapshot>(Protocol.Read(bytes));
        Assert.Equal(new[] { true, true, false, false, false }, back.Shots!.Select(s => s.Armoured));
        Assert.Equal(shots.Select(s => Protocol.Quantize(s, players.First(p => p.PlayerId == s.Shooter).Position)), back.Shots!);
        Assert.Equal(HitFlags.Head | HitFlags.Armoured, back.Hits!.Value.LastFlags);
        Assert.Equal(Protocol.Quantize(hits), back.Hits.Value);
        // Refused: the bit on a world hit; unknown hit flags (32 and up).
        int record = bytes.Length - 5 * Protocol.ShotBytes;
        static byte[] Patch(byte[] b, int at, int value) { var copy = (byte[])b.Clone(); copy[at] = (byte)value; return copy; }
        Assert.Equal(128 | (15 << 3), bytes[record + 2]);                                   // body hit: stage 0, unknown surface, armour
        Assert.Null(Protocol.Read(Patch(bytes, record + 3 * Protocol.ShotBytes + 2, bytes[record + 3 * Protocol.ShotBytes + 2] | 128)));   // world hit
        Assert.Null(Protocol.Read(Patch(bytes, record + 4 * Protocol.ShotBytes + 2, bytes[record + 4 * Protocol.ShotBytes + 2] | 128)));   // no hit
        int flags = bytes.Length - 5 * Protocol.ShotBytes - 1 - 3;                          // the hit block's flag byte
        Assert.Equal((int)(HitFlags.Head | HitFlags.Armoured), bytes[flags]);
        Assert.Null(Protocol.Read(Patch(bytes, flags, 32)));
        // The fullest snapshot with every guaranteed shot record armoured is still the worst case.
        var many = Enumerable.Range(1, Protocol.MaxPlayers).Select(i => Player(i, new Vector3(i, -i, 2f)) with { Class = 63 }).ToArray();
        var full = Protocol.Write(new Snapshot(1, 1, 1, many, Self: 7,
            Projectiles: [.. Enumerable.Range(0, Protocol.MaxSnapshotProjectiles).Select(k => new ProjectileView(60000 + k, 254, 255, new Vector3(k), ProjectileFlags.Detonated))],
            Match: new MatchView(MatchState.Playing, Team.None, 1, 2, 3),
            Kills: [.. Enumerable.Range(0, Protocol.MaxSnapshotKills).Select(k => new KillView(k, 1, 2, KillCause.Weapon, 0, Vector3.UnitX))],
            Points: [.. Enumerable.Range(0, Protocol.MaxSnapshotPoints).Select(_ => new ControlPointView(Team.Chaos, Team.Chaos, 1f, true))],
            Hits: hits, Shots: [.. ShotTests.ManyShots(many, 40).Select(s => s with { Hit = ShotHit.Body, Surface = -1, Armoured = true })]));
        Assert.True(full.Length <= Protocol.SnapshotBudget, $"{full.Length}");
        Assert.Equal(Protocol.MinSnapshotShots, Assert.IsType<Snapshot>(Protocol.Read(full)).Shots!.Count(s => s.Armoured));
    }
}
