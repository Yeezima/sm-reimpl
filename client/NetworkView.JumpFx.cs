using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmContent;
using SmSim;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// M44 (the owner's playtest, 2026-10-28): the jump pack's flight effects and the ground pound's target marker, for the own player and the
/// others (client presentation only). Everything played is the data's: the pack's <c>jump_info</c> actions read by <see cref="JumpPackEffects"/>
/// (<c>action_ascending</c>: the thrust from the lift-off to the apex; <c>action_apex</c>; <c>action_descending_regular</c>: the fall;
/// <c>action_descending_smash</c>: the dive's thrust; <c>action_idle_by_power</c>: the pilot glow by power on the ground;
/// <c>action_smash_target_point</c>: the pound's target marker) and the jump evade's <c>fx_jump_evade</c> (the dash's burst), each at its
/// <c>play_fx</c> marker on the worn pack (<see cref="WeaponRig.TryWornMarker"/>; the Chaos pack names its inside centres). Ours: when each starts
/// and stops (the phases below: the remote players' from their slim flags and vertical speed), looping thrusts while their phase lasts, the
/// idle glow of other players at full power (their power is not on the wire), and the charge sounds of the idle entries for the own player only.
/// The sounds of the lift-off, apex, dive, landings and impact were already played (M26b, <see cref="SoundJump"/>); the dash's burst sound and
/// the idle charge sounds are new here.
/// <para>
/// The pound's target marker (<see cref="PoundIndicator"/>): while the own player could start a pound (<see cref="JumpPack.CanPound"/>), the data's
/// <c>mp_assault_land_reticule</c> (Chaos: <c>mp_chaos_assault_land_reticule</c>) at scale 1.5 stands where the simulation's dive would land if
/// the melee button were pressed now (<see cref="JumpPack.PoundLanding"/> on the client's own collision world, at the view's yaw and pitch).
/// </para>
/// </summary>
public partial class NetworkView
{
    enum PackMode : byte { Ground, Rise, Fall, Dive }

    sealed class JumpFxTrack
    {
        public PackMode Mode = PackMode.Ground;
        public readonly List<FxEffect> Looping = new();
        public readonly List<FxEffect> Idle = new();
        public float IdleLevel = float.NaN;
        public bool Dashing, Rushing;
        public int DashTicks;
        public EvadeDirection DashDirection;
    }

    readonly Dictionary<int, JumpFxTrack> _jumpFx = new();
    readonly List<FxEffect> _reticule = new();
    int _thrustFx, _apexFx, _diveFx, _dashFx, _idleFx, _reticuleShown, _burstFx;
    bool _reticuleLogged;

    /// <summary>Looping effects fade over this long when their phase ends (s; ours: the data's "stop_emitting" lets the particles finish).</summary>
    const double JumpFxFade = 0.35;
    /// <summary>wfx (ours, owner's item 26): the thrust's fade at the peak of the jump, seconds (short, so that it reads as stopping there).</summary>
    const double PeakFadeSeconds = 0.08;
    bool? _oldPackFall;
    bool OldPackFall => _oldPackFall ??= OS.GetCmdlineUserArgs().Contains("--old-pack-fall");

    /// <summary>
    /// fx/impact-pass (the owner, 2026-10-29: "shorten the trail by a lot, reference the video"; **ours**, tuned by eye against the reference flights): the pack effects' trails
    /// (<c>jumppack_thrust</c>'s <c>rocket_trail_00</c> / Chaos <c>firey_trail</c> streamers, 15 to 20 m at the play's scale 0.5) are laid along this share of their length in metres
    /// of the nozzle's own path (<see cref="FxEffect.TrailScale"/>): 2.5 to 3.3 m, starting at the nozzle every frame. Before: the length read as seconds at 4 m/s (up to the 4 s the
    /// path is kept), a column back to the take-off. <c>--old-pack-trail</c> (test aid, before / after) restores that.
    /// </summary>
    float PackTrailScale => _oldPackTrail ??= OS.GetCmdlineUserArgs().Contains("--old-pack-trail") ? 0f : PackTrailShare;
    float? _oldPackTrail;
    internal const float PackTrailShare = 1f / 3f;

    /// <summary>Called once per frame from <see cref="UpdateFx"/> (before the player draws): every player's pack effects and the own target marker.</summary>
    void FxJumpPacks(PlayerState me)
    {
        if (_client is null || _config is null || _catalogue is null || _fx is null) return;
        var seen = new HashSet<int>();
        JumpFxFor(me, own: true);
        OwnPackFxHide();
        seen.Add(me.PlayerId);
        foreach (var s in _lastOthers) { JumpFxFor(s, own: false); seen.Add(s.PlayerId); }
        foreach (var gone in _jumpFx.Keys.Where(id => !seen.Contains(id)).ToList()) { StopAll(_jumpFx[gone]); _jumpFx.Remove(gone); }
        PoundIndicator(me);
    }

