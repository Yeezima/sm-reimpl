using System.Numerics;
using SmContent.Extras.Warfront;
using SmSim;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront;
using SmSim.Extras.Warfront.Campaign;
using SmSim.Extras.Warfront.Fortress;
using SmSim.Extras.Warfront.Hq;
using SmSim.Extras.Warfront.Maps;

namespace SmServer;

/// <summary>Warfront's stage flow on the server (slice W5a): the deployment, the preparation off the tick thread, the stage change, the arrival, the landing, the battle's end.</summary>
public sealed partial class GameServer
{
    // ---- deploy ------------------------------------------------------------------------------------------------------------------------------------

    /// <summary>The leader's Deploy (plan section 3): only from the leader, only on the ship, only a listed target. Same map: land at once; another map: prepare it, then Go.</summary>
    void WarfrontDeploy(int player, WarfrontTarget target, bool serverFallback = false)
    {
        if (_wf is not { } s) return;
        WarfrontApplyQueuedProgress(s);   // a progress record that waited for the ship is applied before any gate
        var c = s.Campaign;
        if (!s.KeyOf.TryGetValue(player, out ulong key)) { WfLog($"P{player} sent a Deploy before it identified: refused"); return; }
        if (s.Prep is not null || s.Go is not null) return;   // one deployment at a time; the client's resend finds it taken
        if (c.Phase != WarfrontPhase.Ship) { WfLog($"deploy from P{player} refused: the campaign is in {c.Phase}"); return; }
        if (c.Leader != key) { WfLog($"deploy from P{player} refused: only the leader deploys"); return; }
        if (!c.AvailableTargets().Contains(target)) { WfLog($"deploy from P{player} refused: {target.Kind} lane {target.Lane} is not an available target"); return; }
        string? refusal = serverFallback ? null : WarfrontCoopDeployGate();   // the dedicated server's own deploy after 90 s passes the gate (an absent player choosing cards must not hold the ship forever)   // the co-op rules: a joiner still choosing a class or picking cards holds the deployment
        if (refusal is not null) { WfLog($"deploy from P{player} refused: {refusal}"); s.GateRefusal = refusal; s.GateRefusalTick = World.Tick; return; }

        string map = WarfrontStagePlan.MapOf(c, target);
        bool landHere = WarfrontStagePlan.CanLandHere(map, s.Map, c.Faction, WarfrontRunFiles.StageFaction);   // Chaos Warfront C1 (wired at the series-37 integration): a stage built for the other army restages
        if (map == s.Map && !landHere) WfLog($"deploy {target.Kind} lane {target.Lane} on this map ({map}): this stage was built for {WarfrontRunFiles.StageFaction}, the run's army is {c.Faction}: restaging");
        if (landHere && World.Tfw is not null)
        {
            if (!c.TryDeploy(key, target, out string? error)) { WfLog($"deploy refused: {error}"); return; }
            WfSave(s);
            s.HubDirty = true;
            WfLog($"deploy {target.Kind} lane {target.Lane} on this map ({map}): landing at once, no restart");
            WarfrontLand(s, target);
            return;
        }
        var prep = new Prepare { Target = target, Map = map, Started = DateTime.UtcNow };
        if (int.TryParse(Environment.GetEnvironmentVariable("SM_WF_PREBUILD_DELAY"), out int delay) && delay > 0) prep.DelaySeconds = Math.Min(delay, 600);
        s.Prep = prep;
        WfLog($"deploy {target.Kind} lane {target.Lane}: preparing {map} while everyone stays connected (a cold build takes 100 to 180 s)");
        var config = World.Config;
        Task.Run(() =>
        {
            try
            {
                prep.Note = WarfrontPrebuild(map, config, (percent, status) =>
                {
                    prep.Percent = percent; prep.Status = status;
                    if (percent >= 30) prep.GraphStarted = DateTime.UtcNow;
                });
                for (int i = 0; i < prep.DelaySeconds; i++) Thread.Sleep(1000);
            }
            catch (Exception e) when (e is not OutOfMemoryException) { prep.Error = e.Message; }
            prep.Done = true;
        });
    }

