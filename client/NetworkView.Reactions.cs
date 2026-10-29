using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmContent;
using SmSim;

namespace SmClient;

/// <summary>
/// M28 (protocol 17): hit reactions and heavy strikes in the networked view. The simulation runs the reaction (it stops the
/// player, moves the body by the knockback; <see cref="Reaction"/>); the client poses the original reaction clip
/// (<see cref="ReactionClip.Animation"/>: the light interrupt <c>SM_REC_Light_Itrpt_*</c>, the stun <c>SM_MP_REC_Grenade_*</c>,
/// the stagger <c>SM_REC_Stagger_*</c>) at the reaction's time (<see cref="Reaction.Time"/>; remote players at the snapshot's
/// value), and the heavy strikes' clips (<see cref="MpClasses.MeleeHeavyAnimationsByWeapon"/>) like the light attacks'.
/// The own player's view shakes briefly when a reaction starts and turns white while blinded (the blind grenade's
/// <c>do_blindness</c>; the files call it the krak grenade; M37: it only blinds, see <see cref="SplashSpec.BlindOnly"/>); both are **placeholder** looks (spec/behavior.md): the original's camera effects for reactions and its
/// blindness screen effect are not decoded.
/// </summary>
public partial class NetworkView
{
    /// <summary>The heavy strikes' clips by melee wargear (checked against the rig), and per class for its default combo.</summary>
    readonly Dictionary<string, string?[]> _heavyAnimationsByWeapon = new(StringComparer.OrdinalIgnoreCase);
    string?[][] _heavyAnimations = [];
    /// <summary>Reaction clips the rig has and decodes (others show the idle/run animation, noted once).</summary>
    readonly HashSet<string> _reactionClips = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Placeholder shake: seconds, and peak angle in radians by kind (interrupt, stun, stagger).</summary>
    const float ShakeSeconds = 0.35f;
    static readonly float[] ShakeAngle = [0.012f, 0.03f, 0.022f];
    float _shakeLeft, _shakePeak;
    int _lastReactionTicks;
    ReactionKind _lastReaction;
    int _reactionsOwn, _reactionsSeen;
    readonly Dictionary<int, (ReactionKind Kind, int Ticks)> _remoteReactions = new();
    ColorRect? _blindRect;
    int _lastBlindTicks;

    /// <summary>The heavy clips per class and per weapon, and the reaction clips; notes for anything missing (after <see cref="SetUpClasses"/>).</summary>
    void SetUpReactions()
    {
        var config = _config!;
        var names = _assets!.Model.Rig.AnimationNames;
        bool Usable(string? name) => name is not null && names.Contains(name, StringComparer.OrdinalIgnoreCase) && _assets.Animation(name) is not null;
        // Ork O7c: the Ork's charge, choppa heavy strike and the clips of its reactions are the boy's (checked against the Ork rig in NetworkView.OrkActions.cs), not marine-rig clips.
        var marineClasses = config.Classes.Where(k => k.Race != Race.Orks).ToList();
        foreach (var rush in marineClasses.SelectMany(k => k.MeleeChoices.Select(m => m.Rush).Append(k.MeleeRush)).OfType<RushSpec>().SelectMany(r => new[] { r.Rush, r.Collision }).Distinct())
        {
            if (Usable(rush.Animation)) _reactionClips.Add(rush.Animation);
            else if (_reactionClips.Add("#missing " + rush.Animation)) _notes.Add($"charge clip '{rush.Animation}' not in the rig or not decodable; the charge shows the idle/run animation");
        }
        SetUpDashAttack();   // protocol 30: the dash attack's and the sword slash's clips (NetworkView.DashAttack.cs)
        foreach (var spec in config.Reactions)
            foreach (var clip in spec.Clips)
            {
                if (Usable(clip.Animation)) _reactionClips.Add(clip.Animation);
                else if (_reactionClips.Add("#missing " + clip.Animation)) _notes.Add($"reaction clip '{clip.Animation}' not in the rig or not decodable; that reaction shows the idle/run animation");
            }
        _heavyAnimations = new string?[config.Classes.Count][];
        if (_classes is null) return;
        foreach (var combo in marineClasses.SelectMany(k => k.MeleeChoices))
        {
            if (_heavyAnimationsByWeapon.ContainsKey(combo.Wargear) || combo.Heavy.Count == 0) continue;
            _classes.MeleeHeavyAnimationsByWeapon.TryGetValue(combo.Wargear, out var clips);
            var result = new string?[combo.Heavy.Count];
            for (int i = 0; i < result.Length; i++)
            {
                string? name = clips is not null && i < clips.Count ? clips[i] : null;
                if (Usable(name)) result[i] = name;
                else _notes.Add($"melee weapon {combo.Wargear} heavy strike {i + 1}: clip '{name ?? "(none)"}' not usable; it shows the idle/run animation");
            }
            _heavyAnimationsByWeapon[combo.Wargear] = result;
        }
        for (int k = 0; k < config.Classes.Count; k++)
        {
            var first = config.Classes[k].MeleeChoices.FirstOrDefault();
            _heavyAnimations[k] = first is not null && _heavyAnimationsByWeapon.TryGetValue(first.Wargear, out var byWeapon) ? byWeapon : [];
        }
    }

