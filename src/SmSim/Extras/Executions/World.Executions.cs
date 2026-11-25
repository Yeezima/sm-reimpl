using System.Numerics;
using SmSim.Extras.Executions;
using SmSim.Extras.Musou.Campaign;

namespace SmSim;

/// <summary>Why a press of Execute did nothing (X3; the server's log and the tests: a refusal is recorded before any choice is made).</summary>
public enum ExecutionRefusal : byte
{
    /// <summary>The match has no rows at all (an install without the clips).</summary>
    NoRows = 1,
    Dead = 2,
    /// <summary>A retainer or an AI Marine (<see cref="TfwContracts.IsPuppet"/>): never an executor (owner).</summary>
    Puppet = 3,
    /// <summary>Already executing.</summary>
    Locked = 4,
    /// <summary>In a jump, a fall or a hop (review note 4).</summary>
    Airborne = 5,
    /// <summary>In an interrupt, a stun or a stagger (lead decision 8); never for a flinch (lead 40).</summary>
    Reaction = 6,
    /// <summary>Mid-charge, mid-evade or in a jump-pack phase.</summary>
    Moving = 7,
    /// <summary>The melee weapon has no execution family (a kick, a claw, a spear; <see cref="ExecutionWeapons.FamilyOf"/>).</summary>
    NoFamily = 8,
    /// <summary>An Ork player (<see cref="ExecutionTargets.CanExecute"/>).</summary>
    Race = 9,
    /// <summary>The match is counting down or has ended.</summary>
    NotInPlay = 10,
    /// <summary>Another feature refused it (X4: the Warfront cooldown or a retainer rule).</summary>
    Refused = 11,
    /// <summary>Classic: no open window of this player's lethal melee kill at the press's tick.</summary>
    NoOffer = 12,
    /// <summary>The choice found no move that aligns (the normal kill stands; Classic: the offer is spent).</summary>
    NoFit = 13,
    /// <summary>The chosen row has no wire index (it is not in the wire order).</summary>
    NoWire = 14,
    /// <summary>Warfront: no Ork picked (or Warfront's pick is not in yet: X4).</summary>
    NoTarget = 15,
}

/// <summary>
/// Executions X3, the state machine (design state/design-executions-1125.md "Simulation"; the plan's X3 section): the Execute press, the refusals, the start, the lock, the pinned position,
/// the kill and end events and every end. Classic PvP Extended's offers and press are in <c>World.ExecutionsClassic.cs</c>; Warfront fills the partials declared here (X4). Everything is reached
/// only through X1's gated hooks (<c>World.ExecutionsHooks.cs</c>), so nothing here runs without <see cref="ContentExtras.Executions"/>.
/// <list type="bullet">
/// <item>Tick order: <see cref="ExecutionsInput"/> (each player, before its step: the press edge and the lock), <see cref="ExecutionsPin"/> (each player, after its step), then
/// <see cref="ExecutionsStep"/> once after the deaths and pickups: this tick's offers, the queued presses (a run started here is pinned from the next tick, <c>StartTick + 1</c>), the checks
/// and events of the runs started before this tick.</item>
/// <item>A run is locked for the ticks <c>StartTick + 1</c> to its end tick (inclusive: the end tick is pinned, then the run ends in that tick's step; the player is free the tick after).</item>
/// </list>
/// </summary>
public sealed partial class World
{
    readonly SortedDictionary<int, ExecutionRun> _executionRuns = new();
    readonly Dictionary<int, int> _executionRunRound = new();
    readonly List<ExecutionRun> _executionStarts = new();
    readonly List<ExecutionEnded> _executionEnds = new(), _executionEndsPending = new();
    readonly List<(int Id, ExecutionRefusal Why)> _executionRefusals = new();
    readonly Dictionary<int, bool> _executionPress = new();
    readonly List<(int Id, uint PressTick)> _executionRequests = new();

    /// <summary>The executions in progress, by executor id.</summary>
    public IReadOnlyCollection<ExecutionRun> ExecutionsLive => _executionRuns.Values;
    /// <summary>The executions started in the last step (the server sends a Start for each).</summary>
    public IReadOnlyList<ExecutionRun> ExecutionsStartedThisTick => _executionStarts;
    /// <summary>The executions that ended in the last step, and those aborted since the step before it (the server sends an End for each).</summary>
    public IReadOnlyList<ExecutionEnded> ExecutionsEndedThisTick => _executionEnds;
    /// <summary>The presses of the last step that did nothing, and why (the server's log, the tests).</summary>
    public IReadOnlyList<(int Id, ExecutionRefusal Why)> ExecutionsRefusedThisTick => _executionRefusals;

