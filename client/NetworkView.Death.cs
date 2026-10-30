using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmContent;
using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// M23: the death screen. While the own player is dead it has no control (no input is sampled or sent, the mouse look is frozen),
/// the camera watches the own ragdoll and a strong vignette darkens the screen; every player who dies, own or remote, turns into a
/// physics ragdoll (<see cref="Ragdoll"/>) that starts from the last animated pose and the body's velocity, pushed away from the
/// killer, and lies on the map's collision until the player respawns. The death screen's look (loss of control, ragdoll, vignette,
/// slow third-person view of the body) is **after the user's description of the original**; the push strengths, the camera path and the
/// vignette's numbers are placeholders. The ragdoll itself (shapes, masses, joints, limits) is original data (<see cref="RagdollDef"/>); the original's death camera is not decoded.
/// </summary>
public partial class NetworkView
{
    const int RagdollCap = Ragdoll.MaxSlots;       // most ragdolls at once (simulating or lying); the oldest goes first
    const float DeathFadeIn = 0.4f, DeathFadeOut = 0.25f;   // vignette (seconds)
    const float DeathBlendSeconds = 0.3f;           // from the last camera to the death camera

    /// <summary><c>--bot-idle</c> (test aid): the bot stands still and does not fire, so that an opponent kills it (death screen checks).</summary>
    public bool BotIdle { get; init; }
    /// <summary><c>--bot-fire-walls</c> (M38 test aid, with <c>--bot-fire</c>): on a map the bot also fires with no enemy in sight, into the walls it turns toward (impact effects, decals).</summary>
    public bool BotFireWalls { get; init; }

    /// <summary><c>--ragdoll-rest &lt;s&gt;</c> (test aid): seconds a ragdoll must be still before its pose is baked and its physics freed (default 3).</summary>
    public float? RagdollRest { get; init; }
    /// <summary><c>--ragdoll-debug</c> (test aid): logs each ragdoll speeds twice a second.</summary>
    public bool RagdollDebug { get; init; }

    // ---- state ----
    RagdollDef? _ragdollDef;
    DeathTuning? _deathTuning;
    bool _ragdollTried;
    readonly List<Ragdoll> _ragdolls = new();
    readonly Dictionary<int, bool> _aliveSeen = new();
    readonly Dictionary<int, (Vector3 Position, Vector3 Velocity)> _motion = new();   // game space, smoothed
    readonly Dictionary<int, (KillView Kill, double At)> _deathCauses = new();
    readonly List<(int Id, CharacterViewer Character, PlayerState State, bool Own, Vector3 Velocity)> _pendingDeaths = new();
    StaticBody3D? _ragdollWorld;
    string _ragdollWorldNote = "none yet";
    int _ragdollsStarted, _ragdollsSettled, _ragdollsRemoved;
    double _ragdollSettleSum;
    // Frame times (s) while at least one ragdoll simulates, and while none does (for the log and the F1 line).
    double _framesSimSum, _framesSimMax, _framesIdleSum;
    int _framesSim, _framesIdle;
    double _ragdollLogAt;
    // Vignette.
    ColorRect? _vignette;
    ShaderMaterial? _vignetteMaterial;
    float _vignetteStrength;
    // Death camera.
    bool _deathCameraOn;
    double _deathAge;
    Transform3D _deathStart;
    float _deathFov, _deathAz, _deathRadius, _deathEl;
    Vector3 _deathFocus;
    float _deathFraction = 1f;       // M40-T6: the death camera's place along the cast from the focus (NetworkView.Spectate.cs)

    /// <summary>The own player is dead (connected, no control until the respawn).</summary>
    bool OwnDead => _client is { Status: ClientStatus.Connected } c && !c.Predicted.Alive && Spectate is null;

    Ragdoll? OwnRagdoll => _client is null ? null : _ragdolls.LastOrDefault(r => r.Own && IsInstanceValid(r));

