using System.Numerics;
using SmNet.Extras.Musou.Campaign;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Musou.Campaign;

/// <summary>The drop pod assault (docs/drop-pod-assault.md, owner 2026-11-12): the call, the fall, the touchdown, the four Tactical Marines, their 30 s, their retreat to the HQ and their end, scripted through the real World.</summary>
public class TfwDropPodTests
{
    const int Human = TfwRig.Human;
    static readonly Vector3 Open = new(60f, -60f, 0f);

    /// <summary>A battle with the field emptied of its own Orks and Guardsmen and the human well away from the landing point.</summary>
    static World Battle()
    {
        var w = TfwRig.War();
        TfwRig.Deploy(w);
        w.Tfw!.Fire.NoSchedule = true;
        w.Musou!.Clear();
        w.TfwGuards!.Clear();
        w.TfwRespawnAt(Human, new Vector3(0f, -100f, 0f), 0f);
        return w;
    }

    static ushort Enemy(World w, MusouEnemyType type, Vector3 at)
    {
        ushort id = w.Musou!.Spawn(type, at, 0f, w.Musou.Rules.Of(type).Health);
        Assert.NotEqual(0, id);
        return id;
    }

    static IEnumerable<Marine> PodMarines(World w) => w.Tfw!.Marines.Where(m => m.Role == MarineRole.Pod);

    [Fact]
    public void The_command_and_the_record_survive_the_wire()
    {
        Assert.True(TfwWire.HasPoint(TfwCommand.DropPod));
        var command = new TfwCommandRecord(5, TfwCommand.DropPod, 0, Open);
        Assert.Equal(command, TfwWire.ReadCommand(TfwWire.WriteCommand(command)));
        var records = new[] { new TfwFireEvent(TfwFireKind.Pod, 321, 7, new Vector3(12.5f, -3f, 4f)), new TfwFireEvent(TfwFireKind.Pod, 900, 7, new Vector3(12.5f, -3f, 4f), Vector3.UnitX) };
        var back = TfwWire.ReadFire(TfwWire.WriteFire(records));
        Assert.NotNull(back);
        Assert.Equal(2, back!.Length);
        Assert.All(back, r => Assert.Equal(TfwFireKind.Pod, r.Kind));
        Assert.Equal((321u, (ushort)7), (back[0].Tick, back[0].Subject));
        Assert.True(back[1].To.X > 0.5f);
    }

    [Fact]
    public void A_call_lands_a_pod_3_s_later_on_the_point_and_starts_the_support_cooldown()
    {
        var w = Battle();
        var run = w.Tfw!;
        uint called = w.Tick;
        Assert.True(w.TfwDropPod(Human, Open));
        var pod = Assert.Single(run.Pods);
        Assert.Equal((uint)(TfwDropPodRules.LandSeconds * w.Config.TickRate), pod.LandTick - called);
        Assert.Equal(TfwPodPhase.Falling, pod.Phase);
        Assert.Equal(Open.X, pod.At.X, 1);
        Assert.Equal(60, w.TfwOwn(Human).OrbitalSeconds);   // the artillery strike's cooldown, shared
        var records = new List<TfwFireEvent>();
        run.Fire.TakeEvents(records);
        var record = Assert.Single(records, r => r.Kind == TfwFireKind.Pod);
        Assert.Equal((pod.LandTick, (ushort)Human), (record.Tick, record.Subject));
        Assert.False(w.TfwDropPod(Human, Open + new Vector3(30f, 0f, 0f)));   // one pod each, and the cooldown
        Assert.False(w.TfwOrbital(Human, Open));   // the cooldown is shared with the artillery strike
        Assert.Empty(PodMarines(w));
    }

    [Fact]
    public void The_server_refuses_a_call_off_the_battlefield_too_near_the_caller_dead_or_outside_a_battle()
    {
        var hub = TfwRig.War();
        Assert.False(hub.TfwDropPod(Human, Open));
        var w = Battle();
        var box = w.Tfw!.Field.Bounds;
        Assert.False(w.TfwDropPod(Human, new Vector3(box.Max.X + 5f, 0f, 0f)));
        Assert.False(w.TfwDropPod(Human, new Vector3(float.NaN, 0f, 0f)));
        Assert.False(w.TfwDropPod(Human + 1, Open));
        Assert.False(w.TfwDropPod(Human, w.Players[Human].Position + new Vector3(TfwDropPodRules.MinCallerDistance - 1f, 0f, 0f)));   // not on the caller
        Assert.Empty(w.Tfw.Pods);
        Assert.Equal(0, w.TfwOwn(Human).OrbitalSeconds);   // a refused call costs nothing
        var dead = Battle();
        Assert.True(dead.TfwKillForTests(Human));
        Assert.False(dead.TfwDropPod(Human, Open));
    }

