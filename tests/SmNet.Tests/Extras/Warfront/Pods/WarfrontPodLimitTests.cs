using System.Numerics;
using SmNet.Tests.Extras.Musou.Campaign;
using SmNet.Tests.Extras.Warfront.Wiring;
using SmServer;
using SmSim;
using SmSim.Extras.Musou.Campaign;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Warfront.Pods;

/// <summary>
/// Phase B T2b (owner 2026-11-24: "Drop pod marines don't count towards the limit"; protocol 94): Warfront's drop pod Marines take their own 20 ids, so a pod lands its five at 1, 2
/// and 4 humans with full squads; the snapshot carries up to 32 records (the AI Marines as lean records), its shot index has five bits, and the writer keeps every snapshot and scoreboard within the
/// 1200-byte budget. Musou Mode's pods keep the puppet ids.
/// </summary>
public class WarfrontPodLimitTests(ITestOutputHelper output)
{
    static readonly Vector3 Away = new(0f, -100f, 0f);

    static WarfrontSquadWiringTests.Rig Squads(int humans)
    {
        var r = WarfrontSquadWiringTests.Rig.Make(humans: humans, marines: 2);   // full squads: two retainers each (4 humans: 4 + 8 = 12 World players)
        r.W.Tfw!.Fire.NoSchedule = true;
        r.W.TfwGuards?.Clear();
        return r;
    }

    static IEnumerable<Marine> PodMarines(World w) => w.Tfw!.Marines.Where(m => m.Role == MarineRole.Pod);

