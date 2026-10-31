using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SmContent.Extras.Musou;
using SmFormats.Fx;
using SmNet;
using SmNet.Extras.Musou.Campaign;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// The Guardsmen in the networked view (TF7a): this frame's sections go into <see cref="TfwClientState"/>, sampled at the render tick for <see cref="GuardCrowd"/>; each shot plays the wargear's
/// <c>lasgun_muzzle</c> and <c>lasgun_tracer</c> from the man's muzzle. A lean bot or a headless client keeps the state and draws nothing. Test aids: <c>--guard-demo N</c> (<see cref="GuardDemoFeed"/>,
/// at <c>--guard-demo-at x,y,z</c>, default the arena's centre), <c>--guard-visible N</c> (most drawn, default 120; the Compatibility renderer 60), <c>--guard-no-vat</c> (skinned only).
/// </summary>
public partial class NetworkView
{
    /// <summary>Metres from the camera within which a shot plays its muzzle flash, and its tracer (ours: a crowd's fire beyond that is not worth its effects).</summary>
    const float GuardMuzzleMetres = 45f, GuardTracerMetres = 90f;
    /// <summary>Shots given effects in one frame (ours: <see cref="FxPlayer"/> keeps at most 160 effects).</summary>
    const int GuardFxPerFrame = 12;

    readonly TfwClientState _tfwState = new();
    GuardLooks? _guardLooks;
    GuardCrowd? _guardCrowd;
    GuardDemoFeed? _guardDemo;
    Task<(Dictionary<string, CSystem> Systems, List<string> Failures)>? _guardFxLoad;
    bool _guardFxReady, _guardDemoChecked, _tfwDeployChecked;
    TfwLane? _tfwDeployLane;
    double _guardLogAt, _guardFrameMs, _tfwDeployNext;
    int _guardFrames, _guardFxPlayed, _guardFxSkipped, _guardHurts, _guardDeaths;

    /// <summary>The war as this client knows it (the HUD of TF7b reads it too).</summary>
    internal TfwClientState TfwState => _tfwState;

