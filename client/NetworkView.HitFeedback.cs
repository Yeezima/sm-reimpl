using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmContent;
using SmFormats.Audio;
using SmNet;
using SmSim;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// M38: feedback on hits (client only, not simulated).
/// <list type="bullet">
/// <item><b>Screen flash for the local player</b> (owner's description of the original footage, 2026-10-28): damage to the armour shield flashes the screen edges <b>yellow</b>, damage to health
/// <b>red</b>. The install has the effects behind it: <c>screen_dmg_&lt;position&gt;</c> (health: a red bloom, (255, 29, 16), and a warm streak at a screen edge, 0.4 s) and
/// <c>screen_dmg_&lt;position&gt;_ar</c> (armour: a white-gold bloom and the <c>add_point_horizon_beam_gold</c> streak) in <c>master_fx</c>, screen-space emitters the effect player does
/// not draw; which one plays when is in code (not in any attribute file), and the player blueprint's <c>take_ranged_damage_action</c> adds a faint red camera tint (0.05, 0.1 s). We draw
/// an edge flash of our own after those: red for health damage, yellow for armour damage (the owner's colour), at the screen edge toward the attacker when a shot
/// event names him (the data's eight positions, ours: as a smooth direction), all round otherwise. Durations follow the data's bloom keys (about 0.4 s); the opacity is ours (readable without
/// hiding the view: edges only, the centre stays clear).</item>
/// <item><b>Armour and flesh on a player hit</b>: a ranged hit's sound plays the armour layer while the victim has armour and the flesh layer once it is gone, both when unknown or in melee (<see cref="PostPlayerHitSound"/>, owner 2026-10-30; `--combined-hit-sounds` restores both always); the effects use the victim's armour state where it is really known
/// (<see cref="ArmourUpForHit"/>), else the armour variant.</item>
/// </list>
/// </summary>
public partial class NetworkView
{
    /// <summary><c>--hit-armour up|down</c> (test aid): every player hit reads as on raised (<c>up</c>) or broken (<c>down</c>) armour, whatever is known.</summary>
    public string? HitArmour { get; init; }

    /// <summary>
    /// <c>--damage-demo [armour|health|both|cycle]</c> (test aid): <c>cycle</c> (the default) flashes every 6 s (armour from the right at 1 s, health from behind at 3 s); the others hold that
    /// flash at its start (for a screenshot).
    /// </summary>
    public string? DamageDemo { get; init; }

    bool? ForcedArmour => HitArmour switch { "up" => true, "down" => false, _ => null };

    // ---- who was hit, and was the armour up ----

    /// <summary>Own shield and feet position over the last 1.5 s (time, shield, position), for hits on the own player.</summary>
    readonly List<(double T, float Shield, Num.Vector3 Position)> _shieldTrail = new();

    void TrackOwnShield(PlayerState me)
    {
        _shieldTrail.Add((_connectedFor, me.Shield, me.Position));
        while (_shieldTrail.Count > 2 && _connectedFor - _shieldTrail[0].T > 1.5) _shieldTrail.RemoveAt(0);
    }

    /// <summary>The own shield <paramref name="seconds"/> ago (the oldest value kept when the trail is shorter).</summary>
    float OwnShieldBefore(double seconds)
    {
        for (int i = _shieldTrail.Count - 1; i >= 0; i--)
            if (_connectedFor - _shieldTrail[i].T >= seconds) return _shieldTrail[i].Shield;
        return _shieldTrail.Count > 0 ? _shieldTrail[0].Shield : _client?.Predicted.Shield ?? 0f;
    }

    /// <summary>
    /// The player whose body a hit at <paramref name="end"/> (game space) struck: the shown other player, or the own player, nearest to it within a body's reach (distance over reach, so
    /// that a teammate beside the own player does not take the hit); -1 for none. The shooter is never the victim. The own player's reach is matched against its positions of the last
    /// 0.4 s (a lag-compensated shot end differs from the predicted position by 0.6 to 1.2 m at normal latency, more while moving), the others' against where they are shown.
    /// </summary>
    int VictimAt(Num.Vector3 end, int shooter = -1)
    {
        const float Reach = 1.3f, OwnReach = 1.5f;
        int best = -1; float bestScore = 1f;
        void Consider(int id, Num.Vector3 feet, float reach)
        {
            float dz = end.Z - feet.Z, horizontal = MathF.Sqrt((end.X - feet.X) * (end.X - feet.X) + (end.Y - feet.Y) * (end.Y - feet.Y));
            if (dz < -0.3f || dz > 2.4f) return;
            float score = horizontal / reach;
            if (score < bestScore) { bestScore = score; best = id; }
        }
        foreach (var o in _lastOthers) if (o.Alive && o.PlayerId != shooter) Consider(o.PlayerId, o.Position, Reach);
        if (_client is { } c && c.PlayerId != shooter && c.Predicted.Alive)
        {
            Consider(c.PlayerId, c.Predicted.Position, OwnReach);
            foreach (var past in _shieldTrail) if (_connectedFor - past.T <= 0.4) Consider(c.PlayerId, past.Position, OwnReach);
        }
        return best;
    }

