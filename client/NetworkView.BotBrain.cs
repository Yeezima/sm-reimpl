using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmContent;
using SmNet;
using SmSim;
using Brain = SmBots.ClassicBotBrain;
using V3 = System.Numerics.Vector3;

namespace SmClient;

/// <summary>
/// T1 (Phase B, 2026-11-25): the client's thin adapter to the Classic bot brain, which moved out of this class (NetworkView.Bot*.cs, NetworkView.Nav.cs and the bot parts
/// of NetworkView.cs, Jump.cs, Seize.cs, Evade.cs, Reactions.cs, DashAttack.cs) into the engine-free src/SmBots (<see cref="Brain"/>) without a behaviour change. The client
/// keeps its test aids and flags and hands the brain its world (<see cref="BotWorld"/>: the GameClient, the shown players, the view's yaw and pitch), its log (GD.Print, so
/// a bot's log reads as before) and its test-aid hooks (<see cref="BotHooks"/>: the scripted test inputs in their old order, added content's <c>ExtrasBotInput</c>, the
/// charge-direction press log, the frame-profile phase). The members below with the old names forward to the brain for the client code that still calls them
/// (Musou Mode's co-op bot, <c>--bot-pickups</c>, <c>--barrel-test</c>, the hit statistics, the F6 panel, the logs).
/// <para>
/// Test aid <c>--bot-shadow</c>: a second brain runs beside the live one on the same inputs (the same world, a view of its own set to the live view before each
/// step, the live brain's background jobs), its input compared every tick: a "shadow:" log line every 10 s and one line per differing tick (the first 40).
/// <c>--bot-shadow-sync</c> also plans both brains' paths inside the steering call, so thread timing cannot tell them apart. T1 ran it with the client's old copy
/// as the live brain (docs/milestones.md); now it checks that the brain is deterministic for its inputs, and a variant brain can be put in the shadow's place.
/// </para>
/// </summary>
public partial class NetworkView
{
    /// <summary><c>--bot-shadow</c> (test aid): the shadow comparison (see the class).</summary>
    static readonly bool BotShadow = CmdFlag("bot-shadow") || CmdFlag("bot-shadow-sync");
    /// <summary><c>--bot-shadow-sync</c>: the shadow comparison with the paths planned inside the steering call (both brains).</summary>
    static readonly bool BotShadowSync = CmdFlag("bot-shadow-sync");

    /// <summary><c>--bot-goal</c>: one point the bot walks to and holds, fighting what it sees (the brain's route test aid, ClassicBotBrain.Nav.cs).</summary>
    public string? BotGoal { get; init; }
    /// <summary><c>--bot-route</c>: points the bot walks through in order, then again from the first (fighting what it sees).</summary>
    public string? BotRoute { get; init; }

    /// <summary>The background jobs of this client's brains (one store: the shadow shares the live brain's jobs).</summary>
    readonly SmBots.BotWork _botWork = new();
    Brain? _botBrain, _botShadowBrain;
    BotWorld? _botWorld;
    ShadowWorld? _botShadowWorld;
    int _botTickStamp;
    bool _botStepping;
    /// <summary>The map's mean spawn point (game space), set at the map load for every client: the overview camera uses it (NetworkView.MapLoad.cs).</summary>
    V3? _mapCentre3;

    /// <summary>The bot brain (created on first use; bots only).</summary>
    Brain BotBrainOf() => _botBrain ??= NewBotBrain(_botWorld ??= new BotWorld(this), GD.Print, _botHooks ??= new BotHooks(this), primary: true);
    BotHooks? _botHooks;

    /// <summary>The shadow brain and its world (own yaw and pitch), or null without <c>--bot-shadow</c>.</summary>
    Brain? BotShadowOf()
    {
        if (!BotShadow) return null;
        _botWorld ??= new BotWorld(this);
        _botShadowWorld ??= new ShadowWorld(_botWorld);
        // The client's hooks run once a tick, for the live brain; the shadow is handed the live brain's answers of this tick (no test aid or added content's bot runs twice).
        return _botShadowBrain ??= NewBotBrain(_botShadowWorld, null, new ShadowHooks(_botHooks ??= new BotHooks(this)), primary: false);
    }

