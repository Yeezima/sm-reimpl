using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SmContent;
using SmNet;
using SmSim;
using V3 = System.Numerics.Vector3;
using V2 = System.Numerics.Vector2;

namespace SmClient;

/// <summary>
/// Test-bot navigation (test script, not the original AI): on a map a bot client builds a <see cref="NavGraph"/> for its own
/// side from the side's character collision in the background (<see cref="BotNavigation"/>; read from
/// <c>%LOCALAPPDATA%\sm-reimpl\cache\nav</c> when an earlier run built the same one; never in the repo), so it never plans
/// through the other side's spawn-room blockers, and follows A* paths with a <see cref="NavAgent"/> to its goals: the nearest
/// enemy's last shown position, a Seize Ground point, the map centre, or a test route. Jump-pack classes (Assault, Raptor)
/// also fly jump links the builder checked with the real jump rules (<see cref="NavJumpSet"/>): the agent walks to the
/// take-off cell, stands still, presses Jump and replays the checked controls to the landing. Until the graph is ready (and on
/// flat ground) the old straight walk with fixed-angle detours runs. While fighting it strafes and rolls only onto walkable
/// cells. Test aids: <c>--bot-goal &lt;point&gt;</c> and <c>--bot-route &lt;p1,p2,...&gt;</c> (points: x,y pairs or names
/// A/B/C for the Seize Ground points, marines/chaos for the start areas, centre, spawnN), and a "nav:" log line every 5 s.
/// </summary>
public partial class NetworkView
{
    const double NavLogEvery = 5.0;              // seconds between the bot's "nav:" log lines
    const float BotStrafeCheck = 1.5f;           // metres of walkable ground a strafe needs on its side (test script)
    const float BotRollCheck = 3f;               // metres of walkable ground an evade roll needs on its side (test script)
    const float BotRouteArrive = 1.5f;           // metres from a route point at which it counts as reached (test script)

    /// <summary><c>--bot-goal</c>: one point (see <see cref="ParseRoute"/>) the bot walks to and holds, fighting what it sees.</summary>
    public string? BotGoal { get; init; }
    /// <summary><c>--bot-route</c>: points the bot walks through in order, then again from the first (fighting what it sees).</summary>
    public string? BotRoute { get; init; }

    /// <summary>One side's (and jump pack's) graph build and agent; a bot only ever uses its own side's.</summary>
    sealed class NavSlot(Team team, string pack)
    {
        public readonly Team Team = team;
        public readonly string Pack = pack;
        public Task<(NavGraph? Graph, string Note)>? Task;
        public NavGraph? Graph;
        public NavAgent? Agent;
        public string Note = "building";
        /// <summary>M44a: the other side's spawn room as this side's bots see it (NetworkView.BotSpawn.cs).</summary>
        public SpawnRoomWatch? Watch;
        public Task<(SpawnRoomWatch?, string)>? WatchTask;
        public bool WatchDone;
    }

    readonly Dictionary<(Team, string), NavSlot> _navSlots = new();
    NavSlot? _navCurrent;
    System.Threading.CancellationTokenSource _navCancel = new();
    MapContent? _navMap;
    CollisionWorld? _navWorld;
    int _navGeneration;
    NavGraph? _nav => _navCurrent?.Graph;
    NavAgent? _navAgent => _navCurrent?.Agent;
    string _navNote = "no map";
    InputButtons _navJump;          // the Jump button the navigation wants this tick (a link's press or hold)
    readonly Dictionary<string, V3> _namedPoints = new(StringComparer.OrdinalIgnoreCase);
    V3? _mapCentre3;
    List<(string Name, V3? Point, V2 Flat)>? _route;
    int _routeIndex, _routeLaps;
    double _routeLegStart;
    double _navLogAt;
    string _navPurpose = "";
    V3 _navGoal;
    int _botStrafeSide;
    bool _routeNotesLogged, _routeArrived;

