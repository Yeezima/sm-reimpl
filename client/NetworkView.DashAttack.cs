using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmSim;

namespace SmClient;

/// <summary>
/// Protocol 30 in the networked view (the simulation's rules are <see cref="Rush"/>, <see cref="JumpPack"/> and <see cref="Movement"/>):
/// <list type="bullet">
/// <item>the Assault / Raptor dash attack: its phases pose the original clips through the charge's path (<c>RushPose</c>: the lunge
/// <c>SM_JMP_DashAtk_F</c>, the hit <c>SM_MEL_PA_XATK_Combo3FIN</c>, the run-out <c>SM_NAV_Sprint_to_Idle_F</c>, the wall stop), so the clips'
/// tags post their sounds and effects through the sound triggers; a connected lunge plays the data's <c>actn_dash_hit</c> landing effect
/// (<c>jumppack_land_normal</c>; the pack's landing effect for a 15 m drop, its stand-in);</item>
/// <item>the jump pack's sword slash: the dive and the impact pose <c>SM_MEL_CS_XATK_Hv1_LS_2_7m</c> (<see cref="TrackJump"/>), the landing plays the
/// landing's effect, not the pound's crater; the pound target marker shows only from the slash's 8 m up (<c>JumpPack.CanPound</c> with the map);</item>
/// <item>test aids: <c>--bot-dash-attack</c> (the bot dashes at a target 5 to 14 m away and presses melee in the window), <c>--log-falls</c> (the own
/// player's walk-off drops and charges in the air: horizontal speed when it left the ground and where it landed).</item>
/// </list>
/// The hammer's own clips (<c>SM_JMP_TH_DashAtk_F</c>, <c>SM_MEL_TH_XATK_Combo3</c>) are not used: the simulation reads the sword's (a gap).
/// </summary>
public partial class NetworkView
{
    /// <summary>The slash's clip when the rig has and decodes it, else null (the slash then shows the pound's dive and impact clips).</summary>
    string? _slashClip;
    int _dashAttacksOwn, _dashAttacksSeen, _dashHits, _dashWalls, _slashesOwn, _slashesSeen, _fallsLogged;
    /// <summary>feel/jump-pack-2 (ours): the own camera's knock when the dash attack's lunge meets a wall (radians; the slash's hit shake is 0.05).</summary>
    const float DashWallShake = 0.04f;
    readonly Dictionary<int, RushPhase> _remoteDash = new();

    /// <summary><c>--bot-dash-attack</c> (test aid with <c>--bot</c>; not original behaviour).</summary>
    bool BotDashAttack => _botDashAttack ??= OS.GetCmdlineUserArgs().Contains("--bot-dash-attack");
    bool? _botDashAttack;
    /// <summary><c>--log-falls</c> (test aid): log the own walk-off drops and airborne charges.</summary>
    bool LogFalls => _logFalls ??= OS.GetCmdlineUserArgs().Contains("--log-falls");
    bool? _logFalls;
    double _botDashNext = 4.0;

    /// <summary>The dash attack's and the slash's clips, checked against the rig (from <see cref="SetUpReactions"/>; missing ones are noted).</summary>
    void SetUpDashAttack()
    {
        var names = _assets!.Model.Rig.AnimationNames;
        bool Usable(string? name) => name is not null && names.Contains(name, StringComparer.OrdinalIgnoreCase) && _assets.Animation(name) is not null;
        foreach (var pack in _config!.JumpPacks)
        {
            if (pack.DashAttack is { } dash)
                foreach (var move in new[] { dash.Charge.Rush, dash.Hit, dash.Stop, dash.Charge.Collision })
                {
                    if (Usable(move.Animation)) _reactionClips.Add(move.Animation);
                    else if (_reactionClips.Add("#missing " + move.Animation)) _notes.Add($"dash attack clip '{move.Animation}' not in the rig or not decodable; that part shows the idle/run animation");
                }
            if (pack.Slash is { } slash && _slashClip is null)
            {
                if (Usable(slash.Animation)) _slashClip = slash.Animation;
                else _notes.Add($"sword slash clip '{slash.Animation}' not in the rig or not decodable; the slash shows the pound's clips");
            }
        }
        SlashMark.Request(_config.MeleeRange, _config.MeleeConeDegrees);   // feel/jump-pack: the hit mark's texture, made on a worker thread
    }