    Brain NewBotBrain(SmBots.IBotWorldView world, Action<string>? print, SmBots.IBotHooks hooks, bool primary) =>
        new(world, new SmBots.BotArgs(OS.GetCmdlineUserArgs()),
            new SmBots.ClassicBotOptions
            {
                GameDir = _gameDir, Melee = _botMelee, Fire = _botFire, FireWalls = BotFireWalls, ThrowMax = _botThrowMax, JumpFree = _botJumpFree,
                Goal = BotGoal, Route = BotRoute, Lean = Lean, SyncPlans = BotShadowSync, WorkLane = "bot", Primary = primary,
            },
            print is null ? SmBots.BotLog.None : new SmBots.BotLog(s => print(s), s => GD.PrintErr(s)),
            hooks);

    // ---- the host's calls (from the input sampling and the frame) ----

    /// <summary>The brain's step for this tick (the old <c>BotApplyAim(BotInput(tick))</c>), with the shadow comparison.</summary>
    PlayerInput BotBrainStep(uint tick)
    {
        _botTickStamp++;
        _botWork.Poll();
        float yaw0 = _yaw, pitch0 = _pitch;
        _botStepping = true;   // the projectile list is taken once for this step (both brains); outside a step it is read fresh
        try
        {
            var live = BotBrainOf().Step(tick);
            if (BotShadowOf() is { } shadow)
            {
                float liveYaw = _yaw, livePitch = _pitch;
                _botShadowWorld!.Yaw = yaw0; _botShadowWorld.Pitch = pitch0;
                ShadowCompare("step", live, shadow.Step(tick), liveYaw, livePitch, BotBrainOf().Note, shadow.Note);
            }
            return live;
        }
        finally { _botStepping = false; }
    }

    /// <summary>The grenade safety's hold on the final input (the old <c>BotGrenadeHold</c>), with the shadow comparison.</summary>
    PlayerInput BotBrainHold(PlayerInput input)
    {
        float yaw0 = _yaw, pitch0 = _pitch;
        var live = BotBrainOf().Hold(input);
        if (BotShadowOf() is { } shadow)
        {
            float liveYaw = _yaw, livePitch = _pitch;
            _botShadowWorld!.Yaw = yaw0; _botShadowWorld.Pitch = pitch0;
            ShadowCompare("hold", live, shadow.Hold(input), liveYaw, livePitch, BotBrainOf().Note, shadow.Note);
            ShadowTickDone();
        }
        return live;
    }

    /// <summary>The bot is dead (the input sampling skips the brain then): the lives and the push learn of the death.</summary>
    void BotBrainNoteDead() { BotBrainOf().NoteDead(); BotShadowOf()?.NoteDead(); }

    /// <summary>Per frame: a death resets the path; the "nav:", stuck, wait and "aim:" log lines.</summary>
    void BotBrainFrame(PlayerState me) { BotBrainOf().Frame(me); BotShadowOf()?.Frame(me); }

    /// <summary>The own predicted release of a thrown item (grenade safety).</summary>
    void BotBrainNoteOwnRelease(PlayerState me) { BotBrainOf().NoteOwnRelease(me); BotShadowOf()?.NoteOwnRelease(me); }

    /// <summary>A hit report of the own attacks (the bot's aim and tactics statistics; NetworkView.Camera.cs).</summary>
    void BotNoteHits(HitReport hits)
    {
        if (!_bot) return;
        BotBrainOf().NoteHits(hits);
        BotShadowOf()?.NoteHits(hits);
    }

    /// <summary>
    /// When the map loads (every client): the map's mean spawn point for the overview camera; for a bot the brain's named points and the background build (or
    /// cache read) of its navigation graph.
    /// </summary>
    void PrepareNavigation(MapContent map, CollisionWorld world)
    {
        if (map.Spawns.Count > 0) _mapCentre3 = new V3(map.Spawns.Average(s => s.Position.X), map.Spawns.Average(s => s.Position.Y), map.Spawns.Average(s => s.Position.Z));
        if (!_bot) return;
        _botWork.Clear();
        BotBrainOf().PrepareNavigation(map, world);
        BotShadowOf()?.PrepareNavigation(map, world);
    }

    /// <summary>Whether this client may use a Classic PvP bot preset at all (the map is checked later; the brain's <see cref="Brain.BotPresetOnMap"/>).</summary>
    bool BotPresetWanted()
    {
        if (!_bot || Observe || _demoPlayback is not null || string.Equals(CmdArg("bot-preset"), "none", StringComparison.OrdinalIgnoreCase)) return false;
        if (_loadout.Primary is not null || _loadout.Pistol is not null || _loadout.Melee is not null || _loadout.Equipment is not null || _loadout.Random) return false;
        var args = OS.GetCmdlineUserArgs();
        return !args.Contains("--perks") && !args.Contains("--second") && !args.Contains("--profile") && !args.Contains("--bot-change-loadout");
    }

