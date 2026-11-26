using System.Numerics;
using SmNet.Extras.Musou.Campaign;
using SmServer;
using SmSim;
using SmSim.Extras.Musou.Campaign;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Warfront.Pods;

/// <summary>
/// Phase B T2b review fix (protocol 94): a crowded Warfront snapshot's lean records are cut against the worst case of every other block, so the same ones stay in from snapshot to
/// snapshot (<see cref="LeanSchedule.Capacity"/>); when the pod Marines still do not all fit they take turns (<see cref="LeanSchedule.Pick"/>), and the client holds a missing lean record for a second
/// (<see cref="LeanHold"/>), so every pod Marine is drawn. The pod's battle cry is timed from the pod alone (<see cref="TfwPodCry"/>).
/// </summary>
public class WarfrontLeanScheduleTests(ITestOutputHelper output)
{
    static PlayerState Body(int id, Vector3 at) => new(id, at, new Vector3(1.25f, -2.5f, 0f), 1.0f, true)
    {
        Alive = true, Class = 3, Team = Team.SpaceMarines, Slot = WeaponSlot.Ranged, Primary = 2, MeleeChoice = 1, Pitch = -0.2f,
    };

    static Vector3 PodAt(int id) => new(100f * ((id - TfwContracts.PodIdFirst) / 5 + 1), 0f, 0f);

    /// <summary>Four humans (1 receives), eight retainers, the allied pair (<paramref name="pair"/>) and <paramref name="pods"/> pods: the owner's worst case is 4 + 8 + 2 + 20 = 34 World players.</summary>
    static PlayerState[] Crowd(int pods, bool pair)
    {
        var all = new List<PlayerState>();
        for (int id = 1; id <= 4; id++) all.Add(Body(id, new Vector3(id, 0f, 0f)));
        for (int k = 0; k < 8 + (pair ? 2 : 0); k++) all.Add(Body(TfwContracts.PuppetIdFirst + k, new Vector3(k, 5f, 0f)));
        for (int k = 0; k < pods * 5; k++) all.Add(Body(TfwContracts.PodIdFirst + k, PodAt(TfwContracts.PodIdFirst + k)));
        return [.. all];
    }

    static Snapshot Packed(PlayerState[] players, int lean, int projectiles, int kills, int shots) =>
        new(1, 1, 1, players, Self: 1, Lean: lean,
            Projectiles: [.. Enumerable.Range(0, projectiles).Select(k => new ProjectileView(60000 + k, 254, 255, new Vector3(k), ProjectileFlags.Detonated))],
            Match: new MatchView(MatchState.Playing, Team.None, 1, 2, 3),
            Kills: [.. Enumerable.Range(0, kills).Select(k => new KillView(k, 1, 2, KillCause.Weapon, 0, Vector3.UnitX))],
            Points: [], Hits: new HitView(1, 2, 3, 4, 5, HitFlags.All, 1f, 1f), Shots: ShotTests.ManyShots(players, shots));

    [Fact]
    public void The_stable_capacity_is_measured_against_the_worst_case_of_the_other_blocks()
    {
        int worst = LeanSchedule.WorstOtherBytes(0), quiet = Protocol.SnapshotBytesOf(0, 0, 0, 0, 0, false, 0);
        int four = LeanSchedule.Capacity(3, true, 0), fourPoints = LeanSchedule.Capacity(3, true, Protocol.MaxSnapshotPoints), spectator = LeanSchedule.Capacity(4, false, 0);
        int fourBytes = Protocol.SnapshotBytesOf(12, 4, 0, 4, 3, true, four);
        output.WriteLine($"other blocks: {quiet} bytes quiet, {worst} at their most (moves by {worst - quiet}: {(worst - quiet) / Protocol.LeanRecordBytes} lean records)");
        output.WriteLine($"4 humans: {four} lean records always fit ({fourPoints} with 8 control points; a spectator of 4 humans {spectator}); {fourBytes} bytes at the worst case");
        Assert.Equal((279, 39), (worst, quiet));
        Assert.Equal((18, 17, 22), (four, fourPoints, spectator));
        Assert.True(fourBytes <= Protocol.SnapshotBudget && Protocol.SnapshotBytesOf(12, 4, 0, 4, 3, true, four + 1) > Protocol.SnapshotBudget);
        Assert.Equal(22, LeanSchedule.Capacity(0, true, 0));   // a lone recipient: the budget, not the 32 records, is the limit
        Assert.Equal(0, LeanSchedule.Capacity(30, false, 0));    }

