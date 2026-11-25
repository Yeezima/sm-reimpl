using System.Numerics;
using SmSim.Extras.Executions;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmSim;

/// <summary>Why Warfront refused an Execute press before any choice (X4; the server's log and the tests). X3 records each as <see cref="ExecutionRefusal.Refused"/>.</summary>
public enum ExecutionWarfrontRefusal : byte
{
    /// <summary>Within <see cref="ExecutionRules.WarfrontCooldownSeconds"/> of the player's last successful execution.</summary>
    Cooldown = 1,
    /// <summary>Not in a battle (the hub, the travel, the pickup, a run that ended, or no war at all).</summary>
    NotInBattle = 2,
    /// <summary>A Chaos stage before X4b (its alias slots would play Ork rows on another rig; the plan's cross-cutting decision).</summary>
    ChaosStage = 3,
}

/// <summary>A Warfront execution's reward (X4): player <see cref="Player"/> set to <see cref="Health"/> and <see cref="Shield"/> at <see cref="Tick"/>, ready again at <see cref="ReadyAt"/>.</summary>
public readonly record struct ExecutionWarfrontReward(int Player, ushort Ork, float Health, float Shield, uint Tick, uint ReadyAt);

/// <summary>
/// Executions X4, Warfront on Orks (owner-approved brief 2026-11-24, answers 2026-11-25; design state/design-executions-1125.md "Warfront"; the plan's X4 section). A human in a Warfront battle
/// taps Execute near a regular Ork at any health: X3's state machine (<c>World.Executions.cs</c>) asks this file for the target (<see cref="ExecutionsWarfrontPick"/>), and this file holds the Ork out of
/// the fight (<c>MusouSim.Executions.cs</c>), makes the executor take no damage during the move, kills the Ork at the kill frame through the normal damage path, and on a completed move with its
/// kill gives full health, full armour and a one-bar overshield (<see cref="ExecutionRules.RewardShield"/>) and starts the 10 s cooldown. Everything returns at once without
/// <see cref="ContentExtras.Warfront"/> (X3 calls the Warfront partials only under it; the damage hook and the refusals check it here), so Classic PvP Extended and Musou Mode are untouched.
/// <list type="bullet">
/// <item>Executors: X3 refuses the dead (the downed and the spectators are dead), retainers and AI Marines (<see cref="TfwContracts.IsPuppet"/>), the airborne and the reacting. A held co-op
/// joiner and a protected player have their input locked by Warfront (<c>WarfrontInputLocked</c>, <c>MatchFlow.Locked</c>: no buttons), so their press never reaches the state machine.</item>
/// <item>Targets: <see cref="ExecutionChoice.ChooseAlignedOrk"/> over the Orks <see cref="MusouSim.Query"/> finds within <see cref="ExecutionsGatherRadius"/> (never a scan of the pool), with the
/// live runs' Orks reserved and the bystanders of <see cref="ExecutionBystanders"/> (other living players and the player-solid big Orks only).</item>
/// <item>The cooldown table is cleared by the player's death and never saved; the overshield is the armour above the maximum, kept by <see cref="SimConfig.KeepsOvershield"/>.</item>
/// </list>
/// </summary>
public sealed partial class World
{
    /// <summary>The candidates are gathered within this distance of the executor (metres, horizontal; ours): <see cref="ExecutionChoice.WarfrontRange"/> plus a 0.5 m margin.</summary>
    public const float ExecutionsGatherRadius = ExecutionChoice.WarfrontRange + 0.5f;
    /// <summary>The most candidates and the most nearby enemies a press reads (ours: the nearest first; a crowd beyond them is too far to matter for a 1 m slide).</summary>
    public const int ExecutionsCandidateMost = 64, ExecutionsBystanderEnemyMost = 256;

    readonly Dictionary<int, (uint ReadyAt, int Deaths)> _executionReadyAt = new();
    readonly Dictionary<int, (uint Tick, ExecutionWarfrontRefusal Why)> _executionWarfrontRefused = new();
    readonly List<ExecutionWarfrontReward> _executionRewards = new();
    uint _executionRewardsTick;
    ushort[]? _executionQuery, _executionNearby;

    /// <summary>Test aid: how many candidate gathers (<see cref="MusouSim.Query"/> calls) the presses made.</summary>
    public int ExecutionsGathers { get; private set; }

    /// <summary>Test aid: treat the stage as a Chaos one without a Chaos config. Never set outside tests.</summary>
    internal bool ExecutionsChaosStageForTests { get; set; }

    /// <summary>Whether the stage's enemies are Chaos (no executions until X4b): the stage's army (Chaos C2's <c>SimConfig.WarfrontFaction</c>, read by Warfront's <see cref="WarfrontChaosArmy"/>; X4 phase 2, wired at the series-37 integration).</summary>
    bool ExecutionsChaosStage
    {
        get
        {
            bool chaos = ExecutionsChaosStageForTests;
            WarfrontChaosArmy(ref chaos);   // WARFRONT-HOOK: the stage's army (Extras/Warfront/Chaos/Roster/World.WarfrontChaosArmy.cs); without Warfront compiled, never Chaos
            return chaos;
        }
    }

