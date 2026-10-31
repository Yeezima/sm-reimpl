using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using Godot;
using SmContent;
using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// M6 networked view (<c>--connect host[:port]</c>): connects to a dedicated server through <see cref="GameClient"/>,
/// shows one animated marine per player (the local one at its predicted state, others interpolated; dead players are
/// hidden), a placeholder ground plane, a third-person camera that tilts with the aim and the HUD (<see cref="Hud"/>: laid out after
/// the original multiplayer HUD; health and shield bar, ammunition, weapon, heat, reloading, respawn countdown, crosshair
/// at the screen centre, score panel, kill feed). The info text (status, controls, provenance notes) is an overlay that is
/// hidden by default: F1 toggles it, <c>--show-info</c> starts it shown; errors and connection status show as a centred banner.
/// Input: WASD move, Shift sprint, mouse (captured; Esc releases, click recaptures) turns and aims, Left/Right arrows turn, E pick up,
/// left mouse button fires the weapon in hand, the right mouse button (also V; F: the heavy "stun" strike, M28) attacks with the
/// class's melee weapon (all while the mouse is captured), R reloads, X or the mouse wheel switches between primary and
/// pistol. On a charge weapon the left mouse button taps for the normal shot and is held for the charged shot (M31, protocol 20; owner's
/// decision); holding Left Ctrl is aim mode (protocol 12; these bindings are from the user's knowledge of the original); R with grenade-launcher grenades out
/// detonates them. M12 draws projectiles and blasts from the snapshots (<see cref="UpdateProjectiles"/>), shows a charge
/// bar, a pending charged shot and grenades out in the HUD, and a charging glow on any charging player (slim records
/// carry the flag); the bot charges charge weapons and detonates its grenades (<see cref="BotWeaponButtons"/>). All of
/// that drawing is a **placeholder demonstration**.
/// M13 (protocol 8): G throws the equipment item (<c>--equipment frag|blind</c>; "krak" in the files also accepted); the HUD shows the item's count, cooldown
/// and a throw under way, and a self-recharging clip's (the melta's) recharge time; thrown grenades are drawn as small
/// spheres (frag dark green, blind grenade grey), each of a projectile's damaging blasts as a sphere (a lingering one as a disc for
/// its lifetime), and the own melta shots as a translucent cone for 0.15 s (cone shots are not on the wire, so only the
/// own, predicted ones); a throw poses the toss clip (<see cref="SetUpToss"/>); the bot throws at 6–20 m and closes in to
/// melta range. All of it a **placeholder demonstration**.
/// M14 (protocol 9): Space is the jump pack (held: the hold sets the height), melee in the air the ground pound; the
/// jump-pack clips, HUD power readout, impact / landing rings and the bot's jumps are in NetworkView.Jump.cs (placeholder
/// demonstration around original clips).
/// <c>--class &lt;name&gt;</c> asks the server for a multiplayer class (<see cref="ResolveClass"/>); the server's default otherwise.
/// <c>--primary</c>, <c>--pistol</c> and <c>--melee &lt;name&gt;</c> ask for a weapon from that class's choice lists
/// (<see cref="ResolveChoice"/>; <see cref="LoadoutRequest"/>); the class default otherwise.
/// <c>--bot</c> replaces the keyboard with scripted input for automated tests: run and sprint in a circle, then stand;
/// while another living player is visible, aim at the nearest one, fire the weapon in hand (held for full-automatic,
/// pressed on alternate ticks for semi-automatic) and strafe instead, switch weapons when the one in hand is empty or
/// overheated, or, within <see cref="BotMeleeDistance"/> and if the player has a melee combo, tap melee so the combo chains.
/// <c>--bot-melee</c> (test aid) makes the bot run at the target without firing, so that melee is exercised every time.
/// <c>--bot-fire</c> (test aid) makes the bot also fire while no target is visible, so that heat builds up and overheats
/// (on flat ground only: on a map the bot fires only at an enemy it can see within <see cref="BotFightRange"/> and
/// otherwise walks toward the nearest enemy, see NetworkView.Bot.cs).
/// During a melee attack a marine shows its chosen melee weapon's original clip for the step
/// (<see cref="MpClasses.MeleeAnimationsByWeapon"/>, else its class's <see cref="MpClasses.MeleeAnimations"/>) at the
/// simulation's attack time. Movement, animation choice, the HUD (heat readout included) and the camera are a
/// **placeholder demonstration**: animation by speed threshold and attack step only, no blending, no weapon model, no
/// muzzle effects, hit markers from the server's hit reports (<see cref="Hud.ShowHitMarker"/>). The weapons', heat, melee combos' and classes' values themselves come from the install
/// through <see cref="GameplayTuning"/>. Other players arrive slim (protocol 6): only their class, weapon in hand and melee
/// choice are known; their health, shield, ammunition, heat and timers are not known here and not shown.
/// </summary>
public partial class NetworkView : Node3D
{
    public const int DefaultPort = 27015;
    const float RunThreshold = 0.5f;            // m/s horizontal speed above which the run cycle plays
    const float MouseYawPerPixel = 0.0035f;     // radians per pixel; client-side preference, not an original value
    const float KeyYawRate = 2.5f;              // radians per second for the Left / Right arrow keys
    const float MaxPitchDown = 1.4f;            // radians below level; client-side preference, not an original value (the sim's PlayerInput.MinPitch is the same)
    // sim/roll-fire-pitch (the owner 2026-11-01: "all classes should be able to zoom directly upwards into the sky"): the view goes up to 89.9 degrees, just under
    // straight up, so the camera's look-at (world up) and the yaw read back from a view direction (FreshAim) stay defined; the simulation accepts π/2.
    const float MaxPitchUp = 89.9f * MathF.PI / 180f;
    const float BotTargetHeight = 1.2f;         // metres above the target's feet the bot aims at (chest; placeholder)
    const float BotMeleeDistance = 3f;          // metres (horizontal) within which the bot uses melee instead of firing (test script)
    const float BotMeleeHeight = 1.5f;          // metres (vertical) a melee target may be above or below the bot (test script)

    static readonly string[] IdleCandidates = ["SM_NAV_IdleL_S_01", "SM_NAV_Idle_S_01", "SM_NAV_IdleR_S_01"];

    readonly string _gameDir;
    readonly string _target;
    readonly bool _bot;
    readonly int _waitPlayers;
    readonly List<string> _notes = new();
    GameClient? _client;
    IReadOnlyList<ShotView> _frameShots = [];   // this frame's others' shots (GameClient.TakeShots, drained in HipFireBegin), read by effects, sound and hip fire
    UdpClientTransport? _transport;
    SimConfig? _config;
    MarineAssets? _assets;
    string? _idleAnimation;
    /// <summary>Per class (index = <see cref="PlayerState.Class"/>): clip per melee step (index = step - 1) of the class's default combo; null where the clip is unknown or missing from the rig.</summary>
    string?[][] _meleeAnimations = [];
    /// <summary>Per melee wargear (a <see cref="MeleeCombo.Wargear"/>): clip per step, checked like <see cref="_meleeAnimations"/>.</summary>
    readonly Dictionary<string, string?[]> _meleeAnimationsByWeapon = new(StringComparer.OrdinalIgnoreCase);
    readonly LoadoutRequest _loadout;
    byte _requestedPrimary = Protocol.DefaultChoice, _requestedPistol = Protocol.DefaultChoice, _requestedMelee = Protocol.DefaultChoice, _requestedEquipment = Protocol.DefaultChoice;
    /// <summary>The bot put the primary away because it overheated; it switches back once the primary is released.</summary>
    bool _botCoolingPrimary;
    /// <summary>Last seen overheat state per slot (index = <see cref="WeaponSlot"/>), for the bot's log.</summary>
    readonly bool[] _overheatSeen = new bool[2];
    readonly float[] _peakHeat = new float[2];
    int _overheats, _releases;
    /// <summary>Per class: the HUD name of the class and of its melee weapon (placeholder labels, not the original UI text).</summary>
    string[] _classLabels = [];
    string[] _meleeLabels = [];
    MpClasses? _classes;
    readonly string? _className;
    byte _requestedClass = Protocol.ServerDefaultClass;
    int _switches;
    WeaponSlot _lastSlot;
    bool _slotSeen;
    int _meleeSwings;
    int _lastMeleeStep;
    Node3D? _gameSpace;
    Camera3D? _camera;
    Label? _overlay;
    Hud? _hudView;
    /// <summary>The info overlay is shown (F1 toggles it; hidden unless <c>--show-info</c>).</summary>
    bool _showInfo;
    CharacterViewer? _local;
    readonly Dictionary<int, CharacterViewer> _remotes = new();
    string _error = "";
    string _tuningSource = "not loaded";
    StandardMaterial3D? _groundMaterial;
    DirectionalLight3D? _sun;
    Godot.Environment? _environment;
    MapLightingView.FrameStats? _frameStats;
    PerfProbe? _perfProbe;
    MapLightingView.EnvironmentBlender? _blender;
    string? _mapNote;
    readonly bool _showCollision;
    readonly bool _waitMelee;
    readonly bool _botMelee;
    /// <summary>feel/melee-charge-2 test aid <c>--bot-heavy-chain</c> (with <c>--bot-melee</c>): the bot's melee is F, F (the backhand, then the stomp in its Y,Y window).</summary>
    static readonly bool BotHeavyChain = OS.GetCmdlineUserArgs().Contains("--bot-heavy-chain");
    readonly bool _botFire;
    bool _attackSeen;
    bool _switchPending;   // a mouse-wheel notch since the last input sample
    bool _wasAlive;
    float _yaw;
    float _pitch;
    IReadOnlyList<PlayerState> _lastOthers = [];
    bool _cameraPlaced;
    Vector3 _cameraOffset;   // camera position minus the local player's (Godot space), smoothed in anchored views
    double _connectedFor;
    double _elapsed;

    // ---- M12: charged fire, projectiles and blasts (placeholder demonstration) ----
    const double BlastSeconds = 0.4;            // how long a blast sphere grows and fades (client-side, not an original value)
    const float BotDetonateAfter = 1.2f;        // seconds with grenades out after which the bot detonates them anyway (test script)
    const double BotChargeCycle = 12.0;         // charge weapons: seconds per cycle of tapped basic shots and one charge window (test script)
    const double BotChargeWindow = 4.0;         // charge weapons: seconds at the end of each cycle in which it holds for charged shots (test script)
    bool _botTap;                               // the bot's charge-weapon press is a tap (released on the next tick: the basic shot)
    int _ownBasicShots, _ownChargedShots;       // the own predicted shots by kind (bot log)
    readonly double? _screenshotAt;
    readonly bool _waitBlast;
    Node3D? _projectileRoot;
    readonly Dictionary<int, MeshInstance3D> _projectileNodes = new();
    readonly HashSet<int> _liveProjectiles = new();   // M40-T9: reused every frame
    readonly List<int> _goneProjectiles = new();
    readonly Stack<MeshInstance3D> _projectilePool = new();
    readonly Dictionary<string, (Mesh Mesh, Material Material)> _projectileLooks = new();
    readonly List<(MeshInstance3D Node, StandardMaterial3D Material, double Age, float Radius, float Alpha)> _blasts = new();
    readonly Stack<(MeshInstance3D Node, StandardMaterial3D Material)> _blastPool = new();
    readonly HashSet<int> _projectileIdsSeen = new();
    int _projectilesSeen, _chargedProjectilesSeen, _detonationsSeen, _chargedDetonationsSeen;
    int _chargedReleases, _detonateRequests, _remoteChargeStarts;
    readonly Dictionary<int, bool> _remoteCharging = new();
    PlayerState _previousMe;
    bool _previousMeSeen;
    /// <summary>Seconds the own player has had grenades out (for the bot's fallback detonation).</summary>
    float _grenadesOutFor;

    // ---- M13: thrown equipment and the melta cone (placeholder demonstration) ----
    const string DefaultTossAnimation = "SM_RNG_GrenadeToss_01";   // the toss sequencer's clip on the install (docs/discovery.md)
    const double ConeFlashSeconds = 0.15;       // how long a melta cone shot is drawn (client-side, not an original value)
    const float BotThrowMin = 6f;               // metres (horizontal) beyond which the bot throws (test script)
    /// <summary>Metres (horizontal) within which the bot throws: 20, or <c>--bot-throw-max</c> (test aid for big maps).</summary>
    readonly float _botThrowMax;
    const double BotThrowEvery = 4.0;           // seconds between the bot's throw presses at most (test script)
    const float BotThrowShort = 8f;             // metres short of the target the bot aims a throw at the ground (test script)
    /// <summary>The toss clip when the rig has it and it decodes, else null (throws keep the idle/run animation).</summary>
    string? _tossAnimation;
    /// <summary>Remote players' throws: connected time at which their slim Throwing flag rose (the clip is posed from there).</summary>
    readonly Dictionary<int, double> _remoteThrowStart = new();
    /// <summary>Lingering blast areas (melta bomb): a translucent disc for the area's lifetime.</summary>
    readonly List<(MeshInstance3D Node, StandardMaterial3D Material, double Age, double Lifetime)> _areas = new();
    readonly List<(MeshInstance3D Node, double Age)> _coneFlashes = new();
    int _throwsStarted, _throwsReleased, _remoteThrowsSeen, _thrownSeen, _thrownDetonationsSeen, _meltaShots;
    double _botLastThrow = double.NegativeInfinity;

    /// <summary>
    /// True once the view is worth capturing: connected, enough players visible, settled for a few seconds and, with
    /// <c>--wait-melee</c>, a melee attack shown past its hit time (or given up).
    /// </summary>
    public bool ReadyForScreenshot =>
        (_client?.Status == ClientStatus.Connected && 1 + _remotes.Count >= _waitPlayers && _connectedFor >= Math.Max(4.0, _screenshotAt ?? 0.0)
         && (!_waitMelee || _attackSeen) && (!_waitBlast || BlastWorthCapturing) && (!_waitJump || JumpWorthCapturing) && (!WaitMatchEnd || MatchEndWorthCapturing) && FallWorthCapturing && UpperWorthCapturing) ||
        _client?.Status is ClientStatus.Rejected or ClientStatus.Disconnected or ClientStatus.NoAnswer || _error != "" || _elapsed > Math.Max(WaitMatchEnd ? 600.0 : 30.0, (_screenshotAt ?? 0.0) + 25.0);

    /// <summary>
    /// <c>--wait-blast</c>: a blast and a projectile (or a melta cone) drawn together in the camera's view; 8 s after <c>--screenshot-at</c> a blast alone; 15 s
    /// after it any projectile.
    /// </summary>
    bool BlastWorthCapturing
    {
        get
        {
            double since = _connectedFor - (_screenshotAt ?? 4.0);
            bool InView(Node3D n) => _camera is not null && n.IsInsideTree() && _camera.IsPositionInFrustum(n.GlobalPosition);
            // A melta cone flash counts as a projectile (M13).
            bool blast = _blasts.Any(b => InView(b.Node)), projectile = _projectileNodes.Values.Any(InView) || _coneFlashes.Any(f => InView(f.Node));
            return (blast && (projectile || since >= 8.0)) || (projectile && since >= 15.0);
        }
    }

    /// <summary><c>--ork-data</c> (Ork O5 test aid): the Ork Boy with the data's values, as a server started with <c>--ork-data</c> (else the config hashes differ).</summary>
    static readonly bool OrkData = OS.GetCmdlineUserArgs().Contains("--ork-data");

    /// <summary>
    /// <c>--content &lt;profile&gt;</c> (docs/added-content.md): the added content to play, as the server's (<c>faithful</c>, <c>extended</c> or a list such as
    /// <c>orks,gore</c>; default <see cref="ContentProfiles.Default"/>, the server's default). Another profile than the server's is refused (config hash).
    /// </summary>
    internal static ContentExtras ContentArg()
    {
        // Added content E2: a match joined from the front end (or with a profile) plays the profile the lobby menu showed (seeded from --content, else the saved choice).
        if (SmClient.FrontEnd.FrontEndLaunch.Current is { } joined) return joined.Content ?? joined.Services.Content;
        var args = OS.GetCmdlineUserArgs();
        int i = Array.IndexOf(args, "--content");
        if (i < 0) return ContentProfiles.Default;
        string? value = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[i + 1] : null;
        return ContentProfiles.TryParse(value, out var extras, out string error) ? extras : throw new ArgumentException(error);
    }

    /// <param name="screenshotAt">With a screenshot: not before this many seconds after connecting (<c>--screenshot-at</c>).</param>
    /// <param name="waitBlast">With a screenshot: also wait for a blast (<see cref="BlastWorthCapturing"/>; <c>--wait-blast</c>).</param>
    public NetworkView(string gameDir, string target, bool bot, int waitPlayers, bool showCollision = false, bool waitMelee = false, bool botMelee = false,
                       string? className = null, LoadoutRequest? loadout = null, bool botFire = false, double? screenshotAt = null, bool waitBlast = false,
                       float botThrowMax = 20f, bool waitJump = false, bool botJumpFree = false)
    {
        _waitJump = waitJump; _botJumpFree = botJumpFree;
        _gameDir = gameDir; _target = target; _bot = bot; _waitPlayers = Math.Max(1, waitPlayers); _showCollision = showCollision;
        _waitMelee = waitMelee; _botMelee = botMelee; _className = className; _loadout = loadout ?? new LoadoutRequest(); _botFire = botFire;
        _screenshotAt = screenshotAt; _waitBlast = waitBlast; _botThrowMax = botThrowMax;
    }

    public NetworkView() : this("", "", false, 1) { }

