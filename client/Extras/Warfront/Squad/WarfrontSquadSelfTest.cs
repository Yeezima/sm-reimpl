using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SmClient.Customizer;
using SmContent;
using SmContent.Customization;
using SmNet.Extras.Warfront;
using SmSim;
using Num = System.Numerics;

namespace SmClient.Extras.Warfront.Squad;

/// <summary>
/// Self-test of the Warfront squad's client side (W10): the demo feed (<see cref="WarfrontSquadDemoFeed"/>, through the wire's own writers) drives the real character renderer, the HUD, the revive prompt and the
/// squad panel, with no server. Headless: <c>godot.cmd res://Extras/Warfront/Squad/WarfrontSquadSelfTest.tscn -- --squad-selftest</c>. Options: <c>--squad-shots &lt;dir&gt;</c> (a windowed run,
/// <c>godot.cmd --windowed ...</c>, saves the stills), <c>--squad-size 1600x900</c>, <c>--squad-cost</c> (the cost of 36 Marines against none, bounded; windowed), <c>--squad-clips</c> (prints the clips found),
/// <c>--squad-no-lod</c> (the far-pose stride off), <c>--squad-quick</c> (the first checks only). Prints one SELFTEST PASS or FAIL per check and SQUAD SELFTEST RESULT; quits non-zero on a failure. Looks come from the customizer's own path
/// with four preset colour schemes (read from the owner's install; nothing original is kept).
/// </summary>
public sealed partial class WarfrontSquadSelfTest : Node
{
    int _passed, _failed, _shot;
    string? _shotDir;
    string _gameDir = "";
    double _now;
    Node3D _space = null!;
    Camera3D _camera = null!;
    WarfrontSquadView _view = null!;
    WarfrontSquadDemoFeed _feed = null!;
    WarfrontSquadLooks _looks = null!;
    CustomizationCatalog _catalog = null!;
    DemoDresser _dresser = null!;
    readonly List<byte[]> _commands = new();
    Num.Vector3 _player;
    Control _root = null!;
    int _frames;

    public override void _Ready() => _ = RunAsync();

