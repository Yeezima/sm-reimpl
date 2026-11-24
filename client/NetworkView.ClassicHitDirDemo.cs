using System;
using Godot;
using SmClient.ImpactRules;
using SmNet;
using SmSim;
using static SmClient.ImpactRules.ClassicHitDirRules;

namespace SmClient;

/// <summary>
/// classic-hit-effects capture aid (a *placeholder demonstration* of the drawing, not of the confirmation path): <c>--hit-dir-demo &lt;case&gt;</c> draws one case every <c>--hit-dir-every</c> s
/// (default 2) on the other player nearest the camera, from the camera's side, through the same spray, sparks, blood hook and decal trace the confirmed hits use, in the match's own profile
/// (Faithful draws no blood or decal). Cases: <c>lr</c>, <c>rl</c>, <c>overhead</c>, <c>diagonal</c>, <c>thrust</c>, <c>combo</c> (lr, rl, diagonal, thrust in turn), <c>gun</c>, <c>heavy</c>,
/// <c>lethal</c>, <c>armour</c>, <c>break</c> (armour broken, no health: sparks), <c>mixed</c>, <c>blood</c>, <c>two</c> (one stroke through the two nearest players), <c>charge</c> (a bull-rush connect from the camera's side, thrown straight on), <c>airslash</c> (the jump pack's air slash: a stroke from high on one side down across him), <c>headshot</c> (a fatal
/// gunfire headshot along the camera's line through his head: the wall decal if a wall is within 5 m behind him; place the camera to test each wall case), <c>wallshot</c> / <c>farshot</c>
/// (a fatal headshot toward the nearest wall within 4.6 m / between 5.4 and 6.3 m of his head, found by probing the level: decal / none), <c>cycle</c> (every case).
/// Each draw prints a <c>HITDIRDEMO</c> line. The real swings and shots are tested in play (bots or a second client) with <c>--log-hit-dir</c>.
/// </summary>
public partial class NetworkView
{
    string? _hitDirDemo;
    double _hitDirDemoAt = double.NegativeInfinity;
    int _hitDirDemoStep, _hitDirDemoKill = 60000;
    static readonly string[] HitDirDemoCycle = ["lr", "rl", "overhead", "diagonal", "thrust", "gun", "heavy", "lethal", "armour", "break", "mixed", "blood", "two", "headshot"];
    static readonly string[] HitDirDemoCombo = ["lr", "rl", "diagonal", "thrust"];