    /// <summary>
    /// Whether the victim's armour shield was up when a player hit landed, where the client really knows (<see cref="HitFeedbackRules.ArmourUp"/>); null: unknown, and the effects then use
    /// the armour variant (<see cref="FxImpact"/>). No guessing (owner's direction, M38). Ranged hit sounds use it too, to pick the armour or the flesh layer (<see cref="PostPlayerHitSound"/>).
    /// <para>
    /// Protocol 22: <paramref name="armoured"/> is the server's armour bit, <c>ShotView.Armoured</c> for others' shots (<c>FxRemoteShot</c>). Own hits play from the
    /// prediction before the hit block arrives, so <c>FxOwnShot</c> uses <see cref="OwnHitArmoured"/>: the previous hit report on the same victim.
    /// </para>
    /// </summary>
    bool? ArmourUpForHit(ShotHit hit, int victim, bool own, bool? armoured = null) =>
        HitFeedbackRules.ArmourUp(hit, ForcedArmour, _client is { } c && victim == c.PlayerId, OwnShieldBefore(0.25), armoured);

    /// <summary>
    /// For an own hit predicted before its report: whether the victim's armour is still up, from the last hit report on the same victim within 2 s (its shield
    /// after that hit, <c>HitReport.VictimShield</c>: above 0, the next hit strikes armour; this is more current than that report's <c>LastArmoured</c>, the
    /// state before it); null for another victim or an old report (the armour variant, no guessing).
    /// </summary>
    bool? OwnHitArmoured(int victim) => victim == _lastVictim && _connectedFor - _lastHitAt < 2.0 ? _lastVictimShield > 0f : null;

    const uint ImpactStateGroup = 0x6f0f7f7c;

    /// <summary>
    /// The impact sound of a bullet that struck a player (owner's direction 2026-10-28, "combine both", a deliberate change from the original's state switching): the armour impact
    /// (<c>material_type</c> = <c>ceramite_armour</c>, the random metal clangs) and the flesh layer together, once per hit, whatever the victim's shield. The event's last action plays its
    /// six-sound flesh layer while the impact state group is the broken-armour state, so the state is set to <c>set_state_impact_broken_armor</c> for this one post (the post resolves
    /// synchronously) and put back, so that level impacts and the own armour events keep their own state. One post: each layer plays once, nothing doubles.
    /// </summary>
    void PostPlayerHitSound(string hitSound, SoundEmitter at, VoiceOffset offset = default, bool? armoured = null)
    {
        if (_audio is null || _sound is null) return;
        // fx/hit-armour-sound (owner's item 59): armour up plays the data's ceramite_armour case (the hard clang, armour state), armour down the flesh case and flesh layer; unknown plays both.
        var layers = HitSoundRules.Of(armoured, CombinedHitSounds);
        at.Switches.Set(AudioCatalog.MaterialType, Fnv.Hash32(layers == HitLayers.Flesh ? "flesh" : "ceramite_armour"));
        var before = BeginImpactState(flesh: layers != HitLayers.Armour);
        var play = _sound.Post(hitSound, at, offset: offset);
        EndImpactState(before);
        LogImpact($"body hit ({layers.ToString().ToLowerInvariant()})", hitSound, at, offset, play);
    }

    /// <summary><c>--combined-hit-sounds</c> (test aid): every player hit plays the armour and the flesh layers together, whatever the victim's armour (the behaviour before fx/hit-armour-sound).</summary>
    static readonly bool CombinedHitSounds = OS.GetCmdlineUserArgs().Contains("--combined-hit-sounds");

    /// <summary>
    /// A level impact (a wall, a floor): the hit event with the armour impact state, explicitly, so that the flesh layer never leaks onto it (the state is global; the own
    /// <c>armourshield_broken</c> / <c>armourshield_restored</c> events, the original's way of setting it, may have left the broken-armour state behind). The per-post state setting is ours.
    /// </summary>
    void PostLevelImpact(string hitSound, SoundEmitter at, VoiceOffset offset = default)
    {
        if (_sound is null) return;
        var before = BeginImpactState(flesh: false);
        var play = _sound.Post(hitSound, at, offset: offset);
        EndImpactState(before);
        LogImpact("impact", hitSound, at, offset, play);
    }

