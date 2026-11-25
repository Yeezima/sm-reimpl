using System;
using System.Collections.Generic;
using Godot;
using SmClient.Extras.Warfront.Chaos.Breach;
using SmNet.Extras.Warfront;
using SmSim.Extras.Warfront.Chaos;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// The warp breaches on the client (Chaos Warfront C4c; tag 51, <see cref="WarfrontChaosWire"/>, sent only by a Warfront stage whose army is Chaos, so nothing here runs anywhere else). The envelope's one
/// forward hands every tag 51 section to <see cref="WarfrontChaosSection"/>: kinds 1 and 2 go into the <see cref="ChaosBreachModel"/>, kinds 16 to 31 to C5's <see cref="WarfrontChaosDuelSection"/> (a no-op
/// until C5c). Each breach is drawn with the install's <see cref="ChaosBreachLook.Effect"/> looping, hums with the install's <see cref="ChaosBreachLook.HumEvent"/> and crackles with
/// <see cref="ChaosBreachLook.ArcsEvent"/> at random 2 to 5 s while it stands, and carries a marker (<c>WARP BREACH  52 m</c>, red-violet, seen through walls like the battle's letters). A closure (the first
/// copy of each) plays the effect's flash, an implosion and a shockwave ring (<see cref="BreachClosureFx"/>, ours) and the install's portal explosion, and the marker turns grey (<c>BREACH CLOSED</c>) for 3 s.
/// No camera shake: the only client shake seam (<c>ExtrasCameraShake</c>) is the Dreadnought's. The frame runs on a small node under the game space (<see cref="ChaosBreachDriver"/>), so no other
/// Warfront file carries a line for it; a bot or a headless client keeps the model and the log lines and draws nothing.
/// </summary>
public partial class NetworkView
{
    /// <summary>C5c's finale sections (tag 51 kinds 16 to 31): a no-op until it lands.</summary>
    partial void WarfrontChaosDuelSection(byte tag, ReadOnlySpan<byte> payload, ref bool handled);

    readonly ChaosBreachModel _wfBreach = new();
    ChaosBreachDriver? _wfBreachDriver;
    readonly Dictionary<byte, BreachShow> _wfBreachShows = new();
    readonly Random _wfBreachRoll = new(0x51);
    string _wfBreachLogged = "";

    /// <summary>The breach model, for the tests and the capture aid.</summary>
    internal ChaosBreachModel WarfrontBreachModel => _wfBreach;

    /// <summary>One breach on the screen: its looping effect, hum, next crackle and marker.</summary>
    sealed class BreachShow
    {
        public FxEffect? Loop;
        public SoundEmitter? Hum;
        public bool Humming;
        public double NextArc;
        public Label3D? Marker;
    }

    /// <summary>Tag 51 (the envelope's forward): the breaches' kinds here, the finale's to C5.</summary>
    partial void WarfrontChaosSection(byte tag, ReadOnlySpan<byte> payload, ref bool handled)
    {
        if (tag != WarfrontChaosWire.Tag) return;
        handled = true;
        switch (ChaosBreachModel.Route(payload))
        {
            case ChaosSectionRoute.Duel:
                WarfrontChaosDuelSection(tag, payload, ref handled);
                return;
            case ChaosSectionRoute.Refused:
                WarfrontDroppedChaos++;
                return;
        }
        double now = _connectedFor;
        if (_wfBreach.Tick(now)) WarfrontBreachDropShows();   // a list that went stale while no frame ran (the old map's driver went with it)
        if (!_wfBreach.Apply(payload, now, out _)) { WarfrontDroppedChaos++; return; }
        WarfrontBreachLog(now);
        WarfrontBreachEnsureDriver();
        foreach (var c in _wfBreach.NewClosures)
        {
            string who = c.Tick == 0 && c.Closer == 0 ? "(the list's change; its closure section was lost)" : $"by P{c.Closer} (tick {c.Tick})";
            GD.Print($"t {now:F1} s: warfront chaos: breach {c.Breach} closed {who}{(c.Centre is { } at ? $" at {at.X:F0},{at.Y:F0},{at.Z:F0}" : ", not in the list")}");
            WarfrontBreachClosure(c);
        }
    }