    /// <summary>A slash's dive started (from <see cref="TrackJump"/>): the log.</summary>
    void SlashStarted(PlayerState s, bool own, float aboveLiftOff)
    {
        if (own) _slashesOwn++; else _slashesSeen++;
        if (_bot) GD.Print($"t {_connectedFor:F1} s: {(own ? $"id {_client!.PlayerId}" : $"id {_client!.PlayerId} sees P{s.PlayerId}")} start a sword slash {aboveLiftOff:F1} m above the lift-off, " +
                           $"{JumpPack.AboveGround(s, _client.Collision):F1} m above the ground (pound from {Loadouts.JumpPack(_config!, s)?.Slash?.MinPoundHeight ?? 8f:F0} m; slashes {(own ? _slashesOwn : _slashesSeen)}{(own ? "" : " seen")})");
    }

    /// <summary>
    /// A slash's dive reached the ground (from <see cref="TrackJump"/>): feel/jump-pack (ours; the owner: "intensify the effect"): the 15 m landing's dust and grit with the light
    /// landing's at <see cref="GroundPoundFx.DustScale"/> x 0.6 (no crater, ring or chunks: the hit's mark and chunks follow at the hit, <see cref="SlashHitPresentation"/>), a short
    /// shake near it (<see cref="SlashShakeRange"/>), and the log.
    /// </summary>
    void SlashLanded(PlayerState s, bool own, JumpPackSpec? pack, float drop)
    {
        if (FxActive && _fx is not null && _catalogue is not null && pack is not null && _catalogue.JumpPack(pack.Name) is { } effects)
        {
            var (point, normal, surface) = GroundUnder(s.Position);
            var dust = effects.LandingFor(float.MaxValue).Concat(effects.LightLanding.Select(p => p with { Scale = (p.Scale <= 0f ? 1f : p.Scale) * GroundPoundFx.DustScale * 0.6f })).ToList();
            GroundPoundFx.Play(_fx, dust, point, normal, 0f, null, _catalogue.SurfaceName(surface), 0);
            if (_camera is { } cam && cam.GlobalPosition.DistanceTo(point) is var d && d < SlashShakeRange) FxShake(Math.Clamp(1f - d / SlashShakeRange, 0f, 1f) * SlashShakeAmount);
        }
        else LandingPresentation(s, own, pack, drop);
        if (_bot) GD.Print($"t {_connectedFor:F1} s: {(own ? $"id {_client!.PlayerId}" : $"id {_client!.PlayerId} sees P{s.PlayerId}")} sword slash lands at {s.Position.X:F1},{s.Position.Y:F1},{s.Position.Z:F1} after a {drop:F1} m drop" +
                           $" (cone hit {pack?.Slash?.HitTime ?? 0f:F3} s later: {pack?.Slash?.Damage.Damage ?? 0f:F0} melee, {pack?.Slash?.Reaction.Kind}; landing blast {(pack is null || JumpPack.LandingBlast(pack, drop) is not { } b ? "none" : b.Radius.ToString("F1") + " m")})");
    }

    /// <summary>feel/jump-pack (ours): the slash's landing and hit shakes: within this distance of the camera (m), at most this much (radians; the pound's 0.09 within 35 m).</summary>
    const float SlashShakeRange = 25f, SlashShakeAmount = 0.035f, SlashHitShakeAmount = 0.05f;
    int _slashMarks;

    /// <summary>
    /// feel/jump-pack (ours; the owner: "add a hit effect that shows roughly where the attack hitbox landed"): when the slash's cone resolves (<see cref="SlashSpec.HitTime"/> after the
    /// impact, client time), the melee cone's wedge on the ground at the marine's feet along its body's heading, the landing dust at the cone's middle, a few chunks thrown into the
    /// cone (<see cref="GroundPoundFx.PlaySlash"/>) and a short shake near it.
    /// </summary>
    void SlashHitPresentation(PlayerState s, bool own, JumpPackSpec? pack)
    {
        if (!FxActive || _fx is null || _catalogue is null || pack is null || _config is null || _catalogue.JumpPack(pack.Name) is not { } effects) return;
        var (point, normal, surface) = GroundUnder(s.Position);
        var heading = DirToGodot(new System.Numerics.Vector3(-MathF.Sin(s.Yaw), MathF.Cos(s.Yaw), 0f));
        bool near = _camera is not { } camNear || camNear.GlobalPosition.DistanceTo(point) <= PoundDetailRange;
        int started = GroundPoundFx.PlaySlash(_fx, effects.LightLanding, point, normal, heading, _config.MeleeRange, _config.MeleeConeDegrees,
            NoShards || !near ? null : GroundShardsNode(), _catalogue.SurfaceName(surface), s.PlayerId * 17 + _slashMarks);
        _slashMarks++;
        SlashConeAdd(point + normal.Normalized() * 0.03f, heading, _config.MeleeRange, _config.MeleeConeDegrees);   // fx/visuals-2 (item 35, NetworkView.SlashCone.cs)
        if (_camera is { } cam && cam.GlobalPosition.DistanceTo(point) is var d && d < SlashShakeRange) FxShake(Math.Clamp(1f - d / SlashShakeRange, 0f, 1f) * SlashHitShakeAmount);
        if (_bot) GD.Print($"t {_connectedFor:F2} s: {(own ? $"id {_client!.PlayerId}" : $"id {_client!.PlayerId} sees P{s.PlayerId}")} sword slash hit mark at {s.Position.X:F1},{s.Position.Y:F1},{s.Position.Z:F1} heading {s.Yaw:F2} ({started} parts; marks {_slashMarks})");
    }