    /// <summary>Sets the impact state for the posts that follow (flesh: the broken-armour state, else the armour state); returns the state it had, for <see cref="EndImpactState"/>.</summary>
    uint? BeginImpactState(bool flesh)
    {
        if (_audio is null) return null;
        var before = _audio.State(ImpactStateGroup);
        _audio.Post(flesh ? "set_state_impact_broken_armor" : "set_state_impact_armor", new SoundSwitches());
        return before;
    }

    void EndImpactState(uint? before)
    {
        // An unset group goes back to Wwise's None (sound/impacts review), not to the state this post set.
        if (_audio is not null) _audio.SetState(ImpactStateGroup, before ?? AudioCatalog.NoneState);
    }

    // ---- the victim's own effects ----

    readonly Dictionary<int, double> _fxArmourFxAt = new();
    int _fxArmourFx;

    /// <summary>The place of a marker on a player's skeleton, as a follower for an attached effect; null when the player or the bone is not there.</summary>
    Func<(Vector3 Position, Basis Axes)?>? BodyMarker(int victim, string marker)
    {
        CharacterViewer? c = _client is not null && victim == _client.PlayerId ? _local : _remotes.GetValueOrDefault(victim);
        if (c?.Skeleton is not { } skeleton) return null;
        int bone = skeleton.FindBone(marker);
        if (bone < 0) return null;
        return () => GodotObject.IsInstanceValid(c) && GodotObject.IsInstanceValid(skeleton) && c.IsInsideTree() ? ((skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(bone)).Origin, Basis.Identity) : null;
    }

    /// <summary>
    /// What the victim shows for a hit on raised armour (<c>armourshield_hit</c>: <c>sm_armor_dmg</c> at the hit, the shoulder pads and body glows) or on broken armour
    /// (<c>armourshield_inactive_hit</c>: the red variants), from the player blueprint (<see cref="CharacterSurface"/>). Effects on markers of the own body are not drawn (the camera is inside it).
    /// </summary>
    void FxArmourHit(bool up, Vector3 end, Vector3 dir, int victim, double delay)
    {
        if (_catalogue is null || _fx is null || _client is null) return;
        bool own = victim == _client.PlayerId;
        var plays = up ? _catalogue.Player.ArmourHit : _catalogue.Player.ArmourInactiveHit;
        bool glows = victim >= 0 && !own && _connectedFor - _fxArmourFxAt.GetValueOrDefault(victim, double.NegativeInfinity) >= 0.15;   // at most a set of glows per 0.15 s and victim
        if (glows)
        {
            _fxArmourFxAt[victim] = _connectedFor;
            if (_fxArmourFxAt.Count > 24) foreach (var gone in _fxArmourFxAt.Keys.Where(id => id != _client.PlayerId && !_remotes.ContainsKey(id)).ToList()) _fxArmourFxAt.Remove(gone);   // players who left
        }
        foreach (var p in plays)
        {
            float scale = p.Scale <= 0f ? 1f : p.Scale;
            if (p.TriggerPoint == "hitpoint")
            {
                if (own) continue;
                if (_fx.Play(p.Name, end, AxesFromY(-dir), scale, null, null, p.Lifetime, delay, false, 0) is not null) _fxArmourFx++;
            }
            else if (glows && p.Marker.Length > 0 && BodyMarker(victim, p.Marker) is { } follow && follow() is { } place)
            {
                if (_fx.Play(p.Name, place.Position, Basis.Identity, scale, null, follow, p.Lifetime, delay, false, 0) is not null) _fxArmourFx++;
            }
        }
    }

    // ---- decals do not paint players ----

    /// <summary>Render layer of characters, their weapons and ragdolls (decals only project on layer 1; <c>Decal.CullMask</c>).</summary>
    const uint CharacterRenderLayer = 2;

    /// <summary>Moves every mesh that enters the tree under a character, a ragdoll or a magazine drop to <see cref="CharacterRenderLayer"/> (the cameras and lights see all layers).</summary>
    void SetUpDecalLayers()
    {
        GetTree().NodeAdded += node =>
        {
            if (node is not GeometryInstance3D g) return;
            for (var p = g.GetParent(); p is not null; p = p.GetParent())
                if (p is CharacterViewer or Ragdoll or MagazineDrops or GroundShards) { g.Layers = CharacterRenderLayer; return; }
        };
    }