    /// <summary>Human <paramref name="caller"/>, away from the point, calls a pod there; the pod falls and its Marines step out.</summary>
    static void CallAndLand(WarfrontSquadWiringTests.Rig r, IEnumerable<int> callers)
    {
        var w = r.W;
        int k = 0;
        foreach (int caller in callers)
        {
            w.TfwRespawnAt(caller, Away + new Vector3(k * 3f, 0f, 0f), 0f);
            Assert.True(w.TfwDropPod(caller, new Vector3(60f - k * 25f, -60f + k * 10f, 0f)), $"human {caller}'s pod is accepted");
            k++;
        }
        int ticks = (int)MathF.Ceiling((TfwDropPodRules.LandSeconds + TfwDropPodRules.DoorSeconds + TfwDropPodRules.StepOutSeconds) * w.Config.TickRate) + 2;
        r.Stand(ticks);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void A_pod_lands_all_five_Marines_at_any_number_of_humans_with_full_squads(int humans)
    {
        var r = Squads(humans);
        int before = r.W.Players.Count;
        Assert.Equal(humans * 3, before);   // the humans and two retainers each
        CallAndLand(r, [1]);
        var marines = PodMarines(r.W).ToList();
        Assert.Equal(TfwDropPodRules.Marines, marines.Count);
        Assert.All(marines, m => Assert.True(TfwContracts.IsPodId(m.Id) && TfwContracts.IsPuppet(m.Id), $"{m.Id} is a pod id and an AI Marine's"));
        Assert.All(marines, m => Assert.Equal(Team.SpaceMarines, r.W.Players[m.Id].Team));
        Assert.Equal(before + TfwDropPodRules.Marines, r.W.Players.Count);
        Assert.Equal(humans * 2, r.W.Tfw!.Marines.Count(m => m.Role == MarineRole.Squad && !TfwContracts.IsPodId(m.Id)));   // the retainers keep the puppet ids
    }

    [Fact]
    public void Four_humans_with_full_squads_land_four_pods_at_once_and_fill_the_32_records()
    {
        var r = Squads(4);
        CallAndLand(r, [1, 2, 3, 4]);
        Assert.Equal(4 * TfwDropPodRules.Marines, PodMarines(r.W).Count());
        Assert.Equal(Protocol.MaxPlayers, r.W.Players.Count);   // 4 + 8 + 20
        Assert.Equal(TfwContracts.PodIds, r.W.Players.Keys.Count(TfwContracts.IsPodId));
        Assert.Equal(8, r.W.Players.Keys.Count(id => id is >= TfwContracts.PuppetIdFirst and <= TfwContracts.PuppetIdLast));   // the retainers' ids untouched
    }

    [Fact]
    public void Musou_Modes_pods_keep_the_puppet_ids()
    {
        var w = TfwRig.War();
        TfwRig.Deploy(w);
        w.Tfw!.Fire.NoSchedule = true;
        w.Musou!.Clear();
        w.TfwGuards!.Clear();
        w.TfwDismissOtherMarinesForTests();
        w.TfwRespawnAt(TfwRig.Human, Away, 0f);
        Assert.Null(w.Tfw.Warfront);
        Assert.True(w.TfwDropPod(TfwRig.Human, new Vector3(60f, -60f, 0f)));
        TfwRig.Steps(w, (int)MathF.Ceiling((TfwDropPodRules.LandSeconds + TfwDropPodRules.DoorSeconds + TfwDropPodRules.StepOutSeconds) * w.Config.TickRate) + 2);
        var marines = PodMarines(w).ToList();
        Assert.Equal(TfwDropPodRules.Marines, marines.Count);
        Assert.All(marines, m => Assert.True(m.Id is >= TfwContracts.PuppetIdFirst and <= TfwContracts.PuppetIdLast, $"{m.Id}: a puppet id"));
    }

    [Fact]
    public void The_pod_range_is_its_own_reserved_for_joiners_and_apart_from_the_puppet_ids()
    {
        Assert.Equal((65490, 65509, 20), (TfwContracts.PodIdFirst, TfwContracts.PodIdLast, TfwContracts.PodIds));
        Assert.Equal((TfwContracts.PodIdFirst, TfwContracts.PodIdLast), (GameServer.ReservedPodIdFirst, GameServer.ReservedPodIdLast));
        Assert.True(TfwContracts.PodIdLast < TfwContracts.PuppetIdFirst - 1);   // PuppetIdFirst - 1 is no AI Marine's
        Assert.False(TfwContracts.IsPuppet(TfwContracts.PodIdFirst - 1));
        Assert.False(TfwContracts.IsPuppet(TfwContracts.PodIdLast + 1));
        Assert.True(TfwContracts.PointTargetId(0) > ushort.MaxValue - 10 && TfwContracts.PointTargetId(0) > TfwContracts.PuppetIdLast);
        Assert.Equal(Protocol.MaxPlayers, 4 + 8 + TfwContracts.PodIds);   // four humans, two retainers each and four pods fill the snapshot's records exactly
        Assert.Equal(16, Protocol.MaxMatchPlayers);   // a match still admits 16 (Classic 8v8)
    }

    // ---------------------------------------------------------------- the wire

    static PlayerState Body(int id, Vector3 at, int playerClass = 3) => new(id, at, new Vector3(1.25f, -2.5f, 0.75f), 1.0f, true)
    {
        Alive = true, Class = playerClass, Team = Team.SpaceMarines, Slot = WeaponSlot.Ranged, Primary = 2, MeleeChoice = 1, Pitch = -0.2f, Aiming = true,
        EvadeTicks = 3, EvadeDirection = EvadeDirection.Left, DamageCount = 2, DamageShield = true,
    };

    /// <summary>Four humans (1..4; 1 receives), eight retainers and the pair (puppet ids) and <paramref name="pods"/> pods (pod ids), the AI Marines lean, as the Warfront server orders them.</summary>
    static (PlayerState[] Players, int Lean) Crowd(int pods, bool pair = true)
    {
        var all = new List<PlayerState>();
        for (int id = 1; id <= 4; id++) all.Add(Body(id, new Vector3(id, 0f, 0f)));
        for (int k = 0; k < 8 + (pair ? 2 : 0); k++) all.Add(Body(TfwContracts.PuppetIdFirst + k, new Vector3(k, 5f, 0f)));
        for (int k = 0; k < pods * 5; k++) all.Add(Body(TfwContracts.PodIdFirst + k, new Vector3(100f * (k / 5 + 1), 0f, 0f)));
        var ordered = GameServer.WarfrontSnapshotOrder([.. all], 1, Vector3.Zero, id => new Vector3(100f * ((id - TfwContracts.PodIdFirst) / 5 + 1), 0f, 0f), out int lean);
        return (ordered, lean);
    }

    static Snapshot Full(PlayerState[] players, int lean, bool points = false, int projectiles = Protocol.MaxSnapshotProjectiles, int kills = Protocol.MaxSnapshotKills) =>
        new(1, 1, 1, players, Self: 1, Lean: lean,
            Projectiles: [.. Enumerable.Range(0, projectiles).Select(k => new ProjectileView(60000 + k, 254, 255, new Vector3(k), ProjectileFlags.Detonated))],
            Match: new MatchView(MatchState.Playing, Team.None, 1, 2, 3),
            Kills: [.. Enumerable.Range(0, kills).Select(k => new KillView(k, 1, 2, KillCause.Weapon, 0, Vector3.UnitX))],
            Points: points ? [.. Enumerable.Repeat(new ControlPointView(Team.Chaos, Team.Chaos, 1f, true), Protocol.MaxSnapshotPoints)] : [],
            Hits: new HitView(1, 2, 3, 4, 5, HitFlags.All, 1f, 1f), Shots: ShotTests.ManyShots(players, Protocol.MaxSnapshotShots));

    [Fact]
    public void The_snapshot_budget_at_the_worst_cases_is_measured_and_held()
    {
        // The measure (every other block full: 12 projectiles, 4 kills, the 4 guaranteed shots; Warfront has no control points).
        int slimOnly22 = Protocol.SnapshotBytesOf(12, 4, 0, 4, slimOthers: 21, full: true, lean: 0);
        int slimOnly24 = Protocol.SnapshotBytesOf(12, 4, 0, 4, slimOthers: 23, full: true, lean: 0);
        int lean22 = Protocol.SnapshotBytesOf(12, 4, 0, 4, slimOthers: 3, full: true, lean: 18);
        int lean24 = Protocol.SnapshotBytesOf(12, 4, 0, 4, slimOthers: 3, full: true, lean: 20);
        int lean32 = Protocol.SnapshotBytesOf(12, 4, 0, 4, slimOthers: 3, full: true, lean: 28);
        int lean32Quiet = Protocol.SnapshotBytesOf(0, 0, 0, 4, slimOthers: 3, full: true, lean: 28);
        output.WriteLine($"slim {Protocol.SlimRecordBytes}, full {Protocol.FullRecordBytes}, lean {Protocol.LeanRecordBytes} bytes; budget {Protocol.SnapshotBudget}");
        output.WriteLine($"4 humans + 8 retainers + 2 pods (22): all slim {slimOnly22}, AI lean {lean22}; + the pair (24): all slim {slimOnly24}, AI lean {lean24}; 4 pods (32): {lean32} full blocks, {lean32Quiet} quiet");
        Assert.Equal((48, 180, 33), (Protocol.SlimRecordBytes, Protocol.FullRecordBytes, Protocol.LeanRecordBytes));
        Assert.True(slimOnly22 > Protocol.SnapshotBudget);   // why the lean records exist
        Assert.True(lean22 <= Protocol.SnapshotBudget, $"4 humans, full squads and two pods: {lean22} bytes, every record in");

        // The real writer at the real worst case of two pods at four humans keeps every record.
        var (players, lean) = Crowd(pods: 2, pair: false);
        var bytes = Protocol.Write(Full(players, lean));
        output.WriteLine($"written, 22 players: {bytes.Length} bytes");
        Assert.Equal(lean22, bytes.Length);
        var back = Assert.IsType<Snapshot>(Protocol.Read(bytes));
        Assert.Equal(22, back.Players.Length);
        Assert.Equal(18, back.Lean);

        // Past the budget the writer leaves out the farthest pod's Marines, never more than the budget, never the recipient.
        foreach (var (pods, pair, pointsOn) in new[] { (2, true, false), (2, true, true), (4, false, false), (4, true, true), (0, true, true) })
        {
            var (crowd, leanCount) = Crowd(pods, pair);
            var written = Protocol.Write(Full(crowd, leanCount, pointsOn));
            var read = Assert.IsType<Snapshot>(Protocol.Read(written));
            output.WriteLine($"{pods} pods, pair {pair}, points {pointsOn}: {crowd.Length} players, {read.Players.Length} written, {written.Length} bytes");
            Assert.True(written.Length <= Protocol.SnapshotBudget && written.Length <= UdpServerTransport.MaxPacket, $"{written.Length} bytes");
            Assert.Equal(1, read.Self);
            Assert.Equal(crowd.Take(read.Players.Length).Select(p => p.PlayerId), read.Players.Select(p => p.PlayerId));   // a prefix: the last ones out
            Assert.True(read.Players.Length == crowd.Length || written.Length - (read.Shots!.Length - Protocol.MinSnapshotShots) * Protocol.ShotBytes + Protocol.LeanRecordBytes > Protocol.SnapshotBudget, "no room left unused");
            Assert.True(read.Shots!.Length >= Protocol.MinSnapshotShots);
        }
    }

    [Fact]
    public void Lean_records_and_ids_round_trip_and_read_as_the_slim_record()
    {
        var (players, lean) = Crowd(pods: 2);
        Assert.Equal(20, lean);
        var bytes = Protocol.Write(new Snapshot(7, 6, 0, players, Self: 1, Lean: lean));
        Assert.Equal(Protocol.LeanBlockFlag | 4, bytes[13 + 1]);   // the count byte after the header and the (empty) projectile block: 4 slim, a lean block
        var back = Assert.IsType<Snapshot>(Protocol.Read(bytes));
        Assert.Equal(players.Select(p => p.PlayerId), back.Players.Select(p => p.PlayerId));
        Assert.Contains(back.Players, p => p.PlayerId == TfwContracts.PodIdLast - 10);
        for (int k = 4; k < players.Length; k++)
        {
            var slim = Protocol.Slim(players[k]);
            var got = back.Players[k];
            Assert.True(Vector3.Distance(slim.Position, got.Position) <= 1f / Protocol.PositionScale, $"{got.PlayerId} position");
            Assert.True(Vector3.Distance(slim.Velocity, got.Velocity) <= 1f / Protocol.LeanVelocityScale, $"{got.PlayerId} velocity");
            Assert.Equal(slim.Yaw, got.Yaw, 3);
            Assert.Equal(slim.Pitch, got.Pitch, 3);
            Assert.Equal(slim with { Position = got.Position, Velocity = got.Velocity, Yaw = got.Yaw, Pitch = got.Pitch }, got);   // every other field as the slim record
        }
        for (int k = 0; k < bytes.Length; k++) Assert.Null(Protocol.Read(bytes.AsSpan(0, k)));   // truncations are malformed
        // A yaw past pi reads as the same heading.
        var turned = Protocol.Read(Protocol.Write(new Snapshot(1, 1, 0, [players[0], players[5] with { Yaw = 7f }], Self: 1, Lean: 1))) as Snapshot;
        Assert.Equal(7f - MathF.Tau, turned!.Players[1].Yaw, 3);
        // The writer refuses a lean record for the recipient or an id over 16 bits; the reader a lean block of none, more than 32 records, or a full lean record.
        Assert.Throws<ArgumentException>(() => Protocol.Write(new Snapshot(1, 1, 0, [players[0]], Self: 1, Lean: 1)));
        Assert.Throws<ArgumentException>(() => Protocol.Write(new Snapshot(1, 1, 0, [Body(70000, Vector3.Zero)], Lean: 1)));
        var empty = Protocol.Write(new Snapshot(1, 1, 0, [players[0]], Self: 1));
        int at = 13 + 1;
        Assert.Equal(1, empty[at]);
        var flagged = empty.ToList();
        flagged[at] = (byte)(1 | Protocol.LeanBlockFlag);
        flagged.Insert(at + 1 + Protocol.FullRecordBytes, 0);
        Assert.Null(Protocol.Read(flagged.ToArray()));
        var thirtyThree = Protocol.Write(new Snapshot(1, 1, 0, [.. players.Take(4), .. players.Skip(4).Take(1)], Self: 1, Lean: 1)).ToList();
        thirtyThree[at + 1 + Protocol.FullRecordBytes + 3 * Protocol.SlimRecordBytes] = 29;   // 4 + 29 = 33 records
        Assert.Null(Protocol.Read(thirtyThree.ToArray()));
    }

    [Fact]
    public void Shot_indices_of_16_and_more_round_trip_for_every_hit_and_under_16_keep_protocol_91s_bytes()
    {
        var players = Enumerable.Range(0, Protocol.MaxPlayers).Select(k => Body(k < 4 ? k + 1 : TfwContracts.PodIdFirst + k, new Vector3(k * 2f, 1f, 0f))).ToArray();
        var shots = new List<ShotView>();
        foreach (int shooter in new[] { 1, 15, 16, 17, 23 })
        {
            int id = players[shooter].PlayerId;
            var end = players[shooter].Position + new Vector3(3f, 4f, 0.5f);
            shots.Add(new ShotView(id, 7, 2, ShotHit.None, -1, end, 1));
            shots.Add(new ShotView(id, 7, 1, ShotHit.World, 9, end, 2));
            shots.Add(new ShotView(id, 7, 0, ShotHit.Body, -1, end, 3, Armoured: true));
            shots.Add(new ShotView(id, 7, 0, ShotHit.Head, -1, end, 0));
        }
        var s = new Snapshot(1, 1, 0, players[..24], Shots: [.. shots], Lean: 20);   // a spectator's view (no full record) of 24 records: room for every shot
        var bytes = Protocol.Write(s);
        var back = Assert.IsType<Snapshot>(Protocol.Read(bytes));
        Assert.True(bytes.Length <= Protocol.SnapshotBudget);
        Assert.Equal(24, back.Players.Length);
        var expected = shots.Take(back.Shots!.Length).ToList();
        Assert.Equal(shots.Count, back.Shots.Length);
        for (int k = 0; k < back.Shots.Length; k++)
        {
            var (want, got) = (expected[k], back.Shots[k]);
            Assert.Equal((want.Shooter, want.Hit, want.Surface, want.Armoured, want.Stage, want.Age), (got.Shooter, got.Hit, got.Surface, got.Armoured, got.Stage, got.Age));
        }
        // A snapshot of one index-31 shot: all 32 records fit with nothing else.
        var high = new ShotView(players[31].PlayerId, 7, 0, ShotHit.Body, -1, players[31].Position + Vector3.UnitX, 0, Armoured: true);
        var one = Assert.IsType<Snapshot>(Protocol.Read(Protocol.Write(s with { Players = players, Lean = Protocol.MaxPlayers - 4, Shots = [high] })));
        Assert.Equal(Protocol.MaxPlayers, one.Players.Length);
        Assert.Equal((high.Shooter, true, ShotHit.Body), (one.Shots![0].Shooter, one.Shots[0].Armoured, one.Shots[0].Hit));
        // Under 16 the third byte is protocol 91's: stage | surface << 3 | armour << 7.
        var small = Protocol.Write(new Snapshot(1, 1, 0, players[..2], Self: 1, Shots: [new ShotView(players[1].PlayerId, 7, 2, ShotHit.Body, -1, Vector3.One, 0, Armoured: true)]));
        Assert.Equal(2, small[13 + 1]);   // 2 records, no lean flag
        Assert.Equal((byte)(2 | 15 << 3 | 128), small[^7]);
        Assert.Equal((byte)(1 | (int)ShotHit.Body << 4), small[^9]);
        // A player hit naming a surface other than 15 or 14 is malformed.
        var bad = (byte[])small.Clone();
        bad[^7] = (byte)(2 | 9 << 3);
        Assert.Null(Protocol.Read(bad));
    }

    [Fact]
    public void The_Warfront_order_puts_the_recipient_and_humans_first_then_the_AI_with_the_nearest_pod_first()
    {
        var all = new[]
        {
            Body(TfwContracts.PodIdFirst + 5, Vector3.Zero), Body(3, Vector3.Zero), Body(TfwContracts.PuppetIdFirst + 1, Vector3.Zero), Body(TfwContracts.PodIdFirst, Vector3.Zero), Body(2, Vector3.Zero),
            Body(TfwContracts.PuppetIdFirst, Vector3.Zero), Body(TfwContracts.PodIdFirst + 6, Vector3.Zero), Body(1, Vector3.Zero),
        };
        Vector3? PodAt(int id) => id < TfwContracts.PodIdFirst + 5 ? new Vector3(200f, 0f, 0f) : new Vector3(20f, 0f, 0f);
        var ordered = GameServer.WarfrontSnapshotOrder(all, 3, Vector3.Zero, PodAt, out int lean);
        Assert.Equal([3, 1, 2, TfwContracts.PuppetIdFirst, TfwContracts.PuppetIdFirst + 1, TfwContracts.PodIdFirst + 5, TfwContracts.PodIdFirst + 6, TfwContracts.PodIdFirst], ordered.Select(p => p.PlayerId));
        Assert.Equal(5, lean);
    }

    [Fact]
    public void A_scoreboard_of_32_long_names_stays_within_one_packet_leaving_out_its_last_rows()
    {
        var rows = Enumerable.Range(1, Protocol.MaxPlayers).Select(i => new ScoreEntry(i, Team.SpaceMarines, 1, 65535, 65535, 65535, 3, ("Name" + i).PadRight(PlayerNames.MaxLength, 'x'), 999, true, 255, 255)).ToArray();
        var board = new Scoreboard(1, true, 1, 1f, true, rows, Spectators: [.. Enumerable.Range(100, Protocol.MaxSpectators).Select(i => new SpectatorEntry(i, new string('s', PlayerNames.MaxLength)))]);
        var bytes = Protocol.Write(board);
        var back = Assert.IsType<Scoreboard>(Protocol.Read(bytes));
        output.WriteLine($"32 rows of {PlayerNames.MaxLength}-letter names: {back.Players.Length} written, {bytes.Length} bytes");
        Assert.True(bytes.Length <= Protocol.SnapshotBudget);
        Assert.True(back.Players.Length < Protocol.MaxPlayers && back.Players.Length >= Protocol.MaxMatchPlayers);
        Assert.Equal(rows.Take(back.Players.Length), back.Players);
        // Four humans, eight retainers and four pods with their real names fit whole.
        var real = rows.Take(12).Concat(Enumerable.Range(0, 20).Select(k => new ScoreEntry(TfwContracts.PodIdFirst + k, Team.SpaceMarines, 1, 0, 0, 1, 3, TfwDropPodRules.Names[k % 5], 0))).ToArray();
        var whole = Assert.IsType<Scoreboard>(Protocol.Read(Protocol.Write(board with { Players = real, Spectators = null })));
        Assert.True(whole.Players.Length >= 12 + 15, $"{whole.Players.Length} rows");
    }
}