    /// <summary>Each frame (from <see cref="UpdateDashAttackFrame"/>'s caller): the dash attack's starts and hits for the log and the hit effect.</summary>
    void UpdateDashAttackFrame(PlayerState me, IReadOnlyList<PlayerState> others)
    {
        Note(me, own: true);
        foreach (var o in others) Note(o, own: false);
        foreach (var gone in _remoteDash.Keys.Where(id => id != -1 && others.All(o => o.PlayerId != id)).ToList()) _remoteDash.Remove(gone);
        if (LogFalls) FallLog(me);
        SlashTestFrame(me);
        ChargeTravelFrame(me);   // feel/charge-directions: the own charge's heading and travel against the view (bots; NetworkView.ChargeDirections.cs)

        void Note(PlayerState s, bool own)
        {
            int key = own ? -1 : s.PlayerId;
            var was = _remoteDash.GetValueOrDefault(key);
            var now = s.Alive ? s.Rush : RushPhase.None;
            _remoteDash[key] = now;
            if (now == was) return;
            string who = own ? $"id {_client!.PlayerId}" : $"id {_client!.PlayerId} sees P{s.PlayerId}";
            if (now == RushPhase.DashAttack)
            {
                if (own) _dashAttacksOwn++; else _dashAttacksSeen++;
                if (_bot) GD.Print($"t {_connectedFor:F1} s: {who} dash attack at {s.Position.X:F1},{s.Position.Y:F1},{s.Position.Z:F1} ({JumpPack.AboveGround(s, _client!.Collision):F1} m above the ground{(s.Grounded ? "" : ", in the air")}; " +
                                   $"dash attacks {(own ? _dashAttacksOwn : _dashAttacksSeen)}{(own ? $"; prediction corrections {Corrections()}" : " seen")})");
            }
            else if (now == RushPhase.DashHit)
            {
                _dashHits++;
                // The data's actn_dash_hit: jumppack_land_normal (the pack's landing effect for a heavy drop stands in) and play_sm_jump_pack_smash (through the clip's tags).
                LandingPresentation(s, own, Loadouts.JumpPack(_config!, s), 15f);
                if (_bot) GD.Print($"t {_connectedFor:F1} s: {who} dash attack connects (hits {_dashHits})");
            }
            else if (now == RushPhase.DashCollision)
            {
                // feel/jump-pack-2 (the owner's request): the lunge met a wall. The wall stop poses the data's SM_NAV_Bullrush2Idle_F_Collision_01 (RushPose, the
                // clip the bull rush plays when it connects; its tags post its sounds); ours: the own camera's short knock, as a connected charge's.
                _dashWalls++;
                if (own && FxActive) FxShake(DashWallShake);
                if (_bot) GD.Print($"t {_connectedFor:F2} s: {who} dash attack stopped by a wall at {s.Position.X:F1},{s.Position.Y:F1},{s.Position.Z:F1} " +
                                   $"({(s.Grounded ? "on the ground" : "in the air")}; wall stop clip {Rush.MoveOf(_config!, s)?.Animation ?? "none"}{(RushPose(s) is null ? " NOT POSED (clip missing)" : "")}; wall stops {_dashWalls})");
            }
        }
    }

    // ---- --log-falls: the own walk-off drops and airborne charges ----
    bool _fallAir;
    System.Numerics.Vector3 _fallFrom;
    float _fallSpeed;
    string _fallKind = "";
    int _fallAirTicks;
    float _fallMaxHorizontal;