    /// <summary>The preparation's progress: percent and a log line every 10 s; when it is done the deployment is saved (Travel) and the stage says Go.</summary>
    void WarfrontPrepareWatch(WarfrontServerState s)
    {
        if (s.Prep is not { } p) return;
        double elapsed = (DateTime.UtcNow - p.Started).TotalSeconds;
        if (p.Done)
        {
            s.Prep = null;
            if (p.Error is not null) { WfLog($"preparing {p.Map} failed: {p.Error}; staying on the ship"); return; }
            var c = s.Campaign;
            string? error = "no leader";
            if (c.Leader is not { } leader || !c.TryDeploy(leader, p.Target, out error)) { WfLog($"the deployment to {p.Map} is no longer valid ({error}); staying on the ship"); return; }
            WfLog($"{p.Note}; prepared in {elapsed:0.#} s");
            WfSave(s);
            s.HubDirty = true;
            WarfrontStartGo(s, p.Map, p.Target);
            return;
        }
        // an estimate once the graph starts (the work reports 0, 30, 100 only): rises toward 95 over an expected 150 s
        int percent = p.Percent;
        if (percent >= 30 && p.Percent < 100) percent = Math.Clamp(30 + (int)((DateTime.UtcNow - p.GraphStarted).TotalSeconds / 150.0 * 65.0), 30, 95);
        else if (percent < 30) percent = Math.Min(29, (int)(elapsed / 10.0 * 29.0));
        s.PrepPercent = percent;
        if ((DateTime.UtcNow - p.LastLog).TotalSeconds >= 10)
        {
            p.LastLog = DateTime.UtcNow;
            WfLog($"preparing {p.Map}: {percent} % after {elapsed:0} s ({p.Status})");
        }
    }

    // ---- stage change ------------------------------------------------------------------------------------------------------------------------------

    /// <summary>Stage = Go: remember who must rejoin, keep answering a moment, then ask the loop to leave with the next-stage exit code.</summary>
    void WarfrontStartGo(WarfrontServerState s, string map, WarfrontTarget? target)
    {
        WarfrontApplyQueuedProgress(s);
        WarfrontSendProgressSaveFinal(s);   // the last XP reaches the clients' files before this process leaves
        var expected = s.KeyOf.Values.ToList();
        WarfrontRunFiles.WriteStage(s.Dir, new(map, expected, WarfrontStagePlan.HasShip(s.Map) ? s.Map : WarfrontRunFiles.ReadStage(s.Dir)?.Ship));
        s.Go = new GoState { Map = map, Target = target, Tick = World.Tick };
        WfLog($"stage Go: {map} ({expected.Count} player(s) to rejoin); this process leaves in {WarfrontGoLingerSeconds} s");
    }

    void WarfrontGoWatch(WarfrontServerState s)
    {
        if (s.Go is { } go && !s.RestartDue && World.Tick - go.Tick >= (uint)(WarfrontGoLingerSeconds * WarfrontRate))
        {
            s.RestartDue = true;
            WfLog($"stage change: leaving with code {ServerCommandLine.WarfrontStageExit} for {go.Map}");
        }
    }

    // ---- arrival and landing -------------------------------------------------------------------------------------------------------------------------

    /// <summary>A stage that loaded a save in Travel waits until every expected player has rejoined and identified, or 20 s after the first; then it lands.</summary>
    void WarfrontArrivalStep(WarfrontServerState s)
    {
        if (!s.Arriving) return;
        var c = s.Campaign;
        int arrived = s.Expected.Count(k => s.KeyOf.ContainsValue(k));
        s.ArrivedCount = arrived;
        if (s.FirstIdentifiedTick is not { } first) return;
        bool all = s.Expected.Count == 0 || arrived >= s.Expected.Count;
        bool late = World.Tick - first >= (uint)(WarfrontArrivalSeconds * WarfrontRate);
        if (!all && !late) return;
        if (c.CurrentTarget is not { } target) { s.Arriving = false; return; }
        s.Arriving = false;
        s.ArrivedUntilTick = World.Tick + (uint)(3 * WarfrontRate);
        WfLog($"arrival: {arrived} of {s.Expected.Count} expected player(s) back{(all ? "" : " (the 20 s are over; the rest are flown in when they come)")}; landing");
        WarfrontLand(s, target);
    }