    /// <summary>The clips of the player's heavy strikes (its chosen weapon's, else its class default's), or null.</summary>
    string?[]? HeavyClips(PlayerState s)
    {
        if (MeleeWargear(s) is { } wargear && _heavyAnimationsByWeapon.TryGetValue(wargear, out var byWeapon)) return byWeapon;
        return s.Class >= 0 && s.Class < _heavyAnimations.Length ? _heavyAnimations[s.Class] : null;
    }

    /// <summary>The attack clip for the player's melee step (light or heavy list), or null.</summary>
    string? AttackClip(PlayerState s)
    {
        if (s.MeleeStep <= 0 || (s.MeleeHeavy ? HeavyClips(s) : MeleeClips(s)) is not { } clips || s.MeleeStep > clips.Length) return null;
        return clips[s.MeleeStep - 1];
    }

    /// <summary>The reaction clip and time to pose for a player (null when it is not reacting or the rig lacks the clip).</summary>
    (string Clip, float Time)? ReactionPose(PlayerState s)
    {
        if (!s.Alive || _config is null || Reaction.ClipOf(s, _config) is not { } clip || !_reactionClips.Contains(clip.Animation)) return null;
        return (clip.Animation, Math.Clamp(Reaction.Time(s, _config), 0f, clip.Duration));
    }

    /// <summary>The charge's clip (rush or collision stop) and time to pose for a player (M29), or null.</summary>
    (string Clip, float Time)? RushPose(PlayerState s)
    {
        if (!s.Alive || _config is null || Rush.MoveOf(_config, s) is not { } move || !_reactionClips.Contains(move.Animation)) return null;   // protocol 30: the dash attack's phases too
        return (move.Animation, Math.Clamp(Rush.Time(s, _config), 0f, move.ClipLength));
    }

    double _botRushNext = 6.0;
    int _rushesOwn, _rushesSeen;
    readonly Dictionary<int, RushPhase> _remoteRush = new();