    void FallLog(PlayerState me)
    {
        bool air = me.Alive && !me.Grounded && me.Jump == JumpPhase.None && !Evade.Evading(me);
        float horizontal = MathF.Sqrt(me.Velocity.X * me.Velocity.X + me.Velocity.Y * me.Velocity.Y);
        if (air && !_fallAir)
        {
            _fallAir = true; _fallFrom = me.Position; _fallSpeed = horizontal; _fallAirTicks = 0; _fallMaxHorizontal = 0f;
            _fallKind = me.Rush switch { RushPhase.Rush => "a charge", RushPhase.DashAttack => "a dash attack", RushPhase.None => "a walk-off", _ => $"a charge stop ({me.Rush})" };
            GD.Print($"t {_connectedFor:F2} s: falls: {_fallKind} left the ground at {me.Position.X:F2},{me.Position.Y:F2},{me.Position.Z:F2} at {horizontal:F2} m/s horizontally");
        }
        else if (air)
        {
            // Frames, not ticks: only states already falling (vertical speed not 0: from the second tick in the air) count.
            _fallAirTicks++;
            if (me.Velocity.Z != 0f) _fallMaxHorizontal = MathF.Max(_fallMaxHorizontal, horizontal);
        }
        else if (_fallAir)
        {
            _fallAir = false; _fallsLogged++;
            float across = new System.Numerics.Vector2(me.Position.X - _fallFrom.X, me.Position.Y - _fallFrom.Y).Length();
            GD.Print($"t {_connectedFor:F2} s: falls: {_fallKind} landed at {me.Position.X:F2},{me.Position.Y:F2},{me.Position.Z:F2} after a {_fallFrom.Z - me.Position.Z:F2} m drop, " +
                     $"{across:F2} m from where it left (left at {_fallSpeed:F2} m/s; fastest horizontal while falling {_fallMaxHorizontal:F2} m/s; {(me.Rush != RushPhase.None ? $"now {me.Rush} at {Rush.Time(me, _config!):F2} s" : "no charge")}; falls logged {_fallsLogged})");
        }
    }

    /// <summary>
    /// <c>--bot-dash-attack</c>: on the ground with a target 5 to 14 m away, free and the dash attack's spacing over, the bot taps Jump with the stick forward
    /// (the dash) every few seconds; during the dash's flight it presses melee as soon as the simulation's window allows (<see cref="Rush.CanDashAttack"/>) and then
    /// lets the attack play without input. Null otherwise (the bot's other behaviour).
    /// </summary>
    PlayerInput? BotDashAttackInput(PlayerState me, PlayerState target, uint tick)
    {
        if (!BotDashAttack || Loadouts.JumpPack(_config!, me)?.DashAttack is null || !me.Alive) return null;
        if (Rush.IsDash(me.Rush)) return new PlayerInput(tick, 0f, 0f, _yaw, InputButtons.None, _pitch);
        if (Evade.Evading(me) && !me.EvadeLanded)
        {
            bool press = Rush.CanDashAttack(me, _config!, _client!.Collision) && me.RushLock == 0 && !me.MeleeHeld;
            // feel/charge-directions: --bot-dash-dirs holds the stick in each of the eight directions in turn at the press (the lunge goes that way).
            var (sx, sy, keys) = press && BotDashDirs ? DashDirs[_botDashDir++ % DashDirs.Length] : (0f, 0f, "none");
            if (press) NotePress(_yaw, keys, me.Velocity.X * -MathF.Sin(_yaw) + me.Velocity.Y * MathF.Cos(_yaw));
            return new PlayerInput(tick, sx, sy, _yaw, press ? InputButtons.Melee : InputButtons.None, _pitch);
        }
        if (me.Jump == JumpPhase.TakeOff) return new PlayerInput(tick, 0f, 1f, _yaw, InputButtons.None, _pitch);   // released: the tap is the dash
        float dx = target.Position.X - me.Position.X, dy = target.Position.Y - me.Position.Y, distance = MathF.Sqrt(dx * dx + dy * dy);
        if (_connectedFor < _botDashNext || !me.Grounded || me.Jump != JumpPhase.None || Evade.Evading(me) || me.EvadeLock > 0 || me.RushLock > 0
            || me.MeleeStep > 0 || me.Throwing || me.JumpHeld || distance is < 5f or > 14f) return null;
        _botDashNext = _connectedFor + 3.0;
        if (_bot) GD.Print($"t {_connectedFor:F1} s: id {_client!.PlayerId} bot dashes at P{target.PlayerId} {distance:F1} m away to dash attack");
        return new PlayerInput(tick, 0f, 1f, _yaw, InputButtons.Jump, _pitch);
    }