    void ClassicHitDirDemo()
    {
        _hitDirDemo ??= CmdFlag("hit-dir-demo") ? (CmdArg("hit-dir-demo") ?? "cycle") : "";
        if (_hitDirDemo.Length == 0 || _client is null || _config is null) return;
        double every = double.TryParse(CmdArg("hit-dir-every"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double e) && e > 0.2 ? e : 2.0;
        if (_connectedFor - _hitDirDemoAt < every) return;
        var camera = GetViewport().GetCamera3D();
        if (camera is null) return;
        int first = 0, second = 0; float best = float.MaxValue, next = float.MaxValue;
        foreach (var (pid, c) in _remotes)
        {
            if (!GodotObject.IsInstanceValid(c) || !c.IsInsideTree() || !c.Visible) continue;
            float d = c.GlobalPosition.DistanceSquaredTo(camera.GlobalPosition);
            if (d < best) { next = best; second = first; best = d; first = pid; }
            else if (d < next) { next = d; second = pid; }
        }
        if (first == 0) return;
        _hitDirDemoAt = _connectedFor;
        string kind = _hitDirDemo switch
        {
            "cycle" => HitDirDemoCycle[_hitDirDemoStep++ % HitDirDemoCycle.Length],
            "combo" => HitDirDemoCombo[_hitDirDemoStep++ % HitDirDemoCombo.Length],
            _ => _hitDirDemo,
        };
        if (HitDirStage(kind, first)) return;
        if (kind == "charge")
        {
            // a bull-rush connect from the camera's side: thrown straight on along the charge
            PlayerState? cv = null;
            foreach (var p in _lastOthers) if (p.PlayerId == first) { cv = p; break; }
            if (cv is not { } chv) return;
            var cchest = ToGodot(chv.Position + new System.Numerics.Vector3(0f, 0f, 1.25f));
            var ctoCam = camera.GlobalPosition - cchest; ctoCam.Y = 0f;
            ctoCam = ctoCam.LengthSquared() > 1e-4f ? ctoCam.Normalized() : Vector3.Forward;
            // on the fixed side camera (--observe-side) the charge comes from the frame's left, so it crosses the view instead of running along it
            var cright = Vector3.Up.Cross(ctoCam).Normalized();
            var cdir = ObserveSide > 0f ? cright : -ctoCam;
            bool cd = DrawCharge(first, _client.PlayerId, cchest + cdir * BodySurface, cdir, HitVisual.Blood, false);
            GD.Print($"HITDIRDEMO charge on P{first} ({HitDirLook}): drawn {cd} (t {_connectedFor:F2})");
            return;
        }   // wall:<m>, sidewall, blocked, floor (NetworkView.ClassicHitDirStage.cs)
        PlayerState? v = null;
        foreach (var p in _lastOthers) if (p.PlayerId == first) { v = p; break; }
        if (v is not { } victim) return;
        var chest = ToGodot(victim.Position + new System.Numerics.Vector3(0f, 0f, 1.3f));
        var head = BodyMarker(first, "RIG_Head")?.Invoke()?.Position ?? ToGodot(victim.Position + new System.Numerics.Vector3(0f, 0f, 1.75f));
        var toCam = camera.GlobalPosition - chest; toCam.Y = 0f;
        toCam = toCam.LengthSquared() > 1e-4f ? toCam.Normalized() : Vector3.Forward;
        var right = Vector3.Up.Cross(toCam).Normalized();
        var look = HitDirLook;
        HitVisual visual = kind switch { "armour" or "break" => HitVisual.Sparks, "mixed" => HitVisual.Mixed, _ => HitVisual.Blood };
        bool heavy = kind == "heavy", lethal = kind is "lethal" or "headshot";
        if (kind is "gun" or "headshot" or "wallshot" or "farshot")
        {
            var from = camera.GlobalPosition;
            if (ObserveSide > 0f && kind == "gun") from = chest - right * 8f + Vector3.Up * 0.3f;   // the fixed side camera: the shot comes from the frame's left, across the view
            var target = kind == "gun" ? chest + toCam * BodySurface : head;
            var dir = (target - from).Normalized();
            NoteHitDirFocus(target, dir);
            if (kind is "wallshot" or "farshot")
            {
                // the wall cases without placing the camera: the level probed level from the head in 32 directions; wallshot shoots toward the nearest wall within 4.6 m
                // (a decal expected), farshot toward the nearest between 5.4 and 6.3 m (none expected: past the 5 m cutoff); nothing found: no shot this time
                if (WallProbe(head, kind == "wallshot" ? 0.3f : 5.4f, kind == "wallshot" ? 4.6f : 6.3f) is not { } probe) { GD.Print($"HITDIRDEMO {kind} on P{first}: no wall in range (t {_connectedFor:F2})"); return; }
                dir = probe;
                kind = "headshot";
            }
            bool drawn = false;
            ExtrasClassicBlood(new ClassicContact(first, _client.PlayerId, target, dir, visual, true, kind == "headshot", lethal, false, 0.32f, BloodAmount(false, lethal), 0.0, null, 0, null), ref drawn);   // EXTRAS-HOOK: classic-hit-effects, the Extended blood (Extras/Gore)
            if (kind == "headshot")
            {
                ExtrasClassicFatalHeadshot(first, _client.PlayerId, target, dir);   // EXTRAS-HOOK: classic-hit-effects, the Extended blood (Extras/Gore)
                ExtrasClassicKill(new KillView(++_hitDirDemoKill, _client.PlayerId, first, default, 0, default, Headshot: true));   // EXTRAS-HOOK: classic-hit-effects, the Extended blood (Extras/Gore)
            }
            GD.Print($"HITDIRDEMO {kind} on P{first} ({look}): blood {drawn} (t {_connectedFor:F2})");
            return;
        }
        Vector3 motion = kind switch
        {
            "rl" => -right,
            "overhead" => (Vector3.Down + toCam * 0.25f).Normalized(),
            "diagonal" => (right + Vector3.Down).Normalized(),
            "thrust" => ObserveSide > 0f ? right : -toCam,   // the fixed side camera: a thrust from the frame's left, across the view
            "airslash" => (Vector3.Down * 0.8f + right * 0.6f - toCam * 0.2f).Normalized(),   // a leaping slash: from high on one side, down across him
            "curve" => right,   // a sweep whose arc bends up and over in the camera's view (the path's curve is seen on screen)
            _ => right,
        };
        float thrust = kind == "thrust" ? 1f : 0f;
        DemoStroke(first, chest, toCam, motion, visual, heavy, lethal, thrust, look, kind == "curve");
        if (kind == "two" && second != 0)
            foreach (var p in _lastOthers)
                if (p.PlayerId == second) { DemoStroke(second, ToGodot(p.Position + new System.Numerics.Vector3(0f, 0f, 1.3f)), toCam, motion, visual, false, false, 0f, look); break; }
        GD.Print($"HITDIRDEMO {kind} on P{first}{(kind == "two" && second != 0 ? $" and P{second}" : "")} ({look}): {visual} motion {motion.X:F2},{motion.Y:F2},{motion.Z:F2} (t {_connectedFor:F2})");
    }

    /// <summary>The direction (Godot world, horizontal) from <paramref name="head"/> to the nearest wall (abs(normal z) at most 0.5, the decal rule) between <paramref name="near"/> and <paramref name="far"/> m.</summary>
    Vector3? WallProbe(Vector3 head, float near, float far)
    {
        if (_client?.Collision is not { } world || _gameSpace is null) return null;
        var from = ToGame(head);
        Vector3? best = null; float bestD = float.MaxValue;
        for (int i = 0; i < 32; i++)
        {
            float a = i * MathF.Tau / 32f;
            var d = new System.Numerics.Vector3(MathF.Cos(a), MathF.Sin(a), 0f);
            if (world.Weapons.Raycast(from, d, far + 0.5f) is not { } hit || hit.Distance < near || hit.Distance > far || MathF.Abs(System.Numerics.Vector3.Normalize(hit.Normal).Z) > 0.5f) continue;
            if (hit.Distance < bestD) { bestD = hit.Distance; best = DirToGodot(d).Normalized(); }
        }
        if (best is not null) GD.Print($"HITDIRDEMO wall probe: {bestD:F2} m");
        return best;
    }

    /// <summary>One synthetic stroke: a weapon head moving at 12 m/s through the body's surface on the camera's side, bending a little as a swing does.</summary>
    void DemoStroke(int victim, Vector3 chest, Vector3 toCam, Vector3 motion, HitVisual visual, bool heavy, bool lethal, float thrust, DirLook look, bool curveInView = false)
    {
        var contact = chest + toCam * BodySurface;
        // the stroke's points from 0.1 s before the contact to 0.2 s after it (the blood is laid along both sides; the sparks take the part after it)
        var hist = new (double T, Vector3 P)[15];
        var bend = thrust > 0.5f ? Vector3.Zero : curveInView ? Vector3.Down * 5f : toCam * 2.5f;   // a swing arcs round the attacker (the camera's side): before and after the contact it curves back toward him; "curve": an arc over the top, seen side-on
        for (int k = 0; k < hist.Length; k++)
        {
            double t = (k - 4) * 0.02;
            hist[k] = (t, contact + motion * 12f * (float)t + bend * (float)(t * t) * 6f);
        }
        if (thrust > 0.5f)
        {
            // a thrust drives through him: the spray starts on the exit side, as the confirmed path does (lead decision 7)
            contact = chest + motion.Normalized() * BodySurface;   // along the thrust, where it leaves him (review 3: it was put on the far side from the camera)
            for (int k = 0; k < hist.Length; k++) hist[k].P += contact - (chest + toCam * BodySurface);
        }
        float spread = SpreadFor(thrust);
        NoteHitDirFocus(contact, motion);
        if (HasSparks(visual) && look.HasFlag(DirLook.MeleeSparks)) HitSpray().SpawnMelee(contact, new ReadOnlySpan<(double, Vector3)>(hist, 5, hist.Length - 5), null, motion, new ClassicHitSpray.Look(true, 1f, spread * 0.8f), 0.0);
        bool drawn = false;
        if (HasBlood(visual))
            ExtrasClassicBlood(new ClassicContact(victim, _client!.PlayerId, contact, motion, visual, false, false, lethal, heavy, spread, BloodAmount(heavy, lethal), 0.0, hist, hist.Length, null, Exit: thrust > 0.5f), ref drawn);   // EXTRAS-HOOK: classic-hit-effects, the Extended blood (Extras/Gore)
        DemoPowerHit(contact, toCam);
    }

    /// <summary>
    /// Capture aid <c>--hit-dir-demo-power hammer|maul|axe|sword</c> (gore visibility pass, 2026-11-24): each demo stroke also plays that power weapon's hit flash and cloud at the contact,
    /// through the same <see cref="PowerHit"/> a real power-weapon hit uses (outward toward the camera's side, as the real hit's toward the attacker), so the blood can be judged over the glow.
    /// </summary>
    void DemoPowerHit(Vector3 contact, Vector3 toCam)
    {
        if (CmdArg("hit-dir-demo-power") is not { Length: > 0 } w) return;
        string wargear = w switch { "maul" => "csm_mp_melee_2hand_power_mace", "axe" => "sm_mp_melee_power_axe", "sword" => "sm_mp_melee_powersword", _ => "sm_mp_melee_2hand_thunder_hammer" };
        _powerSwing.Forget(-1);
        PowerHit(-1, wargear, contact, toCam + Vector3.Up * 0.3f, "hit-dir demo");
    }
}