    /// <summary>
    /// The bot's charge (test script, M29): every ~8 s, with a target 4 to 9 m away and a charge available, it runs at the target
    /// (W, sprint) and presses F (the heavy button) once the charge can start (protocol 39: W held is enough); then it lets the rush play without input.
    /// <c>--bot-charge-dirs</c> (NetworkView.ChargeDirections.cs): W only for <see cref="ChargeDirsRunUp"/> s, turned away from the target, then F with W, W + A, W + D in turn.
    /// </summary>
    PlayerInput? BotRushInput(PlayerState me, uint tick, float distanceSquared)
    {
        if (Loadouts.Rush(_config!, me) is not { } spec || !me.Alive) return null;
        if (me.Rush != RushPhase.None) { _botChargeRunSince = -1; return new PlayerInput(tick, 0f, 0f, _yaw, InputButtons.None, _pitch); }
        bool dirs = BotChargeDirs;
        // --bot-charge-dirs runs away from the target during its run-up, so it starts at 3 to 30 m (it needs no target in reach).
        if (_connectedFor < _botRushNext || (dirs ? distanceSquared is < 9f or > 900f : distanceSquared is < 16f or > 81f) || me.RushLock > 0) { _botChargeRunSince = -1; return null; }
        if (dirs) _yaw += MathF.PI;   // away from the target: no melee lock steers the charge (its rule: a locked charge starts along the view)
        var run = new PlayerInput(tick, 0f, 1f, _yaw, dirs ? InputButtons.None : InputButtons.Sprint, _pitch);
        if (dirs && _botChargeRunSince < 0 && me.Grounded && me.MeleeStep == 0) _botChargeRunSince = _connectedFor;
        bool runUp = dirs && (_botChargeRunSince < 0 || _connectedFor - _botChargeRunSince < ChargeDirsRunUp);
        if (runUp || !Rush.CanStart(me, run, spec, _config!)) return me.Grounded && me.MeleeStep == 0 ? run : null;
        _botRushNext = _connectedFor + 8.0;
        _botChargeRunSince = -1;
        float forward = me.Velocity.X * -MathF.Sin(_yaw) + me.Velocity.Y * MathF.Cos(_yaw);
        var (x, keys) = dirs ? ChargeDirs[_botChargeDir++ % ChargeDirs.Length] : (0f, "W+Shift");
        NotePress(_yaw, keys, forward);
        GD.Print($"t {_connectedFor:F1} s: id {_client!.PlayerId} presses F with {keys} at {forward:F2} m/s forward (protocol 39: W held is enough)");
        return run with { MoveX = x, Buttons = run.Buttons | (me.MeleeHeavyHeld ? InputButtons.None : InputButtons.MeleeHeavy) };
    }