    /// <summary>The place of a pack marker of a player (Godot world position and axes, +Y along the locator's +Y), or null when it has no worn model with it.</summary>
    /// <param name="exhaust">fx/impact-pass: the nozzle's exit along the marker's +Y (<see cref="WeaponRig.TryWornMarker(string, bool, out Transform3D)"/>), for the thrusts.</param>
    (Vector3 Position, Basis Axes)? PackMarker(int id, string marker, bool exhaust = false)
    {
        CharacterViewer? character = _client is not null && id == _client.PlayerId ? _local : _remotes.GetValueOrDefault(id);
        if (character?.Weapons is not { } rig || marker.Length == 0) return null;
        if (rig.TryWornMarker(marker, exhaust, out var world)) return (world.Origin, world.Basis);
        return null;
    }

    /// <summary>Starts the plays at their markers (attached when the data says so); returns the effects started.</summary>
    List<FxEffect> PlayOnPack(int id, IReadOnlyList<FxPlay> plays, bool repeat, string what, bool exhaust = false, bool flashOnly = false)
    {
        var started = new List<FxEffect>();
        exhaust &= PackTrailScale > 0f;   // --old-pack-trail: the old placement too
        foreach (var p in plays)
        {
            string marker = p.Marker;
            if (PackMarker(id, marker, exhaust) is not { } place)
            {
                if (_fxNoted.Add($"packmarker:{id % 2}:{marker}")) GD.Print($"  effects: {what}: no worn marker '{marker}' on P{id}'s pack; {p.Name} not shown");
                continue;
            }
            Func<(Vector3, Basis)?>? follow = p.Attached ? () => PackMarker(id, marker, exhaust) : null;
            float lifetime = flashOnly ? PackFlashSeconds : repeat ? 0f : p.Lifetime;
            if (_fx!.Play(p.Name, place.Position, place.Axes, p.Scale <= 0f ? 1f : p.Scale, null, follow, lifetime, 0.0, repeat, 1) is { } e)
            {
                e.FlashOnly = flashOnly;
                e.TrailScale = PackTrailScale;   // fx/impact-pass: the thrust's trail is the last few metres of the nozzle's path
                if (exhaust) e.BeamScale = PackTrailScale;   // ... and its flame a third as long
                started.Add(e);
                if (_client is not null && id == _client.PlayerId) { e.Hidden = _ownHidden; _ownPackFx.Add(e); }   // feel/camera-hitstop: hidden with the own character
            }
        }
        return started;
    }

    /// <summary>fx/jump-flash (the owner, 2026-11-02): how long the one blue flash at a jump, hop, dive, dash or air charge's start lives (s, ours). The flame and trail are gone.</summary>
    const float PackFlashSeconds = 0.35f;

    /// <summary>The pack's one blue flash at the start of a move.</summary>
    void PackFlash(int id, JumpPackEffects effects) => PlayOnPack(id, effects.Ascending, repeat: false, "flash", exhaust: true, flashOnly: true);

    static void Fade(List<FxEffect> effects, double fade)
    {
        foreach (var e in effects) { e.Repeat = false; e.Life = Math.Min(e.Life, Math.Max(0.0, e.Age) + fade); }
        effects.Clear();
    }

    static void StopAll(JumpFxTrack t) { Fade(t.Looping, JumpFxFade); Fade(t.Idle, 0.0); t.IdleLevel = float.NaN; }