    const string VignetteShader = """
        shader_type canvas_item;
        // Death vignette (after the user's description of the original): heavy darkening toward the edges, slight desaturation and a dark red tint.
        uniform float strength : hint_range(0.0, 1.0) = 0.0;
        uniform sampler2D screen_tex : hint_screen_texture, filter_linear_mipmap;
        void fragment() {
            vec3 scene = texture(screen_tex, SCREEN_UV).rgb;
            vec2 p = (SCREEN_UV - 0.5) * 2.0;
            float d = length(p * vec2(0.85, 1.0));
            float v = smoothstep(0.28, 1.15, d);
            float grey = dot(scene, vec3(0.299, 0.587, 0.114));
            vec3 col = mix(scene, vec3(grey) * vec3(1.0, 0.9, 0.9), 0.55 * strength);
            col *= 1.0 - 0.28 * strength;
            col = mix(col, vec3(0.16, 0.0, 0.0), clamp(v * 0.55, 0.0, 1.0) * strength);
            col *= 1.0 - 0.95 * v * v * strength;
            COLOR = vec4(col, 1.0);
        }
        """;

    /// <summary>The full-screen vignette, in a canvas layer below the HUD's (so the "KILLED" text stays on top), unaffected by --no-hud.</summary>
    void AddVignette()
    {
        var layer = new CanvasLayer { Name = "DeathVignette", Layer = 0 };
        _vignetteMaterial = new ShaderMaterial { Shader = new Shader { Code = VignetteShader } };
        _vignetteMaterial.SetShaderParameter("strength", 0f);
        _vignette = new ColorRect { Name = "Vignette", Material = _vignetteMaterial, MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false, Color = Colors.White };
        _vignette.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        layer.AddChild(_vignette);
        AddChild(layer);
    }

    void UpdateVignette(double delta)
    {
        if (_vignette is null || _vignetteMaterial is null) return;
        float target = OwnDead ? 1f : 0f;
        _vignetteStrength = Mathf.MoveToward(_vignetteStrength, target, (float)delta / (target > _vignetteStrength ? DeathFadeIn : DeathFadeOut));
        _vignette.Visible = _vignetteStrength > 0.001f;
        if (_vignette.Visible) _vignetteMaterial.SetShaderParameter("strength", _vignetteStrength * _vignetteStrength * (3f - 2f * _vignetteStrength));
    }

    // ---- death tracking ----

    /// <summary>Remembers the cause of a death (called for each kill-feed event) for the push on the ragdoll.</summary>
    void NoteDeathCause(KillView kill)
    {
        _deathCauses[kill.Victim] = (kill, _connectedFor);
        NoteVengeanceKill(kill);   // F-D1: Final Vengeance's explosion seen from the kill it made
    }

    /// <summary>
    /// Per player and frame, before the character is placed: tracks the motion, spots the living-to-dead edge (queued, handled in
    /// <see cref="ProcessDeaths"/> once the kill events of the frame are in) and removes a ragdoll when the player is alive again.
    /// A player first seen dead gets no ragdoll. Returns whether the character should be placed (alive).
    /// </summary>
    bool TrackDeath(int id, CharacterViewer character, PlayerState state, bool own, float delta)
    {
        bool wasAlive = _aliveSeen.GetValueOrDefault(id);
        _aliveSeen[id] = state.Alive;
        if (state.Alive)
        {
            if (!wasAlive) UpperReset(id);   // pass 2: a respawn (or the first sight) starts afresh
            RemoveRagdolls(id);   // also drops a death still queued for this id
            var pos = new Vector3(state.Position.X, state.Position.Y, state.Position.Z);
            Vector3 vel;
            if (own) vel = new Vector3(state.Velocity.X, state.Velocity.Y, state.Velocity.Z);
            else if (_motion.TryGetValue(id, out var prev) && delta > 1e-4f)
            {
                var raw = (pos - prev.Position) / delta;
                vel = raw.Length() > 60f ? Vector3.Zero : prev.Velocity.Lerp(raw, 0.35f);   // a teleport (respawn) is not a velocity
            }
            else vel = Vector3.Zero;
            _motion[id] = (pos, vel);
            NoteAirborne(id, state);   // F-D1: a jump death needs to know the player was in a jump (NetworkView.DeathFx.cs)
            return true;
        }
        // Ork O7a: a failed marine ragdoll definition must not stop an Ork from getting its own (its definition is made at its first death, OrkRagdollDefinition).
        if (wasAlive && !(_ragdollTried && _ragdollDef is null && !IsOrkCharacter(character)))
        {
            if (_pendingDeaths.Count > 0) _pendingDeaths.RemoveAll(d => d.Id == id);   // never two queued deaths for one id
            var v = _motion.TryGetValue(id, out var m) ? m.Velocity : Vector3.Zero;
            _pendingDeaths.Add((id, character, state, own, v));
        }
        UpperReset(id);   // pass 2: dead
        if (own) character.Visible = false;
        return false;
    }

