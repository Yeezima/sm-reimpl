using System;
using System.Globalization;
using Godot;
using SmNet;
using SmSim;
using static SmClient.ImpactRules.ClassicHitDirRules;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// classic-hit-effects test aids for the tester's visual checks (client only, nothing sent; brief 2026-11-23 rev 1, coordinator's request of 2026-11-24):
/// <list type="bullet">
/// <item><b>Staged fatal headshots</b> (<c>--hit-dir-demo wall:&lt;m&gt;|sidewall|blocked|floor</c>): a *placeholder demonstration* of the decal rule at a known wall. The level is probed
/// level from the nearest other player's head (32 directions, up to 25 m) for the nearest wall with open floor in front of it; a staged head is put that many metres out from the wall
/// (no player moves: the server is authoritative and the brief allows no protocol change) and a fatal gunfire headshot is fired at the wall through it, through the same spray, decal
/// trace and placement as a real one. <c>sidewall</c>: 1 m out, shot along the wall (the wall is beside the line). <c>blocked</c>: the nearest other player stands between the staged
/// head (1.2 m in front of him) and a wall within 3.5 m behind him. <c>floor</c>: 2 m out, shot 45 degrees down. The victim is a staged id (<see cref="StagedVictim"/>), so no real
/// player is skipped by the trace. Each prints <c>HITDIRDEMO stage ...</c> and the decal result line.</item>
/// <item><b>Action camera</b> (<c>--observe-action</c>, with <c>--observe</c>): the observer frames the latest drawn hit effect (a demo, staged or confirmed one, held 2.5 s) from its
/// side, else the nearest pair of enemies within 8 m, 4.5 m away and a little above, eased; without either it falls back to the follow or free camera.</item>
/// </list>
/// </summary>
public partial class NetworkView
{
    /// <summary>The player id the staged headshots name (no real player has it).</summary>
    const int StagedVictim = 65000;

    /// <summary>The staged <c>blocked</c> case without a real player near a wall: a body this far along the staged shot's line, counted by the decal trace for that one shot (m).</summary>
    float? _stagedBlocker;

    static readonly bool ObserveAction = CmdFlag("observe-action");
    (Vector3 At, Vector3 Dir, double T)? _hitDirFocus;
    Vector3 _actionEye, _actionLook;
    bool _actionPlaced;
    double _actionPairAt = double.NegativeInfinity;
    Vector3 _actionPairAtPos, _actionPairDir;

    /// <summary>The latest drawn hit effect, for the action camera.</summary>
    void NoteHitDirFocus(Vector3 at, Vector3 dir) => _hitDirFocus = (at, dir, _connectedFor);