    void JumpFxFor(PlayerState s, bool own)
    {
        if (!_jumpFx.TryGetValue(s.PlayerId, out var t)) _jumpFx[s.PlayerId] = t = new JumpFxTrack();
        var pack = Loadouts.JumpPack(_config!, s);
        var effects = pack is null ? null : _catalogue!.JumpPack(pack.Name);
        if (!s.Alive || effects is null) { StopAll(t); t.Mode = PackMode.Ground; t.Dashing = false; return; }

        // The phase as both records show it: pounding (own: dive; others: the pound flag in the air), in a jump in the air rising or not, else on the ground.
        bool pounding = own ? s.Jump == JumpPhase.Dive : JumpPack.Pounding(s.Jump) && !s.Grounded;
        bool flying = JumpPack.Jumping(s.Jump) && !(own ? s.Jump == JumpPhase.TakeOff : s.Grounded);
        // M45: the own player's thrust ends at the release that cuts the rise (others' records do not carry the cut: their vertical speed decides).
        var mode = pounding ? PackMode.Dive : flying ? (s.Velocity.Z > 0f && !(own && s.JumpCut) ? PackMode.Rise : PackMode.Fall) : PackMode.Ground;
        if (mode != t.Mode)
        {
            Fade(t.Looping, t.Mode == PackMode.Rise && mode == PackMode.Fall && !OldPackFall ? PeakFadeSeconds : JumpFxFade);   // wfx: the thrust is gone almost at once at the peak
            if (mode != PackMode.Ground) Fade(t.Idle, 0.15);
            t.IdleLevel = float.NaN;
            switch (mode)
            {
                case PackMode.Rise:
                    PackFlash(s.PlayerId, effects);
                    if (t.Mode == PackMode.Ground) TakeOffBurst(s, effects.Ascending);
                    _thrustFx++;
                    break;
                case PackMode.Fall:
                    if (t.Mode == PackMode.Rise) { PlayOnPack(s.PlayerId, effects.Apex, repeat: false, "apex"); _apexFx++; }
                    // wfx (owner, 2026-10-30, playtest item 26): the thrust and fire stop at the peak; the data's looping fall effect (jumppack_innate_no_power_combo) is not played (ours; --old-pack-fall restores it).
                    if (OldPackFall) t.Looping.AddRange(PlayOnPack(s.PlayerId, effects.Descending, repeat: true, "fall"));
                    break;
                case PackMode.Dive:
                    PackFlash(s.PlayerId, effects);
                    _diveFx++;
                    break;
            }
            if (_bot && own) GD.Print($"t {_connectedFor:F1} s: jump pack effects {t.Mode} -> {mode} ({t.Looping.Count} looping)");
            t.Mode = mode;
        }
        if (mode == PackMode.Ground)
        {
            // The pilot glow by power (others: full; their power is not on the wire, ours) and, for the own player, the entry's charge sound when it changes.
            float power = own ? s.JumpPower : pack!.MaxPower;
            var entry = effects.IdleByPower.Where(l => l.MinDrop <= power + 1e-4f).OrderByDescending(l => l.MinDrop).FirstOrDefault();
            float level = entry?.MinDrop ?? -1f;
            if (!level.Equals(t.IdleLevel))
            {
                bool rising = !float.IsNaN(t.IdleLevel) && level > t.IdleLevel;
                Fade(t.Idle, 0.0);
                if (entry is not null) { t.Idle.AddRange(PlayOnPack(s.PlayerId, entry.Plays, repeat: true, "idle")); _idleFx++; }
                if (own && rising) IdleChargeSound(s, pack!, level);
                t.IdleLevel = level;
            }
        }

        // The dash's burst (the jump evade's fx_jump_evade, with its sound) when a dash starts.
        bool dashing = Evade.Evading(s) && !s.EvadeLanded && Loadouts.Evade(_config!, s) is { Dash: true };
        bool restart = dashing && (!t.Dashing || s.EvadeTicks < t.DashTicks || s.EvadeDirection != t.DashDirection);
        if (restart && Loadouts.Evade(_config!, s) is { } dash)
        {
            if (_catalogue!.Dashes.GetValueOrDefault(dash.Name) is { Count: > 0 } burst) { PackFlash(s.PlayerId, effects); TakeOffBurst(s, burst); _dashFx++; }
            DashSound(s, dash, own);
            if (_bot) GD.Print($"t {_connectedFor:F1} s: id {_client!.PlayerId}{(own ? "" : $" sees P{s.PlayerId}")} dash {s.EvadeDirection} (burst {(_catalogue.Dashes.ContainsKey(dash.Name) ? "on" : "none")}; dashes shown {_dashFx})");
        }
        t.Dashing = dashing; t.DashTicks = s.EvadeTicks; t.DashDirection = s.EvadeDirection;
        // A charge started in the air (a hop): the same flash.
        bool rushing = s.Rush != RushPhase.None;
        if (rushing && !t.Rushing && !s.Grounded) PackFlash(s.PlayerId, effects);
        t.Rushing = rushing;
    }

    /// <summary>The thrust's ground burst under a player leaving the ground (<see cref="GroundPoundFx.PlayBurst"/>), within <see cref="PoundDetailRange"/> of the camera.</summary>
    void TakeOffBurst(PlayerState s, IReadOnlyList<FxPlay> thrust)
    {
        var (point, normal, _) = GroundUnder(s.Position);
        if (_camera is { } cam && cam.GlobalPosition.DistanceTo(point) > PoundDetailRange) return;
        if (GroundPoundFx.PlayBurst(_fx!, thrust, point, normal) > 0) _burstFx++;
    }

