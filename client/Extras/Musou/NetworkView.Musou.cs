using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmContent;
using SmNet;
using SmNet.Extras.Musou;
using SmSim;
using SmSim.Extras.Musou;

namespace SmClient;

/// <summary>
/// Added content "Musou" in the networked view (design section D), only when the match runs <see cref="ContentExtras.Musou"/>. The routed sections (tags 3 to 5,
/// from the dispatcher <c>NetworkView.Extras.cs</c>) go into the engine-free crowd state (<see cref="MusouClientState"/>), which is sampled at the view's render
/// clock (the remote players' interpolated tick); the crowd (<see cref="MusouCrowd"/>) draws the result, the dead play their death where they fell, with the
/// red splash of a body hit (<see cref="BloodSplashAt"/>) within 25 m. Test aids: <c>--musou-visible N</c> (most drawn, default 150), <c>--musou-no-vat</c>
/// (skinned only, the fallback). Every 10 s one log line: the frame times (mean fps, p50 and p99 ms) and what the crowd drew. A lean bot or a headless client
/// keeps the state and draws nothing (no HUD node either). MU3: the HUD, the hit feedback, the telegraphs and the voices (<c>MusouFx.cs</c>, <see cref="MusouHud"/>).
/// </summary>
public partial class NetworkView
{
    readonly MusouClientState _musouState = new();
    MusouLooks? _musouLooks;
    MusouCrowd? _musouCrowd;
    bool _musouNoDraw, _musouDrawChecked;
    double _musouStarted = -1, _musouLogAt;
    readonly List<float> _musouFrames = new();
    readonly List<MusouDeathShown> _musouDeaths = new();   // deaths not yet given a body (kept while the crowd is being made; at most MaxBodies)

    bool MusouActive => _config is { } c && c.Extras.HasFlag(ContentExtras.Musou);

    /// <summary>The latest director the server sent (null: none yet, or not a Musou match).</summary>
    public MusouDirectorView? MusouDirector => _musouState.Director;
    /// <summary>The crowd state (test aids and the HUD of MU3).</summary>
    internal MusouClientState MusouState => _musouState;

    /// <summary>This feature's part of the frame hook: <paramref name="sections"/> are this frame's sections with Musou's tags, oldest first.</summary>
    void MusouFrame(PlayerState me, IReadOnlyList<PlayerState> others, double delta, IReadOnlyList<ExtrasSection> sections)
    {
        if (!MusouActive || _client is not { } client) { if (_musouHud is not null) _musouHud.Visible = false; return; }
        double now = Time.GetTicksUsec() / 1e6;
        foreach (var section in sections) if (!MusouDropSection(section)) _musouState.Apply(section, now);   // MU-D: tag 6 to the drops (MusouDrops.cs)
        var (renderTick, _) = client.RemotePlayers();
        var shown = _musouState.Sample(renderTick > 0 ? renderTick : _musouState.NewestTick, now);

        if (!_musouDrawChecked)
        {
            _musouDrawChecked = true;
            _musouNoDraw = Lean || DisplayServer.GetName() == "headless";
            GD.Print($"musou: crowd {(_musouNoDraw ? "not drawn (headless or lean)" : "drawn")}");
        }
        if (!_musouNoDraw)
        {
            foreach (var d in _musouState.NewDeaths) _musouDeaths.Add(d);
            if (_musouDeaths.Count > MusouCrowd.MaxBodies) _musouDeaths.RemoveRange(0, _musouDeaths.Count - MusouCrowd.MaxBodies);
            if (_gameSpace is not null) MusouDraw(shown, (float)delta);
            MusouPresent(me, shown, (float)delta);   // MU3: the HUD, hit feedback, telegraphs and voices (MusouFx.cs)
        }
        _musouState.TakeNew();
        MusouDropsFrame(me, (float)delta);   // MU-D: the own weapon drops (MusouDrops.cs)
        MusouLog(delta, shown.Count);
    }

