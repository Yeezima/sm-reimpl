using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmContent;
using SmClient.Customizer;
using SmClient.FrontEnd;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// M2 viewer: shows one original static model from the user's install with its original diffuse textures.
/// User arguments (after "--"): --game-dir &lt;dir&gt;, --archive &lt;path under the game folder&gt;, --mesh &lt;name&gt;,
/// With neither --archive nor --mesh, shows the animated multiplayer Space Marine (see CharacterViewer: --anim, --time).
/// --stance &lt;low|mid|high|t&gt; (test aid) shows a blended stance of the character viewer (CharacterViewer.Stance).
/// --paint &lt;scheme&gt; (test aid) paints the armour with another scheme of the install (e.g. sm_blood_angels, csm_alpha_legion) instead of the side default.
/// --focus &lt;material&gt; (frame only the triangles using that material, seen from their front),
/// --screenshot &lt;file.png&gt; (relative names go to %LOCALAPPDATA%\sm-reimpl\screenshots), --quit (exit after the screenshot).
/// --connect &lt;host[:port]&gt; joins a dedicated server instead (see NetworkView; --bot for scripted input,
/// --wait-players &lt;n&gt; delays the screenshot until n players are visible; default 2 with --bot, else 1;
/// --show-collision also draws the map's character collision in grey; --wait-melee also delays the screenshot until a
/// melee attack is shown past its hit time; --bot-melee makes the bot run at its target instead of firing;
/// --class &lt;name&gt; asks for a multiplayer class: its key ("sm - tactical"), its short name or role ("tactical",
/// "assault", "heavy" or "devastator"), or with a race word for Chaos ("chaos tactical", "havoc", "raptor"); without it,
/// or for an unknown name (noted in the console), the server's default class).
/// --primary &lt;name&gt;, --pistol &lt;name&gt;, --melee &lt;name&gt; ask for a weapon from the class's choice lists, by its
/// wargear name without race and category prefix, case, spaces, '_' and '-' alike ("plasma_gun" or "plasma gun",
/// "storm bolter", "las cannon" or "lascannon", "chainsword gold" or "gold chainsword"), optionally with the slot word
/// ("bolter pistol" is the pistol slot's "bolter"), an alias ("sniper" or "stalker" for the sniper rifle), a unique part
/// of a name, or the list index;
/// without them, or for a name the class does not offer (noted in the console with the valid names), the class default.
/// Without --class they are matched against class 0, which is then requested.
/// --equipment &lt;name&gt; asks for a thrown item from the class's equipment list the same way ("frag", "blind" (the files' "krak" also matches),
/// "frag grenade" or the list index; protocol 8); without it, or for a name the class does not offer, the class default.
/// --bot-loadout random picks a random valid choice for every slot not named (test aid).
/// --bot-fire makes the bot keep firing while no target is visible too (test aid for heat and charged fire).
/// --show-info shows the info overlay (F1 toggles it; hidden by default); --hud-demo cycles the HUD's aimed-enemy, hit-marker,
/// kill-feed and weapon-switch popup states (showcase aid); --aim-test (test aid) gives the bot the human follow camera and has it hold
/// aim mode every other 5 s.
/// --bot-throw-max &lt;metres&gt;: the bot throws grenades at targets 6 m to this far away (default 20; test aid for big maps).
/// Networked controls: WASD move, Shift sprint, mouse aim, Left/Right arrows turn, E pick up, left mouse Fire (on a charge weapon, M31: tap for the normal
/// shot, hold to charge and release for the charged shot; the first shot from a lowered weapon waits for its raise); right mouse or V
/// melee, F the stun strike (heavy melee; with W held: the charge); hold Left Ctrl for aim mode;
/// R reload, or with grenade-launcher grenades out, detonate them; G throws the equipment item (frag or blind grenade);
/// X or the mouse wheel switches weapons; Space uses the jump pack (Assault Marine and Raptor; held: the hold length sets
/// the height), and melee in the air starts the ground pound; Esc frees the mouse. Buttons are sent as held every tick; the simulation finds the press edges. These bindings are a client
/// preference, not the original ones (not decoded).
/// M25b: --placeholder-fx keeps the old placeholder projectile/blast spheres instead of the original effects, --no-fx draws no effects, --fx-slow <n> runs effects n times slower than the game (showcase aid), --spectate shot frames the last shot's tracer; --fx <name,...> (no server) plays original effects in a plain room (FxTestView).
/// M24: --no-weapons (test aid) draws no weapon models; the character viewer takes --weapons, --hand, --worn, --view (see below).
/// M23 test aids: --ragdoll-rest <s> sets how long a ragdoll must be still before it is baked; --bot-idle makes the bot stand still without firing (so an opponent kills it: death screen checks).
/// --screenshot-at &lt;seconds&gt; (networked view) takes the screenshot no earlier than that long after connecting;
/// --wait-blast also waits until a blast and a projectile are drawn together (8 s later a blast alone, 15 s later any projectile).
/// --wait-jump also waits until the own marine is drawn in the air from a jump, 3 m or more above its lift-off (15 s after
/// --screenshot-at any jump in the air).
/// --bot-jump-free (test aid): a bot with a jump pack also jumps toward its target every 5 s when the target is not
/// 8-30 m away in the open (on a map: over whatever is in the way), to exercise flight and landings on level geometry.
/// M44a bot aids (NetworkView.BotAim.cs, BotSpawn.cs): --bot-accuracy &lt;0..1&gt; (fraction of shots meant to hit at medium range; default 0.03 plus a trigger discipline, 1 = perfect aim), --bot-range &lt;m&gt;
/// (farthest distance a bot opens fire on a visible enemy; default its weapon's range), --bot-no-spawn-wait (the old chase of an enemy in its spawn room), --bot-trace &lt;file&gt;
/// (CSV of every input tick); tools/bots.ps1 / bots.cmd starts up to 6 bots per side.
/// --side marines|chaos (protocol 10) asks for a side; the server may balance the player onto the other side with that
/// side's equivalent class (printed once known). --show-scoreboard (test aid) always shows the scoreboard (else Tab, or
/// automatically while a match has ended); --wait-match-end delays the screenshot until a match has ended (up to 600 s).
/// M16 showcase aids: --spectate [overview|action|point:&lt;letter&gt;] replaces the follow camera (an orbit over the
/// Seize Ground points, a side view of the own player and its nearest enemy, or an orbit around one point); --no-hud hides
/// every 2D element, --no-overlay only the debug text, --no-markers (M34) also the test rings, name tags and placeholder blasts (and the zone indicators, which are hidden anyway since M36); --spectate inspect orbits the own weapon (record at a chosen size with movie.cmd); --screenshot-every &lt;seconds&gt; turns --screenshot into a
/// numbered series (name_001.png, ...) from the first ready frame until --quit-after; with --screenshot-on-effect a series
/// shot also waits for a blast, a melta cone or the own marine high in a jump in view (NetworkView.EffectInView).
/// --quit-after &lt;seconds&gt; exits after that long (after the screenshot, if one is requested); for automated runs.
/// M36: --show-markers shows the zone indicators hidden since M36 (Seize Ground point columns, ground-pound and landing rings, spawn-room shield planes);
/// --no-locomotion keeps the old single run clip (no sprint or backpedal clips); --bot-gait (with --bot) walks every gait in turn;
/// --pan-rate &lt;deg/s&gt; turns the view of a human client, --yaw-probe logs the own model and camera steps per frame and --no-view-smoothing draws the old tick position (NetworkView.Turning.cs).
/// Rendering is deliberately simple: diffuse map only, opaque, standard lighting. The original shaders
/// (vertex blending, normal and specular maps) are not reproduced yet.
/// </summary>
public partial class ModelViewer : Node3D
{
    // A statue from a multiplayer map (mp_dlc2_map4): compact, recognizable, and upright only if the axes are right.
    const string DefaultArchive = "data/chunks/mp_dlc2_map4__wrld.oppc";
    const string DefaultMesh = "Geo_Statue_LODH";