    /// <summary>A staged decal case; false when <paramref name="kind"/> is not one.</summary>
    bool HitDirStage(string kind, int nearest)
    {
        bool wall = kind.StartsWith("wall:", StringComparison.Ordinal);
        if (!wall && kind is not ("sidewall" or "blocked" or "floor")) return false;
        if (_client?.Collision is not { } world || _gameSpace is null) return true;
        PlayerState? near = null;
        foreach (var p in _lastOthers) if (p.PlayerId == nearest) { near = p; break; }
        if (near is not { } r) { GD.Print($"HITDIRDEMO stage {kind}: no other player to stage near (t {_connectedFor:F2})"); return true; }
        var head = r.Position + new Num.Vector3(0f, 0f, 1.7f);
        Vector3 from, dir;
        string note;
        if (kind == "blocked")
        {
            if (Probe(world, head, 0.4f, 3.5f, 0f, 0f, null) is { } w)
            {
                var u = Num.Vector3.Normalize(new Num.Vector3(w.Point.X - head.X, w.Point.Y - head.Y, 0f));
                var staged = head - u * 1.2f;
                from = ToGodot(staged); dir = DirToGodot(u).Normalized();
                note = $"P{r.PlayerId} between the staged head and a wall {w.Distance:F2} m behind him";
            }
            else if (Probe(world, head, 0.3f, 25f, 3.4f, 3f, "wall") is { } w2)
            {
                // no wall near the player (an open spawn, tester round 2): a staged head 3 m square in front of a wall and a staged body 1.5 m along the line between them
                var n = Num.Vector3.Normalize(new Num.Vector3(w2.Normal.X, w2.Normal.Y, 0f));
                if (Num.Vector3.Dot(n, w2.Point - head) > 0f) n = -n;
                var staged = new Num.Vector3(w2.Point.X, w2.Point.Y, head.Z) + n * 3f;
                from = ToGodot(staged); dir = DirToGodot(-n).Normalized();
                _stagedBlocker = 1.5f;
                note = "a staged body 1.5 m along the line to a wall 3 m away (no wall within 3.5 m behind a real player)";
            }
            else { GD.Print($"HITDIRDEMO stage blocked: no wall near P{r.PlayerId} and none within 25 m with 3.4 m of open floor (t {_connectedFor:F2})"); return true; }
        }
        else
        {
            float d = 1f;
            if (wall && !float.TryParse(kind[5..], NumberStyles.Float, CultureInfo.InvariantCulture, out d)) { GD.Print($"HITDIRDEMO stage {kind}: expected wall:<metres> (t {_connectedFor:F2})"); return true; }
            if (kind == "floor") d = 2f;
            if (kind == "sidewall") d = 1f;
            if (Probe(world, head, 0.3f, 25f, d + 0.4f, d, kind) is not { } w) { GD.Print($"HITDIRDEMO stage {kind}: no wall within 25 m of P{r.PlayerId} fits the case ({d:F1} m of open floor in front, the line clear of players{(kind == "sidewall" ? ", nothing ahead on it" : kind == "floor" ? ", a floor first on it" : "")}) (t {_connectedFor:F2})"); return true; }
            var n = Num.Vector3.Normalize(new Num.Vector3(w.Normal.X, w.Normal.Y, 0f));
            if (Num.Vector3.Dot(n, w.Point - head) > 0f) n = -n;   // out of the wall, toward the probe
            var staged = new Num.Vector3(w.Point.X, w.Point.Y, head.Z) + n * d;
            var u = -n;
            if (kind == "sidewall") u = Num.Vector3.Normalize(Num.Vector3.Cross(n, Num.Vector3.UnitZ));
            if (kind == "floor") u = Num.Vector3.Normalize(-n + new Num.Vector3(0f, 0f, -1f));
            from = ToGodot(staged); dir = DirToGodot(u).Normalized();
            note = kind == "sidewall" ? "1 m from a wall, shot along it" : kind == "floor" ? "2 m from a wall, shot 45 degrees down" : $"{d:F2} m in front of a wall, shot square at it";
        }
        GD.Print($"HITDIRDEMO stage {kind}: staged head {note} (t {_connectedFor:F2})");
        bool drawn = false;
        ExtrasClassicBlood(new ClassicContact(StagedVictim, _client.PlayerId, from, dir, HitVisual.Blood, true, true, true, false, 0.32f, BloodAmount(false, true), 0.0, null, 0, null), ref drawn);   // EXTRAS-HOOK: classic-hit-effects, the Extended blood (Extras/Gore)
        ExtrasClassicFatalHeadshot(StagedVictim, _client.PlayerId, from, dir);   // EXTRAS-HOOK: classic-hit-effects, the Extended blood (Extras/Gore)
        ExtrasClassicKill(new KillView(++_hitDirDemoKill, _client.PlayerId, StagedVictim, default, 0, default, Headshot: true));   // EXTRAS-HOOK: classic-hit-effects, the Extended blood (Extras/Gore)
        _stagedBlocker = null;
        NoteHitDirFocus(from, dir);
        // the fixed side camera (--observe-side) moves once, to the first staged line: side-on to the staged head and the wall it shoots at, so the decal is in view
        if (ObserveSide > 0f && !_sideCameraOnStage)
        {
            _sideCameraOnStage = true;
            var wallAt = from + dir * (kind.StartsWith("wall:", StringComparison.Ordinal) && float.TryParse(kind[5..], NumberStyles.Float, CultureInfo.InvariantCulture, out var wd) ? wd : kind == "blocked" ? 3f : 2f);
            var mid = (from + wallAt) * 0.5f;
            var side = dir.Cross(Vector3.Up).Normalized();
            var eye = mid + side * MathF.Max(ObserveSide, from.DistanceTo(wallAt) * 0.9f) - dir * 0.6f + Vector3.Up * 0.25f;
            if (_mapNote is not null) eye = Unblocked(mid, eye);
            _sideCamera = new Transform3D(Basis.LookingAt((mid - eye).Normalized(), Vector3.Up), eye);
            GD.Print($"observe-side: camera moved side-on to the staged line ({kind}) at {eye.X:F2},{eye.Y:F2},{eye.Z:F2}");
        }
        return true;
    }