    /// <summary><see cref="ClientOptions.ReviseOnMap"/>: on a Classic PvP map, the options with a preset of the requested class's role (the live brain only: one draw).</summary>
    ClientOptions BotPresetOnMap(string map, ClientOptions options) => BotBrainOf().BotPresetOnMap(map, options);

    // ---- the old names, for the client code that calls the brain ----

    /// <summary>The bots' default accuracy (the brain's; the F6 panel shows it).</summary>
    public const float DefaultBotAccuracy = Brain.DefaultBotAccuracy;

    /// <summary>The number after <c>--name</c> (a finite float, invariant culture), or null: the client's test aids read their values with it (it lived beside the bot's aim flags).</summary>
    static float? FloatArg(string name) => SmBots.BotArgs.FiniteFloat(CmdArgs(name, 1));

    /// <summary>The bot's last decision, for its log (and set by <c>--bot-pickups</c>).</summary>
    string _botNote { get => _bot ? BotBrainOf().Note : ""; set { if (_bot) BotBrainOf().Note = value; } }
    string _botSeizeNote => _bot ? BotBrainOf().SeizeNote : "";
    int _detonateRequests => _bot ? BotBrainOf().DetonateRequests : 0;
    SmSim.NavAgent? _navAgent => _bot ? BotBrainOf().NavAgent : null;
    InputButtons _navJump => _bot ? BotBrainOf().NavJump : InputButtons.None;

    static Vector2 G(SmBots.Vector2 v) => new(v.X, v.Y);

    (Vector2 Direction, float Speed, bool Arrived, bool DropAhead) BotSteer(PlayerState me, V3 goal, string purpose)
    {
        var (d, speed, arrived, drop) = BotBrainOf().BotSteer(me, goal, purpose);
        return (G(d), speed, arrived, drop);
    }

    V3 BotPathGoal(V3 p) => BotBrainOf().BotPathGoal(p);
    Vector2 BotWalkDirection(PlayerState me, Vector2 goal) => G(BotBrainOf().BotWalkDirection(me, new SmBots.Vector2(goal.X, goal.Y)));
    PlayerInput BotMoveInput(uint tick, Vector2 to, InputButtons buttons) => BotBrainOf().BotMoveInput(tick, new SmBots.Vector2(to.X, to.Y), buttons);
    float BotWalkableStrafe(PlayerState me, float wanted) => BotBrainOf().BotWalkableStrafe(me, wanted);
    InputButtons BotWeaponButtons(PlayerState me, uint tick, PlayerState? target) => BotBrainOf().BotWeaponButtons(me, tick, target);
    PlayerState? BotFightTarget(PlayerState me, out float distance) => BotBrainOf().BotFightTarget(me, out distance);

    // ---- the shadow comparison ----

    int _shadowTicks, _shadowSame, _shadowLogged;
    double _shadowLogAt;
    bool _shadowDiffer;
    readonly Dictionary<string, int> _shadowFields = new();

    void ShadowCompare(string stage, PlayerInput live, PlayerInput shadow, float liveYaw, float livePitch, string liveNote, string shadowNote)
    {
        var sw = _botShadowWorld!;
        var diff = new List<string>();
        if (live.MoveX != shadow.MoveX) diff.Add($"movex {live.MoveX:R}/{shadow.MoveX:R}");
        if (live.MoveY != shadow.MoveY) diff.Add($"movey {live.MoveY:R}/{shadow.MoveY:R}");
        if (live.Yaw != shadow.Yaw) diff.Add($"yaw {live.Yaw:R}/{shadow.Yaw:R}");
        if (live.Pitch != shadow.Pitch) diff.Add($"pitch {live.Pitch:R}/{shadow.Pitch:R}");
        if (live.Buttons != shadow.Buttons) diff.Add($"buttons {live.Buttons}/{shadow.Buttons}");
        if (live.ShotYawOffset != shadow.ShotYawOffset || live.ShotPitchOffset != shadow.ShotPitchOffset) diff.Add("shot offsets");
        if (live != shadow && diff.Count == 0) diff.Add("other fields");
        if (liveYaw != sw.Yaw || livePitch != sw.Pitch) diff.Add($"view {liveYaw:R},{livePitch:R}/{sw.Yaw:R},{sw.Pitch:R}");
        if (diff.Count == 0) return;
        foreach (var d in diff) { string key = stage + " " + d.Split(' ')[0]; _shadowFields[key] = _shadowFields.GetValueOrDefault(key) + 1; }
        _shadowDiffer = true;
        if (_shadowLogged++ < 40)
            GD.Print($"t {_connectedFor:F2} s: id {_client?.PlayerId} shadow differs at tick {live.Tick} ({stage}): {string.Join(", ", diff)}; live [{liveNote}] shadow [{shadowNote}]");
    }

