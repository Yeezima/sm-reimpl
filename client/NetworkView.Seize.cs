using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Godot;
using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// M16 (protocol 11): Seize Ground in the networked view, and the showcase camera aids.
/// <para>
/// Points: each control point of the scoreboard's layout is drawn as a translucent cylinder (radius and half height from
/// the layout) in its owner's colour (neutral grey, Space Marines blue, Chaos orange), with an edge ring at the marker's
/// height, a disc that grows with the progress in the colour of the side whose progress it is (a neutral point: the
/// capturing side; an owned point: the owner's hold, shrinking while it is neutralised), a white-red flash while
/// contested, and a floating letter (A, B, C in the layout's order). The HUD (top centre) shows one entry per point with
/// owner, progress and contested; the banner and scoreboard name the mode. All of it is a **placeholder demonstration**:
/// the original markers and HUD are not reproduced, and the only text taken from the game is the mode's name.
/// </para>
/// <para>
/// Bot (Seize Ground only, <see cref="BotSeizeInput"/>): heads for a point its side does not hold fully (or a contested
/// one), a side's bots spread over those points (<see cref="BotSeizePoint"/>), along a navigation path (NetworkView.Nav.cs),
/// firing at enemies it can see within <see cref="BotFightRange"/> on the way (M19: not at ones behind walls), and once
/// inside fights as before but stays in. With every point held it plays as in Annihilation. Test script, not the original AI.
/// </para>
/// <para>
/// Showcase aids: <c>--spectate overview|action|point:&lt;letter&gt;</c> replaces the follow camera (an orbit high over
/// the points, a side view of the own player and its nearest enemy, or an orbit around one point); <c>--no-hud</c>
/// hides every 2D element; <c>--no-overlay</c> hides only the debug text at the top left.
/// </para>
/// </summary>
public partial class NetworkView
{
    const float BotFightRange = 25f;            // metres within which the bot fights on its way to a point (test script)
    const float InspectRadius = 1.3f, InspectHeight = 0.35f, InspectBehindMuzzle = 0.4f, InspectTurnSpeed = 0.35f;   // --spectate inspect: metres, metres, metres, radians per second (ours)
    const float CameraHideDistance = 1.0f;      // metres from the head within which the own model is hidden (client-side preference)
    const float BotPointInside = 0.6f;          // fraction of the point radius the bot walks to before it counts as in (test script)
    static readonly Color NeutralPointColour = new(0.7f, 0.7f, 0.72f);

    /// <summary><c>--spectate</c>: "overview", "action", "point:A", "shot", "inspect" (M34: orbit around the own weapon) or "cam:x,y,z,tx,ty,tz" (null: the normal camera).</summary>
    public string? Spectate { get; init; }
    /// <summary><c>--no-hud</c>: hide every 2D element (clean shots).</summary>
    public bool NoHud { get; init; }
    /// <summary><c>--no-overlay</c>: keep the info overlay hidden even when F1 is pressed.</summary>
    public bool NoOverlay { get; init; }
    /// <summary>
    /// <c>--no-markers</c> (M34 capture aid): hides every test overlay and placeholder marker that is not the original look: the
    /// side rings and name tags over players, the charging glow, the blast spheres / discs and ground-pound and landing rings, the
    /// melta cone flash, the spawn-room shield planes, the Seize Ground point columns (the HUD keeps showing the points), and the
    /// debug text (as <c>--no-overlay</c>). The HUD stays (<c>--no-hud</c> hides it). Not hidden: the grenade models, still balls.
    /// </summary>
    public bool NoMarkers { get => _noMarkers || !ObserverHudOn; init => _noMarkers = value; }   // observer mode: H (HUD off) hides them too
    bool _noMarkers;
    /// <summary>
    /// <c>--show-markers</c> (M36 test aid): shows the zone indicators that are hidden in normal play (owner's playtest 2026-10-28:
    /// they are not in the original) - the Seize Ground point columns, discs and letters in the world, the ground-pound and landing
    /// rings, and the translucent spawn-room shield planes. <c>--no-markers</c> wins over it. The HUD point markers always show.
    /// </summary>
    public bool ShowMarkers { get; init; }
    /// <summary>Whether the placeholder zone indicators of <see cref="ShowMarkers"/> are drawn (only with <c>--show-markers</c> and not <c>--no-markers</c>).</summary>
    bool ZoneMarkers => ShowMarkers && !NoMarkers;
    /// <summary><c>--frame-stats</c>: print the mean and worst frame time (and the renderer's CPU/GPU time) every 5 s.</summary>
    public bool FrameStatsLog { get; init; }
    /// <summary><c>--hud-demo</c> (showcase aid): cycles the HUD's aimed-enemy, hit-marker, kill-feed and switch-popup states every 8 s.</summary>
    public bool HudDemo { get; init; }
    /// <summary><c>--show-info</c>: start with the info overlay shown (hidden by default; F1 toggles it).</summary>
    public bool ShowInfo { get; init; }

