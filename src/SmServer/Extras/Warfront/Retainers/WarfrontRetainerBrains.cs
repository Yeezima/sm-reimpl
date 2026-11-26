using System.Diagnostics;
using System.Numerics;
using SmBots;
using SmBots.Extras.Warfront;
using SmContent;
using SmNet;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront;
using SmSim.Extras.Warfront.Retainers;
using SmSim.Extras.Warfront.Squad;

namespace SmServer.Extras.Warfront.Retainers;

/// <summary>
/// The Warfront retainers' brains on the server (Phase B T4; added content, Warfront only; spec "Added content: Warfront: the retainers' brain"): every retainer (a World player of <see cref="MarineRole.Squad"/>)
/// is driven by the Classic PvP bot brain (<see cref="ClassicBotBrain"/>, the same engine-free code the bot clients run) through a server <see cref="IBotWorldView"/> and the Warfront strategy
/// (<see cref="WarfrontRetainerStrategy"/>: Follow, Hold, Attack, the 40 m leash, overshoot, the 3 s grace, the jump-pound and revives).
/// <para>
/// Once per tick, at the first retainer's input (<see cref="WarfrontRetainers.Brain"/>), the host polls the brains' background work, builds each retainer's view from the players as they stood at
/// the tick's start (<see cref="World.WarfrontTickStartPlayers"/>) and the <see cref="MaxOrks"/> nearest Orks, then steps every brain in parallel; each brain reads only its own view and
/// state, and plans its paths inside its steering call (<see cref="ClassicBotOptions.SyncPlans"/>), so the inputs do not depend on the threads. Log lines are written in World id order after the step.
/// </para>
/// <para>
/// The walking graph is the server's own graph of the stage map (the bots' <c>NavSettings.From</c>). A jump-pack class's links (review F1/L5, keyed by the walking graph and the pack): read from the private
/// nav cache in the tick the pack is first seen when the cache has them; otherwise built in the background (into the cache) and taken only at a battle's start, so the inputs never depend on when a
/// thread finishes (deterministic for the same nav cache at the battle's start). The brains' cover and approach searches run inside their step (<see cref="ClassicBotOptions.InlineSearches"/>).
/// </para>
/// </summary>
public sealed class WarfrontRetainerBrains
{
    /// <summary>The nearest Orks a retainer's view shows (Phase B design Q4: candidates capped at 8). Kind: ours (design); confidence: medium.</summary>
    public const int MaxOrks = 8;
    /// <summary>How far (flat) and how high a retainer's view looks for Orks (ours: beyond any weapon's fight range). Kind: ours; confidence: low.</summary>
    public const float OrkSight = 80f, OrkVertical = 30f;
    /// <summary>An Ork's player id in a view: this plus its pool id (far above every player id).</summary>
    public const int OrkIdBase = 1_000_000;
    /// <summary>A downed ally is a revive candidate within this of the retainer (ours). Kind: ours; confidence: low.</summary>
    public const float ReviveSight = 15f;

    readonly World _world;
    readonly WarfrontRetainers _registry;
    readonly Func<NavGraph?> _nav;
    readonly Func<string> _mapName;
    readonly Action<string>? _log;
    readonly BotWork _work = new();
    readonly Dictionary<int, Retainer> _brains = new();
    readonly Dictionary<int, PlayerInput> _inputs = new();
    readonly Dictionary<string, Task<(NavGraph? Graph, string Note)>> _jumpBuilds = new(StringComparer.Ordinal);
    readonly Dictionary<string, NavGraph?> _jumpGraphs = new(StringComparer.Ordinal);   // null: none this battle (building, or failed)
    /// <summary>The walking graph the jump dictionaries belong to (review L5: a new stage graph forgets them).</summary>
    NavGraph? _jumpWalk;
    int _battleSeen = -1;
    readonly List<Retainer> _stepping = new();
    readonly List<PlayerState> _sorted = new();
    readonly List<int> _gone = new();
    long _computedTick = -1;
    /// <summary>A search radius and height for snapping a Hold place or a pound landing onto the graph (review L8). Kind: ours; confidence: low.</summary>
    public const float SnapRadius = 4f, SnapHeight = 2.5f;