    async Task Frames(int n = 3) { for (int i = 0; i < n; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }

    void Check(string name, bool ok, string detail = "")
    {
        if (ok) _passed++; else _failed++;
        GD.Print($"SELFTEST {(ok ? "PASS" : "FAIL")} {name}{(!ok && detail.Length > 0 ? " - " + detail : "")}");
    }

    static string? Arg(string name) { var a = OS.GetCmdlineUserArgs(); int i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }
    static bool Flag(string name) => Array.IndexOf(OS.GetCmdlineUserArgs(), name) >= 0;

    /// <summary>The demo's dress: the squad class ids to catalogue roles, the owner's colours from four chapter presets, the gear names fixed.</summary>
    sealed class DemoDresser(CustomizationCatalog catalog, Func<int, AppearanceCode> ownerCode) : IWarfrontSquadDresser
    {
        static readonly string[] Roles = ["assault", "tactical", "heavy"];
        static readonly string[] Primary = ["", "sm_mp_ranged_bolter", "sm_mp_exotic_las_cannon"], Secondary = ["sm_mp_pistol_bolt_pistol", "sm_mp_pistol_bolt_pistol", ""], Melee = ["sm_mp_melee_chainsword", "sm_mp_melee_combat_knife", ""];
        public WarfrontMarineDress? Dress(in WarfrontSquadShown s)
        {
            if (s.Class > 2) return null;
            string? key = WarfrontSquadLooks.ClassKeyFor(catalog, null, Roles[s.Class], chaos: false);
            if (key is null) return null;
            return new WarfrontMarineDress(key, WarfrontSquadModel.LookCode(ownerCode(s.OwnerPlayer), s.OwnPieces), [], Primary[s.Class].Length > 0 ? Primary[s.Class] : null, Secondary[s.Class].Length > 0 ? Secondary[s.Class] : null, Melee[s.Class].Length > 0 ? Melee[s.Class] : null, s.Class == 0 ? "sm_mp_abil_jumppack" : null);
        }
    }

    AppearanceCode OwnerCode(int player)
    {
        var race = catalogRace();
        var presets = race.Presets;
        int pick = presets.Count == 0 ? -1 : (player * Math.Max(1, presets.Count / 5) + 1) % presets.Count;
        var profile = AppearanceProfile.Empty("demo" + player, CustomizationCatalog.SpaceMarine, "demo");
        if (pick >= 0) profile = ProfileWire.ApplyPreset(profile, presets[pick], _catalog);
        return ProfileWire.ToCode(profile, _catalog);
    }

    RaceCatalog catalogRace() => _catalog.Race(CustomizationCatalog.SpaceMarine)!;

    void BuildStage(int width, int height)
    {
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(0.16f, 0.18f, 0.22f),
            AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color(0.6f, 0.62f, 0.66f), AmbientLightEnergy = 0.8f,
        };
        AddChild(new WorldEnvironment { Environment = env });
        AddChild(new DirectionalLight3D { LightEnergy = 1.15f, ShadowEnabled = true, RotationDegrees = new Vector3(-50f, 30f, 0f) });
        var checker = Image.CreateEmpty(2, 2, false, Image.Format.Rgb8);
        checker.SetPixel(0, 0, new Color(0.32f, 0.33f, 0.35f)); checker.SetPixel(1, 1, new Color(0.32f, 0.33f, 0.35f));
        checker.SetPixel(1, 0, new Color(0.22f, 0.23f, 0.25f)); checker.SetPixel(0, 1, new Color(0.22f, 0.23f, 0.25f));
        AddChild(new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(400, 400) },
            MaterialOverride = new StandardMaterial3D { AlbedoTexture = ImageTexture.CreateFromImage(checker), TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest, Uv1Scale = new Vector3(200, 200, 1) },
        });
        _space = new Node3D { Name = "GameSpace", Basis = new Basis(new Vector3(1, 0, 0), new Vector3(0, 0, -1), new Vector3(0, 1, 0)) };
        AddChild(_space);
        _camera = new Camera3D { Current = true, Fov = 70f, Near = 0.05f, Far = 400f };
        AddChild(_camera);
        // The Orks' stand-ins: twelve green pillars where the tracers end.
        for (int id = 1001; id <= 1012; id++)
            if (_feed.EnemyAt(id) is { } p)
                _space.AddChild(new MeshInstance3D { Mesh = new CapsuleMesh { Radius = 0.45f, Height = 1.9f }, Basis = new Basis(new Vector3(1, 0, 0), Mathf.Pi / 2f), Position = new Vector3(p.X, p.Y, 0.95f), MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.25f, 0.5f, 0.2f) } });
    }

    void Place(Num.Vector3 from, Num.Vector3 to)
    {
        _camera.GlobalPosition = _space.ToGlobal(new Vector3(from.X, from.Y, from.Z));
        _camera.LookAt(_space.ToGlobal(new Vector3(to.X, to.Y, to.Z)), Vector3.Up);
    }

    /// <summary>One virtual frame of 1/30 s: the feed's sections through the view, then the frame.</summary>
    async Task Step(int frames = 1, double dt = 1.0 / 30.0)
    {
        for (int i = 0; i < frames; i++)
        {
            _now += dt;
            if (dt > 0) foreach (var (tag, payload) in _feed.Step(dt)) _view.Section(tag, payload, _now);
            _view.Frame(_now, dt, _camera, _space, _player);
            _frames++;
            await Frames(1);
        }
    }

    /// <summary>Steps until every known Marine has its real model (or <paramref name="seconds"/> of real time ran out).</summary>
    async Task<bool> WaitModels(double seconds)
    {
        double start = Time.GetTicksMsec() / 1000.0;
        while (Time.GetTicksMsec() / 1000.0 - start < seconds)
        {
            await Step(1, 0.0);   // the script's time stands still while the models are read and built
            if (_view.Crowd.Drawn > 0 && _view.Crowd.WithModel == _view.Crowd.Drawn) return true;
        }
        return false;
    }

    async Task Still(string name)
    {
        await Frames(4);
        if (_shotDir is null) return;
        var img = GetViewport().GetTexture()?.GetImage();
        if (img is null || img.IsEmpty()) { GD.Print("SELFTEST NOTE no image for " + name); return; }
        string path = System.IO.Path.Combine(_shotDir, $"{++_shot:00}-{name}.png");
        img.SavePng(path);
        GD.Print($"SELFTEST SHOT {path} ({img.GetWidth()}x{img.GetHeight()})");
    }

    async Task Fresh(int per, int owners, double startAt, float spread = 1f)
    {
        if (_view is not null) { _view.Crowd.QueueFree(); _view.Hud.QueueFree(); _view.Panel.QueueFree(); _view.QueueFree(); await Frames(2); }
        _feed = new WarfrontSquadDemoFeed(per, owners, default, spread);
        _view = new WarfrontSquadView(_looks, _gameDir, id => id switch { 0 => "ASSAULT", 1 => "TACTICAL", 2 => "DEVASTATOR", _ => "?" })
        {
            PlayerName = WarfrontSquadDemoFeed.PlayerName,
            OwnHasJumpPack = () => false,
            SendCommand = _commands.Add,
            UseKeyLabel = "E",
        };
        _view.Model.OwnPlayer = 1;
        _view.Crowd.Dresser = _dresser;
        _view.Crowd.EnemyAt = id => _feed.EnemyAt(id);
        _view.Crowd.Lod = !Flag("--squad-no-lod");
        _view.Crowd.Shot = (from, to, cls) => TracerAt(from, to);
        _space.AddChild(_view.Crowd);
        _root.AddChild(_view.Hud);
        _root.AddChild(_view.Panel);
        AddChild(_view);
        _now = 0;
        _player = new Num.Vector3(0, 0, 0);
        if (startAt > 0) { _now = startAt; foreach (var (tag, payload) in _feed.Step(startAt)) if (tag != SquadWire.EventsTag) _view.Section(tag, payload, _now); }   // the history's events are not replayed
    }

    readonly List<(MeshInstance3D Node, double Until)> _tracers = new();

    /// <summary>The demo's shot: a thin bright streak (the networked view plays the Guardsmen's effect instead).</summary>
    void TracerAt(Num.Vector3 from, Num.Vector3 to)
    {
        var a = new Vector3(from.X, from.Y, from.Z); var b = new Vector3(to.X, to.Y, to.Z + 1.2f);
        var mesh = new ImmediateMesh();
        mesh.SurfaceBegin(Mesh.PrimitiveType.Lines);
        mesh.SurfaceAddVertex(a); mesh.SurfaceAddVertex(a.Lerp(b, 0.12f));
        mesh.SurfaceEnd();
        var node = new MeshInstance3D { Mesh = mesh, MaterialOverride = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = new Color(1f, 0.85f, 0.4f) }, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        _space.AddChild(node);
        _tracers.Add((node, _now + 0.08));
        while (_tracers.Count > 0 && _tracers[0].Until <= _now) { _tracers[0].Node.QueueFree(); _tracers.RemoveAt(0); }
    }

    async Task RunAsync()
    {
        var args = OS.GetCmdlineUserArgs();
        _shotDir = Arg("--squad-shots");
        if (_shotDir is not null) System.IO.Directory.CreateDirectory(_shotDir);
        UiKit.EnterVirtualSpace(GetWindow());
        if (Arg("--squad-size") is { } size)
        {
            var wh = size.Split('x');
            GetWindow().Size = new Vector2I(int.Parse(wh[0]), int.Parse(wh[1]));
            await Frames(10);
            GD.Print($"SELFTEST SIZE window {GetWindow().Size} viewport {GetViewport().GetVisibleRect().Size}");
        }
        _root = new Control { Name = "Root", Theme = UiKit.Theme, MouseFilter = Control.MouseFilterEnum.Ignore };
        _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_root);

        var repoRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), ".."));
        var found = GameInstall.TryLocate(null, repoRoot);
        if (found is null) { GD.Print("SELFTEST FAIL install - no game install found; the squad self-test needs one"); GetTree().Quit(1); return; }
        _gameDir = found.Dir;
        var notes = new List<string>();
        _catalog = CustomizationCatalog.Load(_gameDir, notes, MarineAssets.Schemes(_gameDir, notes));
        var assets = AppearanceAssets.Shared(_gameDir, notes);
        _looks = new WarfrontSquadLooks(assets, _catalog);
        _feed = new WarfrontSquadDemoFeed();
        BuildStage(1280, 720);
        _dresser = new DemoDresser(_catalog, OwnerCode);
        Check("catalogue has the three classes the squad uses", new[] { "assault", "tactical", "heavy" }.All(r => WarfrontSquadLooks.ClassKeyFor(_catalog, null, r, false) is not null));

        // ---- one squad of nine in the owner's colours -------------------------------------------------------------------------------------
        await Fresh(9, 1, 18.2);
        Place(new Num.Vector3(0, -10f, 2.0f), new Num.Vector3(0, 6f, 1.4f));
        bool ready = await WaitModels(60);
        Check("nine Marines of one squad get real models", ready && _view.Crowd.Drawn == 9, $"drawn {_view.Crowd.Drawn}, with model {_view.Crowd.WithModel}, notes {string.Join("; ", _looks.Notes)}");
        Check("the squad shares one look build (one owner, one class)", _looks.Built == 1, $"{_looks.Built} builds");
        await Step(20);
        string? line = _view.Hud.Line;
        Check("HUD line shows the squad count", _view.Hud.Line == "SQUAD 9/9", _view.Hud.Line);
        Check("the roster names reached the Marines", _view.Model.TrySample(0, _now, out var m0) && m0.Name == "Cassian" && m0.Own, m0.Name);
        Check("no feed line before anything happened", _view.Hud.FeedText.Count == 0, string.Join("|", _view.Hud.FeedText));
        if (Flag("--squad-clips")) PrintClips();
        PrintCrowd("one squad");
        await Still("nine-marines-player-view");
        Place(new Num.Vector3(12f, -13f, 5f), new Num.Vector3(0, 2f, 1.2f));
        await Step(2);
        await Still("nine-marines-third-person");
        // The squad firing: wait for the standing-and-firing phase (the script's 6 to 12 s).
        await Step(40);
        Check("a firing Marine draws tracers toward an Ork", _view.Crowd.ShotsFired > 0, $"{_view.Crowd.ShotsFired} shots");

        if (Flag("--squad-quick")) { Finish(); return; }

        // ---- a downed Marine, the prompt and the ring -----------------------------------------------------------------------------------------
        await Fresh(9, 1, 2.0);
        await WaitModels(60);
        // Squad 0's eighth Marine is downed from 2.0 s and revived from 8.0 s over two seconds. Step virtual time to 9.0 s (the ring half full), the player beside it.
        while (_now < 3.2) await Step();
        Check("a downed Marine gives a feed line and the HUD turns to DOWN", _view.Hud.FeedText.Any(t => t.EndsWith("IS DOWN")) && _view.Hud.Line == "1 DOWN", $"{string.Join("|", _view.Hud.FeedText)} / {_view.Hud.Line}");
        _view.Model.TrySample(7, _now, out var downed);
        Check("the downed Marine counts its window", downed.Downed && downed.DownedLeft is > 16f and < 19.5f, $"{downed.DownedLeft}");
        while (_now < 9.0) { if (_view.Model.TrySample(7, _now, out var follow)) _player = follow.Position + new Num.Vector3(1.2f, 0.4f, 0f); await Step(); }
        Check("the revive prompt names the Marine and the ring has progress", _view.Hud.PromptShown && _view.Hud.Prompt.StartsWith("HOLD [E] TO REVIVE ") && _view.Hud.RingShown && _view.Hud.RingShare is > 0.3f and < 0.7f, $"'{_view.Hud.Prompt}' ring {_view.Hud.RingShown} {_view.Hud.RingShare}");
        _view.Model.TrySample(7, _now, out var nowDowned);
        var dp = nowDowned.Position;
        Place(new Num.Vector3(dp.X + 4.2f, dp.Y - 5.2f, 2.6f), new Num.Vector3(dp.X, dp.Y + 1.2f, 1.1f));
        await Step(2);
        await Still("downed-marine-prompt-and-ring");
        _player = new Num.Vector3(0, 0, 0);
        while (_now < 11.0) await Step();
        Check("the revive ends the prompt and the feed says who revived", !_view.Hud.PromptShown || _view.Hud.FeedText.Any(t => t.Contains("REVIVED BY You")), string.Join("|", _view.Hud.FeedText));
        Check("the revived Marine counts as standing again", _view.Hud.Line == "SQUAD 9/9", _view.Hud.Line);

        // ---- the panel aboard the ship ---------------------------------------------------------------------------------------------------
        _view.Aboard = true;
        _view.HandleKey(new InputEventKey { Pressed = true, PhysicalKeycode = WarfrontSquadView.PanelKey });
        await Step(6);
        Check("the panel key opens it aboard the ship", _view.Panel.Visible && _view.Panel.RowCount == 9, $"rows {_view.Panel.RowCount}");
        _view.Panel.BeginEdit(1, "Brother Castor the Brave");
        await Step(3);
        await Still("squad-panel-rename-under-way");
        _view.Panel.NameEdits[1].EmitSignal(LineEdit.SignalName.TextSubmitted, "Brother Castor");
        await Step(2);
        Check("Enter sends a Rename for slot 1", _commands.Count == 1 && SquadWire.TryDecodeCommand(_commands[0], out var cmd) && cmd.Kind == SquadCommandKind.Rename && cmd.Slot == 1 && cmd.Name == "Brother Castor", _commands.Count == 1 ? Convert.ToHexString(_commands[0]) : $"{_commands.Count} commands");
        _view.Panel.Close();
        Check("Escape or the close button hides it", !_view.Panel.Visible);
        _view.Aboard = false;
        _view.HandleKey(new InputEventKey { Pressed = true, PhysicalKeycode = WarfrontSquadView.PanelKey });
        Check("the panel does not open in the field", !_view.Panel.Visible);

        // ---- the jump-pound key (Assault or Raptor only) ---------------------------------------------------------------------------------
        _commands.Clear();
        _view.MarkPoint = () => new Num.Vector3(12, 20, 0);
        _view.HandleKey(new InputEventKey { Pressed = true, PhysicalKeycode = WarfrontSquadView.JumpKey });
        Check("a class without a jump pack sends no jump-pound", _commands.Count == 0);
        _view.OwnHasJumpPack = () => true;
        await Step(2);
        Check("an Assault sees the jump hint on the squad line", _view.Hud.JumpHint == "[J] JUMP ASSAULT", _view.Hud.JumpHint);
        _view.HandleKey(new InputEventKey { Pressed = true, PhysicalKeycode = WarfrontSquadView.JumpKey });
        Check("the jump key sends the mark", _commands.Count == 1 && SquadWire.TryDecodeCommand(_commands[0], out var jp) && jp.Kind == SquadCommandKind.JumpPound && MathF.Abs(jp.Point.X - 12f) < 0.1f && MathF.Abs(jp.Point.Y - 20f) < 0.1f);

        // ---- 36 Marines of four squads in four colour schemes ----------------------------------------------------------------------------
        await Fresh(9, 4, 19.0, 0.5f);
        Place(new Num.Vector3(0, -33f, 8f), new Num.Vector3(0, -6f, 1.0f));
        bool all = await WaitModels(120);
        Check("36 Marines of four squads all get real models", all && _view.Crowd.Drawn == 36, $"drawn {_view.Crowd.Drawn}, with model {_view.Crowd.WithModel}");
        Check("four owners make four looks (the Librarian's squad its own)", _looks.Built >= 4, $"{_looks.Built} builds");
        await Step(30);
        PrintCrowd("36 marines");
        await Still("36-marines-four-squads");
        Place(new Num.Vector3(-22f, -24f, 3.2f), new Num.Vector3(-3f, -17f, 1.5f));
        await Step(2);
        await Still("squad-four-owners-colours-closer");

        if (Flag("--squad-cost")) await Cost();
        Finish();
    }

    void PrintClips()
    {
        var dress = _dresser.Dress(default(WarfrontSquadShown) with { Class = 1, OwnPieces = true, OwnerPlayer = 1 });
        if (dress is null) return;
        var model = _looks.Assets.Build(AppearanceResolver.Resolve(dress.Code, dress.ClassKey, _catalog, _looks.Assets.ResolveOptions()));
        var clips = WarfrontSquadClips.Of(model);
        GD.Print($"SELFTEST NOTE clips: enter {clips.DownEnter}, idle {clips.Idle}, run {clips.Run}, aim {clips.Aim}, fire {clips.Fire}, melee {clips.Melee}, down {clips.Down}, getup {clips.GetUp}, air {clips.Air}");
        foreach (var key in new[] { "Incap", "Stun", "Down", "Incap", "Death", "GetUp", "Get_Up", "Revive", "Jump", "Fall", "Fire", "Focus", "SM_MEL_" })
            GD.Print($"SELFTEST NOTE clips containing '{key}': {string.Join(", ", model.Model.Rig.AnimationNames.Where(n => n.Contains(key, StringComparison.OrdinalIgnoreCase)).Take(14))}");
    }

    void PrintCrowd(string label)
    {
        var c = _view.Crowd;
        GD.Print($"SELFTEST NOTE crowd {label}: drawn {c.Drawn}, with model {c.WithModel}, full {c.Full}, far {c.Far}, shots {c.ShotsFired}, frame {c.FrameMs:F2} ms; looks built {_looks.Built}; notes {string.Join("; ", c.Notes.Concat(_looks.Notes).Distinct())}");
    }

    // ---- the cost of 36 characters --------------------------------------------------------------------------------------------------------

    sealed record Sample(double ProcessMs, double RenderCpuMs, double RenderGpuMs, double CrowdMs, double WallMs);

    async Task<List<Sample>> Measure(int warm, int frames)
    {
        var vp = GetViewport().GetViewportRid();
        RenderingServer.ViewportSetMeasureRenderTime(vp, true);
        for (int i = 0; i < warm; i++) await Step();
        var list = new List<Sample>(frames);
        double last = Time.GetTicksUsec() / 1000.0;
        for (int i = 0; i < frames; i++)
        {
            await Step();
            double nowMs = Time.GetTicksUsec() / 1000.0;
            list.Add(new Sample(Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000.0, RenderingServer.ViewportGetMeasuredRenderTimeCpu(vp), RenderingServer.ViewportGetMeasuredRenderTimeGpu(vp), _view.Crowd.FrameMs, nowMs - last));
            last = nowMs;
        }
        return list;
    }

    static double Median(IEnumerable<double> v) { var l = v.OrderBy(x => x).ToList(); return l.Count == 0 ? 0 : l[l.Count / 2]; }

    static string Summary(string label, List<List<Sample>> blocks)
    {
        // The machine is shared (other agents run builds and tests), so each block's median frame is taken and the blocks' medians and best are reported.
        double Med(Func<Sample, double> f) => Median(blocks.Select(b => Median(b.Select(f))));
        double Best(Func<Sample, double> f) => blocks.Min(b => Median(b.Select(f)));
        return $"SQUAD COST {label}: frame median {Med(x => x.WallMs):F2} ms (best block {Best(x => x.WallMs):F2}), render cpu {Med(x => x.RenderCpuMs):F2} ms, render gpu {Med(x => x.RenderGpuMs):F2} ms, crowd script {Med(x => x.CrowdMs):F2} ms ({blocks.Count} blocks of {blocks[0].Count} frames)";
    }

    async Task Cost()
    {
        GD.Print($"SQUAD COST window {GetWindow().Size}, viewport {GetViewport().GetVisibleRect().Size}, renderer {RenderingServer.GetCurrentRenderingDriverName()} / {RenderingServer.GetVideoAdapterName()}; the process monitor is not used (it averages startup spikes)");
        Place(new Num.Vector3(0, -34f, 12f), new Num.Vector3(0, -4f, 1.0f));
        await Step(30);
        const int Rounds = 7, Frames = 120;
        var none = new List<List<Sample>>(); var full = new List<List<Sample>>(); var bare = new List<List<Sample>>(); var frozen = new List<List<Sample>>();
        for (int r = 0; r < Rounds; r++)
        {
            _view.Crowd.Lod = false; _view.Crowd.Weapons = true;
            none.Add(await MeasureWithout(20, Frames));
            full.Add(await Measure(20, Frames));
            _view.Crowd.Weapons = false;
            bare.Add(await Measure(20, Frames));
            _view.Crowd.PoseFrozen = true;
            frozen.Add(await Measure(20, Frames));
            _view.Crowd.PoseFrozen = false;
        }
        GD.Print(Summary("0 marines (the crowd hidden and not updated)", none));
        GD.Print(Summary("36 marines, full detail, weapons, no LOD", full));
        GD.Print(Summary("36 marines, full detail, no weapons", bare));
        GD.Print(Summary("36 marines, poses frozen (drawing only)", frozen));
        double delta = Median(full.Select(b => Median(b.Select(x => x.WallMs)))) - Median(none.Select(b => Median(b.Select(x => x.WallMs))));
        double bestDelta = full.Min(b => Median(b.Select(x => x.WallMs))) - none.Min(b => Median(b.Select(x => x.WallMs)));
        GD.Print($"SQUAD COST extra frame time of 36 full characters: {delta:F2} ms (median of blocks), {bestDelta:F2} ms (best block against best block)");
        Place(new Num.Vector3(0, -110f, 40f), new Num.Vector3(0, -4f, 1.0f));
        var far = new List<List<Sample>>(); var farFull = new List<List<Sample>>();
        for (int r = 0; r < 4; r++)
        {
            _view.Crowd.Weapons = true; _view.Crowd.Lod = true; far.Add(await Measure(20, Frames));
            _view.Crowd.Lod = false; farFull.Add(await Measure(20, Frames));
        }
        GD.Print(Summary("36 marines seen from 110 m (all beyond 40 m), LOD on", far));
        GD.Print(Summary("36 marines seen from 110 m, LOD off", farFull));
        _view.Crowd.Lod = false;
    }

    /// <summary>The empty scene: the same frames with the crowd hidden and not updated (its nodes made invisible), so the stage costs what it costs.</summary>
    async Task<List<Sample>> MeasureWithout(int warm, int frames)
    {
        _view.Crowd.Visible = false;
        _view.Crowd.ProcessMode = ProcessModeEnum.Disabled;
        var vp = GetViewport().GetViewportRid();
        RenderingServer.ViewportSetMeasureRenderTime(vp, true);
        for (int i = 0; i < warm; i++) { _now += 1.0 / 30.0; await Frames(1); }
        var list = new List<Sample>(frames);
        double last = Time.GetTicksUsec() / 1000.0;
        for (int i = 0; i < frames; i++)
        {
            _now += 1.0 / 30.0;
            await Frames(1);
            double nowMs = Time.GetTicksUsec() / 1000.0;
            list.Add(new Sample(Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000.0, RenderingServer.ViewportGetMeasuredRenderTimeCpu(vp), RenderingServer.ViewportGetMeasuredRenderTimeGpu(vp), 0, nowMs - last));
            last = nowMs;
        }
        _view.Crowd.Visible = true;
        _view.Crowd.ProcessMode = ProcessModeEnum.Inherit;
        return list;
    }

    void Finish()
    {
        GD.Print($"SQUAD SELFTEST RESULT {(_failed == 0 ? "PASS" : "FAIL")}: {_passed} passed, {_failed} failed");
        GetTree().Quit(_failed == 0 ? 0 : 1);
    }
}