    public override void _Ready()
    {
        DemoReadArgs();   // M40 T0: the demo flags, read once
        StartJitPrewarm();   // M40-T9: compiles the client's methods in the background (NetworkView.Prewarm.cs)
        // Game space (Z up) to Godot (Y up): x → x, y → −z, z → y. Characters live inside it, so server states need no conversion.
        _gameSpace = new Node3D { Name = "GameSpace", Basis = new Basis(new Vector3(1, 0, 0), new Vector3(0, 0, -1), new Vector3(0, 1, 0)) };
        AddChild(_gameSpace);
        _projectileRoot = new Node3D { Name = "Projectiles" };
        _gameSpace.AddChild(_projectileRoot);
        AddGroundCameraAndLights();
        var layer = new CanvasLayer();
        // Top left, wrapped short of the match banner and kill feed on the right (M15).
        _overlay = new Label { Position = new Vector2(12, 8), Size = new Vector2(820, 0), AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _overlay.AddThemeFontSizeOverride("font_size", 14);
        _overlay.AddThemeColorOverride("font_outline_color", Colors.Black);
        _overlay.AddThemeConstantOverride("outline_size", 3);
        _showInfo = ShowInfo && !NoOverlay;
        _overlay.Visible = _showInfo;
        layer.AddChild(_overlay);
        AddHud(layer);
        AddMatchUi(layer);
        AddSeizeUi(layer);
        layer.Visible = !NoHud;
        AddChild(layer);
        AddVignette();
        AddDamageFlash();   // M38
        SetUpDecalLayers();   // M38

        try
        {
            var endpoint = DemoPlayPath is null ? ParseEndpoint(_target) : null;   // M40 T0: a replayed demo needs no server
            // --content: the server's profile (the Ork Boy as class 6 only with the Orks; Parity, --ork-data on both for the data's values), so the config hashes agree.
            var content = ContentArg();
            GD.Print($"content: {ContentProfiles.Describe(content)}");
            MemStep("start"); var tuning = GameplayTuning.LoadForMatch(_gameDir, content, OrkData); MemStep("tuning");
            _config = WithPlayerCollisionArg(tuning.Config);   // sim/player-collision: --no-player-collision (NetworkView.Bodies.cs)
            _tuningSource = tuning.Placeholders.Any(p => p is nameof(SimConfig.Damage) or nameof(SimConfig.ClipSize) or nameof(SimConfig.HealthMax) or nameof(SimConfig.MeleeSteps) or nameof(SimConfig.Classes))
                ? "partly placeholders (see server log)" : "from the install";
            _notes.AddRange(tuning.Notes);
            _assets = MarineAssets.Load(_gameDir, _notes); MemStep("marine assets");
            if (!Lean) StartLooks();   // customizer T7 (NetworkView.Appearance.cs); perf/lean-bots: a lean bot builds no looks (NetworkView.Lean.cs)
            _idleAnimation = _assets.FindAnimation(IdleCandidates, "Idle");
            if (_idleAnimation is null) _notes.Add("no idle animation found; the run cycle is used throughout");
            if (_bot) { var aimClips = _assets.Model.Rig.AnimationNames.Where(n => n.Contains("aim", StringComparison.OrdinalIgnoreCase)).ToList(); GD.Print($"  rig animations with 'aim' in the name: {aimClips.Count}: {string.Join(", ", aimClips.Take(15))}"); }
            _classes = tuning.Classes;
            SetUpClasses(tuning); MemStep("classes");
            SetUpToss();
            SetUpReactions();   // M28: heavy strike and reaction clips
            RagdollDefinition();   // M23: read the ragdoll now, not at the first death
            SetUpJump();
            SetUpAim(); MemStep("toss, reactions, ragdoll, jump, aim");
            SetUpHipFire();   // M27 (loads in the background)
            SetUpLocomotion();   // M36 (loads in the background)
            SetUpFall();         // animation pass 1 (loads in the background)
            SetUpUpperBody();    // animation pass 2 (loads in the background)
            MemStep("hip fire, locomotion, fall, upper body (started)");
            SetUpWeapons();   // M24
            SetUpFx();   // M25b
            SetUpSound();     // M26b (loads in the background)
            SetUpPickups();   // fidelity gap 1: the drops' looks and prompts (loads in the background)
            SetUpBraceVent();   // fidelity gap 7: the brace's and the vent's clips and texts (loads in the background)
            LoadCameraRig(); MemStep("weapons, fx, sound, pickups, brace, camera");
            if (_className is not null)
            {
                int index = ResolveClass(_className, _config, _classes);
                if (index >= 0) { _requestedClass = (byte)index; GD.Print($"--class '{_className}': requesting class {index} '{_classLabels[index]}'"); }
                else _notes.Add($"--class '{_className}' matches no class ({string.Join(", ", _classLabels.Select((l, i) => $"{i} '{l}'"))}); the server's default is used");
            }
            ChooseLoadout();
            ulong fingerprint = ContentFingerprint.Compute(_gameDir);
            var (clientTransport, clientClock, clientNonce) = DemoNetwork(endpoint, fingerprint);   // M40 T0: live UDP (recording on request) or a replayed demo
            var clientOptions = new ClientOptions
            {
                LoadMap = LoadMap, RequestedClass = _requestedClass,
                RequestedPrimary = _requestedPrimary, RequestedPistol = _requestedPistol, RequestedMelee = _requestedMelee,
                RequestedEquipment = _requestedEquipment, RequestedSide = RequestedSide,
                SmoothRenderClock = !OldRenderClock,
                // anim/feel-pass: a replay's Welcome comes at its recorded time, however long the recording's map load took (a demo recorded beside other loading clients waited
                // more than the 10 s live timeout for it, and the replay gave up with NoAnswer); the live timeout stays for live connections.
                ConnectTimeout = DemoPlayPath is not null ? TimeSpan.FromMinutes(10) : ConnectFailure.DefaultTimeout,
                MeleeLock = !NoMeleeLock,   // feel/melee-lock (NetworkView.MeleeLock.cs)
                Bot = _bot,   // protocol 37: the Hello marks a test bot (the server's --bot-damage-scale)
                Observer = Observe,   // protocol 46: a spectator (NetworkView.Observer.cs)
            };
            // The front end's perks, looks and emblems (customizer, T8); a replayed demo keeps its recorded Hello.
            if (_demoPlayback is null) clientOptions = SmClient.FrontEnd.FrontEndLaunch.WithProfile(clientOptions);
            if (_demoPlayback is null) clientOptions = WithPerkArgs(clientOptions);   // perks/inert-2 test aid: --perks a,b
            SetOwnLooks(clientOptions);   // customizer T7
            _client = new GameClient(_config, fingerprint, clientTransport, clientClock, clientOptions, nonce: clientNonce);
            DemoPrerollInReady();   // M40 T0: a replayed demo takes the Challenge (loads the map) and waits for the background loads before the first frame
            if (RequestedSide != Team.None) GD.Print($"--side: requesting the {SideName(RequestedSide)} side (the server may balance onto the other side)");
            GD.Print($"connecting to {endpoint}; tuning from {tuning.Source}; config {_config.Hash():X16}; idle animation {_idleAnimation ?? "none"}");
            for (int i = 0; i < _config.Classes.Count; i++)
            {
                GD.Print($"  class {i} '{_classLabels[i]}': {LoadoutText(i)}; melee animations {string.Join(", ", _meleeAnimations[i].Select(a => a ?? "none"))}");
                GD.Print($"    choices: primary [{string.Join(", ", ChoiceLabels(i, WeaponSlot.Ranged))}], pistol [{string.Join(", ", ChoiceLabels(i, WeaponSlot.Pistol))}], melee [{string.Join(", ", MeleeChoiceLabels(i))}], equipment [{string.Join(", ", EquipmentChoiceLabels(i))}]");
            }
            GD.Print($"  throw animation {_tossAnimation ?? "none"} (release {_config.ThrowRelease:F3} s, end {_config.ThrowDuration:F3} s)");
            GD.Print($"  jump animations: {JumpClipsText()}");
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            GD.PrintErr("network view: " + ex.Message);
            if (DemoPlayPath is not null) GetTree().Quit(1);   // a replay that cannot start must not sit there
        }
        foreach (var n in _notes) GD.Print("  note: " + n);
        if (!_bot && !ModelViewer.TestWindow && _demoPlayback is null && _error == "") Input.MouseMode = Input.MouseModeEnum.Captured;
        ControlsInput.Ensure();   // ui/keybinds: the player's bindings are in the InputMap before the first input
        PadSetup();   // controller (NetworkView.Gamepad.cs)
    }

    public override void _ExitTree()
    {
        // Tell the server right away (best effort); otherwise it would drop this player only after its timeout.
        PadTeardown();
        _watchdog?.Dispose();
        _watchdog?.Profile?.PrintSummary();   // M40-T9
        _perfProbe?.PrintSummary();   // perf/pass-1 (--perf-probe)
        if (_watchdog?.Profile is not null) GD.Print($"profile: {HeavyReport()}; {_jit?.Report ?? "jit prewarm off"}");
        _jit?.Stop(); _animationCancel.Cancel();
        LooksExit();   // customizer T7: the shared assets must not keep asking this view for emblems
        TurnAimProbeFinal();   // M40-T5 (--anim-probe): the barrel-against-aim lines
        DemoClose();
        _client?.Leave();
        _transport?.Dispose();
        _transport = null;
    }

    public override void _UnhandledInput(InputEvent e)
    {
        // F1 toggles the info overlay (also on a bot or spectator window; --no-overlay keeps it off). Not while the customizer is
        // open: a key pressed there must not switch on the overlay behind it (owner's report, 2026-10-28).
        if (e is InputEventKey { Pressed: true, Echo: false, PhysicalKeycode: Key.F1 } && !NoOverlay && !PickerBusy)
        {
            _showInfo = !_showInfo;
            if (_overlay is not null) _overlay.Visible = _showInfo;
            return;
        }
        if (Observe) { ObserverInput(e); return; }   // protocol 46: a spectator's keys and mouse (NetworkView.Observer.cs)
        if (_bot) return;
        PickupKey(e);   // fidelity gap 1: latch an E tap between input samples
        if (LoadoutInput(e)) return;   // customizer T7: C on the death screen; while the picker is open nothing else sees the mouse and keys
        LatencyProbeEvent(e);   // feel/controls test aid (--latency-probe): the delivery frame
        ControlEvent(e);   // feel/controls: presses and releases for the tap latch and the early tick (NetworkView.Controls.cs)
        if (e is InputEventMouseMotion motion && MouseCaptured && !OwnDead)
        {
            _yaw -= motion.Relative.X * MouseYawPerPixel * MouseLook;   // mouse right turns right (clockwise seen from above)
            _pitch = Math.Clamp(_pitch - motion.Relative.Y * SmClient.Customizer.Model.LiveOptions.MouseVertical * MouseYawPerPixel * MouseLook, _pitchMin, _pitchMax);   // mouse up aims up
        }
        else if (e is InputEventKey { Pressed: true, PhysicalKeycode: Key.Escape })
        {
            Input.MouseMode = Input.MouseModeEnum.Visible;
            _latch.Reset();   // feel/controls: a tap noted before the mouse was released does not act after it
        }
        else if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown })
        {
            // Either direction switches (to the next weapon: two weapons toggle, three cycle; protocol 44) when the wheel is bound to Next weapon (it is by default; ui/keybinds); only while the mouse is captured, like Fire.
            if (Input.MouseMode == Input.MouseModeEnum.Captured && ControlsInput.Is(e, GameAction.NextWeapon)) _switchPending = true;
        }
        else if (e is InputEventMouseButton { Pressed: true } && Input.MouseMode != Input.MouseModeEnum.Captured && !ModelViewer.TestWindow)
            Input.MouseMode = Input.MouseModeEnum.Captured;
    }

    /// <summary>Bots: reports a frame that does not finish (FrameWatchdog.cs); the phases below say where the main thread is.</summary>
    FrameWatchdog? _watchdog;
    bool _goneNoted;
    readonly HashSet<int> _seenRemotes = new();   // M40-T9: reused every frame
    readonly List<int> _goneRemotes = new();
    void Ph(string phase) => _watchdog?.Phase(phase);

    public override void _Process(double delta)
    {
        if (_bot || FrameProfileOn) _watchdog ??= new FrameWatchdog(_className ?? "bot", System.Environment.GetEnvironmentVariable("SM_WATCHDOG_FILE") is { Length: > 0 } wf ? wf + "." + System.Environment.ProcessId + ".log" : null) { Profile = FrameProfileOn ? NewFrameProfile() : null };
        Ph("frame start");
        _frameNo = (long)Engine.GetProcessFrames();   // qc/reference-pass review: read once per frame (the sprint edges' staleness)
        ProcessFrame(delta);
        _watchdog?.Frame();
    }

    void ProcessFrame(double delta)
    {
        _elapsed += delta;
        HeavyReset();   // M40-T9: this frame's budget for first builds (characters, weapon rigs, corpses)
        if (FrameStatsLog) (_frameStats ??= new MapLightingView.FrameStats(GetViewport())).Add(delta);
        if (PerfProbe.On) (_perfProbe ??= new PerfProbe(GetViewport())).Frame(delta, GetTree().Root);   // perf/pass-1 test aid (--perf-probe)
        if (_blender is not null && _camera is not null) { var cp = _camera.GlobalPosition; _blender.Update(new System.Numerics.Vector3(cp.X, -cp.Z, cp.Y)); }   // Godot to game space
        if (_client is null || _config is null || _assets is null) { UpdateOverlay(); return; }

        if (!_bot && !OwnDead && !PickerBusy && _demoPlayback is null)   // dead: the view is frozen (M23); a replayed demo has the recorded view
        {
            // Keyboard turning on the arrow keys (Q/E until fidelity gap 1, where E became Interact).
            float turn = (Input.IsPhysicalKeyPressed(Key.Left) ? 1f : 0f) - (Input.IsPhysicalKeyPressed(Key.Right) ? 1f : 0f);
            _yaw += turn * KeyYawRate * (float)delta;
            _yaw %= 2 * MathF.PI;
            PanView(delta);   // M36 test aid (--pan-rate)
            PadLook(delta);   // controller: the right stick turns the view (NetworkView.Gamepad.cs)
        }

        Ph("client update (input, network)");
        DemoBeforeUpdate(delta);   // M40 T0: a replayed demo advances its clock by this frame and sets the recorded view
        LatencyProbeBeforeUpdate();   // feel/controls test aid (--latency-probe)
        _client.Update(SampleInput, EarlyTickWanted);   // feel/controls: an input edge runs the next tick now (NetworkView.Controls.cs)
        DemoAfterUpdate(delta);    // M40 T0: records the view / ends the replay
        PadTestFrame();   // --pad-test (NetworkView.GamepadTest.cs)
        if (_client.Status != ClientStatus.Connected)
        {
            // A connection that was up and is gone (the server stopped hearing from this client, or the client stopped hearing the server,
            // for 5 s: typically a frame that blocked that long) used to leave a silent idle process. Say so, and let a bot end (exit code 3).
            if (_connectedFor > 0 && !_goneNoted)
            {
                _goneNoted = true;
                GD.PrintErr($"t {_connectedFor:F1} s: the connection is gone ({_client.Status}); frames since the last snapshot are not sent. " +
                            (_watchdog is { } w ? $"Longest frame so far {w.LongestFrameSeconds:F1} s (server and client time out after 5 s without a frame). " : "") +
                            (_bot ? "The bot ends." : ""));
                if (_bot) GetTree().Quit(3);
            }
            if (_client.Status == ClientStatus.Rejected && !_goneNoted)
            {
                _goneNoted = true;   // added content E1: say why in the console too (a --content or --ork-data mismatch names the server's profile)
                GD.PrintErr($"rejected by the server: {_client.RejectedBecause}{(string.IsNullOrEmpty(_client.RejectDetail) ? "" : " (" + _client.RejectDetail + ")")}" +
                            (_bot ? "; the bot ends (exit code 4)." : ""));
                if (_bot) GetTree().Quit(4);   // a refused bot (bots.cmd against a server with another profile) must not idle forever
            }
            if (_client.Status == ClientStatus.NoAnswer && !_goneNoted)
            {
                _goneNoted = true;   // fix/connect-timeout: no answer to the Hellos (no server there, or another version that drops them unread)
                GD.PrintErr(ConnectFailure.NoAnswerText(_target) + (_bot ? " The bot ends (exit code 5)." : ""));
                if (_bot) GetTree().Quit(5);
            }
            if (PickerOpen) ClosePicker();   // customizer T7: nothing to deploy to
            UpdateOverlay();
            return;
        }
        if (_connectedFor == 0) GD.Print($"connected as player {_client.PlayerId}; handshake round trip {_client.HandshakeRoundTrip.TotalMilliseconds:F0} ms; map '{_client.MapName}'");
        _connectedFor += delta;
        MemoryReportFrame();   // perf/lean-bots: one memory line (bots at 30 s; --mem-report [s]) (NetworkView.Lean.cs)

        if (Observe) { ObserverFrame(delta); return; }   // protocol 46: a spectator draws the world without a player of its own (NetworkView.Observer.cs)
        var me = _client.Predicted;
        Ph("own state");
        LooksBeginFrame(me);   // customizer T7: arrivals, the acknowledged setup, the own look at the respawn
        LeanFrame();   // perf/lean-bots: a lean bot drains the queues the looks would have read (NetworkView.Lean.cs)
        var shown = OwnShownState(me, delta);   // M36: position between ticks and view pitch for the model and the camera (NetworkView.Turning.cs)
        // On (re)spawn the server places the player at a spawn point facing its yaw; take that facing as the view.
        if (me.Alive && !_wasAlive) { _yaw = me.Yaw; _pitch = 0f; }
        _wasAlive = me.Alive;
        if (!me.Alive) _cameraPlacedValid = false;   // feel/controls: no fresh-aim turn from a camera placed before the death (FreshCenterRay)
        HipFireBegin(me);   // M27: drains the own events and the others' shots once, notes who fired
        MeleeLockFrame(me, delta);   // feel/melee-lock: hit-stops, the camera assist (NetworkView.MeleeLock.cs)
        MeleeFlowFrame(me);   // anim/melee-flow: the stun-strike shakes (NetworkView.MeleeFlow.cs)
        MeleeInteractionFrame(me, _lastOthers, observer: false);   // anim/melee-interaction: charge impacts, reaction sources, the hit-frame flinch (NetworkView.MeleeInteraction.cs)
        FlinchFeed(me);     // animation pass 2 (NetworkView.UpperBody.cs): this frame's hits start the flinch
        HandlingFrame(me, delta);   // weapons/spread: the data's climb and sway kick the own view (NetworkView.Spread.cs)
        Ph("own model");
        _local = EnsureModel(_local, _client.PlayerId, true, me);
        Ph("own model: tracks and place");
        TrackJump(me, own: true);
        TrackEvade(me, own: true, (float)delta);
        // Dead (M23): the character is not placed or animated any more; its ragdoll (NetworkView.Death.cs) took over.
        if (TrackDeath(_client.PlayerId, _local, me, own: true, (float)delta))
            Place(_local, shown, OwnThrowTime(me), own: true, smooth: SmoothOwnClip(me));   // feel/controls: own clips at the frame rate
        ApplyTeamLook(_local, _client.PlayerId, me.Team);
        Ph("own model: weapons");
        SyncWeapons(_local, me, (float)delta);   // M24
        Ph("own model: probes");
        YawProbeFrame(delta, me);   // M36 test aid (--yaw-probe)

        Ph("remote players");
        var (renderTick, interpolated) = _client.RemotePlayers();
        var others = DrawnRemotes(interpolated, me, shown);   // sim/player-collision: drawn apart (NetworkView.Bodies.cs; ours)
        _lastOthers = others;
        HipFireOthers(me, others, renderTick > 0);   // M27 (fetches this frame's projectile list once, _frameProjectiles)
        var seen = _seenRemotes;
        seen.Clear();
        foreach (var state in others)
        {
            seen.Add(state.PlayerId);
            Ph("remote players: model");
            _remotes.TryGetValue(state.PlayerId, out var character);
            if (character is null && !HeavyLeft) continue;   // M40-T9: a joining player's character is built a few frames later, not all in one
            long heavy = HeavyStart();
            _remotes[state.PlayerId] = character = EnsureModel(character, state.PlayerId, false, state);
            if (character.Weapons is null) HeavyEnd(heavy);   // a character built just now (its rig not yet): the build time counts
            // Slim records carry only a Throwing flag (protocol 8): time the throw from the flag's rise.
            float? remoteThrow = null;
            if (state.Alive && state.Throwing)
            {
                if (!_remoteThrowStart.TryGetValue(state.PlayerId, out double start))
                {
                    _remoteThrowStart[state.PlayerId] = start = _connectedFor;
                    _remoteThrowsSeen++;
                    if (_bot) GD.Print($"t {_connectedFor:F1} s: id {_client.PlayerId} sees P{state.PlayerId} throw (remote throws seen {_remoteThrowsSeen})");
                }
                remoteThrow = (float)(_connectedFor - start);
            }
            else _remoteThrowStart.Remove(state.PlayerId);
            Ph("remote players: jump and evade tracks");
            TrackJump(state, own: false);
            Ph("remote players: evade track");
            TrackEvade(state, own: false, (float)delta);
            Ph("remote players: death tracking");
            bool placed = TrackDeath(state.PlayerId, character, state, own: false, (float)delta);
            Ph("remote players: place");
            if (placed) Place(character, state, remoteThrow, smooth: state.Alive ? SmoothRemoteClip(state, (float)delta) : null);
            Ph("remote players: team look");
            ApplyTeamLook(character, state.PlayerId, state.Team);
            Ph("remote players: weapons");
            if (character.Weapons is not null || HeavyLeft)   // M40-T9: the weapon rig of a new character is started when the frame has budget left
            {
                long rigStart = HeavyStart();
                bool starting = character.Weapons is null;
                SyncWeapons(character, state, (float)delta);   // M24
                if (starting) HeavyEnd(rigStart);
            }
            Ph("remote players: bookkeeping");
            character.Visible = state.Alive;   // a dead marine's living model is hidden: its ragdoll lies there instead (M23)
            // Slim records carry only whether a player is charging (protocol 7): count the starts for the log.
            bool wasCharging = _remoteCharging.GetValueOrDefault(state.PlayerId);
            if (state.Charging && !wasCharging)
            {
                _remoteChargeStarts++;
                if (_bot) GD.Print($"t {_connectedFor:F1} s: id {_client.PlayerId} sees P{state.PlayerId} start charging ({WeaponLabel(Loadouts.InHand(_config, state))})");
            }
            _remoteCharging[state.PlayerId] = state.Charging;
        }
        _goneRemotes.Clear();
        foreach (var id in _remotes.Keys) if (!seen.Contains(id)) _goneRemotes.Add(id);   // M40-T9: no per-frame LINQ or list
        foreach (var gone in _goneRemotes)
        {
            _remotes[gone].QueueFree();
            _remotes.Remove(gone);
            RemoveRagdolls(gone);
            _aliveSeen.Remove(gone); _motion.Remove(gone); _deathCauses.Remove(gone);
            _remoteCharging.Remove(gone);
            _remoteThrowStart.Remove(gone);
            _clipClocks.Remove(gone); _clipStats.Remove(gone);
            _jumpTracks.Remove(gone);
            _evadeTracks.Remove(gone);
            _aimMoving.Remove(gone);
            HipFireForget(gone);
            ReloadForget(gone);   // M35
            BraceForget(gone);   // fidelity gap 7
            LocomotionForget(gone);
            MeleeLockForget(gone);   // feel/melee-lock
            MeleeFlowForget(gone);   // anim/melee-flow
            _stimTracks.Remove(gone);   // fx/visuals-2: a reused id never inherits a stale stim glow
            _orkTracks.Remove(gone); _orkShown.Remove(gone);   // Ork O7a (the capture aid keeps no entry of a player who left)
        }

        Ph("looks");
        LooksEndFrame(delta);   // customizer T7: builds the wanted looks within the frame budget
        OrkLog();   // Ork O7a
        LoadoutFrame(me, delta);   // customizer T7: the death screen's loadout hint and the picker (NetworkView.Loadout.cs)
        Ph("projectiles and blasts");
        LogClipStats();
        UpdateProjectiles();
        UpdateBlasts(delta);
        UpdateAreas(delta);
        WatchThrowAndCone(me);
        UpdateConeFlashes(delta);
        UpdateRings(delta);
        LogCharge(me, (float)delta);
        Ph("camera");
        FxShakeBegin();   // M25b
        FollowCamera(shown, (float)delta);
        CameraProbeFrame(shown, delta);   // M40-T6 test aid (--camera-probe)
        PosProbeFrame(delta, me, shown);   // M36 test aid (--yaw-probe)
        MotionProbeFrame(delta, me, others);   // M40 T0 test aid (--motion-probe)
        Ph("effects");
        UpdateFx(me, delta);   // M25b
        UpdatePickups(me);   // fidelity gap 1 (NetworkView.Pickups.cs)
        Ph("reactions");
        UpdateReactions(me, others, (float)delta);   // M28: shake (after the camera is placed), blindness, logs
        UpdateDashAttackFrame(me, others);   // protocol 30: the dash attack's hit effect and logs, --log-falls (NetworkView.DashAttack.cs)
        Ph("overlay text");
        UpdateOverlay();
        Ph("match ui");
        UpdateMatchUi();
        Ph("deaths");
        ProcessDeaths();   // after the kill events of this frame (UpdateMatchUi) are noted
        Ph("ragdolls");
        UpdateRagdolls(delta);
        UpdateVignette(delta);
        UpdateDamageFlash(me, delta);   // M38
        Ph("weapons");
        WeaponFrame(delta);   // M24
        Ph("sound");
        UpdateSound(me, others, delta);   // M26b
        ExtrasFrame(me, others, delta);   // EXTRAS-HOOK: added content's per-frame work after the sound (loyalist barks: barks and the FOR THE EMPEROR key)
        UpdateBraceVent(me, others);   // fidelity gap 7: the brace's and the vent's sounds and effects
        UpdateStims(me, others);   // perks/inert-2: combat stims' sound and effects (NetworkView.Stims.cs)
        UpdateHits();
        Ph("hip fire");
        HipFireMeasure(me);   // M27
        HipClipProbe(me);     // M27 test aid (--hip-probe)
        HipFireLog();
        UpdateSeize(delta);
        LatencyProbeFrameEnd(me, delta);   // feel/controls test aid (--latency-probe)
        if (me.MeleeStep > 0 && me.MeleeStep != _lastMeleeStep)
        {
            _meleeSwings++;
            if (_bot) GD.Print($"t {_connectedFor:F1} s: id {_client.PlayerId} melee step {me.MeleeStep} starts (swing {_meleeSwings}, {MeleeLabel(me)}); lock P{_client.MeleeLockTarget}; hp {me.Health:F0} sh {me.Shield:F0} {AmmoText(me)}");
        }
        _lastMeleeStep = me.MeleeStep;
        // A switch puts the other weapon in hand and starts the switch time (a respawn or the first snapshot does not).
        if (_slotSeen && me.Slot != _lastSlot && me.SwitchLeft > 0f)
        {
            _switches++;
            if (_bot) GD.Print($"t {_connectedFor:F1} s: id {_client.PlayerId} switches to {WeaponLabel(Loadouts.InHand(_config, me))} (switch {_switches}); {AmmoText(me)}");
        }
        _lastSlot = me.Slot; _slotSeen = true;
        Ph("logging");
        LogHeat(me);
        if (!_attackSeen && _connectedFor >= 4.0)
            _attackSeen = others.Where(o => o.Alive).Append(me).Any(p => Loadouts.Melee(_config, p) is var steps
                && p.MeleeStep >= 1 && p.MeleeStep <= steps.Count && p.MeleeTime >= steps[p.MeleeStep - 1].HitTime);
        // Other players' health, shield and ammunition are not sent (slim records); only what is drawn is logged for them.
        if (_bot) { if (!me.Alive) _navAgent?.Reset(); LogNav(me); }
        if (_bot && (int)(_connectedFor / 2.0) != (int)((_connectedFor - delta) / 2.0))
            GD.Print($"t {_connectedFor:F0} s: id {_client.PlayerId} class '{ClassLabel(me.Class)}' at {me.Position.X:F1},{me.Position.Y:F1},{me.Position.Z:F1} yaw {me.Yaw:F2} pitch {me.Pitch:F2} speed {new Vector2(me.Velocity.X, me.Velocity.Y).Length():F1} {_local.Playing} " +
                     $"{(me.Alive ? "alive" : "dead")} hp {me.Health:F0} sh {me.Shield:F0} {AmmoText(me)}{HeatText(me)}{ChargeText(me)} bot [{_botNote}] melee {me.MeleeStep} ({MeleeLabel(me)}) swings {_meleeSwings} switches {_switches} overheats {_overheats} releases {_releases}; " +
                     $"charged releases {_chargedReleases} own shots basic {_ownBasicShots} charged {_ownChargedShots} detonate requests {_detonateRequests}; projectiles drawn {_projectileNodes.Count} seen {_projectilesSeen} (charged {_chargedProjectilesSeen}) detonations seen {_detonationsSeen} (charged {_chargedDetonationsSeen}) remote charge starts {_remoteChargeStarts}; " +
                     $"{ThrowText(me)} throws started {_throwsStarted} released {_throwsReleased} remote throws seen {_remoteThrowsSeen} thrown seen {_thrownSeen} thrown detonations seen {_thrownDetonationsSeen} melta shots {_meltaShots}; " +
                     $"{JumpLogText(me)}; {DashAttackLogText()}; {MeleeLockLogText()}; {MeleeFlowLogText()}; {MeleeInteractionLog()}; " +
                     string.Join(" ", others.Select(o => $"P{o.PlayerId} '{ClassLabel(o.Class)}' {WeaponLabel(Loadouts.InHand(_config, o))} melee {MeleeLabel(o)} {o.Position.X:F1},{o.Position.Y:F1} yaw {o.Yaw:F2} {(o.Alive ? "alive" : "dead")}{(o.Charging ? " CHARGING" : "")}{(o.Throwing ? " THROWING" : "")}{(JumpPack.Jumping(o.Jump) ? " JUMPING" : JumpPack.Pounding(o.Jump) ? " POUNDING" : "")} z {o.Position.Z:F1} melee {o.MeleeStep} {_remotes.GetValueOrDefault(o.PlayerId)?.Playing}")));
    }

    /// <summary>
    /// Hook for added content (docs/added-content.md): may add buttons to a live sampled input (never to a replayed demo's). Implemented in an
    /// <c>Extras/</c> folder; each implementation returns at once unless its flag is set in the match's config.
    /// </summary>
    partial void ExtrasInput(ref PlayerInput input);

    /// <summary>Hook for added content: once a frame after the sound (<see cref="UpdateSound"/>), with the own and the other players' shown states.</summary>
    partial void ExtrasFrame(PlayerState me, IReadOnlyList<PlayerState> others, double delta);

    PlayerInput SampleInput(uint tick)
    {
        Ph("input sampling");
        var input = _demoPlayback is not null ? DemoSampleInput(tick) : SampleInputCore(tick);   // M40 T0: replayed demos answer from the recording
        if (_demoPlayback is null) ExtrasInput(ref input);   // EXTRAS-HOOK: added content's keys on a live input (loyalist barks: the FOR THE EMPEROR key)
        DemoRecordInput(input);
        NoteSentInput(input);   // feel/bullets: the shot of this tick is drawn on the ray this input carries (NetworkView.Bullets.cs)
        Ph("client update (tick step)");   // the prediction step and the send follow in GameClient.Update
        return input;
    }

    PlayerInput SampleInputCore(uint tick)
    {
        _sampledYaw = _yaw; _sampledPitch = _pitch;   // M36: the view this input takes (NetworkView.Turning.cs)
        // Dead (M23): no control until the respawn - neither movement, look, fire nor abilities are sent.
        if (OwnDead || PickerBusy) { _switchPending = false; _latch.Reset(); return new PlayerInput(tick, 0f, 0f, _yaw, InputButtons.None, _pitch); }
        if (_bot && BotSwitchEvery > 0 && _connectedFor > 4.0) return BotSwitchInput(tick);   // fix/stow-right test aid (NetworkView.UpperBody.cs)
        if (_bot && BotIdle) return new PlayerInput(tick, 0f, 0f, _yaw, InputButtons.None, _pitch);
        if (_bot && BarrelTest) return BarrelTestInput(tick);   // fx/barrel-origin test aid (NetworkView.Barrel.cs)
        if (_bot && BotGait) return BotGaitInput(tick);   // M36 test aid
        if (_bot && AimTest && BotBackup) return BotBackupInput(tick);   // feel/camera-hitstop test aid (--bot-backup): backs into the wall behind
        if (_bot && SpreadWallInput(tick) is { } spreadWall) return spreadWall;   // weapons/spread test aid (NetworkView.Spread.cs)
        if (_bot)
        {
            // --aim-test: hold aim mode every other 5 s so the screenshots show both cameras (test aid).
            var scripted = BotPickupInput(BotApplyAim(BotInput(tick)), tick);   // M44a: aim error on ranged shots at enemies (NetworkView.BotAim.cs); fidelity gap 1: E in reach of a pickup; --bot-pickups walks to one
            scripted = BotBraceVentInput(scripted);   // fidelity gap 7 test aid (--bot-brace): brace and vent with Reload
            scripted = BotTurnLimit(scripted);   // anim/melee-flow test aid (--bot-turn-rate): the view turns like a hand on a mouse, not in one tick (NetworkView.MeleeFlow.cs)
            if (AimTest) LogConvergence(scripted);
            // --bot-slot pistol|primary (test aid): one press of that slot key (protocol 14) a few seconds after joining.
            if (BotSlot is { Length: > 0 } && _connectedFor is >= 3.0 and < 3.2)
                scripted = scripted with { Buttons = scripted.Buttons | (BotSlot.StartsWith("pi", StringComparison.OrdinalIgnoreCase) ? InputButtons.SlotPistol : InputButtons.SlotPrimary) };
            // --bot-stand (test aid, with --aim-test): holds aim mode and an 8 s cycle: stands 4 s, walks forward 2 s, strafes right 2 s.
            if (AimTest && BotStand)
            {
                double phase = _connectedFor % 8.0;
                return new PlayerInput(tick, phase >= 6.0 ? 1f : 0f, phase is >= 4.0 and < 6.0 ? 1f : 0f, scripted.Yaw, scripted.Buttons & ~InputButtons.Sprint | InputButtons.Aim, 0f);
            }
            var botInput = AimTest && (int)(_connectedFor / 5.0) % 2 == 1 ? scripted with { Buttons = scripted.Buttons | InputButtons.Aim } : scripted;
            return BulletProbe ? Converged(botInput) : botInput;   // feel/bullets --bullet-probe: a bot with --aim-test sends the converged shot of a human (offsets and origin)
        }
        if (_pad is not null && PadHost.Active) return SamplePad(tick);   // controller: the last device used wins (NetworkView.Gamepad.cs)
        // ui/keybinds (ours, an added option): the keyboard and mouse bindings are the player's (ControlsInput, controls.json); the defaults are the old keys. Fire, Aim, Melee, the stun strike,
        // the grenade and the weapon switch act only while the mouse is captured (a click on a released mouse recaptures it). The melee strike and the stun strike are the same bits as ever,
        // so the sim's charge (sprint + forward + stun strike) and the directional strikes follow whichever inputs those actions are bound to. The wheel (if bound) is _switchPending.
        var sampled = BindingSet.Sample(ControlsInput.Held, MouseCaptured);
        float x = sampled.X, y = sampled.Y;
        var buttons = sampled.Buttons;
        if (_switchPending) buttons |= InputButtons.Switch;
        // Interact (protocol 26, fidelity gap 1): the Use action picks up a dropped pickup in reach (NetworkView.Pickups.cs).
        buttons |= InteractButton();
        buttons = LatchButtons(buttons);   // feel/controls: a tap since the last sample is held for this one (NetworkView.Controls.cs)
        _switchPending = false;
        // The shot goes where the screen-centre crosshair is (NetworkView.Camera.cs).
        return Converged(new PlayerInput(tick, x, y, _yaw, buttons, _pitch));
    }

    /// <summary>
    /// Scripted test input. Without a target it is a function of the tick and player id only: a 10 s cycle of running
    /// forward while turning (5 s), sprinting while turning (3 s) and standing (2 s); players are offset in phase and
    /// alternate turn direction. While a living remote player is visible, the bot aims at the nearest one (from its eye,
    /// <see cref="SimConfig.EyeHeight"/> above its feet, to <see cref="BotTargetHeight"/> above the target's feet, at the
    /// interpolated position it is shown at, which is what lag compensation rewinds to), fires the weapon in hand (held for
    /// a full-automatic weapon; for a semi-automatic one pressed on a released trigger, so press and release alternate
    /// ticks) and strafes sideways in the same run/stand rhythm instead of running at the target. Reloading on an empty clip
    /// is automatic in the sim. Weapons (<see cref="BotWantsSwitch"/>): with the primary empty it switches to a loaded
    /// pistol, with the pistol empty back to a primary that has ammunition left. Within <see cref="BotMeleeDistance"/>, if
    /// its class has a melee combo, it stands and taps melee instead of firing: pressed for one tick, released, and pressed
    /// again while the attack runs (the simulation buffers it and moves on at the combo window), so the combo chains.
    /// With <c>--bot-melee</c> it runs (sprints beyond 6 m) straight at the target instead of firing and strafing.
    /// M19: on a map "a target" is an enemy it can see within <see cref="BotFightRange"/> (<see cref="BotCanFight"/>); with
    /// none it walks toward the nearest enemy or the map centre (<see cref="BotChaseInput"/>).
    /// </summary>
    PlayerInput BotInput(uint tick)
    {
        int id = Math.Max(_client?.PlayerId ?? 0, 0);
        double t = tick * (double)_config!.TickSeconds + id * 3.7;
        double phase = t % 10.0;
        var me = _client!.Predicted;
        if (JumpTest && JumpTestInput(me, tick) is { } jumpTest) return jumpTest;   // M44 test aid (NetworkView.JumpFx.cs)
        if (SlashTestInput(me, tick) is { } slashTest) return slashTest;   // feel/jump-pack test aid --slash-test (NetworkView.DashAttack.cs)
        if (StompMoveTestInput(me, tick) is { } stompMove) return stompMove;   // anim/stomp-to-move test aid --stomp-move-test (NetworkView.StompMoveTest.cs)
        if (CarryTestInput(me, tick) is { } carry) return carry;   // anim/relic-bolter-idle test aid --carry-test (NetworkView.CarryTest.cs)
        if (DashWallTestInput(me, tick) is { } dashWall) return dashWall;   // feel/jump-pack-2 test aid --dash-wall-test (NetworkView.DashAttack.cs)
        // A navigation jump in flight keeps its checked controls (NetworkView.Nav.cs).
        if (BotNavFlightInput(me, tick) is { } flightInput) return flightInput;
        // M44a: during the match's "MATCH BEGINS" countdown (attacks do not work yet) the bot holds still in its spawn room: no wasted shots, no stale path.
        if (_client.Match.Effective() == MatchState.Countdown) { _botNote = "countdown: holding"; _navAgent?.Reset(); return new PlayerInput(tick, 0f, 0f, _yaw, InputButtons.None, _pitch); }
        // Seize Ground (M16): go to a point not held, fight on the way and hold it (NetworkView.Seize.cs).
        _botNote = "";
        if (BotSeizeInput(me, tick, phase) is { } seizeInput) { _botNote = _botSeizeNote; return seizeInput; }
        // M19: on a map it fights only an enemy it sees within BotFightRange; otherwise it chases (NetworkView.Bot.cs).
        if (me.Alive && BotFightTarget(me, out _) is { } target)
        {
            float dx = target.Position.X - me.Position.X, dy = target.Position.Y - me.Position.Y;
            float dz = target.Position.Z + BotTargetHeight - (me.Position.Z + _config.EyeHeight);
            _yaw = MathF.Atan2(-dx, dy);   // direction (-sin yaw, cos yaw)
            _pitch = Math.Clamp(MathF.Atan2(dz, MathF.Sqrt(dx * dx + dy * dy)), -MaxPitchDown, MaxPitchUp);
            // Jump pack (assault classes): jump at targets 8-30 m away and pound from above (NetworkView.Jump.cs).
            if (BotDashAttackInput(me, target, tick) is { } dashInput) return dashInput;   // protocol 30 test aid --bot-dash-attack (NetworkView.DashAttack.cs)
            if (BotJumpInput(me, target, tick) is { } jumpInput) return jumpInput;
            // Evade roll (Tactical / Devastator classes): sideways every ~6 s (NetworkView.Evade.cs).
            if (BotEvadeInput(me, tick) is { } evadeInput) return evadeInput;
            bool hasMelee = Loadouts.Melee(_config, me).Count > 0;
            // perks/inert-2: a bot carrying combat stims uses them once it has a target within 20 m (melee bots too; ours, test script).
            if (StimOf(me) is not null && BotThrow(me, MathF.Sqrt(dx * dx + dy * dy)) is var stimPress and not InputButtons.None)
                return new PlayerInput(tick, 0f, 1f, _yaw, stimPress, _pitch);
            // The charge (M29): sometimes, at 4-9 m, run at the target and press F once moving fast enough (NetworkView.Reactions.cs).
            if (BotRushInput(me, tick, dx * dx + dy * dy) is { } rushInput) return rushInput;
            // Same floor only: a target more than BotMeleeHeight above or below is not in reach (QA 2026-10-28: bots swung at an
            // enemy 5 m below a ledge for 40 s).
            bool sameFloor = MathF.Abs(target.Position.Z - me.Position.Z) <= BotMeleeHeight;
            if (hasMelee && sameFloor && dx * dx + dy * dy <= BotMeleeDistance * BotMeleeDistance)
            {
                // Press on a released key unless a press is already buffered; the next tick releases it. During the second light
                // attack the press is the heavy strike (M28: X X Y, the combo's third heavy strike), when the class has one.
                bool press = !me.MeleeHeld && !me.MeleeHeavyHeld && !me.MeleeQueued;
                // Weapons C: a single-attack combo (the Devastator's kick) goes on to its heavy strike (the stomp), and a heavy strike with a
                // Y,Y finisher chains into it.
                int heavies = Loadouts.MeleeHeavy(_config, me).Count;
                // feel/melee-charge-2 test aid --bot-heavy-chain: F from rest (the backhand), F again during it (the Y,Y chain to the stomp), and so on.
                bool heavy = (BotHeavyChain && heavies >= 1 && (!me.MeleeHeavy || Combat.MeleeStepOf(_config, me) is { YNext: > 0 }))
                    || (me.MeleeStep == 2 && !me.MeleeHeavy && heavies >= 3)
                    || (me.MeleeStep == 1 && !me.MeleeHeavy && Loadouts.Melee(_config, me).Count == 1 && heavies >= 1)
                    || (me.MeleeHeavy && Combat.MeleeStepOf(_config, me) is { YNext: > 0 });
                return new PlayerInput(tick, 0f, 0f, _yaw, press ? heavy ? InputButtons.MeleeHeavy : InputButtons.Melee : InputButtons.None, _pitch);
            }
            if (_botMelee) return new PlayerInput(tick, 0f, 1f, _yaw, dx * dx + dy * dy > 36f ? InputButtons.Sprint : InputButtons.None, _pitch);
            // Strafe only onto walkable ground (navigation graph, NetworkView.Nav.cs): the other side or none otherwise.
            float strafe = BotWalkableStrafe(me, phase >= 8.0 ? 0f : id % 2 == 0 ? 1f : -1f);
            float horizontal = MathF.Sqrt(dx * dx + dy * dy);
            var grenade = BotThrow(me, horizontal);
            // Throwing (pressed now or under way until the release): aim at the ground BotThrowShort m short of the target,
            // since a grenade keeps going after it lands (test script heuristic, not the original's aim).
            if (grenade != InputButtons.None || (me.Throwing && me.ThrowTime <= _config.ThrowRelease + 0.05f))
                _pitch = Math.Clamp(MathF.Atan2(target.Position.Z - (me.Position.Z + _config.EyeHeight), MathF.Max(2f, horizontal - BotThrowShort)), -MaxPitchDown, MaxPitchUp);
            // The melta only reaches its cone length: beyond it, run at the target (no fire) instead of strafing.
            if (Loadouts.InHand(_config, me) is { Delivery: DeliveryKind.Cone, Cone: { } cone } && horizontal > cone.Length * 0.9f)
                return new PlayerInput(tick, 0f, 1f, _yaw, grenade, _pitch);
            // A switch in the same tick would win and swallow the throw (and its 4 s spacing), so a throw drops the switch.
            var weaponButtons = BotWeaponButtons(me, tick, target);
            if (grenade != InputButtons.None) weaponButtons &= ~InputButtons.Switch;
            // M44a: a ranged shot at an enemy gets the aim error (BotApplyAim); a throw keeps the exact aim.
            _botAimNow = grenade == InputButtons.None && !me.Throwing;
            _botAimTarget = target.PlayerId;   // the trigger discipline's reaction delay (NetworkView.BotAim.cs)
            BotNoteEngagement(horizontal);
            // M44a: far from the target (more than BotAdvanceBeyond m) the bot keeps firing and walks toward it along its path, so that long-range
            // fights close up (no sprint: it would stop the fire). Without a graph, or at the end of the path, it strafes as before.
            if (horizontal > BotAdvanceBeyond && Nav is not null)
            {
                var (advance, speed, _, _) = BotSteer(me, BotPathGoal(target.Position), $"advance on P{target.PlayerId}");
                // Not along a path that flies a jump link (the take-off needs the bot still); drops are fine (a spawn room's exit is one).
                bool flies = _navAgent?.Path is { } adv && adv.Waypoints.Skip(Math.Max(_navAgent.Waypoint, 0)).Take(6).Any(w => w.Kind == NavLinkKind.Jump);
                if (advance.LengthSquared() > 1e-6f && !flies)
                    return BotMoveInput(tick, advance * speed, grenade | weaponButtons | _navJump);
            }
            return new PlayerInput(tick, strafe, 0f, _yaw, grenade | weaponButtons, _pitch);
        }
        // --bot-goal / --bot-route (test aid): walk the route instead of chasing (NetworkView.Nav.cs).
        if (BotRouteInput(me, tick) is { } routeInput) return routeInput;
        if (!(BotFireWalls && NearestLivingTarget(me) is null) && BotChaseInput(me, tick) is { } chaseInput) return chaseInput;   // --bot-fire-walls: stays in its room when alone
        float turnRate = id % 2 == 0 ? 1.1f : -1.1f;   // about a 6 m running circle
        _yaw = (float)(t * turnRate % (2 * Math.PI));
        _pitch = 0f;
        // --bot-fire: keep shooting without a target too (sprinting would stop the fire, so it runs instead). Not on a map:
        // there it fires only at an enemy it sees (M19), so the shots do not go into walls.
        bool idleFire = _botFire && me.Alive && (_client.Collision is null || BotFireWalls);
        var idle = idleFire ? BotWeaponButtons(me, tick, null) : InputButtons.None;
        if (phase >= 8.0) return new PlayerInput(tick, 0f, 0f, _yaw, idle);
        // With --bot-fire it never sprints (released-trigger ticks included), so a charge or the fire is not interrupted.
        return new PlayerInput(tick, 0f, 1f, _yaw, idle != InputButtons.None || idleFire ? idle : phase >= 5.0 ? InputButtons.Sprint : InputButtons.None);
    }

    /// <summary>
    /// The bot's weapon buttons: a switch press edge when <see cref="BotWantsSwitch"/>, else the trigger.
    /// <para>
    /// Charge weapons (<see cref="WeaponSpec.Charge"/>; Fire for both shots since M31): in the last <see cref="BotChargeWindow"/>
    /// seconds of every <see cref="BotChargeCycle"/> it holds Fire until the last stage with a shot is reached
    /// (<see cref="Combat.ChargeStage"/>), then releases it (it aims at the target every tick, so the release is aimed); otherwise it
    /// taps (presses on a released button, releases on the next tick: the basic shot). It does nothing while a released shot is
    /// pending or a raise is under way (the tap's shot waits for the weapon to come up).
    /// </para>
    /// <para>
    /// Remote detonation (grenade launcher): with grenades out it presses Reload (on a released key) when a grenade of
    /// this weapon is drawn within half its blast radius of the target, or once grenades have been out for
    /// <see cref="BotDetonateAfter"/> s. Snapshots do not say whose a grenade is, so any grenade of this weapon counts.
    /// </para>
    /// </summary>
    InputButtons BotWeaponButtons(PlayerState me, uint tick, PlayerState? target)
    {
        var c = _config!;
        if (BotWantsSwitch(me)) return me.SwitchHeld ? InputButtons.None : InputButtons.Switch;   // press edge; released on the next tick
        if (Loadouts.InHand(c, me) is not { } weapon) return InputButtons.None;
        var buttons = InputButtons.None;

        // Not during a switch or a melee attack: the simulation ignores the press then.
        if (Shots.RemoteDetonated(weapon) && me.ProjectilesOut > 0 && !me.ReloadHeld && me.SwitchLeft <= 0f && me.MeleeStep == 0
            && BotWantsDetonation(me, weapon, target) is { } why)
        {
            buttons |= InputButtons.Reload;
            _detonateRequests++;
            GD.Print($"t {_connectedFor:F1} s: id {_client!.PlayerId} detonates {me.ProjectilesOut} grenade(s) ({why}; requests {_detonateRequests})");
        }

        if (weapon.Charge is { Stages.Count: > 0, FiresOnPress: false } charge)   // sim/instant-fire: the lascannon press-fires like any basic weapon
        {
            int lastFiring = 0;
            for (int i = 0; i < charge.Stages.Count; i++) if (charge.Stages[i].Shot is not null) lastFiring = i + 1;
            if (me.PendingShot > 0 || (me.RaiseTicks > 0 && !me.Charging)) return buttons;   // the released shot is about to leave, or waits for the raise (a charge held through the raise goes on)
            if (me.Charging)
            {
                // A tap releases at once (the basic shot); a hold lasts until the firing stage (aimed: the bot turns to the target every tick).
                if (_botTap) return buttons;
                bool ready = lastFiring > 0 && Combat.ChargeStage(charge, me.ChargeTime, firing: true) >= lastFiring;
                if (ready) return buttons;
                return buttons | InputButtons.Fire;
            }
            if (me.FireHeld || me.Overheated) return buttons;   // release first: a charge starts on a press edge
            bool chargeNow = (tick * (double)c.TickSeconds + Math.Max(_client?.PlayerId ?? 0, 0) * 1.3) % BotChargeCycle >= BotChargeCycle - BotChargeWindow;
            // Too little in the clip to start a charge (the lascannon needs 2) but some in reserve: reload first.
            if (chargeNow && me.Clip > 0 && me.Clip < Math.Max(1, charge.AmmoToStart) && me.Reserve > 0 && me.ReloadLeft <= 0f)
                return buttons | (me.ReloadHeld ? InputButtons.None : InputButtons.Reload);
            _botTap = !chargeNow;
            return buttons | InputButtons.Fire;
        }
        return buttons | (weapon.Trigger == TriggerKind.FullAuto || !me.FireHeld ? InputButtons.Fire : InputButtons.None);
    }

    /// <summary>
    /// The bot's throw: a Grenade press (on a released key) when it carries one, the item's cooldown is over, nothing else
    /// is under way (throw, melee, switch, charge or pending shot), the target is <see cref="BotThrowMin"/> to
    /// <see cref="_botThrowMax"/> m away (horizontally) and its last throw press was <see cref="BotThrowEvery"/> s ago or more.
    /// </summary>
    InputButtons BotThrow(PlayerState me, float distance)
    {
        if (me.GrenadeHeld || me.Grenades <= 0 || me.GrenadeCooldown > 0f || me.Throwing || me.MeleeStep > 0 || me.SwitchLeft > 0f
            || me.Charging || me.PendingShot > 0 || (distance < BotThrowMin && StimOf(me) is null) || distance > _botThrowMax || _connectedFor - _botLastThrow < BotThrowEvery)   // perks/inert-2: stims at any distance
            return InputButtons.None;
        _botLastThrow = _connectedFor;
        GD.Print($"t {_connectedFor:F1} s: id {_client!.PlayerId} bot presses throw at a target {distance:F1} m away ({ThrowText(me)})");
        return InputButtons.Grenade;
    }

    /// <summary>Why the bot's grenades should go off now (see <see cref="BotWeaponButtons"/>), or null.</summary>
    string? BotWantsDetonation(PlayerState me, WeaponSpec weapon, PlayerState? target)
    {
        if (target is { } t && _client is not null && GrenadeNear(me, weapon, t) is { } g)
            return $"grenade #{g.Id} at {g.Position.X:F1},{g.Position.Y:F1},{g.Position.Z:F1}{((g.Flags & ProjectileFlags.Stuck) != 0 ? " stuck" : (g.Flags & ProjectileFlags.Attached) != 0 ? " attached" : "")} near P{t.PlayerId} at {t.Position.X:F1},{t.Position.Y:F1},{t.Position.Z:F1}";
        return _grenadesOutFor >= BotDetonateAfter ? $"out for {_grenadesOutFor:F1} s" : null;
    }

    /// <summary>A grenade of the weapon in hand drawn within half its blast radius of the target's chest, or null.</summary>
    ProjectileView? GrenadeNear(PlayerState me, WeaponSpec weapon, PlayerState t)
    {
        int index = Loadouts.WeaponIndex(_config!, me, me.Slot);
        float radius = Shots.ProjectileOf(weapon, 0)?.Splash?.Radius ?? 2f;
        var centre = t.Position + new System.Numerics.Vector3(0, 0, 1f);
        foreach (var q in _client!.Projectiles())
            if (!q.Thrown && q.Weapon == index && q.Stage == 0 && System.Numerics.Vector3.DistanceSquared(q.Position, centre) <= radius * radius * 0.25f) return q;
        return null;
    }

    /// <summary>
    /// Whether the bot should switch weapons now: no attack or switch under way, the player has a weapon in the other slot,
    /// and either the primary is empty (reloading or not) while the pistol is loaded, or the pistol is empty while the
    /// primary has ammunition (loaded or in reserve; it reloads once in hand). The holstered weapon does not reload, so
    /// after one round trip the bot stays on the primary and reloads it.
    /// Heat: with the primary overheated (locked) and a loaded pistol, it switches to the pistol, and back once the
    /// holstered primary is released (heat at or below its recovery value), staying on the pistol (which reloads itself)
    /// until then. Without a loaded pistol it keeps the trigger down on the locked primary (the simulation refuses the
    /// shots) until it is released.
    /// </summary>
    bool BotWantsSwitch(PlayerState me)
    {
        if (BarrelTest) return false;   // fx/barrel-origin: the weapon asked for stays in hand
        if (me.MeleeStep > 0 || me.SwitchLeft > 0f || me.Charging || me.PendingShot > 0) return false;   // a switch would cancel the charge
        if (Loadouts.Holstered(_config!, me) is null) { _botCoolingPrimary = false; return false; }
        if (me.Slot == WeaponSlot.Ranged)
        {
            // Fidelity gap 7 (--bot-brace): a vent weapon vents its overheat (2 to 4 s) instead of being switched away.
            if (me.Overheated && me.OtherClip > 0 && !(BotBraceVent && Loadouts.InHand(_config!, me)?.Vent is not null)) { _botCoolingPrimary = true; return true; }
            _botCoolingPrimary = false;
        }
        else if (_botCoolingPrimary)
        {
            if (!me.OtherOverheated) { _botCoolingPrimary = false; return true; }
            return false;   // the primary is still locked: stay on the pistol (it reloads itself when empty)
        }
        if (me.Clip > 0) return false;
        // A self-recharging weapon (the melta) in hand with reserve left waits for its recharge (a switch would lose it).
        if (Loadouts.InHand(_config!, me)?.Recharge is not null && me.Reserve > 0) return false;
        return me.Slot == WeaponSlot.Ranged ? me.OtherClip > 0 : me.OtherClip + me.OtherReserve > 0;
    }

    /// <summary>Bot log: each weapon's overheat and release, with the heat it reached (own player, predicted state).</summary>
    void LogHeat(PlayerState me)
    {
        foreach (var slot in new[] { WeaponSlot.Ranged, WeaponSlot.Pistol })
        {
            bool inHand = me.Slot == slot;
            var weapon = Loadouts.Weapon(_config!, me, slot);
            if (weapon?.Heat is not { } heat) continue;
            bool overheated = inHand ? me.Overheated : me.OtherOverheated;
            float value = inHand ? me.Heat : me.OtherHeat;
            int i = (int)slot;
            _peakHeat[i] = MathF.Max(_peakHeat[i], value);
            if (overheated == _overheatSeen[i]) continue;
            _overheatSeen[i] = overheated;
            if (overheated) _overheats++; else _releases++;
            if (_bot)
                GD.Print($"t {_connectedFor:F1} s: id {_client!.PlayerId} {WeaponLabel(weapon)} {(overheated ? "OVERHEATED" : "released")} at heat {value:F1}/{heat.Overheat:F0} " +
                         $"(recovery {heat.Recovery:F0}; peak so far {_peakHeat[i]:F1}; {(inHand ? "in hand" : "holstered")})");
            if (!overheated) _peakHeat[i] = value;
        }
    }

    /// <summary>
    /// Draws every projectile <see cref="GameClient.Projectiles"/> gives (interpolated, server-authoritative) as a
    /// placeholder sphere at its game-space position: plasma blue-white and emissive, grenades dark, anything else orange;
    /// a charged shot's (stage &gt; 0) larger. Resting (stuck) and attached ones are drawn where the snapshots put them,
    /// which keeps them still. Nodes are pooled: one per live projectile id, hidden and reused when it is gone.
    /// </summary>
    void UpdateProjectiles()
    {
        if (_client is null || _config is null || _projectileRoot is null) return;
        var live = _liveProjectiles;   // M40-T9: reused every frame
        live.Clear();
        foreach (var q in _frameProjectiles)   // fetched once per frame in HipFireOthers
        {
            live.Add(q.Id);
            if (_projectileIdsSeen.Add(q.Id))
            {
                _projectilesSeen++;
                if (q.Stage > 0) _chargedProjectilesSeen++;
                if (q.Thrown)
                {
                    _thrownSeen++;
                    if (_bot) GD.Print($"t {_connectedFor:F1} s: id {_client.PlayerId} sees thrown {ThrownLabel(q.Weapon)} #{q.Id} at {q.Position.X:F1},{q.Position.Y:F1},{q.Position.Z:F1} (thrown seen {_thrownSeen})");
                }
                if (_projectileIdsSeen.Count > 4096) _projectileIdsSeen.Clear();   // ids wrap at 65536; the counts stay
            }
            if (!_projectileNodes.TryGetValue(q.Id, out var node))
            {
                node = _projectilePool.Count > 0 ? _projectilePool.Pop() : NewProjectileNode();
                _projectileNodes[q.Id] = node;
            }
            var (mesh, material) = ProjectileLook(q.Weapon, q.Stage, q.Thrown);
            if (node.Mesh != mesh) node.Mesh = mesh;
            if (node.MaterialOverride != material) node.MaterialOverride = material;
            var drawn = BarrelDrawn(q);   // fx/barrel-origin: leaves the barrel, joins the server's path within 3 m (NetworkView.Barrel.cs)
            node.Position = new Vector3(drawn.X, drawn.Y, drawn.Z);
            // M25b: with the original trail drawing it, the placeholder ball stays only for grenades (their model is not drawn yet).
            Ph("projectiles: effects");
            bool trail = FxProjectile(q, ToGodot(drawn));
            Ph("projectiles and blasts");
            string label = q.Thrown ? "grenade" : q.Weapon >= 0 && q.Weapon < _config.Weapons.Count ? _config.Weapons[q.Weapon].Name : "";
            node.Visible = !(trail && !(q.Thrown || (label.Contains("grenade", StringComparison.OrdinalIgnoreCase) && !label.Contains("plasma", StringComparison.OrdinalIgnoreCase)))) || (trail && IsPlasmaShot(q.Weapon, q.Thrown) && !OldPlasmaBall);   // wfx: a plasma shot's solid ball shows with the data's trail around it
        }
        _goneProjectiles.Clear();
        foreach (var id in _projectileNodes.Keys) if (!live.Contains(id)) _goneProjectiles.Add(id);
        foreach (var id in _goneProjectiles)
        {
            var node = _projectileNodes[id];
            node.Visible = false;
            FxProjectileGone(id);
            BarrelForget(id);
            _projectileNodes.Remove(id);
            _projectilePool.Push(node);
        }

        // Blasts: each detonation once, when the interpolated view reaches it; one sphere per damaging blast
        // (ProjectileSpec.AllSplashes), and a disc for the lifetime of a lingering one (the melta bomb's; not offered).
        foreach (var d in _client.TakeDetonations())
        {
            _detonationsSeen++;
            if (d.Stage > 0) _chargedDetonationsSeen++;
            if (d.Thrown) _thrownDetonationsSeen++;
            var weapon = !d.Thrown && d.Weapon >= 0 && d.Weapon < _config.Weapons.Count ? _config.Weapons[d.Weapon] : null;
            var spec = Shots.ProjectileOf(_config, d.Weapon, d.Stage, d.Thrown);
            var splashes = spec?.AllSplashes.ToList() ?? [];
            string what = d.Thrown ? ThrownLabel(d.Weapon) : WeaponLabel(weapon) + (d.Stage > 0 ? " charged" : "");
            if (_bot) GD.Print($"t {_connectedFor:F1} s: id {_client.PlayerId} sees detonation of projectile #{d.Id} ({what}) at {d.Position.X:F1},{d.Position.Y:F1},{d.Position.Z:F1}, " +
                               $"blast radius {(splashes.Count == 0 ? "none" : string.Join(" + ", splashes.Select(x => x.Radius.ToString("F1") + (x.Lifetime > 0f && x.Period > 0f ? $" lingering {x.Lifetime:F0} s" : ""))))} (detonations seen {_detonationsSeen}, thrown {_thrownDetonationsSeen})");
            Ph("blasts: sound");
            SoundDetonation(d);   // M26b
            var colour = d.Thrown ? ThrownBlastColour(d.Weapon) : ProjectileColour(weapon, d.Stage);
            var position = new Vector3(d.Position.X, d.Position.Y, d.Position.Z);
            // M25b: the original explosion replaces the placeholder blast spheres (lingering areas stay; --placeholder-fx keeps the spheres).
            Ph("blasts: effects");
            bool original = FxDetonation(d);
            Ph("projectiles and blasts");
            // Without a splash (none read), a small flash so the detonation is still visible.
            if (splashes.Count == 0 && !original) AddBlast(position, colour, 0.5f);
            foreach (var splash in splashes)
            {
                if (splash.Lifetime > 0f && splash.Period > 0f) AddArea(position, colour, splash.Radius, splash.Lifetime);
                else if (!original) AddBlast(position, colour, splash.Radius > 0f ? splash.Radius : 0.5f);
            }
        }
    }

    /// <summary>A blast sphere; wide ones (beyond 8 m, e.g. the krak grenade's 30 m) are fainter so they do not hide the scene.</summary>
    void AddBlast(Vector3 position, Color colour, float radius)
    {
        if (NoMarkers) return;
        var (blast, blastMaterial) = _blastPool.Count > 0 ? _blastPool.Pop() : NewBlast();
        blast.Position = position;
        float alpha = 0.55f * Math.Clamp(8f / radius, 0.2f, 1f);
        blastMaterial.AlbedoColor = new Color(colour, alpha);
        blast.Visible = true;
        _blasts.Add((blast, blastMaterial, 0.0, radius, alpha));
    }

    /// <summary>A lingering blast area: a thin translucent disc of its radius lying at the blast point (game space, Z up).</summary>
    void AddArea(Vector3 position, Color colour, float radius, float lifetime)
    {
        if (NoMarkers) return;
        var material = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled, AlbedoColor = new Color(colour, 0.3f),
        };
        var node = new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = radius, BottomRadius = radius, Height = 0.08f, RadialSegments = 32, Rings = 1 },
            MaterialOverride = material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            // The cylinder's axis (local +Y) along game +Z.
            Transform = new Transform3D(new Basis(new Vector3(1, 0, 0), new Vector3(0, 0, 1), new Vector3(0, -1, 0)), position + new Vector3(0, 0, 0.05f)),
        };
        _projectileRoot!.AddChild(node);
        _areas.Add((node, material, 0.0, lifetime));
    }

    /// <summary>The HUD/log name of thrown item <paramref name="index"/> of <see cref="SimConfig.Thrown"/>.</summary>
    string ThrownLabel(int index) => index >= 0 && index < _config!.Thrown.Count ? EquipmentLabel(_config.Thrown[index]) : $"thrown item {index}";

    /// <summary>Placeholder blast colour of a thrown item: frag orange, the blind grenade (files: krak) pale (it only blinds, M37), melta bomb red-orange.</summary>
    Color ThrownBlastColour(int index)
    {
        string name = index >= 0 && index < _config!.Thrown.Count ? _config.Thrown[index].Name.ToLowerInvariant() : "";
        if (name.Contains("krak")) return new Color(0.85f, 0.85f, 0.9f);
        if (name.Contains("melta")) return new Color(1f, 0.35f, 0.1f);
        return new Color(1f, 0.6f, 0.2f);
    }

    /// <summary>Grows each blast sphere from a fifth of its radius to the full radius while it fades out over <see cref="BlastSeconds"/>.</summary>
    void UpdateBlasts(double delta)
    {
        for (int i = _blasts.Count - 1; i >= 0; i--)
        {
            var (node, material, age, radius, alpha) = _blasts[i];
            age += delta;
            if (age >= BlastSeconds)
            {
                node.Visible = false;
                _blastPool.Push((node, material));
                _blasts.RemoveAt(i);
                continue;
            }
            float k = (float)(age / BlastSeconds);
            float scale = radius * (0.2f + 0.8f * MathF.Sqrt(k));
            node.Scale = new Vector3(scale, scale, scale);
            var c = material.AlbedoColor;
            material.AlbedoColor = new Color(c.R, c.G, c.B, alpha * (1f - k));
            _blasts[i] = (node, material, age, radius, alpha);
        }
    }

    MeshInstance3D NewProjectileNode()
    {
        var node = new MeshInstance3D { CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        _projectileRoot!.AddChild(node);
        return node;
    }

    (MeshInstance3D, StandardMaterial3D) NewBlast()
    {
        var material = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,   // also seen from inside the blast
            NoDepthTest = false,
        };
        var node = new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 1f, Height = 2f, RadialSegments = 24, Rings = 12 },
            MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        _projectileRoot!.AddChild(node);
        return (node, material);
    }

    /// <summary>Placeholder colour by weapon kind: plasma blue-white (Chaos: purple), grenades dark, the rest orange.</summary>
    static Color ProjectileColour(WeaponSpec? weapon, int stage)
    {
        string name = (weapon is null ? "" : weapon.Name + " " + (Shots.ProjectileOf(weapon, stage)?.Blueprint ?? "")).ToLowerInvariant();
        if (name.Contains("plasma"))
        {
            // Owner's knowledge (2026-10-28): the loyalist plasma is blue, the Chaos plasma purple (PlasmaColour); the weapon's file name carries the side (csm_).
            var (r, g, b) = PlasmaColour.BallColour(weapon is not null && weapon.Name.StartsWith("csm_", StringComparison.OrdinalIgnoreCase));
            return new Color(r, g, b);
        }
        if (name.Contains("grenade")) return new Color(0.9f, 0.55f, 0.2f);   // the blast is orange; the grenade itself is drawn dark
        return new Color(1f, 0.6f, 0.2f);
    }

    /// <summary>
    /// The shared mesh and material of one weapon's shot (cached per weapon and stage), or of a thrown item
    /// (<paramref name="thrown"/>: <paramref name="weaponIndex"/> indexes <see cref="SimConfig.Thrown"/>): a small sphere,
    /// dark green for the frag grenade, grey for the krak grenade, dark red for the melta bomb (placeholders).
    /// </summary>
    (Mesh Mesh, Material Material) ProjectileLook(int weaponIndex, int stage, bool thrown = false)
    {
        string key = $"{(thrown ? "t" : "")}{weaponIndex}:{stage}";
        if (_projectileLooks.TryGetValue(key, out var look)) return look;
        if (thrown)
        {
            string item = weaponIndex >= 0 && weaponIndex < _config!.Thrown.Count ? _config.Thrown[weaponIndex].Name.ToLowerInvariant() : "";
            var albedo = item.Contains("krak") ? new Color(0.45f, 0.46f, 0.48f) : item.Contains("melta") ? new Color(0.35f, 0.08f, 0.05f) : new Color(0.12f, 0.25f, 0.1f);
            look = (new SphereMesh { Radius = 0.15f, Height = 0.3f, RadialSegments = 12, Rings = 6 },
                    new StandardMaterial3D { AlbedoColor = albedo, Metallic = 0.3f, Roughness = 0.6f, EmissionEnabled = true, Emission = albedo, EmissionEnergyMultiplier = 0.4f });
            _projectileLooks[key] = look;
            return look;
        }
        var weapon = weaponIndex >= 0 && weaponIndex < _config!.Weapons.Count ? _config.Weapons[weaponIndex] : null;
        string name = (weapon is null ? "" : weapon.Name + " " + (Shots.ProjectileOf(weapon, stage)?.Blueprint ?? "")).ToLowerInvariant();
        float radius = stage > 0 ? 0.35f : 0.2f;   // larger than the real shots (radius 0.25 or a point) so they show at a distance
        Material material;
        if (name.Contains("grenade") && !name.Contains("plasma"))
        {
            radius = 0.15f;
            material = new StandardMaterial3D { AlbedoColor = new Color(0.12f, 0.13f, 0.12f), Metallic = 0.4f, Roughness = 0.5f,
                                                EmissionEnabled = true, Emission = new Color(0.9f, 0.3f, 0.1f), EmissionEnergyMultiplier = 0.12f };
        }
        else if (IsPlasmaShot(weaponIndex, false) && !OldPlasmaBall) material = PlasmaBallMaterial(ProjectileColour(weapon, stage), radius);   // wfx (owner's item 25): a solid-ish roiling ball
        else material = GlowMaterial(ProjectileColour(weapon, stage), stage > 0 ? 4f : 2.5f);
        bool plasmaBall = IsPlasmaShot(weaponIndex, false) && !OldPlasmaBall;
        look = (new SphereMesh { Radius = radius, Height = 2f * radius, RadialSegments = plasmaBall ? 32 : 12, Rings = plasmaBall ? 16 : 6 }, material);
        _projectileLooks[key] = look;
        return look;
    }

    /// <summary>
    /// Own player's charged fire for the log, from the predicted state: a charge that ends with a shot pending or ammunition
    /// spent counts as a charged release (predicted; the server's log is authoritative). Also times grenades out.
    /// </summary>
    void LogCharge(PlayerState me, float delta)
    {
        _grenadesOutFor = me.ProjectilesOut > 0 ? _grenadesOutFor + delta : 0f;
        var weapon = Loadouts.InHand(_config!, me);
        int stage = _previousMeSeen ? Combat.ChargeStage(weapon?.Charge, _previousMe.ChargeTime, firing: true) : 0;
        // A tap (M31: on every charge weapon) fires the basic shot: no stage reached, so not counted. A charge that
        // reached its stage within the last frame shows the stage of the frame before; the pending shot then names it.
        if (me.PendingShot > 0) stage = Math.Max(stage, me.PendingShot);
        if (_previousMeSeen && _previousMe.Charging && !me.Charging && me.Slot == _previousMe.Slot && stage > 0 && (me.PendingShot > 0 || me.Clip < _previousMe.Clip))
        {
            _chargedReleases++;
            string stageName = weapon?.Charge is { } ch && stage > 0 ? ch.Stages[stage - 1].Name : $"stage {stage}";
            if (_bot) GD.Print($"t {_connectedFor:F1} s: id {_client!.PlayerId} releases a charged shot: {WeaponLabel(weapon)} {stageName} after {_previousMe.ChargeTime:F2} s (charged releases {_chargedReleases}, predicted: a pending shot can still be cancelled by death or melee); {AmmoText(me)}{HeatText(me)}");
        }
        _previousMe = me; _previousMeSeen = true;
    }

    /// <summary>Charge state for the log: " charging 0.80 s" / " pending shot".</summary>
    static string ChargeText(PlayerState me) =>
        (me.Charging ? $" charging {me.ChargeTime:F2} s" : "") + (me.PendingShot > 0 ? $" pending shot {me.PendingLeft:F2} s" : "") +
        (me.ProjectilesOut > 0 ? $" grenades out {me.ProjectilesOut}" : "");

    /// <summary>Thrown item state for the log: "frag grenade 1/2 cooldown 0.4 s throwing 0.10 s".</summary>
    string ThrowText(PlayerState me) => Loadouts.Equipment(_config!, me) is { } item
        ? $"{EquipmentLabel(item)} {me.Grenades}/{item.Count}{(me.GrenadeCooldown > 0f ? $" cooldown {me.GrenadeCooldown:F1} s" : "")}{(me.Throwing ? $" throwing {me.ThrowTime:F2} s" : "")};"
        : "no equipment;";

    /// <summary>
    /// Own player (predicted state): counts and logs throw starts and releases (one fewer carried while the item stays the
    /// same), and draws a melta cone shot (<see cref="ShowConeFlash"/>) when the clip of a <see cref="DeliveryKind.Cone"/>
    /// weapon in hand drops without a switch (cone shots are not on the wire; the prediction fires them locally).
    /// </summary>
    void WatchThrowAndCone(PlayerState me)
    {
        if (!_previousMeSeen) return;
        var p = _previousMe;
        if (me.Alive && me.Throwing && !p.Throwing)
        {
            _throwsStarted++;
            if (_bot) GD.Print($"t {_connectedFor:F1} s: id {_client!.PlayerId} starts a throw ({ThrowText(me)} throws started {_throwsStarted})");
        }
        if (me.Alive && p.Alive && me.Equipment == p.Equipment && me.Grenades < p.Grenades)
        {
            _throwsReleased += p.Grenades - me.Grenades;
            if (_bot) GD.Print($"t {_connectedFor:F1} s: id {_client!.PlayerId} releases a grenade ({ThrowText(me)} released {_throwsReleased}; predicted)");
        }
        // A recharged round that arrives and is fired within one frame leaves the clip unchanged but the reserve lower.
        bool shot = me.Clip < p.Clip || (me.Reserve < p.Reserve && me.Clip <= p.Clip);
        if (me.Alive && p.Alive && me.Slot == p.Slot && me.Class == p.Class && shot
            && Loadouts.InHand(_config!, me) is { Delivery: DeliveryKind.Cone, Cone: { } cone } weapon)
        {
            _meltaShots++;
            if (!FxActive) ShowConeFlash(me, cone);   // M25b: the original blast beam replaces the placeholder cone
            if (_bot) GD.Print($"t {_connectedFor:F1} s: id {_client!.PlayerId} fires a cone shot ({WeaponLabel(weapon)}; melta shots {_meltaShots}; predicted) {AmmoText(me)}");
        }
    }

    /// <summary>
    /// Placeholder cone of a melta shot for <see cref="ConeFlashSeconds"/>: the volume <see cref="Cones.Inside"/> tests, a
    /// truncated cone from the eye point along the aim to <see cref="ConeSpec.Length"/> ahead, its apex
    /// <see cref="ConeSpec.StartLength"/> behind the eye, half angle <see cref="ConeSpec.HalfAngle"/>.
    /// </summary>
    void ShowConeFlash(PlayerState me, ConeSpec cone)
    {
        if (_projectileRoot is null || _config is null || cone.Length <= 0f || NoMarkers) return;
        float tan = MathF.Tan(cone.HalfAngle);
        var material = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            AlbedoColor = new Color(1f, 0.55f, 0.15f, 0.35f),
        };
        // CylinderMesh runs along its local +Y: bottom (−Y) at the eye, top (+Y) at Length ahead.
        var mesh = new CylinderMesh
        {
            BottomRadius = MathF.Max(0.01f, cone.StartLength * tan), TopRadius = MathF.Max(0.01f, (cone.StartLength + cone.Length) * tan),
            Height = cone.Length, RadialSegments = 24, Rings = 1, CapTop = false, CapBottom = false,
        };
        var origin = me.Position + new System.Numerics.Vector3(0, 0, _config.EyeHeight);
        var d = Combat.AimDirection(me.Yaw, me.Pitch);
        var axis = new Vector3(d.X, d.Y, d.Z).Normalized();
        var side = MathF.Abs(axis.Z) > 0.99f ? new Vector3(1, 0, 0) : axis.Cross(new Vector3(0, 0, 1)).Normalized();
        var third = side.Cross(axis).Normalized();
        var node = new MeshInstance3D
        {
            Mesh = mesh, MaterialOverride = material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            // Inside the game-space root: columns x = side, y = aim, z = third (right-handed: side × aim = third).
            Transform = new Transform3D(new Basis(side, axis, third), new Vector3(origin.X, origin.Y, origin.Z) + axis * (cone.Length * 0.5f)),
        };
        _projectileRoot.AddChild(node);
        _coneFlashes.Add((node, 0.0));
    }

    /// <summary>Fades and removes the melta cone flashes.</summary>
    void UpdateConeFlashes(double delta)
    {
        for (int i = _coneFlashes.Count - 1; i >= 0; i--)
        {
            var (node, age) = _coneFlashes[i];
            age += delta;
            if (age >= ConeFlashSeconds) { node.QueueFree(); _coneFlashes.RemoveAt(i); continue; }
            if (node.MaterialOverride is StandardMaterial3D m) m.AlbedoColor = new Color(m.AlbedoColor, 0.35f * (1f - (float)(age / ConeFlashSeconds)));
            _coneFlashes[i] = (node, age);
        }
    }

    /// <summary>Lingering blast areas (a translucent disc of the area's radius at the blast point) until their lifetime ends.</summary>
    void UpdateAreas(double delta)
    {
        for (int i = _areas.Count - 1; i >= 0; i--)
        {
            var (node, material, age, lifetime) = _areas[i];
            age += delta;
            if (age >= lifetime) { node.QueueFree(); _areas.RemoveAt(i); continue; }
            material.AlbedoColor = new Color(material.AlbedoColor, 0.3f * (1f - 0.5f * (float)(age / lifetime)));
            _areas[i] = (node, material, age, lifetime);
        }
    }

    /// <summary>
    /// The nearest living remote enemy as last shown (interpolated), or null: with teams (the own player has a side)
    /// allies are skipped (M15); without teams everyone is a target.
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

    /// <summary>A character wearing <paramref name="assets"/> (a built look, or the default marine as the placeholder: <see cref="Looks.LookKeys.Placeholder"/>); <paramref name="lookKey"/> names it for <see cref="EnsureModel"/>.</summary>
    CharacterViewer Spawn(int playerId, bool local, MarineAssets assets, string lookKey)
    {
        // An Ork starts in its own idle (the marine's clip names are not in its rig); it is posed every frame from then on (PlaceOrk).
        string idle = _orks is not null && ReferenceEquals(assets.Rig, _orks.Rig) ? assets.FindAnimation(RigProfiles.OrkBoy.Clips.IdleCandidates, "Idle") ?? CharacterViewer.DefaultAnimation : _idleAnimation ?? CharacterViewer.DefaultAnimation;
        var character = new CharacterViewer(assets, idle, gameSpaceRoot: false) { Name = $"Player{playerId}" };
        character.SetMeta("look_key", lookKey);
        _gameSpace!.AddChild(character);
        // Label3D draws in Godot space; inside the game-space parent, +Z (game up) puts it over the head.
        character.AddChild(new Label3D
        {
            Name = "Nameplate",
            Text = local ? $"{HudName(playerId)} (you)" : HudName(playerId),   // renamed when the scoreboard names it (ApplyTeamLook)
            Position = new Vector3(0, 0, 2.6f),
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            FontSize = 48,
            PixelSize = 0.006f,
            Modulate = local ? OwnColour : Colors.White,
        });
        // M15: the side's ring at the feet (placeholder; coloured once the side is known, ApplyTeamLook).
        character.AddChild(NewSideRing());
        // Placeholder charging glow: a small emissive sphere in front of the chest, shown while the player is charging
        // (game space: +Y is the marine's front, +Z up). Not the original charge effect.
        character.AddChild(new MeshInstance3D
        {
            Name = "ChargeGlow",
            Mesh = new SphereMesh { Radius = 0.18f, Height = 0.36f, RadialSegments = 12, Rings = 6 },
            MaterialOverride = GlowMaterial(new Color(0.55f, 0.8f, 1f), 3f),
            Position = new Vector3(0.15f, 0.6f, 1.3f),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false,
        });
        return character;
    }

    static StandardMaterial3D GlowMaterial(Color colour, float energy) => new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        AlbedoColor = colour,
        EmissionEnabled = true,
        Emission = colour,
        EmissionEnergyMultiplier = energy,
    };

    /// <summary>Positions are game space (metres, Z up); yaw rotates about +Z, and yaw 0 faces +Y like the model.</summary>
    /// <param name="throwTime">Seconds into a throw under way (own player: the simulation's ThrowTime; others: since their
    /// slim Throwing flag rose), or null when not throwing: the toss clip is posed at that time when the rig has it.</param>
    /// <param name="smooth">Remote players: the smoothed time of the reaction, charge or attack clip (<see cref="SmoothRemoteClip"/>), used instead of the snapshot's value.</param>
    void Place(CharacterViewer character, PlayerState state, float? throwTime = null, bool own = false, SmoothClip? smooth = null)
    {
        // qc/reference-pass: the transition probe measures the melee prop bones (RIG_Prop_M_*) only where a mesh can hang on them: a marine with a two-handed melee weapon, and every
        // Ork (the boy's shoota rides RIG_Prop_M_Chest); on other marines they carry nothing.
        if (TransitionProbe.Enabled) character.SetMeta(TransitionProbe.TwoHandedMeta, IsOrkCharacter(character) || MeleeWargear(state)?.Contains("_2hand_", StringComparison.OrdinalIgnoreCase) == true);
        if (IsOrkCharacter(character)) { PlaceOrk(character, state, throwTime, own, smooth); return; }   // Ork O7a (NetworkView.Ork.cs): the boy's rig has its own clips
        character.ClearPoseLayers();  // M27: the recoil layer is laid again by PlaceLocomotion when it applies (animation pass 1: every layer slot, CharacterViewer.Layers.cs; the fall slots by FallLayers)
        character.ClearTurnPose();    // M40-T5: so are the turn layer and the spine twist
        var fallTrack = LocoOf(state.PlayerId);
        FallObserve(state, fallTrack, own);   // animation pass 1 (NetworkView.Fall.cs): falling after 2 unsupported ticks, the landing at touchdown
        UpperObserve(character, state, fallTrack, own);   // animation pass 2 (NetworkView.UpperBody.cs): the weapon switch begins, the flinch layer
        // Heavy hop (M21): the simulation lifts the body by the hop's height and the clip already lifts the feet, so the model
        // is drawn that height lower to show the hop once. Not the jump-pack dash (M44): its clip is in place, the body carries the rise.
        float hop = state.Alive && Evade.Move(state, _config!) is { LiftsPose: true } hopMove ? hopMove.HeightAt(Evade.Time(state, _config!)) : 0f;
        // The own model turns with the view at the frame rate, not with the 30 Hz predicted state (M36, NetworkView.Turning.cs).
        float aimYaw = own ? DisplayYaw(state) : state.Yaw;
        float yaw = BodyYawFor(character, state, own, aimYaw, throwTime);   // M40-T5 (NetworkView.TurnInPlace.cs): the body is planted while the aim moves; else the aim's yaw
        if (TurnProbe.Enabled) character.SetMeta("aim_yaw", aimYaw);   // the probe compares the chest with the aim
        character.Transform = new Transform3D(new Basis(new Vector3(0, 0, 1), yaw), new Vector3(state.Position.X, state.Position.Y, state.Position.Z - hop));
        if (character.GetNodeOrNull<Node3D>("ChargeGlow") is { } glow) glow.Visible = state.Alive && state.Charging && !NoMarkers;
        // Reacting (M28): the reaction clip at the reaction's time; a reaction ends any attack, throw, evade or jump on the ground.
        character.ClipFootLock = false;   // anim/melee-flow: only the strikes and the charge below hold their put-down feet (CharacterViewer.ClipFootLock.cs)
        if (ReactionPose(state) is { } reaction && character.Pose(reaction.Clip, MathF.Max(0f, (smooth is { Kind: ClipKind.Reaction } sr ? sr.Time : reaction.Time) - HitStopHold(state.PlayerId)), ClipBlends.Reaction)) return;   // feel/melee-lock: the hit-stop holds it
        // Charging (M29): the rush or its collision stop, at the phase's time.
        // feel/jump-pack (the owner, 2026-10-29): a charge held in the air off a ledge shows the fall pose (the locomotion path below), not its clip frozen (NetworkView.Fall.cs).
        // anim/melee-flow: nor, once landed, the rush clip again in that charge (RushRebashHeld: no second bash).
        bool rebashHeld = RushRebashHeld(state);
        character.ClipFootLock = ClipFootLockFor(state, melee: false);
        character.ClipRootSpeed = float.NaN;
        if (!(ChargeHeldInAir(state) && fallTrack.ChargeFall) && !rebashHeld && RushPose(state) is { } rush)   // sim/combat-pass: the held charge keeps its clip until FallRules.ChargeFallDelay
        {
            var (rushClip, rushTime) = HammerRush(state, rush.Clip, smooth is { Kind: ClipKind.Rush } sh ? sh.Time : rush.Time);   // anim/melee-flow: the hammer's own sprint attack
            rushTime = MathF.Max(0f, rushTime - HitStopHold(state.PlayerId));   // anim/melee-interaction: the charge connect's hit-stop holds the collision stop too
            if (character.Pose(rushClip, rushTime, ClipBlends.RushFor(rush.Clip))) return;
        }
        // Attacking: the step's original clip (light or heavy, M28) at the simulation's attack time (seeked, not free-running).
        // A jump blocks melee attacks, so this only overlaps a landing.
        // From the step's MoveAt, while the body moves, the locomotion has the legs and the clip only the upper body (NetworkView.Handoff.cs, M40-T4 follow-up).
        // feel/melee-lock (NetworkView.MeleeLock.cs): the blend of the attack's distance variants at its start distance (the lower one posed, the upper laid
        // over it), held back by a hit-stop.
        character.ClipFootLock = ClipFootLockFor(state, melee: true);
        if (AttackClip(state) is { } clip)
        {
            float attackTime = MathF.Max(0f, (smooth is { Kind: ClipKind.Attack } sa ? sa.Time : state.MeleeTime) - HitStopHold(state.PlayerId));
            var (baseClip, baseTime, overClip, overTime, overWeight) = AttackVariants(state, own, clip, attackTime);
            character.ClipRootSpeed = NoMeleeFlow ? float.NaN : MeleeRootSpeed(state, attackTime, ClipLength(baseClip, 0f));   // anim/melee-flow: the clip foot lock's still-foot test
            // anim/melee-flow (NetworkView.MeleeFlow.cs): the blend into the strike from its graph (entry or chain); noted every frame the strike shows, handed over or not.
            float entryBlend = NoMeleeFlow ? ClipBlends.Melee(state.MeleeStep) : MeleeEntryBlend(state);
            if (HandoffAttack(character, state, own, baseClip, attackTime, baseTime)) return;
            if (character.Pose(baseClip, baseTime, entryBlend))
            {
                if (overClip is not null) character.SetLayer(PoseSlots.MeleeVariant, overClip, overTime, overWeight, additive: false);
                return;
            }
        }
        character.ClipFootLock = false;
        // Jump pack: take-off, flight, dive and impact win over a throw (the toss clip is a standing one) ...
        if (ShowJumpPose(character, JumpPose(state, airborneOnly: true))) { JumpUpperLayers(character, state, throwTime); return; }   // pass 2: the wobble and the upper-body actions in flight
        // Evade roll (protocol 13): the class's roll clip at the shown time; the simulation moves the body, the clip is in place.
        // sim/movement-fixes: a roll or hop falling off a drop shows the fall pose after FallRules.ChargeFallDelay in the air (NetworkView.Fall.cs).
        if (!(EvadeFallingInAir(state) && fallTrack.ChargeFall) && EvadePose(state) is { } roll && character.Pose(roll.Clip, roll.Time, ClipBlends.Evade)) return;
        // Throwing: the toss clip at the throw's time (the simulation ends the throw at ThrowDuration, before the clip's end).
        if (throwTime is { } tt && _tossAnimation is not null && TossShown(state) && (HandoffThrow(character, state, own, _tossAnimation, tt) || character.Pose(_tossAnimation, tt, ClipBlends.Throw))) return;
        // anim/feel-pass (NetworkView.Feel.cs): a slash whose marine moved on keeps its follow-through on the upper body over the strike's exit.
        if (SlashHandoff(character, state, own)) return;
        // ... the landing and the pound's get-up lose to it.
        if (ShowJumpPose(character, JumpPose(state, airborneOnly: false))) return;
        // The heavy bolter's brace (fidelity gap 7, NetworkView.Brace.cs): the entry, the braced idle, the exit over the legs.
        if (BracePose(character, state, own)) return;
        // Aim mode (held aim, every player's flag): the weapon class's aim stance bent by the pitch; else the idle / run choice.
        // A falling marine stands in the air (the graph's Dropping state has no direction or speed): the locomotion is posed with no horizontal velocity, the fall's layers go over it.
        PlaceLocomotion(character, FallBaseState(state, fallTrack), own);
        FallLayers(character, state, fallTrack);
        ReloadPose(character, state);   // M35: the reload clip over the legs' movement
        SwitchLayer(character, state, fallTrack);   // pass 2: the weapon switch over the upper body
        VentPose(character, state, own);   // fidelity gap 7: the vent clip over the upper body
        StimPose(character, state, throwTime);   // perks/inert-2: the stim's clip over the upper body (NetworkView.Stims.cs)
        HammerDrawPose(character, state);   // anim/melee-flow: the hammer taken back into both hands after the pistol (NetworkView.MeleeFlow.cs)
    }

    /// <summary>
    /// Per-class HUD labels and melee clips. The clips are <see cref="MpClasses.MeleeAnimations"/> of each class (without
    /// classes from the install, the single combo's <see cref="MeleeTuning.Animations"/>), checked against the rig's
    /// animation list; a missing or unknown clip is noted once and that step keeps the idle/run choice.
    /// </summary>
    void SetUpClasses(GameplayTuning tuning)
    {
        var config = _config!;
        int count = config.Classes.Count;
        _classLabels = new string[count];
        _meleeLabels = new string[count];
        _meleeAnimations = new string?[count][];
        var noted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = _assets!.Model.Rig.AnimationNames;
        for (int i = 0; i < count; i++)
        {
            var mp = _classes is { } k && i < k.Classes.Count ? k.Classes[i] : null;
            _classLabels[i] = mp?.Key ?? config.Classes[i].Name;
            int steps = Loadouts.Melee(config, i).Count;
            IReadOnlyList<string>? clips = _classes is { } c && i < c.MeleeAnimations.Count ? c.MeleeAnimations[i]
                : _classes is null ? tuning.Melee?.Animations : null;
            // The melee weapon the class's combo was read from: MpClasses takes the first supported one in wargear order (Weapons C: the
            // heavy classes' core ability).
            string? melee = mp is null ? null : config.Classes[i].MeleeChoices.FirstOrDefault()?.Wargear;
            _meleeLabels[i] = steps == 0 ? "none" : melee is not null ? WeaponLabel(melee) : tuning.Melee is not null && _classes is null ? "combat knife" : "melee";
            var result = new string?[steps];
            if (config.Classes[i].Race == Race.Orks) { _meleeAnimations[i] = result; continue; }   // Ork O7c: the boy's clips are checked against the Ork rig (NetworkView.OrkActions.cs), not the marine's
            if (clips is null && steps > 0 && noted.Add("#none")) _notes.Add("melee animations not read from the install; attacks show the idle/run animation");
            for (int step = 0; step < steps && clips is not null; step++)
            {
                string? name = step < clips.Count ? clips[step] : null;
                if (name is null) { _notes.Add($"class '{_classLabels[i]}' melee step {step + 1}: no animation named; it shows the idle/run animation"); continue; }
                if (!names.Contains(name, StringComparer.OrdinalIgnoreCase)) { if (noted.Add(name)) _notes.Add($"melee animation '{name}' (class '{_classLabels[i]}', step {step + 1}) not in the rig; it shows the idle/run animation"); continue; }
                if (_assets.Animation(name) is null) { if (noted.Add(name)) _notes.Add($"melee animation '{name}' (class '{_classLabels[i]}', step {step + 1}) could not be decoded; it shows the idle/run animation"); continue; }
                result[step] = name;
            }
            _meleeAnimations[i] = result;
        }

        // Every melee choice's clips, by its wargear (the combo a player chose; MpClasses.MeleeAnimationsByWeapon).
        if (_classes is null) return;
        foreach (var combo in config.Classes.Where(k => k.Race != Race.Orks).SelectMany(k => k.MeleeChoices))   // Ork O7c: the Ork's choppa is not a marine-rig clip set
        {
            if (_meleeAnimationsByWeapon.ContainsKey(combo.Wargear)) continue;
            if (!_classes.MeleeAnimationsByWeapon.TryGetValue(combo.Wargear, out var clips))
            {
                _notes.Add($"melee weapon {combo.Wargear}: no animations named; its attacks use its class's default clips");
                continue;
            }
            var result = new string?[combo.Steps.Count];
            for (int step = 0; step < result.Length; step++)
            {
                string? name = step < clips.Count ? clips[step] : null;
                if (name is null) { _notes.Add($"melee weapon {combo.Wargear} step {step + 1}: no animation named; it shows the idle/run animation"); continue; }
                if (!names.Contains(name, StringComparer.OrdinalIgnoreCase) || _assets.Animation(name) is null)
                {
                    if (noted.Add(name)) _notes.Add($"melee animation '{name}' ({combo.Wargear}, step {step + 1}) not in the rig or not decodable; it shows the idle/run animation");
                    continue;
                }
                result[step] = name;
            }
            _meleeAnimationsByWeapon[combo.Wargear] = result;
        }
    }

    /// <summary>
    /// The throw's clip: the toss graph's clip as <see cref="MpClasses.Throw"/> read it (<see cref="DefaultTossAnimation"/>
    /// without it), used only when the rig has it and it decodes; otherwise a note, and throws show the idle/run animation.
    /// </summary>
    void SetUpToss()
    {
        string name = _classes?.Throw?.Animation ?? DefaultTossAnimation;
        if (!_assets!.Model.Rig.AnimationNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            _notes.Add($"throw animation '{name}' not in the rig; throws show the idle/run animation");
        else if (_assets.Animation(name) is null)
            _notes.Add($"throw animation '{name}' could not be decoded; throws show the idle/run animation");
        else _tossAnimation = name;
    }

    /// <summary>The wargear of the player's melee combo (its choice when valid, else the class default), or null.</summary>
    string? MeleeWargear(PlayerState s)
    {
        int choice = Loadouts.MeleeChoiceOf(_config!, s);
        return choice >= 0 && Loadouts.ClassOf(_config!, s.Class) is { } loadout ? loadout.MeleeChoices[choice].Wargear : null;
    }

    /// <summary>The clips of the player's melee combo: its chosen weapon's, else its class's default ones; null when none.</summary>
    string?[]? MeleeClips(PlayerState s)
    {
        if (_config is null) return null;
        if (MeleeWargear(s) is { } wargear && _meleeAnimationsByWeapon.TryGetValue(wargear, out var byWeapon)) return byWeapon;
        return s.Class >= 0 && s.Class < _meleeAnimations.Length ? _meleeAnimations[s.Class] : null;
    }

    /// <summary>
    /// Resolves <c>--primary</c>, <c>--pistol</c> and <c>--melee</c> (or <c>--bot-loadout random</c>) against the class
    /// that will be played into the indices <see cref="ClientOptions"/> sends. Without <c>--class</c>, names are matched
    /// against class 0 and that class is then requested explicitly, so the printed choice is the one played (the
    /// server's default class is not known here).
    /// </summary>
    void ChooseLoadout()
    {
        var c = _config!;
        bool any = _loadout.Primary is not null || _loadout.Pistol is not null || _loadout.Melee is not null || _loadout.Equipment is not null || _loadout.Random;
        if (!any || c.Classes.Count == 0) return;
        int playerClass = _requestedClass != Protocol.ServerDefaultClass ? _requestedClass : 0;
        if (_requestedClass == Protocol.ServerDefaultClass)
        {
            _requestedClass = (byte)playerClass;
            GD.Print($"loadout options without --class: choosing from class 0 '{ClassLabel(0)}' and requesting it");
        }
        byte Pick(string option, string? name, IReadOnlyList<string> labels, string category)
        {
            if (_loadout.Random && name is null)
            {
                if (labels.Count == 0) return Protocol.DefaultChoice;
                int r = System.Random.Shared.Next(labels.Count);
                GD.Print($"--bot-loadout random: {option} choice {r} '{labels[r]}' (class '{ClassLabel(playerClass)}')");
                return (byte)r;
            }
            if (name is null) return Protocol.DefaultChoice;
            int index = ResolveChoice(name, labels, category);
            if (index >= 0 && index < Protocol.DefaultChoice)
            {
                GD.Print($"--{option} '{name}': requesting {option} choice {index} '{labels[index]}' (class '{ClassLabel(playerClass)}')");
                return (byte)index;
            }
            _notes.Add($"--{option} '{name}' matches no {option} of class '{ClassLabel(playerClass)}' (valid: {(labels.Count == 0 ? "none" : string.Join(", ", labels))}); the class default is used");
            return Protocol.DefaultChoice;
        }
        _requestedPrimary = Pick("primary", _loadout.Primary, ChoiceLabels(playerClass, WeaponSlot.Ranged), "ranged");
        _requestedPistol = Pick("pistol", _loadout.Pistol, ChoiceLabels(playerClass, WeaponSlot.Pistol), "pistol");
        _requestedMelee = Pick("melee", _loadout.Melee, MeleeChoiceLabels(playerClass), "melee");
        // The blind grenade is "krak" in the files (M37): --equipment krak and --equipment blind both pick it.
        _requestedEquipment = Pick("equipment", _loadout.Equipment?.Replace("krak", "blind", StringComparison.OrdinalIgnoreCase), EquipmentChoiceLabels(playerClass), "grenade");
    }

    /// <summary>Friendly names of a class's thrown items ("frag grenade"), in <see cref="ClassLoadout.EquipmentChoices"/> order.</summary>
    List<string> EquipmentChoiceLabels(int playerClass) =>
        Loadouts.EquipmentChoices(_config!, playerClass).Select(i => i >= 0 && i < _config!.Thrown.Count ? EquipmentLabel(_config.Thrown[i]) : $"item {i}").ToList();   // the player-facing name ("blind grenade")

    /// <summary>The HUD name of a thrown item ("frag grenade"), or "none".</summary>
    static string EquipmentLabel(ThrownSpec? item) => item is null ? "none" : item.PlayerName ?? WeaponLabel(item.Name);   // M37: "blind grenade" (files: krak)

    /// <summary>Friendly names of a class's weapon choices in <paramref name="slot"/>, in <see cref="Loadouts.Choices"/> order.</summary>
    List<string> ChoiceLabels(int playerClass, WeaponSlot slot) =>
        Loadouts.Choices(_config!, playerClass, slot).Select(i => WeaponLabel(_config!.Weapons[i])).ToList();

    /// <summary>Friendly names of a class's melee choices, in <see cref="ClassLoadout.MeleeChoices"/> order.</summary>
    List<string> MeleeChoiceLabels(int playerClass) =>
        Loadouts.ClassOf(_config!, playerClass)?.MeleeChoices.Select(m => WeaponLabel(m.Wargear)).ToList() ?? [];

    /// <summary>Aliases for weapon names (compact form, no separators) to the wargear's own words.</summary>
    static readonly Dictionary<string, string> ChoiceAliases = new(StringComparer.Ordinal)
    {
        ["stalker"] = "sniperrifle", ["stalkerbolter"] = "sniperrifle", ["stalkerpatternbolter"] = "sniperrifle", ["sniper"] = "sniperrifle",
        ["lascannon"] = "lascannon",
    };

    /// <summary>
    /// The index in <paramref name="labels"/> (friendly wargear names, e.g. "plasma gun") of a user-given weapon name, or -1.
    /// Case, spaces, '_' and '-' do not matter. In order: a list index ("1"); the same name ("plasma_gun", "las cannon" or
    /// "lascannon"); the same words in another order ("gold chainsword"); the same without the slot's category word
    /// <paramref name="category"/> ("bolter pistol" for the pistol slot's "bolter"); an alias ("sniper"/"stalker" for the
    /// sniper rifle); the only name containing it ("plasma" when just one name does).
    /// </summary>
    public static int ResolveChoice(string name, IReadOnlyList<string> labels, string? category = null)
    {
        static string[] Words(string s) => s.ToLowerInvariant().Split([' ', '_', '-'], StringSplitOptions.RemoveEmptyEntries);
        if (int.TryParse(name.Trim(), out int number)) return number >= 0 && number < labels.Count ? number : -1;
        var words = Words(name);
        string compact = string.Concat(words);
        if (compact.Length == 0) return -1;
        var compacts = labels.Select(l => string.Concat(Words(l))).ToList();
        int exact = compacts.IndexOf(compact);
        if (exact >= 0) return exact;
        var sorted = string.Join(' ', words.Order());
        for (int i = 0; i < labels.Count; i++) if (string.Join(' ', Words(labels[i]).Order()) == sorted) return i;
        if (category is not null && words.Contains(category) && words.Length > 1)
        {
            var rest = string.Join(' ', words.Where(w => w != category));
            if (ResolveChoice(rest, labels) is var stripped and >= 0) return stripped;
        }
        if (ChoiceAliases.TryGetValue(compact, out var alias) && compacts.IndexOf(alias) is var aliased and >= 0) return aliased;
        var containing = Enumerable.Range(0, labels.Count).Where(i => compacts[i].Contains(compact, StringComparison.Ordinal)).ToList();
        return containing.Count == 1 ? containing[0] : -1;
    }

    /// <summary>
    /// The class index for a <c>--class</c> name, or -1. Accepted, case-insensitively (spaces, '-' and '_' alike): the
    /// class key ("sm - tactical"), the part after the key's race prefix ("tactical"), or the data's role ("assault",
    /// "tactical", "heavy"), optionally preceded by a race word ("chaos tactical", "sm heavy"); "devastator" is the Space
    /// Marine heavy class, "havoc" the Chaos heavy one and "raptor" the Chaos assault one. Without a race word, a Space
    /// Marine class is preferred. Without classes from the install, only the config's class names match.
    /// </summary>
    public static int ResolveClass(string name, SimConfig config, MpClasses? classes)
    {
        static string Normal(string key) => string.Join(' ', key.ToLowerInvariant().Split([' ', '_', '-'], StringSplitOptions.RemoveEmptyEntries));
        string wanted = Normal(name);
        for (int i = 0; i < config.Classes.Count; i++)
            if (Normal(config.Classes[i].Name) == wanted || (classes is not null && i < classes.Classes.Count && Normal(classes.Classes[i].Key) == wanted)) return i;
        if (classes is null) return -1;

        string? race = null;
        foreach (var (word, r) in new[] { ("chaos space marine ", "chaos"), ("chaos ", "chaos"), ("csm ", "chaos"), ("space marine ", "space_marine"), ("sm ", "space_marine") })
            if (wanted.StartsWith(word, StringComparison.Ordinal)) { race = r; wanted = wanted[word.Length..]; break; }
        (wanted, race) = wanted switch
        {
            "devastator" => ("heavy", race ?? "space_marine"),
            "havoc" => ("heavy", race ?? "chaos"),
            "raptor" => ("assault", race ?? "chaos"),
            _ => (wanted, race),
        };
        int best = -1;
        for (int i = 0; i < Math.Min(classes.Classes.Count, config.Classes.Count); i++)
        {
            var c = classes.Classes[i];
            string key = Normal(c.Key);
            string shortName = key.Contains(' ') ? key[(key.IndexOf(' ') + 1)..] : key;   // "sm tactical" to "tactical"
            if (race is not null && !c.Race.Equals(race, StringComparison.OrdinalIgnoreCase)) continue;
            if (shortName != wanted && !c.Role.Equals(wanted, StringComparison.OrdinalIgnoreCase)) continue;
            if (race is not null || c.Race.Equals("space_marine", StringComparison.OrdinalIgnoreCase)) return i;
            if (best < 0) best = i;
        }
        return best;
    }

    /// <summary>A wargear name without its race and category prefix: "sm_mp_pistol_bolt_pistol" gives "bolt pistol".</summary>
    static string WeaponLabel(string wargear)
    {
        // Ork O5 review: the Ork wargear's own names (the AI files carry no MP display name; ours) instead of "ork coop boy 2hand shoota rapid".
        if (wargear.Equals(OrkClass.Shoota, StringComparison.OrdinalIgnoreCase)) return "shoota";
        if (wargear.Equals(OrkClass.Slugga, StringComparison.OrdinalIgnoreCase)) return "slugga";
        if (wargear.Equals(OrkClass.Choppa, StringComparison.OrdinalIgnoreCase)) return "choppa";
        if (wargear.Equals(OrkClass.Stikkbomb, StringComparison.OrdinalIgnoreCase)) return "stikkbomb";
        // Weapons C: the Devastator's and the Havoc's melee is their core ability's kick and stomp.
        if (wargear.EndsWith("_core_abil_heavy_evade", StringComparison.OrdinalIgnoreCase)) return "kick / stomp";
        string label = wargear;
        int mp = label.IndexOf("_mp_", StringComparison.OrdinalIgnoreCase);
        if (mp >= 0) label = label[(mp + 4)..];
        foreach (var category in new[] { "ranged_", "pistol_", "exotic_", "melee_", "eq_" })
            if (label.StartsWith(category, StringComparison.OrdinalIgnoreCase)) { label = label[category.Length..]; break; }
        return label.Replace('_', ' ');
    }

    /// <summary>The HUD name of a weapon; the single-weapon fallback ("default") is the bolter's values.</summary>
    static string WeaponLabel(WeaponSpec? weapon) => weapon is null ? "no weapon" : weapon.Name == "default" ? "bolter" : WeaponLabel(weapon.Name);

    string ClassLabel(int playerClass) => playerClass >= 0 && playerClass < _classLabels.Length ? _classLabels[playerClass] : $"class {playerClass}";

    string MeleeLabel(int playerClass) => playerClass >= 0 && playerClass < _meleeLabels.Length ? _meleeLabels[playerClass] : "melee";

    /// <summary>The HUD name of the player's melee weapon (its choice, else the class default's label).</summary>
    string MeleeLabel(PlayerState s) =>
        Loadouts.Melee(_config!, s).Count == 0 ? "none" : MeleeWargear(s) is { } wargear ? WeaponLabel(wargear) : MeleeLabel(s.Class);

    /// <summary>
    /// Heat of the own player's weapons for the log: " heat 63.0/100" (OVERHEATED while locked) for the weapon in hand when
    /// it has heat, and the holstered one's when it has heat and is not cold. Empty for weapons without heat.
    /// </summary>
    string HeatText(PlayerState me)
    {
        string text = "";
        if (Loadouts.InHand(_config!, me)?.Heat is { } heat) text += $" heat {me.Heat:F1}/{heat.Overheat:F0}{(me.Overheated ? " OVERHEATED" : "")}";
        if (Loadouts.Holstered(_config!, me)?.Heat is { } other && (me.OtherHeat > 0f || me.OtherOverheated))
            text += $" (holstered heat {me.OtherHeat:F1}/{other.Overheat:F0}{(me.OtherOverheated ? " OVERHEATED" : "")})";
        return text;
    }

    /// <summary>A text bar for the HUD: <paramref name="value"/> of <paramref name="max"/> in <paramref name="width"/> cells.</summary>
    static string Bar(float value, float max, int width = 20)
    {
        int filled = max > 0f ? Math.Clamp((int)MathF.Round(value / max * width), 0, width) : 0;
        return "[" + new string('|', filled) + new string('.', width - filled) + "]";
    }

    string LoadoutText(int playerClass) =>
        $"primary {WeaponLabel(Loadouts.Weapon(_config!, playerClass, WeaponSlot.Ranged))}, pistol {WeaponLabel(Loadouts.Weapon(_config!, playerClass, WeaponSlot.Pistol))}, " +
        $"melee {MeleeLabel(playerClass)} ({Loadouts.Melee(_config!, playerClass).Count} steps)";

    /// <summary>The own player's weapon in hand with its ammunition, and the holstered one (full state only).</summary>
    string AmmoText(PlayerState me)
    {
        string text = $"{WeaponLabel(Loadouts.InHand(_config!, me))} {me.Clip}/{me.Reserve}";
        if (Loadouts.Weapon(_config!, me, Loadouts.Other(me.Slot)) is { } other) text += $" (holstered {WeaponLabel(other)} {me.OtherClip}/{me.OtherReserve})";
        if (Loadouts.Weapon(_config!, me, Loadouts.Third(me.Slot)) is { } third) text += $" (holstered {WeaponLabel(third)} {me.ThirdClip}/{me.ThirdReserve})";
        if (me.SwitchLeft > 0f) text += " switching";
        if (me.ReloadLeft > 0f) text += Loadouts.InHand(_config!, me)?.Recharge is not null ? $" recharging {me.ReloadLeft:F1} s" : " reloading";
        return text;
    }

    /// <summary>The connection status: text and whether it is an error (rejection, disconnect, load error, map not loaded); empty while all is well.</summary>
    (string Text, bool IsError) StatusLine()
    {
        if (_error != "") return ($"Error: {_error}", true);
        if (_client is null) return ("Not started", false);
        return _client.Status switch
        {
            ClientStatus.Connecting => ($"Connecting to {_target} ...", false),
            ClientStatus.Connected => _client.MapError is { } mapError ? ("Map not loaded: " + mapError, true) : ("", false),
            ClientStatus.Rejected => (ConnectFailure.RejectText(_client.RejectedBecause, _client.RejectDetail), true),   // fix/connect-timeout: why, in plain words
            ClientStatus.NoAnswer => (ConnectFailure.NoAnswerText(_target), true),
            _ => ("Disconnected (no snapshot from the server)", true),
        };
    }

    /// <summary>
    /// The info overlay (top left; hidden by default, F1 toggles it, <c>--show-info</c> starts it shown): status, round trip,
    /// position, controls and the provenance notes. Text is built only while it is visible; nothing reads it back (logging
    /// goes to stdout). The HUD is updated here either way.
    /// </summary>
    void UpdateOverlay()
    {
        if (_overlay is not null && _showInfo)
        {
            string status;
            if (_error != "") status = $"Error: {_error}";
            else if (_client is null) status = "Not started";
            else status = _client.Status switch
            {
                ClientStatus.Connecting => $"Connecting to {_target} ...",
                ClientStatus.Connected => $"Connected to {_target}, id {_client.PlayerId}",
                ClientStatus.Rejected => ConnectFailure.RejectText(_client.RejectedBecause, _client.RejectDetail),
                ClientStatus.NoAnswer => ConnectFailure.NoAnswerText(_target),
                _ => "Disconnected (no snapshot from the server)",
            };
            var lines = new List<string> { status };
            if (_client?.Status == ClientStatus.Connected)
            {
                var me = _client.Predicted;
                lines.Add($"RTT {_client.HandshakeRoundTrip.TotalMilliseconds:F0} ms (handshake)   players {1 + _remotes.Count}   tick {_client.Tick}");
                lines.Add($"position {me.Position.X:F1}, {me.Position.Y:F1}, {me.Position.Z:F1} m   speed {MathF.Sqrt(me.Velocity.X * me.Velocity.X + me.Velocity.Y * me.Velocity.Y):F1} m/s   {_local?.Playing}");
            }
            lines.Add(_bot ? (_botMelee ? "bot input (runs at the nearest living player; melee within 3 m)" : $"bot input (aims at and fires on the nearest living enemy (anyone without teams; on a map only one it sees within its weapon's range (--bot-range) and not one in its spawn room; shots get aim error (--bot-accuracy); otherwise it walks toward the nearest enemy, waits 8-15 m from a sealed spawn door, or goes to the map centre), charges charge weapons, detonates grenades, throws grenades at 6-{_botThrowMax:F0} m, closes in with the melta, jumps at 8-30 m and pounds from above with a jump pack, switches when empty or overheated; melee within 3 m)") : "WASD move, Shift sprint, mouse aim, Left/Right arrows turn, E pick up, left mouse fire (charge weapons: tap = normal shot, hold = charged shot; the first shot waits for the weapon to come up), right mouse / V melee, F stun strike (heavy melee, in the direction of the keys held; Shift + W + F: the charge, Shift + W + A / W + D + F a bit to that side), hold Left Ctrl aim mode, G throw grenade, Space jump pack (hold: higher; V in the air: ground pound), R reload (detonates grenades out), X / mouse wheel switch weapon, Tab scoreboard, Esc frees the mouse");
            lines.Add(_cameraNote + (_cameraInfo.Length > 0 && RigCameraWanted ? "; now " + _cameraInfo : ""));
            if (_mapNote is not null) lines.Add(_mapNote);
            else if (_client?.MapError is { } mapError) lines.Add("map not loaded: " + mapError);
            lines.Add($"Marines: original asset loaded. Class, weapon, heat, melee and health values: {_tuningSource}; switch time: placeholder. Ground, camera and animation choice: placeholder demonstration. HUD (M44): layout after the original (measured on footage); pictures (bar, emblems, weapon pictures, ticks, medals): original asset loaded; frames, fonts and crosshair: placeholder.");
            lines.Add(AimInfoLine());
            lines.Add(HipInfoLine());
            lines.Add(LocomotionInfoLine());
            lines.Add(FallInfoLine());
            lines.Add(UpperInfoLine());
            if (_orks is not null) lines.Add(OrkInfoLine());   // Ork O7a
            lines.Add($"Evade roll: original clips and root motion; rules: interpretation. {EvadeInfo()}");
            lines.Add(DeathInfoLine());
            lines.Add(WeaponInfoLine());
            lines.Add(SoundInfoLine());   // M26b
            lines.Add(PickupInfoLine());   // fidelity gap 1
            lines.Add("Jump pack: original clips; clip choice and timing, the power readout and the pound / landing rings (own: predicted; others: inferred from their flags): placeholder demonstration.");
            lines.Add("Teams: nameplates (own green, ally blue, enemy red), side ring (Space Marines blue, Chaos orange) placeholder. Chaos players use the original Chaos multiplayer armour on the shared marine rig (the set follows the class name; the red tint only if it fails to load). Kill feed, score panel and scoreboard: layout after the original, placeholder demonstration; names are player numbers (the protocol has no names).");
            if (SeizeGround) lines.Add("Seize Ground: point columns, edge rings, progress discs, the HUD's point icons and edge-clamped objective markers: placeholder demonstration; point layout and state from the server.");
            lines.Add(FxInfoLine());
            lines.Add("Placeholders: grenade bodies (small spheres; frag dark green, blind grenade grey), the lingering blast area (disc), the charging glow; with --placeholder-fx also the plasma balls, blast spheres (each splash radius) and the melta cone.");
            _overlay.Text = string.Join("\n", lines);
        }
        UpdateHud();
    }

    /// <summary>
    /// Fills the HUD (<see cref="Hud"/>) from the local predicted state. GameClient exposes no separate acknowledged server
    /// state; the predicted state is the latest authoritative snapshot with only the unacknowledged inputs replayed on it,
    /// and damage, death and respawn are applied only by the server, so health, shield and Alive here lag the server by at
    /// most those inputs (only regeneration and the respawn countdown are predicted). Ammunition and reloading are fully
    /// predicted, and so are the weapon in hand and switching. Only the own player is shown: other players arrive without these values.
    /// </summary>
    void UpdateHud()
    {
        if (_hudView is null) return;
        var f = new Hud.Frame();
        var (statusText, statusError) = StatusLine();
        f.Status = statusText; f.StatusIsError = statusError;
        _hudView.ShowHint = !_showInfo && !_bot && Spectate is null && !NoOverlay;
        _hudView.Data = f;
        if (_client?.Status != ClientStatus.Connected || _config is null) { _scopeView?.Hide(); _scopeSince = -1; return; }   // feel/scope-lascannon: no overlay without a match
        var me = _client.Predicted;
        var c = _config;
        f.InGame = true; f.Alive = me.Alive; f.Spectating = Spectate is not null; f.RespawnLeft = me.RespawnLeft;
        var body = Loadouts.Body(c, me);   // M43: the perks' maxima (Iron Halo: shield 150)
        f.Health = me.Health; f.HealthMax = body.HealthMax; f.Shield = me.Shield; f.ShieldMax = body.ShieldMax;
        if (Loadouts.InHand(c, me) is { Recharge: { } recharge } melee0 && me.Alive)   // ui/hud-3: the melta's bar is the progress to its next shot (shot delay, then the recharged round)
            f.WeaponBarFull = me.Cooldown > 1e-4f && melee0.ShotCooldown > 0f ? Math.Clamp(1f - me.Cooldown / melee0.ShotCooldown, 0f, 1f)
                : me.ReloadLeft > 0f ? Math.Clamp(1f - me.ReloadLeft / MathF.Max(recharge.RoundTime, 1e-3f), 0f, 1f) : 1f;
        f.IronHalo = Loadouts.HasPerk(c, me, "mp_perk_iron_halo");   // ui/hud-3: the armour bar's medallion
        var inHand = Loadouts.InHand(c, me);
        f.SlotKey = (int)me.Slot;
        if (inHand is not null)
        {
            f.HasWeapon = true;
            f.WeaponName = Capitalized(WeaponLabel(inHand)); f.WeaponWargear = inHand.Name;   // M44: the wargear names the weapon's picture
            f.Clip = me.Clip; f.ClipMax = inHand.ClipSize; f.Reserve = me.Reserve; f.ReserveMax = inHand.ReserveSize;
            f.HasCharge = inHand.Charge is { Stages.Count: > 0, FiresOnPress: false };
            // A self-recharging clip (the melta) shows the time to its next round instead of RELOADING.
            f.WeaponState = me.SwitchLeft > 0f ? "SWITCHING"
                : me.ReloadLeft > 0f ? (inHand.Recharge is not null ? $"RECHARGING {me.ReloadLeft:F1} s" : "RELOADING") : "";
            if (inHand.Heat is { } heat) { f.HasHeat = true; f.Heat = me.Heat; f.HeatMax = heat.Overheat; f.Overheated = me.Overheated; }
            // Fidelity gap 7: the stance and the vent (NetworkView.Brace.cs), and the Reload hint at high heat.
            var (braceState, braceHint) = BraceVentHud(me, inHand);
            f.Venting = me.Venting;
            if (f.WeaponState.Length == 0) f.WeaponState = braceState ?? braceHint ?? "";
            (f.Charge, f.ChargeText, f.ChargeReady) = ChargeMeter(me, inHand);
            if (Shots.RemoteDetonated(inHand) && inHand.Projectile is { } projectile)
                f.GrenadesOutText = $"Grenades out {me.ProjectilesOut} / {projectile.MaxActive}{(me.ProjectilesOut > 0 ? " (R detonates)" : "")}";
        }
        // Protocol 44: the first holstered slot's weapon is "the other" (as the only one was); with Weapon Versatility the second holstered one is "the third".
        if (Loadouts.Weapon(c, me, Loadouts.Other(me.Slot)) is { } other)
        {
            f.HasOther = true; f.OtherName = Capitalized(WeaponLabel(other)); f.OtherWargear = other.Name; f.OtherClip = me.OtherClip; f.OtherClipMax = other.ClipSize; f.OtherReserve = me.OtherReserve;
        }
        if (Loadouts.Weapon(c, me, Loadouts.Third(me.Slot)) is { } third)
        {
            f.HasThird = true; f.ThirdName = Capitalized(WeaponLabel(third)); f.ThirdWargear = third.Name; f.ThirdClip = me.ThirdClip; f.ThirdClipMax = third.ClipSize; f.ThirdReserve = me.ThirdReserve;
        }
        if (Loadouts.Equipment(c, me) is { } item)
        {
            f.HasGrenade = true; f.GrenadeName = Capitalized(EquipmentLabel(item)); f.GrenadeWargear = item.Name; f.Grenades = me.Grenades;
            f.GrenadeCooldown = me.GrenadeCooldown; f.Throwing = me.Throwing; f.StimLeft = me.Alive ? me.StimLeft : 0f;
        }
        if (Loadouts.JumpPack(c, me) is { } pack)
        {
            // M44: a meter under the weapon box (owner's request) instead of the "JUMP PACK 100 / 100" row above it.
            f.HasJump = true; f.JumpPower = me.JumpPower; f.JumpMax = pack.MaxPower; f.JumpWargear = pack.Name;
            f.JumpLow = me.JumpPower + 1e-3f < pack.CostAt(0f); f.JumpPhase = JumpPhaseText(me);
        }
        f.MeleeName = Loadouts.Melee(c, me).Count > 0 ? Capitalized(MeleeLabel(me)) : "";

        // Match and objectives.
        var match = _client.Match;
        f.OwnTeam = me.Team == Team.Chaos ? Team.Chaos : Team.SpaceMarines;
        f.Teams = _client.Scoreboard is { Teams: true } || match.State != MatchState.None;
        bool ownChaos = me.Team == Team.Chaos;
        f.OwnScore = ownChaos ? match.ChaosScore : match.MarineScore;
        f.EnemyScore = ownChaos ? match.MarineScore : match.ChaosScore;
        f.ScoreLimit = _client.Scoreboard is { Teams: true } sb ? sb.ScoreLimit : 0;
        f.Clock = match.State == MatchState.Playing && match.TicksLeft != uint.MaxValue ? Clock(SecondsLeft(match)) : "";
        f.Banner = match.State switch
        {
            MatchState.Warmup => match.IsCountdown() ? "" : "WARMUP - waiting for both sides",   // match flow: the countdown has its own view
            MatchState.Ended => "",   // match flow: the splash, then the scoreboard with NEXT MAP, say it (MatchFlowView)
            _ => "",
        };
        f.Seize = SeizeGround;
        if (f.Seize) FillPoints(f, me);

        // Crosshair: red over a living enemy, its name under the bar, the aimed enemy's health bar.
        Ph("hud: crosshair ray");
        if (me.Alive && _camera is not null)
        {
            // Red crosshair and name when the centre ray's first hit is a hostile player (the simulation's hit shapes).
            var centre = CenterRayTarget(me, c);   // ui/hud-fidelity: one centre ray for the aimed enemy and the crosshair's place
            if (centre.Hit is { } enemy && World.Hostile(me.Team, enemy.Team))
            {
                f.AimedAtEnemy = true; f.AimedName = HudName(enemy.PlayerId);   // kept for the headshot line; never drawn (no name over enemies)
                // Protocol 44: Tactical Readout / Signum Link send the enemies' health and armour (fractions of the class body, as the hit report's); without them no bar.
                if (_client!.EnemyBarFor(enemy.PlayerId) is { } bar)
                {
                    f.AimedBar = true; f.AimedHealth = bar.Health / 255f; f.AimedShield = bar.Shield / 255f; f.AimedHealthMax = 1f; f.AimedShieldMax = 1f;
                }
            }
            FillDecorators(f, me, centre.Hit);   // ui/hud-3: the name over the aimed player only (NetworkView.Match.cs)
            f.SpreadRadius = SpreadPixels(me, c);   // weapons/spread: the crosshair ticks open with the scatter (NetworkView.Spread.cs)
            // ui/hud-fidelity: the original reticle's spread frame, aim mode and the brace (Hud.Movie.cs).
            f.SpreadFraction = ReticleRules.SpreadFraction(inHand, me, c.MoveSpeedScale);
            f.Aiming = me.Aiming; f.Braced = me.Brace is BracePhase.Entering or BracePhase.Braced;
        }
        f.Prompt = me.Alive ? _hudPrompt : "";   // ui/hud-fidelity (NetworkView.Pickups.cs)
        UpdateScope(f, me);   // feel/scope-lascannon
        if (HudDemo) RunHudDemo(f);
        Ph("overlay text");
    }

    double _hudDemoLast = -1;

    /// <summary>
    /// <c>--hud-demo</c> (showcase aid): every 8 s of connected time a scripted sequence: switch popup at 0 s, an aimed enemy from
    /// 2 s, a white hit marker at 2.5 s, a kill (red marker, reward line, kill-feed line) at 4.5 s.
    /// </summary>
    void RunHudDemo(Hud.Frame f)
    {
        if (_hudView is null) return;
        double t = _connectedFor % 8.0, last = _hudDemoLast;
        _hudDemoLast = t;
        bool Crossed(double at) => last >= 0 && last < at && t >= at;
        if (last < 0 || last > t) _hudView.ShowSwitchPopup();   // first call, or the 8 s cycle wrapped
        if (t >= 2.0 && t < 6.0) { f.AimedAtEnemy = true; f.AimedName = "[CSM] P1"; f.AimedBar = true; f.AimedHealth = 0.6f; f.AimedShield = 0.3f; f.AimedHealthMax = 1f; f.AimedShieldMax = 1f; }
        // ui/hud-3: the aimed player's name for screenshots: an enemy from 2 s, a friend from 4 s (the real one needs a hit shape under the crosshair).
        if (t >= 2.0 && t < 6.0 && _camera is not null)
        {
            bool ally = t >= 4.0;
            f.Decorators.Add(new Hud.Decorator { Name = ally ? "Brother P2" : "[CSM] P1", Rel = ally ? Hud.Rel.Own : Hud.Rel.Enemy, Team = ally ? Team.SpaceMarines : Team.Chaos, Screen = new Vector2(800f, 330f) * (_hudView.Size.Y / 1080f), Distance = 25f });
        }
        // M44: fill the meters for screenshots (heat ramps up, a charge repeats every 2 s, the jump pack swings, the armour drops to 70 % from 4 s).
        if (f.HasHeat) f.Heat = f.HeatMax * (float)Math.Clamp(t / 6.0, 0.0, 1.0);
        else if (f.HasCharge) { f.Charge = (float)(t % 2.0 / 2.0); f.ChargeReady = f.Charge > 0.75f; }
        if (f.HasJump) f.JumpPower = f.JumpMax * (float)(0.3 + 0.7 * Math.Abs(Math.Sin(t)));
        if (t >= 4.0) f.Shield = MathF.Min(f.Shield, 0.7f * f.ShieldMax);
        if (t >= 6.0) f.Health = MathF.Min(f.Health, 0.6f * f.HealthMax);   // ui/hud-3: a second hit for the damage vignette (health colour)
        if (t >= 5.0) f.Prompt = "Pick up Ammo";   // ui/hud-fidelity: the prompt for screenshots
        if (Crossed(2.5)) _hudView.ShowHitMarker(false);
        if (Crossed(4.5))
        {
            _hudView.ShowHitMarker(true);
            _hudView.AddEvent("+ Kill P1");
            _hudView.AddKill(new Hud.Kill("P2", Hud.Rel.Own, "bolter", "P1", Hud.Rel.Enemy, "", "sm_mp_ranged_bolter"));
            _hudView.AddKill(new Hud.Kill("P1", Hud.Rel.Enemy, "combat knife", "P3", Hud.Rel.Own, " (team kill)", "sm_mp_melee_combat_knife"));
            _hudView.AddKill(new Hud.Kill("P3", Hud.Rel.Own, "blind grenade", "P2", Hud.Rel.Own, "", "sm_mp_eq_krak_grenade"));   // feed pictures (flips): knife, krak
        }
    }

    /// <summary>
    /// The name shown for a player (names/bot-lore, protocol 41): the scoreboard's display name (test bots: a name from their race's pool, ours), else "P3"
    /// (<see cref="GameClient.NameOf"/>).
    /// </summary>
    string HudName(int playerId) => _client?.NameOf(playerId) ?? PlayerNames.Fallback(playerId);

    /// <summary>The player's relation to the viewer for the HUD's colours (gold own side, red enemy, white without teams).</summary>
    Hud.Rel HudRelation(int playerId) => HudRelation(TeamOf(playerId));

    Hud.Rel HudRelation(Team team)
    {
        var mine = _client?.Predicted.Team ?? Team.None;
        if (mine == Team.None || team == Team.None) return Hud.Rel.None;
        return World.Hostile(mine, team) ? Hud.Rel.Enemy : Hud.Rel.Own;
    }

    /// <summary>The charge meter row: fill (0..1 against the next firing stage, or the last), text and whether a release fires; fill -1 when not charging.</summary>
    static (float Fill, string Text, bool Ready) ChargeMeter(PlayerState me, WeaponSpec weapon)
    {
        if (weapon.Charge is not { Stages.Count: > 0 } charge) return (-1f, "", false);
        if (me.Charging)
        {
            int ready = Combat.ChargeStage(charge, me.ChargeTime, firing: true);
            float next = 0f, last = 0f;
            for (int i = 0; i < charge.Stages.Count; i++)
            {
                if (charge.Stages[i].Shot is null) continue;
                float at = charge.Stages[i].MinTime * charge.MinTimeMultiplier;
                last = at;
                if (next == 0f && i + 1 > ready) next = at;
            }
            float goal = next > 0f ? next : last;
            return (goal > 0f ? Math.Clamp(me.ChargeTime / goal, 0f, 1f) : 1f,
                    $"CHARGE {me.ChargeTime:F1} / {goal:F1} s" + (ready > 0 ? $"   READY ({charge.Stages[ready - 1].Name}): release to fire" : ""), ready > 0);
        }
        if (me.PendingShot > 0) return (1f, $"Charged shot leaving in {me.PendingLeft:F2} s", true);
        return (-1f, "", false);
    }

    /// <summary>Seize Ground: each point's owner / progress / contested and its marker's screen position (6 m over the point's centre).</summary>
    void FillPoints(Hud.Frame f, PlayerState me)
    {
        var layout = _client?.Scoreboard?.Seize;
        if (layout is null || _client is null || _camera is null || _gameSpace is null) return;
        var points = _client.Points;
        for (int i = 0; i < layout.Points.Length && i < points.Count; i++)
        {
            var info = layout.Points[i];
            var p = points[i];
            var world = _gameSpace.Transform * new Vector3(info.Center.X, info.Center.Y, info.Center.Z + 6f);
            f.Points.Add(new Hud.Point
            {
                Letter = PointLetter(i), Owner = HudRelation(p.Owner), OwnerTeam = p.Owner, Side = HudRelation(p.Side), Progress = p.Progress, Contested = p.Contested,
                Screen = _camera.UnprojectPosition(world), Behind = _camera.IsPositionBehind(world),
                Distance = new Vector2(info.Center.X - me.Position.X, info.Center.Y - me.Position.Y).Length(),
            });
        }
    }

    static string Capitalized(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    void AddHud(CanvasLayer layer)
    {
        SetUpScope(layer);   // feel/scope-lascannon: under the HUD (NetworkView.Scope.cs)
        _hudView = new Hud { GameDir = Lean ? null : _gameDir };   // M44: the original HUD art is read from the install (perf/lean-bots: not by a lean bot)
        layer.AddChild(_hudView);
    }

    /// <summary>
    /// Called by <see cref="GameClient"/> when the server names its map: loads the collision from this install for
    /// prediction and draws the map with its original meshes and diffuse textures (<see cref="MapView"/>), replacing the
    /// flat checker ground. The grey character collision is drawn instead if the visuals fail or nothing could be drawn,
    /// and in addition with --show-collision.
    /// </summary>
    LoadedMap? LoadMap(string name)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Ph("map load: collision and content");
        MemStep("before map"); var map = MapContent.Load(_gameDir, name); MemStep("map content");
        var world = map.ToCollisionWorld(); MemStep("collision world");
        if (!Lean) BuildRagdollWorld(map);
        PrepareNavigation(map, world); MemStep("navigation");
        _mapCentre = map.Spawns.Count > 0 ? new Vector2(map.Spawns.Average(sp => sp.Position.X), map.Spawns.Average(sp => sp.Position.Y)) : null;
        long collisionMs = watch.ElapsedMilliseconds;
        if (Lean) return LeanMapLoaded(name, map, world, collisionMs, watch);   // perf/lean-bots: collision, navigation and the jump ground only (NetworkView.Lean.cs)
        _gameSpace?.GetNodeOrNull("MapCollision")?.QueueFree();
        _gameSpace?.GetNodeOrNull("MapVisuals")?.QueueFree();

        // The map's look: original meshes and diffuse textures (MapView). Failure falls back to the grey collision.
        string? visualsError = null;
        MapView? view = null;
        string lightingNote = "lighting: provisional approximation (warm sun, fog, SSAO)";
        var visualNotes = new List<string>();
        try
        {
            Ph("map load: visuals");
            var scene = MapScene.Load(_gameDir, name);
            visualNotes.AddRange(scene.Notes);
            view = MapView.Build(scene, visualNotes);
            if (view.Instances == 0)
            {
                visualsError = "no part of the map could be drawn";
                view.Free();
                view = null;
            }
            else
            {
                _gameSpace?.AddChild(view);
                try
                {
                    if (_sun is not null && _environment is not null
                        && MapLightingView.Apply(scene.Lighting, _sun, _environment, visualNotes, out lightingNote, out _blender) is { } placed)
                        _gameSpace?.AddChild(placed);
                }
                catch (Exception e) when (e is not (StackOverflowException or AccessViolationException))
                {
                    // The map stays drawn with the provisional sun and fog; the half-built lights were freed by Apply.
                    _blender = null;
                    visualNotes.Add($"the map's lighting failed ({e.GetType().Name}: {e.Message}); the provisional look stays");
                }
            }
        }
        catch (Exception e) when (e is not (StackOverflowException or AccessViolationException))
        {
            visualsError = $"{e.GetType().Name}: {e.Message}";
            view = null;   // MapView.Build frees a half-built view itself
        }
        foreach (var note in visualNotes) GD.Print("  map note: " + note);

        if ((view is null || _showCollision) && map.Layer(MapContent.CharacterLayer) is { } layer && _gameSpace is not null)
        {
            var vertices = new Vector3[layer.Indices.Length];
            var normals = new Vector3[layer.Indices.Length];
            for (int t = 0; t < layer.Indices.Length; t += 3)
            {
                var a = layer.Vertices[layer.Indices[t]]; var b = layer.Vertices[layer.Indices[t + 1]]; var c = layer.Vertices[layer.Indices[t + 2]];
                var n = System.Numerics.Vector3.Normalize(System.Numerics.Vector3.Cross(b - a, c - a));
                if (!float.IsFinite(n.X)) n = System.Numerics.Vector3.UnitZ;
                // Godot's front faces wind clockwise; the game's winding is not known, so both sides are drawn.
                vertices[t] = new(a.X, a.Y, a.Z); vertices[t + 1] = new(c.X, c.Y, c.Z); vertices[t + 2] = new(b.X, b.Y, b.Z);
                normals[t] = normals[t + 1] = normals[t + 2] = new(n.X, n.Y, n.Z);
            }
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = vertices;
            arrays[(int)Mesh.ArrayType.Normal] = normals;
            var mesh = new ArrayMesh();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            var material = (StandardMaterial3D)_groundMaterial!.Duplicate();
            material.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
            _gameSpace.AddChild(new MeshInstance3D { Name = "MapCollision", Mesh = mesh, MaterialOverride = material });
        }
        if (GetNodeOrNull<Node3D>("Ground") is { } ground) ground.Visible = false;
        DrawBlockers(world);
        DrawProps(world);
        _mapNote = view is not null
            ? $"map {name}: original meshes and textures from the install ({view.Objects:N0} objects, {view.Meshes} meshes, {view.TexturedSurfaces}/{view.Surfaces} surfaces textured, {view.Materials.NormalMapped} with normal maps, {view.Materials.Specular} with specular maps, {view.Materials.Emissive} emissive, {view.Materials.Layered} two-layer, {view.Materials.CutOut} cut-out, {view.Materials.Tinted} vertex-tinted); {lightingNote}, shaders: placeholder"
              + (_showCollision ? "; collision shown in grey" : "")
            : $"map {name}: collision geometry only, placeholder grey (visuals failed: {visualsError})";
        GD.Print($"{_mapNote}; collision {collisionMs} ms, total {watch.ElapsedMilliseconds} ms");
        // The default look of every class and the own look, built now so no player's first look stalls a frame (customizer T7).
        Ph("map load: looks");
        _warmClock = System.Diagnostics.Stopwatch.StartNew();   // Ork O7a: one deadline for the marines' and the Ork's warm-up
        WarmLooks();
        WarmOrk();   // Ork O7a: an Ork player's own look and weapons (NetworkView.Ork.cs)
        PrewarmWeaponDefaults();   // M40-T9: the weapon models of every class's default loadout
        PrewarmPickups();   // perf/pass-1: the drops' models (the first drop cost a 100 ms frame); a lean bot returned above (LeanMapLoaded) and warms none
        PrewarmCollision(world);   // M40-T9: the crosshair's first ray
        _jumpGround = world;   // M45: the flight pose's height above the ground
        return new LoadedMap(world, map.Hash);
    }

    void AddGroundCameraAndLights()
    {
        // Placeholder ground: a checker plane at game z = 0 (the simulation's flat floor), 1 m squares.
        var checker = Image.CreateEmpty(2, 2, false, Image.Format.Rgb8);
        checker.SetPixel(0, 0, new Color(0.34f, 0.35f, 0.37f)); checker.SetPixel(1, 1, new Color(0.34f, 0.35f, 0.37f));
        checker.SetPixel(1, 0, new Color(0.27f, 0.28f, 0.3f)); checker.SetPixel(0, 1, new Color(0.27f, 0.28f, 0.3f));
        var ground = _groundMaterial = new StandardMaterial3D
        {
            AlbedoTexture = ImageTexture.CreateFromImage(checker),
            TextureFilter = BaseMaterial3D.TextureFilterEnum.NearestWithMipmaps,
            Uv1Triplanar = true,
            Uv1WorldTriplanar = true,
            Uv1Scale = new Vector3(0.5f, 0.5f, 0.5f),
        };
        AddChild(new MeshInstance3D { Name = "Ground", Mesh = new PlaneMesh { Size = new Vector2(200, 200) }, MaterialOverride = ground });

        _camera = new Camera3D { Fov = 65f, Near = 0.05f, Far = 3000f, Current = true, Position = new Vector3(0, 6, 9) };
        AddChild(_camera);
        _camera.LookAt(Vector3.Zero, Vector3.Up);

        var sun = new DirectionalLight3D { ShadowEnabled = true, LightEnergy = 1.4f, LightColor = new Color(1.0f, 0.86f, 0.68f), DirectionalShadowMaxDistance = 120f };
        AddChild(sun);
        _sun = sun;
        sun.RotationDegrees = new Vector3(-50f, 35f, 0f);
        // Provisional look (placeholder) for the flat ground and as the fallback: warm key light, cool-grey ambient, dusty fog.
        // A loaded map replaces it with its own sun, ambient, fog and lights (LoadMap, MapLightingView; docs/discovery.md).
        AddChild(new WorldEnvironment
        {
            Environment = _environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.24f, 0.2f, 0.17f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.62f, 0.6f, 0.58f),
                AmbientLightEnergy = 1.1f,
                TonemapMode = Godot.Environment.ToneMapper.Filmic,
                TonemapExposure = 1.15f,
                FogEnabled = true,
                FogLightColor = new Color(0.36f, 0.3f, 0.25f),
                FogDensity = 0.004f,
                SsaoEnabled = true,
            },
        });
    }

    /// <summary>"host", "host:port", "1.2.3.4:port" or "[::1]:port"; the port defaults to 27015. Hostnames prefer IPv4.</summary>
    public static IPEndPoint ParseEndpoint(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("--connect needs host[:port]");
        text = text.Trim();
        if (IPEndPoint.TryParse(text, out var literal))
            return literal.Port == 0 ? new IPEndPoint(literal.Address, DefaultPort) : literal;
        string host = text;
        int port = DefaultPort;
        int colon = text.LastIndexOf(':');
        if (colon > 0 && text.IndexOf(':') == colon)
        {
            host = text[..colon];
            if (!int.TryParse(text[(colon + 1)..], out port) || port is < 1 or > 65535) throw new ArgumentException($"bad port in '{text}'");
        }
        var addresses = Dns.GetHostAddresses(host);
        var address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault()
                      ?? throw new ArgumentException($"cannot resolve '{host}'");
        return new IPEndPoint(address, port);
    }
}

/// <summary>
/// The loadout a networked client asks for (<see cref="NetworkView"/>): weapon names matched against the played class's
/// choice lists by <see cref="NetworkView.ResolveChoice"/> (null: the class default); <paramref name="Random"/> picks a
/// random valid choice for every slot not named (test aid, <c>--bot-loadout random</c>). <paramref name="Equipment"/>
/// names the thrown item (<c>--equipment</c>, protocol 8), matched against the class's equipment list the same way.
/// </summary>
public sealed record LoadoutRequest(string? Primary = null, string? Pistol = null, string? Melee = null, bool Random = false, string? Equipment = null);