    void ShadowTickDone()
    {
        _shadowTicks++;
        if (!_shadowDiffer) _shadowSame++;
        _shadowDiffer = false;
        if (_connectedFor - _shadowLogAt < 10.0) return;
        _shadowLogAt = _connectedFor;
        GD.Print($"t {_connectedFor:F1} s: id {_client?.PlayerId} shadow: ticks {_shadowTicks}, identical {_shadowSame} ({(_shadowTicks > 0 ? 100.0 * _shadowSame / _shadowTicks : 100.0):F3}%), " +
                 $"differing {_shadowTicks - _shadowSame}{(BotShadowSync ? " (sync plans)" : "")}; by field: {string.Join(", ", _shadowFields.OrderBy(k => k.Key).Select(k => $"{k.Key} {k.Value}"))}");
    }

    // ---- the world the brain reads ----

    /// <summary>The bot's world as the brain reads it: this view's GameClient and shown players, its yaw and pitch.</summary>
    sealed class BotWorld(NetworkView view) : SmBots.IBotWorldView
    {
        int _projectilesStamp = -1;
        IReadOnlyList<ProjectileView> _projectiles = [];

        public int PlayerId => view._client!.PlayerId;
        public PlayerState Predicted => view._client!.Predicted;
        public IReadOnlyList<PlayerState> Others => view._lastOthers;
        public double ConnectedFor => view._connectedFor;
        public SimConfig? Config => view._config;
        public CollisionWorld? Collision => view._client?.Collision;
        public MatchView Match => view._client!.Match;
        public IReadOnlyList<ControlPointView> Points => view._client!.Points;
        public SeizeGroundInfo? SeizeLayout => view._client?.Scoreboard?.Seize;
        public bool SeizeGround => view.SeizeGround;
        public string MapName => view._client?.MapName ?? "";
        public TimeSpan CurrentInterpolationDelay => view._client?.CurrentInterpolationDelay ?? TimeSpan.Zero;
        public IEnumerable<ProjectileView> NewestProjectiles() => view._client?.NewestProjectiles() ?? [];

        /// <summary>
        /// The interpolated projectiles: within a brain step computed once (the render clock moves within a tick; the shadow brain sees the live one's list); any other
        /// call (outside a step: the client's own code through the forwarders) reads them fresh, as before the move.
        /// </summary>
        public IReadOnlyList<ProjectileView> Projectiles()
        {
            if (!view._botStepping) return view._client?.Projectiles() ?? [];
            if (_projectilesStamp != view._botTickStamp) { _projectilesStamp = view._botTickStamp; _projectiles = view._client?.Projectiles() ?? []; }
            return _projectiles;
        }

        public float Yaw { get => view._yaw; set => view._yaw = value; }
        public float Pitch { get => view._pitch; set => view._pitch = value; }
        public float GrenadesOutFor => view._grenadesOutFor;
        public int OwnShotsFired => view._hipOwnShots;
        public ContentExtras MatchExtras => view.MatchExtras;
        public StimSpec? StimOf(PlayerState s) => view.StimOf(s);
        public string ClassLabel(int playerClass) => view.ClassLabel(playerClass);
        public SmBots.BotWork Work => view._botWork;
    }