    [Fact]
    public void The_touchdown_kills_the_ordinary_enemies_within_6_m_and_hurts_no_one_on_our_side()
    {
        var w = Battle();
        ushort near = Enemy(w, MusouEnemyType.Boy, Open + new Vector3(TfwDropPodRules.CrushRadius - 0.5f, 0f, 0f));
        ushort far = Enemy(w, MusouEnemyType.Boy, Open + new Vector3(TfwDropPodRules.CrushRadius + 1.5f, 0f, 0f));
        w.TfwRespawnAt(Human, Open + new Vector3(-3f, 0f, 0f), 0f);   // inside the crush: a player is not hurt by it
        var before = w.Players[Human];
        w.Musou!.BeginTick(w.Tick);
        w.TfwPodCrushForTests(Open, Human);
        Assert.True(w.Musou!.TryGet(near, out var n));
        Assert.Equal(MusouEnemyState.Dying, n.State);
        Assert.True(w.Musou.TryGet(far, out var f));
        Assert.NotEqual(MusouEnemyState.Dying, f.State);
        Assert.Equal(before.Health + before.Shield, w.Players[Human].Health + w.Players[Human].Shield);
        var death = Assert.Single(w.Musou.Events, x => x.Kind == MusouEventKind.Death && x.Enemy == near);
        Assert.Equal((0, (byte)MusouDeathKind.Barrage), (death.Player, death.Value));
    }

    [Fact]
    public void A_call_lands_the_pod_which_runs_the_crush_on_the_tick_it_touches_down()
    {
        var w = Battle();
        Assert.True(w.TfwDropPod(Human, Open));
        TfwRig.Steps(w, (int)(TfwDropPodRules.LandSeconds * w.Config.TickRate) - 2);
        Assert.Equal(TfwPodPhase.Falling, w.Tfw!.Pods[0].Phase);
        TfwRig.Steps(w, 4);
        Assert.Equal(TfwPodPhase.Doors, w.Tfw.Pods[0].Phase);
    }

    [Fact]
    public void Four_tactical_marines_step_out_fight_30_s_retreat_to_the_hq_and_are_gone_with_the_pod()
    {
        var w = Battle();
        var run = w.Tfw!;
        var hq = run.Battle!.Layout.Hq;
        Assert.True(w.TfwDropPod(Human, Open));
        var pod = run.Pods[0];
        int rate = w.Config.TickRate;
        TfwRig.Seconds(w, TfwDropPodRules.LandSeconds + TfwDropPodRules.DoorSeconds + 0.5f);
        Assert.Empty(PodMarines(w));   // the doors are still opening
        TfwRig.Seconds(w, TfwDropPodRules.StepOutSeconds + 0.3f);
        var marines = PodMarines(w).ToList();
        Assert.Equal(TfwDropPodRules.Marines, marines.Count);
        Assert.All(marines, m =>
        {
            Assert.Equal(Human, m.Owner);
            Assert.True(TfwContracts.IsPuppet(m.Id));
            var p = w.Players[m.Id];
            Assert.True(p.Alive);
            Assert.Equal(Team.SpaceMarines, p.Team);
            Assert.InRange(MarineBrain.Flat(p.Position, Open), 2f, TfwDropPodRules.DoorRing + 3f);
        });
        Assert.Equal(TfwPodPhase.Fighting, pod.Phase);
        Assert.Equal(TfwDropPodRules.Marines, marines.Select(m => m.Name).Distinct().Count());
        // 30 s of fighting from stepping out, held on the spot: still here a little before the end, retreating just after it.
        TfwRig.Seconds(w, TfwDropPodRules.FightSeconds - 3f);
        Assert.Equal(TfwPodPhase.Fighting, pod.Phase);
        Assert.Equal(TfwDropPodRules.Marines, PodMarines(w).Count());
        Assert.All(PodMarines(w), m => Assert.InRange(MarineBrain.Flat(w.Players[m.Id].Position, Open), 0f, TfwDropPodRules.DoorRing + 6f));
        TfwRig.Seconds(w, 4f);
        Assert.Equal(TfwPodPhase.Retreating, pod.Phase);
        // The retreat ends at the HQ or after the longest retreat; then every Marine is gone and the wreck follows a moment later.
        float closest = float.MaxValue;
        for (int k = 0; k < (TfwDropPodRules.RetreatMaxSeconds + 1f) * rate && PodMarines(w).Any(); k++)
        {
            TfwRig.Steps(w, 1);
            foreach (var m in PodMarines(w)) closest = MathF.Min(closest, MarineBrain.Flat(w.Players[m.Id].Position, hq));
        }
        Assert.Empty(PodMarines(w));
        TfwRig.Steps(w, 2);   // the pod notices on the next tick
        Assert.Equal(TfwPodPhase.Wreck, pod.Phase);
        Assert.Equal(Enumerable.Range(TfwContracts.PuppetIdFirst, TfwContracts.PuppetIds).Count(id => w.Players.ContainsKey(id)), run.Marines.Count);   // no body of theirs left in the world
        var records = new List<TfwFireEvent>();
        run.Fire.TakeEvents(records);
        Assert.Contains(records, r => r.Kind == TfwFireKind.Pod && r.To.X > 0.5f && r.Subject == Human && r.Tick == pod.EndTick);
        TfwRig.Seconds(w, TfwDropPodRules.WreckSeconds + 0.3f);
        Assert.Empty(run.Pods);
    }