    /// <summary>The landing: the fronts are set so the battle is the target's node (one battle per process), then the run's own Deploy, by the leader.</summary>
    void WarfrontLand(WarfrontServerState s, WarfrontTarget target)
    {
        s.LandingTarget = target;
        s.Landing = true;
        s.LandingStartTick = s.LandingLastLog = World.Tick;
        s.LandingEnqueued = 0;
        WarfrontTryLand(s);
    }

    void WarfrontTryLand(WarfrontServerState s)
    {
        if (!s.Landing || s.LandingTarget is not { } target) return;
        var c = s.Campaign;
        if (World.Tfw is not { } run)
        {
            // no run on this map (a map that was not composed with the hub): the battle begins at once and only SM_WF_RESOLVE ends it.
            s.Landing = false;
            c.BeginBattle(); WfSave(s);
            WfLog($"battle begins on {s.Map} (deployment {c.Deployments + 1}); this map has no run: it ends under SM_WF_RESOLVE only");
            WarfrontArmResolve(s);
            return;
        }
        bool hq = target.Kind == WarfrontTargetKind.Hq && s.Map == WarfrontMaps.HqName;
        bool fortress = target.Kind == WarfrontTargetKind.Fortress && s.Map == WarfrontMaps.FortressName;
        var slot = WarfrontSlots.ByName(s.Map);
        if (!hq && !fortress && (target.Kind != WarfrontTargetKind.Node || slot is null)) { s.Landing = false; WfLog($"cannot land {target.Kind} on {s.Map}"); return; }
        if (run.Phase != TfwRunPhase.Hub)
        {
            // the run took the Deploy (Travel, then the battle): the landing is under way
            s.Landing = false;
            s.Landed = true;
            WfLog(hq ? $"landing on {s.Map}: the HQ defence; deployment {c.Deployments + 1}" : fortress ? $"landing on {s.Map}: the Ork fortress, through lane {(char)('A' + Math.Max(0, target.Lane))}; deployment {c.Deployments + 1}" : $"landing on {slot!.Name}: slot {slot.Number}, lane {slot.Lane}, node {slot.Node}, band {slot.Band} ({slot.Profile}), seed {slot.Seed}; deployment {c.Deployments + 1}; fronts set to node {slot.Node}");
            return;
        }
        // the run's Deploy is dropped silently unless the match is playing and the leader is alive inside the hub, so it is asked again each second until the run leaves the hub
        if (World.State != MatchState.Playing) { WarfrontLandingWait(s, $"the match is {World.State}"); return; }
        if (WarfrontLeaderPlayer() is not { } leader) { WarfrontLandingWait(s, "no identified leader"); return; }
        if (!World.Players.TryGetValue(leader, out var lp) || !lp.Alive || !run.Field.Hub.Contains(lp.Position)) { WarfrontLandingWait(s, "the leader is not alive aboard yet"); return; }
        if (s.LandingEnqueued != 0 && World.Tick - s.LandingEnqueued < (uint)WarfrontRate) return;
        if (run.Warfront is { } rules)
        {
            rules.Target = target.Kind;   // the run's battle plan reads it (the HQ defence's layout)
            run.Allies = SmSim.World.WarfrontAlliedPair(s.MaxHumans, !hq && !fortress);   // W13d part 3 (owner 2026-11-17, an interpretation): the two allied AI Marines only in a solo campaign (the cap is one); the Deploy below spawns them
            if (s.LandingEnqueued == 0) WfLog(run.Allies ? "allied pair: on (solo campaign, lane battle)" : "allied pair: off");
            rules.Fortress = fortress ? WarfrontFortressSetupFor(run) : null;   // the fortress's plan and sight test (W14b); only a fortress stage ever sets it
            if (rules.Fortress is { Sight: null }) WfLog($"FAULT: the fortress stage has no sight test (this map has no weapon collision): stage 2 sources stay closed to anyone in the plaza and no spawn is judged hidden");
        }
        if (hq)   // the HQ's stage sources use the same ray as the fortress's (W13c); none supplied: the 60 m stand-in, said once
        {
            if (run.Warfront is { } hqRules) hqRules.HqSight = WarfrontSightOverStage();
            WfLog(run.Warfront?.HqSight is null ? "HQ defence: no weapon collision on this stage, the stage sources use the 60 m sight stand-in" : "HQ defence: the stage sources are hidden by the eye-to-eye ray over this stage's weapon collision");
        }
        run.War.SetFrontsForTests(hq || fortress ? (sbyte)0 : (sbyte)(slot!.Node - 1));   // the HQ and the fortress: a node battle's frame on lane A; the plan hook replaces the layout
        if (!run.Enqueue(leader, new TfwCommandRecord(++s.CommandSeq, TfwCommand.Deploy, (byte)(hq || fortress ? 0 : slot!.Lane)))) { WarfrontLandingWait(s, "the run refused the Deploy"); return; }
        s.LandingEnqueued = World.Tick;
    }

