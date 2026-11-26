using System;
using Godot;
using SmClient.ImpactRules;
using SmSim;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// gore-streak (added content: gore; owner 2026-11-25): the shared part of the blood streak for Classic PvP with the Extended gore and for Warfront - the switches, the one
/// <see cref="GoreStreakView"/> per match, the spawn helper (camera bias, Godot to the rules' vectors) and the <c>gore streak:</c> log line. Faithful and Musou alone never make the view.
/// Classic's wiring is in NetworkView.ClassicBlood.cs, Warfront's in NetworkView.WarfrontStreak.cs, the capture aid in NetworkView.GoreStreakCapture.cs.
/// </summary>
public partial class NetworkView
{
    static readonly bool OldGoreStreakFlag = CmdFlag(GoreStreakRules.OldLookFlag);
    static readonly bool NoGoreStreakFlag = CmdFlag(GoreStreakRules.OffFlag);

    /// <summary>Whether this match draws the streak (<see cref="GoreStreakRules.Draws"/>).</summary>
    bool StreakDraws => GoreStreakRules.Draws(MatchExtras, OldGoreStreakFlag, NoGoreStreakFlag);

    GoreStreakView? _goreStreaks;
    int _streakSeed;

    GoreStreakView GoreStreaks()
    {
        if (_goreStreaks is null || !GodotObject.IsInstanceValid(_goreStreaks))
        {
            _goreStreaks = new GoreStreakView { Name = "GoreStreakView" };
            AddChild(_goreStreaks);
            if (MatchExtras.HasFlag(ContentExtras.Warfront))
            {
                _goreStreaks.SetNearFade(GoreStreakRules.WarfrontNearFadeFrom, GoreStreakRules.WarfrontNearFadeTo);
                _goreStreaks.BeforeFrame = StreakWarfrontFlush;
            }
        }
        return _goreStreaks;
    }

    static Num.Vector3 GsN(Vector3 v) => new(v.X, v.Y, v.Z);
    static Vector3 GsG(Num.Vector3 v) => new(v.X, v.Y, v.Z);

    /// <summary>The body's radius as a share of its height (ours: about 0.44 m for a 2 m marine).</summary>
    const float BodyRadiusShare = 0.22f;

    /// <summary>The next streak seed (from 1).</summary>
    int NextStreakSeed() => ++_streakSeed;

    /// <summary>
    /// Starts one streak at <paramref name="origin"/> (Godot world) along <paramref name="axis"/> on the plane of <paramref name="normal"/> (turned toward the camera,
    /// <see cref="GoreStreakRules.CameraBias"/>), <paramref name="bodyHeight"/> m tall victim, <paramref name="scale"/>; on <paramref name="victim"/> (0: none known; one streak per victim at a time:
    /// a later hit holds or replaces the victim's live streak, <see cref="GoreStreakRules.JoinOf"/>, never stacks a second); <paramref name="unitCap"/> the largest metres per table unit.
    /// </summary>
    void SpawnStreak(Vector3 origin, Vector3 axis, Vector3 normal, float bodyHeight, float scale, float preAge, float bend, bool lethal, bool heavy, int seed, GoreStreakRules.Source source = GoreStreakRules.Source.Gunfire,
        int victim = 0, bool own = false, float unitCap = GoreStreakRules.MaxUnitMetres)
    {
        if (axis.LengthSquared() < 1e-8f) return;
        var ax = Num.Vector3.Normalize(GsN(axis));
        var n = GsN(normal);
        float facing = 0.5f;
        if (_camera is { } cam && GodotObject.IsInstanceValid(cam))
        {
            // the wound's side toward the camera (gate repair 3): pushed out by the body's radius (of the capped unit, finding L4), so the root is not buried inside the body drawn over it
            var toCam = cam.GlobalPosition - origin; toCam.Y = 0f;
            if (toCam.LengthSquared() > 1e-4f) origin += toCam.Normalized() * (BodyRadiusShare * MathF.Min(bodyHeight, unitCap));
            n = GoreStreakRules.CameraBias(n, ax, GsN(cam.GlobalPosition - origin));
            var tc = cam.GlobalPosition - origin;
            if (tc.LengthSquared() > 1e-6f) facing = MathF.Abs(Num.Vector3.Dot(Num.Vector3.Normalize(n), Num.Vector3.Normalize(GsN(tc))));
        }
        var streak = new GoreStreakRules.Streak(GsN(origin), ax, n, bodyHeight, scale, seed, preAge, Math.Clamp(bend, -0.4f, 0.4f), lethal, heavy,
            GoreStreakRules.WidthShareOf(source, facing, heavy && !lethal), GoreStreakRules.EntryShareOf(source), unitCap);
        _lastStreakOrigin = origin; _lastStreakAxis = GsG(ax); _lastStreakUnit = streak.Unit; _lastStreakFacing = facing;   // for the capture aid's capture.json
        GoreStreaks().Spawn(streak, victim, own);
    }

    /// <summary>The plane normal for a streak along <paramref name="axis"/> (Godot world; <see cref="GoreStreakRules.Plane"/> with Y up).</summary>
    static Vector3 StreakPlane(GoreStreakRules.Source source, Vector3 axis, int seed, Vector3? before = null, Vector3? after = null) =>
        GsG(GoreStreakRules.Plane(source, GsN(axis), Num.Vector3.UnitY, seed, before is { } b ? GsN(b) : null, after is { } a ? GsN(a) : null));
}