    /// <summary>The shadow brain's world: the live world with a view (yaw, pitch) of its own, set to the live view's before each of its steps.</summary>
    sealed class ShadowWorld(BotWorld live) : SmBots.IBotWorldView
    {
        public float Yaw { get; set; }
        public float Pitch { get; set; }
        public int PlayerId => live.PlayerId;
        public PlayerState Predicted => live.Predicted;
        public IReadOnlyList<PlayerState> Others => live.Others;
        public double ConnectedFor => live.ConnectedFor;
        public SimConfig? Config => live.Config;
        public CollisionWorld? Collision => live.Collision;
        public MatchView Match => live.Match;
        public IReadOnlyList<ControlPointView> Points => live.Points;
        public SeizeGroundInfo? SeizeLayout => live.SeizeLayout;
        public bool SeizeGround => live.SeizeGround;
        public string MapName => live.MapName;
        public TimeSpan CurrentInterpolationDelay => live.CurrentInterpolationDelay;
        public IEnumerable<ProjectileView> NewestProjectiles() => live.NewestProjectiles();
        public IReadOnlyList<ProjectileView> Projectiles() => live.Projectiles();
        public float GrenadesOutFor => live.GrenadesOutFor;
        public int OwnShotsFired => live.OwnShotsFired;
        public ContentExtras MatchExtras => live.MatchExtras;
        public StimSpec? StimOf(PlayerState s) => live.StimOf(s);
        public string ClassLabel(int playerClass) => live.ClassLabel(playerClass);
        public SmBots.BotWork Work => live.Work;
    }

    /// <summary>The client's test aids and added content's bot, as the brain asks for them (in the old BotInput's order).</summary>
    sealed class BotHooks(NetworkView view) : SmBots.IBotHooks
    {
        /// <summary>This tick's answers, which the shadow brain is handed (<see cref="ShadowHooks"/>).</summary>
        public (uint Tick, PlayerInput? Input)? LastTestAid, LastAdded;

        public PlayerInput? TestAidInput(PlayerState me, uint tick)
        {
            var input = TestAid(me, tick);
            LastTestAid = (tick, input);
            return input;
        }

        PlayerInput? TestAid(PlayerState me, uint tick)
        {
            if (view.JumpTest && view.JumpTestInput(me, tick) is { } jumpTest) return jumpTest;   // M44 test aid (NetworkView.JumpFx.cs)
            if (view.SlashTestInput(me, tick) is { } slashTest) return slashTest;   // feel/jump-pack test aid --slash-test (NetworkView.DashAttack.cs)
            if (view.HopRulesTestInput(me, tick) is { } hopRules) return hopRules;   // sim/hop-rules capture aid --hop-rules-test (NetworkView.HopRulesTest.cs)
            if (view.StabRecoveryTestInput(me, tick) is { } stabRecovery) return stabRecovery;   // sim/stab-release capture aid --stab-recovery-test (NetworkView.StabRecoveryTest.cs)
            if (view.SwingTestInput(me, tick) is { } swingTest) return swingTest;   // anim/hammer-swing test aid --swing-test (NetworkView.SwingTest.cs)
            if (view.StompMoveTestInput(me, tick) is { } stompMove) return stompMove;   // anim/stomp-to-move test aid --stomp-move-test (NetworkView.StompMoveTest.cs)
            if (view.CameraTestInput(me, tick) is { } cameraTest) return cameraTest;   // camera/hop-and-walls test aid --camera-test (NetworkView.CameraTest.cs)
            if (view.FireTestInput(me, tick) is { } fireTest) return fireTest;   // fx/weapon-visuals capture aid --fire-test (NetworkView.PowerGlow.cs)
            if (view.CarryTestInput(me, tick) is { } carry) return carry;   // anim/relic-bolter-idle test aid --carry-test (NetworkView.CarryTest.cs)
            if (view.DashWallTestInput(me, tick) is { } dashWall) return dashWall;   // feel/jump-pack-2 test aid --dash-wall-test (NetworkView.DashAttack.cs)
            return null;
        }

        public PlayerInput? AddedContentInput(PlayerState me, uint tick)
        {
            PlayerInput? input = null;
            view.ExtrasBotInput(me, tick, ref input);   // EXTRAS-HOOK: added content's own bot (Musou Mode co-op: deploy with the team, fight the Orks)
            LastAdded = (tick, input);
            return input;
        }

        public void NotePress(float viewYaw, string keys, float speed) => view.NotePress(viewYaw, keys, speed);
        public void Phase(string phase) => view.Ph(phase);
    }

    /// <summary>
    /// The shadow brain's hooks (<c>--bot-shadow</c>): the live brain's answers of the same tick (null when the live brain did not ask this tick), no press log, no
    /// phase marks; so a test aid, added content's bot and the charge-direction log run once a tick.
    /// </summary>
    sealed class ShadowHooks(BotHooks live) : SmBots.IBotHooks
    {
        public PlayerInput? TestAidInput(PlayerState me, uint tick) => live.LastTestAid is { } a && a.Tick == tick ? a.Input : null;
        public PlayerInput? AddedContentInput(PlayerState me, uint tick) => live.LastAdded is { } a && a.Tick == tick ? a.Input : null;
    }
}