    /// <summary>The landing could not start this tick: say so once a second, and after 60 s give the deployment up (back to the ship, nothing rolled).</summary>
    void WarfrontLandingWait(WarfrontServerState s, string why)
    {
        int rate = WarfrontRate;
        if (World.Tick - s.LandingLastLog >= (uint)rate) { s.LandingLastLog = World.Tick; WfLog($"landing waits: {why} ({(World.Tick - s.LandingStartTick) / rate} s)"); }
        if (World.Tick - s.LandingStartTick < (uint)(60 * rate)) return;
        s.Landing = false;
        WfLog($"landing given up after 60 s ({why}): back on the ship, nothing rolled");
        WarfrontRevertToShip(s);
    }

    /// <summary>The campaign back on the ship before this deployment (a Travel that cannot land): through its own save format, since the phase has no setter.</summary>
    void WarfrontRevertToShip(WarfrontServerState s)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(s.Campaign.ToJson())!.AsObject();
        node["Phase"] = "Ship";
        node["CurrentTarget"] = null;
        var reverted = WarfrontSave.FromJson(node.ToJsonString(), s.Campaign.RollSource);
        WarfrontSaveSquads(s);
        WarfrontSave.Write(s.Dir, reverted);   // the wiring and the progression are built once per process, so the stage restarts on the reverted run
        s.Leaving = true;
        WarfrontStartGo(s, s.Map, null);
    }

    /// <summary>When the run's battle has begun, the campaign's Travel becomes Battle and is saved (a kill now resumes on the ship, nothing rolled).</summary>
    void WarfrontBattleWatch(WarfrontServerState s)
    {
        if (s.Landing) WarfrontTryLand(s);
        var c = s.Campaign;
        if (s.Landed && c.Phase == WarfrontPhase.Travel && World.Tfw is { Phase: TfwRunPhase.Battle or TfwRunPhase.FinalAssault })
        {
            s.Landed = false;
            c.BeginBattle();
            WfSave(s);
            if (WarfrontStagePlan.HasShip(s.Map)) WarfrontRunFiles.WriteStage(s.Dir, new(s.Map, [], s.Map));   // the ship of this stage: a kill in this battle resumes on it
            s.HubDirty = true;
            WfLog($"battle begins (deployment {c.Deployments + 1}, {c.CurrentTarget?.Kind} lane {c.CurrentTarget?.Lane}); saved");
            WarfrontArmResolve(s);
        }
    }

    // ---- the battle's end ----------------------------------------------------------------------------------------------------------------------------

    void WarfrontArmResolve(WarfrontServerState s)
    {
        if (s.ResolveAid) s.ResolveAtTick = World.Tick + (uint)(5 * WarfrontRate);
    }

    /// <summary>SM_WF_RESOLVE: the battle under way ends that way 5 s after it began; on a map with no battle rules it is the only way out.</summary>
    void WarfrontResolveAid(WarfrontServerState s)
    {
        if (s.ResolveAtTick is not { } at || World.Tick < at) return;
        s.ResolveAtTick = null;
        WfLog($"test aid SM_WF_RESOLVE: the battle ends as a {(s.ResolveWins ? "win" : "wipe")}");
        s.ForcedWin = s.ResolveWins;
        if (World.Tfw is { } run && run.Battle is not null) run.PickupForTests(World);   // leaves the battle as a win; the forced outcome overrides what the callback is told
        else WarfrontNodeEnded(default, 0, TfwMapResult.Won);
    }

    /// <summary>The run's NodeEnded seam: win or wipe resolve the campaign; every human gone is no result (back to the ship, nothing rolled).</summary>
    void WarfrontNodeEnded(TfwLane lane, int node, TfwMapResult result)
    {
        if (_wf is not { } s) return;
        var c = s.Campaign;
        if (c.Phase != WarfrontPhase.Battle) return;
        bool? forced = s.ForcedWin; s.ForcedWin = null;
        s.ResolveAtTick = null;
        if (forced is null && result == TfwMapResult.Left && (s.KeyOf.Count == 0 || s.KeyOf.Keys.All(id => !World.Players.ContainsKey(id))))   // the last human may have dropped this very tick: the roster below is synced after the step
        {
            c.ResumeAfterRestart();
            WfSave(s);
            WfLog("the battle was left with nobody connected: no result, back on the ship, nothing rolled");
            return;
        }
        var outcome = (forced ?? result == TfwMapResult.Won) ? WarfrontOutcome.Win : WarfrontOutcome.Wipe;
        int dep = c.Deployments + 1;
        c.Resolve(outcome);
        string rolls = string.Join(", ", c.Rolls.Where(r => r.Deployment == dep).Select(r => $"lane {(char)('A' + r.Lane)} {(r.Lost ? (r.HeldAtFloor ? "lost, held at the floor" : "lost") : "held")} ({r.Before}->{r.After})"));
        WfLog($"battle {dep} ended: {outcome}; fronts {c.Front(0)}/{c.Front(1)}/{c.Front(2)}; rolls: {(rolls.Length == 0 ? "none" : rolls)}; phase {c.Phase}{(c.HqMandatory ? "; HQ defense is mandatory" : "")}");
        WfSave(s);
        s.HubDirty = true;
        if (c.IsOver) { WarfrontFinishRun(s); return; }
        WarfrontCoopRules?.ShipStop();
        if (!WarfrontStagePlan.HasShip(s.Map))
        {
            string ship = WarfrontRunFiles.ReadStage(s.Dir)?.Ship is { } last && WarfrontStagePlan.HasShip(last) ? last : WarfrontStagePlan.FirstMap;
            WfLog($"{s.Map} has no ship: back to {ship}");
            WarfrontStartGo(s, ship, null);
        }
    }

    /// <summary>The fortress's setup (W14b): the plan of <see cref="WarfrontSlots.Fortress"/> and a sight test over this stage's weapon collision (a straight eye-to-eye line that no wall stops). The stage's map is wf_fortress, one battle per process.</summary>
    WarfrontFortressSetup WarfrontFortressSetupFor(TfwRun run)
    {
        var setup = WarfrontFortressSetup.For(WarfrontSlots.Fortress);
        setup.Sight = WarfrontSightOverStage();
        return setup;
    }

    /// <summary>A straight eye-to-eye line that no wall of this stage's weapon collision stops (the fortress's and, since W13c, the HQ's sight test); null without a collision.</summary>
    Func<Vector3, Vector3, bool>? WarfrontSightOverStage()
    {
        var weapons = Map?.Collision.Weapons;
        if (weapons is null) return null;
        return (from, to) =>
        {
            var d = to - from;
            float len = d.Length();
            return len < 0.01f || weapons.Raycast(from, d / len, len) is null;
        };
    }
}