    /// <summary>Whether jump-pack classes get their jump links: off by default; env <c>SM_WF_RETAINER_JUMPS=1</c> turns them on (see the remarks).</summary>
    /// <remarks>
    /// Off by default since the review of 2026-11-25: the jump-link path has not run on a real stage (no headless real-stage run yet); <c>SM_WF_RETAINER_JUMPS=1</c> turns it on. Off, a jump-pack
    /// retainer walks the stage's walking graph (it still jumps and pounds in its fights and on the jump-pound order).
    /// </remarks>
    public bool JumpLinks { get; init; } = Environment.GetEnvironmentVariable("SM_WF_RETAINER_JUMPS") == "1";
    /// <summary>The nav cache folder the jump links are read from and built into (tests: a private folder).</summary>
    public string CacheDirectory { get; init; } = BotNavigation.CacheDirectory;
    /// <summary>Forwards the brains' own log lines (the Classic bot's chatty lines; review 2026-11-25: off by default, <c>SM_WF_RETAINER_LOG=1</c>); the host's event lines always go out.</summary>
    public bool VerboseLog { get; init; } = Environment.GetEnvironmentVariable("SM_WF_RETAINER_LOG") == "1";
    /// <summary>Jump-link cache reads on the tick and background builds started (tests).</summary>
    public int JumpReads { get; private set; }
    public int JumpBuildsStarted { get; private set; }
    /// <summary>Steps the brains on the thread pool (false: one after the other; the same inputs either way).</summary>
    public bool Parallel { get; init; } = true;
    /// <summary>Ticks stepped and the milliseconds the last one took (the whole compute: views, brains, logs).</summary>
    public long Steps { get; private set; }
    public double LastMilliseconds { get; private set; }

    public WarfrontRetainerBrains(World world, WarfrontRetainers registry, Func<NavGraph?> nav, Func<string> mapName, Action<string>? log = null)
    {
        _world = world; _registry = registry; _nav = nav; _mapName = mapName; _log = log;
    }

    /// <summary>Plugs the brains into the registry (<see cref="WarfrontRetainers.Brain"/>).</summary>
    public WarfrontRetainerBrains Attach() { _registry.Brain = Input; return this; }

    /// <summary>The input of retainer <paramref name="id"/> this tick (computed for every retainer at the first call of a tick).</summary>
    public PlayerInput? Input(int id, PlayerState self)
    {
        if (_world.Tick != _computedTick) Compute();
        return _inputs.TryGetValue(id, out var input) ? input : null;
    }

    /// <summary>The brain of World id <paramref name="id"/> (tests and logs), or null.</summary>
    public ClassicBotBrain? BrainOf(int id) => _brains.TryGetValue(id, out var r) ? r.Brain : null;
    /// <summary>The strategy of World id <paramref name="id"/> (tests and logs), or null.</summary>
    public WarfrontRetainerStrategy? StrategyOf(int id) => _brains.TryGetValue(id, out var r) ? r.Strategy : null;

