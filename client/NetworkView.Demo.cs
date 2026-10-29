using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Godot;
using SmContent;
using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// Time-faithful capture (M40 T0; the M40 audit, section 2). Godot's Movie Maker advances Godot time by exactly 1/30 s per frame, while
/// the network clock (<see cref="SystemClock"/>) follows the wall clock, so footage of a live match ran at 0.56x to 20x and turned every hitch
/// into a jump. A demo fixes that: a real-time client records what it received and sampled, and a replay drives the client's clocks with
/// Godot time, so footage at any size and frame rate is exactly real time and as smooth as the engine can draw it.
/// <list type="bullet">
/// <item><c>--record-demo &lt;file&gt;</c> (with <c>--connect</c>, a human or <c>--bot</c> client, headless or windowed): writes every packet the client
/// received with its arrival time, every input it sampled per tick and its view per frame (<see cref="Demo"/>; relative names go under the
/// private data folder, never into the repository or the game install). <c>--record-seconds &lt;s&gt;</c> stops recording that long after the first
/// packet (the client keeps playing); a client killed hard loses at most the last two seconds.</item>
/// <item><c>--play-demo &lt;file&gt;</c> (no server needed): runs a client on the recorded packets, inputs and view with <see cref="DemoPlayback"/>:
/// the client's clock advances by Godot's frame time and starts when the background loads are done (a few "pre-roll" frames, reported), the
/// prediction and the reconciliation replay as recorded, the view is the recorded one. Everything the view draws (effects, sound, weapons, paint)
/// works as in a live client, and camera options (<c>--spectate</c>, <c>--no-hud</c> ...) apply to the replay. The run ends and quits when the
/// recorded time has been played (<c>--demo-no-quit</c> keeps it open) and prints a DEMO-REPLAY line: frames, Godot time, the match clock, the
/// server clock advance and the input misses. With Movie Maker: <c>movie.cmd --movie-size 1920x1080 --write-movie x.avi --fixed-fps 30 -- --play-demo f</c>.</item>
/// <item><c>--motion-probe</c> (live or replay): the per-frame displacement of the drawn own marine and of each other player while they run
/// steadily (speed within 3 % of the commonest speed above 3 m/s): coefficient of variation of step / frame time, printed every 10 s and at the end.</item>
/// <item><c>--old-render-clock</c>: remote players at the old per-snapshot clock (M40 T2 before / after numbers).</item>
/// </list>
/// **Client capture aid, ours.** The recorded stream is the original-protocol stream of this client; the replay is deterministic up to the frame
/// quantization of packet delivery (a tick or two), which the engine-free tests bound.
/// </summary>
public partial class NetworkView
{
    static string? CmdArg(string name)
    {
        var args = OS.GetCmdlineUserArgs();
        int i = Array.IndexOf(args, "--" + name);
        return i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[i + 1] : null;
    }

    static bool CmdFlag(string name) => OS.GetCmdlineUserArgs().Contains("--" + name);

    // The demo flags, read once in _Ready (DemoReadArgs), not on every frame.
    string? DemoPlayPath { get; set; }
    string? DemoRecordPath { get; set; }
    double? DemoRecordSeconds { get; set; }
    bool OldRenderClock { get; set; }
    bool MotionProbe { get; set; }
    bool _motionProbeWorst, _demoNoQuit;

    /// <summary>Reads the demo flags from the command line once (first thing in <c>_Ready</c>).</summary>
    void DemoReadArgs()
    {
        DemoPlayPath = CmdArg("play-demo");
        DemoRecordPath = CmdArg("record-demo");
        DemoRecordSeconds = CmdArg("record-seconds") is { } s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : null;
        OldRenderClock = CmdFlag("old-render-clock");
        MotionProbe = CmdFlag("motion-probe");
        _motionProbeWorst = CmdFlag("motion-probe-worst");
        _demoNoQuit = CmdFlag("demo-no-quit");
    }