    // ---- --dash-wall-test (feel/jump-pack-2 test aid with --bot): the dash attack into a wall ----
    /// <summary><c>--dash-wall-test</c> (test aid with <c>--bot</c>; not original behaviour): see <see cref="DashWallTestInput"/>.</summary>
    bool DashWallTest => _dashWallTest ??= OS.GetCmdlineUserArgs().Contains("--dash-wall-test");
    bool? _dashWallTest;
    double _dashWallNext = 3.0;
    int _dashWallTries;

    /// <summary>
    /// <c>--dash-wall-test</c>: every 3 s, on the ground and free, the own Assault / Raptor looks for a wall 7 to 12 m away (16 headings, a ray 1 m above the
    /// feet against the map's character collision; the heading nearest to head-on first, in turn), turns to it, taps Jump with the stick forward (the dash),
    /// presses melee as soon as the dash attack's window opens and lets the lunge run into the wall; the wall stop is logged by <see cref="UpdateDashAttackFrame"/>.
    /// No wall found: null (the bot's other behaviour, which walks it elsewhere).
    /// </summary>
    PlayerInput? DashWallTestInput(PlayerState me, uint tick)
    {
        if (!DashWallTest || Loadouts.JumpPack(_config!, me)?.DashAttack is null || !me.Alive || _client?.Collision is not { } world) return null;
        if (Rush.IsDash(me.Rush)) return new PlayerInput(tick, 0f, 0f, _yaw, InputButtons.None, 0f);
        if (Evade.Evading(me) && !me.EvadeLanded)
        {
            bool press = Rush.CanDashAttack(me, _config!, world) && me.RushLock == 0 && !me.MeleeHeld;
            return new PlayerInput(tick, 0f, 0f, _yaw, press ? InputButtons.Melee : InputButtons.None, 0f);
        }
        if (me.Jump == JumpPhase.TakeOff) return new PlayerInput(tick, 0f, 1f, _yaw, InputButtons.None, 0f);   // released: the dash
        if (_connectedFor < _dashWallNext || !me.Grounded || me.Jump != JumpPhase.None || Evade.Evading(me) || me.EvadeLock > 0 || me.RushLock > 0
            || me.MeleeStep > 0 || me.Throwing || me.JumpHeld || me.Reaction != ReactionKind.None) return null;
        var from = me.Position + new System.Numerics.Vector3(0f, 0f, 1f);
        for (int k = 0; k < 16; k++)
        {
            float yaw = (_dashWallTries * 5 + k) * (MathF.PI / 8f);
            var dir = new System.Numerics.Vector3(-MathF.Sin(yaw), MathF.Cos(yaw), 0f);
            if (world.Character.Raycast(from, dir, 14f) is not { Distance: >= 7f and <= 12f } hit) continue;
            _dashWallTries++;
            _dashWallNext = _connectedFor + 3.0;
            _yaw = yaw;
            if (_bot) GD.Print($"t {_connectedFor:F2} s: id {_client.PlayerId} dash-wall-test #{_dashWallTries}: a wall {hit.Distance:F1} m away at heading {yaw:F2} (normal {hit.Normal.X:F2},{hit.Normal.Y:F2},{hit.Normal.Z:F2}); dashes to dash attack into it");
            return new PlayerInput(tick, 0f, 1f, _yaw, InputButtons.Jump, 0f);
        }
        return null;
    }

    // ---- --slash-test (feel/jump-pack test aid with --bot): the sword slash at a target, measured ----
    /// <summary><c>--slash-test</c> (test aid with <c>--bot</c>; not original behaviour): see <see cref="SlashTestInput"/>.</summary>
    bool SlashTest => _slashTest ??= OS.GetCmdlineUserArgs().Contains("--slash-test");
    bool? _slashTest;
    int _slashTestCycle;
    uint _slashTestStart;
    bool _slashTestPressed;
    double _slashPressAt = -1, _slashDiveAt = -1, _slashLandAt = -1, _slashFreeAt = -1;
    /// <summary>
    /// <c>--slash-jump</c> (with <c>--slash-test</c>; feel/buffer-speed test aid, not original behaviour): Space is held from the slash's landing through the
    /// recovery and the jump that follows (to its apex), so the input buffer's jump on the recovery's first free tick is measured (logged in ticks).
    /// </summary>
    bool SlashJump => _slashJump ??= OS.GetCmdlineUserArgs().Contains("--slash-jump");
    bool? _slashJump;
    bool _slashJumpHeld, _slashLiftLogged;