    string? _screenshot;
    readonly List<string> _forbiddenScreenshotDirs = new();
    bool _quit;
    int _frames;
    NetworkView? _network;
    double? _quitAfter;
    double? _screenshotEvery;
    double _nextSeriesShot;
    bool _onEffect;
    int _seriesIndex;
    double _elapsed;

    /// <summary>The image <c>--emblem-file</c> names: the emblem importer reads it instead of opening a file dialog (test aid).</summary>
    public static string? EmblemFile { get; private set; }

    FrontEndView? _frontEnd;
    bool _fromFrontEnd;          // the networked view was started by the front end's Join: a failed join can go back to it
    CanvasLayer? _backLayer;
    Label? _backHint;
    string _gameDir = "";
    Dictionary<string, string?> _launchArgs = new();

    /// <summary>The window is a test window (<c>--test-window</c>): no focus, no mouse capture.</summary>
    public static bool TestWindow { get; private set; }

    public override void _Ready()
    {
        var args = ParseArgs(OS.GetCmdlineUserArgs());
        // --test-window (automated tests on the owner's desktop): the window never takes keyboard focus and the game never
        // captures the mouse (NetworkView.NoCapture), so test runs cannot lock the owner's cursor; pair with Godot's own
        // `--position -4000,-4000` before `--` to keep it off-screen. Bots never capture the mouse either.
        if (args.ContainsKey("test-window"))
        {
            TestWindow = true;
            GetWindow().SetFlag(Window.Flags.NoFocus, true);
            GetWindow().SetFlag(Window.Flags.AlwaysOnTop, false);
        }
        GD.Print(SafeMode.Describe());   // hotfix/old-gpu: the renderer and whether safe mode is on (one line)
        GD.Print(RenderQuality.Apply(GetViewport()));   // M40-T7 / M42: anti-aliasing (--aa) for every view, not only the networked one
        MarineAssets.PaintOverride = args.GetValueOrDefault("paint");
        EmblemFile = args.GetValueOrDefault("emblem-file");
        _screenshot = args.GetValueOrDefault("screenshot");
        _quit = args.ContainsKey("quit");
        _onEffect = args.ContainsKey("screenshot-on-effect");
        _screenshotEvery = args.GetValueOrDefault("screenshot-every") is { } se && double.TryParse(se, System.Globalization.CultureInfo.InvariantCulture, out var every) && every > 0 ? every : null;
        _quitAfter = args.GetValueOrDefault("quit-after") is { } qa && double.TryParse(qa, System.Globalization.CultureInfo.InvariantCulture, out var after) ? after : null;
        string repoRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), ".."));

        // Added content E2: an invalid --content is refused the same way on every path (front end and --connect): a message, no start.
        if (args.ContainsKey("content") && !SmSim.ContentProfiles.TryParse(args["content"], out _, out string contentError))
        {
            GD.PrintErr(contentError);
            Finish(contentError, new List<string>());
            GetTree().Quit(2);   // as the server's exit code 2 for a bad flag
            return;
        }

        var notes = new List<string>();
        string status;
        try
        {
            _forbiddenScreenshotDirs.Add(repoRoot);
            if (WantsFrontEnd(args) && !FrontEndExcluded.Any(args.ContainsKey))
            {
                // The front end looks for the install on a worker thread ("Looking for the game..."): a slow or unreachable drive must not freeze the window.
                // Found: the front end; not found: the game-folder screen (first run).
                BeginLocate(args, repoRoot);
                return;
            }
            var found = GameInstall.TryLocate(args.GetValueOrDefault("game-dir"), repoRoot) ?? throw new System.IO.DirectoryNotFoundException(GameInstall.NotFoundMessage);
            string gameDir = found.Dir;
            _foundNote = FoundNote(found);
            _forbiddenScreenshotDirs.Add(gameDir);
            if (args.ContainsKey("fx") || args.ContainsKey("pound-test") || args.ContainsKey("stomp-test"))
            {
                // M25b test aid: original effects in a plain room (FxTestView).
                AddChild(new FxTestView(gameDir, args) { Name = "FxTest" });
                return;
            }
            if (args.ContainsKey("reload-test")) { AddChild(new ReloadTestView(gameDir, args) { Name = "ReloadTest" }); return; }   // M35 test aid
            if (args.ContainsKey("switch-test")) { AddChild(new SwitchTestView(gameDir, args) { Name = "SwitchTest" }); return; }   // fix/stow-right test aid
            if (args.ContainsKey("appearance-test")) { AddChild(new AppearanceTestView(gameDir, args) { Name = "AppearanceTest" }); return; }   // customizer T6 test aid
            if (args.ContainsKey("ork-test")) { AddChild(new OrkTestView(gameDir, args) { Name = "OrkTest" }); return; }   // Ork O3 test aid
            if (args.ContainsKey("connect") || args.ContainsKey("play-demo"))   // M40 T0: --play-demo replays a recorded client without a server
            {
                // M44: "Leave match" in the in-match menu goes back to the front end from a plain --connect too. A copy taken before ApplyProfileLoadout adds the class and item
                // arguments of the profile's loadout to args: the Join that follows must use the player's saved choice, not these.
                _gameDir = gameDir; _launchArgs = new Dictionary<string, string?>(args, args.Comparer);
                ApplyProfileLoadout(args, gameDir);
                StartNetwork(args, gameDir);
                return;
            }
            // The front end (J1): no --connect and no model-viewer argument, or --menu / --customizer / --appearance.
            if (WantsFrontEnd(args))
            {
                StartFrontEnd(args, gameDir);
                return;
            }
            if (!args.ContainsKey("mesh") && !args.ContainsKey("archive"))
            {
                // Default view (M3): the animated multiplayer Space Marine.
                float? freeze = args.GetValueOrDefault("time") is { } t && float.TryParse(t, System.Globalization.CultureInfo.InvariantCulture, out var seconds) ? seconds : null;
                var character = new CharacterViewer(gameDir, args.GetValueOrDefault("anim"), freeze, args.ContainsKey("bones"), args.GetValueOrDefault("chaos")) { Name = "Character" };
                AddChild(character);
                // --stance low|mid|high|t (test aid): shows a blended stance (CharacterViewer.Stance), e.g. the rifle's aim triple
                // "SM_NAV_IdleLFocus_S_AimDown_01|SM_NAV_IdleLFocus_S_AimForward_01|SM_NAV_IdleLFocus_S_AimUp_01|0.5"; empty low / high leave a side out.
                if (args.GetValueOrDefault("stance") is { } stance)
                {
                    var parts = stance.Split('|');
                    float pitchT = parts.Length > 3 && float.TryParse(parts[3], System.Globalization.CultureInfo.InvariantCulture, out var pt) ? pt : 0f;
                    string? Part(int i) => i < parts.Length && parts[i].Length > 0 ? parts[i] : null;
                    if (Part(1) is not { } mid || !character.Stance("test", Part(0), mid, Part(2), pitchT)) notes.Add($"--stance '{stance}': needs low|mid|high|t with a mid clip the rig has");
                }
                // --clip-stats a,b,*text (test aid): prints the length and foot travel of the clips (*text: every clip whose name contains text).
                if (args.GetValueOrDefault("clip-stats") is { } clipStats)
                    foreach (var item in clipStats.Split(',', StringSplitOptions.RemoveEmptyEntries))
                        foreach (var clip in item.StartsWith('*') ? character.FindClips(item[1..]).ToArray() : [item])
                            GD.Print("CLIPSTATS " + (character.ClipFootTravel(clip) ?? clip + ": not in the rig"));
                // --weapons a,b,c (M24 test aid): wargear names of the primary, pistol and melee weapon (empty: none) drawn on the character,
                // --hand primary|pistol|melee: the one in hand (the others stowed), --worn <wargear,...>: worn models (jump pack), --view front|side|back|left.
                if (args.ContainsKey("weapons") || args.ContainsKey("worn"))
                {
                    var given = (args.GetValueOrDefault("weapons") ?? "").Split(',');
                    string? Given(int i) => i < given.Length && given[i].Trim().Length > 0 ? given[i].Trim() : null;
                    var worn = (args.GetValueOrDefault("worn") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
                    var weaponNotes = new List<string>();
                    var all = given.Concat(worn).Where(n => n.Trim().Length > 0).Select(n => n.Trim());
                    var weaponAssets = WeaponAssets.Load(gameDir, character.Assets!, all, weaponNotes);
                    var rig = new WeaponRig(character.Skeleton!, weaponAssets);
                    rig.Configure((0, 0, 0, "viewer", null), Given(0), Given(1), Given(2), worn, worn.Length > 0);
                    string hand = args.GetValueOrDefault("hand") ?? "primary";
                    rig.Update(hand == "pistol" ? SmSim.WeaponSlot.Pistol : SmSim.WeaponSlot.Ranged, hand == "melee", true, 0f, 0.6f);
                    character.Weapons = rig;
                    if (worn.Length > 0) character.SetPieceHidden("_backpack", args.GetValueOrDefault("keep-backpack") is null && rig.WornCount > 0);
                    notes.AddRange(weaponNotes);
                    GD.Print("weapons: " + string.Join(", ", rig.Description) + $"; {rig.WornCount} worn nodes; {weaponAssets.ModelsLoaded} models in {weaponAssets.LoadMilliseconds:F0} ms");
                }
                notes.AddRange(character.Notes);
                // The marine faces +Y in game space (its backpack is on the −Y side), which is −Z in Godot.
                var viewFacing = args.GetValueOrDefault("view") switch
                {
                    "side" => new Vector3(1f, 0.1f, 0f), "left" => new Vector3(-1f, 0.1f, 0f), "back" => new Vector3(0f, 0.2f, 1f), "front" => new Vector3(0f, 0.15f, -1f),
                    _ => new Vector3(0.55f, 0.2f, -1f),
                };
                AddCameraAndLights(character.Bounds, viewFacing.Normalized());
                status = character.Status;
                Finish(status, notes);
                return;
            }
            string archivePath = System.IO.Path.Combine(gameDir, args.GetValueOrDefault("archive") ?? DefaultArchive);
            var model = StaticModel.Load(archivePath, args.GetValueOrDefault("mesh") ?? DefaultMesh, notes);
            var aabb = AddModel(model, notes);
            var focus = args.GetValueOrDefault("focus") is { } material ? FocusOn(model, material, notes) : null;
            AddCameraAndLights(focus?.Bounds ?? aabb, focus?.Facing);
            int textured = model.Surfaces.Count(s => s.DiffuseDds is not null);
            status = $"{model.MeshName} from {model.Archive}: {model.Mesh.Blocks.Sum(b => b.Positions.Length)} vertices, " +
                     $"{model.Surfaces.Count} surfaces, {textured} with original diffuse texture. " +
                     (textured == model.Surfaces.Count ? "Label: original asset loaded." : "Some surfaces use a placeholder colour.");
        }
        catch (Exception ex)
        {
            status = "Could not load the model: " + ex.Message;
            AddCameraAndLights(new Aabb(new Vector3(-1, -1, -1), new Vector3(2, 2, 2)));
        }

        Finish(status, notes);
    }

    string? _foundNote;
    System.Threading.Tasks.Task<GameLocation?>? _locating;
    CanvasLayer? _lookingLayer;
    Dictionary<string, string?> _lookArgs = new();
    string _lookRepoRoot = "";

    /// <summary>Test views that need the install before they start (they keep the synchronous lookup).</summary>
    static readonly string[] FrontEndExcluded = ["fx", "pound-test", "stomp-test", "reload-test", "switch-test", "appearance-test"];

    void BeginLocate(Dictionary<string, string?> args, string repoRoot)
    {
        _lookArgs = args; _lookRepoRoot = repoRoot;
        _lookingLayer = new CanvasLayer { Name = "LookingLayer" };
        var background = new ColorRect { Color = new Color(0.04f, 0.04f, 0.05f) };
        background.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        var label = new Label { Text = "Looking for the game...", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        label.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        label.AddThemeFontSizeOverride("font_size", 36);
        _lookingLayer.AddChild(background);
        _lookingLayer.AddChild(label);
        AddChild(_lookingLayer);
        string? explicitDir = args.GetValueOrDefault("game-dir");
        _locating = System.Threading.Tasks.Task.Run(() => GameInstall.TryLocate(explicitDir, repoRoot));
    }

    /// <summary>The lookup finished: the front end when the install was found, else the game-folder screen. Any failure here is shown, never silent.</summary>
    void PollLocate()
    {
        if (_locating is not { IsCompleted: true } task) return;
        _locating = null;
        _lookingLayer?.QueueFree();
        _lookingLayer = null;
        try
        {
            var located = task.IsCompletedSuccessfully ? task.Result : null;
            if (located is null)
            {
                if (task.IsFaulted) GD.PrintErr("looking for the game failed: " + task.Exception?.GetBaseException().Message);
                ShowGameFolderSetup(_lookArgs, _lookRepoRoot);
                return;
            }
            _foundNote = FoundNote(located);
            _forbiddenScreenshotDirs.Add(located.Dir);
            StartFrontEnd(_lookArgs, located.Dir);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            GD.PrintErr("front end failed to start: " + ex);
            Finish("Could not start the front end: " + ex.Message, new List<string>());
        }
    }

    static string FoundNote(GameLocation location) => location.Source.StartsWith("Steam library", StringComparison.Ordinal) ? $"Found: {location.Source}" : $"Found: {location.Source} ({location.Dir})";

    void StartFrontEnd(Dictionary<string, string?> args, string gameDir)
    {
        _gameDir = gameDir; _launchArgs = args;
        GD.Print(_foundNote);
        UiSounds.Ensure(this, gameDir, args.ContainsKey("no-sound"));   // the menus' sounds (fidelity gap 9); one node for the app, handed to the match when one starts
        _frontEnd = new FrontEndView(() => MakeServices(args, gameDir), OnJoin, args.ContainsKey("appearance") ? StartScreen.Appearance : args.ContainsKey("customizer") ? StartScreen.Creator : StartScreen.Join, args) { Name = "FrontEnd", GameFoundNote = _foundNote };
        var layer = new CanvasLayer { Name = "FrontEndLayer" };
        layer.AddChild(_frontEnd);
        AddChild(layer);
        UiSounds.Current?.Attach(layer);   // the menus' sounds on every button of this screen
    }

    /// <summary>No install found at start: asks for the game folder (GameFolderSetup), then goes on as if it had been found.</summary>
    void ShowGameFolderSetup(Dictionary<string, string?> args, string repoRoot)
    {
        var layer = new CanvasLayer { Name = "SetupLayer" };
        var setup = new GameFolderSetup(location =>
        {
            _foundNote = FoundNote(location);
            _forbiddenScreenshotDirs.Add(location.Dir);
            layer.QueueFree();
            try { StartFrontEnd(args, location.Dir); }
            catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
            {
                GD.PrintErr("front end failed to start: " + ex);
                Finish("Could not start the front end: " + ex.Message, new List<string>());
            }
        }, repoRoot, args.GetValueOrDefault("game-dir"), args.GetValueOrDefault("setup-path"));
        layer.AddChild(setup);
        AddChild(layer);
    }

    /// <summary>Stops the server the Host button started (never any other process).</summary>
    public override void _ExitTree() => HostedServer.Stop();

    /// <summary>Starts the networked view (M5) for <c>--connect</c> or for a Join from the front end; <paramref name="args"/> are the client's user arguments.</summary>
    void StartNetwork(Dictionary<string, string?> args, string gameDir)
    {
            // M5: networked view. It draws its own overlay and decides when a screenshot is worth taking.
            bool bot = args.ContainsKey("bot");
            bool lean = SmNet.LeanBot.Decide(bot, DisplayServer.GetName(), OS.GetCmdlineUserArgs());   // perf/lean-bots + perf/pass-1: a headless bot (NetworkView.Lean.cs, BotLean)
            if (lean) BotLean.Apply(args);
            // perf/pass-1: a human's client asks the .NET GC to avoid blocking full collections while it plays (background gen2 only, unless memory runs low): a
            // blocking gen2 took an 88 ms frame in the 7-bot replay of the e792504 build. --gc-default keeps the runtime's default (before / after).
            if (!bot && !args.ContainsKey("gc-default"))
            {
                try { System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency; }
                catch (Exception e) when (e is ArgumentOutOfRangeException or NotSupportedException or InvalidOperationException) { GD.Print($"gc: latency mode not set ({e.Message}); the runtime's default stays"); }
            }
            int wait = args.GetValueOrDefault("wait-players") is { } w && int.TryParse(w, out var n) ? n : bot ? 2 : 1;
            var side = NetworkView.ParseSide(args.GetValueOrDefault("side"));
            _network = new NetworkView(gameDir, args.GetValueOrDefault("connect") ?? "", bot, wait, args.ContainsKey("show-collision"), args.ContainsKey("wait-melee"), args.ContainsKey("bot-melee"),
                                      args.GetValueOrDefault("class"),
                                      new LoadoutRequest(args.GetValueOrDefault("primary"), args.GetValueOrDefault("pistol"), args.GetValueOrDefault("melee"),
                                                         string.Equals(args.GetValueOrDefault("bot-loadout"), "random", StringComparison.OrdinalIgnoreCase),
                                                         args.GetValueOrDefault("equipment")),
                                      args.ContainsKey("bot-fire"),
                                      args.GetValueOrDefault("screenshot-at") is { } at && double.TryParse(at, System.Globalization.CultureInfo.InvariantCulture, out var atSeconds) ? atSeconds : null,
                                      args.ContainsKey("wait-blast"),
                                      args.GetValueOrDefault("bot-throw-max") is { } tm && float.TryParse(tm, System.Globalization.CultureInfo.InvariantCulture, out var throwMax) ? throwMax : 20f,
                                      args.ContainsKey("wait-jump"), args.ContainsKey("bot-jump-free"))
                                      {
                                          Name = "Network", RequestedSide = side ?? SmSim.Team.None,
                                          ShowScoreboard = args.ContainsKey("show-scoreboard"), WaitMatchEnd = args.ContainsKey("wait-match-end"),
                                          Spectate = args.GetValueOrDefault("spectate") ?? (args.ContainsKey("spectate") ? "action" : args.ContainsKey("observe") ? "observe" : null),
                                          Observe = args.ContainsKey("observe"), ObservePose = args.GetValueOrDefault("observe-pose"), ObserveFollow = args.ContainsKey("observe-follow"),   // protocol 47: a spectator (NetworkView.Observer.cs)
                                          ShowInfo = args.ContainsKey("show-info"), HudDemo = args.ContainsKey("hud-demo"), AimTest = args.ContainsKey("aim-test"), BotSlot = args.GetValueOrDefault("bot-slot"), BotStand = args.ContainsKey("bot-stand"), BotIdle = args.ContainsKey("bot-idle"), BotFireWalls = args.ContainsKey("bot-fire-walls"), RagdollDebug = args.ContainsKey("ragdoll-debug"), RagdollRest = args.GetValueOrDefault("ragdoll-rest") is { } rr && float.TryParse(rr, System.Globalization.CultureInfo.InvariantCulture, out var restSeconds) ? restSeconds : null, PlaceholderFx = args.ContainsKey("placeholder-fx"), FxSlow = args.GetValueOrDefault("fx-slow") is { } fs && float.TryParse(fs, System.Globalization.CultureInfo.InvariantCulture, out var slow) ? slow : 1f, NoFx = args.ContainsKey("no-fx") || lean, NoHud = args.ContainsKey("no-hud"), NoWeapons = args.ContainsKey("no-weapons"), NoOverlay = args.ContainsKey("no-overlay") || args.ContainsKey("no-markers"), NoMarkers = args.ContainsKey("no-markers"), ShowMarkers = args.ContainsKey("show-markers"), BotGoal = args.GetValueOrDefault("bot-goal"), BotRoute = args.GetValueOrDefault("bot-route"), FrameStatsLog = args.ContainsKey("frame-stats"), NoSound = args.ContainsKey("no-sound"), MapSounds = args.ContainsKey("map-sounds"), HitArmour = args.GetValueOrDefault("hit-armour"), DamageDemo = args.ContainsKey("damage-demo") ? args.GetValueOrDefault("damage-demo") ?? "cycle" : null, SoundGainDb = args.GetValueOrDefault("sound-gain") is { } soundGainText && float.TryParse(soundGainText, System.Globalization.CultureInfo.InvariantCulture, out var soundGain) && float.IsFinite(soundGain) ? soundGain : 0f, NoHipFire = args.ContainsKey("no-hip-fire"), NoLocomotion = args.ContainsKey("no-locomotion"), YawProbe = args.ContainsKey("yaw-probe"), NoViewSmoothing = args.ContainsKey("no-view-smoothing"), PanRate = args.GetValueOrDefault("pan-rate") is { } pr && float.TryParse(pr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var panRate) ? panRate : 0f, BotGait = args.ContainsKey("bot-gait"), HipProbe = args.ContainsKey("hip-probe"), PerWeaponSprint = args.ContainsKey("per-weapon-sprint"),
                                      };
            if (side is null) GD.PrintErr($"--side '{args.GetValueOrDefault("side")}' is not marines or chaos; the class's side is used");
            _network.LeaveToMenu = () => CallDeferred(nameof(LeaveMatch));   // M44: the Esc menu's "LEAVE MATCH"
            AddChild(_network);
    }

    /// <summary>"Leave match" in the in-match menu: back to the front end's Join screen with a short note (deferred: it frees the view that asked).</summary>
    void LeaveMatch()
    {
        if (_network is null) return;   // already leaving
        foreach (var key in LoadoutArguments.Append("loadout")) _launchArgs.Remove(key);   // a stale --class / --loadout must not override the saved choice on the next Join
        BackToFrontEnd("You left the match.");
    }

    static readonly string[] ViewerArguments = ["mesh", "archive", "anim", "time", "bones", "chaos", "stance", "clip-stats", "weapons", "worn", "hand", "view", "paint", "focus", "viewer", "keep-backpack"];

    /// <summary>The front end opens when asked (<c>--menu</c>, <c>--customizer</c>, <c>--appearance</c>) or when nothing else says what to show; any model-viewer argument keeps the old viewers.</summary>
    static bool WantsFrontEnd(Dictionary<string, string?> args) =>
        !args.ContainsKey("connect") && !args.ContainsKey("play-demo")   // a networked or replayed launch is never the front end
        && (args.ContainsKey("menu") || args.ContainsKey("customizer") || args.ContainsKey("appearance") || !ViewerArguments.Any(args.ContainsKey));

    /// <summary>The content profile of the last Join (added content E2): after a match the front end keeps the player's in-session choice, not the <c>--content</c> seed.</summary>
    SmSim.ContentExtras? _lastContent;

    CustomizerServices MakeServices(Dictionary<string, string?> args, string gameDir) =>
        new(gameDir, args.GetValueOrDefault("profile"), args.ContainsKey("lock-demo"),
            _lastContent is { } last ? SmSim.ContentProfiles.Name(last) : args.GetValueOrDefault("content"));

    /// <summary>The Join button: leaves the front end and starts the networked view with the active loadout (the same arguments <c>--connect</c> takes).</summary>
    void OnJoin(JoinRequest request)
    {
        var args = new Dictionary<string, string?>(_launchArgs, StringComparer.OrdinalIgnoreCase) { ["connect"] = request.Host };
        foreach (var (key, value) in request.Plan.ToArgs()) args[key] = value;
        if (request.Spectate) args["observe"] = "";   // protocol 47: the Join screen's SPECTATE button (not remembered: the next Join is a player's again)
        FrontEndLaunch.Current = request;
        _lastContent = request.Content ?? request.Services.Content;
        _frontEnd?.Release();
        _frontEnd?.GetParent()?.QueueFree();
        _frontEnd = null;
        request.Services.Previews.Clear();   // the preview's models are not needed in the match
        _fromFrontEnd = true;
        StartNetwork(args, _gameDir);
        // A hint that stays until the join has worked: Esc goes back to the menu (a wrong address, a rejection or a lost server must not strand the player).
        _backLayer = new CanvasLayer { Name = "BackHint", Layer = 50 };
        _backHint = new Label { Text = "Esc: back to the menu", Visible = false, Position = new Vector2(24, 0) };
        _backHint.SetAnchorsPreset(Control.LayoutPreset.BottomLeft);
        _backHint.OffsetTop = -44; _backHint.OffsetLeft = 24;
        _backHint.AddThemeFontSizeOverride("font_size", 22);
        _backHint.AddThemeColorOverride("font_color", new Color(1f, 0.8f, 0f));
        _backHint.AddThemeColorOverride("font_outline_color", Colors.Black);
        _backHint.AddThemeConstantOverride("outline_size", 4);
        _backLayer.AddChild(_backHint);
        AddChild(_backLayer);
    }

    /// <summary>Esc while the join is going nowhere (still connecting, rejected, disconnected, load error): back to the front end with the reason on its Join screen.</summary>
    void BackToFrontEnd(string? reasonOverride = null)
    {
        string reason = reasonOverride ?? (_network?.FailureText is { Length: > 0 } text ? text : "Could not connect to the server.");
        _network?.QueueFree();
        _network = null;
        _backLayer?.QueueFree();
        _backLayer = null; _backHint = null;
        _fromFrontEnd = false;
        FrontEndLaunch.Current = null;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        _frontEnd = new FrontEndView(() => MakeServices(_launchArgs, _gameDir), OnJoin, StartScreen.Join, _launchArgs, reason) { Name = "FrontEnd", GameFoundNote = _foundNote };
        var layer = new CanvasLayer { Name = "FrontEndLayer" };
        layer.AddChild(_frontEnd);
        AddChild(layer);
        UiSounds.Current?.Attach(layer);   // the menus' sounds on every button of this screen
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (_fromFrontEnd && _network is { } n && (n.ConnectionFailed || n.StillConnecting) && e is InputEventKey { Pressed: true, Echo: false, PhysicalKeycode: Key.Escape })
        {
            GetViewport().SetInputAsHandled();
            BackToFrontEnd();
        }
    }

    static readonly string[] ScriptedArguments = ["test-window", "screenshot", "quit-after", "spectate", "observe", "hud-demo", "aim-test"];

    static readonly string[] LoadoutArguments = ["class", "primary", "pistol", "melee", "equipment", "bot-loadout"];

    /// <summary>
    /// A human <c>--connect</c> without loadout arguments plays the profile's selected loadout (docs/customizer-architecture.md section 6); <c>--loadout</c> picks another
    /// (<c>default:tactical</c> or a slot 1 to 10). With <c>--bot</c> the profile is never read unless <c>--profile</c> or <c>--loadout</c> is given. No profile file and no
    /// <c>--loadout</c>: nothing changes (today's behaviour, no wait).
    /// </summary>
    void ApplyProfileLoadout(Dictionary<string, string?> args, string gameDir)
    {
        if (LoadoutArguments.Any(args.ContainsKey) || args.ContainsKey("play-demo")) return;   // a replay keeps its recorded Hello
        bool asked = args.ContainsKey("profile") || args.ContainsKey("loadout");
        if (args.ContainsKey("bot") && !asked) return;
        // Scripted runs (test windows, screenshots, timed or spectating clients, HUD and aim demos) never pick up the owner's own profile unless they name one.
        if (!asked && ScriptedArguments.Any(args.ContainsKey)) return;
        string profileDir = args.GetValueOrDefault("profile") is { Length: > 0 } file ? file : System.IO.Path.Combine(ProjectSettings.GlobalizePath("user://"), SmContent.Customization.ProfileStore.FileName);
        if (!asked && !System.IO.File.Exists(profileDir)) return;
        try
        {
            var services = MakeServices(args, gameDir);
            services.LoadTask.Wait();
            if (!services.Ready) { GD.PrintErr("profile: " + services.Error + "; the class default is used"); return; }
            var selection = args.GetValueOrDefault("loadout") is { } text ? SmClient.Customizer.Model.LaunchOptions.ParseLoadout(text, services.Catalog!) : null;
            if (args.ContainsKey("loadout") && selection is null) GD.PrintErr($"--loadout '{args["loadout"]}' is neither default:<role> nor a slot 1 to 10; the profile's selection is used");
            var side = args.GetValueOrDefault("side") is { } sideText && sideText.StartsWith("c", StringComparison.OrdinalIgnoreCase) ? SmClient.Customizer.Model.SideChoice.Chaos
                : args.ContainsKey("side") ? SmClient.Customizer.Model.SideChoice.SpaceMarines : SmClient.Customizer.Model.SideChoice.Auto;
            var chosen = selection ?? SmClient.Customizer.Model.LoadoutEdits.Active(services.Session!.Working, services.Catalog!, out _);
            var plan = SmClient.Customizer.Model.JoinPlan.Build(services.Session!.Working, chosen, services.Config!, services.Catalog!, side, services.Content);
            foreach (var (key, value) in plan.ToArgs()) if (!args.ContainsKey(key)) args[key] = value;
            FrontEndLaunch.Current = new JoinRequest(args.GetValueOrDefault("connect") ?? "", plan, services, services.Content);
            GD.Print($"loadout from the profile: {plan.Summary}");
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            GD.PrintErr("profile: " + ex.Message + "; the class default is used");
        }
    }

    void Finish(string status, List<string> notes)
    {
        GD.Print(status);
        foreach (var n in notes) GD.Print("  note: " + n);
        var overlay = new CanvasLayer();
        overlay.AddChild(new Label { Text = status + (notes.Count > 0 ? $"\n{notes.Count} notes in the console." : ""), Position = new Vector2(12, 8) });
        AddChild(overlay);
    }

    public override void _Process(double delta)
    {
        // A few frames let textures upload and the first image settle before capturing.
        _elapsed += delta;
        if (_quitAfter is { } limit && _elapsed >= limit && (_screenshot is null || _screenshotEvery is not null)) { GetTree().Quit(0); return; }
        PollLocate();
        if (_locating is not null) return;   // still looking for the game: nothing to show or capture yet
        // The networked view waits until it is connected and the players are in view.
        if (_backHint is not null) _backHint.Visible = _network is { } hinted && (hinted.ConnectionFailed || hinted.StillConnecting);
        // --auto-back <seconds> (test aid): the Esc of a join that goes nowhere, pressed after that long.
        if (_fromFrontEnd && _launchArgs.GetValueOrDefault("auto-back") is { } autoBack && double.TryParse(autoBack, System.Globalization.CultureInfo.InvariantCulture, out var backAfter)
            && _network is { } stuck && (stuck.ConnectionFailed || stuck.StillConnecting) && _elapsed > backAfter) BackToFrontEnd();
        if (_screenshot is null) return;
        if (_network is not null ? !_network.ReadyForScreenshot : _frontEnd is not null ? !_frontEnd.ReadyForScreenshot : ++_frames != 10) return;
        // --screenshot-every (showcase aid): a numbered series from the first ready frame until --quit-after.
        if (_screenshotEvery is not null && (_elapsed < _nextSeriesShot || (_onEffect && _network is { EffectInView: false }))) return;
        string name = _screenshotEvery is null ? _screenshot
            : System.IO.Path.ChangeExtension(_screenshot, null) + $"_{++_seriesIndex:000}" + System.IO.Path.GetExtension(_screenshot);
        // Relative names go to the private data folder, never into the repo (screenshots show original assets).
        string path = System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(name)
            ? name
            : System.IO.Path.Combine(GameInstall.DataRoot, "screenshots", name));
        // Never write into the repository or the game install, whatever path was given (".." included).
        if (_forbiddenScreenshotDirs.Any(dir => SmFormats.InstallPaths.IsWithin(path, dir)))
        {
            GD.Print($"screenshot refused: {path} is inside the repository or the game folder");
            _screenshot = null;
            if (_quit && _quitAfter is null) GetTree().Quit(1);
            return;
        }
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var err = GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print(err == Error.Ok ? $"screenshot -> {path}" : $"screenshot failed: {err}");
        if (_screenshotEvery is { } interval) { _nextSeriesShot = _elapsed + interval; return; }
        _screenshot = null;
        if (_quit && _quitAfter is null) GetTree().Quit(err == Error.Ok ? 0 : 1);
    }

    Aabb AddModel(StaticModel model, List<string> notes)
    {
        var mesh = new ArrayMesh();
        var textures = new Dictionary<string, Texture2D?>(StringComparer.OrdinalIgnoreCase);
        Aabb? bounds = null;
        var blockArrays = new Dictionary<int, Godot.Collections.Array>();
        foreach (var surface in model.Surfaces)
        {
            var block = model.Mesh.Blocks[surface.BlockIndex];
            int n = block.Positions.Length;
            if (n == 0 || surface.Range.IndexCount < 3) { notes.Add($"surface '{surface.Range.Name}' has no triangles; skipped"); continue; }
            var indices = new int[surface.Range.IndexCount - surface.Range.IndexCount % 3];
            for (int i = 0; i < indices.Length; i++) indices[i] = block.Indices[surface.Range.StartIndex + i];
            if (!blockArrays.TryGetValue(surface.BlockIndex, out var shared))
            {
                shared = BlockArrays(block);
                blockArrays[surface.BlockIndex] = shared;
                var blockBounds = new Aabb(shared[(int)Mesh.ArrayType.Vertex].AsVector3Array()[0], Vector3.Zero);
                foreach (var p in shared[(int)Mesh.ArrayType.Vertex].AsVector3Array()) blockBounds = blockBounds.Expand(p);
                bounds = bounds is { } b0 ? b0.Merge(blockBounds) : blockBounds;
            }
            // Surfaces of one block share its vertex arrays; only the index list differs.
            var arrays = shared.Duplicate();
            arrays[(int)Mesh.ArrayType.Index] = indices;
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, MaterialFor(surface, textures, notes));
        }
        AddChild(new MeshInstance3D { Mesh = mesh, Name = model.MeshName });
        return bounds ?? new Aabb();
    }

    internal static Godot.Collections.Array BlockArrays(SmFormats.Dcm.DcmBlock block)
    {
        int n = block.Positions.Length;
        var vertices = new Vector3[n];
        var normals = new Vector3[n];
        var tangents = new float[4 * n];
        var uv = new Vector2[n];
        var uv2 = new Vector2[n];
        for (int v = 0; v < n; v++)
        {
            vertices[v] = ToGodot(block.Positions[v]);
            normals[v] = ToGodot(block.Normals[v]);
            var t = ToGodot(new Num.Vector3(block.Tangents[v].X, block.Tangents[v].Y, block.Tangents[v].Z));
            (tangents[4 * v], tangents[4 * v + 1], tangents[4 * v + 2], tangents[4 * v + 3]) = (t.X, t.Y, t.Z, block.Tangents[v].W);
            uv[v] = new Vector2(block.Uv0[v].X, block.Uv0[v].Y);
            uv2[v] = new Vector2(block.Uv1[v].X, block.Uv1[v].Y);
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.Tangent] = tangents;
        arrays[(int)Mesh.ArrayType.TexUV] = uv;
        arrays[(int)Mesh.ArrayType.TexUV2] = uv2;
        return arrays;
    }

    internal static Material MaterialFor(StaticModelSurface surface, Dictionary<string, Texture2D?> cache, List<string> notes)
    {
        var material = new StandardMaterial3D
        {
            TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
            AlbedoColor = new Color(1f, 0.2f, 0.8f),    // placeholder colour when no original texture resolves
        };
        if (surface.DiffuseName is null || surface.DiffuseDds is null) return material;
        if (!cache.TryGetValue(surface.DiffuseName, out var texture))
        {
            var image = new Image();
            var err = image.LoadDdsFromBuffer(surface.DiffuseDds);
            texture = err == Error.Ok ? ImageTexture.CreateFromImage(image) : null;
            if (texture is null) notes.Add($"texture '{surface.DiffuseName}' failed to decode: {err}");
            cache[surface.DiffuseName] = texture;
        }
        if (texture is not null) { material.AlbedoTexture = texture; material.AlbedoColor = Colors.White; }
        return material;
    }

    /// Bounds and average front direction of the triangles drawn with one material, for close inspection.
    static (Aabb Bounds, Vector3 Facing)? FocusOn(StaticModel model, string material, List<string> notes)
    {
        Aabb? bounds = null;
        var facing = Vector3.Zero;
        foreach (var s in model.Surfaces.Where(s => s.Range.Name.Equals(material, StringComparison.OrdinalIgnoreCase)))
        {
            var block = model.Mesh.Blocks[s.BlockIndex];
            for (int i = s.Range.StartIndex; i < s.Range.StartIndex + s.Range.IndexCount; i++)
            {
                var p = ToGodot(block.Positions[block.Indices[i]]);
                bounds = bounds is { } b ? b.Expand(p) : new Aabb(p, Vector3.Zero);
                facing += ToGodot(block.Normals[block.Indices[i]]);
            }
        }
        if (bounds is null) { notes.Add($"--focus: no surface uses material '{material}'"); return null; }
        return (bounds.Value, facing.LengthSquared() > 0 ? facing.Normalized() : new Vector3(1, 0.35f, 1).Normalized());
    }

    void AddCameraAndLights(Aabb bounds, Vector3? facing = null)
    {
        var center = bounds.GetCenter();
        float radius = Mathf.Max(bounds.Size.Length() * 0.5f, 0.5f);
        var camera = new Camera3D { Far = radius * 20f, Near = Mathf.Max(radius * 0.001f, 0.01f), Fov = 60f };
        AddChild(camera);
        // Distance at which a sphere of this radius fits the vertical field of view, with a margin.
        float distance = radius / Mathf.Sin(Mathf.DegToRad(camera.Fov * 0.5f)) * 1.1f;
        var direction = facing ?? new Vector3(1f, 0.35f, 1f).Normalized();
        camera.GlobalPosition = center + direction * distance;
        camera.LookAt(center, Mathf.Abs(direction.Dot(Vector3.Up)) > 0.99f ? Vector3.Forward : Vector3.Up);

        var sun = new DirectionalLight3D { ShadowEnabled = true, LightEnergy = 1.1f };
        AddChild(sun);
        sun.RotationDegrees = new Vector3(-50f, 35f, 0f);

        var environment = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color(0.18f, 0.2f, 0.23f),
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.55f, 0.57f, 0.6f),
            AmbientLightEnergy = 0.6f,
            TonemapMode = Godot.Environment.ToneMapper.Filmic,
        };
        AddChild(new WorldEnvironment { Environment = environment });
    }

    /// The game is Z-up (docs/discovery.md); Godot is Y-up. A rotation, so triangle winding is unchanged.
    static Vector3 ToGodot(Num.Vector3 v) => new(v.X, v.Z, -v.Y);

    static Dictionary<string, string?> ParseArgs(string[] args)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) continue;
            string key = args[i][2..];
            string? value = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : null;
            result[key] = value;
        }
        return result;
    }
}