    /// <summary>Whether player <paramref name="id"/> is executing (its input locked, its position pinned).</summary>
    public bool ExecutionsLocked(int id) => _executionRuns.ContainsKey(id);

    /// <summary>The run of executor <paramref name="id"/>, or null.</summary>
    public ExecutionRun? ExecutionOf(int id) => _executionRuns.TryGetValue(id, out var run) ? run : null;

    /// <summary>Ends player <paramref name="id"/>'s run with <paramref name="why"/> at once (no-op without one). Safe outside the step: the End is reported by the next step.</summary>
    public void ExecutionsAbort(int id, ExecutionEndReason why)
    {
        if (!_executionRuns.TryGetValue(id, out var run)) return;
        EndExecution(run, why, _executionEndsPending);
    }

    /// <summary>Ends every run with <paramref name="why"/> (Warfront C5's duel, a map change). Safe outside the step.</summary>
    public void ExecutionsAbortAll(ExecutionEndReason why)
    {
        foreach (var run in _executionRuns.Values.ToArray()) EndExecution(run, why, _executionEndsPending);
    }

    /// <summary>Test seam: starts <paramref name="run"/> (quantized) as if a press had chosen it in this tick's step; the Start is reported by the next step.</summary>
    public void ExecutionsStartForTests(in ExecutionRun run)
    {
        var q = ExecutionTrack.Quantize(run);
        _executionRuns[q.Executor] = q;
        _executionRunRound[q.Executor] = RoundsBegun;
        _pendingTestStarts.Add(q);
    }
    readonly List<ExecutionRun> _pendingTestStarts = new();

    /// <summary>Test seam: puts player <paramref name="id"/> at <paramref name="at"/> facing <paramref name="yaw"/> (a teleport, as a dev tool or a respawn would).</summary>
    public void ExecutionsPlaceForTests(int id, Vector3 at, float yaw)
    {
        if (_players.TryGetValue(id, out var p)) _players[id] = p with { Position = at, Yaw = yaw, Velocity = Vector3.Zero };
    }

    // ---- X1's hooks (World.ExecutionsHooks.cs, gated on the flag) ----

    partial void ExecutionsInput(int id, ref PlayerInput input)
    {
        bool down = (input.Buttons & InputButtons.Execute) != 0;
        bool was = _executionPress.GetValueOrDefault(id);
        _executionPress[id] = down;
        bool locked = _executionRuns.ContainsKey(id);
        if (down && !was) _executionRequests.Add((id, ExecutionRules.PressTick(Config, Tick, input.ViewTick)));   // a 0-to-1 edge; a held press never counts again
        if (locked) input = MatchFlow.Locked(input);   // no stick and no buttons (Execute among them); the view's yaw and pitch kept
    }

    partial void ExecutionsPin(int id, PlayerState before, ref PlayerState next)
    {
        if (!next.Alive || !_executionRuns.TryGetValue(id, out var run) || run.StartTick >= Tick) return;
        if (Config.Executions.AtWire(run.Wire) is not { } row) return;   // the step ends it Invalid
        // Moved off last tick's pin between ticks (a respawn, a round reset, a developer tool): left where it is, so the step ends the run Invalid.
        if (Vector3.Distance(before.Position, ExecutionTrack.Pose(run, row, Tick - 1, Config.TickRate).Feet) > PinTolerance) return;
        next = ExecutionTrack.Pin(next, run, row, Tick, Config.TickRate);
    }

    partial void ExecutionsReactionBlocked(int target, ref bool blocked)
    {
        if (_executionRuns.ContainsKey(target)) blocked = true;   // a nonlethal hit does not interrupt; a lethal one still kills (ResolveDeaths) and the step ends the run Killed
    }

    partial void ExecutionsStep()
    {
        _executionStarts.Clear();
        _executionEnds.Clear();
        _executionRefusals.Clear();
        _executionNoFits.Clear();
        _executionEnds.AddRange(_executionEndsPending);
        _executionEndsPending.Clear();
        _executionStarts.AddRange(_pendingTestStarts);
        _pendingTestStarts.Clear();

        ExecutionsClassicOffers();   // this tick's hostile melee kills (Classic only; LastKills is this tick's, ResolveDeaths has run)

        // The presses of this tick, in player order.
        if (_executionRequests.Count > 0)
        {
            _executionRequests.Sort((a, b) => a.Id.CompareTo(b.Id));
            foreach (var (id, pressTick) in _executionRequests) ExecutionsPress(id, pressTick);
            _executionRequests.Clear();
        }

        // The runs started before this tick: their checks, then their events.
        if (_executionRuns.Count > 0)
            foreach (var run in _executionRuns.Values.ToArray())
                if (run.StartTick < Tick) CheckExecution(run);

        // Forget the press latch of players who left.
        if (_executionPress.Count > _players.Count)
            foreach (int gone in _executionPress.Keys.Where(k => !_players.ContainsKey(k)).ToArray()) _executionPress.Remove(gone);
        ExecutionsClassicExpire();
    }