    System.Numerics.Vector3 _slashTargetAt;

    /// <summary>
    /// anim/air-slash-character capture aids with <c>--slash-test</c> (not original behaviour): <c>--slash-view-pitch &lt;rad&gt;</c> turns the view to this pitch once the slash is
    /// pressed and gives the bot the human follow camera (framing the dive and landing like the reference footage; the dive's direction is taken on the press tick only); <c>--slash-move-on &lt;s&gt;</c> pushes the stick
    /// right from that long after the landing for 1.2 s (the player moving on, as in the reference: the follow-through over a walk; sideways, the target stands in front).
    /// </summary>
    static readonly float SlashViewPitch = CmdFloat("slash-view-pitch");
    static readonly float SlashMoveOn = CmdFloat("slash-move-on");
    bool SlashMovingOn => float.IsFinite(SlashMoveOn) && _slashLandAt >= 0 && _connectedFor - _slashLandAt >= SlashMoveOn && _connectedFor - _slashLandAt < SlashMoveOn + 1.2;

    /// <summary>
    /// <c>--slash-test</c>: with a living enemy in sight 3 to 10 m away (horizontally) the own Assault / Raptor faces it and jumps (Jump held 6, 8, 10 or 12 ticks in turn: with the
    /// three jumps (feel/jump-pack-2) all the small jump, apex about 7.7 m; M45's middle jump gave about 2, 3.5, 5 and 6.5 m), presses melee at the apex aimed at the enemy's feet (the slash's dive goes toward the point under the aim) and then waits; farther away it walks to it.
    /// The log (<see cref="SlashTestFrame"/>) has, per slash, the press-to-dive, dive, landing-to-free and press-to-free times (client frames), where it landed against the target,
    /// and the frame time (headless bots run at a low frame rate: the times are quantised by it; the hits are in the client's hit reports).
    /// </summary>
    PlayerInput? SlashTestInput(PlayerState me, uint tick)
    {
        if (!SlashTest || Loadouts.JumpPack(_config!, me)?.Slash is null || !me.Alive) return null;
        PlayerState? target = null;
        float best = float.MaxValue;
        foreach (var o in _lastOthers)
        {
            if (!o.Alive || o.Team == me.Team) continue;
            float d = new System.Numerics.Vector2(o.Position.X - me.Position.X, o.Position.Y - me.Position.Y).Length();
            if (d < best) { best = d; target = o; }
        }
        if (target is not { } t) return null;
        float dx = t.Position.X - me.Position.X, dy = t.Position.Y - me.Position.Y;
        _yaw = MathF.Atan2(-dx, dy);
        int[] holds = [6, 8, 10, 12];
        switch (me.Jump)
        {
            case JumpPhase.TakeOff or JumpPhase.Ascent or JumpPhase.Hang or JumpPhase.Fall:
            {
                if (me.Jump is JumpPhase.Hang or JumpPhase.Fall) _slashJumpHeld = false;
                bool hold = _slashJumpHeld || tick - _slashTestStart < (uint)holds[_slashTestCycle % holds.Length];
                var b = hold ? InputButtons.Jump : InputButtons.None;
                if (!_slashTestPressed && me.Jump == JumpPhase.Hang)
                {
                    // Aim at the target's feet from the eye.
                    float above = me.Position.Z + _config!.EyeHeight - t.Position.Z;
                    _pitch = -MathF.Atan2(above, MathF.Max(0.5f, best));
                    b |= InputButtons.Melee; _slashTestPressed = true; _slashPressAt = _connectedFor; _slashTargetAt = t.Position;
                    _slashDiveAt = _slashLandAt = _slashFreeAt = -1;
                    GD.Print($"t {_connectedFor:F2} s: slash-test #{_slashTestCycle}: melee at the apex {JumpPack.AboveGround(me, _client!.Collision):F2} m above the ground, target P{t.PlayerId} {best:F1} m away (pitch {_pitch:F2})");
                }
                return new PlayerInput(tick, 0f, 0f, _yaw, b, _pitch);
            }
            case JumpPhase.Recover when SlashJump && me.JumpSlash:
                _slashJumpHeld = true;   // feel/buffer-speed: Space held through the landing
                return new PlayerInput(tick, 0f, 0f, _yaw, InputButtons.Jump, _pitch);
            case JumpPhase.Dive or JumpPhase.Recover or JumpPhase.Landed:
                // anim/air-slash-character (capture aid): the view's pitch after the press (the dive's direction is set on the press tick only).
                if (float.IsFinite(SlashViewPitch)) _pitch = SlashViewPitch;
                return new PlayerInput(tick, SlashMovingOn ? 1f : 0f, 0f, _yaw, InputButtons.None, _pitch);
        }
        if (SlashMovingOn) return new PlayerInput(tick, 1f, 0f, _yaw, InputButtons.None, float.IsFinite(SlashViewPitch) ? SlashViewPitch : 0f);
        _pitch = 0f;
        if (!me.Grounded || me.MeleeStep > 0 || me.JumpHeld || me.MeleeHeld || Rush.IsDash(me.Rush) || Evade.Evading(me)) return new PlayerInput(tick, 0f, 0f, _yaw, InputButtons.None, 0f);
        // feel/jump-pack-2: stand still for 2 s after a slash first (the get-up's clip plays out: --slash-trace sees all of it), then walk to or back off.
        if (_slashPressAt >= 0 && _connectedFor - _slashPressAt < 2.0) return new PlayerInput(tick, 0f, 0f, _yaw, InputButtons.None, 0f);
        if (best > 10f) return new PlayerInput(tick, 0f, 1f, _yaw, InputButtons.None, 0f);   // walk to it
        if (best < 3f) return new PlayerInput(tick, 0f, -1f, _yaw, InputButtons.None, 0f);   // back off
        if (_slashTestPressed) { _slashTestPressed = false; _slashTestCycle++; }
        _slashTestStart = tick;
        return new PlayerInput(tick, 0f, 0f, _yaw, InputButtons.Jump, 0f);
    }