    DemoPlayback? _demoPlayback;
    Demo.Writer? _demoWriter;
    RecordingTransport? _demoRecorder;
    IClock? _demoClock;
    bool _demoSessionWritten, _demoStarted, _demoReported;
    int _demoFrames, _demoPrerollFrames;
    long _demoWaitStart;
    double _demoFlushAt;
    double _demoStartRender = double.NaN, _demoEndRender, _demoStartClock = double.NaN, _demoEndClock, _demoStartHud = double.NaN, _demoEndHud;
    double _demoStartElapsed, _demoEndElapsed, _demoStartHudElapsed, _demoEndHudElapsed;

    /// <summary>Reads a demo file (size-capped by <see cref="Demo.Read(Stream)"/>), also while a recorder still has it open.</summary>
    static Demo ReadDemoFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, System.IO.FileAccess.Read, FileShare.ReadWrite);
        return Demo.Read(stream);
    }

    /// <summary>
    /// <paramref name="path"/> with every link on the way resolved (junctions, symbolic links), component by component, so a link inside the
    /// private data folder cannot lead a recording into the repository or the install. A part that does not exist yet is kept as it is.
    /// </summary>
    static string ResolveReal(string path)
    {
        string full = Path.GetFullPath(path);
        string root = Path.GetPathRoot(full) ?? "";
        string current = root;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            string next = Path.Combine(current, part);
            try
            {
                FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
                if (info.Exists && info.ResolveLinkTarget(returnFinalTarget: true) is { } target) next = target.FullName;
            }
            catch (IOException) { /* not a link, or unreadable: keep the name */ }
            catch (UnauthorizedAccessException) { }
            current = next;
        }
        return current;
    }

    /// <summary>The folders a demo must never be written into: this repository (as the project sees it, and as found from the running code, which differs for a Movie Maker shadow project), the game install.</summary>
    IEnumerable<string> ForbiddenDemoRoots()
    {
        var roots = new List<string> { Path.GetFullPath(Path.Combine(ProjectSettings.GlobalizePath("res://"), "..")) };
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
            for (var dir = new DirectoryInfo(ResolveReal(start)); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "SmReimpl.slnx")) || Directory.Exists(Path.Combine(dir.FullName, ".git")) || File.Exists(Path.Combine(dir.FullName, ".git")))
                { roots.Add(dir.FullName); break; }
        if (_gameDir.Length > 0) roots.Add(_gameDir);
        return roots.Select(ResolveReal).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Where a demo may be written (review S4): a relative name goes under the private data folder (<c>demos</c>), an absolute one is taken as given; either way the
    /// path, links resolved, must be outside the repository and the game install. Demos hold other players' states and emblems: keep them private.
    /// </summary>
    string DemoOutputPath(string name)
    {
        string path = Path.GetFullPath(Path.IsPathRooted(name) ? name : Path.Combine(GameInstall.DataRoot, "demos", name));
        string real = ResolveReal(path);
        foreach (var forbidden in ForbiddenDemoRoots())
            if (SmFormats.InstallPaths.IsWithin(real, forbidden))
                throw new InvalidOperationException($"--record-demo {path} is inside the repository or the game folder; use a path under the private data folder or another folder outside both");
        return path;
    }

    /// <summary>
    /// The network a client runs on: a replayed demo's transport and clock, or the live UDP transport on the system clock (recording when asked).
    /// </summary>
    (IClientTransport Transport, IClock Clock, uint Nonce) DemoNetwork(IPEndPoint? endpoint, ulong fingerprint)
    {
        if (DemoPlayPath is { } play)
        {
            var demo = ReadDemoFile(play);
            if (demo.Header.ProtocolVersion != Protocol.Version)
                throw new InvalidDataException($"the demo was recorded with protocol {demo.Header.ProtocolVersion}; this client speaks {Protocol.Version}");
            if (demo.Header.ConfigHash != _config!.Hash())
                GD.PrintErr($"demo: recorded with config {demo.Header.ConfigHash:X16}, this client has {_config.Hash():X16}: the replay may differ from what was recorded");
            if (demo.Header.ContentFingerprint != fingerprint)
                GD.PrintErr($"demo: recorded on content {demo.Header.ContentFingerprint:X16}, this install is {fingerprint:X16}");
            if (demo.Packets.Count == 0) throw new InvalidDataException("the demo holds no packets");
            _demoPlayback = new DemoPlayback(demo);
            GD.Print($"demo: playing {play}: {demo.Packets.Count} packets, {demo.Inputs.Count} inputs, {demo.Views.Count} views, {demo.Duration:F1} s{(demo.Truncated ? " (cut off)" : "")}, session {(demo.Session is { } s ? $"player {s.PlayerId}, handshake {s.RoundTripMs} ms" : "never connected")}");
            return (_demoPlayback.Transport, _demoPlayback.Clock, demo.Header.Nonce);
        }
        var clock = new SystemClock();
        _demoClock = clock;
        uint nonce = (uint)System.Security.Cryptography.RandomNumberGenerator.GetInt32(1, int.MaxValue);
        _transport = new UdpClientTransport(endpoint!);
        IClientTransport transport = _transport;
        if (DemoRecordPath is { } record)
        {
            // A recording that cannot start (a refused path, a file that exists, a full disk) must not stop the session: say so and play on.
            try
            {
                string path = DemoOutputPath(record);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                // CreateNew: never truncate or write through an existing file (or a link) at that name.
                var file = new FileStream(path, FileMode.CreateNew, System.IO.FileAccess.Write, FileShare.Read);
                _demoWriter = new Demo.Writer(file, new DemoHeader(Demo.Version, Protocol.Version, _config!.Hash(), fingerprint, nonce, _config.TickRate));
                _demoWriter.Failed += reason => GD.PrintErr($"demo: recording stopped, the client plays on: {reason}");
                transport = _demoRecorder = new RecordingTransport(_transport, clock, _demoWriter);
                GD.Print($"demo: recording to {path}{(DemoRecordSeconds is { } s ? $" for {s:F0} s from the first packet" : "")}");
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                _demoWriter = null;
                GD.PrintErr($"demo: not recording ({ex.Message}); the client plays on");
            }
        }
        return (transport, clock, nonce);
    }

    /// <summary>The sample of a tick: the recorded input when replaying (the view the sample took is the replayed one), else the keyboard / bot.</summary>
    PlayerInput DemoSampleInput(uint tick)
    {
        _sampledYaw = _yaw; _sampledPitch = _pitch;
        return _demoPlayback!.SampleInput(tick);
    }

    /// <summary>Recording: notes what was sampled for a tick.</summary>
    void DemoRecordInput(PlayerInput input)
    {
        if (_demoWriter is not null && _demoClock is not null) _demoWriter.Input(_demoClock.Now.TotalSeconds, input);
    }

    /// <summary>Per frame before the client update: replaying, advances the demo clock by Godot's frame time and sets the recorded view.</summary>
    void DemoBeforeUpdate(double delta)
    {
        if (_demoPlayback is not { } pb) return;
        if (_demoStarted)
        {
            pb.Advance(delta);
            _demoFrames++;
        }
        else _demoPrerollFrames++;
        if (pb.ViewAt(pb.Elapsed) is { } view) { _yaw = view.Yaw; _pitch = view.Pitch; }
    }

    /// <summary>Per frame after the client update: records the view (recording), or runs the pre-roll and ends the playback (replaying).</summary>
    void DemoAfterUpdate(double delta)
    {
        if (_demoWriter is { } writer && _demoClock is { } clock && _client is not null)
        {
            double now = clock.Now.TotalSeconds;
            if (_client.Status == ClientStatus.Connected && !_demoSessionWritten)
            {
                _demoSessionWritten = true;
                writer.Session(now, _client.PlayerId, (int)_client.HandshakeRoundTrip.TotalMilliseconds);
            }
            writer.View(now, _yaw, _pitch);
            if (_demoElapsedWall - _demoFlushAt >= 2.0) { _demoFlushAt = _demoElapsedWall; writer.Flush(); }
            if (!writer.IsOpen && writer.Error is not null) { _demoWriter = null; return; }   // writing failed (reported once by Failed); the session goes on
            if (DemoRecordSeconds is { } limit && _demoRecorder?.FirstPacketAt is { } first && now - first >= limit)
            {
                writer.Dispose();
                GD.Print($"demo: recording closed after {now - first:F1} s");
                _demoWriter = null;
            }
            return;
        }
        if (_demoPlayback is not { } pb || _client is null) return;
        if (!_demoStarted)
        {
            // Pre-roll: the clock stands still until the Challenge is in (the map loaded) and the background loads are done, so the
            // footage starts with the demo and not with the loading.
            if (pb.Transport.Delivered < 1) return;
            DemoPrerollStep();
            if (DemoLoadsPending() is { } pending)
            {
                if (_demoWaitStart == 0) _demoWaitStart = System.Diagnostics.Stopwatch.GetTimestamp();
                if (_demoPrerollFrames > 300 || System.Diagnostics.Stopwatch.GetElapsedTime(_demoWaitStart).TotalSeconds > 120) { GD.PrintErr($"demo: still waiting for {pending} after {_demoPrerollFrames} frames; starting anyway"); }
                else { WaitForDemoLoads(); return; }
            }
            _demoStarted = true;
            GD.Print($"demo: playback starts after {_demoPrerollFrames} pre-roll frames");
            return;
        }
        var (renderTick, _) = _client.RemotePlayers();
        double serverClock = renderTick / _config!.TickRate;
        if (double.IsNaN(_demoStartRender) && renderTick > 0) { _demoStartRender = renderTick; _demoStartClock = serverClock; _demoStartElapsed = pb.Elapsed; }
        _demoEndRender = renderTick; _demoEndClock = serverClock; _demoEndElapsed = pb.Elapsed;
        if ((int)(pb.Elapsed / 10.0) != (int)((pb.Elapsed - delta) / 10.0))
            GD.Print($"demo: t {pb.Elapsed:F1} s: render tick {renderTick:F1}, match {_client.Match.State} ticks left {_client.Match.TicksLeft} (HUD {(_client.Match.TicksLeft == uint.MaxValue ? double.NaN : SecondsLeft(_client.Match)):F1} s), own {_client.Predicted.Position.X:F1},{_client.Predicted.Position.Y:F1}");
        // The match clock the HUD counts down, over the frames the match is in play.
        if (_client.Match is { State: MatchState.Playing, TicksLeft: not uint.MaxValue })
        {
            double hud = _client.Match.TicksLeft / (double)_config!.TickRate;   // the newest snapshot's ticks left (the HUD counts down from it by Godot time)
            if (double.IsNaN(_demoStartHud)) { _demoStartHud = hud; _demoStartHudElapsed = pb.Elapsed; }
            _demoEndHud = hud; _demoEndHudElapsed = pb.Elapsed;
        }
        if (pb.Finished && !_demoReported) DemoFinish(pb);
    }

    double _demoElapsedWall => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

    /// <summary>What the pre-roll still waits for (a background load or the main-thread step after it), or null when everything is in.</summary>
    string? DemoLoadsPending()
    {
        static bool Running(Task? t) => t is { IsCompleted: false };
        if (Running(_hipLoad)) return "hip fire animations";
        if (Running(_locoLoad)) return "locomotion animations";
        if (Running(_reloadLoad)) return "reload animations";
        if (Running(_soundLoad)) return "sound data";
        if (Running(_fxLoad)) return "effect data";
        if (Running(_fxMapLoad)) return "map effects";
        if (_soundLoad is not null) return "sound system (loaded, not built yet)";
        if (_fxLoad is not null && !_fxStarted) return "effect player (loaded, not built yet)";
        if (_fx is not null && _client!.MapName.Length > 0 && _fxMapSpawned != _client.MapName) return "map effects (not started)";
        return null;
    }

    /// <summary>
    /// Replaying: before the first frame, lets the client take the Challenge (which loads the map) and waits for the background loads and their
    /// main-thread follow-ups, so the footage starts with the demo and not with the loading (no pre-roll frames in the movie). If something
    /// is still pending after that, the frame loop keeps the clock still until it is in (<see cref="DemoAfterUpdate"/>).
    /// </summary>
    void DemoPrerollInReady()
    {
        if (_demoPlayback is null || _client is null) return;
        _client.Update(DemoSampleInput);
        for (int i = 0; i < 40; i++)
        {
            WaitForDemoLoads();
            DemoPrerollStep();
            if (DemoLoadsPending() is null) { _demoStarted = true; GD.Print("demo: playback starts with the first frame (loads done)"); return; }
        }
        GD.Print($"demo: still waiting for {DemoLoadsPending()} after the first load; the clock stands still until it is in");
    }

    /// <summary>
    /// The main-thread follow-ups of the background loads, which the frame loop only runs once connected (the clock stands still in the
    /// pre-roll, so the client is not): builds the effect player and starts the map's own effects, takes the sound system.
    /// </summary>
    void DemoPrerollStep()
    {
        try
        {
            if (FxReady() && FxActive) FxMapEffects();
            SoundReady();
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            GD.PrintErr($"demo: pre-roll step failed: {ex.Message}");
        }
    }

    void WaitForDemoLoads()
    {
        var running = new Task?[] { _hipLoad, _locoLoad, _reloadLoad, _soundLoad, _fxLoad, _fxMapLoad }.OfType<Task>().Where(t => !t.IsCompleted).ToArray();
        if (running.Length == 0) return;
        try { Task.WaitAll(running, TimeSpan.FromSeconds(60)); }
        catch (AggregateException) { /* a failed load is reported by its own step */ }
    }

    void DemoFinish(DemoPlayback pb)
    {
        _demoReported = true;
        double godot = _demoFrames > 0 ? pb.Elapsed : 0;
        double over = _demoEndElapsed - _demoStartElapsed, advanced = _demoEndClock - _demoStartClock;
        GD.Print($"DEMO-REPLAY frames {_demoFrames} (pre-roll {_demoPrerollFrames}), demo time {pb.Elapsed:F3} s of {pb.Demo.Duration:F3} s, godot time {godot:F3} s; " +
                 $"server clock (render tick) advanced {advanced:F3} s over {over:F3} s of demo time (ratio {(over > 0 ? advanced / over : double.NaN):F4}), " +
                 $"match clock (snapshot ticks left, while playing) {(double.IsNaN(_demoStartHud) ? "n/a" : $"counted down {_demoStartHud - _demoEndHud:F3} s over {_demoEndHudElapsed - _demoStartHudElapsed:F3} s of demo time")}, " +
                 $"input misses {pb.InputMisses}, packets {pb.Transport.Delivered}/{pb.Demo.Packets.Count}, client status {_client?.Status}");
        MotionProbeReport(final: true);
        if (!_demoNoQuit) GetTree().Quit();
    }

    /// <summary>Closes a recording that is still open (the view is leaving).</summary>
    void DemoClose()
    {
        if (_demoWriter is { } writer && _demoClock is { } clock)
        {
            try { writer.Dispose(); GD.Print($"demo: recording closed at {clock.Now.TotalSeconds:F1} s"); }
            catch (IOException ex) { GD.PrintErr("demo: " + ex.Message); }
            _demoWriter = null;
        }
        if (!_demoReported) MotionProbeReport(final: true);   // a live probe run reports at the end too
    }

    // ---- --motion-probe ----

    sealed class MotionTrack
    {
        public Vector3 Previous;
        public bool Has, WasAlive;
        public readonly List<(double Dt, double Step, double Speed)> Samples = new();
    }

    readonly Dictionary<int, MotionTrack> _motionTracks = new();
    double _motionReportAt;

    /// <summary>Per frame after everyone is placed: records how far each drawn marine moved this frame and how fast the simulation says it ran.</summary>
    void MotionProbeFrame(double delta, PlayerState me, IReadOnlyList<PlayerState> others)
    {
        if (!MotionProbe || delta <= 0 || _client is null) return;
        void Track(int id, CharacterViewer? node, PlayerState state)
        {
            if (node is null) return;
            if (!_motionTracks.TryGetValue(id, out var track)) _motionTracks[id] = track = new MotionTrack();
            var position = node.Transform.Origin;
            if (track.Has && track.WasAlive && state.Alive)
                track.Samples.Add((delta, position.DistanceTo(track.Previous), new System.Numerics.Vector2(state.Velocity.X, state.Velocity.Y).Length()));
            track.Previous = position; track.Has = true; track.WasAlive = state.Alive;
        }
        Track(_client.PlayerId, _local, me);
        foreach (var o in others) Track(o.PlayerId, _remotes.GetValueOrDefault(o.PlayerId), o);
        if (_connectedFor - _motionReportAt >= 10.0) { _motionReportAt = _connectedFor; MotionProbeReport(final: false); }
    }

    /// <summary>Prints the coefficient of variation of step / frame time for each tracked player's steady running frames (all frames so far).</summary>
    void MotionProbeReport(bool final)
    {
        if (!MotionProbe) return;
        foreach (var (id, track) in _motionTracks.OrderBy(t => t.Key))
        {
            var running = track.Samples.Where(s => s.Speed >= 3.0).ToList();
            if (running.Count < 30) { if (final) GD.Print($"MOTIONPROBE P{id}{(id == _client?.PlayerId ? " (own)" : "")}: {running.Count} running frames, too few"); continue; }
            double mode = running.GroupBy(s => Math.Round(s.Speed * 10)).OrderByDescending(g => g.Count()).First().Key / 10.0;
            // Steady: the simulation speed within 3 % of the commonest running speed; a respawn or teleport frame (a step far beyond the speed) is left out.
            var steady = running.Where(s => Math.Abs(s.Speed - mode) <= 0.03 * mode && s.Step / s.Dt < 3.0 * s.Speed).Select(s => s.Step / s.Dt).ToList();
            if (steady.Count < 30) { if (final) GD.Print($"MOTIONPROBE P{id}: {steady.Count} steady frames, too few"); continue; }
            double mean = steady.Average(), sd = Math.Sqrt(steady.Sum(v => (v - mean) * (v - mean)) / steady.Count);
            double mean2 = track.Samples.Count > 0 ? track.Samples.Average(s => s.Dt) : 0;
            if (final || _motionProbeWorst)
            {
                var worst = running.Where(w => Math.Abs(w.Speed - mode) <= 0.03 * mode && w.Step / w.Dt < 3.0 * w.Speed).OrderByDescending(w => Math.Abs(w.Step / w.Dt - mean)).Take(6)
                    .Select(w => $"dt {w.Dt * 1000:F1} ms step {w.Step:F3} m = {w.Step / w.Dt:F1} m/s");
                GD.Print($"MOTIONPROBE worst P{id}: {string.Join("; ", worst)}");
            }
            GD.Print($"MOTIONPROBE{(final ? " final" : "")} P{id}{(id == _client?.PlayerId ? " (own)" : "")}: {steady.Count} steady frames at {mode:F2} m/s (mean frame {mean2 * 1000:F1} ms{(OldRenderClock ? ", old render clock" : "")}), " +
                     $"step/time mean {mean:F3} m/s sd {sd:F3} CV {sd / mean * 100:F1} % min {steady.Min():F2} max {steady.Max():F2}");
        }
    }
}