    /// <summary>
    /// The own player's reaction effects each frame: a short shake when a reaction starts (placeholder), the blindness overlay
    /// (placeholder: white, full until the last second, then fading), and the bot's log lines.
    /// </summary>
    void UpdateReactions(PlayerState me, IReadOnlyList<PlayerState> others, float delta)
    {
        bool started = me.Alive && me.Reaction != ReactionKind.None && (_lastReaction == ReactionKind.None || me.Reaction != _lastReaction || me.ReactionTicks < _lastReactionTicks);
        if (started)
        {
            _reactionsOwn++;
            if (HitStopOn) _shakeLeft = ShakeSeconds;   // owner 2026-10-29: no hit shake (opt-in --hit-stop)
            _shakePeak = ShakeAngle[Math.Clamp((int)me.Reaction - 1, 0, ShakeAngle.Length - 1)];
            if (_bot)
                GD.Print($"t {_connectedFor:F1} s: id {_client!.PlayerId} reacts {me.Reaction} {me.ReactionDirection} ({Reaction.ClipOf(me, _config!)?.Animation}); knockback {me.PushDistance / 16f:F2} m over {me.PushTicks} ticks; reactions {_reactionsOwn}; prediction corrections {Corrections()}");
        }
        _lastReaction = me.Alive ? me.Reaction : ReactionKind.None;
        // M29: the charge's starts and contacts, for the bot's log.
        if (me.Alive && me.Rush == RushPhase.Rush && me.RushTicks <= 2 && _remoteRush.GetValueOrDefault(-1) != RushPhase.Rush)
        {
            _rushesOwn++;
            if (_bot) GD.Print($"t {_connectedFor:F1} s: id {_client!.PlayerId} charges (rushes {_rushesOwn}); prediction corrections {Corrections()}");
        }
        if (me.Alive && me.Rush == RushPhase.Collision && _remoteRush.GetValueOrDefault(-1) == RushPhase.Rush && _bot)
            GD.Print($"t {_connectedFor:F1} s: id {_client!.PlayerId} charge stopped (contact or wall)");
        _remoteRush[-1] = me.Alive ? me.Rush : RushPhase.None;
        foreach (var o in others)
        {
            var was = _remoteRush.GetValueOrDefault(o.PlayerId);
            if (o.Alive && o.Rush == RushPhase.Rush && was != RushPhase.Rush)
            {
                _rushesSeen++;
                if (_bot) GD.Print($"t {_connectedFor:F1} s: id {_client!.PlayerId} sees P{o.PlayerId} charge (seen {_rushesSeen})");
            }
            _remoteRush[o.PlayerId] = o.Alive ? o.Rush : RushPhase.None;
        }
        _lastReactionTicks = me.ReactionTicks;
        foreach (var o in others)
        {
            var was = _remoteReactions.GetValueOrDefault(o.PlayerId);
            if (o.Alive && o.Reaction != ReactionKind.None && (was.Kind == ReactionKind.None || o.Reaction != was.Kind || o.ReactionTicks < was.Ticks))
            {
                _reactionsSeen++;
                if (_bot) GD.Print($"t {_connectedFor:F1} s: id {_client!.PlayerId} sees P{o.PlayerId} react {o.Reaction} {o.ReactionDirection} (seen {_reactionsSeen})");
            }
            _remoteReactions[o.PlayerId] = (o.Alive ? o.Reaction : ReactionKind.None, o.ReactionTicks);
        }
        foreach (var gone in _remoteReactions.Keys.Where(id => others.All(o => o.PlayerId != id)).ToList()) _remoteReactions.Remove(gone);

        // Shake: a decaying wobble of the view about its own axes (placeholder; only the camera, never the simulation's aim).
        if (_shakeLeft > 0f && _camera is not null)
        {
            _shakeLeft = MathF.Max(0f, _shakeLeft - delta);
            float k = _shakeLeft / ShakeSeconds;
            float t = (ShakeSeconds - _shakeLeft) * 40f;
            float yaw = _shakePeak * k * MathF.Sin(t), pitch = _shakePeak * k * 0.8f * MathF.Sin(t * 1.37f + 1f);
            var basis = _camera.GlobalBasis;
            basis = basis.Rotated(basis.Y.Normalized(), yaw);
            basis = basis.Rotated(basis.X.Normalized(), pitch);
            _camera.GlobalBasis = basis.Orthonormalized();
        }

        // Blindness: white over the view (placeholder look).
        if (_blindRect is null)
        {
            var layer = new CanvasLayer { Name = "Blindness", Layer = 0 };   // under the HUD, as the death vignette
            _blindRect = new ColorRect { Name = "Blind", MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false, Color = new Color(1f, 1f, 1f, 0f) };
            _blindRect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            layer.AddChild(_blindRect);
            AddChild(layer);
        }
        // Bot log (M37 live checks): a new or longer blindness from a blind grenade.
        if (_bot && me.Alive && me.BlindTicks > _lastBlindTicks + 1)
            GD.Print($"t {_connectedFor:F1} s: blinded for {me.BlindTicks * _config!.TickSeconds:F1} s (blind grenade)");
        _lastBlindTicks = me.Alive ? me.BlindTicks : 0;
        float left = me.Alive ? me.BlindTicks * _config!.TickSeconds : 0f;
        float alpha = Math.Clamp(left, 0f, 1f) * 0.92f;
        _blindRect.Visible = alpha > 0.005f;
        if (_blindRect.Visible) _blindRect.Color = new Color(1f, 1f, 0.97f, alpha);
    }
}