    /// <summary>Turns this frame's deaths into ragdolls (their kill events have been noted by now).</summary>
    void ProcessDeaths()
    {
        if (_pendingDeaths.Count == 0) return;
        // M40-T9: deaths are spread over frames like other heavy builds (a grenade that kills four players builds four corpses): the first one always, the
        // rest while the frame has budget; the others stay queued (a player who is alive again by then gets no ragdoll).
        _deathBatch.Clear();
        _deathBatch.AddRange(_pendingDeaths);
        _pendingDeaths.Clear();   // first: whatever happens below, a death is handled once
        // The own death goes first and is never deferred (the death camera and the vignette wait for it); the others keep their order.
        int ownAt = _deathBatch.FindIndex(d => d.Own);
        if (ownAt > 0) { var own = _deathBatch[ownAt]; _deathBatch.RemoveAt(ownAt); _deathBatch.Insert(0, own); }
        for (int index = 0; index < _deathBatch.Count; index++)
        {
            var (id, character, state, own, velocity) = _deathBatch[index];
            if (index > 0 && !own && !HeavyLeft) { _pendingDeaths.Add(_deathBatch[index]); continue; }
            if (!_aliveSeen.TryGetValue(id, out var aliveNow) || aliveNow) continue;   // respawned, or left the match, while it waited
            // Ork O7c (NetworkView.OrkActions.cs): an Ork plays its death clip until the graph hands the body to the ragdoll; the death waits (queued again) meanwhile.
            if (IsInstanceValid(character) && IsOrkCharacter(character) && OrkDeathHold(id, character, state, own)) { _pendingDeaths.Add(_deathBatch[index]); continue; }
            // Animation pass 2 (NetworkView.UpperBody.cs): a marine plays the graph's death clip for the blow until the clip's powered-ragdoll tag; the ragdoll starts from that pose.
            if (IsInstanceValid(character) && !IsOrkCharacter(character) && MarineDeathHold(id, character, state, own)) { _pendingDeaths.Add(_deathBatch[index]); continue; }
            long heavy = HeavyStart();
            Ragdoll? rd = null;
            try
            {
                if (!IsInstanceValid(character) || _gameSpace is null || _client is null || _config is null) continue;
                var marineDef = RagdollDefinition();   // also for an Ork: the push uses the marine ragdoll's mass, and a first death must not see the 780 kg fallback
                var def = IsOrkCharacter(character) && character.Assets is { } orkAssets ? OrkRagdollDefinition(orkAssets) : marineDef;   // Ork O7a: the boy's own ragdoll
                if (def is null) continue;
                RemoveRagdolls(id, keepDeathState: true);   // one player never has two corpses
                KillView? kill = _deathCauses.TryGetValue(id, out var c) && _connectedFor - c.At < 3.0 ? c.Kill : null;
                var kick = DeathKick(state, kill, out string why);   // Ork O7a: the push keeps the marine mass (parity) until the Ork blueprint's own push is read: spec "The networked Ork"
                // The body's own velocity is capped like the remote players' (a teleport or a dive must not fire the ragdoll through the map).
                var rawVelocity = velocity;
                velocity = HeldVelocity(id, velocity);   // pass 2: the held body has slid on and slowed meanwhile
                var start = velocity.Length() > MaxRagdollSpeed ? velocity.Normalized() * MaxRagdollSpeed : velocity;
                // Make room: the cap counts ragdolls simulating or lying; the oldest goes (lying ones and other players' first, the own last).
                while (_ragdolls.Count >= RagdollCap)
                {
                    var oldest = _ragdolls.FirstOrDefault(r => !r.Simulating && !r.Own) ?? _ragdolls.FirstOrDefault(r => !r.Own) ?? _ragdolls[0];
                    RemoveRagdoll(oldest);
                }
                int slot = Enumerable.Range(0, Ragdoll.MaxSlots).First(s => !_ragdolls.Any(r => r.Simulating && r.Slot == s));
                EnsureRagdollWorld();
                rd = new Ragdoll { Name = $"Ragdoll{id}", PlayerId = id, Own = own };
                _gameSpace.AddChild(rd);
                var spawnWatch = System.Diagnostics.Stopwatch.StartNew();
                rd.Start(def, character, slot, _gameSpace.Basis * start, _gameSpace.Basis * kick);
                spawnWatch.Stop();
                if (spawnWatch.Elapsed.TotalMilliseconds > 30) GD.Print($"t {_connectedFor:F1} s: slow ragdoll start for P{id}: {spawnWatch.Elapsed.TotalMilliseconds:F0} ms ({Ragdoll.LastSplits})");
                if (character.Overlay is { } overlay) rd.Character?.SetOverlay(overlay);
                _ragdolls.Add(rd);
                _ragdollsStarted++;
                double deathSeenAgo = _connectedFor - DeathSeenAt(id, _connectedFor);
                try { ScheduleDeathFx(id, character, state, own, kill, rd); }   // F-D1: the data's death effects (queued; played in later frames, so the death frame costs nothing more)
                catch (Exception ex) when (ex is not (StackOverflowException or AccessViolationException or OutOfMemoryException)) { if (_fxNoted.Add("deathfx-fault")) GD.PrintErr($"death effects failed: {ex}"); }
                MarineDeathForget(id);   // pass 2: the hold is over, its state was used above
                if (own) _cameraPlaced = false;
                if (_bot || own || FrameProfileOn) GD.Print($"t {_connectedFor:F1} s: id {_client.PlayerId} ragdoll for P{id}{(own ? " (own)" : "")} at {state.Position.X:F1},{state.Position.Y:F1},{state.Position.Z:F1}: " +
                                          $"body velocity {start.X:F1},{start.Y:F1},{start.Z:F1} m/s (raw {rawVelocity.X:F1},{rawVelocity.Y:F1},{rawVelocity.Z:F1}; death seen {deathSeenAgo:F2} s ago), push {kick.X:F1},{kick.Y:F1},{kick.Z:F1} m/s ({why}); slot {slot}, {_ragdolls.Count} ragdoll(s); corpse weapons {rd.Character?.Weapons?.WeaponCount ?? 0} ({string.Join(", ", rd.Character?.Weapons?.Description ?? [])}); built in {spawnWatch.Elapsed.TotalMilliseconds:F1} ms");
            }
            catch (Exception ex) when (ex is not (StackOverflowException or AccessViolationException or OutOfMemoryException))
            {
                // One bad death must not leave nodes behind every frame or stop the frame: drop this ragdoll, say so, go on.
                if (rd is not null) { _ragdolls.Remove(rd); if (IsInstanceValid(rd)) rd.QueueFree(); }
                if (_ragdollErrors++ < 5) GD.PrintErr($"ragdoll for P{id} failed: {ex.GetType().Name}: {ex.Message}");
            }
            HeavyEnd(heavy);
            if (FrameProfileOn) GD.Print($"t {_connectedFor:F1} s: death of P{id} handled in {System.Diagnostics.Stopwatch.GetElapsedTime(heavy).TotalMilliseconds:F1} ms");
        }
    }

