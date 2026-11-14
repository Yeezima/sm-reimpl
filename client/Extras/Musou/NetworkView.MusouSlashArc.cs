using System;
using System.Collections.Generic;
using Godot;
using SmNet.Extras.Musou;
using SmSim;
using SmSim.Extras.Musou;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// Musou Mode's swing arc (owner, 2026-11-10; <see cref="MusouSlashArcRules"/>): one flat curved line per melee swing, as wide as its cleave and through the enemies it struck, in place of a stroke
/// on every enemy struck.
/// <list type="bullet">
/// <item>The own swing: drawn at the predicted strike (<see cref="MusouOwnStrikeContact"/>, every melee weapon) when the client sees an enemy in the cleave (<see cref="MusouStrikeTargets"/>, the
/// cleave's shape, the spear's sweep too), from the strike's pose, through the enemies it sees. The server's report of that swing adds no second arc. If the client saw no enemy but the server
/// struck one, the arc comes with the report, still from the strike's pose. A report counts as the swing's only within <see cref="MusouContactRules.MatchSeconds"/> of the strike, while a melee
/// step is still in progress and no charge is.</item>
/// <item>Another player's swing (an AI Marine's too): drawn once the frame's reported melee hits (marker 3) are all in. All hits of one cleave share a server tick: one arc per player and tick,
/// from that player's newest state (position, heading, step), through the enemies reported struck. A player's arc still waiting from an earlier tick when a later tick's hit comes (a backlog
/// of reports after a hitch) is drawn first, never dropped.</item>
/// <item>A melee hit with no swing behind it (a charge's contact, the spear's impale, the air slash's landing, anyone the client has no state for) keeps the old stroke on that enemy.</item>
/// </list>
/// The arc's width is the cleave's (<see cref="MusouCleaveShape"/>); its colour, the 30 m draw limit (from the swinger) and <c>--no-musou-slash</c> are the strokes'. Switches:
/// <c>--musou-old-slashes</c> (the strokes on every enemy struck, as before), test aids <c>--musou-slash-arc-radius f</c> (the line at f times the cleave's reach instead of through the enemies
/// struck) and <c>--log-musou-arcs</c>.
/// </summary>
public partial class NetworkView
{
    static readonly bool MusouOldSlashes = CmdFlag("musou-old-slashes");
    static readonly bool LogMusouArcs = CmdFlag("log-musou-arcs");
    static readonly float MusouSlashArcReachShare = CmdFloat("musou-slash-arc-radius") is var share && float.IsFinite(share) && share > 0f ? share : 0f;

    /// <summary>A swing seen in the server's hits of this frame, drawn at the frame's end: its pose, step, and the sums of its struck enemies' flat distances and chest heights.</summary>
    sealed class PendingArc
    {
        public uint Tick;
        public Num.Vector3 Feet;
        public float Yaw;
        public MeleeStep Step;
        public bool Heavy;
        public int StepIndex;
        public string? Wargear;
        public (string Clip, float Scale)[]? Clips;
        public float Distance, Height;
        public int Hits;
    }

    MusouSlashArcView? _musouArcView;
    readonly Dictionary<ushort, PendingArc> _musouPendingArcs = new();
    readonly Dictionary<ushort, (uint Tick, int StepIndex, bool Heavy, double At)> _musouArcLast = new();   // each player's last drawn swing
    (double At, Num.Vector3 Feet, float Yaw, MeleeStep Step, bool Heavy, int StepIndex, string? Wargear, bool Drawn, (string Clip, float Scale)[] Clips)? _musouOwnSwing;

    /// <summary>When the own player was last seen in a charge or a landing (its rush phase set); -1 before any.</summary>
    double _musouOwnRushAt = -1;

    /// <summary>Each frame: notes the own charge or landing, so a hit the server reports after it is never taken for the swing before it.</summary>
    void MusouArcOwnFrame(PlayerState me) { if (me.Rush != RushPhase.None) _musouOwnRushAt = _connectedFor; }
    bool _musouArcFlushQueued;
    int _musouArcs;

    bool MusouArcsOn => !MusouOldSlashes && !NoMusouSlash;