    /// <summary>Tag 51 sections refused (malformed or an unknown kind).</summary>
    public int WarfrontDroppedChaos { get; private set; }

    /// <summary>One line whenever the list's states or Daemon counts change.</summary>
    void WarfrontBreachLog(double now)
    {
        var parts = new List<string>();
        foreach (var b in _wfBreach.Breaches) parts.Add($"{b.Id} {b.State} {b.Alive} at {b.Position.X:F0},{b.Position.Y:F0},{b.Position.Z:F0}");
        string line = parts.Count == 0 ? "none" : string.Join("; ", parts);
        if (line == _wfBreachLogged) return;
        _wfBreachLogged = line;
        GD.Print($"t {now:F1} s: warfront chaos: breaches {line}");
    }

    bool WarfrontBreachDraws => !Lean && !_bot && _gameSpace is not null && DisplayServer.GetName() != "headless";

    /// <summary>The frame node under the game space (made with the first section of a map; it goes with the map).</summary>
    void WarfrontBreachEnsureDriver()
    {
        if (_gameSpace is null) return;
        if (_wfBreachDriver is { } d && IsInstanceValid(d) && d.IsInsideTree()) return;
        WarfrontBreachDropShows();   // the old map's nodes went with it
        _wfBreachDriver = new ChaosBreachDriver { Name = "WarfrontChaosBreaches", Step = WarfrontBreachFrame, Gone = WarfrontBreachGone };
        _gameSpace.AddChild(_wfBreachDriver);
    }

    System.Threading.Tasks.Task<(Dictionary<string, CSystem> Systems, Dictionary<string, byte[]> Decals, List<string> Failed)>? _wfBreachFxLoad;
    FxPlayer? _wfBreachFxFor;
    bool _wfBreachFxReady, _wfBreachNoFxLogged;

    /// <summary>The breach's effects are in no catalogue the client preloads: compiled once per effect player on a worker from master_fx (as Musou's Mek effects are).</summary>
    void WarfrontBreachFxLoad()
    {
        if (_fx is not { IsReady: true, Library: { } library } fx) return;
        if (!ReferenceEquals(_wfBreachFxFor, fx)) { _wfBreachFxFor = fx; _wfBreachFxReady = false; _wfBreachFxLoad = null; }
        if (_wfBreachFxReady) return;
        _wfBreachFxLoad ??= System.Threading.Tasks.Task.Run(() =>
        {
            var failures = new List<string>();
            var systems = CompileSystems(library, [ChaosBreachLook.Effect, ChaosBreachLook.FallbackEffect], failures);
            return (new Dictionary<string, CSystem>(systems), new Dictionary<string, byte[]>(library.Textures(FxPlayer.DecalNames(systems.Values))), failures);
        });
        if (!_wfBreachFxLoad.IsCompleted) return;
        _wfBreachFxReady = true;
        if (_wfBreachFxLoad.IsFaulted) { GD.PrintErr("warfront chaos: breach effects: " + _wfBreachFxLoad.Exception?.GetBaseException().Message); return; }
        var (systems, decals, failed) = _wfBreachFxLoad.Result;
        fx.Preload(systems, decals);
        GD.Print($"warfront chaos: {systems.Count} breach effect(s) compiled ({string.Join(", ", systems.Keys)}){(failed.Count > 0 ? "; failed: " + string.Join(", ", failed) : "")}");
    }