    /// <summary>
    /// The bot's own side's graph once built (null before, on flat ground, or when the build failed). The build starts when the
    /// side is known (the first snapshot names it) and the map is loaded; one graph per side and jump pack, so a respawn on
    /// the same side reuses it.
    /// </summary>
    NavGraph? Nav
    {
        get
        {
            if (!_bot || _navMap is null || _navWorld is null || _client is null || _config is null) return null;
            Ph("navigation");
            var me = _client.Predicted;
            if (me.PlayerId <= 0) return _navCurrent?.Graph;
            // Teams are known from the first snapshot; before that (or in free-for-all) a map with blockers waits a few seconds.
            if (me.Team == Team.None && _navWorld.Blockers.Count > 0 && _connectedFor < 5.0) return _navCurrent?.Graph;
            string pack = Loadouts.JumpPack(_config, me)?.Name ?? "";
            if (!_navSlots.TryGetValue((me.Team, pack), out var slot))
            {
                slot = new NavSlot(me.Team, pack);
                _navSlots[(me.Team, pack)] = slot;
                var map = _navMap; var world = _navWorld; var config = _config; var team = me.Team; int playerClass = me.Class;
                if (BotNavigation.Setup(map, config) is null) slot.Note = "no spawns or points to build around";
                else
                {
                    var cancel = _navCancel.Token;
                    slot.Task = Task.Run(() =>
                    {
                        var graph = BotNavigation.LoadOrBuild(map, world, config, team, playerClass, BotNavigation.CacheDirectory, out string note, cancel);
                        return ((NavGraph?)graph, $"side {team}{(pack == "" ? "" : ", pack " + pack)}: {note}");
                    }, cancel);
                }
            }
            _navCurrent = slot;
            // Only the current map's slots are ever stored (PrepareNavigation clears them), so an older map's build cannot set this map's graph or note.
            if (slot.Graph is null && slot.Task is { IsCompleted: true } task)
            {
                slot.Task = null;
                if (task.IsCompletedSuccessfully) (slot.Graph, slot.Note) = task.Result;
                else slot.Note = $"build failed: {task.Exception?.GetBaseException().Message}";
                _navNote = slot.Note;
                if (_bot) GD.Print($"t {_connectedFor:F1} s: id {_client.PlayerId} nav graph (map {_navGeneration}) {slot.Note}");
                if (slot.Graph is not null) slot.Agent = new NavAgent(slot.Graph) { Background = true };
            }
            return slot.Graph;
        }
    }

    /// <summary>
    /// When the map loads (bot clients only): the named points and the background build (or cache read) of the graph over
    /// the box around the spawns, points and start areas, keeping what they reach.
    /// </summary>
    void PrepareNavigation(MapContent map, CollisionWorld world)
    {
        _namedPoints.Clear();
        for (int i = 0; i < map.ControlPoints.Count; i++) _namedPoints[PointLetter(i)] = map.ControlPoints[i].Position;
        foreach (var a in map.StartAreas)
        {
            _namedPoints[a.Side == Team.SpaceMarines ? "marines" : "chaos"] = a.Position;
            _namedPoints[a.Side == Team.SpaceMarines ? "sm" : "csm"] = a.Position;
        }
        for (int i = 0; i < map.Spawns.Count; i++) _namedPoints["spawn" + (i + 1)] = map.Spawns[i].Position;
        if (map.Spawns.Count > 0)
        {
            var mean = new V3(map.Spawns.Average(s => s.Position.X), map.Spawns.Average(s => s.Position.Y), map.Spawns.Average(s => s.Position.Z));
            _mapCentre3 = mean;
            _namedPoints["centre"] = _namedPoints["center"] = mean;
        }
        _navCancel.Cancel();   // a jump-link build for the previous map stops at its next take-off cell
        _navCancel = new System.Threading.CancellationTokenSource();
        _navSlots.Clear(); _navCurrent = null;
        ++_navGeneration;
        _navMap = _bot ? map : null; _navWorld = _bot ? world : null;
        _navNote = "waiting for the side";
    }

