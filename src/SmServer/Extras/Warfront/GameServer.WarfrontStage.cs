using SmContent.Extras.Warfront;
using SmSim;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront;
using SmSim.Extras.Warfront.Campaign;
using SmSim.Extras.Warfront.Maps;

namespace SmServer;

/// <summary>Warfront's stage flow on the server (slice W5a): the deployment, the preparation off the tick thread, the stage change, the arrival, the landing, the battle's end.</summary>
public sealed partial class GameServer
{
    // ---- deploy ------------------------------------------------------------------------------------------------------------------------------------

    /// <summary>The leader's Deploy (plan section 3): only from the leader, only on the ship, only a listed target. Same map: land at once; another map: prepare it, then Go.</summary>
    void WarfrontDeploy(int player, WarfrontTarget target)
    {
        if (_wf is not { } s) return;
        var c = s.Campaign;
        if (!s.KeyOf.TryGetValue(player, out ulong key)) { WfLog($"P{player} sent a Deploy before it identified: refused"); return; }
        if (s.Prep is not null || s.Go is not null) return;   // one deployment at a time; the client's resend finds it taken
        if (c.Phase != WarfrontPhase.Ship) { WfLog($"deploy from P{player} refused: the campaign is in {c.Phase}"); return; }
        if (c.Leader != key) { WfLog($"deploy from P{player} refused: only the leader deploys"); return; }
        if (!c.AvailableTargets().Contains(target)) { WfLog($"deploy from P{player} refused: {target.Kind} lane {target.Lane} is not an available target"); return; }
        string? refusal = null;
        WarfrontDeployGate(ref refusal);
        if (refusal is not null) { WfLog($"deploy from P{player} refused: {refusal}"); s.GateRefusal = refusal; s.GateRefusalTick = World.Tick; return; }

        string map = WarfrontStagePlan.MapOf(c, target);
        if (map == s.Map && World.Tfw is not null)
        {
            if (!c.TryDeploy(key, target, out string? error)) { WfLog($"deploy refused: {error}"); return; }
            WarfrontSave.Write(s.Dir, c);
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
            WarfrontSave.Write(s.Dir, c);
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
        s.LandingNoticeTick = World.Tick;
        WarfrontTryLand(s);
    }

    void WarfrontTryLand(WarfrontServerState s)
    {
        if (!s.Landing || s.LandingTarget is not { } target) return;
        var c = s.Campaign;
        if (World.Tfw is not { } run)
        {
            // HQ and fortress: no hub and no battle rules before W13b and W14b; the battle begins at once and only SM_WF_RESOLVE ends it.
            s.Landing = false;
            c.BeginBattle(); WarfrontSave.Write(s.Dir, c);
            WfLog($"battle begins on {s.Map} (deployment {c.Deployments + 1}); this map has no battle rules yet: it ends under SM_WF_RESOLVE only");
            WarfrontArmResolve(s);
            return;
        }
        if (target.Kind != WarfrontTargetKind.Node || WarfrontSlots.ByName(s.Map) is not { } slot) { s.Landing = false; WfLog($"cannot land {target.Kind} on {s.Map}"); return; }
        if (WarfrontLeaderPlayer() is not { } leader) return;   // nobody identified yet: try again next tick
        run.War.SetFrontsForTests((sbyte)(slot.Node - 1));
        if (!run.Enqueue(leader, new TfwCommandRecord(++s.CommandSeq, TfwCommand.Deploy, (byte)slot.Lane))) return;
        s.Landing = false;
        s.Landed = true;
        WfLog($"landing on {slot.Name}: slot {slot.Number}, lane {slot.Lane}, node {slot.Node}, band {slot.Band} ({slot.Profile}), seed {slot.Seed}; deployment {c.Deployments + 1}; fronts set to node {slot.Node}");
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
            WarfrontSave.Write(s.Dir, c);
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
        if (forced is null && result == TfwMapResult.Left && s.KeyOf.Count == 0)
        {
            c.ResumeAfterRestart();
            WarfrontSave.Write(s.Dir, c);
            WfLog("the battle was left with nobody connected: no result, back on the ship, nothing rolled");
            return;
        }
        var outcome = (forced ?? result == TfwMapResult.Won) ? WarfrontOutcome.Win : WarfrontOutcome.Wipe;
        int dep = c.Deployments + 1;
        c.Resolve(outcome);
        string rolls = string.Join(", ", c.Rolls.Where(r => r.Deployment == dep).Select(r => $"lane {(char)('A' + r.Lane)} {(r.Lost ? (r.HeldAtFloor ? "lost, held at the floor" : "lost") : "held")} ({r.Before}->{r.After})"));
        WfLog($"battle {dep} ended: {outcome}; fronts {c.Front(0)}/{c.Front(1)}/{c.Front(2)}; rolls: {(rolls.Length == 0 ? "none" : rolls)}; phase {c.Phase}{(c.HqMandatory ? "; HQ defense is mandatory" : "")}");
        WarfrontSave.Write(s.Dir, c);
        s.HubDirty = true;
        if (c.IsOver) { WarfrontFinishRun(s); return; }
        WarfrontCoopShipStop();
        if (!WarfrontStagePlan.HasShip(s.Map))
        {
            string ship = WarfrontRunFiles.ReadStage(s.Dir)?.Ship is { } last && WarfrontStagePlan.HasShip(last) ? last : WarfrontStagePlan.FirstMap;
            WfLog($"{s.Map} has no ship: back to {ship}");
            WarfrontStartGo(s, ship, null);
        }
    }
}
