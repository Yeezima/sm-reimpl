using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmContent;
using SmNet;
using SmSim;
using Brain = SmBots.ClassicBotBrain;

namespace SmClient;

/// <summary>
/// T1 (Phase B, 2026-11-25): the client's adapter to the Classic bot brain, which moved into the engine-free src/SmBots (<see cref="Brain"/>). The client keeps its
/// test aids and flags and hands the brain its world (<see cref="BotWorld"/>: the GameClient, the shown players, the view's yaw and pitch), its log (GD.Print) and its
/// test-aid hooks (<see cref="BotHooks"/>). Test aid <c>--bot-shadow</c>: a second brain runs beside the live one on the same inputs, its input compared every tick
/// (a "shadow:" log line every 10 s and one line per differing tick, the first 40); <c>--bot-shadow-sync</c> also plans both brains' paths inside the steering call,
/// so thread timing cannot tell them apart.
/// </summary>
public partial class NetworkView
{
    /// <summary><c>--bot-shadow</c> (test aid): the shadow comparison (see the class).</summary>
    static readonly bool BotShadow = CmdFlag("bot-shadow") || CmdFlag("bot-shadow-sync");
    /// <summary><c>--bot-shadow-sync</c>: the shadow comparison with the paths planned inside the steering call (both brains).</summary>
    static readonly bool BotShadowSync = CmdFlag("bot-shadow-sync");
    /// <summary><c>--bot-new-brain</c> (T1 transition): the moved brain plays instead of the client's own copy.</summary>
    static readonly bool BotNewBrain = CmdFlag("bot-new-brain");

    /// <summary>The background jobs of this client's brains (one store: the shadow shares the live brain's jobs).</summary>
    readonly SmBots.BotWork _botWork = new();
    Brain? _botBrain, _botShadowBrain;
    BotWorld? _botWorld;
    ShadowWorld? _botShadowWorld;
    int _botTickStamp;

    /// <summary>The moved brain (created on first use; bots only).</summary>
    Brain BotBrainOf() => _botBrain ??= NewBotBrain(_botWorld ??= new BotWorld(this), GD.Print);

    /// <summary>The shadow brain and its world (own yaw and pitch), or null without <c>--bot-shadow</c>.</summary>
    Brain? BotShadowOf()
    {
        if (!BotShadow) return null;
        _botWorld ??= new BotWorld(this);
        _botShadowWorld ??= new ShadowWorld(_botWorld);
        return _botShadowBrain ??= NewBotBrain(_botShadowWorld, null);
    }

    Brain NewBotBrain(SmBots.IBotWorldView world, Action<string>? print) =>
        new(world, new SmBots.BotArgs(OS.GetCmdlineUserArgs()),
            new SmBots.ClassicBotOptions
            {
                GameDir = _gameDir, Melee = _botMelee, Fire = _botFire, FireWalls = BotFireWalls, ThrowMax = _botThrowMax, JumpFree = _botJumpFree,
                Goal = BotGoal, Route = BotRoute, Lean = Lean, SyncPlans = BotShadowSync, WorkLane = "bot",
            },
            print is null ? SmBots.BotLog.None : new SmBots.BotLog(s => print(s), s => GD.PrintErr(s)),
            new BotHooks(this));

    // ---- the shadow comparison ----

    int _shadowTicks, _shadowSame, _shadowLogged;
    double _shadowLogAt;
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

    bool _shadowDiffer;

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

    /// <summary>The brain's step for this tick (the old <c>BotApplyAim(BotInput(tick))</c>), with the shadow comparison.</summary>
    PlayerInput BotBrainStep(uint tick)
    {
        _botTickStamp++;
        _botWork.Poll();
        float yaw0 = _yaw, pitch0 = _pitch;
        var live = BotNewBrain ? BotBrainOf().Step(tick) : BotApplyAim(BotInput(tick));
        if (BotShadowOf() is { } shadow)
        {
            float liveYaw = _yaw, livePitch = _pitch;
            _botShadowWorld!.Yaw = yaw0; _botShadowWorld.Pitch = pitch0;
            var other = shadow.Step(tick);
            ShadowCompare("step", live, other, liveYaw, livePitch, BotNewBrain ? BotBrainOf().Note : _botNote, shadow.Note);
        }
        return live;
    }

    /// <summary>The grenade safety's hold on the final input (the old <c>BotGrenadeHold</c>), with the shadow comparison.</summary>
    PlayerInput BotBrainHold(PlayerInput input)
    {
        float yaw0 = _yaw, pitch0 = _pitch;
        var live = BotNewBrain ? BotBrainOf().Hold(input) : BotGrenadeHold(input);
        if (BotShadowOf() is { } shadow)
        {
            float liveYaw = _yaw, livePitch = _pitch;
            _botShadowWorld!.Yaw = yaw0; _botShadowWorld.Pitch = pitch0;
            var other = shadow.Hold(input);
            ShadowCompare("hold", live, other, liveYaw, livePitch, BotNewBrain ? BotBrainOf().Note : _botNote, shadow.Note);
            ShadowTickDone();
        }
        return live;
    }

    void BotBrainNoteDead()
    {
        if (BotNewBrain) BotBrainOf().NoteDead(); else BotNoteDead();
        BotShadowOf()?.NoteDead();
    }

    void BotBrainFrame(PlayerState me)
    {
        if (BotNewBrain) BotBrainOf().Frame(me);
        else { if (!me.Alive) _navAgent?.Reset(); LogNav(me); }
        BotShadowOf()?.Frame(me);
    }

    void BotBrainNoteOwnRelease(PlayerState me)
    {
        if (BotNewBrain) BotBrainOf().NoteOwnRelease(me); else BotNoteOwnRelease(me);
        BotShadowOf()?.NoteOwnRelease(me);
    }

    /// <summary>The moved brain's and the shadow's share of a hit report (called from the client copy's BotNoteHits during the transition).</summary>
    void BotBrainNoteHits(HitReport hits)
    {
        if (BotNewBrain) BotBrainOf().NoteHits(hits);
        BotShadowOf()?.NoteHits(hits);
    }

    /// <summary>The moved brain's and the shadow's map load (called from the client copy's PrepareNavigation during the transition; bots only).</summary>
    void BotBrainPrepareNavigation(MapContent map, CollisionWorld world)
    {
        _botWork.Clear();
        if (BotNewBrain) BotBrainOf().PrepareNavigation(map, world);
        BotShadowOf()?.PrepareNavigation(map, world);
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

        /// <summary>The interpolated projectiles, computed once per bot tick (the render clock moves within a tick; the shadow brain sees the live one's list).</summary>
        public IReadOnlyList<ProjectileView> Projectiles()
        {
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
        public PlayerInput? TestAidInput(PlayerState me, uint tick)
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

        public PlayerInput? ExtrasBotInput(PlayerState me, uint tick)
        {
            PlayerInput? input = null;
            view.ExtrasBotInput(me, tick, ref input);   // EXTRAS-HOOK: added content's own bot (Musou Mode co-op: deploy with the team, fight the Orks)
            return input;
        }

        public void NotePress(float viewYaw, string keys, float speed) => view.NotePress(viewYaw, keys, speed);
        public void Phase(string phase) => view.Ph(phase);
    }
}