    /// <summary>
    /// Route points: items separated by commas (or semicolons); x/y/z is a point at that height; two numbers in a row are an x,y point (the height comes
    /// from the graph near the bot's own), anything else a name from the map (A, B, C, marines, chaos, centre, spawnN).
    /// Unknown names are dropped with a note.
    /// </summary>
    List<(string Name, V3? Point, V2 Flat)> ParseRoute(string text, List<string> notes)
    {
        var items = text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var route = new List<(string, V3?, V2)>();
        for (int i = 0; i < items.Length; i++)
        {
            var xyz = items[i].Split('/');
            if (xyz.Length == 3 && float.TryParse(xyz[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float px) && float.TryParse(xyz[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float py)
                && float.TryParse(xyz[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float pz))
            {
                route.Add(($"{px:0.#}/{py:0.#}/{pz:0.#}", new V3(px, py, pz), new V2(px, py)));   // x/y/z: a point at a given height (upper floors)
            }
            else if (float.TryParse(items[i], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                && i + 1 < items.Length && float.TryParse(items[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y))
            {
                route.Add(($"{x:0.#},{y:0.#}", null, new V2(x, y)));
                i++;
            }
            else if (_namedPoints.TryGetValue(items[i], out var p)) route.Add((items[i].ToUpperInvariant(), p, new V2(p.X, p.Y)));
            else notes.Add($"unknown route point '{items[i]}' (known: {string.Join(", ", _namedPoints.Keys)})");
        }
        return route;
    }

    /// <summary>
    /// The direction to walk toward <paramref name="goal"/> (unit, ground plane), a speed factor, and whether it is reached:
    /// path following on the graph, else (graph not ready) the old straight walk with detours. <paramref name="purpose"/>
    /// names the goal for the log.
    /// </summary>
    (Vector2 Direction, float Speed, bool Arrived, bool DropAhead) BotSteer(PlayerState me, V3 goal, string purpose)
    {
        _navPurpose = purpose;
        _navGoal = goal;
        _navJump = InputButtons.None;
        if (Nav is null || _navAgent is null)
        {
            var straight = BotWalkDirection(me, new Vector2(goal.X, goal.Y));
            return (straight, 1f, false, false);
        }
        var steer = _navAgent.Steer(me.Position, me.Grounded, goal, _connectedFor, NavBodyOf(me));
        _navJump = steer.Jump ? InputButtons.Jump : InputButtons.None;
        LogNavJump(me);
        var d = new Vector2(steer.Direction.X, steer.Direction.Y);
        return (d, steer.Speed, steer.Arrived, steer.DropAhead);
    }

    /// <summary>What the agent needs to know to fly a jump link (null for a class without a jump pack).</summary>
    NavBody? NavBodyOf(PlayerState me)
    {
        if (_config is null || Loadouts.JumpPack(_config, me) is not { } pack) return null;
        bool busy = me.MeleeStep > 0 || me.Throwing || me.SwitchLeft > 0f || me.Charging || me.PendingShot > 0 || me.JumpHeld || me.Jump is JumpPhase.Dive or JumpPhase.Recover;
        return new NavBody(me.Jump, me.JumpTicks, me.JumpPower, new Vector2(me.Velocity.X, me.Velocity.Y).Length(), pack, _config.TickSeconds, busy);
    }

    /// <summary>
    /// A link in flight (press to landing) keeps its checked controls whatever else the bot would do: null unless the agent
    /// is flying one. Called first in <see cref="BotInput"/>.
    /// </summary>
    PlayerInput? BotNavFlightInput(PlayerState me, uint tick)
    {
        if (!_bot || !me.Alive || _navAgent is not { JumpInProgress: true } agent) return null;
        var steer = agent.Steer(me.Position, me.Grounded, _navGoal, _connectedFor, NavBodyOf(me));
        LogNavJump(me);
        if (!steer.Flying) return null;
        _pitch = 0f;
        if (steer.Direction != V2.Zero) _yaw = MathF.Atan2(-steer.Direction.X, steer.Direction.Y);
        _botNote = $"nav jump to {agent.FlightTarget.X:F0},{agent.FlightTarget.Y:F0}";
        return new PlayerInput(tick, 0f, steer.Speed, _yaw, steer.Jump ? InputButtons.Jump : InputButtons.None, 0f);
    }

    int _jumpsLogged, _jumpsLandedLogged;

    /// <summary>One line when a link's flight starts and one when it ends (where it was meant to land and where the body came down).</summary>
    void LogNavJump(PlayerState me)
    {
        if (_navAgent is not { } agent) return;
        if (agent.JumpsStarted < _jumpsLogged || agent.JumpsLanded + agent.JumpsMissed < _jumpsLandedLogged) { _jumpsLogged = 0; _jumpsLandedLogged = 0; }
        if (agent.JumpsStarted > _jumpsLogged)
        {
            _jumpsLogged = agent.JumpsStarted;
            var path = agent.Path;
            var wp = path is not null && agent.Waypoint < path.Waypoints.Count ? path.Waypoints[agent.Waypoint] : default;
            GD.Print($"t {_connectedFor:F1} s: id {_client!.PlayerId} nav jump #{_jumpsLogged} starts at {me.Position.X:F1},{me.Position.Y:F1},{me.Position.Z:F1} for {wp.Position.X:F1},{wp.Position.Y:F1},{wp.Position.Z:F1} (hold {wp.Jump.Hold} ticks, power {wp.Jump.Power:F0} of {me.JumpPower:F0}, heading {wp.Jump.Direction}); goal '{_navPurpose}'");
        }
        int ended = agent.JumpsLanded + agent.JumpsMissed;
        if (ended > _jumpsLandedLogged)
        {
            _jumpsLandedLogged = ended;
            if (agent.LastJump is { } j)
                GD.Print($"t {_connectedFor:F1} s: id {_client!.PlayerId} nav jump lands at {j.Came.X:F1},{j.Came.Y:F1},{j.Came.Z:F1} (planned {j.To.X:F1},{j.To.Y:F1},{j.To.Z:F1}, off by {new Vector2(j.Came.X - j.To.X, j.Came.Y - j.To.Y).Length():F1} m); landed {agent.JumpsLanded} missed {agent.JumpsMissed}");
        }
    }

    /// <summary>A strafe side (-1, 0 or 1) for a fighting bot that wants <paramref name="wanted"/>: kept when the ground
    /// <see cref="BotStrafeCheck"/> m that way is walkable, else the other side if that is, else none.</summary>
    float BotWalkableStrafe(PlayerState me, float wanted)
    {
        if (wanted == 0f || Nav is not { } nav) return wanted;
        var forward = new V2(-MathF.Sin(_yaw), MathF.Cos(_yaw));
        var right = new V2(forward.Y, -forward.X);
        int side = _botStrafeSide != 0 ? _botStrafeSide : Math.Sign(wanted);
        if (nav.CanWalk(me.Position, right * (side * BotStrafeCheck))) { _botStrafeSide = side; return side; }
        if (nav.CanWalk(me.Position, right * (-side * BotStrafeCheck))) { _botStrafeSide = -side; return -side; }
        _botStrafeSide = 0;
        return 0f;
    }

    /// <summary>Whether an evade roll to <paramref name="side"/> (-1 left, 1 right) lands on walkable ground (true without a graph).</summary>
    bool BotRollClear(PlayerState me, float side)
    {
        if (Nav is not { } nav) return true;
        var forward = new V2(-MathF.Sin(_yaw), MathF.Cos(_yaw));
        var right = new V2(forward.Y, -forward.X);
        return nav.CanWalk(me.Position, right * (side * BotRollCheck));
    }

    /// <summary>
    /// <c>--bot-goal</c> / <c>--bot-route</c>: walk to the current route point (sprinting when far), holding at a single
    /// goal and starting over after the last route point; each reached point is logged with the leg's time. Null without a
    /// route, when dead, or before the map's named points are known.
    /// </summary>
    PlayerInput? BotRouteInput(PlayerState me, uint tick)
    {
        if ((BotGoal is null && BotRoute is null) || !me.Alive || _client?.Collision is null) return null;
        if (_route is null)
        {
            if (_namedPoints.Count == 0) return null;
            var notes = new List<string>();
            _route = ParseRoute(BotRoute ?? BotGoal!, notes);
            foreach (var n in notes) GD.PrintErr("bot route: " + n);
            GD.Print($"t {_connectedFor:F1} s: id {_client.PlayerId} bot route ({(BotRoute is null ? "goal" : "route")}): {string.Join(" -> ", _route.Select(r => r.Name))}");
            _routeLegStart = _connectedFor;
        }
        if (_route.Count == 0) return null;
        var (name, point, flat) = _route[_routeIndex];
        var goal = point ?? new V3(flat.X, flat.Y, me.Position.Z);
        float distance = new V2(goal.X - me.Position.X, goal.Y - me.Position.Y).Length();
        bool single = BotRoute is null;
        if (distance <= BotRouteArrive || _routeArrived)
        {
            _routeArrived = false;
            if (!single || !_routeNotesLogged)
            {
                GD.Print($"t {_connectedFor:F1} s: id {_client.PlayerId} bot route: reached {name} after {_connectedFor - _routeLegStart:F1} s (lap {_routeLaps + 1}, stuck so far {_navAgent?.StuckCount ?? _botDetours})");
                _routeNotesLogged = single;
            }
            if (single)
            {
                _botNote = $"holding goal {name}";
                return new PlayerInput(tick, 0f, 0f, _yaw, InputButtons.None, 0f);
            }
            _routeIndex = (_routeIndex + 1) % _route.Count;
            if (_routeIndex == 0) _routeLaps++;
            _routeLegStart = _connectedFor;
            _navAgent?.Reset();
            (name, point, flat) = _route[_routeIndex];
            goal = point ?? new V3(flat.X, flat.Y, me.Position.Z);
            distance = new V2(goal.X - me.Position.X, goal.Y - me.Position.Y).Length();
        }
        var (to, speed, arrived, dropAhead) = BotSteer(me, goal, $"route point {name}");
        _botNote = $"route to {name} {distance:F0} m";
        if (arrived)
        {
            // As near as the graph gets (the point itself may be off it): counts as reached on the next tick.
            _routeArrived = true;
            return new PlayerInput(tick, 0f, 0f, _yaw, InputButtons.None, 0f);
        }
        if (to.LengthSquared() > 1e-6f) _yaw = MathF.Atan2(-to.X, to.Y);
        _pitch = 0f;
        return BotMoveInput(tick, to * speed, ((_navAgent?.Remaining ?? distance) > BotChaseSprint * 0.5f && !dropAhead && _navAgent is not { OnJumpLink: true } ? InputButtons.Sprint : InputButtons.None) | _navJump);
    }

    int _stuckLogged;

    /// <summary>One "nav stuck" line per stuck detection of the agent: where, on whose side, and the nearest spawn-room blocker (test measurement).</summary>
    void LogStuck(PlayerState me)
    {
        if (_navAgent is not { } agent || _client?.Collision is not { } world) return;
        if (agent.StuckCount < _stuckLogged) _stuckLogged = 0;
        while (_stuckLogged < agent.StuckCount)
        {
            _stuckLogged++;
            string near = "none";
            float best = float.MaxValue;
            for (int i = 0; i < world.Blockers.Count; i++)
            {
                float d = world.Blockers[i].HorizontalDistance(me.Position);
                if (d < best) { best = d; near = $"{i} ({world.Blockers[i].Side}{(world.Blockers[i].Blocks(me.Team) ? ", blocks me" : ", not me")}) {d:F1} m"; }
            }
            LogStuckProbe(me, agent, world);
            GD.Print($"t {_connectedFor:F1} s: id {_client.PlayerId} nav stuck #{_stuckLogged} at {me.Position.X:F1},{me.Position.Y:F1},{me.Position.Z:F1} team {me.Team} class '{ClassLabel(me.Class)}' goal '{_navPurpose}' nearest blocker {near}; path {(agent.Path is { } sp ? $"{sp.Waypoints.Count} waypoints{(sp.Partial ? ", partial" : "")}, at {agent.Waypoint} ({(agent.Waypoint < sp.Waypoints.Count ? sp.Waypoints[agent.Waypoint].Kind + " " + sp.Waypoints[agent.Waypoint].Position.X.ToString("F1") + "," + sp.Waypoints[agent.Waypoint].Position.Y.ToString("F1") + "," + sp.Waypoints[agent.Waypoint].Position.Z.ToString("F1") : "-")})" : "none")}, grounded {me.Grounded}, jump {me.Jump}, speed {new Vector2(me.Velocity.X, me.Velocity.Y).Length():F1}");
        }
    }

    /// <summary>
    /// M44a probe, with each stuck line: what the shot collision (<c>Weapons</c>, which holds the props and railings) and the character collision show around
    /// the bot, as distances in metres to the first surface in eight directions (0 degrees = toward the path's next waypoint, counter-clockwise) at knee height and at
    /// chest height, and the graph cell under the bot and the one at the waypoint.
    /// </summary>
    void LogStuckProbe(PlayerState me, NavAgent agent, CollisionWorld world)
    {
        if (agent.Path is not { } path || agent.Waypoint >= path.Waypoints.Count || _nav is not { } graph) return;
        var wp = path.Waypoints[agent.Waypoint].Position;
        float heading = MathF.Atan2(wp.Y - me.Position.Y, wp.X - me.Position.X);
        string Ring(StaticMesh mesh, float height)
        {
            var parts = new string[8];
            for (int k = 0; k < 8; k++)
            {
                float a = heading + k * MathF.PI / 4f;
                var hit = mesh.Raycast(me.Position + new V3(0, 0, height), new V3(MathF.Cos(a), MathF.Sin(a), 0), 3f);
                parts[k] = hit is { } h ? h.Distance.ToString("F1") : "-";
            }
            return string.Join(" ", parts);
        }
        int under = graph.Nearest(me.Position, 0.6f, 1.0f);
        string cell = under >= 0 ? $"{graph.Position(under).X:F1},{graph.Position(under).Y:F1},{graph.Position(under).Z:F1}" : "none";
        GD.Print($"t {_connectedFor:F1} s: id {_client!.PlayerId} stuck probe: heading to {wp.X:F1},{wp.Y:F1},{wp.Z:F1}; cell under me {cell}; character knee [{Ring(world.Character, 0.3f)}] chest [{Ring(world.Character, 1.0f)}]; shots knee [{Ring(world.Weapons, 0.3f)}] chest [{Ring(world.Weapons, 1.0f)}]; can walk 1 m toward it {graph.CanWalk(me.Position, new V2(MathF.Cos(heading), MathF.Sin(heading)))}");
    }

    /// <summary>The bot's "nav:" log line every <see cref="NavLogEvery"/> seconds (bots on a map only).</summary>
    void LogNav(PlayerState me)
    {
        if (_bot && _client?.Collision is not null) { LogStuck(me); LogSpawnWait(me); }
        LogBotAim();   // M44a
        if (!_bot || _client?.Collision is null || _connectedFor - _navLogAt < NavLogEvery) return;
        _navLogAt = _connectedFor;
        var a = _navAgent;
        string path = a?.Path is { } p
            ? $"path {p.Length:F0} m ({p.Waypoints.Count} waypoints{(p.Partial ? ", partial" : "")}), remaining {a.Remaining:F0} m ({a.Progress * 100f:F0}% of {a.LegLength:F0} m), waypoint {a.Waypoint}/{p.Waypoints.Count - 1}"
            : a is null ? "no graph yet (straight walk with detours)" : "no path";
        GD.Print($"t {_connectedFor:F1} s: id {_client.PlayerId} nav: {(me.Alive ? "" : "dead; ")}{(_navPurpose == "" ? "no goal" : $"goal {_navPurpose} at {_navGoal.X:F0},{_navGoal.Y:F0},{_navGoal.Z:F0}")}; {path}; " +
                 $"plans {a?.Plans ?? 0} (failed {a?.Failures ?? 0}, {(a is { Plans: > 0 } ? a.PlanMilliseconds / a.Plans : 0):F1} ms mean, {a?.MaxPlanMilliseconds ?? 0:F0} ms max, off the frame loop; steering {a?.MaxSteerMilliseconds ?? 0:F1} ms max){(a is { Unreachable: true } ? ", goal unreachable" : "")}, stuck {a?.StuckCount ?? 0}, old detours {_botDetours}; jumps {a?.JumpsStarted ?? 0} (landed {a?.JumpsLanded ?? 0}, missed {a?.JumpsMissed ?? 0}); graph {(_nav is null ? _navCurrent?.Note ?? _navNote : $"{_nav.NodeCount:N0} cells{(_nav.Jumps is { } js ? $", {js.LinkCount:N0} jump links" : "")}")}");
        _navPurpose = "";
    }
}
