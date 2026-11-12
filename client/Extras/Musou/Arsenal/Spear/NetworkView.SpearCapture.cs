using System;
using System.IO;
using System.Threading.Tasks;
using Godot;
using SmContent.Extras.Musou;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Arsenal;
using SmSim.Extras.Musou.Campaign;

namespace SmClient;

/// <summary>
/// Test aid <c>--tfw-spear-capture</c> (with <c>--class assault --melee sm_mp_melee_power_spear --content musou</c>, before <c>--tfw-test &lt;dir&gt;</c>, a <c>--windowed</c> run for the PNGs; server
/// <c>SM_TFW_KEEP_HUMAN=1</c>): the power spear's approved clips in a real Musou match (<c>NetworkView.SpearPose.cs</c>). The player deploys on lane B and, away from the Orks, stands, runs, strikes a
/// light hew, the stun (F), the F, F stab and a jump-pack pound, each through the real actions, three times: for the game's own camera (<c>spear-player-*</c>) and for the preview stage's
/// front and side views (<c>spear-front-*</c>, <c>spear-side-*</c>: the camera set by the stance's front read from the hips, as the stage sets it, so the stills compare with the approved
/// previews), each still cropped round the marine and the spear (the bones and the spear's ends on screen). A <c>SPEARPOSE</c> line per still: the clip, its time, the overlay's weight,
/// the grip and the hips' turn from the body's forward. Quits 0.
/// </summary>
public partial class NetworkView
{
    /// <summary>The capture's third-person view this frame: 0 none (the game's camera), 1 the stage's front view, 2 its right side view.</summary>
    int _spearCam;

    async Task TfwSpearCapture()
    {
        var client = _client!;
        if (!await TfwTestDeploy(TfwLane.B)) return;
        await TfwWait(2.0);
        TfwCheck("the power spear is the melee weapon", _config is not null && SpearRules.Wields(_config, client.Predicted), MeleeWargear(client.Predicted) ?? "none");
        TfwCheck("the spear's clips drive the pose (no --musou-spear-stand-in)", SpearPosesOn);
        // Away from the landing's Guardsmen first (they stand in the game camera's view of a strike).
        TfwKey(Key.W, true);
        await TfwWait(3.0);
        TfwKey(Key.W, false);
        await TfwWait(1.0);
        float facing = _yaw;
        string views = CmdArg("tfw-spear-views") is { Length: > 0 } v ? v : "player,front";
        foreach (var (cam, view) in new[] { (0, "player"), (1, "front"), (2, "side") })
        {
            if (!views.Contains(view, StringComparison.OrdinalIgnoreCase)) continue;
            _yaw = facing;
            _spearCam = cam;
            await TfwWait(1.0);
            await SpearStill($"spear-{view}-1-idle", cam);
            // A run: W held for 1.2 s, the still in its stride.
            TfwKey(Key.W, true);
            await TfwWait(1.2);
            await SpearStill($"spear-{view}-2-run", cam);
            TfwKey(Key.W, false);
            await TfwWait(0.6);
            TfwKey(Key.S, true);   // back to where he stood, so the passes do not drift into the buildings
            await TfwWait(1.2);
            TfwKey(Key.S, false);
            await TfwWait(1.0);
            // The light hew at its hit; then the stun strike (F) at its hit; then F, F: the stab at its hit.
            await SpearPress(GameAction.Melee);
            await SpearAtHit(false, -1, $"spear-{view}-3-hew", cam);   // Musou picks a random swing of the light chain (MeleeChain.MusouSwing): any hew
            await TfwWait(3.0);
            await SpearPress(GameAction.StunStrike);
            await SpearAtHit(true, 1, $"spear-{view}-4-stun", cam);
            await TfwWait(2.5);
            await SpearPress(GameAction.StunStrike);
            await TfwUntil("spear: the stun shows", () => client.Predicted.MeleeStep == 1 && client.Predicted.MeleeHeavy, 2.0);
            await TfwUntil("spear: the stun's slam window", () => client.Predicted.MeleeTime >= 0.70f, 2.0);
            await SpearPress(GameAction.StunStrike);
            await SpearAtHit(true, 0, $"spear-{view}-5-stab", cam, lead: 0.06f);   // just before the slam's blast hides him (the lunge is all but home: the snap's last 0.06 s)
            await TfwWait(3.5);
            // The pound: a held jump, then melee in the air: the dive, and the impale on landing.
            TfwAction(GameAction.Jump, true);
            await TfwWait(0.55);
            TfwAction(GameAction.Jump, false);
            await TfwWait(0.15);
            await SpearPress(GameAction.Melee);
            if (await TfwUntil("spear: the dive shows", () => _local?.PosedClip is { } p && _jmpDive is not null && p == _jmpDive, 3.0))
            {
                await TfwFrames(6);
                await SpearStill($"spear-{view}-6-dive", cam);
            }
            // The landing away from the Orks (2026-11-11: the impale only on a hit): the plain landing, the spear up beside him, just after the drive-down's time and while he rises.
            if (await TfwUntil("spear: the landing shows", () => _local?.PosedClip is { } p && _jmpImpact is not null && p == _jmpImpact && _local.PosedClipTime >= ClawSpearClips.ImpaleDrive + 0.03f, 6.0))
                await SpearStill($"spear-{view}-7-landing", cam);
            if (await TfwUntil("spear: the landing holds", () => _local?.PosedClip is { } p && _jmpImpact is not null && p == _jmpImpact && _local.PosedClipTime >= 0.6f, 2.0))
                await SpearStill($"spear-{view}-8-landing-held", cam);
            await TfwWait(2.0);
            _yaw = facing;
            TfwKey(Key.S, true);   // back from the pound's leap
            await TfwWait(2.0);
            TfwKey(Key.S, false);
            await TfwWait(1.0);
        }
        _spearCam = 0;
        if (!CmdFlag("tfw-spear-no-hit")) await SpearHitLanding();
        _spearCam = 0;
        await TfwWait(0.5);
        GetTree().Quit(0);
    }