    // ---- the screen flash ----

    const string DamageFlashShader = """
        shader_type canvas_item;
        render_mode blend_add;
        // M38: damage flash on the screen edges, one colour for armour damage and one for health damage; a direction (screen position of the attacker, -1..1)
        // concentrates it on that edge, a zero direction spreads it round the whole edge. The centre stays clear.
        uniform vec3 armour_colour = vec3(1.0, 0.82, 0.2);
        uniform vec3 health_colour = vec3(1.0, 0.11, 0.06);
        uniform float armour_amount = 0.0;
        uniform float health_amount = 0.0;
        uniform vec2 armour_dir = vec2(0.0);
        uniform vec2 health_dir = vec2(0.0);
        vec3 flash(vec2 p, vec3 colour, float amount, vec2 dir) {
            float edge = smoothstep(0.30, 1.05, length(p * vec2(0.85, 1.0)));
            float aim = 1.0;
            if (dot(dir, dir) > 0.01) { vec2 d = p - dir; aim = 0.30 + 0.70 * exp(-dot(d, d) * 1.3); }
            return colour * amount * edge * aim;
        }
        void fragment() {
            vec2 p = (UV - 0.5) * 2.0;
            COLOR = vec4(flash(p, armour_colour, armour_amount, armour_dir) + flash(p, health_colour, health_amount, health_dir), 1.0);
        }
        """;

    // Envelope (brightness over the flash's age): the shape of the data's bloom colour keys (health: 255, 29, 16 -> 80, 42, 45 -> 45, 0, 4 -> 0 at 0, 0.18 and 0.47 of the
    // element's 0.4 s; armour: white-gold -> 154 -> 0), over 0.45 s. Peaks are ours.
    const float FlashSeconds = 0.45f, ArmourPeak = 0.75f, HealthPeak = 0.75f;
    static float FlashEnvelope(float age)
    {
        float t = age / FlashSeconds;
        if (t >= 1f) return 0f;
        // keys: (0, 1), (0.18, 0.45), (0.47, 0.2), (1, 0)
        if (t < 0.18f) return Mathf.Lerp(1f, 0.45f, t / 0.18f);
        if (t < 0.47f) return Mathf.Lerp(0.45f, 0.2f, (t - 0.18f) / 0.29f);
        return Mathf.Lerp(0.2f, 0f, (t - 0.47f) / 0.53f);
    }

    static readonly bool OldDamageFlash = OS.GetCmdlineUserArgs().Contains("--old-damage-flash");

    /// <summary>
    /// The age a flash restarts at when another hit lands (fix/spawn-room-light; ours: the original's rule for a hit during a flash is not known). A fresh hit starts at the peak, as always;
    /// a hit while a flash still runs only lifts it back to the plateau after the peak (envelope 0.45) and never re-peaks it. A crowd hitting every few ticks (Musou Mode) used to restart the
    /// 1.0 peak each time and strobed the whole screen between 1.0 and 0.45; now it holds a steady wash. <c>--old-damage-flash</c>: every hit restarts at the peak.
    /// </summary>
    static float FlashRestartAge(float age) => OldDamageFlash || age >= FlashSeconds ? 0f : Math.Min(age, FlashSeconds * 0.18f);

    ColorRect? _damageRect;
    ShaderMaterial? _damageMaterial;
    float _armourFlashAge = 9f, _healthFlashAge = 9f;
    Vector2 _armourFlashDir, _healthFlashDir;
    readonly List<(double At, int Shooter)> _incomingShots = new();
    PlayerState _damagePrev;
    bool _damagePrevSeen;
    int _armourFlashes, _healthFlashes;
    double _damageLogAt;

    /// <summary>The full-screen overlay: a layer under the HUD (layer 0, added after the death vignette, so over it; the blindness overlay is added later still and covers it).</summary>
    void AddDamageFlash()
    {
        var layer = new CanvasLayer { Name = "DamageFlash", Layer = 0 };
        _damageMaterial = new ShaderMaterial { Shader = new Shader { Code = DamageFlashShader } };
        _damageRect = new ColorRect { Name = "DamageFlash", Material = _damageMaterial, MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false, Color = Colors.White };
        _damageRect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        layer.AddChild(_damageRect);
        AddChild(layer);
    }