    /// <summary>Once a frame while a breach section has come this map.</summary>
    void WarfrontBreachFrame(double dt)
    {
        double now = _connectedFor;
        if (_wfBreach.Tick(now)) { GD.Print($"t {now:F1} s: warfront chaos: no breach list for {ChaosBreachLook.StaleSeconds:F0} s; breaches cleared"); _wfBreachLogged = ""; }
        WarfrontBreachDemoFrame(now);
        if (!WarfrontBreachDraws) return;
        WarfrontBreachFxLoad();
        var viewer = _client?.Predicted.Position ?? default;
        var live = new HashSet<byte>();
        foreach (var b in _wfBreach.Breaches)
        {
            live.Add(b.Id);
            if (!_wfBreachShows.TryGetValue(b.Id, out var show)) _wfBreachShows[b.Id] = show = new BreachShow { NextArc = now + ChaosBreachLook.ArcsDelay(_wfBreachRoll.NextDouble()) };
            WarfrontBreachShow(b, show, now, viewer);
        }
        var gone = new List<byte>();
        foreach (var id in _wfBreachShows.Keys) if (!live.Contains(id)) gone.Add(id);
        foreach (var id in gone) { WarfrontBreachStop(_wfBreachShows[id], true); _wfBreachShows.Remove(id); }
    }

    void WarfrontBreachShow(in WarfrontChaosWire.Breach b, BreachShow show, double now, Num.Vector3 viewer)
    {
        bool standing = _wfBreach.Standing(b);
        var foot = b.Position;
        var centre = b.Centre;
        // the install's effect, looping while the breach stands
        if (standing && show.Loop is null && _fx is not null && FxActive && _wfBreachFxReady)
        {
            var at = ToGodot(foot + new Num.Vector3(0f, 0f, ChaosBreachLook.EffectHeight));
            show.Loop = _fx.Play(ChaosBreachLook.Effect, at, Basis.Identity, 1f, repeat: true, priority: 2)
                        ?? _fx.Play(ChaosBreachLook.FallbackEffect, at, Basis.Identity, ChaosBreachLook.FallbackScale, repeat: true, priority: 2);
            if (show.Loop is not null) GD.Print($"t {now:F1} s: warfront chaos: breach {b.Id} effect {show.Loop.Name}");
            else if (!_wfBreachNoFxLogged) { _wfBreachNoFxLogged = true; GD.Print($"t {now:F1} s: warfront chaos: breach {b.Id} effect none (neither the portal nor the gate in the effect library)"); }
        }
        if (!standing && show.Loop is { } loop) { loop.Repeat = false; loop.Life = Math.Min(loop.Life, loop.Age + 0.25); show.Loop = null; }
        // the hum while it is open; the crackle at random
        if (_sound is not null)
        {
            bool hum = standing && b.State == WarfrontBreachState.Open;
            if (hum && !show.Humming)
            {
                var g = ToGodot(centre);
                show.Hum ??= new SoundEmitter { Label = $"warp breach {b.Id}", Position = g };
                show.Hum.Position = g;
                var play = _sound.Post(ChaosBreachLook.HumEvent, show.Hum);
                show.Humming = true;
                if (play is { Found: false }) GD.Print($"t {now:F1} s: warfront chaos: the hum event {ChaosBreachLook.HumEvent} is not in the loaded banks");
            }
            else if (!hum && show.Humming) { if (show.Hum is not null) _sound.StopEmitter(show.Hum); show.Humming = false; }
            if (hum && now >= show.NextArc)
            {
                show.NextArc = now + ChaosBreachLook.ArcsDelay(_wfBreachRoll.NextDouble());
                _sound.Post(ChaosBreachLook.ArcsEvent, new SoundEmitter { Position = ToGodot(centre), OneShot = true, Label = $"warp breach {b.Id} arcs" });
            }
        }
        // the marker over the centre
        var kind = _wfBreach.Marker(b, now);
        if (show.Marker is null && _wfBreachDriver is { } driver)
        {
            show.Marker = new Label3D
            {
                Name = $"BreachMarker{b.Id}", Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, NoDepthTest = true, FixedSize = true, PixelSize = 0.0012f, FontSize = 30, OutlineSize = 10,
                OutlineModulate = new Color(0f, 0f, 0f, 0.9f), RenderPriority = 10, TextureFilter = BaseMaterial3D.TextureFilterEnum.Linear,
            };
            driver.AddChild(show.Marker);
        }
        if (show.Marker is { } m)
        {
            m.Visible = kind != ChaosBreachMarkerKind.Hidden;
            m.Position = new Vector3(centre.X, centre.Y, centre.Z + ChaosBreachLook.MarkerHeight);
            if (kind == ChaosBreachMarkerKind.Open)
            {
                m.Text = ChaosBreachLook.OpenLine(Num.Vector3.Distance(viewer, centre));
                m.Modulate = new Color(ChaosBreachLook.MarkerColour.R, ChaosBreachLook.MarkerColour.G, ChaosBreachLook.MarkerColour.B);
            }
            else if (kind == ChaosBreachMarkerKind.Closed)
            {
                m.Text = ChaosBreachLook.ClosedText;
                m.Modulate = new Color(ChaosBreachLook.ClosedColour.R, ChaosBreachLook.ClosedColour.G, ChaosBreachLook.ClosedColour.B);
            }
        }
    }