    /// <summary>This feature's part of the war's frame.</summary>
    void TfwGuardsFrame(IReadOnlyList<ExtrasSection> sections, double delta)
    {
        double now = Time.GetTicksUsec() / 1e6;
        MusouBox? box = _musouState.Director is { } director ? director.Box.Sane() : null;
        foreach (var section in sections) _tfwState.Apply(section, box, now);
        TfwCommandsFrame(now);
        double renderTick = _client!.RemotePlayers().Tick;
        if (renderTick <= 0) renderTick = _tfwState.NewestTick;
        if (GuardDemo() is { } demo)
        {
            foreach (var section in demo.Step(delta)) _tfwState.Apply(section, demo.Box, now);
            renderTick = demo.RenderTick;
        }
        var shown = _tfwState.Sample(renderTick, now);
        if (!_musouNoDraw && _gameSpace is not null && (shown.Count > 0 || _guardLooks is not null))
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            GuardsDraw(shown, (uint)renderTick, (float)delta);
            _guardFrameMs += watch.Elapsed.TotalMilliseconds;
            _guardFrames++;
        }
        _tfwState.TakeNew();
    }

    /// <summary>Sends what <see cref="TfwCommandSender"/> has due. Test aid <c>--tfw-deploy a|b|c</c> (until TF7b's war map): a Deploy to that lane each time the run is back in the hub.</summary>
    void TfwCommandsFrame(double now)
    {
        if (_tfwDeployLane is null && !_tfwDeployChecked)
        {
            _tfwDeployChecked = true;
            var args = OS.GetCmdlineUserArgs();
            int at = Array.IndexOf(args, "--tfw-deploy");
            if (at >= 0 && at + 1 < args.Length && Enum.TryParse<TfwLane>(args[at + 1], ignoreCase: true, out var lane)) _tfwDeployLane = lane;
        }
        if (_tfwDeployLane is { } wanted && _tfwState.War is { Phase: TfwRunPhase.Hub } && _tfwState.Commands.Pending == 0 && now >= _tfwDeployNext)
        {
            _tfwDeployNext = now + 5.0;
            _tfwState.Commands.Issue(TfwCommand.Deploy, (byte)wanted);
            GD.Print($"guards: --tfw-deploy: asking for lane {wanted}");
        }
        if (_tfwState.Commands.Due(now) is { } payload) _client!.SendExtrasCommand(TfwWire.CommandTag, payload);
    }

    GuardDemoFeed? GuardDemo()
    {
        if (_guardDemoChecked) return _guardDemo;
        _guardDemoChecked = true;
        if (MusouArg("guard-demo") is not { } men) return null;
        var at = TfwMapCentreArg();
        _guardDemo = new GuardDemoFeed(men, at);
        GD.Print($"guards: scripted feed of {men} men at ({at.X:F0} {at.Y:F0} {at.Z:F0}) (--guard-demo)");
        return _guardDemo;
    }

    static Num.Vector3 TfwMapCentreArg()
    {
        var args = OS.GetCmdlineUserArgs();
        int i = Array.IndexOf(args, "--guard-demo-at");
        if (i >= 0 && i + 1 < args.Length)
        {
            var p = args[i + 1].Split(',');
            if (p.Length == 3 && float.TryParse(p[0], System.Globalization.CultureInfo.InvariantCulture, out float x) && float.TryParse(p[1], System.Globalization.CultureInfo.InvariantCulture, out float y)
                && float.TryParse(p[2], System.Globalization.CultureInfo.InvariantCulture, out float z)) return new Num.Vector3(x, y, z);
        }
        return Num.Vector3.Zero;
    }

    void GuardsDraw(IReadOnlyList<TfwGuardShown> shown, uint tick, float delta)
    {
        if (_guardLooks is null)
        {
            _guardLooks = new GuardLooks(_gameDir);
            int visible = MusouArg("guard-visible") ?? (SafeMode.Active ? GuardCrowd.LowVisible : GuardCrowd.DefaultVisible);
            _guardCrowd = new GuardCrowd(_guardLooks, visible) { NoVat = CmdFlag("guard-no-vat") };
            _gameSpace!.AddChild(_guardCrowd);
        }
        if (_guardLooks.Step() is { } look) GD.Print($"guards: look ready ({OrkAssets.Describe(look.Assets)}; muzzle at {_guardLooks.Muzzle})");
        foreach (var n in _guardLooks.Notes) { GD.Print(n); _notes.Add(n); }
        _guardLooks.Notes.Clear();
        GuardFxLoad();

        var camera = _camera is null ? Vector3.Zero : new Vector3(_camera.GlobalPosition.X, -_camera.GlobalPosition.Z, _camera.GlobalPosition.Y);   // Godot to game space
        foreach (var e in _tfwState.NewEvents)
        {
            _guardCrowd!.Event(e);
            if (e.Kind == TfwGuardEventKind.Hurt) _guardHurts++;
            else if (e.Kind == TfwGuardEventKind.Death) _guardDeaths++;
        }
        int played = 0;
        foreach (var e in _tfwState.NewEvents)
            if (e.Kind == TfwGuardEventKind.Shot) { if (played < GuardFxPerFrame && GuardShotFx(e, camera)) played++; }
        _guardCrowd!.Frame(shown, tick, camera, delta);
        GuardsLog();
    }

    /// <summary>The two effects are not in the marine weapons' set the view preloads: compiled once on a worker when the effects are up.</summary>
    void GuardFxLoad()
    {
        if (_guardFxReady || _fx is not { IsReady: true, Library: { } library }) return;
        _guardFxLoad ??= Task.Run(() =>
        {
            var failures = new List<string>();
            return (CompileSystems(library, [GuardRig.TracerEffect, GuardRig.MuzzleEffect], failures), failures);
        });
        if (!_guardFxLoad.IsCompleted) return;
        _guardFxReady = true;
        if (_guardFxLoad.IsFaulted) { GD.PrintErr("guards: effects: " + _guardFxLoad.Exception?.GetBaseException().Message); return; }
        var (systems, failed) = _guardFxLoad.Result;
        _fx.Preload(systems, new Dictionary<string, byte[]>());
        GD.Print($"guards: {systems.Count} effects compiled{(failed.Count > 0 ? "; failed: " + string.Join(", ", failed) : "")}");
    }

    /// <summary>A shot's muzzle flash and tracer; false when nothing was played.</summary>
    bool GuardShotFx(in TfwGuardEvent e, Vector3 camera)
    {
        if (!_guardFxReady || _fx is not { IsReady: true } fx || !FxActive || _guardCrowd?.MuzzleOf(e.Guard) is not { } muzzle) return false;
        float distance = muzzle.DistanceTo(camera);
        if (distance > GuardTracerMetres) { _guardFxSkipped++; return false; }
        var from = _gameSpace!.Transform * muzzle;
        var end = ToGodot(e.Vector);
        var line = end - from;
        float length = line.Length();
        if (length < 0.4f) return false;
        var axes = AxesFromY(line / length);
        if (distance <= GuardMuzzleMetres) fx.Play(GuardRig.MuzzleEffect, from, axes, GuardRig.MuzzleEffectScale, priority: 0, smokeRein: SmokeRein);
        var tracer = fx.Play(GuardRig.TracerEffect, from, axes, 1f, target: end, priority: 1, smokeRein: SmokeRein);
        if (tracer is not null && TracerHeadSpeed(GuardRig.TracerEffect) is > 1f and var head) tracer.HeadSpeed = MathF.Max(head, length / MaxTracerFlight);
        _guardFxPlayed++;
        return true;
    }

    void GuardsLog()
    {
        if (_connectedFor < _guardLogAt || _guardFrames == 0) return;
        _guardLogAt = _connectedFor + 10.0;
        var c = _guardCrowd!;
        GD.Print($"guards: t {_connectedFor:F0} s: war {(_tfwState.War is { } w ? $"{w.Phase} lane {w.BattleLane}" : "none")}, known {_tfwState.GuardCount}, drawn {c.ShownSkinned} skinned + {c.ShownVat} vat, {c.LeftOut} left out, {c.Bodies} bodies; frame cost {_guardFrameMs / _guardFrames:F2} ms; " +
                 $"sections {_tfwState.SectionsRead} ({_tfwState.SectionsMalformed} malformed), events {_tfwState.EventsPlayed} (+{_tfwState.EventCopies} copies), {_guardHurts} hurt, {_guardDeaths} dead, shot effects {_guardFxPlayed} ({_guardFxSkipped} too far)");
        _guardFrameMs = 0; _guardFrames = 0;
    }
}