    readonly List<(int Id, CharacterViewer Character, PlayerState State, bool Own, Vector3 Velocity)> _deathBatch = new();

    /// <summary>Most speed (m/s) a new ragdoll inherits from its player's last motion.</summary>
    const float MaxRagdollSpeed = 25f;
    int _ragdollErrors;

    /// <summary>
    /// The velocity added to every body of a new ragdoll (game space): the killing hit's direction (<see cref="KillView.Direction"/>,
    /// from the server: a shot along its path, a blast from its point to the body, melee from the attacker) times a push speed
    /// = force level x the blueprint's ragdoll force multiplier / the ragdoll's mass. The force levels are the original's
    /// (<see cref="DeathTuning.Forces"/>: light 1000, medium 2500, strong 4000; units unknown, taken as kg m/s: **provisional**);
    /// which level a cause applies is ours: a shot light (the bolter has none in the data), melee medium, a blast or the melta strong;
    /// nothing for a fall out of the level. Without a direction: away from the killer's position, else backward from the facing.
    /// </summary>
    Vector3 DeathKick(PlayerState victim, KillView? kill, out string why)
    {
        string level, kind;
        if (kill is not { } k) { level = "light"; kind = "cause unknown"; }
        else if (k.Cause == KillCause.World) { level = ""; kind = "fell out of the level"; }
        else
        {
            bool blast = k.Cause is KillCause.Thrown or KillCause.JumpPack or KillCause.Perk   // M43: perk kills are blasts
                || (k.Cause == KillCause.Weapon && _config is not null && k.Index >= 0 && k.Index < _config.Weapons.Count
                    && (Shots.ProjectileOf(_config.Weapons[k.Index], 0)?.Splash is not null || _config.Weapons[k.Index].Delivery == DeliveryKind.Cone));
            (level, kind) = blast ? ("strong", "blast") : k.Cause == KillCause.Melee ? ("medium", "melee") : ("light", "shot");
        }
        if (level.Length == 0) { why = kind + ": no push"; return Vector3.Zero; }
        float mass = _ragdollDef?.TotalMass ?? 780f;   // always the marine ragdoll's, Orks too (parity: the Ork ragdoll's 271 kg would throw its corpse 2.9 times as fast; a playtest item)
        float multiplier = _deathTuning?.RagdollForceMultiplier ?? 1f;
        float impulse = _deathTuning is { } t && t.Forces.TryGetValue(level, out var force) ? force : level switch { "medium" => 2500f, "strong" => 4000f, _ => 1000f };
        float speed = impulse * multiplier / mass;
        Vector3 direction;
        string source;
        if (kill is { HasDirection: true } d) { direction = new Vector3(d.Direction.X, d.Direction.Y, d.Direction.Z); source = "server direction"; }
        else
        {
            System.Numerics.Vector3? killer = null;
            if (kill is { } kk && kk.Killer != 0 && kk.Killer != victim.PlayerId)
            {
                if (_client is not null && kk.Killer == _client.PlayerId) killer = _client.Predicted.Position;
                else if (_lastOthers.FirstOrDefault(p => p.PlayerId == kk.Killer) is { PlayerId: > 0 } found) killer = found.Position;
            }
            if (killer is { } kp) { direction = new Vector3(victim.Position.X - kp.X, victim.Position.Y - kp.Y, 0f); source = $"away from P{kill!.Value.Killer}"; }
            else { direction = new Vector3(MathF.Sin(victim.Yaw), -MathF.Cos(victim.Yaw), 0f); source = "backward from the facing"; }
        }
        direction = direction.LengthSquared() > 1e-6f ? direction.Normalized() : new Vector3(0, 0, 1);
        why = $"{kind}, {level} {impulse:F0} x {multiplier:F1} / {mass:F0} kg = {speed:F1} m/s, {source}";
        return direction * speed;
    }