    sealed class PointMarker
    {
        public required Node3D Root;
        public required StandardMaterial3D Column, Edge, Fill;
        public required MeshInstance3D FillNode;
        public required Label3D Letter;
    }

    readonly List<PointMarker> _pointMarkers = new();
    SeizeGroundInfo? _markerLayout;
    CanvasLayer? _uiLayer;
    ControlPointView[] _pointsSeen = [];
    int _captureLogs;
    // Bot: stuck detection on the way to a goal (BotWalkDirection).
    Vector2 _botLastCheckPos;
    double _botLastCheckAt, _botDetourUntil, _botWalkAt = double.NegativeInfinity;
    int _botDetours;
    // Detour directions tried in turn when stuck (degrees from the direction to the point; test script).
    static readonly float[] BotDetourAngles = [90f, -90f, 180f, 135f, -135f];
    double _spectateAngle;
    /// <summary>The bot's last Seize Ground decision, for its log.</summary>
    string _botSeizeNote = "";

    /// <summary>
    /// <c>--screenshot-on-effect</c> (showcase aid): a blast or melta cone flash is in the camera's view, or the own marine is
    /// in the air from a jump (flight or dive) 3 m or more above its lift-off. No time-outs, unlike the --wait-* aids.
    /// </summary>
    public bool EffectInView
    {
        get
        {
            if (_camera is null || _client?.Status != ClientStatus.Connected) return false;
            bool InView(Node3D n) => n.IsInsideTree() && _camera.IsPositionInFrustum(n.GlobalPosition);
            if (_blasts.Any(b => InView(b.Node)) || _coneFlashes.Any(f => InView(f.Node)) || DeathInView() || FxShotRecent || BarrelShotRecent) return true;   // M25b: or a weapon effect just played
            return _local is not null && _jumpTracks.TryGetValue(_client.PlayerId, out var t) && t.View is JumpView.Air or JumpView.Dive
                && _local.Position.Z - t.LiftZ >= 3f && _camera.IsPositionInFrustum(_local.GlobalPosition + Vector3.Up);
        }
    }

    static string PointLetter(int index) => index < 26 ? ((char)('A' + index)).ToString() : index.ToString();

    static Color SideColour(Team t) => t switch { Team.SpaceMarines => MarineSideColour, Team.Chaos => ChaosSideColour, _ => NeutralPointColour };

    bool SeizeGround => _client?.Scoreboard?.Seize is not null || _client?.Match.Mode == GameMode.SeizeGround;

    void AddSeizeUi(CanvasLayer layer)
    {
        _uiLayer = layer;
    }

    /// <summary>Per frame: builds the markers once the layout is known, then colours them and fills the HUD row.</summary>
    void UpdateSeize(double delta)
    {
        if (_uiLayer is not null) _uiLayer.Visible = !NoHud;
        var layout = _client?.Scoreboard?.Seize;
        if (layout is not null && !ReferenceEquals(layout, _markerLayout) && (_markerLayout is null || !SameLayout(layout, _markerLayout)))
            BuildMarkers(layout);
        _markerLayout = layout ?? _markerLayout;
        bool seize = SeizeGround && _client?.Status == ClientStatus.Connected;
        foreach (var m in _pointMarkers) m.Root.Visible = seize && ZoneMarkers;
        if (!seize || _client is null) return;

        var points = _client.Points;
        float pulse = 0.5f + 0.5f * MathF.Sin((float)_connectedFor * 12f);
        for (int i = 0; i < _pointMarkers.Count; i++)
        {
            var m = _pointMarkers[i];
            var p = i < points.Count ? points[i] : default;
            var owner = SideColour(p.Owner);
            var column = p.Contested ? owner.Lerp(new Color(1f, 0.2f, 0.2f), pulse) : owner;
            m.Column.AlbedoColor = new Color(column, p.Contested ? 0.22f + 0.12f * pulse : 0.14f);
            m.Edge.AlbedoColor = p.Contested ? Colors.White.Lerp(new Color(1f, 0.15f, 0.1f), pulse) : owner;
            m.Edge.Emission = m.Edge.AlbedoColor;
            float progress = Math.Clamp(p.Progress, 0f, 1f);
            m.FillNode.Visible = p.Side != Team.None && progress > 0.005f;
            m.FillNode.Scale = new Vector3(MathF.Max(progress, 0.01f), 1f, MathF.Max(progress, 0.01f));
            m.Fill.AlbedoColor = new Color(SideColour(p.Side), 0.4f);
            m.Letter.Modulate = p.Contested ? Colors.White.Lerp(new Color(1f, 0.2f, 0.2f), pulse) : owner.Lightened(0.25f);
        }

        LogPointChanges(points);
    }

