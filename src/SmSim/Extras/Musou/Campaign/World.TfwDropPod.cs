using System.Numerics;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmSim;

/// <summary>
/// The drop pod assault in the World (Musou Mode's second support ability; docs/drop-pod-assault.md; owner, 2026-11-12): a player's call, the pod's touchdown (the clients draw the fall from tag 11's
/// <see cref="TfwFireKind.Pod"/> record), the four Tactical Marines it brings (<see cref="MarineRole.Pod"/>: puppets, the AI Marine system), their 30 s fight at the pod, their retreat to the Imperial HQ and
/// their end. Runs from <c>TfwFireStep</c> (a battle, or the Firing Range). The pod and its Marines hurt no friend; the touchdown kills every enemy within
/// <see cref="TfwDropPodRules.CrushRadius"/> (30 m) at <see cref="TfwDropPodRules.CrushDamageFactor"/> (3) x a shell's damage (owner 2026-11-12).
/// </summary>
public sealed partial class World
{
    /// <summary>
    /// A player's call for a drop pod at <paramref name="point"/> (the command DropPod; the client only asks): refused, with nothing changed, unless the strike's own conditions hold (a battle on, the player in it
    /// and alive, or at the Firing Range; the point on the battlefield; the player's support cooldown over, <see cref="TfwFireRules.OrbitalCooldownSeconds"/>, shared with the artillery strike) and the player has
    /// no pod out, the point is at least <see cref="TfwDropPodRules.MinCallerDistance"/> from the player and <see cref="TfwDropPodRules.Marines"/> puppet ids are free. Whether it was accepted.
    /// </summary>
    public bool TfwDropPod(int player, Vector3 point)
    {
        if (_tfw is not { } run || _state != MatchState.Playing || _musou?.War is { DropPods: false }) return false;
        bool atRange = run.Battle is null && run.RangeOn && run.Field.Range is not null && run.InRange.Contains(player);
        if (!atRange && (run.Battle is null || !run.InField.Contains(player))) return false;
        if (!_players.TryGetValue(player, out var p) || !p.Alive || TfwContracts.IsPuppet(player)) return false;
        if (!atRange && run.Fire.OrbitalTicksLeft(player, Tick) > 0) return false;
        if (run.PodOf(player) is not null) return false;
        var box = atRange ? run.Field.Range!.Box : run.Field.Bounds;
        if (!float.IsFinite(point.X + point.Y + point.Z) || point.X < box.Min.X || point.X > box.Max.X || point.Y < box.Min.Y || point.Y > box.Max.Y) return false;
        point = atRange ? Vector3.Clamp(point, box.Min, box.Max) : run.Field.Clamp(point);
        point.Z = TfwGroundBelow(point, point.Z);
        if (MarineBrain.Flat(p.Position, point) < TfwDropPodRules.MinCallerDistance) return false;
        if (TfwFreePuppetIds() - run.PodIdsReserved < TfwDropPodRules.Marines) return false;   // the ids other pods have called count as taken
        int rate = Math.Max(1, Config.TickRate);
        uint land = Tick + (uint)MathF.Round(TfwDropPodRules.LandSeconds * rate);
        uint spawn = land + (uint)MathF.Round((TfwDropPodRules.DoorSeconds + TfwDropPodRules.StepOutSeconds) * rate);
        uint fightEnd = spawn + (uint)MathF.Round(TfwDropPodRules.FightSeconds * rate);
        run.AddPod(new TfwPod(player, land, spawn, fightEnd, point));
        run.Fire.Pod(land, player, point, gone: false);
        if (!atRange) run.Fire.OrbitalCalled(player, Tick, rate);
        return true;
    }

    /// <summary>Puppet ids not in use.</summary>
    int TfwFreePuppetIds()
    {
        int free = 0;
        for (int id = TfwContracts.PuppetIdFirst; id <= TfwContracts.PuppetIdLast; id++) if (!_players.ContainsKey(id)) free++;
        return free;
    }

    /// <summary>The pods end with the field (the battle, or the last player leaving the Firing Range): their Marines go, the pods are forgotten.</summary>
    void TfwPodsEnd(TfwRun run)
    {
        if (run.Pods.Count == 0 && !run.Marines.Any(m => m.Role == MarineRole.Pod)) return;
        foreach (var m in run.Marines.Where(m => m.Role == MarineRole.Pod).ToList()) TfwDismissMarine(m.Id);
        run.ForgetPods();
    }