    RagdollDef? RagdollDefinition()
    {
        if (_ragdollTried) return _ragdollDef;
        if (Lean) { _ragdollTried = true; GD.Print("ragdoll: not loaded (lean bot): dead players disappear"); return null; }   // perf/lean-bots (NetworkView.Lean.cs)
        if (RagdollRest is { } rest) Ragdoll.RestSeconds = rest;
        Ragdoll.DebugLog = RagdollDebug;
        _ragdollTried = true;
        if (_assets is null) return null;
        var notes = new List<string>();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var model = RagdollModel.Load(_gameDir, notes);
            _deathTuning = DeathTuning.Load(_gameDir, notes);
            if (model is not null) _ragdollDef = RagdollDef.Build(model, _assets, notes);
        }
        catch (Exception ex) when (ex is not (StackOverflowException or AccessViolationException or OutOfMemoryException))
        {
            _ragdollDef = null;   // whatever is wrong with the data, joining must not fail: dead players disappear instead
            notes.Add($"ragdoll: not available ({ex.GetType().Name}: {ex.Message}); dead players disappear instead");
        }
        foreach (var n in notes) { _notes.Add(n); GD.Print("  note: " + n); }
        GD.Print(_ragdollDef is null ? "ragdoll: not available" : $"ragdoll: {_ragdollDef.Description} ({watch.ElapsedMilliseconds} ms); death tuning {(_deathTuning is { } dt ? $"force multiplier {dt.RagdollForceMultiplier}, levels {string.Join(", ", dt.Forces.Select(f => f.Key + " " + f.Value))}" : "not read")}");
        return _ragdollDef;
    }

    // ---- the world the ragdolls lie on ----

    /// <summary>Called from <see cref="LoadMap"/>: the map's character collision (layers "2" and "8") as one static trimesh for the ragdolls.</summary>
    void BuildRagdollWorld(MapContent map)
    {
        _ragdollWorld?.QueueFree();
        _ragdollWorld = null;
        if (_gameSpace is null) return;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var faces = new List<Vector3>();
        foreach (var name in MapContent.CharacterLayers)
        {
            if (map.Layer(name) is not { } layer) continue;
            for (int i = 0; i + 2 < layer.Indices.Length; i += 3)
            {
                var a = layer.Vertices[layer.Indices[i]]; var b = layer.Vertices[layer.Indices[i + 1]]; var c = layer.Vertices[layer.Indices[i + 2]];
                faces.Add(new Vector3(a.X, a.Y, a.Z)); faces.Add(new Vector3(b.X, b.Y, b.Z)); faces.Add(new Vector3(c.X, c.Y, c.Z));
            }
        }
        if (faces.Count == 0) { _ragdollWorldNote = "map has no character collision; flat floor"; return; }
        float lowest = faces.Min(f => f.Z);   // game z up = Godot Y
        Ragdoll.LostBelowY = (_gameSpace.Transform * new Vector3(0, 0, lowest - 50f)).Y;
        var body = new StaticBody3D { Name = "RagdollWorld", CollisionLayer = Ragdoll.WorldLayer, CollisionMask = 0 };
        body.AddChild(new CollisionShape3D { Shape = new ConcavePolygonShape3D { Data = faces.ToArray(), BackfaceCollision = true } });
        _gameSpace.AddChild(body);
        _ragdollWorld = body;
        _ragdollWorldNote = $"map character collision, {faces.Count / 3:N0} triangles ({watch.ElapsedMilliseconds} ms)";
        GD.Print("ragdoll world: " + _ragdollWorldNote);
    }

    /// <summary>On flat ground (no map loaded) the ragdolls lie on the plane at game z = 0.</summary>
    void EnsureRagdollWorld()
    {
        if (_ragdollWorld is not null && IsInstanceValid(_ragdollWorld)) return;
        var body = new StaticBody3D { Name = "RagdollFloor", CollisionLayer = Ragdoll.WorldLayer, CollisionMask = 0 };
        body.AddChild(new CollisionShape3D { Shape = new WorldBoundaryShape3D { Plane = new Plane(Vector3.Up, 0f) } });
        AddChild(body);
        _ragdollWorld = body;
        _ragdollWorldNote = "flat floor at z = 0";
    }

    // ---- bookkeeping ----

    void RemoveRagdolls(int playerId, bool keepDeathState = false)
    {
        if (_pendingDeaths.Count > 0) _pendingDeaths.RemoveAll(d => d.Id == playerId);   // a death still queued for a player who is alive again or gone (M40-T9)
        OrkDeathForget(playerId);   // Ork O7c: a death clip under way is over
        if (!keepDeathState) MarineDeathForget(playerId);   // pass 2: so is a marine's (ProcessDeaths keeps it until the ragdoll exists: the slide's velocity and the death's seen time are read from it)
        for (int i = _ragdolls.Count - 1; i >= 0; i--)
            if (_ragdolls[i].PlayerId == playerId) RemoveRagdoll(_ragdolls[i]);
    }

    void RemoveRagdoll(Ragdoll rd)
    {
        _ragdolls.Remove(rd);
        _ragdollsRemoved++;
        if (IsInstanceValid(rd)) rd.QueueFree();
    }

    /// <summary>Frame bookkeeping: settled ragdolls, frame-time statistics and (bots) a periodic log line.</summary>
    void UpdateRagdolls(double delta)
    {
        PrewarmFrame();   // M40-T9: the weapon models and one hidden ragdoll a little after joining, so the first real death and the first weapon rig are not the first builds
        bool simulating = false;
        foreach (var rd in _ragdolls)
        {
            if (rd.Simulating) simulating = true;
            else if (rd.SettledAfter >= 0 && !rd.HasMeta("settled_logged"))
            {
                rd.SetMeta("settled_logged", true);
                _ragdollsSettled++;
                _ragdollSettleSum += rd.SettledAfter;
                if (_bot || rd.Own) GD.Print($"t {_connectedFor:F1} s: ragdoll of P{rd.PlayerId} baked after {rd.SettledAfter:F1} s ({_ragdolls.Count(r => r.Simulating)} still simulating)");
            }
        }
        if (simulating) { _framesSim++; _framesSimSum += delta; _framesSimMax = Math.Max(_framesSimMax, delta); }
        else { _framesIdle++; _framesIdleSum += delta; }
        if (_bot && _connectedFor - _ragdollLogAt >= 10.0 && _framesSim > 0)
        {
            _ragdollLogAt = _connectedFor;
            GD.Print($"t {_connectedFor:F0} s: ragdolls started {_ragdollsStarted} baked {_ragdollsSettled} removed {_ragdollsRemoved}; frame time with ragdolls simulating avg {_framesSimSum / _framesSim * 1000:F1} ms max {_framesSimMax * 1000:F1} ms ({_framesSim} frames), " +
                     $"otherwise avg {(_framesIdle > 0 ? _framesIdleSum / _framesIdle * 1000 : 0):F1} ms ({_framesIdle} frames); physics process {Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess) * 1000:F2} ms");
        }
    }

    /// <summary>The F1 line about the death screen.</summary>
    string DeathInfoLine()
    {
        string joints = _ragdollDef is null ? "ragdoll not available" : $"{_ragdollDef.Description}; joints: the original limits as a 6-DOF joint's Euler limits, joint friction as extra angular damping, collisions with the map and within one ragdoll except with the parent body";
        string stats = $"started {_ragdollsStarted}, baked {_ragdollsSettled}{(_ragdollsSettled > 0 ? $" (after {_ragdollSettleSum / _ragdollsSettled:F1} s on average)" : "")}, {_ragdolls.Count(r => r.Simulating)} simulating, {_ragdolls.Count} lying or simulating (cap {RagdollCap}); world: {_ragdollWorldNote}";
        return $"Death: ragdoll from the original rig's ragdoll (original shapes, masses, inertia, material and joint limits, spine mapping: {joints}); {stats}; the push (original force level x ragdoll force multiplier / mass, units assumed, direction from the server) is provisional; vignette and death camera: after the user's description. " + DeathFxInfoLine();
    }

    // ---- the death camera ----

    /// <summary>
    /// While the own player is dead: a third-person view of the ragdoll that blends from the last camera in
    /// <see cref="DeathBlendSeconds"/>, then rises and pulls back slowly (placeholder numbers), kept out of walls by <see cref="Unblocked"/>.
    /// False (and the normal camera) while alive; control and the normal camera return at once on the respawn.
    /// </summary>
    bool DeathCamera(PlayerState me, float delta)
    {
        if (_camera is null || _gameSpace is null || Spectate is not null) return false;
        if (me.Alive)
        {
            if (_deathCameraOn)
            {
                _deathCameraOn = false;
                _cameraPlaced = false;   // the normal camera snaps to its place
                _switchT = 1f;           // no weapon-switch ease left over from before the death
                _aimBlend = 0f; _evadeBlend = 0f; _rigFraction = 1f; _rigFractionA = 1f;
            }
            return false;
        }
        var feet = _gameSpace.Transform * new Vector3(me.Position.X, me.Position.Y, me.Position.Z);
        var rd = OwnRagdoll;
        var focus = rd is not null ? rd.FocusPoint : feet + Vector3.Up * 1.2f;
        if (!_deathCameraOn)
        {
            _deathCameraOn = true;
            _deathAge = 0;
            _deathStart = _camera.GlobalTransform;
            _deathFov = _camera.Fov;
            var offset = _deathStart.Origin - focus;
            float length = Math.Max(offset.Length(), 0.5f);
            _deathAz = MathF.Atan2(offset.Z, offset.X);
            _deathEl = Math.Clamp(MathF.Asin(Math.Clamp(offset.Y / length, -1f, 1f)), 0.1f, 0.9f);
            _deathRadius = Math.Clamp(length, 2.5f, 5f);
            _deathFocus = focus;
            _deathFraction = 1f;
            _aimBlend = 0f; _evadeBlend = 0f;
        }
        _deathAge += delta;
        float t = (float)_deathAge;
        _deathFocus = _deathFocus.Lerp(focus, 1f - MathF.Exp(-8f * delta));   // the tumbling body does not shake the view
        float blend = Math.Clamp(t / DeathBlendSeconds, 0f, 1f);
        blend = blend * blend * (3f - 2f * blend);
        float az = _deathAz + 0.04f * Math.Min(t, 4f);   // almost still: the starting side is known to be clear of walls, a wider swing met props that have no collision shape in the install's data (the M39 crates and buttons do)
        float el = Mathf.Lerp(_deathEl, Math.Max(_deathEl, 0.4f), Math.Clamp(t / 1.5f, 0f, 1f));
        float radius = Mathf.Lerp(_deathRadius, _deathRadius + 1.2f, Math.Clamp(t / 3f, 0f, 1f));
        var wanted = _deathFocus + new Vector3(MathF.Cos(el) * MathF.Cos(az), MathF.Sin(el), MathF.Cos(el) * MathF.Sin(az)) * radius;
        // M40-T6: the blend from the last camera to the orbit is smoothed (a smoothstep over DeathBlendSeconds), the sphere cast from
        // the focus is damped (a pull-in is fast but continuous, the hard cast of the near plane's size is never violated), and the
        // camera is kept out of every living player's body (the killer standing over the corpse).
        var raw = _deathStart.Origin.Lerp(wanted, blend);
        CamProbeNote("death", raw);
        var position = DampFraction(ref _deathFraction, true, Depenetrate(_deathFocus, CameraSoftRadius + 0.05f, 3), raw, delta);
        position = Depenetrate(ClearOfBodies(position, me, includeOwn: false, _deathFocus, delta));
        var toFocus = _deathFocus - position;
        var look = toFocus.LengthSquared() > 1e-4f ? Basis.LookingAt(toFocus.Normalized(), Vector3.Up) : _deathStart.Basis;
        _camera.GlobalTransform = new Transform3D(_deathStart.Basis.Orthonormalized().Slerp(look, blend), position);
        _camera.Fov = Mathf.Lerp(_deathFov, 60f, blend);
        _cameraOffset = position - feet;
        _cameraInfo = $"death camera (after the user's description): orbit {az:F2} rad, elevation {el:F2}, {radius:F1} m, blend {blend:F2}, ragdoll {(rd is null ? "none" : rd.Simulating ? "simulating" : "lying")}";
        return true;
    }

    /// <summary>For --screenshot-on-effect: a ragdoll that died in the last 3 s is in the camera's view.</summary>
    bool DeathInView() =>
        _camera is not null && _ragdolls.Any(r => IsInstanceValid(r) && r.Age < 3.0 && r.IsInsideTree() && _camera.IsPositionInFrustum(r.FocusPoint) && !_camera.IsPositionBehind(r.FocusPoint));
}