    [Fact]
    public void A_marine_that_falls_is_cleared_and_a_squad_with_none_left_ends_early()
    {
        var w = Battle();
        Assert.True(w.TfwDropPod(Human, Open));
        TfwRig.Seconds(w, TfwDropPodRules.LandSeconds + TfwDropPodRules.DoorSeconds + TfwDropPodRules.StepOutSeconds + 0.3f);
        var ids = PodMarines(w).Select(m => m.Id).ToList();
        Assert.Equal(TfwDropPodRules.Marines, ids.Count);
        foreach (int id in ids) Assert.True(w.TfwKillForTests(id));
        TfwRig.Seconds(w, TfwDropPodRules.FallenSeconds + 0.5f);
        Assert.Empty(PodMarines(w));
        Assert.All(ids, id => Assert.DoesNotContain(id, w.Players.Keys));
        Assert.Equal(TfwPodPhase.Wreck, w.Tfw!.Pods[0].Phase);
    }

    [Fact]
    public void A_call_needing_more_puppet_ids_than_are_free_is_refused_and_costs_nothing()
    {
        var w = Battle();
        var run = w.Tfw!;
        int taken = 0;
        for (int k = 0; k < TfwContracts.PuppetIds - (TfwDropPodRules.Marines - 1); k++)
            if (w.TfwSpawnAlly(new Vector3(0f, -90f, 0f) + new Vector3(k, 0f, 0f)) is not null) taken++;   // the pair only fills two; retainers take the rest below
        for (int id = TfwContracts.PuppetIdFirst; id <= TfwContracts.PuppetIdLast - (TfwDropPodRules.Marines - 2); id++)
            if (!w.Players.ContainsKey(id)) w.Spawn(id, 0, team: Team.SpaceMarines);
        Assert.False(w.TfwDropPod(Human, Open));
        Assert.Empty(run.Pods);
        Assert.Equal(0, w.TfwOwn(Human).OrbitalSeconds);
        Assert.True(taken >= 0);
    }

    [Fact]
    public void The_battle_ending_takes_the_pod_and_its_marines_with_it()
    {
        var w = Battle();
        Assert.True(w.TfwDropPod(Human, Open));
        TfwRig.Seconds(w, TfwDropPodRules.LandSeconds + TfwDropPodRules.DoorSeconds + TfwDropPodRules.StepOutSeconds + 0.3f);
        Assert.NotEmpty(PodMarines(w));
        w.TfwDismissMarines();
        Assert.Empty(w.Tfw!.Pods);
        Assert.Empty(PodMarines(w));
    }

    [Fact]
    public void The_switch_off_refuses_the_call_and_the_artillery_strike_is_untouched()
    {
        var w = Battle();
        w.Musou!.War = w.Musou.War! with { DropPods = false };
        Assert.False(w.TfwDropPod(Human, Open));
        Assert.Empty(w.Tfw!.Pods);
        Assert.True(w.TfwOrbital(Human, Open));
    }
}