    /// <summary>
    /// <c>--slash-trace</c> (feel/jump-pack-2 test aid with <c>--slash-test</c>): every frame from the slash's dive until 1.8 s after its impact, the own view, phase,
    /// the clip the character shows and its time, the cross-fade's weight and the right hand against the pelvis in the body's frame (forward, up; metres): a swing
    /// that runs backwards shows as the hand going back up after the hit.
    /// </summary>
    static readonly bool SlashTrace = OS.GetCmdlineUserArgs().Contains("--slash-trace");
    bool _slashClipSampled;

    void SlashTraceFrame(PlayerState me)
    {
        if (!SlashTrace || _local is null || !_jumpTracks.TryGetValue(me.PlayerId, out var t) || !t.Slash) return;
        double since = t.View is JumpView.Impact or JumpView.Getup ? _connectedFor - t.ImpactAt : -(_connectedFor - t.DiveAt);
        if (since > 1.8) return;
        string hand = "";
        if (_local.Skeleton is { } sk && sk.FindBone("RIG_R_Hand") is var h and >= 0 && sk.FindBone("RIG_Pelvis") is var p and >= 0)
        {
            var d = sk.GetBoneGlobalPose(h).Origin - sk.GetBoneGlobalPose(p).Origin;   // the skeleton's space
            hand = $"hand - pelvis {d.X:F2} {d.Y:F2} {d.Z:F2}";
            if (sk.FindBone("RIG_Prop_Rhand") is var prop and >= 0 || (prop = sk.FindBone("RIG_Prop_RHand")) >= 0)
            {
                var b = sk.GetBoneGlobalPose(prop).Basis;
                hand += $"; prop x {b.Column0.X:F2} {b.Column0.Y:F2} {b.Column0.Z:F2} y {b.Column1.X:F2} {b.Column1.Y:F2} {b.Column1.Z:F2}";
            }
        }
        var clip = _local.ClipTime;
        if (!_slashClipSampled && _local.EntryWeight <= 0f && clip is { } shownClip && _slashClip is not null && string.Equals(shownClip.Clip, _slashClip, StringComparison.OrdinalIgnoreCase)
            && _local.Skeleton is { } skel && skel.FindBone("RIG_R_Hand") is var rh and >= 0 && skel.FindBone("RIG_Pelvis") is var pv and >= 0)
        {
            // Once: the slash clip's own path (the hand against the pelvis and the right prop's axes) over its whole length, then the shown time again.
            _slashClipSampled = true;
            int prop = skel.FindBone("RIG_Prop_Rhand") is var pr and >= 0 ? pr : skel.FindBone("RIG_Prop_RHand");
            for (float time = 0f; time <= shownClip.Duration + 1e-3f; time += 0.05f)
            {
                _local.Pose(_slashClip, time, 0f);
                var d = skel.GetBoneGlobalPose(rh).Origin - skel.GetBoneGlobalPose(pv).Origin;
                var b = prop >= 0 ? skel.GetBoneGlobalPose(prop).Basis : Basis.Identity;
                GD.Print($"slash-clip {_slashClip} @ {time:F2}: hand - pelvis {d.X:F2} {d.Y:F2} {d.Z:F2}; prop y {b.Column1.X:F2} {b.Column1.Y:F2} {b.Column1.Z:F2}");
            }
            _local.Pose(_slashClip, shownClip.Time, 0f);
        }
        float speed = MathF.Sqrt(me.Velocity.X * me.Velocity.X + me.Velocity.Y * me.Velocity.Y);
        GD.Print($"slash-trace {(since < 0 ? $"dive {-since:F3}" : $"impact+{since:F3}")} s: view {t.View}, phase {me.Jump}/{me.JumpTicks}, shown {(clip is { } c ? $"{c.Clip} @ {c.Time:F3}/{c.Duration:F3}{(c.Looping ? " looping" : "")}" : "stance blend")}, " +
                 $"entry fade {_local.EntryWeight:F2}, speed {speed:F2} m/s, {hand}");
    }