    /// <summary>The own strike's hit time (prediction): remembers the swing's pose and draws its arc if the client sees an enemy in the cleave (through those it sees).</summary>
    void MusouOwnSwingArc(PlayerState me, MeleeStep step, string? wargear)
    {
        _musouLastStrikeAt = _connectedFor;   // the capture aid's clock (Tfw/NetworkView.TfwArcCapture.cs)
        if (!MusouArcsOn || _client is null || _camera is null || _gameSpace is null || _config is null) return;
        _musouOwnSwing = (_connectedFor, me.Position, me.Yaw, step, me.MeleeHeavy, me.MeleeStep, wargear, false, MusouArcClips(me, step));
        MusouStrikeTargets(me, step, wargear);   // the cleave's shape (the spear's sweep too); the contact below reuses this pick
        if (_musouStrikeTargets.Count == 0) return;
        float distance = 0f, height = 0f;
        foreach (var t in _musouStrikeTargets) { distance += t.Distance; height += t.Chest.Z - me.Position.Z; }
        distance /= _musouStrikeTargets.Count;
        height /= _musouStrikeTargets.Count;
        MusouDrawArc(me.Position, me.Yaw, step, me.MeleeHeavy, me.MeleeStep, wargear, distance, height, _client.PlayerId, _musouStrikeTargets.Count, "seen", _musouOwnSwing.Value.Clips);
        _musouArcLast[(ushort)_client.PlayerId] = (0u, me.MeleeStep, me.MeleeHeavy, _connectedFor);
        _musouOwnSwing = _musouOwnSwing.Value with { Drawn = true };
    }

    /// <summary>
    /// A melee hit (marker 3) the server reports on the enemy whose chest is at <paramref name="at"/> (game space): true when the swing's arc stands for it (drawn already, or gathered to be
    /// drawn at the frame's end); false when there is no swing to draw (the caller then draws the old stroke).
    /// </summary>
    bool MusouHitArc(in MusouEvent e, Num.Vector3 at, Num.Vector3 camera, bool own, PlayerState me)
    {
        if (!MusouArcsOn || _config is null) return false;
        if (_musouPendingArcs.TryGetValue(e.Player, out var pending))
        {
            if (pending.Tick == e.Tick) { MusouArcAdd(pending, at); return true; }
            // Review: an arc of this player still waiting from an earlier tick (a backlog of reports after a hitch) is drawn now, not replaced: its swing keeps its line.
            _musouPendingArcs.Remove(e.Player);
            MusouDrawPending(e.Player, pending, "reported, backlog");
        }
        if (_musouArcLast.TryGetValue(e.Player, out var last) && last.Tick == e.Tick) return true;   // this swing's arc is drawn
        PendingArc? swing = null;
        // The own swing's report: no charge or landing now or since the swing's strike (review: a charge's contact or the spear's impale within the window is no swing's, and keeps
        // its stroke). The step itself may have ended: the report comes a round trip after the predicted strike, often after a short step's tail or an evade (re-review).
        if (own && me.Alive && me.Rush == RushPhase.None && _musouOwnSwing is { } mine && _musouOwnRushAt < mine.At && _connectedFor - mine.At < MusouContactRules.MatchSeconds)
        {
            if (mine.Drawn) { _musouArcLast[e.Player] = (e.Tick, mine.StepIndex, mine.Heavy, _connectedFor); return true; }   // drawn at the predicted strike
            swing = new PendingArc { Feet = mine.Feet, Yaw = mine.Yaw, Step = mine.Step, Heavy = mine.Heavy, StepIndex = mine.StepIndex, Wargear = mine.Wargear, Clips = mine.Clips };
            _musouOwnSwing = mine with { Drawn = true };
        }
        else
        {
            PlayerState? who = own ? me : MusouPlayerState(e.Player);
            if (who is not { } s || !s.Alive || s.MeleeStep <= 0 || s.Rush != RushPhase.None || Combat.MeleeStepOf(_config, s) is not { } step) return false;   // a charge, the air slash's landing, no state
            // The same step reported again within a moment is the same swing (its cleave reported over two server ticks).
            if (_musouArcLast.TryGetValue(e.Player, out last) && last.StepIndex == s.MeleeStep && last.Heavy == s.MeleeHeavy && _connectedFor - last.At < MusouSlashArcRules.SameSwingSeconds)
            { _musouArcLast[e.Player] = last with { Tick = e.Tick }; return true; }
            swing = new PendingArc { Feet = s.Position, Yaw = s.Yaw, Step = step, Heavy = s.MeleeHeavy, StepIndex = s.MeleeStep, Wargear = MeleeWargear(s), Clips = MusouArcClips(s, step) };
        }
        swing.Tick = e.Tick;
        MusouArcAdd(swing, at);
        _musouPendingArcs[e.Player] = swing;
        if (!_musouArcFlushQueued) { _musouArcFlushQueued = true; Callable.From(MusouFlushArcs).CallDeferred(); }   // after the frame's events: every hit of the swing is in
        return true;
    }

