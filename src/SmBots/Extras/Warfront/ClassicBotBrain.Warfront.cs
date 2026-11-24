using SmContent;
using SmSim;
using V3 = System.Numerics.Vector3;

namespace SmBots;

/// <summary>
/// Warfront's part of the Classic bot brain (Phase B T4, added content; <see cref="Extras.Warfront.WarfrontRetainerStrategy"/>): the server hands a retainer's brain the
/// walking graph it already built for the battle (and, later, the same graph with a jump pack's links), and the strategy moves with the brain's own steering and jump rules.
/// Nothing here runs for a Classic bot: only the Warfront host calls these members.
/// </summary>
public sealed partial class ClassicBotBrain
{
    /// <summary>
    /// The battle's walking graph for this brain (Warfront: the server's graph of the stage map, the bots' <c>NavSettings.From</c>), so it plans paths, strafes and rolls onto walkable
    /// cells as a Classic bot does; no graph build of its own. <paramref name="mapName"/> names the map for the logs and the new-map rules (a Warfront name: Musou's rules).
    /// A later call with the jump pack's graph (<paramref name="graph"/> with jump links) swaps it in between flights.
    /// </summary>
    public void WarfrontUseGraph(string mapName, CollisionWorld world, NavGraph graph, Team team, string pack)
    {
        if (_navMap is null || _navMap.Name != mapName || !ReferenceEquals(_navWorld, world))
        {
            _navCancel.Cancel();
            _navCancel = new System.Threading.CancellationTokenSource();
            _navSlots.Clear(); _navCurrent = null;
            ++_navGeneration;
            _navMap = new MapContent(mapName, "warfront", [], [], 0UL);
            _navWorld = world;
        }
        if (_navSlots.TryGetValue((team, pack), out var was))
        {
            if (ReferenceEquals(was.Graph, graph) || was.Agent is { JumpInProgress: true }) return;   // the same graph, or a flight under way: the next call
            was.Graph = graph;
            was.Agent = NewNavAgent(graph);
            return;
        }
        var slot = new NavSlot(team, pack) { Graph = graph, Note = $"warfront graph {graph.NodeCount:N0} cells", WatchDone = true, PlanDone = true };
        slot.Agent = NewNavAgent(graph);
        _navSlots[(team, pack)] = slot;
        _navNote = slot.Note;
    }

    /// <summary>The brain's clock (seconds since it joined) and the players it sees, for the Warfront strategy.</summary>
    internal double WarfrontNow => _connectedFor;
    internal IReadOnlyList<PlayerState> WarfrontOthers => _lastOthers;

    /// <summary>Whether the brain's graph of its current side and pack carries jump links.</summary>
    public bool WarfrontHasJumpLinks => _navCurrent?.Graph?.Jumps is not null;

    /// <summary>A walk toward <paramref name="goal"/> along the graph (the brain's own steering, through the strategy's <see cref="IBotStrategy.ClampGoal"/>); sprints beyond <paramref name="sprintBeyond"/> metres.</summary>
    internal PlayerInput WarfrontWalk(PlayerState me, V3 goal, string purpose, uint tick, float sprintBeyond, InputButtons extra = InputButtons.None)
    {
        var (to, speed, arrived, dropAhead) = BotSteer(me, goal, purpose);
        if (arrived || to.LengthSquared() < 1e-6f) return new PlayerInput(tick, 0f, 0f, _yaw, extra | _navJump, _pitch);
        _yaw = MathF.Atan2(-to.X, to.Y);
        _pitch = 0f;
        float far = _navAgent?.Remaining ?? new Vector2(goal.X - me.Position.X, goal.Y - me.Position.Y).Length();
        var sprint = far > sprintBeyond && !dropAhead && _navAgent is not { OnJumpLink: true } ? InputButtons.Sprint : InputButtons.None;
        return BotMoveInput(tick, to * speed, sprint | extra | _navJump);
    }

    /// <summary>Stands still facing <paramref name="yaw"/> (radians), holding <paramref name="buttons"/>.</summary>
    internal PlayerInput WarfrontStand(uint tick, float yaw, InputButtons buttons = InputButtons.None)
    {
        _yaw = yaw;
        _pitch = 0f;
        return new PlayerInput(tick, 0f, 0f, _yaw, buttons, _pitch);
    }