    /// <summary>
    /// The nearest wall (abs(normal z) at most 0.5) seen level from <paramref name="from"/> between <paramref name="near"/> and <paramref name="far"/> m, with <paramref name="room"/> m of open space in
    /// front of it and, for a staged <paramref name="kind"/>, no living player within 1 m of the staged shot's line (<paramref name="stand"/> m out from the wall), so a wall case is not blocked by chance.
    /// </summary>
    RayHit? Probe(CollisionWorld world, Num.Vector3 from, float near, float far, float room, float stand, string? kind)
    {
        RayHit? best = null;
        for (int i = 0; i < 32; i++)
        {
            float a = i * MathF.Tau / 32f;
            var d = new Num.Vector3(MathF.Cos(a), MathF.Sin(a), 0f);
            if (world.Weapons.Raycast(from, d, far) is not { } hit || hit.Distance < near || MathF.Abs(Num.Vector3.Normalize(hit.Normal).Z) > 0.5f) continue;
            if (room > 0f)
            {
                var n = Num.Vector3.Normalize(new Num.Vector3(hit.Normal.X, hit.Normal.Y, 0f));
                if (Num.Vector3.Dot(n, d) > 0f) n = -n;
                var start = hit.Point + n * 0.05f;
                if (world.Weapons.Raycast(start, n, room) is not null) continue;   // something closer than room in front of the wall
                if (kind is not null)
                {
                    var staged = new Num.Vector3(hit.Point.X, hit.Point.Y, from.Z) + n * stand;
                    var u = kind == "sidewall" ? Num.Vector3.Normalize(Num.Vector3.Cross(n, Num.Vector3.UnitZ)) : kind == "floor" ? Num.Vector3.Normalize(-n + new Num.Vector3(0f, 0f, -1f)) : -n;
                    if (PlayerNearLine(staged, u, 6.5f)) continue;
                    var ahead = world.Weapons.Raycast(staged, u, 6.5f);
                    if (kind == "sidewall" && ahead is not null) continue;   // the wall only beside the line: nothing ahead on it
                    if (kind == "floor" && (ahead is not { } fl || MathF.Abs(Num.Vector3.Normalize(fl.Normal).Z) <= 0.5f)) continue;   // the line meets a floor first
                }
            }
            if (best is null || hit.Distance < best.Value.Distance) best = hit;
        }
        return best;
    }

    bool PlayerNearLine(Num.Vector3 a, Num.Vector3 u, float length)
    {
        foreach (var p in _lastOthers)
        {
            if (!p.Alive) continue;
            var c = p.Position + new Num.Vector3(0f, 0f, 1f);
            float t = Math.Clamp(Num.Vector3.Dot(c - a, u), 0f, length);
            var q = a + u * t;
            var off = c - q; off.Z = MathF.Max(0f, MathF.Abs(off.Z) - 0.9f);
            if (off.Length() < 1f) return true;
        }
        return false;
    }

    static readonly float ObserveSide = float.TryParse(CmdArg("observe-side"), NumberStyles.Float, CultureInfo.InvariantCulture, out var side) && side > 0.5f ? side : 0f;
    static readonly bool CaptureFill = CmdFlag("capture-fill");
    Transform3D? _sideCamera;
    bool _sideCameraOnStage;

