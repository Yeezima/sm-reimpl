using System.Collections.Generic;
using Godot;
using SmContent;
using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// Grenade effects pass (owner, 2026-11-17; protocol 83): a grenade thrown with Master-Crafted Wargear shows and sounds the <c>_upgraded</c> blueprint's (blast effect scale 1.5 against 0.6, the bigger blast
/// sound), the frag blasts draw their smoke and dust again, and the camera takes the blueprint's own <c>camera_effect</c> (<see cref="GrenadeFx"/>) in place of the placeholder shake. <c>--old-grenade-fx</c>
/// restores the earlier look for all of it (the base blueprint's effects and sounds, no smoke, the placeholder shake).
/// </summary>
public partial class NetworkView
{
    /// <summary><c>--old-grenade-fx</c>: the grenades as before the effects pass.</summary>
    static readonly bool OldGrenadeFx = CmdFlag("old-grenade-fx");

    /// <summary>Whether a thrown record draws and sounds the upgraded blueprint (the wire's flag; not with the restore switch).</summary>
    bool GrenadeUpgradedFx(ProjectileView q) => q.Thrown && q.Upgraded && !OldGrenadeFx;

    /// <summary>Whether this blast effect keeps its smoke and dust (the frag grenades' only).</summary>
    static bool GrenadeKeepsSmoke(string effect) => !OldGrenadeFx && GrenadeFx.KeepsSmoke(effect);

    /// <summary>Whether the blast of this thrown record has the data's camera effect (the frag grenades; the krak and the rest keep the earlier feel).</summary>
    bool GrenadeCameraData(ProjectileView d) =>
        !OldGrenadeFx && d.Thrown && _config is not null && d.Weapon >= 0 && d.Weapon < _config.Thrown.Count && GrenadeFx.IsFrag(_config.Thrown[d.Weapon].Name);

    double _grenadeJoltAt = -1;
    float _grenadeJoltStrength;
    /// <summary>Blasts whose camera effect reached the player (test aid and the proof).</summary>
    internal int GrenadeJolts;

    /// <summary>Starts the camera effect of a blast <paramref name="distance"/> metres away (nothing beyond its attenuation).</summary>
    void GrenadeJolt(float distance)
    {
        float strength = GrenadeFx.Strength(distance) * (SmClient.Customizer.UiKit.ReduceMotion ? 0f : 1f);   // Reduce motion: no shake and no blur
        if (strength <= 0f) return;
        GrenadeJolts++;
        if (strength < _grenadeJoltStrength && _connectedFor - _grenadeJoltAt < GrenadeFx.Duration) return;   // a stronger one is still running
        _grenadeJoltAt = _connectedFor;
        _grenadeJoltStrength = strength;
    }

    /// <summary>The camera offset of the running camera effect (right, up in metres; zero when none).</summary>
    Vector3 GrenadeJoltOffset()
    {
        if (_grenadeJoltAt < 0) return Vector3.Zero;
        var (r, u) = GrenadeFx.Offset((float)(_connectedFor - _grenadeJoltAt), _grenadeJoltStrength);
        return new Vector3(r, u, 0f);
    }

    ColorRect? _grenadeBlur;
    ShaderMaterial? _grenadeBlurMaterial;

    /// <summary>The blur layer under the HUD (the camera effect's blur_power; the radius per frame is <see cref="GrenadeBlurFrame"/>).</summary>
    void SetUpGrenadeBlur()
    {
        var shader = new Shader { Code = "shader_type canvas_item;\nuniform sampler2D screen : hint_screen_texture, filter_linear_mipmap;\nuniform float radius = 0.0;\nvoid fragment(){ vec2 px = SCREEN_PIXEL_SIZE * radius; vec4 c = texture(screen, SCREEN_UV); for (int i = 0; i < 8; i++) { float a = float(i) * 0.7853982; c += texture(screen, SCREEN_UV + vec2(cos(a), sin(a)) * px); } COLOR = vec4((c / 9.0).rgb, 1.0); }" };
        _grenadeBlurMaterial = new ShaderMaterial { Shader = shader };
        _grenadeBlur = new ColorRect { Name = "GrenadeBlur", Color = Colors.White, MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false, Material = _grenadeBlurMaterial };
        _grenadeBlur.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        var layer = new CanvasLayer { Name = "FxBlur", Layer = -1 };
        layer.AddChild(_grenadeBlur);
        AddChild(layer);
    }