    /// <summary>Hook for Warfront (Chaos C2): sets <paramref name="chaos"/> when this stage's army is Chaos.</summary>
    partial void WarfrontChaosArmy(ref bool chaos);

    /// <summary>Whether the war is in a battle (node, HQ or fortress) with its enemies in play.</summary>
    bool ExecutionsInBattle => _musou is not null && _tfw is { Battle: not null } run && run.Phase is TfwRunPhase.Battle or TfwRunPhase.FinalAssault or TfwRunPhase.Pickup;   // the fight goes on through the extraction countdown (X4 review)

    /// <summary>The tick player <paramref name="id"/> may execute again (0: now; cleared by its death).</summary>
    public uint ExecutionsReadyAt(int id) => _executionReadyAt.TryGetValue(id, out var r) && !ExecutionsDiedSince(id, r.Deaths) ? r.ReadyAt : 0u;

    /// <summary>Why Warfront refused player <paramref name="id"/>'s press in the last step, or null.</summary>
    public ExecutionWarfrontRefusal? ExecutionsWarfrontRefusedLast(int id) => _executionWarfrontRefused.TryGetValue(id, out var r) && r.Tick == Tick ? r.Why : null;

    /// <summary>The Warfront rewards given in the last step (the server's log, the tests).</summary>
    public IReadOnlyList<ExecutionWarfrontReward> ExecutionsRewardedThisTick => _executionRewardsTick == Tick ? _executionRewards : [];

    /// <summary>Test seam: sets player <paramref name="id"/>'s health and armour (a hit taken earlier, an overshield to test).</summary>
    internal void ExecutionsSetVitalsForTests(int id, float health, float shield)
    {
        if (_players.TryGetValue(id, out var p)) _players[id] = p with { Health = health, Shield = shield };
    }

    bool ExecutionsDiedSince(int id, int deaths) => Scores.TryGetValue(id, out var s) && s.Deaths != deaths;

    int ExecutionsDeathsOf(int id) => Scores.TryGetValue(id, out var s) ? s.Deaths : 0;

    // ---- X3's partials ----

    partial void ExecutionsRefused(int id, ref bool refused)
    {
        if (!Config.Extras.HasFlag(ContentExtras.Warfront)) return;
        ExecutionWarfrontRefusal? why = null;
        if (!ExecutionsInBattle) why = ExecutionWarfrontRefusal.NotInBattle;
        else if (ExecutionsChaosStage) why = ExecutionWarfrontRefusal.ChaosStage;
        else if (_executionReadyAt.TryGetValue(id, out var r))
        {
            if (ExecutionsDiedSince(id, r.Deaths)) _executionReadyAt.Remove(id);   // death clears the cooldown (owner: lost on death)
            else if (Tick < r.ReadyAt) why = ExecutionWarfrontRefusal.Cooldown;
            else _executionReadyAt.Remove(id);
        }
        if (why is not { } w) return;
        refused = true;
        _executionWarfrontRefused[id] = (Tick, w);
    }

    /// <summary>
    /// Death clears the cooldown (owner: lost on death): every step, a player that is dead, gone or respawned in this step loses its entry (the death count check at the press stays as a second
    /// guard). Warfront's landing-zone respawn and the revive happen inside the step, so they are seen here in the same step; a round's spawn (<c>BeginRound</c>) runs outside it and is seen in
    /// the next step (the player dead before it, or the death count). Nothing to do while no cooldown runs.
    /// </summary>
    partial void ExecutionsWarfrontStep()
    {
        if (_executionReadyAt.Count == 0) return;
        foreach (int id in _executionReadyAt.Keys.ToArray())
            if (!_players.TryGetValue(id, out var p) || !p.Alive || LastRespawns.Contains(id)) _executionReadyAt.Remove(id);
    }

    partial void ExecutionsWarfrontPick(int id, in PlayerState executor, uint pressTick, ref ExecutionPick? pick, ref bool handled)
    {
        handled = true;
        if (!ExecutionsInBattle || ExecutionsChaosStage) return;
        var (target, result, _) = ExecutionsWarfrontChoose(id, executor, prompt: false);
        if (target == 0)
        {
            // An eligible Ork was ranked but none of its moves fitted (moves were tried): X3 records NoFit (a failed result); no eligible Ork at all: NoTarget (pick null).
            if (result.Tried.Count > 0) pick = new ExecutionPick(ExecutionTargetKind.Enemy, 0, result, executor.Position, executor.Yaw, -1, Team.None);
            return;
        }
        pick = new ExecutionPick(ExecutionTargetKind.Enemy, target, result, result.VictimAt, result.VictimYaw, -1, Team.None);
    }

    partial void ExecutionsWarfrontBegin(in ExecutionRun run)
    {
        if (run.TargetKind == ExecutionTargetKind.Enemy) _musou?.ExecutionsHold((ushort)run.Target, run.VictimYaw);
    }

