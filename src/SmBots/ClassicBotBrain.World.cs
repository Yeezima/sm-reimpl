using System;
using SmContent;
using SmNet;
using SmSim;

namespace SmBots;

/// <summary>
/// The Classic PvP test bot's brain (test script, **ours**, not the original AI), one per bot. T1 of the Phase B design (2026-11-25) moved it without a behaviour change
/// from the Godot client's NetworkView (client/NetworkView.Bot*.cs, NetworkView.Nav.cs and the bot parts of NetworkView.cs, Jump.cs, Seize.cs, Evade.cs, Reactions.cs and
/// DashAttack.cs) into this engine-free project, so the dedicated server can drive the same brain. Its partial files keep their old names. The moved code reads the world
/// under its old names, which this file maps: <c>_client</c> is the <see cref="IBotWorldView"/>, <c>_yaw</c> and <c>_pitch</c> its view, <c>GD</c> the log sink
/// (<see cref="BotLog"/>), the <c>--bot-*</c> flags come from <see cref="BotArgs"/>, the client's test aids from <see cref="IBotHooks"/>, and what the bot does around its
/// fight from <see cref="IBotStrategy"/>.
/// <para>
/// Each input tick the host calls <see cref="Step"/> (the old <c>BotApplyAim(BotInput(tick))</c>), applies its own test-aid stages, then <see cref="Hold"/> (the grenade
/// safety's hold on the final input); <see cref="NoteDead"/> while the bot is dead (the old input sampling skipped the brain then). The host polls the
/// <see cref="BotWork"/> once per tick before the brain.
/// </para>
/// </summary>
public sealed partial class ClassicBotBrain
{
    /// <summary>Metres above the target's feet the bot aims at (the chest; placeholder): the Classic <see cref="IBotWorldView.AimHeightOf"/>.</summary>
    public const float BotTargetHeight = 1.2f;
    const float BotMeleeDistance = 3f;          // metres (horizontal) within which the bot uses melee instead of firing (test script)
    const float BotMeleeHeight = 1.5f;          // metres (vertical) a melee target may be above or below the bot (test script)
    const float MaxPitchDown = 1.4f;            // radians below level (the client's view limit; the sim's PlayerInput.MinPitch is the same)
    const float MaxPitchUp = 89.9f * MathF.PI / 180f;   // the client's view limit (sim/roll-fire-pitch)

    // ---- M12/M13: charged fire, detonation and throws (test script; moved from NetworkView.cs) ----
    const float BotDetonateAfter = 1.2f;        // seconds with grenades out after which the bot detonates them anyway (test script)
    const double BotChargeCycle = 12.0;         // charge weapons: seconds per cycle of tapped basic shots and one charge window (test script)
    const double BotChargeWindow = 4.0;         // charge weapons: seconds at the end of each cycle in which it holds for charged shots (test script)
    const float BotThrowMin = BotGrenadeSafety.OldThrowMin;   // metres (horizontal) beyond which the bot throws stims and blind grenades (all items with --old-bot-throws; test script)
    const double BotThrowEvery = 4.0;           // seconds between the bot's throw presses at most (test script)
    const float BotThrowShort = BotGrenadeSafety.OldThrowShort;   // metres short of the target the bot aimed a throw at the ground (blind grenades and --old-bot-throws; test script)
    /// <summary>The bot put the primary away because it overheated; it switches back once the primary is released.</summary>
    bool _botCoolingPrimary;
    bool _botTap;                               // the bot's charge-weapon press is a tap (released on the next tick: the basic shot)
    int _detonateRequests;
    double _botLastThrow = double.NegativeInfinity;

    readonly IBotWorldView _world;
    readonly IBotHooks _hooks;
    readonly IBotStrategy _strategy;
    readonly BotArgs _args;
    readonly ClassicBotOptions _options;
    readonly BotLog _log;

    /// <param name="world">What the bot reads of the world, and its view.</param>
    /// <param name="args">The command-line test aids (<see cref="BotArgs.None"/> on a server).</param>
    /// <param name="options">The client's constructor settings (<c>--bot-melee</c> and so on).</param>
    /// <param name="log">Where its log lines go (<see cref="BotLog.None"/>: dropped).</param>
    /// <param name="hooks">The client's test aids and added content's bot (<see cref="IBotHooks.None"/>).</param>
    /// <param name="strategy">What it does around the fight; null: the Classic strategy.</param>
    public ClassicBotBrain(IBotWorldView world, BotArgs args, ClassicBotOptions options, BotLog log, IBotHooks? hooks = null, IBotStrategy? strategy = null)
    {
        _world = world;
        _args = args;
        _options = options;
        _log = log;
        _hooks = hooks ?? IBotHooks.None;
        _strategy = strategy ?? ClassicBotStrategy.Instance;
    }