    /// <summary>Sets the blur for this frame: the camera effect's blur (original data 0.5; unit and pixel size ours / estimate), scaled by the Options menu's camera shake and off with Reduce motion.</summary>
    void GrenadeBlurFrame()
    {
        if (_grenadeBlur is null || _grenadeBlurMaterial is null) return;
        float radius = _grenadeJoltAt < 0 ? 0f
            : GrenadeFx.BlurRadius((float)(_connectedFor - _grenadeJoltAt), _grenadeJoltStrength, GetViewport().GetVisibleRect().Size.Y) * SmClient.Customizer.Model.LiveOptions.CameraShake;
        _grenadeBlur.Visible = radius > 0.05f;
        if (_grenadeBlur.Visible) _grenadeBlurMaterial.SetShaderParameter("radius", radius);
        if (radius > 0.05f) GrenadeBlurFrames++;
    }

    /// <summary>Frames the blur drew (test aid).</summary>
    internal int GrenadeBlurFrames;

    // ---- who threw it (the snapshot names no owner: inferred) ----

    /// <summary>A frag grenade in the world as the client follows it: its inferred thrower (-1: unknown) and side, when it was first seen, when it landed, when it was last seen.</summary>
    sealed class GrenadeInfo
    {
        public int Thrower = -1; public Team Side; public double Since, LastSeen; public double? Landed; public bool Marked;
    }

    readonly Dictionary<int, GrenadeInfo> _grenades = new();
    readonly Dictionary<int, double> _throwingAt = new();
    readonly HashSet<int> _grenadeLive = new();
    readonly List<int> _grenadeScratch = new();
    double _grenadesTrackedAt = double.NegativeInfinity;

    /// <summary>
    /// Once a frame: who threw each frag grenade in the world. The snapshot's projectile record carries no owner (and no side), so the thrower is inferred: the player nearest to the grenade, within 4 m
    /// when it first shows, who was in a throw within the last 1.2 s (the own player's state, the others' slim records' throwing flag). Ours / estimate; an unknown thrower (-1) is treated as not hostile.
    /// Entries live 3 s after the grenade leaves the view, so its blast still finds its thrower.
    /// </summary>
    void GrenadeTrack(PlayerState me, IReadOnlyList<PlayerState> others)
    {
        if (OldGrenadeFx || _client is null || _config is null || _grenadesTrackedAt == _connectedFor) return;
        _grenadesTrackedAt = _connectedFor;
        if (me.Throwing) _throwingAt[me.PlayerId] = _connectedFor;
        foreach (var p in others) if (p.Throwing) _throwingAt[p.PlayerId] = _connectedFor;
        _grenadeLive.Clear();
        foreach (var q in _client.Projectiles())
        {
            if (!q.Thrown || q.Weapon < 0 || q.Weapon >= _config.Thrown.Count || !GrenadeFx.IsFrag(_config.Thrown[q.Weapon].Name)) continue;
            _grenadeLive.Add(q.Id);
            if (!_grenades.TryGetValue(q.Id, out var info))
            {
                _grenades[q.Id] = info = new GrenadeInfo { Since = _connectedFor };
                float best = GrenadeRules.ThrowerReach;
                void Consider(PlayerState p)
                {
                    if (!_throwingAt.TryGetValue(p.PlayerId, out double at) || _connectedFor - at > GrenadeRules.ThrowerWindow) return;
                    float d = System.Numerics.Vector3.Distance(q.Position, p.Position);
                    if (d <= best) { best = d; info.Thrower = p.PlayerId; info.Side = p.Team; }
                }
                Consider(me);
                foreach (var p in others) Consider(p);
                if (_bot || LogSound("grenade")) GD.Print($"t {_connectedFor:F2} s: frag grenade #{q.Id} first seen {System.Numerics.Vector3.Distance(q.Position, me.Position):F1} m from this player, thrower {(info.Thrower < 0 ? "unknown" : "P" + info.Thrower + " side " + info.Side)}{(info.Thrower >= 0 && info.Thrower != me.PlayerId && World.Hostile(me.Team, info.Side) ? " (enemy)" : "")}");
            }
            info.LastSeen = _connectedFor;
            if (info.Landed is null && (q.Flags & (ProjectileFlags.Stuck | ProjectileFlags.Attached)) != 0) info.Landed = _connectedFor;
        }
        if (_grenades.Count > 0)
        {
            _grenadeScratch.Clear();
            foreach (var (id, info) in _grenades) if (!_grenadeLive.Contains(id) && _connectedFor - info.LastSeen > 3.0) _grenadeScratch.Add(id);
            foreach (int id in _grenadeScratch) _grenades.Remove(id);
        }
    }