    [Theory]
    [InlineData(4, true)]    // the owner's worst case: 34 World players
    [InlineData(4, false)]
    [InlineData(3, true)]
    [InlineData(2, true)]
    public void The_cut_does_not_move_with_projectiles_kills_or_shots_and_every_pod_Marine_is_sent_within_the_sweep(int pods, bool pair)
    {
        var crowd = Crowd(pods, pair);
        int aiMarines = 8 + (pair ? 2 : 0) + pods * 5;
        int sweep = LeanSchedule.SweepSnapshots(aiMarines, 8 + (pair ? 2 : 0), LeanSchedule.Capacity(3, true, 0));
        var lastSent = crowd.Where(p => TfwContracts.IsPuppet(p.PlayerId)).ToDictionary(p => p.PlayerId, _ => -1L);
        long longestGap = 0;
        int maxBytes = 0, minWritten = int.MaxValue;
        for (long seq = 0; seq < 40; seq++)
        {
            var picked = GameServer.WarfrontSnapshotPick(crowd, 1, Vector3.Zero, id => TfwContracts.IsPodId(id) ? PodAt(id) : null, 0, seq, out int lean);
            Assert.Equal(4, picked.Length - lean);   // the humans always slim
            Assert.Equal(Enumerable.Range(0, 8 + (pair ? 2 : 0)).Select(k => TfwContracts.PuppetIdFirst + k), picked.Skip(4).Take(8 + (pair ? 2 : 0)).Select(p => p.PlayerId));   // the retainers and the pair always
            // The same pick is written whole whatever the other blocks hold: quiet, busy, at their most.
            foreach (var (projectiles, kills, shots) in new[] { (0, 0, 0), (5, 1, 3), (12, 4, Protocol.MaxSnapshotShots) })
            {
                var bytes = Protocol.Write(Packed(picked, lean, projectiles, kills, shots));
                var back = Assert.IsType<Snapshot>(Protocol.Read(bytes));
                Assert.Equal(picked.Select(p => p.PlayerId), back.Players.Select(p => p.PlayerId));
                Assert.True(bytes.Length <= Protocol.SnapshotBudget, $"{bytes.Length} bytes");
                Assert.True(back.Shots!.Length >= Math.Min(shots, Protocol.MinSnapshotShots));
                maxBytes = Math.Max(maxBytes, bytes.Length);
                minWritten = Math.Min(minWritten, back.Players.Length);
            }
            foreach (var p in picked.Skip(4))
            {
                long previous = lastSent[p.PlayerId];
                longestGap = Math.Max(longestGap, seq - previous);
                lastSent[p.PlayerId] = seq;
            }
        }
        foreach (var (id, last) in lastSent) longestGap = Math.Max(longestGap, 40 - last);
        output.WriteLine($"{pods} pods, pair {pair}: {crowd.Length} World players, {minWritten} records a snapshot, at most {maxBytes} bytes; each AI Marine sent at least every {longestGap} snapshots (sweep {sweep})");
        Assert.True(longestGap <= sweep, $"gap {longestGap} > sweep {sweep}");
        Assert.True(sweep / 30.0 < LeanHold.HoldSeconds, "the hold outlasts the sweep at 30 snapshots a second");
        Assert.True(sweep <= 3, $"sweep {sweep}");   // 0.1 s at 30 Hz at the worst case
    }

    [Fact]
    public void Pick_covers_every_record_within_the_sweep_for_any_count_head_and_capacity()
    {
        for (int count = 1; count <= 40; count++)
            for (int head = 0; head <= count; head++)
                for (int capacity = 1; capacity <= 32; capacity++)
                {
                    int sweep = LeanSchedule.SweepSnapshots(count, head, capacity);
                    var last = new long[count];
                    Array.Fill(last, -1);
                    for (long seq = 0; seq < 3 * sweep + 2; seq++)
                    {
                        var picked = LeanSchedule.Pick(count, head, capacity, seq);
                        Assert.Equal(Math.Min(count, capacity), picked.Length);
                        Assert.Equal(picked.Length, picked.Distinct().Count());
                        Assert.All(picked, k => Assert.InRange(k, 0, count - 1));
                        if (head < capacity) for (int k = 0; k < head; k++) Assert.Contains(k, picked);
                        foreach (int k in picked) { Assert.True(seq - last[k] <= sweep, $"count {count} head {head} capacity {capacity}: index {k} waited {seq - last[k]} > {sweep}"); last[k] = seq; }
                    }
                }
        Assert.Empty(LeanSchedule.Pick(5, 0, 0, 3));
        Assert.Equal(0, LeanSchedule.SweepSnapshots(5, 0, 0));
    }