    /// <summary>The dash's burst sound: the jump evade's <c>fx_jump_evade</c> triggered action (<c>play_sm_jump_pack_launch</c>); the clip's FX_Evade_Start tag plays the rest (M26b).</summary>
    void DashSound(PlayerState s, EvadeSpec dash, bool own)
    {
        if (_sound is null || _soundTriggers?.File(dash.Name) is not { } file || !_soundTracks.TryGetValue(s.PlayerId, out var track)) return;
        PostAll(file.Tag("fx_jump_evade"), track.Emitter, own, "dash");
    }

    /// <summary>The own pack's charge sound when its power reaches an <c>action_idle_by_power</c> entry from below (<c>play_jump_pack_high_charge</c> at 55, <c>_low_charge</c> at 40).</summary>
    void IdleChargeSound(PlayerState s, JumpPackSpec pack, float level)
    {
        if (_sound is null || _soundTriggers?.File(pack.Name) is not { } file || !_soundTracks.TryGetValue(s.PlayerId, out var track)) return;
        var bindings = file.Of("action_idle_by_power").Where(b => b.Parameters.TryGetValue("min_power_level", out float l) && MathF.Abs(l - level) < 1e-3f).ToList();
        PostAll(bindings, track.Emitter, true, "pack charge");
    }

    /// <summary>
    /// The ground pound's target marker for the own player (see the class summary). The point is the simulation's dive stepped on the client's
    /// collision world (or the flat ground); none while no pound can start, when the dive would not land (a kill region or the void) or for a dead player.
    /// </summary>
    void PoundIndicator(PlayerState me)
    {
        if (PoundMarkerDemo && _camera is not null)   // ui/hud-3 screenshot aid: the marker stands on the ground 6.5 m ahead of the own player
        {
            var ahead = -_camera.GlobalBasis.Z; ahead.Y = 0f;
            _poundMarker ??= new PoundMarker(); if (_poundMarker.GetParent() is null) AddChild(_poundMarker);
            _poundMarker.Show(ToGodot(me.Position) + ahead.Normalized() * 6.5f + Vector3.Up * 0.08f, ahead, _connectedFor);
            return;
        }
        var pack = me.Alive ? Loadouts.JumpPack(_config!, me) : null;
        var plays = pack is null ? null : _catalogue!.JumpPack(pack.Name)?.SmashTarget;
        Num.Vector3? at = null;
        if (pack is not null && plays is { Count: > 0 } && JumpPack.CanPound(me, pack, _config!, _client!.Collision) && !NoPoundIndicator)   // protocol 30: not below the slash's 8 m
        {
            // The dive is stepped (up to PoundLookAheadTicks motor steps) only when the predicted state changes (a new tick) or the view
            // turned by more than about 0.3 degrees and a tick's time has passed since the last step: at most about the tick rate, not per frame.
            var key = (me.Position, me.JumpTicks, me.Jump);
            bool turned = MathF.Abs(_yaw - _poundAimYaw) > ViewEpsilon || MathF.Abs(_pitch - _poundAimPitch) > ViewEpsilon;
            if (!_poundAimValid || !key.Equals(_poundAimKey) || (turned && _connectedFor - _poundAimAt >= _config!.TickSeconds))
            {
                _poundAim = JumpPack.PoundLanding(me, _yaw, _pitch, pack, _config!, _client!.Collision, PoundLookAheadTicks);
                _poundAimKey = key; _poundAimYaw = _yaw; _poundAimPitch = _pitch; _poundAimAt = _connectedFor; _poundAimValid = true; _poundAimSteps++;
            }
            at = _poundAim;
        }
        else _poundAimValid = false;
        if (at is not { } point || _camera is null) { _poundMarker?.Hide2(); return; }
        // ui/hud-3: the owner's marker, four inward chevrons (PoundMarker.cs); the data's land-reticule effect is no longer played.
        if (_poundMarker is null) { _poundMarker = new PoundMarker(); AddChild(_poundMarker); _reticuleShown++; }
        _poundMarker.Show(ToGodot(point) + Vector3.Up * 0.08f, -_camera.GlobalBasis.Z, _connectedFor);
    }

    PoundMarker? _poundMarker;

    (Num.Vector3 Position, int Ticks, JumpPhase Phase) _poundAimKey;
    Num.Vector3? _poundAim;
    float _poundAimYaw, _poundAimPitch;
    double _poundAimAt;
    bool _poundAimValid;
    int _poundAimSteps;
    /// <summary>A view turn (radians) below which the target marker's dive is not stepped again (ours).</summary>
    const float ViewEpsilon = 0.005f;