    static void MusouArcAdd(PendingArc arc, Num.Vector3 chest)
    {
        arc.Distance += new Num.Vector2(chest.X - arc.Feet.X, chest.Y - arc.Feet.Y).Length();
        arc.Height += chest.Z - arc.Feet.Z;
        arc.Hits++;
    }

    /// <summary>Draws the swings gathered from this frame's reported hits.</summary>
    void MusouFlushArcs()
    {
        _musouArcFlushQueued = false;
        if (_musouPendingArcs.Count == 0) return;
        foreach (var (player, a) in _musouPendingArcs) MusouDrawPending(player, a, "reported");
        _musouPendingArcs.Clear();
    }

    /// <summary>Draws one gathered swing of <paramref name="player"/> and notes it as that player's last drawn swing.</summary>
    void MusouDrawPending(ushort player, PendingArc a, string source)
    {
        int n = Math.Max(1, a.Hits);
        MusouDrawArc(a.Feet, a.Yaw, a.Step, a.Heavy, a.StepIndex, a.Wargear, a.Distance / n, a.Height / n, player, a.Hits, source, a.Clips);
        _musouArcLast[player] = (a.Tick, a.StepIndex, a.Heavy, _connectedFor);
    }

    /// <summary>
    /// One arc round the swinger's feet <paramref name="feet"/> (game space) along heading <paramref name="yaw"/>, for <paramref name="step"/>, through enemies struck at a mean flat distance
    /// <paramref name="struck"/> and a mean chest height <paramref name="height"/> above the feet.
    /// </summary>
    void MusouDrawArc(Num.Vector3 feet, float yaw, MeleeStep step, bool heavy, int stepIndex, string? wargear, float struck, float height, int attacker, int hits, string source, (string Clip, float Scale)[]? clips = null)
    {
        if (_camera is null || _gameSpace is null || _config is null || _client is null) return;
        if (Num.Vector3.Distance(feet, ToGame(_camera.GlobalPosition)) > MusouSlashRules.Metres) return;   // the strokes' 30 m limit (owner, 2026-11-10), from the swinger
        var shape = MusouCleaveShape.Of(_config, step, _tfwState.War is not null, wargear, human: SmSim.Extras.Musou.Campaign.TfwCombatRules.ScalesPlayer(attacker));   // an AI Marine's arc keeps the reach before the owner's x0.6 (2026-11-11)
        float radius = MusouSlashArcRules.Radius(shape, struck, MusouSlashArcReachShare);
        if (!float.IsFinite(height) || height < 0.3f || height > 2.5f) height = MusouSlashArcRules.DefaultHeight;
        var centre = feet + new Num.Vector3(0f, 0f, height);
        var forward = new Num.Vector3(-MathF.Sin(yaw), MathF.Cos(yaw), 0f);   // the cleave's heading (World.MusouCleave)
        var left = new Num.Vector3(-MathF.Cos(yaw), -MathF.Sin(yaw), 0f);
        var origin = ToGodot(centre);
        var gForward = (ToGodot(centre + forward) - origin).Normalized();
        var gLeft = (ToGodot(centre + left) - origin).Normalized();
        string? look = MusouSlashWeaponArg ?? wargear;
        var colour = MusouSlashRules.ColourOf(look);
        var style = MusouOldContact ? MusouContactRules.DefaultSlash : MusouContactRules.SlashOf(look);
        CharacterViewer? model = attacker == 0 ? null : attacker == _client.PlayerId ? _local : (_remotes.TryGetValue(attacker, out var viewer) ? viewer : null);
        if (_musouArcView is null)
        {
            _musouArcView = new MusouSlashArcView();
            AddChild(_musouArcView);
        }
        int direction = MusouSlashArcRules.Direction(heavy, stepIndex);
        var path = MusouArcPathOf(model, clips, step, wargear, shape.HalfAngleDegrees, out string pathNote);
        _musouArcView.Add(origin, gForward, gLeft, shape, radius, MusouSlashArcRules.Width(heavy, stepIndex), direction, colour, style.Brightness, attacker, model, own: attacker == _client.PlayerId, path: path);
        _musouArcs++;
        if (LogMusouArcs)
            GD.Print($"MUSOU ARC t {_connectedFor:F3} player {attacker} step {stepIndex}{(heavy ? " heavy" : "")} {look ?? "-"}: reach {shape.Reach:F2} m, span {shape.SpanDegrees:F0} deg, " +
                     $"radius {radius:F2} m (struck at {struck:F2}), height {height:F2} m, {(path is null ? (direction > 0 ? "right to left" : "left to right") : "tip path")}, {colour}, {hits} {source}; {pathNote}");
    }