    /// <summary>Per frame: starts a flash when the own shield or health fell since the last frame (the predicted state follows the server's), decays the running ones.</summary>
    void UpdateDamageFlash(PlayerState me, double delta)
    {
        if (_damageRect is null || _damageMaterial is null) return;
        TrackOwnShield(me);
        // Shots that struck the own body, noted as they arrive (the predicted shield may fall a frame before or after the shot events of its snapshot).
        foreach (var shot in _frameShots)
            if (shot.Hit is ShotHit.Body or ShotHit.Head && VictimAt(shot.End, shot.Shooter) == _client?.PlayerId) _incomingShots.Add((_connectedFor, shot.Shooter));
        _incomingShots.RemoveAll(i => _connectedFor - i.At > 0.4);
        if (_damagePrevSeen && HitFeedbackRules.FlashDamage(_damagePrev.Alive, _damagePrev.Shield, _damagePrev.Health, me.Alive, me.Shield, me.Health) is var (shield, health) && (shield > 0f || health > 0f))
        {
            var dir = DamageDirection(me);
            if (shield > 0f) { _armourFlashAge = FlashRestartAge(_armourFlashAge); _armourFlashDir = dir; _armourFlashes++; }
            if (health > 0f) { _healthFlashAge = FlashRestartAge(_healthFlashAge); _healthFlashDir = dir; _healthFlashes++; }
            if (_bot && _connectedFor - _damageLogAt > 1.0)
            {
                _damageLogAt = _connectedFor;
                GD.Print($"t {_connectedFor:F1} s: damage flash: {(shield > 0f ? $"armour -{shield:F0} (yellow)" : "")}{(shield > 0f && health > 0f ? ", " : "")}{(health > 0f ? $"health -{health:F0} (red)" : "")}{(dir == Vector2.Zero ? ", all round" : $", from screen {dir.X:F1},{dir.Y:F1}")} ({_frameShots.Count(s2 => s2.Hit is ShotHit.Body or ShotHit.Head)} body shot events this frame, {_lastOthers.Count} others shown)");
            }
        }
        _damagePrev = me; _damagePrevSeen = true;
        if (DamageDemo is { } demo && me.Alive)
        {
            double t = _connectedFor % 6.0, last = (_connectedFor - delta) % 6.0;
            if (demo is "armour" or "both") { _armourFlashAge = 0.04f; _armourFlashDir = new Vector2(1f, 0.1f); }
            if (demo is "health" or "both") { _healthFlashAge = 0.04f; _healthFlashDir = demo == "both" ? new Vector2(-1f, 0.1f) : new Vector2(0f, 1f); }
            if (demo is not ("armour" or "health" or "both"))
            {
                if (last < 1.0 && t >= 1.0) { _armourFlashAge = 0f; _armourFlashDir = new Vector2(1f, 0.1f); }
                if (last < 3.0 && t >= 3.0) { _healthFlashAge = 0f; _healthFlashDir = new Vector2(0f, 1f); }
            }
        }
        _armourFlashAge += (float)delta; _healthFlashAge += (float)delta;
        float armourNow = me.Alive ? ArmourPeak * FlashEnvelope(_armourFlashAge) : 0f, healthNow = me.Alive ? HealthPeak * FlashEnvelope(_healthFlashAge) : 0f;   // the death vignette (M23) takes over when dead
        _damageRect.Visible = armourNow > 0.002f || healthNow > 0.002f;
        if (!_damageRect.Visible) return;
        _damageMaterial.SetShaderParameter("armour_amount", armourNow);
        _damageMaterial.SetShaderParameter("health_amount", healthNow);
        _damageMaterial.SetShaderParameter("armour_dir", _armourFlashDir);
        _damageMaterial.SetShaderParameter("health_dir", _healthFlashDir);
    }

    /// <summary>
    /// Where on the screen's edge the damage came from: toward the shooter of a shot event that struck the own body this frame (front: top, right: right, behind: bottom;
    /// ours), zero (all round) when none is known (melee, blasts, shots lost with a snapshot).
    /// </summary>
    Vector2 DamageDirection(PlayerState me)
    {
        if (_camera is null || _gameSpace is null) return Vector2.Zero;
        for (int k = _incomingShots.Count - 1; k >= 0; k--)
        {
            var shooter = _lastOthers.FirstOrDefault(o => o.PlayerId == _incomingShots[k].Shooter);
            if (shooter.PlayerId != _incomingShots[k].Shooter) continue;
            var local = _camera.GlobalTransform.AffineInverse() * ToGodot(shooter.Position);
            var d = new Vector2(local.X, local.Z);   // x: right, z: behind (the camera looks down -Z): behind is the bottom of the screen
            if (d.LengthSquared() > 0.0001f) return d.Normalized();
        }
        return Vector2.Zero;
    }
}