    [Fact]
    public void The_hold_keeps_a_missing_lean_record_for_a_second_and_never_acts_without_lean_records()
    {
        const int rate = 30;
        var hold = new LeanHold();
        var human = Body(1, Vector3.Zero);
        var pod = Body(TfwContracts.PodIdFirst, new Vector3(10f, 0f, 0f));
        var fallen = Body(TfwContracts.PodIdFirst + 1, new Vector3(20f, 0f, 3f)) with { Grounded = false };
        // Arrives lean.
        var first = new Dictionary<int, PlayerState> { [1] = human, [pod.PlayerId] = pod, [fallen.PlayerId] = fallen };
        Assert.Equal(0, hold.Apply(100, [human, pod, fallen], 2, first, rate));
        // Missing next snapshot: put back, carried along its ground velocity; one in the air stands where it was.
        var second = new Dictionary<int, PlayerState> { [1] = human };
        Assert.Equal(2, hold.Apply(101, [human], 0, second, rate));
        Assert.Equal(pod.Position + new Vector3(pod.Velocity.X, pod.Velocity.Y, 0f) / rate, second[pod.PlayerId].Position);
        Assert.Equal(fallen.Position, second[fallen.PlayerId].Position);
        // The carry stops at CarrySeconds; the hold ends after HoldSeconds.
        var late = new Dictionary<int, PlayerState>();
        hold.Apply(100 + rate, [], 0, late, rate);
        Assert.Equal(pod.Position + new Vector3(pod.Velocity.X, pod.Velocity.Y, 0f) * (float)LeanHold.CarrySeconds, late[pod.PlayerId].Position);
        var gone = new Dictionary<int, PlayerState>();
        Assert.Equal(0, hold.Apply(100 + rate + 1, [], 0, gone, rate));
        Assert.Empty(gone);
        Assert.Empty(hold.Held);
        // A record sent slim ends the hold of its id (a snapshot of 16 or fewer).
        hold.Apply(200, [human, pod], 1, new Dictionary<int, PlayerState> { [1] = human, [pod.PlayerId] = pod }, rate);
        hold.Apply(201, [human, pod], 0, new Dictionary<int, PlayerState> { [1] = human, [pod.PlayerId] = pod }, rate);
        var after = new Dictionary<int, PlayerState> { [1] = human };
        Assert.Equal(0, hold.Apply(202, [human], 0, after, rate));
        Assert.Single(after);
        // Without lean records (Classic, Musou Mode) nothing is ever held.
        var classic = new LeanHold();
        for (uint t = 1; t < 50; t++)
        {
            var players = t % 2 == 0 ? new[] { human, pod } : new[] { human };
            var map = players.ToDictionary(p => p.PlayerId);
            Assert.Equal(0, classic.Apply(t, players, 0, map, rate));
            Assert.Equal(players.Length, map.Count);
        }
    }

    [Fact]
    public void The_pod_cry_is_timed_from_the_pod_and_picks_each_speaker_when_his_slot_begins()
    {
        const int rate = 30;
        var slots = TfwPodCry.SlotTicks(1000, rate);
        Assert.Equal(TfwDropPodRules.Marines, slots.Length);
        double spawn = 1000 + (TfwDropPodRules.DoorSeconds + TfwDropPodRules.StepOutSeconds) * rate;
        for (int k = 0; k < slots.Length; k++) Assert.Equal(spawn + TfwDropPodRules.ShoutDelay(1000u, k) * rate, slots[k], 6);
        Assert.True(slots.Zip(slots.Skip(1)).All(x => x.First < x.Second));
        int a = TfwContracts.PodIdFirst, b = a + 1, c = a + 2;
        Assert.Equal(a, TfwPodCry.Speaker([c, a, b], []));
        Assert.Equal(c, TfwPodCry.Speaker([c, a, b], [a, b]));
        Assert.Null(TfwPodCry.Speaker([a], [a]));
        Assert.Null(TfwPodCry.Speaker([], []));   // none in sight: the pod's own voice in a crowded run, silence elsewhere
    }
}
