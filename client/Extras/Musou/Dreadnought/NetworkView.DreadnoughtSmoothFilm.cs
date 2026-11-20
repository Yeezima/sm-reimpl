using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Godot;
using SmSim;

namespace SmClient;

/// <summary>
/// <c>--dn-smooth-film</c> (2026-11-20, the Dreadnought smoothing pass's proof; Musou / Warfront added content; a test aid, with <c>--tfw-dreadnought-capture</c>, <c>--tfw-test &lt;dir&gt;</c>, server <c>SM_TFW_KEEP_HUMAN=1</c>
/// <c>SM_DN_TEST_GUARD=1</c> and <c>--bind 127.0.0.1</c>; <c>--write-movie f.avi --fixed-fps 30</c> for the footage): right after the landing, in the open before any Ork has come, from the side view: standing light strikes
/// (X1, X2, X1), a light strike while walking, the stomp, X then Y (the slam) and the charge (Sprint + Melee) with the walk before and after. Every rendered frame is logged to <c>dn-smooth.csv</c> in the test folder (the segment, the
/// frame time, the simulation's state, the drawn body and four bones in the world) so the choppiness is measured, not guessed: <c>DNSMOOTH</c> lines mark the segments. <c>--dn-old-smooth</c> restores the old charge (its clip at the simulation's stepped time; <c>--dn-raw-strike</c> the old strikes).
/// </summary>
public partial class NetworkView
{
    /// <summary>Test aid <c>--dn-old-smooth</c>: the charge as it was drawn before the 2026-11-20 pass (the "before" of the clips).</summary>
    static readonly bool DreadOldSmooth = CmdFlag("dn-old-smooth");

    async Task DnSmoothFilm()
    {
        var client = _client!;
        var csv = new StringBuilder("frame,t,dt,seg,px,py,speed,rush,rushT,step,heavy,stepT,cx,cy,cz,b0x,b0y,b0z,b1x,b1y,b1z,b2x,b2y,b2z,b3x,b3y,b3z\n");
        string seg = "start"; bool stop = false;
        string[] want = ["hand", "hand", "foot", "foot"]; var bones = new int[4]; bool boneLogged = false;
        ulong lastUs = Time.GetTicksUsec();
        void Sample()
        {
            ulong now = Time.GetTicksUsec(); double dt = (now - lastUs) / 1e6; lastUs = now;
            var p = client.Predicted;
            if (!_dreadTracks.TryGetValue(p.PlayerId, out var tr) || tr.Viewer is not { } v || !IsInstanceValid(v) || v.Skeleton is not { } sk) return;
            if (!boneLogged)
            {
                boneLogged = true; int used = 0;
                var names = new List<string>();
                for (int i = 0; i < sk.GetBoneCount(); i++) names.Add(sk.GetBoneName(i));
                int Find(string key, int skip) { int seen = 0; for (int i = 0; i < names.Count; i++) if (names[i].Contains(key, StringComparison.OrdinalIgnoreCase) && seen++ == skip) return i; return Math.Max(0, sk.GetBoneCount() / 2); }
                bones[0] = Find("LHand", 0); bones[1] = Find("RHand", 0); bones[2] = Find("LFoot", 0); bones[3] = Find("RFoot", 0); used++;
                GD.Print($"DNSMOOTH bones {string.Join(",", bones.Select(i => names[i]))} of {names.Count}");
            }
            float speed = MathF.Sqrt(p.Velocity.X * p.Velocity.X + p.Velocity.Y * p.Velocity.Y);
            var c = v.GlobalPosition;
            var sb = new StringBuilder();
            var ci = CultureInfo.InvariantCulture;
            sb.Append(Engine.GetFramesDrawn()).Append(',').Append(_connectedFor.ToString("F4", ci)).Append(',').Append(dt.ToString("F5", ci)).Append(',').Append(seg).Append(',')
              .Append(p.Position.X.ToString("F3", ci)).Append(',').Append(p.Position.Y.ToString("F3", ci)).Append(',').Append(speed.ToString("F2", ci)).Append(',')
              .Append((int)p.Rush).Append(',').Append(SmSim.Rush.Time(p, _config!).ToString("F4", ci)).Append(',').Append(p.MeleeStep).Append(',').Append(p.MeleeHeavy ? 1 : 0).Append(',').Append(p.MeleeTime.ToString("F4", ci)).Append(',')
              .Append(c.X.ToString("F4", ci)).Append(',').Append(c.Y.ToString("F4", ci)).Append(',').Append(c.Z.ToString("F4", ci));
            foreach (int b in bones) { var w = sk.GlobalTransform * sk.GetBoneGlobalPose(b).Origin; sb.Append(',').Append(w.X.ToString("F4", ci)).Append(',').Append(w.Y.ToString("F4", ci)).Append(',').Append(w.Z.ToString("F4", ci)); }
            csv.Append(sb).Append('\n');
        }
        async Task Recorder() { while (!stop) { await TfwFrames(); Sample(); } }
        void Seg(string name) { seg = name; GD.Print($"DNSMOOTH seg {name} frame {Engine.GetFramesDrawn()} t {_connectedFor:F2}"); }
        await TfwWait(5.4);   // the entrance's lock is over
        _dnCam = 1;
        await TfwWait(0.6);
        GD.Print($"DNSMOOTH start frame {Engine.GetFramesDrawn()}");
        _yaw += MathF.PI;   // the Guards stand ahead of the landing: the open ground is behind
        _ = Recorder();
        Seg("idle"); await TfwWait(1.0);
        Seg("light-stand");
        for (int k = 0; k < 3; k++) { await TfwActionTap(GameAction.Melee); await TfwWait(0.55); }
        await TfwWait(1.6);
        Seg("walk"); TfwKey(Key.W, true); await TfwWait(1.5);
        Seg("light-walk");
        await TfwActionTap(GameAction.Melee); await TfwWait(0.5); await TfwActionTap(GameAction.Melee); await TfwWait(0.5);
        Seg("walk-after"); await TfwWait(2.0);
        TfwKey(Key.W, false);
        Seg("idle2"); await TfwWait(1.0);
        Seg("stomp"); await TfwActionTap(GameAction.StunStrike); await TfwWait(4.2);
        Seg("x-then-y"); await TfwActionTap(GameAction.Melee); await TfwWait(0.3); await TfwActionTap(GameAction.StunStrike); await TfwWait(4.2);
        Seg("sprint"); TfwKey(Key.W, true); TfwKey(Key.Shift, true); await TfwWait(1.0);
        Seg("light-sprint"); await TfwActionTap(GameAction.Melee); await TfwWait(1.2);
        TfwKey(Key.Shift, false); TfwKey(Key.W, false); await TfwWait(1.5);
        _yaw += MathF.PI;
        Seg("sprint2"); TfwKey(Key.W, true); TfwKey(Key.Shift, true); await TfwWait(1.0);
        Seg("charge"); await TfwActionTap(GameAction.StunStrike); await TfwWait(1.0);
        Seg("charge-2"); await TfwWait(2.5);
        TfwKey(Key.Shift, false); TfwKey(Key.W, false);
        Seg("idle3"); await TfwWait(1.5);
        stop = true; await TfwFrames(2);
        string path = Path.Combine(_tfwShots ?? ".", "dn-smooth.csv");
        try { File.WriteAllText(path, csv.ToString()); GD.Print($"DNSMOOTH wrote {path}"); } catch (Exception e) { GD.Print($"DNSMOOTH write failed {e.Message}"); }
        GD.Print($"DNSMOOTH end frame {Engine.GetFramesDrawn()}");
        _dnCam = 0;
        GetTree().Quit(0);
    }
}