    /// <summary>
    /// Owner's rule for recordings (2026-11-12: "make the bot be in an open area of the Ashfall map so everything can be seen clearly"): before a film the player walks, along the map's own walking
    /// graph, to an open spot (<c>--tfw-spear-spot x,y</c> in map metres; default the plaza's centre, objective B's open ground at (0, 0)) so no wall or crowd stands in the camera's way. A spot with no
    /// way from the landing is noted and the film is taken where he stands (check the first frames).
    /// </summary>
    async Task TfwSpearOpenSpot()
    {
        float x = 0f, y = 0f;
        if (CmdArg("tfw-spear-spot") is { Length: > 0 } spot)
        {
            var parts = spot.Split(',');
            if (parts.Length != 2 || !float.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out x) || !float.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out y)) { GD.Print($"SPEARFILM note: --tfw-spear-spot '{spot}' is not x,y; using 0,0"); x = y = 0f; }
        }
        var at = new System.Numerics.Vector3(x, y, _client!.Predicted.Position.Z);
        bool ok = await TfwPathTo(at, "the film's open spot", 90.0);
        TfwKey(Key.W, false);
        GD.Print($"SPEARFILM open spot {(ok ? "reached" : "not reached")}: at {_client.Predicted.Position}");
        if (CmdArg("tfw-spear-hide-others") is not null)
        {
            // Spear pass 4b (owner, 2026-11-12: no allies crossing the camera): every other character is hidden from here on (the drawn marine alone), checked ten times a second.
            var timer = new Godot.Timer { WaitTime = 0.1, Autostart = true };
            timer.Timeout += () => { foreach (var n in GetTree().Root.FindChildren("*", "Node3D", true, false)) if (n is CharacterViewer cv && !ReferenceEquals(cv, _local) && cv.Visible) cv.Visible = false; };
            AddChild(timer);
        }
        await TfwWait(0.5);
    }

    /// <summary>
    /// Test aid <c>--tfw-spear-film [--tfw-spear-film-step n] [--tfw-spear-film-takes k] [--tfw-spear-views player,front,side]</c> (musou/spear-authored, 2026-11-12), for a recording
    /// (<c>godot.cmd --windowed --write-movie f.avi --fixed-fps 30 -- --connect ... --content musou --class "sm - assault" --melee "power spear" --tfw-spear-film --tfw-test &lt;dir&gt;</c>, server
    /// <c>SM_TFW_KEEP_HUMAN=1</c>): the real match, the real keys: the player deploys, walks away from the Orks and swings the light chain from standing, k times per view (default 2), keeping
    /// only the takes where Musou's random pick (<c>MeleeChain.MusouSwing</c>) was swing <c>n</c> (default 1), each ended by waiting the swing out. A <c>SPEARFILM</c> line per take with the
    /// view and the engine's frame numbers at the press, the hit and the end (frame / fixed fps = the recording's time), so a before / after recording is cut at the same moments.
    /// </summary>
    async Task TfwSpearFilm()
    {
        var client = _client!;
        if (!await TfwTestDeploy(TfwLane.B)) return;
        await TfwWait(2.0);
        TfwCheck("the power spear is the melee weapon", _config is not null && SpearRules.Wields(_config, client.Predicted), MeleeWargear(client.Predicted) ?? "none");
        TfwCheck("the spear's clips drive the pose (no --musou-spear-stand-in)", SpearPosesOn);
        await TfwSpearOpenSpot();
        TfwKey(Key.W, true);
        await TfwWait(3.0);
        TfwKey(Key.W, false);
        await TfwWait(1.0);
        float facing = _yaw;
        int want = int.TryParse(CmdArg("tfw-spear-film-step"), out int w) && w > 0 ? w : 1;
        int takes = int.TryParse(CmdArg("tfw-spear-film-takes"), out int k) && k > 0 ? k : 2;
        string views = CmdArg("tfw-spear-views") is { Length: > 0 } v ? v : "player,front,side";
        foreach (var (cam, view) in new[] { (0, "player"), (1, "front"), (2, "side") })
        {
            if (!views.Contains(view, StringComparison.OrdinalIgnoreCase)) continue;
            _yaw = facing;
            _spearCam = cam;
            await TfwWait(1.0);
            int got = 0;
            for (int attempt = 0; attempt < 18 && got < takes; attempt++)
            {
                // The real-time simulation and a fixed-rate recording (--write-movie) run at different rates, so each attempt logs, for every frame drawn from the key press on, the engine's frame
                // number (= the recording's frame), the wall-clock time and the clip's own time in a SPEARFILMFRAMES line; the recording is cut and each frame shown for the time it really lasted.
                var rows = new System.Text.StringBuilder();
                ulong t0 = Time.GetTicksMsec();
                async Task Tick()
                {
                    await TfwFrames();
                    var pose = _local?.WeaponPose;
                    rows.Append($"{Engine.GetFramesDrawn()},{Time.GetTicksMsec() - t0},{pose?.Clip?.Name ?? "none"},{pose?.Time ?? 0f:F4};");
                }
                int pressFrame = (int)Engine.GetFramesDrawn();
                TfwAction(GameAction.Melee, true);
                while (Time.GetTicksMsec() - t0 < 120) await Tick();
                TfwAction(GameAction.Melee, false);
                bool began = false;
                while (Time.GetTicksMsec() - t0 < 3000 && !(began = client.Predicted.MeleeStep > 0 && !client.Predicted.MeleeHeavy)) await Tick();
                if (!began) { await TfwWait(1.0); continue; }
                int step = client.Predicted.MeleeStep;
                bool keep = step == want;
                while (Time.GetTicksMsec() - t0 < 4200) await Tick();
                GD.Print($"SPEARFILM view {view} swing {step} {(keep ? "kept" : "skipped")} press {pressFrame} take {got + 1}");
                if (keep) GD.Print($"SPEARFILMFRAMES {view} {got + 1} {rows}");
                if (keep) got++;
                await TfwWait(0.6);
            }
        }
        _spearCam = 0;
        await TfwWait(0.5);
        GetTree().Quit(0);
    }

    /// <summary>
    /// Test aid <c>--tfw-spear-film --tfw-spear-film-kind chain|snap|stab [--tfw-spear-views player,side,front] [--tfw-spear-spot x,y] [--tfw-spear-hide-others]</c> (spear pass 4b, 2026-11-12): the real keys in a real
    /// match for a recording: <c>chain</c> the light string (three melee taps through the combo openings), <c>snap</c> the F strike, <c>stab</c> F then F again in the slam window (the massive stab). One take
    /// per view; a <c>SPEARFILMFRAMES view 1 ...</c> line (engine frame, wall ms, clip, clip time) to cut the recording by.
    /// </summary>
    async Task TfwSpearStrikeFilm()
    {
        var client = _client!;
        if (!await TfwTestDeploy(TfwLane.B)) return;
        await TfwWait(2.0);
        TfwCheck("the power spear is the melee weapon", _config is not null && SpearRules.Wields(_config, client.Predicted), MeleeWargear(client.Predicted) ?? "none");
        await TfwSpearOpenSpot();
        TfwKey(Key.W, true);
        await TfwWait(3.0);
        TfwKey(Key.W, false);
        await TfwWait(1.0);
        float facing = _yaw;
        string kind = CmdArg("tfw-spear-film-kind") ?? "chain", views = CmdArg("tfw-spear-views") is { Length: > 0 } v ? v : "player,side";
        foreach (var (cam, view) in new[] { (0, "player"), (1, "front"), (2, "side") })
        {
            if (!views.Contains(view, StringComparison.OrdinalIgnoreCase)) continue;
            _yaw = facing;
            _spearCam = cam;
            await TfwWait(1.0);
            var rows = new System.Text.StringBuilder();
            ulong t0 = Time.GetTicksMsec();
            async Task Tick()
            {
                await TfwFrames();
                var pose = _local?.WeaponPose;
                rows.Append($"{Engine.GetFramesDrawn()},{Time.GetTicksMsec() - t0},{pose?.Clip?.Name ?? "none"},{pose?.Time ?? 0f:F4};");
            }
            async Task Hold(double seconds) { ulong until = Time.GetTicksMsec() + (ulong)(seconds * 1000); while (Time.GetTicksMsec() < until) await Tick(); }
            async Task Tap(GameAction a) { TfwAction(a, true); await Hold(0.12); TfwAction(a, false); await Tick(); }
            await Hold(0.8);
            if (kind == "chain")
            {
                await Tap(GameAction.Melee); await Hold(0.85);
                await Tap(GameAction.Melee); await Hold(0.85);
                await Tap(GameAction.Melee); await Hold(3.2);
            }
            else if (kind == "snap") { await Tap(GameAction.StunStrike); await Hold(4.0); }
            else
            {
                await Tap(GameAction.StunStrike);
                ulong give = Time.GetTicksMsec() + 3000;
                while (Time.GetTicksMsec() < give && !(client.Predicted.MeleeStep == 1 && client.Predicted.MeleeHeavy && client.Predicted.MeleeTime >= 0.70f)) await Tick();
                await Tap(GameAction.StunStrike); await Hold(5.5);
            }
            GD.Print($"SPEARFILM kind {kind} view {view} done");
            GD.Print($"SPEARFILMFRAMES {view} 1 {rows}");
            await TfwWait(1.0);
            TfwKey(Key.S, true); await TfwWait(2.0); TfwKey(Key.S, false);   // back from the lunge, so the next view starts where this did
            await TfwWait(1.0);
        }
        _spearCam = 0;
        GetTree().Quit(0);
    }

    /// <summary>
    /// Test aid <c>--tfw-spear-carry-film [--tfw-spear-views player,side,front]</c> (spear pass 3, 2026-11-12), for a recording (<c>godot.cmd --windowed --write-movie f.avi --fixed-fps 30 -- --connect ...
    /// --content musou --class "sm - assault" --melee "power spear" --tfw-spear-carry-film --tfw-test &lt;dir&gt;</c>, server <c>SM_TFW_KEEP_HUMAN=1</c>): the real match, the real keys: the player deploys,
    /// walks away from the Orks, then per view stands, runs forward (W), runs left (A), runs right (D), sprints (W + Sprint) and swings one light hew from standing. A <c>SPEARCARRYFRAMES</c> line
    /// per view lists, for every frame drawn, the engine's frame number (= the recording's frame), the wall-clock time and the phase, to cut the recording by; a <c>SPEARCARRY</c> line per phase gives the
    /// drawn spear against the drawn spine: the shaft's fore-and-aft lean and the spine's, its side lean, the right grip socket's distance from the shaft, and the right arm's bone lengths.
    /// </summary>
    async Task TfwSpearCarryFilm()
    {
        var client = _client!;
        if (!await TfwTestDeploy(TfwLane.B)) return;
        await TfwWait(2.0);
        TfwCheck("the power spear is the melee weapon", _config is not null && SpearRules.Wields(_config, client.Predicted), MeleeWargear(client.Predicted) ?? "none");
        TfwCheck("the spear's clips drive the pose (no --musou-spear-stand-in)", SpearPosesOn);
        await TfwSpearOpenSpot();
        TfwKey(Key.W, true);
        await TfwWait(3.0);
        TfwKey(Key.W, false);
        await TfwWait(1.0);
        float facing = _yaw;
        string views = CmdArg("tfw-spear-views") is { Length: > 0 } v ? v : "player,side";
        foreach (var (cam, view) in new[] { (0, "player"), (1, "front"), (2, "side") })
        {
            if (!views.Contains(view, StringComparison.OrdinalIgnoreCase)) continue;
            _yaw = facing;
            _spearCam = cam;
            await TfwWait(1.0);
            var rows = new System.Text.StringBuilder();
            ulong t0 = Time.GetTicksMsec();
            async Task Phase(string label, double seconds, Action? on, Action? off)
            {
                on?.Invoke();
                ulong end = Time.GetTicksMsec() + (ulong)(seconds * 1000.0), probe = Time.GetTicksMsec() + (ulong)(seconds * 500.0);
                bool probed = false;
                while (Time.GetTicksMsec() < end)
                {
                    await TfwFrames();
                    var pose = _local?.WeaponPose;
                    rows.Append($"{Engine.GetFramesDrawn()},{Time.GetTicksMsec() - t0},{label},{pose?.Clip?.Name ?? "none"},{pose?.Time ?? 0f:F3};");
                    if (!probed && Time.GetTicksMsec() >= probe) { probed = true; GD.Print($"SPEARCARRY {view} {label}: {SpearCarryProbe()}"); }
                }
                off?.Invoke();
            }
            // Sprint first (the way ahead is the longest), then back the way he came, so no phase runs into a wall (a sprint into one stands still).
            await Phase("idle", 2.5, null, null);
            await Phase("sprint", 2.0, () => { TfwKey(Key.W, true); TfwAction(GameAction.Sprint, true); }, () => { TfwKey(Key.W, false); TfwAction(GameAction.Sprint, false); });
            await Phase("idle2", 0.8, () => _yaw = facing + MathF.PI, null);
            await Phase("run", 3.0, () => TfwKey(Key.W, true), () => TfwKey(Key.W, false));
            await Phase("idle3", 0.8, () => _yaw = facing, null);
            await Phase("run-left", 2.5, () => TfwKey(Key.A, true), () => TfwKey(Key.A, false));
            await Phase("run-right", 2.5, () => TfwKey(Key.D, true), () => TfwKey(Key.D, false));
            await Phase("idle4", 1.0, null, null);
            await Phase("hew", 4.0, () => TfwAction(GameAction.Melee, true), () => TfwAction(GameAction.Melee, false));
            GD.Print($"SPEARCARRYFRAMES {view} {rows}");
            _yaw = facing;
        }
        _spearCam = 0;
        await TfwWait(0.5);
        GetTree().Quit(0);
    }

    /// <summary>Test aid (<see cref="TfwSpearCarryFilm"/>): the drawn spear against the drawn spine and the right arm, in the own skeleton's space.</summary>
    string SpearCarryProbe()
    {
        if (_local?.Skeleton is not { } sk || _local.Weapons?.SpearEnds(out var butt, out var tip) != true) return "no spear drawn";
        var inv = sk.GlobalTransform.AffineInverse();
        var b = inv * butt; var t = inv * tip;
        var shaft = (t - b).Normalized();
        var spine = _local.WeaponPoseSpine();
        float Lean(float y, float z) => MathF.Atan2(y, z) * 180f / MathF.PI;
        float roll = MathF.Asin(Mathf.Clamp(-shaft.X, -1f, 1f)) * 180f / MathF.PI;
        string hand = "";
        if (_local.WeaponPoseHands(out var right, out _))
        {
            var d = right - b; float along = d.Dot(shaft);
            hand = $", right grip socket {(d - shaft * along).Length() * 100f:F1} cm off the shaft";
        }
        float Len(string a, string c) { int ia = sk.FindBone(a), ic = sk.FindBone(c); return ia < 0 || ic < 0 ? -1f : sk.GetBoneGlobalPose(ia).Origin.DistanceTo(sk.GetBoneGlobalPose(ic).Origin) * 100f; }
        var vel = _client!.Predicted.Velocity;
        return $"speed {MathF.Sqrt(vel.X * vel.X + vel.Y * vel.Y):F1} m/s, shaft lean {Lean(shaft.Y, shaft.Z):F1} deg forward, spine lean {Lean(spine.Y, spine.Z):F1}, side lean {roll:F1}{hand}; right arm bones cm: upper {Len("RIG_R_UpperArm", "RIG_R_Forearm"):F1} fore {Len("RIG_R_Forearm", "RIG_R_Hand"):F1} hand {Len("RIG_R_Hand", SpearHold.RightGripBone):F1}; {SpearHeights()}";
    }

    /// <summary>
    /// The impaling landing (2026-11-11: only on a hit): the player walks up to the nearest Ork the client sees and pounds on it from a short hop, in the game's camera and the stage's side view;
    /// stills at the drive-down and the hold, and the landing's choice in a SPEARLAND line (<see cref="SpearLandingOf"/>).
    /// </summary>
    async Task SpearHitLanding()
    {
        var client = _client!;
        foreach (var (cam, view) in new[] { (0, "player"), (0, "player-again") })   // the game's camera twice (the stage's side view stands in the pound's debris)
        {
            _spearCam = 0;
            bool near = false;
            for (int k = 0; k < 450 && !near; k++)   // up to 45 s of walking toward the nearest Ork
            {
                var me = client.Predicted.Position;
                if (_musouState.NearestTo(me, 60f) is not { } ork) { await TfwWait(0.1); continue; }
                var to = ork - me;
                _yaw = MathF.Atan2(-to.X, to.Y);   // the game's yaw: 0 along +Y, positive toward -X
                near = new System.Numerics.Vector2(to.X, to.Y).Length() < 1.6f;
                TfwKey(Key.W, !near);
                await TfwWait(0.1);
            }
            TfwKey(Key.W, false);
            GD.Print($"SPEARLAND capture: {(near ? "beside an Ork" : "no Ork reached")} for the {view} view");
            _spearCam = cam;
            TfwAction(GameAction.Jump, true);   // the same pound as the views' (a held jump, then melee in the air)
            await TfwWait(0.55);
            TfwAction(GameAction.Jump, false);
            await TfwWait(0.15);
            await SpearPress(GameAction.Melee);
            if (await TfwUntil("spear: the hit landing shows", () => _local?.PosedClip is { } p && _jmpImpact is not null && p == _jmpImpact && _local.PosedClipTime >= ClawSpearClips.ImpaleDrive + 0.03f, 6.0))
                await SpearStill($"spear-{view}-9-impale", cam);
            if (await TfwUntil("spear: the hit landing holds", () => _local?.PosedClip is { } p && _jmpImpact is not null && p == _jmpImpact && _local.PosedClipTime >= 0.6f, 2.0))
                await SpearStill($"spear-{view}-10-impale-held", cam);
            await TfwWait(2.0);
        }
    }

    /// <summary>Test aid: the landing's choice, once it locks (only with <c>--tfw-spear-capture</c>).</summary>
    static readonly bool SpearCaptureLog = CmdFlag("tfw-spear-capture");

    /// <summary>Test aid: how far the spear's head (collar to tip) stands from the screen's centre column, in pixels (positive: to its right; 0: across it), in the camera's view now.</summary>
    string SpearHeadColumn()
    {
        if (_camera is not { } camera || _local?.Weapons?.SpearEnds(out var butt, out var tip) != true) return "head column -";
        float centre = GetViewport().GetVisibleRect().Size.X * 0.5f, least = float.PositiveInfinity, most = float.NegativeInfinity;
        for (int k = 0; k <= 8; k++)
        {
            float along = SpearHold.CollarFromButt - 0.11f + (SpearHold.TipFromButt - SpearHold.CollarFromButt + 0.11f) * k / 8f;
            var p = butt + (tip - butt) * (along / SpearHold.TipFromButt);
            if (camera.IsPositionBehind(p)) return "head column behind";
            float x = camera.UnprojectPosition(p).X - centre;
            least = MathF.Min(least, x); most = MathF.Max(most, x);
        }
        float px = least > 0f ? least : most < 0f ? most : 0f;
        return $"head column {px:F0} px ({least:F0}..{most:F0})";
    }

    /// <summary>A press of <paramref name="action"/> held 0.12 s (a few ticks at 30 Hz, whatever the frame rate).</summary>
    async Task SpearPress(GameAction action) { TfwAction(action, true); await TfwWait(0.12); TfwAction(action, false); await TfwFrames(2); }

    /// <summary>Waits for the own strike (light or heavy step <paramref name="step"/>; 0: any heavy step from 4, the slam's; -1: any step) to reach its hit, then takes the still.</summary>
    async Task SpearAtHit(bool heavy, int step, string name, int cam, float lead = 0.02f)
    {
        var client = _client!;
        string seen = "";
        bool Mine()
        {
            var p = client.Predicted;
            string now = $"step {p.MeleeStep}{(p.MeleeHeavy ? " heavy" : "")} rush {p.Rush} jump {p.Jump} evade {p.EvadeTicks}";
            if (!seen.EndsWith(now, StringComparison.Ordinal)) seen += (seen.Length > 0 ? " > " : "") + now;
            return p.MeleeHeavy == heavy && (step > 0 ? p.MeleeStep == step : step < 0 ? p.MeleeStep > 0 : p.MeleeStep >= 4);
        }
        if (!await TfwUntil($"spear: {name} begins", Mine, 3.0)) { GD.Print($"SPEARPOSE {name}: the strike did not begin; seen {seen}"); return; }
        var hit = _config is not null && Combat.MeleeStepOf(_config, client.Predicted) is { } s ? s.HitTime : 0.7f;
        if (!await TfwUntil($"spear: {name} reaches its hit", () => Mine() && client.Predicted.MeleeTime >= hit - lead, 4.0)) return;
        await SpearStill(name, cam);
    }

    /// <summary>One still in view <paramref name="cam"/>, cropped round the own marine and the spear, with a SPEARPOSE line.</summary>
    async Task SpearStill(string name, int cam)
    {
        _spearCam = cam;
        if (_local?.WeaponPose is { } pose)
        {
            var hips = _local.WeaponPoseBaseFront;
            float turn = MathF.Atan2(hips.Y, hips.X) * 180f / MathF.PI;   // from the skeleton's +X
            float view = 0f;
            if (_camera is { } viewCam && _local.Skeleton is { } skel)
            {
                var toSkeleton = skel.GlobalTransform.AffineInverse().Basis;
                var look = toSkeleton * -viewCam.GlobalBasis.Z; view = MathF.Atan2(look.Y, look.X) * 180f / MathF.PI;
            }
            GD.Print($"SPEARPOSE {name} t {_connectedFor:F3} clip {pose.Clip?.Name ?? "none"} time {pose.Time:F3} weight {pose.Weight:F2} base {pose.Base ?? "-"} posed {_local.PosedClip ?? "stances"} grip {pose.Clip?.GripAt(pose.Time) ?? 1f:F2} point {pose.Clip?.GripPointAt(pose.Time) ?? WeaponPoseClip.LowGrip:F2} m hips {turn:F0} deg camera {view:F0} laid {_local.WeaponPoseBaseLaid ?? "-"} hands z {(_local.WeaponPoseHands(out var rh, out var lh) ? $"{rh.Z:F2} {lh.Z:F2}" : "-")} {SpearHeights()}; {SpearHeadColumn()}; frame {Engine.GetFramesDrawn()}; {_local.Weapons?.SpearPoseText() ?? "no rig"}; melee step {_client!.Predicted.MeleeStep}{(_client.Predicted.MeleeHeavy ? " heavy" : "")} at {_client.Predicted.MeleeTime:F3} s");
        }
        else GD.Print($"SPEARPOSE {name}: no weapon pose on the own marine");
        if (_tfwShots is null || DisplayServer.GetName() == "headless") return;
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var image = GetViewport().GetTexture().GetImage();
        var crop = SpearCrop(image.GetSize());
        if (crop is { } rect) image = image.GetRegion(rect);
        string path = Path.Combine(_tfwShots, name + ".png");
        GD.Print(image.SavePng(path) == Error.Ok ? $"tfw shot -> {path}" : $"tfw shot failed: {path}");
    }

    /// <summary>Test aid: the lowest bone's height and the spear's butt and tip heights in the own skeleton's space (the feet stand near the lowest bone).</summary>
    string SpearHeights()
    {
        if (_local?.Skeleton is not { } sk) return "";
        float low = float.MaxValue;
        for (int b = 0; b < sk.GetBoneCount(); b++) low = MathF.Min(low, sk.GetBoneGlobalPose(b).Origin.Z);
        string ends = _local.Weapons?.SpearEnds(out var butt, out var tip) == true ? $" butt {(sk.GlobalTransform.AffineInverse() * butt).Z:F2} tip {(sk.GlobalTransform.AffineInverse() * tip).Z:F2}" : "";
        return $"lowest bone {low:F2}{ends}";
    }

    /// <summary>The screen rectangle round the own marine's bones and the spear's ends (padded), or null when they are not on screen.</summary>
    Rect2I? SpearCrop(Vector2I size)
    {
        if (_camera is not { } camera || _local?.Skeleton is not { } sk) return null;
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        void Add(Vector3 world)
        {
            if (camera.IsPositionBehind(world)) return;
            var p = camera.UnprojectPosition(world);
            minX = MathF.Min(minX, p.X); minY = MathF.Min(minY, p.Y); maxX = MathF.Max(maxX, p.X); maxY = MathF.Max(maxY, p.Y);
        }
        for (int b = 0; b < sk.GetBoneCount(); b++) Add(sk.GlobalTransform * sk.GetBoneGlobalPose(b).Origin);
        if (_local.Weapons?.SpearEnds(out var butt, out var tip) == true) { Add(butt); Add(tip); }
        if (minX > maxX) return null;
        float pad = MathF.Max(maxX - minX, maxY - minY) * 0.18f + 24f;
        int x0 = Math.Clamp((int)(minX - pad), 0, size.X - 1), y0 = Math.Clamp((int)(minY - pad), 0, size.Y - 1);
        int x1 = Math.Clamp((int)(maxX + pad), x0 + 1, size.X), y1 = Math.Clamp((int)(maxY + pad), y0 + 1, size.Y);
        return new Rect2I(x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>
    /// Puts the camera off the player for the capture's stage views (<see cref="_spearCam"/>), after the game's camera; nothing without the aid. As the preview stage frames its marine: the
    /// stance's front read from the hips before the overlay moved them (<see cref="CharacterViewer.WeaponPoseBaseFront"/>), the front view 4.5 m ahead and 1.5 m up, the side view 4.5 m to his right.
    /// </summary>
    void SpearCamFrame()
    {
        if (_spearCam == 0 || _camera is not { } camera || _local?.Skeleton is not { } sk) return;
        var front = sk.GlobalBasis * _local.WeaponPoseBaseFront;
        front.Y = 0f;
        if (front.LengthSquared() < 1e-6f) return;
        front = front.Normalized();
        var right = front.Cross(Vector3.Up).Normalized();
        var feet = _local.GlobalPosition;
        var eye = _spearCam == 1 ? feet + front * 4.5f + Vector3.Up * 1.5f - right * 0.3f : feet + right * 4.5f + Vector3.Up * 1.5f + front * 0.3f;   // the stage's 6.4 m at its 45 degree lens; closer here for the game's wider one
        var look = feet + Vector3.Up * 1.2f + (_spearCam == 2 ? front * 0.3f : Vector3.Zero);
        camera.GlobalTransform = Transform3D.Identity.Translated(eye).LookingAt(look, Vector3.Up);
    }
}