    void MusouDraw(IReadOnlyList<MusouShown> shown, float delta)
    {
        if (_musouStarted < 0) _musouStarted = _connectedFor;
        if (_musouLooks is null)
        {
            if (Orks() is not { } orks) return;
            _musouLooks = new MusouLooks(_gameDir, orks, MusouChaosLook);
            _musouCrowd = new MusouCrowd(_musouLooks) { Visible = MusouArg("musou-visible") ?? MusouCrowd.DefaultVisible, NoVat = CmdFlag("musou-no-vat") };
            _gameSpace!.AddChild(_musouCrowd);
        }
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 8 && _musouLooks.Step() is { } built) GD.Print($"musou: look {built.Name} ready ({OrkAssets.Describe(built.Assets)})");
        if (_connectedFor - _musouStarted > 15) _musouLooks.StopWaiting();
        foreach (var n in _musouLooks.Notes) { GD.Print(n); _notes.Add(n); }
        _musouLooks.Notes.Clear();

        var camera = _camera is null ? Vector3.Zero : new Vector3(_camera.GlobalPosition.X, -_camera.GlobalPosition.Z, _camera.GlobalPosition.Y);   // Godot to game space
        foreach (var d in _musouDeaths)
        {
            _musouCrowd!.AddBody(d);
            var at = new Vector3(d.Position.X, d.Position.Y, d.Position.Z + 1.1f);
            if (at.DistanceTo(camera) <= MusouCrowd.NearMetres)
                BloodSplashAt(_gameSpace!.Transform * at, -(_gameSpace.Transform.Basis * new Vector3(d.Push.X, d.Push.Y, d.Push.Z)), 0.0);
        }
        _musouDeaths.Clear();
        _musouCrowd!.Frame(shown, camera, delta);
    }

    /// <summary>The Chaos Marine's model: the default look of the first Chaos class, once the view has built it (the warm-up builds every class's default).</summary>
    MarineAssets? MusouChaosLook()
    {
        if (_config is null) return null;
        for (int i = 0; i < _config.Classes.Count; i++)
        {
            if (_config.Classes[i].Side != Team.Chaos) continue;
            var state = Loadouts.Choose(_config, new PlayerState(0, default, default, 0f, true) { Class = i, Team = Team.Chaos }, -1, -1, -1);
            if (LookFor(state, AppearanceCode.Default) is { } look && _builtLooks.TryGetValue(look.Key, out var built)) return built;
        }
        return null;
    }

    static int? MusouArg(string name)
    {
        var args = OS.GetCmdlineUserArgs();
        int at = Array.IndexOf(args, "--" + name);
        return at >= 0 && at + 1 < args.Length && int.TryParse(args[at + 1], out int v) ? Math.Clamp(v, 0, 400) : null;
    }

    void MusouLog(double delta, int known)
    {
        _musouFrames.Add((float)(delta * 1000.0));
        if (_connectedFor < _musouLogAt) return;
        _musouLogAt = _connectedFor + 10.0;
        if (_musouFrames.Count == 0) return;
        var sorted = _musouFrames.OrderBy(f => f).ToArray();
        float P(double p) => sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];
        double fps = sorted.Length / (sorted.Sum() / 1000.0);
        var c = _musouCrowd;
        GD.Print($"musou: t {_connectedFor:F0} s: {sorted.Length} frames, {fps:F1} fps, frame p50 {P(0.5):F1} ms p99 {P(0.99):F1} ms max {sorted[^1]:F1} ms; " +
                 $"enemies known {known}, drawn {(c is null ? "-" : $"{c.ShownSkinned} skinned + {c.ShownVat} vat, {c.LeftOut} left out, {c.Bodies} bodies")}; " +
                 $"sections {_musouState.SectionsRead} ({_musouState.SectionsMalformed} malformed), events {_musouState.EventsPlayed} (+{_musouState.EventCopies} copies), " +
                 $"silent drops {_musouState.DroppedSilent}; {MusouPresentNote()}; director {(_musouState.Director is { } d ? $"wave {d.Wave}/{d.Waves} {d.Phase}" : "none")}");
        _musouFrames.Clear();
    }
}
