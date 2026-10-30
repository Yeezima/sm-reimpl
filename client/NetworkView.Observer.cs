using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// Observer mode (protocol 46; ours, docs/changes-from-original.md "Observer": the owner's tool for promotional footage, not an original feature):
/// <c>client.cmd --connect host:port --observe</c> (or the Join screen's SPECTATE button) joins as a spectator. The server gives it no player, so
/// there is nothing to predict, aim or be hit: this view draws the world as the server sends it (everyone, both sides, no team visibility) from a
/// free-fly camera or from over a chosen player's shoulder. It is a separate per-frame path (<see cref="ObserverFrame"/>) beside the player's
/// (<c>ProcessFrame</c>), reusing the same drawing of remote players, projectiles, effects and sound.
/// <para>
/// Keys: WASD fly (along the view), Space/E up, Ctrl/Q down, Shift fast (x4), Alt slow (x0.2), the wheel changes the speed (follows the distance
/// while following), [ and ] change the field of view (Backspace resets it), G switches cinematic smoothing, F follows the next living player
/// (Shift+F the previous; after the last one back to free-fly; left and right click do the same while following), H hides the whole HUD (2D elements, the objective and zone markers,
/// the scoreboard, name tags and the placeholder rings) for clean footage, Tab the scoreboard, F10 back to the menu, Esc frees the mouse.
/// <c>--observe-pose x,y,z,yaw,pitch</c> starts the free camera at a game-space place (metres, degrees) instead of the overview.
/// </para>
/// </summary>
public partial class NetworkView
{
    /// <summary><c>--observe</c> (protocol 46): this client is a spectator (see the class comment).</summary>
    public bool Observe { get; init; }
    /// <summary><c>--observe-pose x,y,z,yaw,pitch</c> (capture aid): the free camera's first place in game space (metres, Z up; yaw and pitch in degrees, yaw 0 faces +Y), instead of the overview.</summary>
    public string? ObservePose { get; init; }

    /// <summary><c>--observe-follow</c> (capture aid): follows the first living player (lowest id) as soon as there is one, as F does.</summary>
    public bool ObserveFollow { get; init; }

    const float ObsBaseSpeed = 12f, ObsMinSpeed = 0.5f, ObsMaxSpeed = 250f, ObsFastFactor = 4f, ObsSlowFactor = 0.2f;   // m/s; ours
    const float ObsFovMin = 15f, ObsFovMax = 110f, ObsFovRate = 30f;   // degrees; degrees per second held
    const float ObsMoveSmooth = 3.5f, ObsLookSmooth = 7f;              // 1/s: the smoothing's exponential rates
    const float ObsFollowDistance = 3.4f, ObsFollowMin = 1.2f, ObsFollowMax = 12f;   // metres behind the shoulder pivot
    const float ObsFeedSeconds = 8f;

    Vector3 _obsPos, _obsVel;
    float _obsYaw, _obsPitch, _obsYawShown, _obsPitchShown;   // radians in Godot space (yaw 0 faces -Z); the first pair is what the mouse sets
    float _obsSpeed = ObsBaseSpeed, _obsFov = DefaultFov, _obsFollowDist = ObsFollowDistance;
    bool _obsHud = true, _obsSmooth, _obsPlaced, _obsFollowAsked;
    int _obsFollow;                       // the followed player's id; 0 free-fly
    float _obsOrbitYaw, _obsOrbitPitch;   // the mouse's turn around the followed player
    Vector3 _obsFollowEye;
    bool _obsFollowPlaced;
    CanvasLayer? _obsLayer;
    Label? _obsLabel;
    RichTextLabel? _obsFeed;
    readonly List<(string Text, double At)> _obsKills = new();

    /// <summary>The user's HUD toggle (<c>H</c>) in observer mode; always true outside it. <c>NoMarkers</c> follows it, so the rings and tags go with the HUD.</summary>
    bool ObserverHudOn => !Observe || (_obsHud && !NoHud);

    // ---- input ----