    /// <summary>The world under the moved code's old name (it was NetworkView's GameClient).</summary>
    IBotWorldView _client => _world;
    /// <summary>The log sink under the moved code's old name (Godot's GD).</summary>
    BotLog GD => _log;
    SimConfig? _config => _world.Config;
    IReadOnlyList<PlayerState> _lastOthers => _world.Others;
    double _connectedFor => _world.ConnectedFor;
    float _yaw { get => _world.Yaw; set => _world.Yaw = value; }
    float _pitch { get => _world.Pitch; set => _world.Pitch = value; }
    float _grenadesOutFor => _world.GrenadesOutFor;
    int _hipOwnShots => _world.OwnShotsFired;
    ContentExtras MatchExtras => _world.MatchExtras;
    bool SeizeGround => _world.SeizeGround;
    /// <summary>The brain exists only for a bot (the moved code's <c>_bot</c> tests stay true).</summary>
    readonly bool _bot = true;
    string _gameDir => _options.GameDir;
    bool _botMelee => _options.Melee;
    bool _botFire => _options.Fire;
    bool BotFireWalls => _options.FireWalls;
    float _botThrowMax => _options.ThrowMax;
    bool _botJumpFree => _options.JumpFree;
    bool Lean => _options.Lean;
    /// <summary><c>--bot-goal</c>: one point (see <see cref="ParseRoute"/>) the bot walks to and holds, fighting what it sees.</summary>
    public string? BotGoal => _options.Goal;
    /// <summary><c>--bot-route</c>: points the bot walks through in order, then again from the first (fighting what it sees).</summary>
    public string? BotRoute => _options.Route;

    StimSpec? StimOf(PlayerState s) => _world.StimOf(s);
    float AimHeightOf(PlayerState target) => _world.AimHeightOf(target);
    string ClassLabel(int playerClass) => _world.ClassLabel(playerClass);

    int _coverJobs, _approachJobs;

    /// <summary>
    /// A job off the frame loop (was <c>Task.Run(work, cancel)</c>): run through the world's <see cref="BotWork"/> under this brain's lane, this map's generation and
    /// <paramref name="what"/>, so it finishes at a tick boundary and a shadow brain asking the same shares it.
    /// </summary>
    System.Threading.Tasks.Task<T> Job<T>(string what, Func<T> work, System.Threading.CancellationToken cancel) =>
        _world.Work.Run($"{_options.WorkLane}|{_navGeneration}|{what}", work, cancel);

    bool CmdFlag(string name) => _args.Flag(name);
    string? CmdArg(string name) => _args.Arg(name);
    string[] CmdArgs(string name, int count) => _args.Args(name, count);

    // The client's test-aid flags the brain reads (their own code stays in the client).
    /// <summary>feel/melee-charge-2 test aid <c>--bot-heavy-chain</c> (with <c>--bot-melee</c>): the bot's melee is F, F (the backhand, then the stomp in its Y,Y window).</summary>
    bool BotHeavyChain => CmdFlag("bot-heavy-chain");
    /// <summary><c>--bot-charge-all</c> (fx/tracers-impact test aid): a charge weapon always charges.</summary>
    bool BotChargeAll => CmdFlag("bot-charge-all");
    /// <summary><c>--bot-brace</c> (fidelity gap 7 test aid): a vent weapon vents its overheat instead of being switched away.</summary>
    bool BotBraceVent => CmdFlag("bot-brace");
    /// <summary><c>--barrel-test</c> (fx/barrel-origin): the weapon asked for stays in hand.</summary>
    bool BarrelTest => CmdFlag("barrel-test");
    /// <summary><c>--bot-dash-attack</c> (test aid; not original behaviour).</summary>
    bool BotDashAttack => CmdFlag("bot-dash-attack");
    bool BotChargeDirs => CmdFlag("bot-charge-dirs");
    bool BotDashDirs => CmdFlag("bot-dash-dirs");
    /// <summary>Seconds the <c>--bot-charge-dirs</c> bot runs with W (no sprint) before holding Shift for F (test aid, ours).</summary>
    const float ChargeDirsRunUp = 0.5f;
    static readonly (float X, string Name)[] ChargeDirs = [(0f, "Shift+W"), (-1f, "Shift+W+A"), (1f, "Shift+W+D")];
    static readonly (float X, float Y, string Name)[] DashDirs =
        [(0f, 1f, "W"), (1f, 1f, "W+D"), (1f, 0f, "D"), (1f, -1f, "S+D"), (0f, -1f, "S"), (-1f, -1f, "S+A"), (-1f, 0f, "A"), (-1f, 1f, "W+A")];
    void NotePress(float view, string keys, float speed) => _hooks.NotePress(view, keys, speed);