    // ---- the press ----

    void ExecutionsPress(int id, uint pressTick)
    {
        var why = ExecutionsRefusal(id);
        if (why is { } refused) { _executionRefusals.Add((id, refused)); return; }
        var executor = _players[id];
        ExecutionPick? pick = null;
        if (Config.Extras.HasFlag(ContentExtras.Warfront))
        {
            bool handled = false;
            ExecutionsWarfrontPick(id, executor, pressTick, ref pick, ref handled);
            if (!handled || pick is null) { _executionRefusals.Add((id, ExecutionRefusal.NoTarget)); return; }   // Warfront: "nothing happens"
        }
        else if (!ExecutionsClassicPick(id, executor, pressTick, out pick)) return;   // the refusal is recorded there
        var p = pick!.Value;
        if (!p.Result.Ok || p.Result.Row is not { } row) { _executionRefusals.Add((id, ExecutionRefusal.NoFit)); return; }
        int wire = Config.Executions.WireIndexOf(row.Id);
        if (wire < 0) { _executionRefusals.Add((id, ExecutionRefusal.NoWire)); return; }
        StartExecution(new ExecutionRun(id, p.Kind, p.Target, wire, p.Result.Distance, executor.Position, p.Result.Attacker.Position, p.Result.Attacker.Yaw,
            p.Result.SlideTicks, Tick, p.Result.RaceSwapped)
        {
            VictimAt = p.VictimAt, VictimYaw = p.VictimYaw, VictimClass = p.VictimClass, VictimTeam = p.VictimTeam,
        });
    }

    /// <summary>The refusals before any choice, cheap first (the plan's order); null: the press may choose.</summary>
    ExecutionRefusal? ExecutionsRefusal(int id)
    {
        if (Config.Executions.Rows.Count == 0) return ExecutionRefusal.NoRows;
        if (!_players.TryGetValue(id, out var s) || !s.Alive) return ExecutionRefusal.Dead;
        if (TfwContracts.IsPuppet(id)) return ExecutionRefusal.Puppet;
        if (_executionRuns.ContainsKey(id)) return ExecutionRefusal.Locked;
        if (!s.Grounded) return ExecutionRefusal.Airborne;
        if (s.Reaction != ReactionKind.None) return ExecutionRefusal.Reaction;   // an interrupt, stun or stagger (lead decision 8); a light hit's flinch (aim sway only) does not refuse (lead 40)
        if (s.Rush != RushPhase.None || s.EvadeTicks > 0 || s.Jump != JumpPhase.None) return ExecutionRefusal.Moving;
        if (ExecutionWeapons.FamilyOf(ExecutionsMeleeWargear(s)) is null) return ExecutionRefusal.NoFamily;
        if (!ExecutionTargets.CanExecute(Races.Of(Config, s.Class, s.Team))) return ExecutionRefusal.Race;
        if (State is MatchState.Countdown or MatchState.Ended) return ExecutionRefusal.NotInPlay;
        bool refused = false;
        ExecutionsRefused(id, ref refused);
        return refused ? ExecutionRefusal.Refused : null;
    }

    /// <summary>The wargear of <paramref name="s"/>'s melee choice (<see cref="Loadouts.MeleeChoiceOf"/>), or null.</summary>
    string? ExecutionsMeleeWargear(in PlayerState s)
    {
        var loadout = Loadouts.ClassOf(Config, s.Class);
        int choice = Loadouts.MeleeChoiceOf(Config, s);
        return loadout is not null && choice >= 0 && choice < loadout.MeleeChoices.Count ? loadout.MeleeChoices[choice].Wargear : null;
    }

    /// <summary>The class name the rows are keyed by (<see cref="ClassLoadout.Name"/>; "" without one).</summary>
    string ExecutionsClassName(in PlayerState s) => Loadouts.ClassOf(Config, s.Class)?.Name ?? "";