    /// <summary>Observer mode's events (the player's <c>_UnhandledInput</c> path is skipped): mouse look, the wheel, clicks and the keys that act once.</summary>
    void ObserverInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseMotion motion when MouseCaptured:
                if (_obsFollow != 0)
                {
                    _obsOrbitYaw -= motion.Relative.X * MouseYawPerPixel * _mouseScale;
                    _obsOrbitPitch = Math.Clamp(_obsOrbitPitch - motion.Relative.Y * MouseYawPerPixel * _mouseScale, -1.2f, 1.2f);
                }
                else
                {
                    _obsYaw -= motion.Relative.X * MouseYawPerPixel * _mouseScale;
                    _obsPitch = Math.Clamp(_obsPitch - motion.Relative.Y * MouseYawPerPixel * _mouseScale, -1.55f, 1.55f);
                }
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown } wheel:
            {
                float step = wheel.ButtonIndex == MouseButton.WheelUp ? 1.15f : 1f / 1.15f;
                if (_obsFollow != 0) _obsFollowDist = Math.Clamp(_obsFollowDist / step, ObsFollowMin, ObsFollowMax);
                else _obsSpeed = Math.Clamp(_obsSpeed * step, ObsMinSpeed, ObsMaxSpeed);
                break;
            }
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left or MouseButton.Right } click:
                if (Input.MouseMode != Input.MouseModeEnum.Captured && !ModelViewer.TestWindow) Input.MouseMode = Input.MouseModeEnum.Captured;
                else if (_obsFollow != 0) ObserverCycle(click.ButtonIndex == MouseButton.Left ? 1 : -1);
                break;
            case InputEventKey { Pressed: true, Echo: false } key:
                switch (key.PhysicalKeycode)
                {
                    case Key.Escape: Input.MouseMode = Input.MouseModeEnum.Visible; break;
                    case Key.H: _obsHud = !_obsHud; break;
                    case Key.G: _obsSmooth = !_obsSmooth; break;
                    case Key.F: ObserverCycle(key.ShiftPressed ? -1 : 1); break;
                    case Key.Backspace: _obsFov = DefaultFov; break;
                    case Key.F10: LeaveToMenu?.Invoke(); break;
                }
                break;
        }
    }

    /// <summary>F: the next (or with <paramref name="direction"/> -1 the previous) living player to follow; past the last one, free-fly again.</summary>
    void ObserverCycle(int direction)
    {
        var ids = _lastOthers.Where(o => o.Alive).Select(o => o.PlayerId).OrderBy(i => i).ToList();
        if (ids.Count == 0) { _obsFollow = 0; return; }
        int at = ids.IndexOf(_obsFollow);
        int next = _obsFollow == 0 ? (direction > 0 ? 0 : ids.Count - 1) : at < 0 ? 0 : at + direction;
        if (next < 0 || next >= ids.Count) { ObserverFree(); return; }
        _obsFollow = ids[next];
        _obsOrbitYaw = 0f; _obsOrbitPitch = 0f;
        _obsFollowPlaced = false;
    }

    /// <summary>Back to free-fly from where the camera is.</summary>
    void ObserverFree()
    {
        if (_obsFollow == 0 || _camera is null) { _obsFollow = 0; return; }
        _obsFollow = 0;
        _obsPos = _camera.GlobalPosition;
        var e = _camera.GlobalBasis.GetEuler();
        _obsPitch = _obsPitchShown = e.X; _obsYaw = _obsYawShown = e.Y;
        _obsVel = Vector3.Zero;
    }

    // ---- the frame ----

    /// <summary>
    /// The connected frame of a spectator: the players as the server shows them (no own player), the world's effects, the camera, the observer HUD. Skips what belongs to a
    /// player (prediction, the own model, hit and damage feedback, the customizer, weapon HUD).
    /// </summary>
    void ObserverFrame(double delta)
    {
        var me = _client!.Predicted;   // empty: an observer has no player
        LooksBeginFrame(me);
        var (renderTick, interpolated) = _client.RemotePlayers();
        var others = DrawnRemotes(interpolated, me, me);
        _lastOthers = others;
        HipFireBegin(me);
        HipFireOthers(me, others, renderTick > 0);
        var seen = _seenRemotes;
        seen.Clear();
        foreach (var state in others)
        {
            seen.Add(state.PlayerId);
            _remotes.TryGetValue(state.PlayerId, out var character);
            if (character is null && !HeavyLeft) continue;
            long heavy = HeavyStart();
            _remotes[state.PlayerId] = character = EnsureModel(character, state.PlayerId, false, state);
            if (character.Weapons is null) HeavyEnd(heavy);
            float? remoteThrow = null;
            if (state.Alive && state.Throwing)
            {
                if (!_remoteThrowStart.TryGetValue(state.PlayerId, out double start)) _remoteThrowStart[state.PlayerId] = start = _connectedFor;
                remoteThrow = (float)(_connectedFor - start);
            }
            else _remoteThrowStart.Remove(state.PlayerId);
            TrackJump(state, own: false);
            TrackEvade(state, own: false, (float)delta);
            bool placed = TrackDeath(state.PlayerId, character, state, own: false, (float)delta);
            if (placed) Place(character, state, remoteThrow, smooth: state.Alive ? SmoothRemoteClip(state, (float)delta) : null);
            ApplyTeamLook(character, state.PlayerId, state.Team);
            if (character.Weapons is not null || HeavyLeft)
            {
                long rigStart = HeavyStart();
                bool starting = character.Weapons is null;
                SyncWeapons(character, state, (float)delta);
                if (starting) HeavyEnd(rigStart);
            }
            character.Visible = state.Alive;
            _remoteCharging[state.PlayerId] = state.Charging;
        }
        _goneRemotes.Clear();
        foreach (var id in _remotes.Keys) if (!seen.Contains(id)) _goneRemotes.Add(id);
        foreach (var gone in _goneRemotes)
        {
            _remotes[gone].QueueFree();
            _remotes.Remove(gone);
            RemoveRagdolls(gone);
            _aliveSeen.Remove(gone); _motion.Remove(gone); _deathCauses.Remove(gone);
            _remoteCharging.Remove(gone);
            _remoteThrowStart.Remove(gone);
            _clipClocks.Remove(gone); _clipStats.Remove(gone);
            _jumpTracks.Remove(gone);
            _evadeTracks.Remove(gone);
            _aimMoving.Remove(gone);
            HipFireForget(gone);
            ReloadForget(gone);
            BraceForget(gone);
            LocomotionForget(gone);
            _stimTracks.Remove(gone);
            MeleeLockForget(gone);
            MeleeFlowForget(gone);
            _orkTracks.Remove(gone); _orkShown.Remove(gone);
            if (_obsFollow == gone) ObserverFree();
        }
        LooksEndFrame(delta);
        UpdateProjectiles();
        UpdateBlasts(delta);
        UpdateAreas(delta);
        UpdateConeFlashes(delta);
        UpdateRings(delta);

        FxShakeBegin();
        ObserverCamera(others, (float)delta);
        UpdateFx(me, delta);
        UpdatePickups(me);

        // The kill feed is the observer's own (the player's HUD draws a view of the own player's vitals): the kills, once each, to the ragdolls' push and the barks, then to the list.
        foreach (var k in _client.TakeKills())
        {
            NoteDeathCause(k);
            OrkKillSound(k);
            ExtrasKill(k);   // EXTRAS-HOOK: added content's kill reactions, as the player's path calls it (UpdateMatchUi)
            _obsKills.Add((KillText(k), _connectedFor));
            _killsSeen++;
        }
        while (_obsKills.Count > 0 && (_obsKills.Count > 8 || _connectedFor - _obsKills[0].At > ObsFeedSeconds)) _obsKills.RemoveAt(0);
        UpdateMatchUi();
        ProcessDeaths();
        UpdateRagdolls(delta);
        WeaponFrame(delta);
        UpdateSound(me, others, delta);
        UpdateSeize(delta);
        ObserverHud();
        if ((int)(_connectedFor / 5.0) != (int)((_connectedFor - delta) / 5.0) && _camera is not null)
        {
            var c = _camera.GlobalPosition;
            GD.Print($"t {_connectedFor:F0} s: observer {_client.PlayerId} sees {others.Count} players ({others.Count(o => o.Alive)} alive, {_remotes.Count} drawn), {_client.Projectiles().Count} projectiles; " +
                     $"camera {(_obsFollow != 0 ? "following P" + _obsFollow : "free")} at {c.X:F1},{-c.Z:F1},{c.Y:F1} (game space) fov {_camera.Fov:F0}; hud {(_obsHud ? "on" : "off")}; {_obsKills.Count} kills on the feed, {_killsSeen} seen");
        }
    }

    // ---- the camera ----

    void ObserverCamera(IReadOnlyList<PlayerState> others, float delta)
    {
        if (_camera is null || _gameSpace is null) return;
        if (!_obsPlaced && (others.Count > 0 || _connectedFor > 3.0)) ObserverPlace(others);
        if (ObserveFollow && _obsPlaced && _obsFollow == 0 && !_obsFollowAsked && others.Any(o => o.Alive)) { _obsFollowAsked = true; ObserverCycle(1); }
        _camera.Fov = _obsFov;
        var target = _obsFollow != 0 ? others.FirstOrDefault(o => o.PlayerId == _obsFollow) : default;
        if (_obsFollow != 0 && target.PlayerId == _obsFollow) ObserverFollowCamera(target, delta);
        else
        {
            if (_obsFollow != 0) ObserverFree();
            ObserverFlyCamera(delta);
        }
        _cameraPlaced = true;
        var at = _camera.GlobalPosition;   // Godot to game space; the server picks the nearest projectiles and shots for it (protocol 46)
        if (_client is not null) _client.ObserverCamera = new System.Numerics.Vector3(at.X, -at.Z, at.Y);
    }

    /// <summary>The first place: <c>--observe-pose</c>, else a view from above and behind the middle of the living players (or over the map's origin when there are none).</summary>
    void ObserverPlace(IReadOnlyList<PlayerState> others)
    {
        _obsPlaced = true;
        if (ObservePose is { Length: > 0 } text)
        {
            var f = text.Split(',').Select(s => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : float.NaN).ToArray();
            if (f.Length == 5 && f.All(float.IsFinite))
            {
                _obsPos = SpectateG(new System.Numerics.Vector3(f[0], f[1], f[2]));
                _obsYaw = _obsYawShown = Mathf.DegToRad(f[3]);   // game yaw 0 faces +Y; Godot's yaw is the same angle about up (the game's Z)
                _obsPitch = _obsPitchShown = Mathf.DegToRad(f[4]);
                return;
            }
            GD.PrintErr($"--observe-pose '{text}': expected x,y,z,yaw,pitch in game metres and degrees; the overview is used");
        }
        var living = others.Where(o => o.Alive).Select(o => SpectateG(o.Position)).ToList();
        var centre = living.Count > 0 ? living.Aggregate(Vector3.Zero, (a, b) => a + b) / living.Count : SpectateG(System.Numerics.Vector3.Zero);
        _obsPos = centre + new Vector3(0f, 9f, 12f);
        _obsYaw = _obsYawShown = 0f;
        _obsPitch = _obsPitchShown = -MathF.Atan2(9f, 12f);
    }

    void ObserverFlyCamera(float delta)
    {
        if (_obsSmooth)
        {
            float look = 1f - MathF.Exp(-ObsLookSmooth * delta);
            _obsYawShown = Mathf.LerpAngle(_obsYawShown, _obsYaw, look);
            _obsPitchShown = Mathf.Lerp(_obsPitchShown, _obsPitch, look);
        }
        else { _obsYawShown = _obsYaw; _obsPitchShown = _obsPitch; }
        var basis = Basis.FromEuler(new Vector3(_obsPitchShown, _obsYawShown, 0f));
        bool Down(Key k) => Input.IsPhysicalKeyPressed(k);
        var wish = Vector3.Zero;
        if (MouseCaptured || ModelViewer.TestWindow)
        {
            wish += -basis.Z * ((Down(Key.W) ? 1f : 0f) - (Down(Key.S) ? 1f : 0f));
            wish += basis.X * ((Down(Key.D) ? 1f : 0f) - (Down(Key.A) ? 1f : 0f));
            wish += Vector3.Up * ((Down(Key.Space) || Down(Key.E) ? 1f : 0f) - (Down(Key.Ctrl) || Down(Key.Q) ? 1f : 0f));
            if (Down(Key.Bracketleft)) _obsFov = Math.Max(ObsFovMin, _obsFov - ObsFovRate * delta);
            if (Down(Key.Bracketright)) _obsFov = Math.Min(ObsFovMax, _obsFov + ObsFovRate * delta);
        }
        if (wish.LengthSquared() > 1f) wish = wish.Normalized();
        float speed = _obsSpeed * (Down(Key.Shift) ? ObsFastFactor : 1f) * (Down(Key.Alt) ? ObsSlowFactor : 1f);
        var wanted = wish * speed;
        _obsVel = _obsSmooth ? _obsVel.Lerp(wanted, 1f - MathF.Exp(-ObsMoveSmooth * delta)) : wanted;
        _obsPos += _obsVel * delta;
        _camera!.GlobalTransform = new Transform3D(basis, _obsPos);
    }

    /// <summary>Over the right shoulder of <paramref name="target"/>, looking along its aim plus the mouse's turn; the eye pulled in short of walls and eased to its place.</summary>
    void ObserverFollowCamera(PlayerState target, float delta)
    {
        var feet = SpectateG(target.Position);
        var head = feet + Vector3.Up * (_config?.EyeHeight ?? 1.6f);
        float yaw = target.Yaw + _obsOrbitYaw;
        float pitch = Math.Clamp(target.Pitch * 0.6f - 0.12f + _obsOrbitPitch, -1.4f, 1.4f);
        var basis = Basis.FromEuler(new Vector3(pitch, yaw, 0f));
        var pivot = head + Vector3.Up * 0.25f + basis.X * 0.6f;
        if (_mapNote is not null) pivot = Unblocked(head, pivot);
        var eye = pivot + basis.Z * _obsFollowDist;   // behind: +Z is back
        if (_mapNote is not null) eye = Unblocked(pivot, eye);
        float k = _obsFollowPlaced ? 1f - MathF.Exp(-10f * delta) : 1f;
        _obsFollowPlaced = true;
        _obsFollowEye = _obsFollowEye.Lerp(eye, k);
        if (_mapNote is not null) _obsFollowEye = Unblocked(pivot, _obsFollowEye);
        var look = (pivot + (-basis.Z) * 24f) - _obsFollowEye;
        var wantedBasis = Basis.LookingAt(look.Normalized(), Vector3.Up);
        var shown = _camera!.GlobalBasis.Slerp(wantedBasis, k).Orthonormalized();
        _camera.GlobalTransform = new Transform3D(shown, _obsFollowEye);
    }

    // ---- the observer HUD ----

    void ObserverHud()
    {
        if (_obsLayer is null)
        {
            _obsLayer = new CanvasLayer { Name = "ObserverHud" };
            _obsLabel = new Label { Position = new Vector2(16, 0), Size = new Vector2(1100, 0), AutowrapMode = TextServer.AutowrapMode.WordSmart };
            _obsLabel.SetAnchorsPreset(Control.LayoutPreset.BottomLeft);
            _obsLabel.OffsetLeft = 16; _obsLabel.OffsetBottom = -12; _obsLabel.OffsetTop = -120; _obsLabel.OffsetRight = 1116;
            _obsLabel.GrowVertical = Control.GrowDirection.Begin;
            _obsLabel.VerticalAlignment = VerticalAlignment.Bottom;
            _obsLabel.AddThemeFontSizeOverride("font_size", 17);
            _obsLabel.AddThemeColorOverride("font_outline_color", Colors.Black);
            _obsLabel.AddThemeConstantOverride("outline_size", 4);
            _obsLayer.AddChild(_obsLabel);
            _obsFeed = new RichTextLabel { BbcodeEnabled = true, FitContent = true, ScrollActive = false, MouseFilter = Control.MouseFilterEnum.Ignore, HorizontalAlignment = HorizontalAlignment.Right };
            _obsFeed.SetAnchorsPreset(Control.LayoutPreset.TopRight);
            _obsFeed.OffsetLeft = -720; _obsFeed.OffsetRight = -16; _obsFeed.OffsetTop = 12; _obsFeed.OffsetBottom = 200;
            _obsFeed.AddThemeFontSizeOverride("normal_font_size", 18);
            _obsFeed.AddThemeColorOverride("font_outline_color", Colors.Black);
            _obsFeed.AddThemeConstantOverride("outline_size", 4);
            _obsLayer.AddChild(_obsFeed);
            AddChild(_obsLayer);
        }
        if (_hudView is not null)
        {
            var (statusText, statusError) = StatusLine();   // the player's HUD stays out (vitals of nobody): only its status banner is kept honest
            _hudView.ShowHint = false;
            _hudView.Data = new Hud.Frame { Status = statusText, StatusIsError = statusError };
        }
        bool on = ObserverHudOn;
        _obsLayer.Visible = on;
        if (_uiLayer is not null) _uiLayer.Visible = on && !NoHud;
        if (_overlay is not null) _overlay.Visible = _showInfo && on;
        if (!on) return;
        var match = _client!.Match;
        string mode = _obsFollow != 0
            ? $"FOLLOWING {HudName(_obsFollow)} ({SideName(TeamOf(_obsFollow))})   distance {_obsFollowDist:0.0} m (wheel)   F: next, Shift+F: previous, past the last: free-fly   mouse: look around it"
            : $"FREE-FLY   speed {_obsSpeed:0.#} m/s (wheel; Shift x{ObsFastFactor:0}, Alt x{ObsSlowFactor:0.#})   smoothing {(_obsSmooth ? "on" : "off")} (G)";
        _obsLabel!.Text = $"OBSERVER   {mode}   FOV {_obsFov:0} ([ ] , Backspace)\n"
            + $"WASD fly   Space/E up   Ctrl/Q down   F follow a player   H hide the HUD   Tab scoreboard   F10 menu   Esc frees the mouse\n"
            + MatchText(match).Replace('\n', ' ');
        _obsFeed!.Text = string.Join("\n", _obsKills.Select(k => k.Text));
    }
}