    // ---- victim sounds (the blast's hit_actions and player_hit_actions) ----

    /// <summary>
    /// A frag blast's sounds at the players it reaches (grenade-effects fix pass; the data: the damaging <c>area_of_effect</c>'s <c>hit_actions</c> post <c>play_big_damage_grenade</c> for the hit entity when it is
    /// the local player (<c>apply_only_if_on_player</c>), its <c>player_hit_actions</c> post <c>play_grenade_damage_smack</c> at every player hit). **Ours / estimate**: the wire does not say who took damage, so a
    /// player counts as hit when alive, within the blast's radius and in line of sight of the blast point, as the data's line_of_sight blast has it; a player on the thrower's side does not (no friendly fire
    /// unless the server runs it: <see cref="SmNet.Scoreboard.FriendlyFire"/>) except the thrower himself. The thrower is inferred (<see cref="GrenadeTrack"/>); when unknown every player counts. No falloff:
    /// a player at the edge of the radius hears the big-damage sound too. <c>--old-grenade-fx</c> leaves them out.
    /// </summary>
    void GrenadeVictimSounds(ProjectileView d, SoundFile file)
    {
        if (OldGrenadeFx || _client is null || _config is null || _sound is null || !GrenadeCameraData(d)) return;   // frag blasts only
        var spec = Shots.ProjectileOf(_config, d.Weapon, d.Stage, true, d.Upgraded);
        if (spec?.Splash is not { } splash) return;
        float reach = _config.ThrownBlastScale(_config.Thrown[d.Weapon]);
        float radius = splash.Radius * reach;
        _grenades.TryGetValue(d.Id, out var info);
        bool friendlyFire = _client.Scoreboard?.FriendlyFire ?? false;
        var own = _client.Predicted;
        var all = new List<(PlayerState P, bool Own)>();
        if (own.Alive) all.Add((own, true));
        foreach (var p in _client.RemotePlayers().Players) if (p.Alive && p.PlayerId != own.PlayerId) all.Add((p, false));
        var origin = d.Position + new System.Numerics.Vector3(0f, 0f, 0.5f);
        foreach (var (p, mine) in all)
        {
            if (info is { Thrower: >= 0 } && p.PlayerId != info.Thrower && !friendlyFire && !World.Hostile(info.Side, p.Team)) continue;   // an ally of the thrower: not hurt
            var chest = p.Position + new System.Numerics.Vector3(0f, 0f, 1.0f);
            float dist = System.Numerics.Vector3.Distance(origin, chest);
            if (dist > radius) continue;
            if (dist > 0.05f && _client.Collision?.Weapons.Raycast(origin, (chest - origin) / dist, dist) is { } blocked && blocked.Distance < dist - 0.3f) continue;
            var at = new SoundEmitter { Position = ToGodot(p.Position + new System.Numerics.Vector3(0f, 0f, 1.0f)), Label = "grenade victim", OneShot = true };
            PostAll(file.Of("hit_actions"), at, mine, "grenade victim");
            PostAll(file.Of("player_hit_actions"), at, mine, "grenade victim");
        }
    }