    /// <summary>The HUD name of a weapon (the client's WeaponLabel); the single-weapon fallback ("default") is the bolter's values.</summary>
    static string WeaponLabel(WeaponSpec? weapon) => BotLabels.Weapon(weapon);
    /// <summary>Thrown item state for the log (the client's ThrowText).</summary>
    string ThrowText(PlayerState me) => BotLabels.Throw(_config!, me);
    /// <summary>The client's frame-profile phase marker (its watchdog); nothing on a server.</summary>
    void Ph(string phase) => _hooks.Phase(phase);

    /// <summary>
    /// The nearest living enemy as last shown, or null: with teams (the own player has a side) allies are skipped; without teams everyone is a target
    /// (a copy of the client's NearestLivingTarget, for <c>--bot-fire-walls</c>).
    /// </summary>
    PlayerState? NearestLivingTarget(PlayerState me)
    {
        PlayerState? best = null;
        float bestDistance = float.MaxValue;
        foreach (var other in _lastOthers)
        {
            if (!other.Alive) continue;
            if (me.Team != Team.None && !World.Hostile(me.Team, other.Team)) continue;
            float distance = System.Numerics.Vector3.DistanceSquared(other.Position, me.Position);
            if (distance < bestDistance) { bestDistance = distance; best = other; }
        }
        return best;
    }

    // ---- the host's calls ----

    /// <summary>
    /// The bot's input for <paramref name="tick"/> (alive and in play; the host's other test aids first): the brain's decision with this tick's aim error
    /// (the old <c>BotApplyAim(BotInput(tick))</c>). It reads and turns the world's view.
    /// </summary>
    public PlayerInput Step(uint tick) => BotApplyAim(BotInput(tick));

    /// <summary>The grenade safety's hold on the host's final input (the old <c>BotGrenadeHold</c>; Classic PvP).</summary>
    public PlayerInput Hold(PlayerInput input) => BotGrenadeHold(input);

    /// <summary>While the bot is dead (the old input sampling skipped the brain then): the next life is counted when it lives again, and a push or retreat ends.</summary>
    public void NoteDead() => BotNoteDead();

    /// <summary>Per frame (the client's frame loop): the bot's own death resets its path, and the "nav:", stuck, wait and "aim:" log lines.</summary>
    public void Frame(PlayerState me)
    {
        if (!me.Alive) _navAgent?.Reset();
        LogNav(me);
    }

    /// <summary>A hit report of the bot's own shots (aim and tactics statistics).</summary>
    public void NoteHits(HitReport hits) => BotNoteHits(hits);

    /// <summary>The own predicted release of a thrown item (grenade safety: its grenade is a hazard until it goes off).</summary>
    public void NoteOwnRelease(PlayerState me) => BotNoteOwnRelease(me);

    /// <summary>The bot's last decision, for its 2 s log line (and set by the client's test aids that steer it: <c>--bot-pickups</c>).</summary>
    public string Note { get => _botNote; set => _botNote = value; }
    /// <summary>The bot's last Seize Ground decision, for the match log.</summary>
    public string SeizeNote => _botSeizeNote;
    /// <summary>Detonation requests so far (the 2 s log line).</summary>
    public int DetonateRequests => _detonateRequests;
    /// <summary>The navigation's Jump button this tick (a link's press or hold), for an added content's bot that steers with <see cref="BotSteer"/>.</summary>
    public InputButtons NavJump => _navJump;
    /// <summary>The path follower of the bot's own side (null before the graph is built).</summary>
    public NavAgent? NavAgent => _navAgent;
    /// <summary>
    /// Seam (T1, ours; no Classic use): whether the bot is in the middle of a move it must not be pulled out of: a navigation jump in flight, a jump or pound, a
    /// charge or dash attack, a melee attack, a throw or an evade. A strategy with a leash (Warfront, T4) waits for it before it turns the bot round.
    /// </summary>
    public bool CommittedMove(PlayerState me) =>
        _navAgent is { JumpInProgress: true } || me.Jump != JumpPhase.None || me.Rush != RushPhase.None || me.MeleeStep > 0 || me.Throwing || Evade.Evading(me);

    /// <summary>Reads <c>--bot-accuracy</c> and <c>--bot-range</c> once (the F6 panel calls it before it reads the flags; the brain does at its first aimed tick).</summary>
    public void ReadAimFlags() => ReadBotAimFlags();

    /// <summary>The aim model's accuracy flag as the client's F6 developer panel reads and sets it (the raw field: null before the flags are read; a set rebuilds the aim model).</summary>
    public float? AccuracyFlag
    {
        get => _botAccuracyFlag;
        set { _botAccuracyFlag = value; _botAim = null; }
    }

    /// <summary>The engagement range flag as the F6 panel reads and sets it (raw, as <see cref="AccuracyFlag"/>).</summary>
    public float? RangeFlag
    {
        get => _botRangeFlag;
        set => _botRangeFlag = value;
    }
}