    /// <summary>
    /// Capture aid (dev only) <c>--observe-side &lt;m&gt;</c>: a FIXED camera, placed once, that many metres to the right of the nearest other player (the demo victim), at chest height
    /// and looking at his chest, so a <c>--hit-dir-demo</c> stroke crosses the frame side-on; it never moves after. <c>--capture-fill</c> adds a white fill light at the camera (the
    /// spawn rooms are dark; a capture light, not the map's).
    /// </summary>
    bool HitDirSideCamera(System.Collections.Generic.IReadOnlyList<PlayerState> others)
    {
        if (ObserveSide <= 0f || _camera is null || _gameSpace is null) return false;
        if (_sideCamera is null)
        {
            PlayerState? v = null;
            foreach (var o in others) if (o.Alive) { v = o; break; }
            if (v is not { } victim) return false;
            var chest = SpectateG(victim.Position + new Num.Vector3(0f, 0f, 1.25f));
            var facing = DirToGodot(new Num.Vector3(-MathF.Sin(victim.Yaw), MathF.Cos(victim.Yaw), 0f)).Normalized();
            var right = facing.Cross(Vector3.Up).Normalized();
            var eye = chest + right * ObserveSide + Vector3.Up * 0.2f;
            if (_mapNote is not null) eye = Unblocked(chest, eye);
            _sideCamera = new Transform3D(Basis.LookingAt((chest - eye).Normalized(), Vector3.Up), eye);
            GD.Print($"observe-side: fixed camera {ObserveSide:F1} m to the right of P{victim.PlayerId} at {eye.X:F2},{eye.Y:F2},{eye.Z:F2}");
            if (CaptureFill)
            {
                var fill = new OmniLight3D { Name = "CaptureFill", LightEnergy = 6f, OmniRange = 10f, ShadowEnabled = false, LightColor = Colors.White };
                AddChild(fill);
                fill.GlobalPosition = eye + Vector3.Up * 0.8f;
            }
        }
        _camera.GlobalTransform = _sideCamera.Value;
        return true;
    }

    /// <summary>The action camera (<c>--observe-action</c>): true when it placed the camera this frame.</summary>
    bool HitDirActionCamera(System.Collections.Generic.IReadOnlyList<PlayerState> others, float delta)
    {
        if (!ObserveAction || _camera is null || _gameSpace is null) return false;
        Vector3 focus, dir;
        if (_hitDirFocus is { } f && _connectedFor - f.T <= 2.5) { focus = f.At; dir = f.Dir; }
        else
        {
            if (_connectedFor - _actionPairAt > 1.5)
            {
                // the nearest pair of living enemies within 8 m, re-chosen at most every 1.5 s
                float best = 8f; bool found = false;
                for (int i = 0; i < others.Count; i++)
                    for (int j = i + 1; j < others.Count; j++)
                    {
                        var a = others[i]; var b = others[j];
                        if (!a.Alive || !b.Alive || a.Team == b.Team) continue;
                        float d = Num.Vector3.Distance(a.Position, b.Position);
                        if (d < best) { best = d; found = true; _actionPairAtPos = SpectateG((a.Position + b.Position) * 0.5f) + Vector3.Up * 1.2f; _actionPairDir = SpectateG(b.Position) - SpectateG(a.Position); }
                    }
                if (!found) return false;
                _actionPairAt = _connectedFor;
            }
            focus = _actionPairAtPos; dir = _actionPairDir;
        }
        dir.Y = 0f;
        dir = dir.LengthSquared() > 1e-6f ? dir.Normalized() : Vector3.Forward;
        var side = dir.Cross(Vector3.Up).Normalized();
        var cam = _camera.GlobalPosition;
        if ((cam - focus).Dot(side) < 0f) side = -side;   // stay on the side the camera is on
        var eye = focus + side * 4.5f + Vector3.Up * 0.9f;
        if (_mapNote is not null) eye = Unblocked(focus, eye);
        float k = _actionPlaced ? 1f - MathF.Exp(-5f * delta) : 1f;
        _actionPlaced = true;
        _actionEye = _actionEye.Lerp(eye, k);
        _actionLook = _actionLook.Lerp(focus, k);
        var look = _actionLook - _actionEye;
        if (look.LengthSquared() < 1e-4f) look = -side;
        _camera.GlobalTransform = new Transform3D(Basis.LookingAt(look.Normalized(), Vector3.Up), _actionEye);
        return true;
    }
}