    /// <summary>Ticks the target marker's dive is followed at most (4 s at 30 Hz; ours: a dive from any height of the alpha map lands well within it).</summary>
    const int PoundLookAheadTicks = 120;

    /// <summary><c>--pound-marker-demo</c> (screenshot aid): the pound's chevron marker stands on the ground ahead of the own player at all times.</summary>
    bool PoundMarkerDemo => _poundMarkerDemo ??= OS.GetCmdlineUserArgs().Contains("--pound-marker-demo");
    bool? _poundMarkerDemo;

    /// <summary><c>--no-pound-indicator</c> (test aid): no target marker.</summary>
    bool NoPoundIndicator => _noPoundIndicator ??= OS.GetCmdlineUserArgs().Contains("--no-pound-indicator");
    bool? _noPoundIndicator;

    /// <summary><c>--jump-test</c> (test aid with <c>--bot</c>; not original behaviour): the own jump-pack marine runs a fixed script instead of the bot's.</summary>
    bool JumpTest => _jumpTest ??= OS.GetCmdlineUserArgs().Contains("--jump-test");
    bool? _jumpTest;
    uint _jumpTestStart;
    int _jumpTestCycle = -1;

    /// <summary>
    /// The <c>--jump-test</c> script, in 8 s cycles from when the marine stands on the ground: a jump (stick forward for 0.4 s) looking 0.5 rad down, since M45 in even
    /// cycles a middle jump (Space let go 20 ticks after the press; feel/jump-pack-2, with the three jumps: the small jump, let go 8 ticks after it) and in odd ones the full jump (Jump held to the apex; with the three jumps 13 ticks),
    /// in odd cycles a pound 1.6 s after the press (at about the apex), then two dashes (taps) forward-right and back-left 1 s apart. Null for a class without a pack.
    /// </summary>
    PlayerInput? JumpTestInput(PlayerState me, uint tick)
    {
        if (Loadouts.JumpPack(_config!, me) is null || !me.Alive) return null;
        const uint Cycle = 240;
        if (_jumpTestCycle < 0 || tick - _jumpTestStart >= Cycle)
        {
            if (!me.Grounded || me.Jump != JumpPhase.None || Evade.Evading(me)) return new PlayerInput(tick, 0f, 0f, _yaw, InputButtons.None, _pitch);
            _jumpTestStart = tick; _jumpTestCycle++;
        }
        uint k = tick - _jumpTestStart;
        _pitch = -0.5f;
        var buttons = InputButtons.None;
        float mx = 0f, my = 0f;
        // M45: a release while rising cuts the rise, so the full jump holds Jump to the apex (JumpPack.FullRiseTicks; the stick forward for the first 0.4 s).
        var pack = Loadouts.JumpPack(_config!, me)!;
        // feel/jump-pack-2: with the three jumps the full jump needs Jump down on tick 12 (13 held ticks), the small jump any release from tick 6 to 12 (here 8).
        uint full = pack.ReleaseCut ? (uint)JumpPack.FullRiseTicks(pack, _config!) : pack.HoldTiers ? 13u : 12u;
        // M45: even cycles a middle jump (Space let go 20 ticks after the press, about 12 m), odd cycles the full jump and its pound.
        uint hold = _jumpTestCycle % 2 == 0 && pack.HoldTiers ? 8u : pack.ReleaseCut && _jumpTestCycle % 2 == 0 ? 20u : full;
        if (k < hold) { buttons = InputButtons.Jump; if (k < 12) my = 1f; }
        bool pound = OS.GetCmdlineUserArgs().Contains("--jump-test-pound");   // feel/weight-impact capture aid: the melee press at the full jump's apex, above MinPoundHeight
        if ((pound ? k is 29 : k is 48) && _jumpTestCycle % 2 == 1) buttons |= InputButtons.Melee;
        if (k is 150) { buttons = InputButtons.Jump; mx = 1f; my = 1f; }
        else if (k is 151) { mx = 1f; my = 1f; }
        else if (k is 190) { buttons = InputButtons.Jump; mx = -1f; my = -1f; }
        else if (k is 191) { mx = -1f; my = -1f; }
        return new PlayerInput(tick, mx, my, _yaw, buttons, _pitch);
    }

    /// <summary>For the effects log line.</summary>
    string JumpFxLog() => $"pack fx: thrust {_thrustFx}, apex {_apexFx}, dive {_diveFx}, dash {_dashFx}, ground bursts {_burstFx}, idle {_idleFx}, target marker shown {_reticuleShown} (dives stepped {_poundAimSteps})";
}