    void Compute()
    {
        long started = Stopwatch.GetTimestamp();
        _computedTick = _world.Tick;
        _inputs.Clear();
        _work.Poll();
        var players = _world.WarfrontTickStartPlayers;
        var nav = _nav();
        string map = _mapName();
        if (!ReferenceEquals(nav, _jumpWalk)) { _jumpWalk = nav; _jumpGraphs.Clear(); _jumpBuilds.Clear(); _work.Clear(); }   // a new stage map: its jobs start afresh (T1: map jobs are shared per loaded map with the first asker's token)
        if (_registry.Battles != _battleSeen) { _battleSeen = _registry.Battles; AdoptJumpGraphs(); }
        _sorted.Clear();
        foreach (var p in players.Values) _sorted.Add(p);
        _sorted.Sort(static (a, b) => a.PlayerId.CompareTo(b.PlayerId));
        // the retainers of this tick, in World id order; brains of ids gone are dropped
        _stepping.Clear();
        foreach (var r in _registry.All)
        {
            if (r.WorldId == 0 || r.State == SquadState.Out || !players.TryGetValue(r.WorldId, out var me)) continue;
            // review L1: a fresh brain for each battle's record (compared by reference) and for each new life (a revive after a fall); review 2026-11-25 (4): also for a brain not stepped on the
            // last tick (every retainer was down, so nothing asked for an input and its fall was never seen)
            if (!_brains.TryGetValue(r.WorldId, out var b) || !ReferenceEquals(b.Record, r) || (me.Alive && b.Fell) || b.Seen != _computedTick - 1) _brains[r.WorldId] = b = NewRetainer(r, me);
            b.Seen = _computedTick;
            b.Record = r;
            _stepping.Add(b);
        }
        _gone.Clear();
        foreach (var (id, b) in _brains) if (b.Seen != _computedTick) _gone.Add(id);
        foreach (int id in _gone) _brains.Remove(id);
        _stepping.Sort((a, b) => a.Id.CompareTo(b.Id));
        // views (serial: the Ork pool's query and the registry are not for threads)
        foreach (var b in _stepping) Fill(b, players, nav, map);
        // the brains (parallel: each its own view and state)
        if (Parallel && _stepping.Count > 1) System.Threading.Tasks.Parallel.ForEach(_stepping, StepOne);
        else foreach (var b in _stepping) StepOne(b);
        foreach (var b in _stepping)
        {
            _inputs[b.Id] = b.Input;
            if (b.Error is { } error)
            {
                if (!b.ErrorLogged) { b.ErrorLogged = true; _log?.Invoke($"warfront retainer P{b.Id}: brain failed ({error}); it stands this tick (logged once)"); }
                b.Error = null;
            }
            if (b.Strategy.Situation.PoundDone) { b.Strategy.Situation.PoundDone = false; _registry.PoundDone(b.Id); }
            if (VerboseLog) foreach (string line in b.Lines) _log?.Invoke(line);
            b.Lines.Clear();
            NoteEvents(b);
        }
        Steps++;
        LastMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    Retainer NewRetainer(WarfrontRetainer r, PlayerState me)
    {
        var view = new View(this, r.WorldId) { Yaw = me.Yaw, Pitch = 0f };
        var strategy = new WarfrontRetainerStrategy();
        var retainer = new Retainer(r.WorldId, r.Index, view, strategy, _world.Tick) { Record = r, WasAlive = me.Alive };
        var options = new ClassicBotOptions { SyncPlans = true, InlineSearches = true, WorkLane = $"wf-retainer-{r.WorldId}" };
        retainer.Brain = new ClassicBotBrain(view, BotArgs.None, options, VerboseLog ? new BotLog(retainer.Lines.Add, retainer.Lines.Add) : BotLog.None, IBotHooks.None, strategy);
        _log?.Invoke($"warfront retainer P{r.WorldId} (owner P{r.Owner}, slot {r.Slot}): brain on ({_world.Config.Classes[Math.Clamp(me.Class, 0, _world.Config.Classes.Count - 1)].Name})");
        return retainer;
    }

    /// <summary>The retainer's view and situation for this tick.</summary>
    void Fill(Retainer b, IReadOnlyDictionary<int, PlayerState> players, NavGraph? nav, string map)
    {
        var me = players[b.Id];
        // review L2: in Warfront every World player (human, retainer, the allied pair, a pod Marine) is an ally whatever its raw team; the Orks are the enemy
        var ally = me.Team is Team.SpaceMarines or Team.Chaos ? me.Team : Team.SpaceMarines;
        if (me.Team != ally) me = me with { Team = ally };
        var v = b.View;
        v.Me = me;
        v.Now = (_world.Tick - b.CreatedTick) * (double)_world.Config.TickSeconds;
        v.Others.Clear();
        v.Chest.Clear();
        foreach (var p in _sorted) if (p.PlayerId != b.Id) v.Others.Add(p.Team == ally ? p : p with { Team = ally });
        if (_world.Musou is { } musou && me.Alive)
        {
            Span<ushort> near = stackalloc ushort[MaxOrks];
            int n = musou.Query(me.Position, OrkSight, near, OrkVertical);
            var enemyTeam = me.Team == Team.Chaos ? Team.SpaceMarines : Team.Chaos;
            for (int k = 0; k < n; k++)
            {
                if (!musou.TryGet(near[k], out var e) || !e.Alive) continue;
                int id = OrkIdBase + e.Id;
                v.Others.Add(new PlayerState(id, e.Position, new Vector3(e.Velocity.X, e.Velocity.Y, 0f), e.Yaw, true) { Alive = true, Health = e.Health, Team = enemyTeam, Class = me.Class });
                v.Chest[id] = musou.ShotBody(e.Type).Height * 0.55f;
            }
        }
        // the situation: the owner's order, his place, the hold and attack points, a downed ally
        var r = b.Record!;
        var s = b.Strategy.Situation;
        bool ownerHere = players.TryGetValue(r.Owner, out var owner);
        s.Owner = ownerHere ? owner.Position : me.Position;
        s.OwnerYaw = ownerHere ? owner.Yaw : me.Yaw;
        s.OwnerAlive = ownerHere && owner.Alive;
        var marine = _world.Tfw?.MarineOf(b.Id);
        var order = _registry.OrderNow(r.Owner);
        s.Order = !ownerHere ? RetainerOrder.Hold : order switch { MarineOrder.Hold => RetainerOrder.Hold, MarineOrder.Attack => RetainerOrder.Attack, _ => RetainerOrder.Follow };
        s.HoldAt = marine?.HoldAt ?? me.Position;
        if (!ownerHere && marine is not null && marine.HoldAt == Vector3.Zero) s.HoldAt = me.Position;
        s.HoldAt = Snap(nav, s.HoldAt, ref b.HoldRaw, ref b.HoldSnapped);   // review L8: a ring place off the graph (a wall, a drop) is moved onto its nearest cell
        s.AttackAt = marine?.AttackAt;
        s.PoundAt = r.PoundAt is { } mark ? Snap(nav, mark, ref b.PoundRaw, ref b.PoundSnapped) : null;
        if (r.PoundAt is not null) s.Order = RetainerOrder.JumpPound;
        int count = 0, slot = 0;
        foreach (var o in _registry.All) if (o.Owner == r.Owner && o.WorldId != 0) { if (o.WorldId == b.Id) slot = count; count++; }
        s.Slot = slot; s.Count = Math.Max(1, count);
        s.ReviveAt = ReviveTarget(b.Id, me, players);
        // the graph: the stage's walking graph, then the pack's jump links when they are ready
        if (nav is not null && _world.Collision is { } collision)
        {
            string pack = Loadouts.JumpPack(_world.Config, me)?.Name ?? "";
            if (pack != "" && JumpLinks && !_jumpGraphs.ContainsKey(pack)) JumpGraphFor(pack, nav, map, collision, me);
            var graph = pack != "" && _jumpGraphs.TryGetValue(pack, out var withJumps) && withJumps is not null ? withJumps : nav;
            b.Brain!.WarfrontUseGraph(map, collision, graph, me.Team, pack);
        }
        if (!me.Alive) b.Fell = true;
        b.WasAlive = me.Alive;
    }

    /// <summary>The nearest downed ally within <see cref="ReviveSight"/>: a downed retainer, or a fallen human (W3's downed phase).</summary>
    Vector3? ReviveTarget(int self, PlayerState me, IReadOnlyDictionary<int, PlayerState> players)
    {
        Vector3? best = null;
        float bestD = ReviveSight;
        foreach (var o in _registry.All)
        {
            if (o.State != SquadState.Downed || o.WorldId == self || !players.TryGetValue(o.WorldId, out var p)) continue;
            float d = Flat(p.Position, me.Position);
            if (d < bestD) { bestD = d; best = p.Position; }
        }
        if (_world.Tfw is { } run)
            foreach (int id in run.InField)
            {
                if (!players.TryGetValue(id, out var p) || p.Alive || run.WarfrontDownState(id).Phase != WarfrontDownPhase.Downed) continue;
                float d = Flat(p.Position, me.Position);
                if (d < bestD) { bestD = d; best = p.Position; }
            }
        return best;
    }

    void StepOne(Retainer b)
    {
        var me = b.View.Me;
        uint tick = (uint)_world.Tick;
        if (!me.Alive)
        {
            b.Brain!.NoteDead();
            b.Input = new PlayerInput(tick, 0f, 0f, me.Yaw, InputButtons.None);
            return;
        }
        try
        {
            if (FaultForTests is { } fault) fault(b.Id);
            var input = b.Brain!.Step(tick);
            b.Input = b.Brain.Hold(input);
            b.Brain.Frame(me);
        }
        catch (Exception e)
        {
            // review F2: one retainer's fault never stops the others' step or the server's tick: it stands this tick (logged once, in World id order after the step)
            b.Error = $"{e.GetType().Name}: {e.Message}";
            b.Input = new PlayerInput(tick, 0f, 0f, me.Yaw, InputButtons.None);
        }
    }

    /// <summary>Test aid: called with a retainer's World id inside its step (a throw there is the F2 fault test).</summary>
    internal Action<int>? FaultForTests { get; set; }

    /// <summary>A point snapped to its nearest graph cell (within <see cref="SnapRadius"/>), recomputed only when the raw point changes; unchanged without a graph or a cell near.</summary>
    static Vector3 Snap(NavGraph? nav, Vector3 raw, ref Vector3? lastRaw, ref Vector3 snapped)
    {
        if (nav is null) return raw;
        if (lastRaw == raw) return snapped;
        lastRaw = raw;
        int n = nav.Nearest(raw, SnapRadius, SnapHeight);
        snapped = n >= 0 ? nav.Position(n) : raw;
        return snapped;
    }

    static Vector3? Snap(NavGraph? nav, Vector3? raw, ref Vector3? lastRaw, ref Vector3 snapped) => raw is { } p ? Snap(nav, p, ref lastRaw, ref snapped) : null;

    /// <summary>One log line when a retainer's decision or leash state changes (at most one a second per retainer; the event log of a headless run).</summary>
    void NoteEvents(Retainer b)
    {
        string decision = b.Strategy.Decision;
        var leash = b.Strategy.Leash.State;
        if (decision == b.LastDecision && leash == b.LastLeash) return;
        if (_world.Tick - b.LastLogTick < _world.Config.TickRate && leash == b.LastLeash) { b.LastDecision = decision; return; }
        var s = b.Strategy.Situation;
        _log?.Invoke(FormattableString.Invariant($"warfront retainer P{b.Id}: {b.LastDecision} -> {decision}; leash {b.LastLeash} -> {leash}; order {s.Order}; {Flat(b.View.Me.Position, s.Owner):0.0} m from the owner"));
        b.LastDecision = decision; b.LastLeash = leash; b.LastLogTick = _world.Tick;
    }

    /// <summary>
    /// The pack's jump-link graph for this battle (review F1): read from the nav cache now when the cache holds it (a deterministic tick: the first one the pack is seen); otherwise a background build
    /// into the cache starts, the brains walk, and the graph is taken at the next battle's start (<see cref="AdoptJumpGraphs"/>).
    /// </summary>
    void JumpGraphFor(string pack, NavGraph walk, string map, CollisionWorld collision, PlayerState me)
    {
        var config = _world.Config;
        var team = me.Team;
        int playerClass = me.Class;
        var content = new MapContent(map + "-wf-retainer", "warfront", [], [], 0UL);
        string dir = CacheDirectory;
        ulong key = NavJumpSet.KeyFor(walk, config, playerClass, new NavJumpOptions());
        // the tick path only reads (review 2026-11-25 (1)): no build, no build lock, no write; a missing or damaged file goes to the background build below
        JumpReads++;
        if (NavCache.TryLoadJumps(dir, content.Name, key, walk.NodeCount) is { } cached)
        {
            _jumpGraphs[pack] = walk.WithJumps(cached);
            _log?.Invoke($"warfront retainers: jump links for pack {pack} on {map} read from the cache: {cached.LinkCount:N0} links");
            return;
        }
        _jumpGraphs[pack] = null;   // none this battle
        if (_jumpBuilds.ContainsKey(pack)) return;
        int threads = Math.Max(1, Environment.ProcessorCount / 4);
        _log?.Invoke($"warfront retainers: jump links for pack {pack} on {map}: building in the background ({threads} thread(s)); the retainers walk until the next battle");
        JumpBuildsStarted++;
        _jumpBuilds[pack] = _work.Run<(NavGraph?, string)>($"wf jump {map} {pack} {key:X16}", () =>
        {
            var g = BotNavigation.WithJumpLinks(walk, content, collision, config, team, playerClass, dir, out string note, threads);
            return (g, note);
        }, CancellationToken.None);
    }

    /// <summary>At a battle's start: the background builds that have finished hand their graphs over; the packs without one are looked up again (the cache) when first seen.</summary>
    void AdoptJumpGraphs()
    {
        _jumpGraphs.Clear();
        foreach (var (pack, task) in _jumpBuilds.ToList())
        {
            if (!task.IsCompleted) continue;
            _jumpBuilds.Remove(pack);
            if (task.IsCompletedSuccessfully && task.Result.Graph is { } g) { _work.Forget(task); _jumpGraphs[pack] = g; _log?.Invoke($"warfront retainers: jump links for pack {pack} ready at the battle's start: {task.Result.Note}"); }
            else
            {
                _work.Forget(task);   // review 2026-11-25 (1): a failed build's key is forgotten, so a later battle can start it again (BotWork hands a known key's task back)
                _log?.Invoke($"warfront retainers: jump links for pack {pack} failed ({task.Exception?.GetBaseException().Message}); the retainers walk");
            }
        }
    }

    static float Flat(Vector3 a, Vector3 b) => new System.Numerics.Vector2(a.X - b.X, a.Y - b.Y).Length();

    sealed class Retainer(int id, int index, View view, WarfrontRetainerStrategy strategy, long createdTick)
    {
        public readonly int Id = id, Index = index;
        public readonly View View = view;
        public readonly WarfrontRetainerStrategy Strategy = strategy;
        public readonly long CreatedTick = createdTick;
        public ClassicBotBrain? Brain;
        public WarfrontRetainer? Record;
        public readonly List<string> Lines = new();
        public PlayerInput Input;
        public long Seen, LastLogTick = long.MinValue / 2;
        public bool WasAlive, Fell, ErrorLogged;
        public string? Error;
        public Vector3? HoldRaw, PoundRaw;
        public Vector3 HoldSnapped, PoundSnapped;
        public string LastDecision = "";
        public LeashState LastLeash = LeashState.Inside;
    }

    /// <summary>A retainer's world as its brain reads it (filled once per tick before the step; read only while the brains step).</summary>
    sealed class View(WarfrontRetainerBrains host, int id) : IBotWorldView
    {
        public PlayerState Me;
        public double Now;
        public readonly List<PlayerState> Others = new();
        public readonly Dictionary<int, float> Chest = new();

        public int PlayerId => id;
        public PlayerState Predicted => Me;
        IReadOnlyList<PlayerState> IBotWorldView.Others => Others;
        public double ConnectedFor => Now;
        public SimConfig? Config => host._world.Config;
        public CollisionWorld? Collision => host._world.Collision;
        public MatchView Match => new(MatchState.Playing, Team.None, 0, 0, 0);
        public IReadOnlyList<ControlPointView> Points => [];
        public SeizeGroundInfo? SeizeLayout => null;
        public bool SeizeGround => false;
        public string MapName => host._mapName();
        public TimeSpan CurrentInterpolationDelay => TimeSpan.Zero;
        public IEnumerable<ProjectileView> NewestProjectiles() => [];
        public IReadOnlyList<ProjectileView> Projectiles() => [];
        public float Yaw { get; set; }
        public float Pitch { get; set; }
        public float GrenadesOutFor => 0f;
        public int OwnShotsFired => 0;
        public ContentExtras MatchExtras => host._world.Config.Extras;
        public StimSpec? StimOf(PlayerState s) => Loadouts.Equipment(host._world.Config, s)?.Stim;
        public float AimHeightOf(PlayerState target) => Chest.TryGetValue(target.PlayerId, out float chest) ? chest : ClassicBotBrain.BotTargetHeight;
        public string ClassLabel(int playerClass) => playerClass >= 0 && playerClass < host._world.Config.Classes.Count ? host._world.Config.Classes[playerClass].Name : $"class {playerClass}";
        public BotWork Work => host._work;
    }
}