    void StartExecution(ExecutionRun run)
    {
        run = ExecutionTrack.Quantize(run);   // the wire's precision, so a client's run decoded from the Start is this one
        _executionRuns[run.Executor] = run;
        _executionRunRound[run.Executor] = RoundsBegun;
        _executionStarts.Add(run);
        if (Config.Extras.HasFlag(ContentExtras.Warfront)) ExecutionsWarfrontBegin(run);
    }

    // ---- the checks and events of a running execution ----

    /// <summary>
    /// Off the pin by more than this (metres; ours): a teleport, a respawn, a round reset or a developer tool moved the executor, and the run ends Invalid. Anything nearer (a body guard's
    /// push, a charge's shove after the pin) is pinned back here in the step (lead 39: the pin runs before the tick's contacts and pushes).
    /// </summary>
    public const float PinTolerance = 1f;

    void CheckExecution(ExecutionRun run)
    {
        int id = run.Executor;
        if (!_players.TryGetValue(id, out var p)) { EndExecution(run, ExecutionEndReason.Disconnect, _executionEnds); return; }
        if (!p.Alive) { EndExecution(run, ExecutionEndReason.Killed, _executionEnds); return; }
        if (State is MatchState.Countdown or MatchState.Ended || _executionRunRound.GetValueOrDefault(id) != RoundsBegun) { EndExecution(run, ExecutionEndReason.MatchEnd, _executionEnds); return; }
        if (Config.Executions.AtWire(run.Wire) is not { } row) { EndExecution(run, ExecutionEndReason.Invalid, _executionEnds); return; }
        var pose = ExecutionTrack.Pose(run, row, Tick, Config.TickRate);
        if (Vector3.Distance(p.Position, pose.Feet) > PinTolerance) { EndExecution(run, ExecutionEndReason.Invalid, _executionEnds); return; }
        _players[id] = ExecutionTrack.Pin(p, run, row, Tick, Config.TickRate);   // re-pinned after this tick's contacts, pushes and collisions
        bool warfront = Config.Extras.HasFlag(ContentExtras.Warfront);
        if (warfront)
        {
            bool valid = true;
            ExecutionsWarfrontValid(run, ref valid);
            if (!valid) { EndExecution(run, ExecutionEndReason.Invalid, _executionEnds); return; }
        }
        if (!run.KillDone && Tick >= ExecutionTrack.EventTick(run, row, ExecutionEventKind.Kill, Config.TickRate))
        {
            // Classic: the kill already stands (the finisher deals no damage, no score, no second kill). Warfront: the kill frame through the normal damage path (X4).
            run = run with { KillDone = true };
            if (warfront) ExecutionsWarfrontKill(ref run);
            _executionRuns[id] = run;
        }
        if (Tick >= ExecutionTrack.EndTick(run, row, Config.TickRate)) EndExecution(run, ExecutionEndReason.Done, _executionEnds);
    }

    void EndExecution(in ExecutionRun run, ExecutionEndReason why, List<ExecutionEnded> into)
    {
        _executionRuns.Remove(run.Executor);
        _executionRunRound.Remove(run.Executor);
        into.Add(new ExecutionEnded(run, why, Tick));
        if (Config.Extras.HasFlag(ContentExtras.Warfront)) ExecutionsWarfrontEnded(run, why);
    }

    // ---- Warfront (X4 implements these; no-ops until then) ----

    /// <summary>Warfront: the Ork <paramref name="id"/>'s press at <paramref name="pressTick"/> takes; set <paramref name="handled"/> (Warfront owns the press) and <paramref name="pick"/> (null: nothing happens).</summary>
    partial void ExecutionsWarfrontPick(int id, in PlayerState executor, uint pressTick, ref ExecutionPick? pick, ref bool handled);
    /// <summary>Warfront: the run started (reserve the Ork).</summary>
    partial void ExecutionsWarfrontBegin(in ExecutionRun run);
    /// <summary>Warfront: whether the Ork is still held and alive (false ends the run Invalid).</summary>
    partial void ExecutionsWarfrontValid(in ExecutionRun run, ref bool valid);
    /// <summary>Warfront: the kill frame (damage through the normal path; set <c>KillDone</c> from the outcome).</summary>
    partial void ExecutionsWarfrontKill(ref ExecutionRun run);
    /// <summary>Warfront: the run ended (release the Ork; Done with the kill: the reward and the cooldown).</summary>
    partial void ExecutionsWarfrontEnded(in ExecutionRun run, ExecutionEndReason why);
    /// <summary>The checks of other features (X4: the cooldown and the retainer rules); set <paramref name="refused"/> to refuse the press.</summary>
    partial void ExecutionsRefused(int id, ref bool refused);
}