    static bool SameLayout(SeizeGroundInfo a, SeizeGroundInfo b) =>
        a.Points.Length == b.Points.Length && a.Points.Zip(b.Points).All(x => x.First == x.Second);

    void BuildMarkers(SeizeGroundInfo layout)
    {
        foreach (var m in _pointMarkers) m.Root.QueueFree();
        _pointMarkers.Clear();
        if (_gameSpace is null) return;
        // Godot meshes are Y-up; rotating about X by 90° puts their axis along game +Z.
        var upright = new Basis(new Vector3(1, 0, 0), MathF.PI / 2f);
        for (int i = 0; i < layout.Points.Length; i++)
        {
            var info = layout.Points[i];
            var root = new Node3D { Name = "Point_" + PointLetter(i), Position = new Vector3(info.Center.X, info.Center.Y, info.Center.Z) };
            _gameSpace.AddChild(root);
            var column = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled, AlbedoColor = new Color(NeutralPointColour, 0.14f),
            };
            root.AddChild(new MeshInstance3D
            {
                Mesh = new CylinderMesh { TopRadius = info.Radius, BottomRadius = info.Radius, Height = 2f * info.HalfHeight, RadialSegments = 48, Rings = 1, CapTop = false, CapBottom = false },
                Transform = new Transform3D(upright, Vector3.Zero), MaterialOverride = column,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            });
            // The edge ring and fill disc at the marker's height (the marker is assumed to sit on the floor).
            var edge = GlowMaterial(NeutralPointColour, 1.5f);
            root.AddChild(new MeshInstance3D
            {
                Mesh = new TorusMesh { InnerRadius = info.Radius - 0.25f, OuterRadius = info.Radius, Rings = 64, RingSegments = 6 },
                Transform = new Transform3D(upright, new Vector3(0, 0, 0.15f)), MaterialOverride = edge,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            });
            var fill = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled, AlbedoColor = new Color(NeutralPointColour, 0.4f),
            };
            var fillNode = new MeshInstance3D
            {
                Mesh = new CylinderMesh { TopRadius = info.Radius, BottomRadius = info.Radius, Height = 0.06f, RadialSegments = 48, Rings = 1 },
                MaterialOverride = fill, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Visible = false,
            };
            var fillHolder = new Node3D { Transform = new Transform3D(upright, new Vector3(0, 0, 0.12f)) };
            fillHolder.AddChild(fillNode);
            root.AddChild(fillHolder);
            var letter = new Label3D
            {
                // Constant size on screen, drawn through walls (a placeholder objective marker).
                Text = PointLetter(i), FontSize = 72, PixelSize = 0.001f, OutlineSize = 12, OutlineModulate = Colors.Black,
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, NoDepthTest = true, FixedSize = true,
                Position = new Vector3(0, 0, 6f),
                Visible = false,   // the HUD draws the objective markers (letter, CAPTURE / DEFEND, edge clamping)
            };
            root.AddChild(letter);
            _pointMarkers.Add(new PointMarker { Root = root, Column = column, Edge = edge, Fill = fill, FillNode = fillNode, Letter = letter });
        }
        GD.Print($"seize ground: {layout.Points.Length} points drawn: " + string.Join(", ", layout.Points.Select((p, i) =>
            $"{PointLetter(i)} '{p.Name}' at {p.Center.X:F1},{p.Center.Y:F1},{p.Center.Z:F1} r {p.Radius:F1} half height {p.HalfHeight:F1}")) +
            $"; capture {layout.CaptureTime:F0} s, neutralise {layout.NeutraliseTime:F0} s, {layout.PointScore} per point every {layout.ScoreInterval:F1} s");
    }

    /// <summary>Bot log: owner changes and contested starts as the client sees them (the server's log is authoritative).</summary>
    void LogPointChanges(IReadOnlyList<ControlPointView> points)
    {
        if (_pointsSeen.Length == points.Count)
            for (int i = 0; i < points.Count; i++)
            {
                var (was, now) = (_pointsSeen[i], points[i]);
                if (was.Owner != now.Owner || (now.Contested && !was.Contested))
                {
                    _captureLogs++;
                    if (_bot || Spectate is not null)
                        GD.Print($"t {_connectedFor:F1} s: id {_client!.PlayerId} sees point {PointLetter(i)}: " +
                                 (was.Owner != now.Owner ? $"owner {SideName(was.Owner)} -> {SideName(now.Owner)}" : "contested") +
                                 $" (score marines {_client.Match.MarineScore} chaos {_client.Match.ChaosScore})");
                }
            }
        _pointsSeen = points.ToArray();
    }

    /// <summary>Banner line for the points, for the bot's periodic log.</summary>
    string PointsLogText() => _client is null ? "" : string.Join(" ", _client.Points.Select((p, i) =>
        $"{PointLetter(i)}:{(p.Owner == Team.None ? "neutral" : SideName(p.Owner))}{(p.Side != Team.None ? $"/{SideName(p.Side)} {p.Progress * 100f:F0}%" : "")}{(p.Contested ? " contested" : "")}"));

    /// <summary>
    /// Seize Ground bot input, or null to play as in Annihilation (not Seize Ground, dead, no layout, or every point held
    /// fully and uncontested, or already inside the chosen point: then the normal fight runs, and with nothing to fight the bot chases the nearest enemy or walks to the map centre).
    /// </summary>
    PlayerInput? BotSeizeInput(PlayerState me, uint tick, double phase)
    {
        if (!SeizeGround || !me.Alive || _client?.Scoreboard?.Seize is not { } layout || _botMelee || BotGoal is not null || BotRoute is not null) return null;
        var points = _client.Points;
        int best = BotSeizePoint(me, layout, points);
        if (best < 0) { _botSeizeNote = "all points held"; return null; }
        var goal = layout.Points[best];
        float bestDistance = new Vector2(goal.Center.X - me.Position.X, goal.Center.Y - me.Position.Y).Length();
        // Fight only an enemy it can see within BotFightRange (M19); otherwise it keeps walking without aiming at it.
        var target = BotFightTarget(me, out float targetDistance);
        bool fight = target is not null;
        bool inside = bestDistance <= goal.Radius * BotPointInside;
        _botSeizeNote = $"goal {PointLetter(best)} {bestDistance:F1} m{(inside ? " inside" : "")}{(Nav is null && _connectedFor < _botDetourUntil ? " detour" : "")}{(fight ? $" fighting at {targetDistance:F0} m (visible)" : "")}";
        if (inside)
        {
            if (fight) return null;                                                         // fight as usual, staying near
            return new PlayerInput(tick, 0f, 0f, _yaw, InputButtons.None, 0f);              // hold the point
        }
        // On the way: path following to the point (NetworkView.Nav.cs; until the graph is built, straight with detours).
        var (to, speed, _, dropAhead) = BotSteer(me, goal.Center, $"point {PointLetter(best)}");
        if (to.LengthSquared() < 1e-6f) to = new Vector2(goal.Center.X - me.Position.X, goal.Center.Y - me.Position.Y).Normalized();
        InputButtons buttons = InputButtons.None;
        if (fight && target is { } enemy)
        {
            // Aim and shoot at the enemy while walking to the point.
            float dx = enemy.Position.X - me.Position.X, dy = enemy.Position.Y - me.Position.Y;
            float dz = enemy.Position.Z + BotTargetHeight - (me.Position.Z + _config!.EyeHeight);
            _yaw = MathF.Atan2(-dx, dy);
            _pitch = Math.Clamp(MathF.Atan2(dz, targetDistance), -MaxPitch, MaxPitch);
            buttons = BotWeaponButtons(me, tick, enemy);
            _botAimNow = true;                  // M44a: the aim error applies to this ranged shot (NetworkView.BotAim.cs)
            _botAimTarget = enemy.PlayerId;     // the trigger discipline's reaction delay
            BotNoteEngagement(targetDistance);
        }
        else
        {
            _yaw = MathF.Atan2(-to.X, to.Y);
            _pitch = 0f;
            if (bestDistance > 8f && !dropAhead && _navAgent is not { OnJumpLink: true }) buttons = InputButtons.Sprint;
        }
        // On a jump link (slowing to the take-off cell, standing, pressing) the agent's speed counts; else full speed as before.
        return BotMoveInput(tick, _navAgent is { OnJumpLink: true } ? to * speed : to, buttons | _navJump);
    }

    /// <summary>The point the Seize Ground bot heads for (sticky while still wanted).</summary>
    int _botSeizeTarget = -1;

    /// <summary>
    /// The Seize Ground bot's point (test script): of the points its side does not hold fully (or that are contested), in
    /// order of distance from the side's start area (so the order does not change as the bot walks), the bot takes the one
    /// at its rank among its side's players by id (wrapping), so a side's bots spread over the points; chosen again when the
    /// wanted points or its rank change (so after taking its own flank a side moves on to the middle and the enemy's flank).
    /// -1 when every point is held.
    /// </summary>
    int BotSeizePoint(PlayerState me, SeizeGroundInfo layout, IReadOnlyList<ControlPointView> points)
    {
        bool Wanted(int i) => i < points.Count && (points[i].Owner != me.Team || points[i].Contested || points[i].Progress < 0.999f);
        long set = 0;
        for (int i = 0; i < layout.Points.Length && i < 63; i++) if (Wanted(i)) set |= 1L << i;
        int id = _client?.PlayerId ?? 0;
        int rank = _lastOthers.Count(o => o.Team == me.Team && o.PlayerId < id);
        // Same wanted points and team rank as when it chose: keep the point.
        if (set == _botSeizeWanted && rank == _botSeizeRank && _botSeizeTarget >= 0 && _botSeizeTarget < layout.Points.Length && Wanted(_botSeizeTarget)) return _botSeizeTarget;
        _botSeizeWanted = set;
        _botSeizeRank = rank;
        var home = _namedPoints.TryGetValue(me.Team == Team.SpaceMarines ? "marines" : "chaos", out var area) ? area : me.Position;
        var wanted = Enumerable.Range(0, layout.Points.Length).Where(Wanted)
            .OrderBy(i => System.Numerics.Vector3.Distance(layout.Points[i].Center, home)).ToList();
        int before = _botSeizeTarget;
        _botSeizeTarget = wanted.Count == 0 ? -1 : wanted[rank % wanted.Count];
        if (_botSeizeTarget != before) _navAgent?.Reset();
        return _botSeizeTarget;
    }

    long _botSeizeWanted = -1;
    int _botSeizeRank = -1;

    /// <summary>
    /// <c>--spectate</c> camera: the raw eye and target in Godot space (before blending, body push-out and the level cast of <see cref="SpectateFollow"/>), or null for the normal camera.
    /// No allocation per frame (M40-T6): the mode is lower-cased once, the "cam:" numbers parsed once, the "action" subject chosen by a loop.
    /// </summary>
    (Vector3 Eye, Vector3 Target)? SpectateCamera(PlayerState me, Vector3 mePos, float delta)
    {
        if (Spectate is null || _gameSpace is null) return null;
        _spectateAngle += delta;
        _spectateMode ??= Spectate.ToLowerInvariant();
        string mode = _spectateMode;
        if (mode == "barrel" && BarrelView() is { } barrelView) return barrelView;   // fx/barrel-origin
        if (mode == "shot" && FxShotView() is { } shotView) return shotView;   // M25b: close to the last shot's effects
        // "cam:x,y,z,tx,ty,tz": a fixed camera at game position (x, y, z) looking at (tx, ty, tz) (close-up shots).
        if (mode.StartsWith("cam:", StringComparison.Ordinal))
        {
            if (!_spectateFixedParsed)
            {
                _spectateFixedParsed = true;
                var f = mode[4..].Split(',').Select(s => float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x) ? x : float.NaN).ToArray();
                if (f.Length == 6 && f.All(float.IsFinite))
                    _spectateFixed = (SpectateG(new System.Numerics.Vector3(f[0], f[1], f[2])), SpectateG(new System.Numerics.Vector3(f[3], f[4], f[5])));
            }
            if (_spectateFixed is { } fixedView) return fixedView;
        }
        var layout = _client?.Scoreboard?.Seize?.Points;
        // "inspect" (M34 capture aid): a slow orbit close around the own weapon in hand (its middle, a little behind the muzzle),
        // a little above it; without a ranged weapon in hand the "action" view below shows.
        if (mode == "inspect" && _client is not null && MuzzleWorld(_client.PlayerId) is { } muzzle)
        {
            var weapon = muzzle.Position - muzzle.Direction * InspectBehindMuzzle;
            float turn = (float)(_spectateAngle * InspectTurnSpeed);
            return (weapon + new Vector3(MathF.Cos(turn) * InspectRadius, InspectHeight, MathF.Sin(turn) * InspectRadius), weapon);
        }
        if (mode.StartsWith("point:", StringComparison.Ordinal) && layout is { Length: > 0 })
        {
            int index = Math.Clamp(char.ToUpperInvariant(mode[^1]) - 'A', 0, layout.Length - 1);
            var p = layout[index];
            var centre = SpectateG(p.Center);
            float a = (float)(_spectateAngle * 0.12);
            float r = p.Radius * 1.5f + 6f;
            return (centre + new Vector3(MathF.Cos(a) * r, 9f, MathF.Sin(a) * r), centre + Vector3.Up * 1.5f);
        }
        if (mode == "overview")
        {
            Vector3 centre;
            float spread;
            if (layout is { Length: > 0 })
            {
                centre = Vector3.Zero;
                foreach (var p in layout) centre += SpectateG(p.Center);
                centre /= layout.Length;
                float far = 0f, radius = 0f;
                foreach (var p in layout) { far = MathF.Max(far, SpectateG(p.Center).DistanceTo(centre)); radius = MathF.Max(radius, p.Radius); }
                spread = far + radius;
            }
            else { centre = mePos; spread = 30f; }
            float a = (float)(_spectateAngle * 0.05) + 0.6f;
            float r = spread * 1.15f + 20f;
            return (centre + new Vector3(MathF.Cos(a) * r, spread * 0.55f + 18f, MathF.Sin(a) * r), centre);
        }
        // "action": a side view of the own player and its nearest living enemy (or another living player).
        PlayerState? other = null;
        float bestScore = float.MaxValue;
        for (int i = 0; i < _lastOthers.Count; i++)
        {
            var o = _lastOthers[i];
            if (!o.Alive) continue;
            float score = (World.Hostile(me.Team, o.Team) || me.Team == Team.None ? 0f : 1000f) + System.Numerics.Vector3.Distance(o.Position, me.Position);
            if (score < bestScore) { bestScore = score; other = o; }
        }
        var focusA = me.Alive ? mePos : other is { } o0 ? SpectateG(o0.Position) : mePos;
        var focusB = other is { } o1 ? SpectateG(o1.Position) : focusA;
        if (focusA.DistanceTo(focusB) > 30f) focusB = focusA;   // (40 m before M40-T6: see the distance below)
        var mid = (focusA + focusB) * 0.5f;
        var line = focusB - focusA; line.Y = 0;
        var across = line.LengthSquared() > 0.01f ? new Vector3(-line.Z, 0, line.X).Normalized() : new Vector3(1, 0, 0);
        float sep = focusA.DistanceTo(focusB);
        // M40-T6: the distance grows more slowly and stops at 14 m (it was 7 + 0.7 sep, 35 m for two players 40 m apart): a long line
        // between the target and the eye crosses walls, and every wall that crosses it is a pull-in of metres.
        return (mid + across * MathF.Min(6f + 0.4f * sep, 14f) + Vector3.Up * (3.5f + 0.15f * sep), mid + Vector3.Up * 1.2f);
    }

    string? _spectateMode;                                  // the lower-cased --spectate value
    bool _spectateFixedParsed;
    (Vector3 Eye, Vector3 Target)? _spectateFixed;          // the "cam:" view in Godot space, parsed once
    Vector3 SpectateG(System.Numerics.Vector3 v) => _gameSpace!.Transform * new Vector3(v.X, v.Y, v.Z);
}