    /// <summary>One tick of the pods: touchdown, the Marines stepping out, the fight's end, the wreck's removal.</summary>
    void TfwDropPodStep()
    {
        var run = _tfw!;
        if (run.Pods.Count == 0) return;
        int rate = Math.Max(1, Config.TickRate);
        for (int k = run.Pods.Count - 1; k >= 0; k--)
        {
            var pod = run.Pods[k];
            switch (pod.Phase)
            {
                case TfwPodPhase.Falling when Tick >= pod.LandTick:
                    pod.Phase = TfwPodPhase.Doors;
                    TfwPodCrush(pod);
                    break;
                case TfwPodPhase.Doors when Tick >= pod.SpawnTick:
                    pod.Phase = TfwPodPhase.Fighting;
                    TfwPodSpawn(run, pod);
                    break;
                case TfwPodPhase.Fighting when Tick >= pod.FightEndTick:
                    pod.Phase = TfwPodPhase.Retreating;
                    pod.RetreatTick = Tick;
                    break;
            }
            if (pod.Phase is TfwPodPhase.Fighting or TfwPodPhase.Retreating)
            {
                pod.MarineIds.RemoveAll(id => run.MarineOf(id) is null);   // gone: dismissed at the HQ, or killed and cleared
                if (pod.MarineIds.Count == 0)
                {
                    pod.Phase = TfwPodPhase.Wreck;
                    pod.EndTick = Tick + (uint)MathF.Round(TfwDropPodRules.WreckSeconds * rate);
                    run.Fire.Pod(pod.EndTick, pod.Caller, pod.At, gone: true);
                }
            }
            else if (pod.Phase == TfwPodPhase.Wreck && Tick >= pod.EndTick) run.RemovePod(pod);
        }
    }

    /// <summary>The touchdown: every enemy within <see cref="TfwDropPodRules.CrushRadius"/> takes 3 x a shell's damage (ordinary ones die) (a boss takes 3 x a shell's 30), as a shell's kills; nobody on our side is hurt.</summary>
    void TfwPodCrush(TfwPod pod)
    {
        var musou = _musou;
        if (musou is null) return;
        MusouBeginTick();
        float ranged = MusouRangedDealt(pod.Caller);
        int n = musou.Query(pod.At, TfwDropPodRules.CrushRadius, _fireNear, TfwDropPodRules.CrushRadius);
        for (int k = 0; k < n; k++)
        {
            if (!musou.TryGet(_fireNear[k], out var e)) continue;
            if (Vector3.Distance(e.Position, pod.At) > TfwDropPodRules.CrushRadius) continue;
            var away = new Vector2(e.Position.X - pod.At.X, e.Position.Y - pod.At.Y);
            float distance = away.Length();
            var push = distance > 1e-3f ? away / distance : new Vector2(-MathF.Sin(e.Yaw), MathF.Cos(e.Yaw));
            float damage = (e.Id == _tfw!.Battle?.BossId || MusouBossOrks.Is(e.Type) ? TfwFireRules.BossDamage : MathF.Max(e.Health, 1f)) * TfwDropPodRules.CrushDamageFactor * ranged;
            musou.Damage(e.Id, 0, damage, new Vector3(push, 0.8f), out bool killed, marker: 0, cause: MusouDeathKind.Barrage);
            if (killed) _tfw.Fire.ShellKills++;
        }
    }

    /// <summary>Test aid: a pod of <paramref name="caller"/> touches down at <paramref name="at"/> now (the crush, nothing else).</summary>
    public void TfwPodCrushForTests(Vector3 at, int caller = 0) => TfwPodCrush(new TfwPod(caller, Tick, Tick, Tick, at));

