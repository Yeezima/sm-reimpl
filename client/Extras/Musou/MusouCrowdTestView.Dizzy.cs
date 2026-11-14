using System;
using Godot;
using SmNet.Extras.Musou;
using SmSim.Extras.Musou;

namespace SmClient;

/// <summary>
/// Test aid <c>--musou-crowd-test --dizzy [--stun s] [--kinds Warboss] [--cam-dist m]</c> (the dizzy boss, owner 2026-11-14): the real crowd on the plain stage; the boss stands (state Seek) for a second, is Stunned for
/// <c>--stun</c> s (default 3; the state's seconds count from the stun's start, as the server's do), then Seeks again for 1.5 s. A frame is saved every step (30 fps) as <c>&lt;--name&gt;-NNNN.png</c> into <c>--out</c>;
/// <c>--musou-old-boss-stun</c> after the <c>--</c> shows the stagger clip as before. A DIZZYTEST line per second prints the stars drawn, and one at the start the turn each clip makes of the root bone.
/// **Placeholder demonstration** around original assets read at run time.
/// </summary>
public partial class MusouCrowdTestView
{
    int _dizzyFrame;
    bool _dizzyLogged;

    void DizzyFrame(MusouEnemyType kind, MusouLooks.Look look, float dt)
    {
        float stun = Arg(_args, "stun", 3f), lead = 1f, tail = 1.5f;
        float t = _t += dt;
        if (!_dizzyLogged)
        {
            _dizzyLogged = true;
            foreach (var (name, clip) in new[] { ("stagger", look.Clips[MusouLooks.Stagger]), ("idle", look.Clips[MusouLooks.Idle]) })
                if (clip is not null) GD.Print($"DIZZYTEST {kind} {name} clip {clip.Name}: {clip.Animation.Duration:0.00} s, root turns {RootTurnDegrees(clip):0.0} degrees over the clip");
        }
        _shown.Clear();
        var state = t < lead || t >= lead + stun ? MusouEnemyState.Seek : MusouEnemyState.Stunned;
        float seconds = state == MusouEnemyState.Stunned ? t - lead : t;
        _shown.Add(new MusouShown(_id, V(0, 0, 0), -MathF.PI / 2f, kind, state, seconds));   // faces +X
        float height = MathF.Max(2f, LookHeight(look));
        float dist = Arg(_args, "cam-dist", height * 2.1f);
        var target = new Vector3(0f, 0f, height * 0.5f);
        var eye = target + new Vector3(dist * 0.75f, -dist * 0.65f, height * 0.1f);   // from the front and the side: the face and the stars show
        _camera!.Position = GameToGodot * eye;
        _camera.LookAt(GameToGodot * target, Vector3.Up);
        _crowd!.Frame(_shown, eye, dt);
        System.IO.Directory.CreateDirectory(_outDir);
        if (!_args.ContainsKey("no-shots")) GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(_outDir, $"{_name}-{_dizzyFrame:0000}.png"));
        if (_dizzyFrame % 30 == 0) GD.Print($"DIZZYTEST t {t:F2} {state} stars drawn {_crowd.DizzyShown}");
        _dizzyFrame++;
        if (t >= lead + stun + tail) { GD.Print($"DIZZYTEST done: {_dizzyFrame} frames"); GetTree().Quit(0); }
    }

    /// <summary>The angle (degrees) the clip's root bone turns from its first frame to its last.</summary>
    static float RootTurnDegrees(SmFormats.Havok.HavokBoundAnimation clip)
    {
        int track = Array.IndexOf(clip.TrackToBone, 0);
        if (track < 0) return 0f;
        var a = new SmFormats.Havok.HavokTransform[clip.Animation.TrackCount];
        var b = new SmFormats.Havok.HavokTransform[clip.Animation.TrackCount];
        clip.Animation.SampleInto(0f, a);
        clip.Animation.SampleInto(clip.Animation.Duration, b);
        float dot = MathF.Min(1f, MathF.Abs(System.Numerics.Quaternion.Dot(a[track].Rotation, b[track].Rotation)));
        return 2f * MathF.Acos(dot) * 180f / MathF.PI;
    }
}