    /// <summary>The fitted path's segments (the view draws one quad each).</summary>
    const int MusouArcPathSegments = 36;

    /// <summary>Test aid: <c>--no-musou-arc-path</c> draws every arc as the level circle segment again (before 2026-11-14).</summary>
    static readonly bool NoMusouArcPath = CmdFlag("no-musou-arc-path");

    /// <summary>The fitted tip path of each animation and weapon (null: the animation gave none, the level arc is drawn). Sampled once, then kept.</summary>
    readonly Dictionary<(string Clip, string Wargear, int Half), MusouSlashArcPath.Path?> _musouArcPaths = new();   // keys lower case

    /// <summary>
    /// The clips a player's swing may be showing, each with the rate of its time to the step's (a clip of the added content's own, the weapon's base clip, the reach variants the swing blends from),
    /// so the arc can read the one the swinger is posed with.
    /// </summary>
    (string Clip, float Scale)[] MusouArcClips(PlayerState s, MeleeStep step)
    {
        var list = new List<(string, float)>(4);
        if (_assets is null) return [];
        if (OwnStrikeClip(s, 1f) is { } custom) list.Add((custom.Clip, custom.Time));
        if (AttackClip(s) is { } baseClip) list.Add((baseClip, MeleeClipSpeed(s)));
        if (step.Reach is { } reach && step.Duration > 1e-4f)
            foreach (var v in reach.Variants)
                if (v.Clip.Length > 0 && _assets.Animation(v.Clip) is { } anim) list.Add((v.Clip, anim.Animation.Duration / step.Duration));
        return list.ToArray();
    }

    /// <summary>
    /// The arc's path for a swing: the weapon tip's path through the swing's animation (the clip the swinger is posed with when it is one of <paramref name="clips"/>, else the first), over the
    /// strike's active window, fitted by <see cref="MusouSlashArcPath.Fit"/>; sampled once per clip and weapon. Null (the level arc) when it cannot be had.
    /// </summary>
    MusouSlashArcPath.Path? MusouArcPathOf(CharacterViewer? model, (string Clip, float Scale)[]? clips, MeleeStep step, string? wargear, float halfAngle, out string note)
    {
        note = "level";
        if (NoMusouArcPath) return null;
        if (model is null || clips is null || clips.Length == 0 || _assets is null) { note = "level (no model or clip)"; return null; }
        var pick = clips[0];
        string? posed = model.PosedClip;
        foreach (var c in clips) if (posed is not null && string.Equals(posed, c.Clip, StringComparison.OrdinalIgnoreCase)) { pick = c; break; }
        var key = (pick.Clip.ToLowerInvariant(), (wargear ?? "").ToLowerInvariant(), (int)MathF.Round(halfAngle));
        if (_musouArcPaths.TryGetValue(key, out var known)) { note = known is null ? $"level ({pick.Clip}: no tip path)" : $"{pick.Clip} (cached)"; return known; }
        if (_assets.Animation(pick.Clip) is not { } anim || !MusouSlashArcPath.Window(step.HitTime * pick.Scale, anim.Animation.Duration, out float from, out float to)) { note = $"level ({pick.Clip}: no window)"; return null; }
        if (model.SampleMeleeTip(pick.Clip, from, to, MusouSlashArcPath.Samples) is not { } sampled) { note = $"level ({pick.Clip}: no weapon in hand yet)"; return null; }   // not kept: asked again at the next swing
        var path = MusouSlashArcPath.Fit(sampled.Tip, sampled.Chest, MusouArcPathSegments, halfAngle);
        _musouArcPaths[key] = path;
        note = path is null
            ? $"level ({pick.Clip}: no usable tip path)"
            : $"{pick.Clip} {from:F2}..{to:F2} s, weapon {sampled.WeaponLength:F2} m, tilt {path.TiltDegrees:F0} deg, bow {path.BowDegrees:F0} deg, turns {path.TravelDegrees:F0} deg, " +
              $"from {Dir(path.Directions[0])} to {Dir(path.Directions[^1])}";
        return path;

        static string Dir(Num.Vector3 d) => $"({d.X:F2} fwd, {d.Y:F2} left, {d.Z:F2} up)";
    }
}