    partial void ExecutionsWarfrontValid(in ExecutionRun run, ref bool valid)
    {
        if (run.TargetKind != ExecutionTargetKind.Enemy) { valid = false; return; }
        if (run.KillDone) return;   // the body is the Ork's victim half from here; its slot may be freed after its dying time
        valid = _musou is { } musou && musou.TryGet((ushort)run.Target, out var e) && e.Alive && musou.ExecutionsIsHeld((ushort)run.Target);
    }

    partial void ExecutionsWarfrontKill(ref ExecutionRun run)
    {
        bool killed = false;
        if (run.TargetKind == ExecutionTargetKind.Enemy) _musou?.ExecutionsKill((ushort)run.Target, run.Executor, out killed);
        run = run with { KillDone = killed };
    }

    partial void ExecutionsWarfrontEnded(in ExecutionRun run, ExecutionEndReason why)
    {
        if (run.TargetKind == ExecutionTargetKind.Enemy) _musou?.ExecutionsRelease((ushort)run.Target);
        if (why != ExecutionEndReason.Done || !run.KillDone) return;   // an interrupted move (or a death) gives nothing and starts no cooldown
        int id = run.Executor;
        if (!_players.TryGetValue(id, out var p) || !p.Alive) return;
        var body = Loadouts.Body(Config, p);
        float shield = ExecutionRules.RewardShield(body.ShieldMax);   // exactly the cap: a refresh never stacks
        _players[id] = p with { Health = body.HealthMax, Shield = shield };
        uint ready = Tick + (uint)Math.Max(0, ExecutionRules.CooldownTicks(Config));
        _executionReadyAt[id] = (ready, ExecutionsDeathsOf(id));
        if (_executionRewardsTick != Tick) { _executionRewards.Clear(); _executionRewardsTick = Tick; }
        _executionRewards.Add(new ExecutionWarfrontReward(id, (ushort)run.Target, body.HealthMax, shield, Tick, ready));
    }

    // ---- X1's damage hook (World.ExecutionsHooks.cs) ----

    partial void ExecutionsDamageTaken(int target, ref float scale)
    {
        if (Config.Extras.HasFlag(ContentExtras.Warfront) && _executionRuns.ContainsKey(target)) scale = 0f;   // Warfront: invulnerable during the move only; Classic unchanged
    }

    // ---- the choice ----

    /// <summary>
    /// The Ork player <paramref name="id"/> would execute now and the move (<see cref="ExecutionChoice.ChooseAlignedOrk"/>): (0, a failed result) when none fits; <c>Candidates</c>, how many Orks
    /// the gather found. <paramref name="prompt"/>: the HUD prompt's mode (the first move that fits, no seeded pick); the Ork is the same as the press's. Nothing is changed.
    /// </summary>
    public (ushort Id, ExecutionAlignResult Result, int Candidates) ExecutionsWarfrontChoose(int id, in PlayerState executor, bool prompt)
    {
        if (_musou is not { } musou) return (0, ExecutionAlign.Align(executor, executor, [], Config, Collision), 0);
        var query = _executionQuery ??= new ushort[ExecutionsCandidateMost];
        int n = musou.Query(executor.Position, ExecutionsGatherRadius, query, Config.MeleeLock.Vertical);
        ExecutionsGathers++;
        var enemies = new List<MusouEnemy>(n);
        for (int k = 0; k < n; k++) if (musou.TryGet(query[k], out var e)) enemies.Add(e);
        HashSet<ushort>? reserved = null;
        foreach (var run in _executionRuns.Values)
            if (run.TargetKind == ExecutionTargetKind.Enemy && run.Executor != id) (reserved ??= []).Add((ushort)run.Target);
        var bystanders = ExecutionsWarfrontBystanders(id, executor.Position, musou);
        uint tick = Tick;
        var (target, result) = ExecutionChoice.ChooseAlignedOrk(executor, executor.Yaw, ExecutionsClassName(executor), ExecutionsMeleeWargear(executor) ?? "", enemies, Config.ExecutionRows,
            Config, Collision, reserved, prompt ? null : ork => ExecutionChoice.Seed(tick, id, ork), bystanders);
        return (target, result, enemies.Count);
    }

    /// <summary>The bystanders of a Warfront press by <paramref name="executor"/> at <paramref name="at"/> (<see cref="ExecutionBystanders.Warfront"/>).</summary>
    List<Vector3> ExecutionsWarfrontBystanders(int executor, Vector3 at, MusouSim musou)
    {
        float reach = ExecutionsBystanderReach;
        var nearby = _executionNearby ??= new ushort[ExecutionsBystanderEnemyMost];
        int n = musou.Query(at, reach, nearby, Config.MeleeLock.Vertical + 2f);
        var big = new List<(MusouEnemyType, Vector3)>();
        for (int k = 0; k < n; k++)
            if (musou.TryGet(nearby[k], out var e) && ExecutionBystanders.Counts(e.Type)) big.Add((e.Type, e.Position));
        var players = new List<(int, PlayerState)>();
        foreach (var (pid, p) in _players.OrderBy(kv => kv.Key)) players.Add((pid, p));
        return ExecutionBystanders.Warfront(executor, players, big, at, reach);
    }
}