    /// <summary>The Marines step out, at the doors round the pod (<see cref="TfwDropPodRules.DoorRing"/>), each to hold the place he stands on.</summary>
    void TfwPodSpawn(TfwRun run, TfwPod pod)
    {
        for (int k = 0; k < TfwDropPodRules.Marines; k++)
        {
            float angle = MathF.Tau * (k + 0.5f) / TfwDropPodRules.Marines;
            var at = pod.At + new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0f) * TfwDropPodRules.DoorRing;
            if (Musou?.Nav is { } nav && nav.Nearest(at, 1.5f, 2.5f) is var node and >= 0) at = nav.Position(node);
            else at.Z = TfwGroundBelow(at, pod.At.Z + 1f);
            string name = TfwDropPodRules.Names[k % TfwDropPodRules.Names.Length];
            if (TfwSpawnPuppet(run, "tactical", at, angle, id => new Marine(id, -1, name, MarineRole.Pod, pod.Caller) { HoldAt = at, Pod = pod }, reserved: true) is { } id) pod.MarineIds.Add(id);
        }
    }

    /// <summary>The Imperial HQ the pod Marines retreat to: the battle's own, the Firing Range's start otherwise.</summary>
    Vector3 TfwPodHome(TfwRun run) => run.Battle?.Layout.Hq ?? run.Field.MarineStart;

    /// <summary>A pod Marine's tick: a fallen one is cleared after <see cref="TfwDropPodRules.FallenSeconds"/>; a retreating one is gone at the HQ or when the retreat's time is up.</summary>
    void TfwPodMarineStep(TfwRun run, Marine m, in PlayerState p)
    {
        int rate = Math.Max(1, Config.TickRate);
        if (!p.Alive)
        {
            if (++m.FallenTicks >= (int)MathF.Round(TfwDropPodRules.FallenSeconds * rate)) TfwDismissMarine(m.Id);
            else _players[m.Id] = p with { RespawnLeft = MarineRules.DeadHold };   // no respawn of the game's own: the Marine stays down
            return;
        }
        if (m.Pod is not { Phase: TfwPodPhase.Retreating } pod) return;
        if (MarineBrain.Flat(p.Position, TfwPodHome(run)) <= TfwDropPodRules.HqRadius || Tick - pod.RetreatTick >= (uint)MathF.Round(TfwDropPodRules.RetreatMaxSeconds * rate)) TfwDismissMarine(m.Id);
    }

    /// <summary>A pod Marine's input: in the fight he holds his place and shoots what he sees within <see cref="MarineRules.HoldRadius"/>; in the retreat he sprints to the HQ and fires at nothing.</summary>
    PlayerInput TfwPodMarineInput(TfwRun run, Marine m, PlayerState self)
    {
        var musou = _musou!;
        bool retreat = m.Pod is { Phase: TfwPodPhase.Retreating };
        Vector3? enemyAt = null, chest = null;
        float distance = float.MaxValue;
        if (!retreat)
        {
            bool struck = m.Target != 0 && musou.TryGet(m.Target, out var held) && held.State != MusouEnemyState.Dying;
            if (!struck || (Tick + (uint)m.Id) % MarineRules.RetargetTicks == 0)
            {
                Span<ushort> nearest = stackalloc ushort[MarineRules.SightCandidates];
                int seen = musou.Query(m.HoldAt, MarineRules.HoldRadius, nearest);
                m.Target = 0;
                for (int k = 0; k < seen && m.Target == 0; k++)
                    if (TfwMarineSees(self, nearest[k])) m.Target = nearest[k];
            }
            if (m.Target != 0 && musou.TryGet(m.Target, out var enemy) && enemy.State != MusouEnemyState.Dying)
            {
                enemyAt = enemy.Position;
                chest = MusouSim.Chest(enemy.Position, musou.Body(enemy.Type).Height);
                distance = MarineBrain.Flat(self.Position, enemy.Position);
            }
        }
        bool strikesOnly = Loadouts.Weapon(Config, self, WeaponSlot.Ranged) is null;
        MarineGoal? goal = retreat
            ? new MarineGoal(TfwPodHome(run), 0f, TfwDropPodRules.HqRadius * 0.5f, Sprint: true)
            : MarineBrain.PlanRetainer(new RetainerSituation(MarineOrder.Hold, self.Position, null, false, m.HoldAt, null, enemyAt, strikesOnly));
        if (goal is { } g && MarineBrain.Flat(self.Position, g.Point) > g.Far && TfwGuards?.Ways is { } ways)
        {
            if (m.WayGoal < 0 || MarineBrain.Flat(m.WayPoint, g.Point) > MarineRules.WayRefresh) { m.WayPoint = g.Point; m.WayGoal = ways.GoalNode(g.Point); }
            if (ways.Carrot(self.Position, m.WayGoal) is { } next) goal = g with { Via = next };
        }
        return MarineBrain.Decide(self, Loadouts.InHand(Config, self), goal, chest, distance, Config.EyeHeight, Tick);
    }
}