    /// <summary>The Classic idle (the route test aid, else the chase toward the nearest enemy last shown), through the strategy's goal clamp.</summary>
    internal PlayerInput? WarfrontClassicIdle(PlayerState me, uint tick) => ClassicIdle(me, tick);

    /// <summary>The brain's last decision note (the strategy writes its own).</summary>
    internal void WarfrontNote(string note) => _botNote = note;

    bool _wfPoundPressed;

    /// <summary>
    /// The jump-pound order (the squad's rule since W11: max jump, then a ground pound round the marked point): a jump-pack retainer on the ground faces <paramref name="mark"/> and presses the full jump
    /// (the pack's longest hold, as the Classic bot's jump at 30 m), steers at the mark in the air, and presses melee (the pound) within the Classic pound distance and height over the mark; the dive and
    /// recovery play out. <paramref name="done"/> is set once it has landed after a pound, or when it cannot jump (no pack, no power for the press).
    /// </summary>
    internal PlayerInput? WarfrontPoundInput(PlayerState me, V3 mark, uint tick, out bool done)
    {
        done = false;
        var c = _config!;
        if (Loadouts.JumpPack(c, me) is not { } pack || !pack.PoundEnabled) { done = true; return null; }
        float dx = mark.X - me.Position.X, dy = mark.Y - me.Position.Y;
        float horizontal = MathF.Sqrt(dx * dx + dy * dy), above = me.Position.Z - mark.Z;
        bool hold = _botJumpHolding && tick <= _botJumpHoldUntil;
        if (!hold) _botJumpHolding = false;
        var jump = hold ? InputButtons.Jump : InputButtons.None;
        if (horizontal > 0.1f) _yaw = MathF.Atan2(-dx, dy);
        switch (me.Jump)
        {
            case JumpPhase.TakeOff:
                return new PlayerInput(tick, 0f, 1f, _yaw, jump, 0f);
            case JumpPhase.Ascent or JumpPhase.Hang or JumpPhase.Fall:
                if (!_wfPoundPressed && horizontal <= BotPoundDistance && above >= BotPoundMinHeight && !me.MeleeHeld)
                {
                    _wfPoundPressed = true;
                    _pitch = Math.Clamp(MathF.Atan2(-above, Math.Max(0.1f, horizontal)), -MaxPitchDown, MaxPitchUp);
                    if (_bot) GD.Print($"t {_connectedFor:F1} s: id {_client!.PlayerId} warfront retainer presses pound {above:F1} m above the mark, {horizontal:F1} m away");
                    return new PlayerInput(tick, 0f, horizontal > BotPoundDistance * 0.5f ? 1f : 0f, _yaw, jump | InputButtons.Melee, _pitch);
                }
                return new PlayerInput(tick, 0f, horizontal > 1f ? 1f : 0f, _yaw, jump, 0f);
            case JumpPhase.Dive or JumpPhase.Recover:
                return new PlayerInput(tick, 0f, 0f, _yaw, InputButtons.None, _pitch);
            case JumpPhase.None:
                if (_wfPoundPressed) { _wfPoundPressed = false; done = true; return null; }   // landed after the pound
                if (!me.Grounded || me.JumpHeld || me.JumpPower + 1e-3f < pack.CostAt(0f) || me.MeleeStep > 0 || me.Throwing || me.SwitchLeft > 0f) { done = me.Grounded && me.JumpPower + 1e-3f < pack.CostAt(0f); return null; }
                float longest = pack.ReleaseCut ? JumpPack.FullRiseTicks(pack, c) * c.TickSeconds : pack.MaxHold;
                int holdTicks = Math.Max((int)MathF.Round(longest / c.TickSeconds), NavJumpFlight.ShortestJumpHold(c, me.Class));
                _botJumpHoldUntil = tick + (uint)holdTicks; _botJumpHolding = true;
                _botLastJump = _connectedFor;
                if (_bot) GD.Print($"t {_connectedFor:F1} s: id {_client!.PlayerId} warfront retainer presses the full jump for a pound {horizontal:F1} m away (hold {holdTicks} ticks)");
                return new PlayerInput(tick, 0f, 1f, _yaw, InputButtons.Jump, 0f);
            default:
                return null;
        }
    }

    /// <summary>The jump-pound order was dropped (a new order, a death): the next one starts from the ground.</summary>
    internal void WarfrontPoundReset() => _wfPoundPressed = false;
}