    // ---- the in-flight marker (Hud.GrenadeMarker.cs) ----

    /// <summary>Markers drawn so far (test aid and the proof).</summary>
    internal int GrenadeMarkFrames;

    /// <summary>
    /// Per frame: the markers of the frag grenades in flight (grenade-effects, owner 2026-11-17: "match the video", then "also enemy grenades near me"): the player's own, and an enemy's while it is within
    /// <see cref="GrenadeRules.NearMargin"/> metres beyond its blast radius of the player. A teammate's, and a grenade whose thrower is not known, show none. It shows from the first sight until the grenade
    /// lands (stops resting or sticking) and fades over <see cref="GrenadeRules.LandedFade"/>. Who threw it is inferred (<see cref="GrenadeTrack"/>: the snapshot names no owner).
    /// </summary>
    void GrenadeMarkerFrame(Hud.Frame f, PlayerState me)
    {
        if (OldGrenadeFx || _client is null || _config is null || _camera is null || _gameSpace is null || _grenades.Count == 0) return;
        foreach (var q in _client.Projectiles())
        {
            if (!q.Thrown || !_grenades.TryGetValue(q.Id, out var info) || info.Thrower < 0) continue;
            bool own = info.Thrower == me.PlayerId;
            if (!own)
            {
                if (!World.Hostile(me.Team, info.Side)) continue;   // a teammate's grenade: nothing
                var spec = Shots.ProjectileOf(_config, q.Weapon, q.Stage, true, q.Upgraded);
                float radius = (spec?.Splash?.Radius ?? 0f) * _config.ThrownBlastScale(_config.Thrown[q.Weapon]);
                if (!GrenadeRules.Near(System.Numerics.Vector3.Distance(q.Position, me.Position), radius)) continue;
            }
            if (!own && !info.Marked) { info.Marked = true; if (_bot || LogSound("grenade")) GD.Print($"t {_connectedFor:F2} s: enemy grenade #{q.Id} near: marker on"); }
            float age = (float)(_connectedFor - info.Since);
            float fade = GrenadeRules.Fade(age, info.Landed is { } l ? (float)(_connectedFor - l) : null);
            if (fade <= 0f) continue;
            var cam = _camera.GlobalTransform.Basis;
            var forward = new Vector3(-cam.Z.X, 0f, -cam.Z.Z).Normalized();
            var right = new Vector3(cam.X.X, 0f, cam.X.Z).Normalized();
            var d = ToGodot(q.Position) - ToGodot(me.Position);
            f.GrenadeMarks.Add(new Hud.GrenadeMark(GrenadeRules.Angle(d.Dot(right), d.Dot(forward)), age, fade));
            GrenadeMarkFrames++;
        }
    }

    // ---- --bot-grenade-demo (capture aid for the reference comparison; not original behaviour) ----

    /// <summary>
    /// <c>--bot-grenade-demo &lt;s&gt;</c> (capture aid, grenade-effects proof; with <c>--bot --equipment "frag grenade"</c>, optionally <c>--perks equipment_effectiveness</c>): a standing bot with the human follow camera throws its grenade
    /// <c>&lt;s&gt;</c> seconds after joining, at the pitch <c>--bot-grenade-pitch</c> (radians, default -0.12), and stands there to watch it go off.
    /// </summary>
    double GrenadeDemoAt => _grenadeDemoAt ??= FloatArg("bot-grenade-demo") is { } v ? v : -1.0;
    double? _grenadeDemoAt;
    bool GrenadeDemo => GrenadeDemoAt >= 0.0;
    bool _grenadeDemoThrown;

    PlayerInput GrenadeDemoInput(uint tick)
    {
        _pitch = FloatArg("bot-grenade-pitch") ?? -0.12f;
        bool press = !_grenadeDemoThrown && _connectedFor >= GrenadeDemoAt;
        if (press) { _grenadeDemoThrown = true; GD.Print($"t {_connectedFor:F2} s: grenade demo: throw"); }
        return new PlayerInput(tick, 0f, 0f, _yaw, press ? InputButtons.Grenade : InputButtons.None, _pitch);
    }
}