    /// <summary>A closure's show (its first copy): the effect's flash, the implosion and ring, the explosion sound. The marker follows from the model.</summary>
    void WarfrontBreachClosure(ChaosBreachClosureShown c)
    {
        if (c.Centre is not { } centre) return;
        WarfrontBreachEnsureDriver();
        if (!WarfrontBreachDraws) return;
        var foot = centre - new Num.Vector3(0f, 0f, WarfrontBreachRules.CentreHeight);
        if (_fx is not null && FxActive)
            _fx.Play(ChaosBreachLook.Effect, ToGodot(foot + new Num.Vector3(0f, 0f, ChaosBreachLook.EffectHeight)), Basis.Identity, 1f, lifetime: ChaosBreachLook.ClosureFlashFrom + ChaosBreachLook.ClosureFlashLife,
                     delay: -ChaosBreachLook.ClosureFlashFrom, priority: 3);
        if (_wfBreachDriver is { } driver) driver.AddChild(new BreachClosureFx { Position = new Vector3(centre.X, centre.Y, centre.Z) });
        _sound?.Post(ChaosBreachLook.ClosureEvent, new SoundEmitter { Position = ToGodot(centre), OneShot = true, Label = $"warp breach {c.Breach} closed" });
        if (_wfBreachShows.TryGetValue(c.Breach, out var show)) WarfrontBreachStop(show, false);
    }

    /// <summary>Stops a breach's loop and hum (<paramref name="marker"/>: its marker goes too).</summary>
    void WarfrontBreachStop(BreachShow show, bool marker)
    {
        if (show.Loop is { } loop) { loop.Repeat = false; loop.Life = Math.Min(loop.Life, loop.Age + 0.25); show.Loop = null; }
        if (show.Humming && show.Hum is not null) _sound?.StopEmitter(show.Hum);
        show.Humming = false;
        if (marker && show.Marker is { } m && IsInstanceValid(m)) m.QueueFree();
        if (marker) show.Marker = null;
    }

    /// <summary>The frame node went with its map (a stage change, a leave): no hum or marker outlives it, and an end-of-battle list that never came is not waited for.</summary>
    void WarfrontBreachGone()
    {
        WarfrontBreachDemoGone();
        WarfrontBreachDropShows();
        if (_wfBreach.Breaches.Count > 0) GD.Print($"t {_connectedFor:F1} s: warfront chaos: the map went; breaches cleared");
        _wfBreach.Clear();
        _wfBreachLogged = "";
    }

    void WarfrontBreachDropShows()
    {
        foreach (var s in _wfBreachShows.Values) WarfrontBreachStop(s, true);
        _wfBreachShows.Clear();
    }
}

/// <summary>The breaches' frame on the client: a node under the game space that calls <see cref="Step"/> once a frame and goes with the map (Chaos Warfront C4c).</summary>
public sealed partial class ChaosBreachDriver : Node3D
{
    public Action<double>? Step;
    /// <summary>Called when the node leaves the tree (the map or the view went): the breaches' sounds stop and the model forgets the battle.</summary>
    public Action? Gone;
    public override void _Process(double delta) => Step?.Invoke(delta);
    public override void _ExitTree() => Gone?.Invoke();
}