    /// <summary><c>--slash-test</c>: each frame, the own slash's moments and the camera's steps (see <see cref="SlashTestInput"/>).</summary>
    void SlashTestFrame(PlayerState me)
    {
        SlashTraceFrame(me);
        if (!SlashTest) return;
        if (_slashPressAt < 0) return;
        if (_slashDiveAt < 0 && me.Jump == JumpPhase.Dive) _slashDiveAt = _connectedFor;
        if (_slashLandAt < 0 && me.Jump == JumpPhase.Recover) { _slashLandAt = _connectedFor; if (_slashDiveAt < 0) _slashDiveAt = _connectedFor; }
        if (_slashLandAt >= 0 && _slashFreeAt < 0 && me.Jump != JumpPhase.Recover && SlashJump)
        {
            // feel/buffer-speed: with Space held the recovery goes straight into the take-off (no free tick between): the take-off's tick count is the delay since.
            _slashLiftLogged = false;
            GD.Print($"t {_connectedFor:F2} s: slash-jump #{_slashTestCycle}: recovery over, now {me.Jump} {(me.Jump == JumpPhase.TakeOff ? $"(take-off started {me.JumpTicks} ticks ago, so {me.JumpTicks} ticks after the recovery's end; lift-off due 6 ticks after it)" : "(no take-off)")}");
        }
        if (SlashJump && !_slashLiftLogged && _slashFreeAt >= 0 && me.Jump == JumpPhase.Ascent)
        {
            _slashLiftLogged = true;
            GD.Print($"t {_connectedFor:F2} s: slash-jump #{_slashTestCycle}: lift-off seen ({me.JumpTicks} ticks after the press tick of the take-off)");
        }
        if (_slashLandAt >= 0 && _slashFreeAt < 0 && me.Jump != JumpPhase.Recover)
        {
            _slashFreeAt = _connectedFor;
            var landed = me.Position;
            float off = new System.Numerics.Vector2(landed.X - _slashTargetAt.X, landed.Y - _slashTargetAt.Y).Length();
            GD.Print($"t {_connectedFor:F2} s: slash-test #{_slashTestCycle}: press->dive {(_slashDiveAt - _slashPressAt) * 1000:F0} ms, dive {(_slashLandAt - _slashDiveAt) * 1000:F0} ms, land->free {(_slashFreeAt - _slashLandAt) * 1000:F0} ms, " +
                     $"press->free {(_slashFreeAt - _slashPressAt) * 1000:F0} ms; free {off:F2} m from where the target stood at the press (it moves); frame {Engine.GetFramesPerSecond():F0} fps");
        }
    }

    /// <summary>Dash attack and slash counts for the bot's 2 s log line.</summary>
    string DashAttackLogText() => $"dash attacks {_dashAttacksOwn} (seen {_dashAttacksSeen}, connected {_dashHits}, wall stops {_dashWalls}); slashes {_slashesOwn} (seen {_slashesSeen}); falls logged {_fallsLogged}";
}
