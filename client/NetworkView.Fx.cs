using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading.Tasks;
using SmClient.Bullets;
using SmFormats.Fx;
using Godot;
using SmContent;
using SmNet;
using SmSim;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// M25b: the original weapon effects in the networked view. Every shot (own, from the prediction; other players', from the server's shot events,
/// <see cref="GameClient.TakeShots"/>) plays the weapon's muzzle flash and shell ejection at its muzzle locator, a tracer from the muzzle to where the
/// shot ended, and the impact its hit container names for what it hit (surface of the level triangle, or the player's armour), with the original
/// decals; projectiles get their trails, detonations their explosions, the map its ambient effects (god rays, steam), and heavy fire and nearby blasts
/// shake the camera a little. The effects themselves, their textures and which effect each event plays are the install's
/// (<see cref="EffectCatalogue"/>, <see cref="FxLibrary"/>, <see cref="FxPlayer"/>); how the renderer treats their parameters is an interpretation
/// (see <see cref="FxPlayer"/>). <c>--placeholder-fx</c> turns all of it off and keeps the old placeholder spheres; <c>--no-fx</c> draws neither.
/// </summary>
public partial class NetworkView
{
    /// <summary><c>--placeholder-fx</c> (debug): the old placeholder projectile and blast spheres only; no original effects.</summary>
    public bool PlaceholderFx { get; init; }
    /// <summary><c>--fx-slow n</c> (showcase aid): effects run n times slower than the game, so that a 0.05 s tracer streak or a one-frame flash can be seen in a screenshot.</summary>
    public float FxSlow { get; init; } = 1f;
    /// <summary><c>--no-fx</c> (test aid): no effects of either kind for shots (frame time baseline).</summary>
    public bool NoFx { get; init; }

    sealed class FxAssets
    {
        public required EffectCatalogue Catalogue { get; init; }
        public required FxLibrary Library { get; init; }
        public double Milliseconds { get; init; }
        /// <summary>Every effect the weapons, hits and projectiles name, parsed and compiled in the background (a first use mid-fight costs tens of milliseconds).</summary>
        public required IReadOnlyDictionary<string, CSystem> Systems { get; init; }
        /// <summary>The DDS bytes of the decals those effects use, read in the same pass (the main thread never reads the package).</summary>
        public required IReadOnlyDictionary<string, byte[]> DecalBytes { get; init; }
        /// <summary>The effects that run their collision descriptions (the jump packs'; M41).</summary>
        public IReadOnlySet<string>? CollisionSystems { get; init; }
        /// <summary>The atlas decoded off the main thread (22 MB of DXT5).</summary>
        public Image? AtlasImage { get; init; }
        /// <summary>The shell casing model (or why it could not be read), read off the main thread too.</summary>
        public StaticModel? Casing { get; init; }
        public string? CasingError { get; init; }
        /// <summary>Effects that did not parse, for the log.</summary>
        public List<string> Failures { get; init; } = [];
        /// <summary>F-D1: the death actions the player blueprints and jump packs bind (null with <c>--no-death-fx</c> or when they cannot be read).</summary>
        public DeathEffects? Death { get; init; }
        public string? DeathError { get; init; }
    }

    /// <summary>The map's placed effects with the systems they play (compiled) and those systems' decals, all read in the background.</summary>
    sealed record MapFxLoad(IReadOnlyList<MapFxObject> Objects, IReadOnlyDictionary<string, CSystem> Systems, IReadOnlyDictionary<string, byte[]> DecalBytes, List<string> Failures);

    FxPlayer? _fx;
    Task<FxAssets>? _fxLoad;
    Task<MapFxLoad>? _fxMapLoad;
    EffectCatalogue? _catalogue;
    string _fxNote = "not loaded";
    string _fxMapNote = "";
    string _fxMapSpawned = "";
    ColorRect? _flash;
    double _flashLeft;
    // Per-player last shot, for the barrel smoke when firing stops.
    readonly Dictionary<int, (double At, int Weapon, bool Smoked)> _fxLastShot = new();
    // Trails of projectiles in flight.
    readonly Dictionary<int, Vector3> _fxProjectilePos = new();
    readonly Dictionary<int, Vector3> _fxProjectileDir = new();
    readonly HashSet<int> _fxTrailed = new();
    // Own shot detection (predicted state).
    PlayerState _fxPrev;
    bool _fxPrevSeen;
    int _fxChargeStage;
    double _fxChargeStageAt = double.NegativeInfinity;
    // Camera shake.
    Vector3 _shake;
    Transform3D _unshaken;
    bool _shakeApplied;
    // Counters (log and F1).
    int _fxArmourImpacts, _fxFleshImpacts;
    int _fxOwnShots, _fxRemoteShots, _fxImpacts, _fxBodyImpacts, _fxTracers, _fxDetonations, _fxTrails, _fxMissingWeapons;
    double _fxLogAt;
    readonly Dictionary<string, float> _tracerSpeed = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _fxNoted = new();
    bool _fxStarted;
    /// <summary>Connected time of the last weapon effect played (for <c>--screenshot-on-effect</c>).</summary>
    double _fxLastShotAt = double.NegativeInfinity;
    Vector3 _fxLastEnd, _fxLastFrom;
    /// <summary>A shot's effects started in the last 0.4 s and both ends of its tracer are in the camera's view.</summary>
    bool FxShotRecent => _connectedFor - _fxLastShotAt < 0.4 && _camera is not null && _camera.IsPositionInFrustum(_fxLastEnd) && _camera.IsPositionInFrustum(_fxLastFrom)
                         && _camera.GlobalPosition.DistanceTo(_fxLastEnd) < 60f;

    /// <summary>True once the effect player draws: the original effects replace the placeholder spheres and blasts.</summary>
    bool FxActive => _fx is { IsReady: true } && !PlaceholderFx && !NoFx;

    /// <summary>Starts reading the effect data in the background (the catalogue and the effects package take a few seconds); effects play once it is done.</summary>
    void SetUpFx()
    {
        _flash = new ColorRect { Name = "ScreenFlash", Color = new Color(1f, 1f, 1f, 0f), MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        _flash.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        var layer = new CanvasLayer { Name = "FxFlash", Layer = 2 };
        layer.AddChild(_flash);
        AddChild(layer);
        if (NoFx || PlaceholderFx || Lean) { _fxNote = Lean ? "not loaded (lean bot)" : NoFx ? "switched off (--no-fx)" : "placeholder spheres (--placeholder-fx)"; return; }   // perf/lean-bots
        string dir = _gameDir;
        bool loadDeath = !NoDeathFx;   // read on the main thread (it asks the engine for the command line)
        _fxLoad = Task.Run(() =>
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var catalogue = EffectCatalogue.Load(dir);
            var library = FxLibrary.Load(dir);
            var names = new HashSet<string>(StringComparer.Ordinal);
            void Add(IEnumerable<FxBinding> bindings) { foreach (var b in bindings) if (b.Play is { Name.Length: > 0 } p) names.Add(p.Name); }
            foreach (var w in catalogue.Weapons.Values) { Add(w.Bindings); foreach (var pr in w.Projectiles) Add(pr.Bindings); }
            foreach (var t in catalogue.Thrown.Values) { Add(t.Bindings); if (t.Projectile is { } tp) Add(tp.Bindings); }
            foreach (var table in catalogue.Containers.Values)
                foreach (var surface in table.Effects.Values) foreach (var plays in surface.Values) foreach (var p in plays) names.Add(p.Name);
            // M38: the effects on a player hit on raised / broken armour (health_ext armourshield_hit / armourshield_inactive_hit).
            foreach (var p in catalogue.Player.ArmourHit.Concat(catalogue.Player.ArmourInactiveHit)) names.Add(p.Name);
            // M41: the jump packs' ground effects (the pound's impact and the landings; their decals are read in the same pass).
            foreach (var pack in catalogue.JumpPacks.Values) foreach (var n in pack.Names) names.Add(n);
            // M44: the flight's effects (thrust, apex, fall, dive, idle glow, the pound's target marker) and the dash's burst.
            foreach (var pack in catalogue.JumpPacks.Values) foreach (var n in pack.FlightNames) names.Add(n);
            foreach (var plays in catalogue.Dashes.Values) foreach (var p in plays) if (p.Name.Length > 0) names.Add(p.Name);
            names.Add(ArmourSparksEffect);   // fx/bolt-tracer-impact
            names.Add(SlashFlashEffect);   // fx/air-slash-landing: the perk landing's burst for the air slash (NetworkView.SlashFlash.cs)
            // F-D1: the death effects the player blueprints and jump packs bind (their ground impact runs collisions like the jump packs' other ground effects).
            DeathEffects? death = null; string? deathError = null;
            if (loadDeath)
            {
                try
                {
                    death = DeathEffects.Load(dir);
                    foreach (var n in death.EffectNames) names.Add(n);
                }
                catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException)) { death = null; deathError = ex.Message; }
            }
            // Animation pass 1: the landing of a fall (the player blueprint's finish_falling_actions: jump_land at scale 0.5).
            foreach (var p in catalogue.FinishFalling.Plays) if (p.Name.Length > 0) names.Add(p.Name);
            // One pass over the package for all of them (a read per effect inflates it from the start each time).
            var failures = new List<string>();
            var systems = CompileSystems(library, names, failures, death?.EffectLengths);   // F-D1: the death effects read a duration of 0 as the length of their play (CSystem.From)
            // M41: only the jump packs' effects run their collision descriptions (their decals are read; the grenades' and blood droplets' are not).
            var collisionNames = catalogue.JumpPacks.Values.SelectMany(pack => pack.Names).ToHashSet(StringComparer.Ordinal);
            // M44: the thrusts' ground bursts (their droppers' collision content: scorch decal, flash, smoke) under a lift-off or a dash start.
            foreach (var pack in catalogue.JumpPacks.Values) foreach (var p in pack.Ascending) collisionNames.Add(p.Name);
            foreach (var plays in catalogue.Dashes.Values) foreach (var p in plays) collisionNames.Add(p.Name);
            // F-D1: a jump death's impact is a ground effect with a crater decal (the data's jumppack_land_death_mp), like the landings.
            if (death is not null) foreach (var side in new[] { death.Marines, death.Chaos }) if (side.JumpImpact is { } impact) foreach (var n in impact.EffectNames) collisionNames.Add(n);
            collisionNames.Add(SlashFlashEffect);
            foreach (var p in catalogue.FinishFalling.Plays) collisionNames.Add(p.Name);   // the landing is played with its collision content (GroundPoundFx.Play), as the jump packs' landings
            var decals = library.Textures(FxPlayer.DecalNames(systems.Values, collisionNames));
            StaticModel? casing = null; string? casingError = null;
            try { casing = FxPlayer.ReadCasing(dir, []); }
            catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException)) { casingError = ex.Message; }
            Image? atlas = null;
            if (library.Texture(library.Page.AtlasTexture) is { } dds) { atlas = new Image(); if (atlas.LoadDdsFromBuffer(dds) != Error.Ok) atlas = null; }
            return new FxAssets { Catalogue = catalogue, Library = library, Milliseconds = watch.Elapsed.TotalMilliseconds, Systems = systems, DecalBytes = decals, CollisionSystems = collisionNames, AtlasImage = atlas, Casing = casing, CasingError = casingError, Failures = failures, Death = death, DeathError = deathError };
        });
        _fxNote = "loading";
    }

    /// <summary>Reads (one pass) and compiles the named effects; for a background thread. Those that do not parse are left out and described in <paramref name="failures"/>.</summary>
    internal static Dictionary<string, CSystem> CompileSystems(FxLibrary library, IEnumerable<string> names, List<string> failures, IReadOnlyDictionary<string, float>? zeroDurations = null)
    {
        var systems = new Dictionary<string, CSystem>(StringComparer.Ordinal);
        foreach (var (name, fx) in library.Systems(names, failures))
        {
            try { systems[name] = CSystem.From(fx, zeroDurations is not null && zeroDurations.TryGetValue(name, out float zero) ? zero : 0f); }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or ArgumentException or IndexOutOfRangeException or NotSupportedException or EndOfStreamException) { failures.Add($"{name} ({ex.Message})"); }
        }
        return systems;
    }

    /// <summary>Main-thread follow-up of the background load: builds the player once the data is in; false while it is not ready.</summary>
    bool FxReady()
    {
        if (_fx is { IsReady: true }) return true;
        if (_fxLoad is null || _fxStarted) return false;
        if (!_fxLoad.IsCompleted) return false;
        _fxStarted = true;
        if (_fxLoad.IsFaulted)
        {
            _fxNote = "not available: " + _fxLoad.Exception?.GetBaseException().Message;
            _notes.Add("effects: " + _fxNote);
            GD.PrintErr("effects: " + _fxNote);
            return false;
        }
        var assets = _fxLoad.Result;
        _catalogue = assets.Catalogue;
        _deathEffects = assets.Death;
        if (assets.DeathError is { } deathError) { _notes.Add("death effects: " + deathError); GD.PrintErr("death effects: " + deathError); }
        var watch = System.Diagnostics.Stopwatch.StartNew();
        _fx = new FxPlayer { Name = "Effects" };
        AddChild(_fx);
        if (!_fx.Initialize(assets.Library, _gameDir, assets.AtlasImage, assets.Casing, assets.CasingError))
        {
            _fxNote = "not available: " + _fx.Status;
            _notes.Add("effects: " + _fxNote);
            GD.PrintErr("effects: " + _fxNote);
            _fx.QueueFree(); _fx = null;
            return false;
        }
        _fx.Preload(assets.Systems, assets.DecalBytes, assets.CollisionSystems);
        foreach (var failure in assets.Failures.Take(6)) GD.Print("  effect note: does not parse: " + failure);
        foreach (var n in assets.Catalogue.Notes.Take(6)) GD.Print("  effect note: " + n);
        if (assets.Death is { } death) GD.Print($"death effects: {death.Marines.All.Count()} marine and {death.Chaos.All.Count()} Chaos actions, {death.Families.Count} weapons by family, {death.EffectNames.Count()} effects{(death.Notes.Count > 0 ? "; " + string.Join("; ", death.Notes) : "")}");
        _fxNote = $"{assets.Catalogue.Weapons.Count} weapons, {assets.Catalogue.Thrown.Count} thrown items, {assets.Catalogue.Containers.Count} impact containers; {_fx.Status}, {_fx.MeshStatus}; {assets.Systems.Count} effects compiled, read in {assets.Milliseconds:F0} ms, atlas {watch.ElapsedMilliseconds} ms";
        GD.Print("effects: " + _fxNote);
        return true;
    }

    // ---- per frame ----

    /// <summary>Before the camera is placed: takes last frame's shake off so the camera code starts from its own result.</summary>
    void FxShakeBegin()
    {
        if (_shakeApplied && _camera is not null) _camera.GlobalTransform = _unshaken;
        _shakeApplied = false;
    }

    /// <summary>After the camera is placed: plays this frame's events, advances the effects and shakes the camera.</summary>
    void UpdateFx(PlayerState me, double delta)
    {
        if (_camera is null || _client is null || _config is null) return;
        UpdateFlash(delta);
        CharacterFlashFrame();   // fx/character-visuals: armour flashes on the models
        StimGlowFrame();   // fx/visuals-2 (item 48): the stim's red aura and cross (NetworkView.Stims.cs)
        LensGlowFrame();   // fx/character-visuals: the eye glow in the lens colour (NetworkView.LensGlow.cs)
        RequestPoundRings();   // M41
        if (!FxReady() || !FxActive) { _fxPrev = me; _fxPrevSeen = true; return; }
        try
        {
            FxOwnShots(me, delta);
            FxLaserTest(me);   // feel/scope-lascannon test aid (NetworkView.Laser.cs)
            foreach (var shot in _frameShots) FxRemoteShot(shot);
            FxStopFireSmoke();
            FxMapEffects();
            FxEyeGlow();   // M40-T13 (NetworkView.EyeGlow.cs)
            FxMelee(me);   // fx/impact-pass: stomps, slams and the power sword (NetworkView.MeleeFx.cs)
            FxJumpPacks(me);   // M44 (NetworkView.JumpFx.cs)
            FxDeaths();   // F-D1: the death effects that are due (NetworkView.DeathFx.cs)
            _fx!.Update(delta / Math.Max(1f, FxSlow), _camera);
            UpdateDeferredTracers();   // fx/visuals-2 (NetworkView.PistolTracer.cs)
            UpdateSlashCones();   // fx/visuals-2 (NetworkView.SlashCone.cs)
            UpdateMeltaCones();   // wfx (NetworkView.WeaponFx.cs)
            UpdateBulletLines();   // feel/bullets: the fallback streaks and the debug rays (NetworkView.Bullets.cs)
            FxApplyShake(delta);
            FxLog();
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            if (_fxNoted.Add("fault:" + ex.GetType().Name)) { GD.PrintErr($"effects failed: {ex}"); _notes.Add($"effects: {ex.GetType().Name}: {ex.Message}"); }
        }
        _fxPrev = me; _fxPrevSeen = true;
    }

    Vector3 DirToGodot(Num.Vector3 v) => _gameSpace!.Basis * new Vector3(v.X, v.Y, v.Z);

    static Basis AxesFromY(Vector3 y)
    {
        y = y.LengthSquared() < 1e-10f ? Vector3.Up : y.Normalized();
        var r = MathF.Abs(y.Y) > 0.99f ? Vector3.Right : Vector3.Up;
        var x = r.Cross(y).Normalized();
        return new Basis(x, y, x.Cross(y));
    }

    // ---- shots ----

    /// <summary>Whether the effect catalogue knows the weapon, noting once when it does not.</summary>
    WeaponEffects? FxWeapon(string name)
    {
        var w = _catalogue?.Weapon(name);
        if (w is null && _fxNoted.Add("weapon:" + name)) { _fxMissingWeapons++; GD.Print($"  effects: no effect data for weapon '{name}'"); }
        return w;
    }

    /// <summary>The effects of a shot at <paramref name="stage"/> (0 basic; k = charge stage k − 1), falling back to the basic shot's when the stage lists none.</summary>
    static ShotEffects ShotFor(WeaponEffects w, WeaponSpec spec, int stage)
    {
        if (stage <= 0 || spec.Charge is not { } charge || stage > charge.Stages.Count) return w.Basic;
        string name = charge.Stages[stage - 1].Name;
        string ability = w.Bindings.FirstOrDefault(b => b.ChargeStage == name)?.Ability ?? w.BasicAbility;
        var s = w.Shot(ability, name);
        return s.Fire.Count == 0 && s.Tracer is null && s.HitMaterial is null ? w.Basic : s;
    }

    /// <summary>Where a player's effect marker is now (world position and axes with +Y along the locator's +Y); null without a weapon model.</summary>
    (Vector3 Position, Basis Axes)? FxPlace(int shooter, string marker)
    {
        if (marker.Length > 0 && MarkerWorld(shooter, marker) is { } t) return (t.Origin, t.Basis);
        if (MuzzleWorld(shooter) is { } m) return (m.Position, AxesFromY(m.Direction));
        return null;
    }

    /// <summary>Fallback place when the player has no weapon model: in front of the eye along the shot.</summary>
    (Vector3 Position, Basis Axes) FxFallback(Num.Vector3 eye, Vector3 dir) => (ToGodot(eye) + dir * 0.9f, AxesFromY(dir));

    void FxOwnShots(PlayerState me, double delta)
    {
        if (!_fxPrevSeen || _client is null || _config is null) return;
        var p = _fxPrev;
        var weapon = Loadouts.InHand(_config, me);
        // The charged shot's stage: remembered while the charge builds, used by the shot that follows within a second.
        if (me.Charging && weapon?.Charge is { } ch) { _fxChargeStage = Combat.ChargeStage(ch, me.ChargeTime, firing: true); _fxChargeStageAt = _connectedFor; }
        bool comparable = me.Alive && p.Alive && me.Slot == p.Slot && me.Class == p.Class && me.MeleeStep == 0 && p.MeleeStep == 0 && !me.Throwing;
        if (BarrelProbe && me.Clip < p.Clip) GD.Print($"barrel-probe: clip {p.Clip}->{me.Clip}: comparable {comparable} (alive {me.Alive}/{p.Alive}, slot {me.Slot}/{p.Slot}, melee {me.MeleeStep}/{p.MeleeStep}, throwing {me.Throwing}), weapon {(weapon is null ? "none" : weapon.Name)}");
        if (!comparable || weapon is null) { return; }
        if (BarrelProbe && p.Charging && !me.Charging) GD.Print($"barrel-probe: charge ended: clip {p.Clip}->{me.Clip} heat {p.Heat:F2}->{me.Heat:F2} cooldown {p.Cooldown:F2}->{me.Cooldown:F2} pending {p.PendingShot}->{me.PendingShot} held {me.FireHeld} fired events {_frameOwnEvents.Count(e => e.Events.Fired)} stage {_fxChargeStage}");
        int shots = Math.Max(0, p.Clip - me.Clip);
        // Weapons without a clip cost (or a shot that spends a recharged round in one frame): the cooldown restarting, or heat rising, is a shot.
        if (shots == 0 && me.SwitchLeft <= 0f && (me.Cooldown > p.Cooldown + 0.002f || (me.Heat > p.Heat + 0.001f && !me.Charging && me.FireHeld))) shots = 1;
        if (shots == 0 && me.Reserve < p.Reserve && me.Clip <= p.Clip) shots = 1;
        // fx/barrel-origin: a shot the clip, cooldown and heat did not show (a charged shot released from the pending state costs nothing at that moment) is still a fired event of the
        // prediction: it gets its flash, its tracer or ball origin and its note like any other (before: none of its effects played).
        if (shots == 0 && _frameOwnEvents.Count(e => e.Events.Fired) is > 0 and var fired && !OldBarrel)
        {
            shots = fired;
            if (BarrelProbe) GD.Print($"barrel-probe: a fired event of {weapon.Name} with no clip, cooldown or heat change: drawn as {shots} shot(s) (clip {p.Clip}->{me.Clip}, heat {p.Heat:F2}->{me.Heat:F2}, charging {me.Charging}, stage {_fxChargeStage})");
        }
        if (shots <= 0) return;
        shots = Math.Min(shots, 3);
        int stage = _connectedFor - _fxChargeStageAt < 1.0 && !me.Charging ? _fxChargeStage : 0;
        if (stage > 0) { _fxChargeStage = 0; _fxChargeStageAt = double.NegativeInfinity; }
        if (stage > 0 && !OldBarrel) shots = 1;   // fx/barrel-origin: a charged shot costs several rounds (the plasma gun's 15) but is one shot: one flash, one ball, not three
        int index = Loadouts.WeaponIndex(_config, me, me.Slot);
        _fxOwnShots += shots;
        for (int i = 0; i < shots; i++) FxPlayOwnShot(me, weapon, index, stage, i, shots);
        // Camera kick: a little per shot, more for the heavy weapons (placeholder; with the data's handling on, the climb and sway kick instead: NetworkView.Spread.cs).
        if (!HandlingActive) FxKick(weapon, stage);
        HeavyWeaponKick(me, weapon, stage);   // feel/weight-impact (NetworkView.CameraImpulse.cs)
    }

    void FxPlayOwnShot(PlayerState me, WeaponSpec weapon, int index, int stage, int nth = 0, int of = 1)
    {
        var w = FxWeapon(weapon.Name);
        if (w is null) return;
        var (end, hit, surface, normal, dir, victim) = TraceOwn(me, weapon, nth, of);
        var endG = ToGodot(end);
        var dirG = DirToGodot(dir).Normalized();
        var normalG = DirToGodot(normal).Normalized();
        bool hitScan = weapon.Delivery == DeliveryKind.Raycast;
        var start = _traceOwnStart;
        if (BulletProbe && hitScan && MuzzleWorld(_client!.PlayerId) is { } muzzle)
            BulletProbeShot("own", _client.PlayerId, weapon.Name, start, dir, end, ToGame(muzzle.Position), RemoteShotOrigin(me), Combat.EyePosition(me, _config!), $"; camera drawn {V(ToGame(_camera!.GlobalPosition))} rebuilt {V(_rebuiltCamera)}, aim blend {_aimBlend:F2}, pitch {me.Pitch:F3} view {_pitch:F3}");
        if (BarrelProbe && BarrelCanLaunch(weapon)) GD.Print($"barrel-probe: own shot of {weapon.Name} ({weapon.Delivery}), muzzle {(FxPlace(_client!.PlayerId, w.MuzzleMarker) is null ? "none" : "found")} marker '{w.MuzzleMarker}'");
        if (BarrelCanLaunch(weapon) && FxPlace(_client!.PlayerId, w.MuzzleMarker) is { } ownMuzzle) BarrelNoteShot(_client.PlayerId, index, start, ownMuzzle.Position);   // fx/barrel-origin
        FxShot(_client!.PlayerId, weapon, w, index, stage, endG, hit, surface, normalG, dirG, Combat.EyePosition(me, _config!), own: true, victim, ArmourUpForHit(hit, victim, own: true, OwnHitArmoured(victim)),   // protocol 22: the last hit report on this victim
               hitScan ? ToGodot(start) : null, hitScan ? dirG : null);
    }

    /// <summary>The origin of the last <see cref="TraceOwn"/> (game space).</summary>
    Num.Vector3 _traceOwnStart;

    /// <summary>
    /// Where the own shot goes, for the effects: the same ray the shot is sent along (the camera's for a human, the aim for a bot) against the level's
    /// weapon collision and the other players' hit shapes as drawn; the point, what it hit, the level triangle's surface, the surface normal facing the
    /// shooter and the direction. The server's answer decides the damage; this is only the picture.
    /// </summary>
    (Num.Vector3 End, ShotHit Hit, int Surface, Num.Vector3 Normal, Num.Vector3 Dir, int Victim) TraceOwn(PlayerState me, WeaponSpec weapon, int nth = 0, int of = 1)
    {
        var c = _config!;
        Num.Vector3 start, dir;
        if (!_bot && Spectate is null) { var (_, _, s0, d0) = CenterRayTarget(me, c); start = s0; dir = d0; }
        else { start = Combat.EyePosition(me, c); dir = Combat.AimDirection(me.Yaw, me.Pitch); }
        dir = OwnSpreadDirection(me, dir, c);   // weapons/spread: the predicted shot's scatter, as the server draws it (NetworkView.Spread.cs)
        // feel/bullets: the ray the input of this shot really carried, by the server's rules (NetworkView.Bullets.cs); the camera of the frame before is the fallback.
        if (BulletsOn && SentShotRay(me, c, nth, of) is { } sent) { start = sent.Origin; dir = sent.Direction; }
        _traceOwnStart = start;
        float range = weapon.Delivery == DeliveryKind.Cone && weapon.Cone is { } cone ? cone.Length : weapon.MaxRange > 0f ? weapon.MaxRange : 100f;
        float best = range;
        ShotHit hit = ShotHit.None; int surface = -1; var normal = -dir; int victim = -1;
        if (weapon.Delivery == DeliveryKind.Raycast)
        {
            // M39: players and props a piercing shot passes (NetworkView.Pierce.cs); without penetration the same first hit as before.
            var picture = PictureShot(start, dir, range, Pierces.Of(weapon, 0, upgrade => Pierces.HasUpgrade(c, me, upgrade)), c);
            if (picture.EndVictim is { } v)
                return (start + dir * picture.EndDistance, picture.EndZone == HitZone.Head ? ShotHit.Head : ShotHit.Body, -1, -dir, dir, v.PlayerId);
            if (picture.Surface is { } s && _client?.Collision is { } level)
                return (s.Point, ShotHit.World, level.Weapons.MaterialOf(s.Triangle), Num.Vector3.Dot(s.Normal, dir) > 0f ? -s.Normal : s.Normal, dir, -1);
            return (start + dir * picture.EndDistance, ShotHit.None, -1, -dir, dir, -1);
        }
        if (_client?.Collision is { } world && world.Weapons.Raycast(start, dir, range) is { } wall)
        {
            best = wall.Distance; hit = ShotHit.World; surface = world.Weapons.MaterialOf(wall.Triangle);
            normal = Num.Vector3.Dot(wall.Normal, dir) > 0f ? -wall.Normal : wall.Normal;
        }
        if (weapon.Delivery != DeliveryKind.Cone)
            foreach (var other in _lastOthers)
            {
                if (!other.Alive) continue;
                var (zone, distance) = Combat.HitShapes(start, dir, other.Position, HitBodyAsDrawn(other), best, other.Yaw);   // fix/hitboxes: as drawn
                if (zone != HitZone.None && distance < best) { best = distance; hit = zone == HitZone.Head ? ShotHit.Head : ShotHit.Body; normal = -dir; surface = -1; victim = other.PlayerId; }
            }
        return (start + dir * best, hit, surface, normal, dir, victim);
    }

    void FxRemoteShot(ShotView shot)
    {
        if (_config is null || shot.Weapon < 0 || shot.Weapon >= _config.Weapons.Count) return;
        var spec = _config.Weapons[shot.Weapon];
        var w = FxWeapon(spec.Name);
        if (w is null) return;
        _fxRemoteShots++;
        // The shooter's eye as last shown, for the fallback origin and the direction of the shot.
        var shooter = _lastOthers.FirstOrDefault(o => o.PlayerId == shot.Shooter);
        var eye = shooter.PlayerId == shot.Shooter ? Combat.EyePosition(shooter, _config) : shot.End;
        var endG = ToGodot(shot.End);
        var from = FxPlace(shot.Shooter, w.MuzzleMarker)?.Position ?? ToGodot(eye);
        var dirG = (endG - from);
        if (dirG.LengthSquared() < 1e-6f) dirG = DirToGodot(Combat.AimDirection(shooter.Yaw, shooter.Pitch));
        dirG = dirG.Normalized();
        var normalG = -dirG;
        // feel/bullets: the damaging ray of a hit-scan shot, rebuilt from the shooter's camera (RemoteShotOrigin: the record carries the end only); the bullet, the impact's
        // direction and the wall probe below follow it instead of the line from the muzzle.
        Vector3? rayOrigin = null, rayDir = null;
        var origin = eye;
        bool hitScan = spec.Delivery == DeliveryKind.Raycast && shooter.PlayerId == shot.Shooter;
        if (hitScan && (BulletsOn || ShowShotRays || BulletProbe))
        {
            var rebuilt = RemoteShotOrigin(shooter);
            var trueDir = BulletPaths.DirectionTo(rebuilt, shot.End, Combat.AimDirection(shooter.Yaw, shooter.Pitch));
            rayOrigin = ToGodot(rebuilt);   // with --no-bullets only the debug rays use it
            if (BulletsOn) { origin = rebuilt; rayDir = dirG = DirToGodot(trueDir).Normalized(); normalG = -dirG; }
            if (BulletProbe && FxPlace(shot.Shooter, w.MuzzleMarker)?.Position is { } m) BulletProbeRemote(shot, spec.Name, rebuilt, trueDir, ToGame(m), eye);
        }
        if (shot.Hit == ShotHit.World && _client?.Collision is { } world)
        {
            var dir = origin == shot.End ? Num.Vector3.UnitY : Num.Vector3.Normalize(shot.End - origin);
            float dist = Num.Vector3.Distance(origin, shot.End);
            // Quantised end points (1/32 m) can sit just in front of or behind the surface: look a little past.
            if (world.Weapons.Raycast(origin, dir, dist + 0.4f) is { } wall)
                normalG = DirToGodot(Num.Vector3.Dot(wall.Normal, dir) > 0f ? -wall.Normal : wall.Normal).Normalized();
        }
        if (BarrelCanLaunch(spec) && shooter.PlayerId == shot.Shooter && FxPlace(shot.Shooter, w.MuzzleMarker) is { } theirMuzzle) BarrelNoteShot(shot.Shooter, shot.Weapon, RemoteShotOrigin(shooter), theirMuzzle.Position);   // fx/barrel-origin
        int victim = shot.Hit is ShotHit.Body or ShotHit.Head ? VictimAt(shot.End, shot.Shooter) : -1;
        FxShot(shot.Shooter, spec, w, shot.Weapon, shot.Stage, endG, shot.Hit, shot.Surface, normalG, dirG, eye, own: false, victim, ArmourUpForHit(shot.Hit, victim, own: false, shot.Armoured),   // protocol 22: the server's armour bit
               rayOrigin, rayDir);
    }

    /// <summary>
    /// How fast the bright head of a weapon's tracer flies (m/s), or 0 for a tracer that is there at once. Only a clamped streak has a head: the
    /// <c>USpeed</c> of its beam (read as m/s: provisional, FxPlayer). A wrapping texture's <c>USpeed</c> is a texture scroll, not a flight: the
    /// Space Marine lascannon's beam carries a few scrolling layers (50, 10, 2) over its instant laser, and read as heads they delayed its impact.
    /// </summary>
    float TracerHeadSpeed(string effect)
    {
        if (_tracerSpeed.TryGetValue(effect, out float v)) return v;
        float speed = 0f;
        if (_fx?.System(effect) is { } sys)
        {
            static IEnumerable<CNode> All(CNode n) => new[] { n }.Concat(n.Children.SelectMany(All));
            speed = sys.Roots.SelectMany(All).OfType<CBeam>().Where(b => b.Material.UAddressMode == 1 && b.MeterPerU > 0.0001f).Select(b => b.USpeed).DefaultIfEmpty(0f).Max();
        }
        _tracerSpeed[effect] = speed;
        return speed;
    }

    /// <summary>
    /// The longest a bullet tracer's head takes to cross a shot, seconds. User's knowledge of the original (2026-10-28): the bolter's rounds are a
    /// fast streak, not a visible shell, and the impact arrives with it. The beams' own <c>USpeed</c> (800 m/s; 550 for the storm bolter) gives that
    /// to 80 m; for a longer shot the head is sped up to cross in this time (ours: the number is a choice, a few frames).
    /// </summary>
    const float MaxTracerFlight = 0.1f;

    /// <summary>
    /// One shot's effects. Muzzle and fire effects at the weapon's markers (attached ones follow the marker), the tracer from the muzzle to the end
    /// point, the impact by the hit container (after the tracer's head would have arrived), and the shooter's note for the barrel smoke.
    /// </summary>
    void FxShot(int shooter, WeaponSpec spec, WeaponEffects w, int weaponIndex, int stage, Vector3 end, ShotHit hit, int surface, Vector3 normal, Vector3 dir, Num.Vector3 eye, bool own, int victim = -1, bool? armourUp = null,
                Vector3? rayOrigin = null, Vector3? rayDir = null)
    {
        var fx = _fx!;
        var se = ShotFor(w, spec, stage);
        var fallback = FxFallback(eye, dir);
        _fxLastShot[shooter] = (_connectedFor, weaponIndex, false);
        _fxLastShotAt = _connectedFor; _fxLastEnd = end; _fxLastFrom = fallback.Position;
        if ((FxPlace(shooter, w.MuzzleMarker) ?? fallback) is var camMuzzle && (BarrelCamRemote ? shooter != _client?.PlayerId : shooter == _client?.PlayerId || _barrelCam is null || _connectedFor - _barrelCam.Value.At > 2.0)) _barrelCam = (_connectedFor, camMuzzle.Position, camMuzzle.Axes.Y);   // fx/barrel-origin: --spectate barrel
        var played = _fxPlayedScratch;   // feel/bullets: reused (it was a new set per shot)
        played.Clear();
        float travel = 0f;
        bool tracered = false;
        var plays = se.Fire.AsEnumerable();
        if (se.Tracer is { } tracer && !se.Fire.Contains(tracer)) plays = plays.Append(tracer);
        foreach (var p in plays)
        {
            if (!played.Add(p)) continue;
            var place = FxPlace(shooter, p.Marker) ?? fallback;
            if (p.HasTarget)
            {
                if (ForceStreaks || place.Position.DistanceTo(end) < 0.4f) continue;
                // A tracer is fixed in the world once fired. A bullet's has a bright head that crosses in at most MaxTracerFlight and the impact comes
                // with it; a beam without a head (the lascannon's laser) is drawn along the whole shot from its first frame and its impact is at once.
                // feel/bullets (NetworkView.Bullets.cs): with the shot's true ray the bullet leaves the muzzle, joins the ray and flies along it to the end.
                if (PistolRaiseWait(shooter, spec) is > 0f and var wait)
                {
                    // fx/visuals-2 (item 38): the pistol is still coming up: the tracer starts from the raised muzzle once it is (NetworkView.PistolTracer.cs); the impact waits with it.
                    DeferTracer(wait, p, w, place.Position, end, rayOrigin, rayDir, own, shooter);
                    float dist = place.Position.DistanceTo(end), headSpeed = TracerHeadSpeed(p.Name);
                    travel = MathF.Max(travel, wait + (headSpeed > 1f ? dist / MathF.Max(headSpeed, dist / MaxTracerFlight) : 0f));
                }
                else travel = MathF.Max(travel, PlayTracer(p, w, place.Position, end, rayOrigin, rayDir, own, shooter));
                tracered = true;
            }
            else
            {
                Func<(Vector3 Position, Basis Axes)?>? follow = null;
                if (p.Attached) { string marker = p.Marker; follow = () => FxPlace(shooter, marker); }
                fx.Play(p.Name, place.Position, place.Axes, p.Scale <= 0f ? 1f : p.Scale, null, follow, p.Lifetime, 0.0, false, 1, tint: RaceTint(w.Wargear, p.Name), smokeRein: SmokeRein, followFor: AttachFollowSeconds);
            }
        }
        // feel/bullets: a hit-scan weapon whose effects name no tracer still sends a visible bullet along the damaging path (a pooled bright streak).
        if (!tracered && BulletsOn && stage == 0 && spec.Delivery == DeliveryKind.Raycast && rayOrigin is not null && (FxPlace(shooter, w.MuzzleMarker) ?? fallback) is var from && from.Position.DistanceTo(end) >= 0.4f)
            travel = PlayFallbackBullet(from.Position, end, rayOrigin, rayDir);
        FxImpact(w, se, hit, surface, end, normal, dir, travel, victim, armourUp);
    }

    readonly HashSet<FxPlay> _fxPlayedScratch = new((IEqualityComparer<FxPlay>)ReferenceEqualityComparer.Instance);

    /// <summary>The impact effects of a hit by the container of the shot's hit effect, oriented by each effect's own direction rule (interpretation).</summary>
    void FxImpact(WeaponEffects w, ShotEffects se, ShotHit hit, int surface, Vector3 end, Vector3 normal, Vector3 dir, double delay, int victim = -1, bool? armourUp = null)
    {
        if (hit == ShotHit.None || _catalogue is null || _fx is null) return;
        // M38: a player hit on raised armour reads as ceramite armour (bullets breaking armour), on broken armour as the marine's own flesh-penetrating surface, and the victim shows the
        // blueprint's armour-hit or broken-armour-hit effects. Unknown (armourUp null: no real knowledge, owner's direction: no guessing) uses the armour variant.
        bool up = armourUp ?? true;
        var plays = _catalogue.Resolve(se.HitMaterial, hit, surface, up);
        if (hit != ShotHit.World) FxArmourHit(up, end, dir, victim, delay);
        if (hit != ShotHit.World && up) ArmourSparks(end, dir, delay);   // fx/bolt-tracer-impact
        if (hit != ShotHit.World && armourUp == false) BloodSplashAt(end, dir, delay);   // fx/red-glow-blood: no armour left, a red splash
        if (hit != ShotHit.World) CharacterFlashHit(victim, armourUp);   // fx/character-visuals: the victim's model flashes (NetworkView.CharacterFlash.cs)
        if (plays.Count == 0) return;
        _fxImpacts++;
        if (hit != ShotHit.World) { _fxBodyImpacts++; if (up) _fxArmourImpacts++; else _fxFleshImpacts++; }
        foreach (var p in plays)
        {
            var pos = end + normal * 0.02f;
            Vector3 axis;
            if (p.TriggerPoint == "exit_wound") { pos = end + dir * 0.35f; axis = dir; }
            else axis = p.Direction switch
            {
                "reflect_hit_direction" => hit == ShotHit.World ? dir - 2f * dir.Dot(normal) * normal : -dir,
                "negative_hit_direction" => -dir,
                "hitpoint_normal" => hit == ShotHit.World ? normal : -dir,
                _ => normal,
            };
            _fx.Play(p.Name, pos, AxesFromY(axis), p.Scale <= 0f ? 1f : p.Scale, null, null, p.Lifetime, delay, false, 0, tint: RaceTint(w.Wargear, p.Name));
        }
    }

    /// <summary>Barrel smoke when a player stops firing (0.3 s without a shot).</summary>
    void FxStopFireSmoke()
    {
        if (_fx is null || _config is null || _catalogue is null || _fxLastShot.Count == 0) return;
        foreach (var id in _fxLastShot.Keys.ToList())
        {
            var (at, weapon, smoked) = _fxLastShot[id];
            if (smoked || _connectedFor - at < 0.3) continue;
            _fxLastShot[id] = (at, weapon, true);
            if (weapon < 0 || weapon >= _config.Weapons.Count || _catalogue.Weapon(_config.Weapons[weapon].Name) is not { } w) continue;
            foreach (var p in w.StopFire)
            {
                if (FxPlace(id, p.Marker) is not { } place) continue;
                string marker = p.Marker;
                _fx.Play(p.Name, place.Position, place.Axes, p.Scale <= 0f ? 1f : p.Scale, null, p.Attached ? () => FxPlace(id, marker) : null, p.Lifetime, 0.0, false, 0, tint: RaceTint(w.Wargear, p.Name), smokeRein: SmokeRein, followFor: AttachFollowSeconds);
            }
        }
    }

    // ---- projectiles and explosions ----

    /// <summary>
    /// The tint a Chaos plasma weapon gives the effect it plays (<see cref="PlasmaColour.TintFor"/>: owner's knowledge, 2026-10-28, "the plasma of the chaos marines is
    /// purple while the marines is blue"; the Chaos files' own plasma effects are purple already, the loyalist-named ones they share are recoloured), else null.
    /// </summary>
    static Color? RaceTint(string wargear, string effect) =>
        PlasmaColour.TintFor(wargear, effect) is { } t ? new Color(t.R, t.G, t.B) : null;

    /// <summary>The wargear name of the weapon a snapshot projectile came from ("" for a thrown item or an unknown weapon).</summary>
    string ProjectileWargear(ProjectileView q) =>
        !q.Thrown && _config is not null && q.Weapon >= 0 && q.Weapon < _config.Weapons.Count ? _config.Weapons[q.Weapon].Name : "";

    /// <summary>The projectile effects of a snapshot projectile (the weapon's, or the thrown item's), or null.</summary>
    ProjectileEffects? FxProjectileOf(ProjectileView q)
    {
        if (_catalogue is null || _config is null) return null;
        if (q.Thrown)
        {
            string name = q.Weapon >= 0 && q.Weapon < _config.Thrown.Count ? _config.Thrown[q.Weapon].Name : "";
            return _catalogue.Thrown.GetValueOrDefault(name)?.Projectile;
        }
        if (q.Weapon < 0 || q.Weapon >= _config.Weapons.Count || _catalogue.Weapon(_config.Weapons[q.Weapon].Name) is not { } w) return null;
        string? blueprint = Shots.ProjectileOf(_config, q.Weapon, q.Stage, false)?.Blueprint;
        if (blueprint is null) return w.Projectile;
        string leaf = blueprint.Split('\\', '/')[^1];
        return w.Projectiles.FirstOrDefault(p => p.Blueprint.Split('\\', '/')[^1].Equals(leaf, StringComparison.OrdinalIgnoreCase)) ?? w.Projectile;
    }

    /// <summary>
    /// Called for each projectile in flight, each frame (from <see cref="UpdateProjectiles"/>): starts its trail effects the first time and keeps them at
    /// the projectile. Returns whether the original trail draws it (the placeholder plasma ball is then hidden).
    /// </summary>
    bool FxProjectile(ProjectileView q, Vector3 position)
    {
        if (!FxActive) return false;
        if (_fxProjectilePos.TryGetValue(q.Id, out var last) && last.DistanceSquaredTo(position) > 1e-6f)
            _fxProjectileDir[q.Id] = (position - last).Normalized();
        _fxProjectilePos[q.Id] = position;
        var fx = FxProjectileOf(q);
        var trail = fx?.Trail;
        if (trail is null || trail.Count == 0) return false;
        if (_fxTrailed.Add(q.Id))
        {
            int id = q.Id;
            foreach (var p in trail)
            {
                var e = _fx!.Play(p.Name, position, AxesFromY(Vector3.Down), p.Scale <= 0f ? 1f : p.Scale, null,
                    () => _fxProjectilePos.TryGetValue(id, out var pos) ? (pos, AxesFromY(_fxProjectileDir.TryGetValue(id, out var d) ? -d : Vector3.Down)) : null,
                    p.Lifetime > 0f ? p.Lifetime : 8f, 0.0, false, 1, tint: RaceTint(ProjectileWargear(q), p.Name));
                if (e is not null) { e.StopOnLost = true; _fxTrails++; }
            }
            if (_fxTrailed.Count > 4096) _fxTrailed.Clear();
        }
        return true;
    }

    /// <summary>Forgets the position of a projectile that is gone (its trail effects stop emitting).</summary>
    void FxProjectileGone(int id)
    {
        _fxProjectilePos.Remove(id);
        _fxProjectileDir.Remove(id);
    }

    /// <summary>
    /// A detonation: the projectile's detonation effects, and the explosion its material container picks (the default surface: the snapshot does not say what
    /// it hit). Returns whether the original explosion plays (the placeholder blast sphere is then not drawn).
    /// </summary>
    bool FxDetonation(ProjectileView d)
    {
        if (!FxActive || _config is null) return false;
        var fx = FxProjectileOf(d);
        if (fx is null) return false;
        var pos = ToGodot(d.Position);
        // M38: the surface the blast went off on (the snapshot does not say): the nearest level triangle within a metre, so that the material container picks its entry for it and its decals
        // (craters, char, glow) lie on that surface, a wall's included, instead of always on the ground.
        var (surfacePoint, surfaceNormal, surface) = DetonationSurface(d.Position);
        var plays = new List<FxPlay>(fx.Detonation);
        var byMaterial = _catalogue!.Resolve(fx.DetonationMaterial, ShotHit.World, surface);
        if (plays.Count == 0 && byMaterial.Count == 0) return false;
        _fxDetonations++;
        _fxLastShotAt = _connectedFor; _fxLastEnd = pos; _fxLastFrom = pos;   // for --screenshot-on-effect
        string shooterWargear = ProjectileWargear(d);
        foreach (var p in plays) _fx!.Play(p.Name, pos + Vector3.Up * 0.05f, Basis.Identity, p.Scale <= 0f ? 1f : p.Scale, null, null, p.Lifetime, 0.0, false, 2, tint: RaceTint(shooterWargear, p.Name));
        foreach (var p in byMaterial)
            _fx!.Play(p.Name, surfaceNormal is { } n ? surfacePoint + n * 0.02f : pos + Vector3.Up * 0.05f, surfaceNormal is { } sn ? AxesFromY(sn) : Basis.Identity, p.Scale <= 0f ? 1f : p.Scale, null, null, p.Lifetime, 0.0, false, 2, tint: RaceTint(shooterWargear, p.Name));
        // Shake the camera by the distance, flash for a krak close by.
        if (_client is { } client)
        {
            float distance = ToGodot(client.Predicted.Position).DistanceTo(pos);
            float radius = d.Thrown ? 4f : 6f;
            if (distance < 25f) FxShake(Math.Clamp(1f - distance / 25f, 0f, 1f) * (radius > 5f ? 0.9f : 0.6f) * 0.012f * 3f);
            CameraKick(WeightFeel.BlastJoltDegrees(distance), "blast");   // feel/weight-impact
            string name = d.Thrown && d.Weapon >= 0 && d.Weapon < _config.Thrown.Count ? _config.Thrown[d.Weapon].Name : "";
            if (name.Contains("krak", StringComparison.OrdinalIgnoreCase) && client.Predicted.Alive && distance < 7f)
            {
                _flashLeft = 0.35;
                if (_bot) GD.Print($"t {_connectedFor:F1} s: krak grenade {distance:F1} m away: screen flash");
            }
        }
        return true;
    }

    /// <summary>
    /// The level surface a detonation at <paramref name="at"/> (game space) went off on: the nearest hit of six short rays along the axes (the point is a contact of the projectile's sphere, so
    /// the surface is within a few tenths of a metre). Returns the hit point and the normal facing the point (Godot space), and the level material id; (point, null, -1) when none is near.
    /// </summary>
    (Vector3 Point, Vector3? Normal, int Surface) DetonationSurface(Num.Vector3 at)
    {
        if (_client?.Collision is not { } world) return (ToGodot(at), null, -1);
        RayHit? best = null; Num.Vector3 bestDir = default;
        foreach (var dir in new[] { -Num.Vector3.UnitZ, Num.Vector3.UnitX, -Num.Vector3.UnitX, Num.Vector3.UnitY, -Num.Vector3.UnitY, Num.Vector3.UnitZ })
            if (world.Weapons.Raycast(at, dir, 1.0f) is { } hit && (best is null || hit.Distance < best.Value.Distance)) { best = hit; bestDir = dir; }
        if (best is not { } h) return (ToGodot(at), null, -1);
        var normal = Num.Vector3.Dot(h.Normal, bestDir) > 0f ? -h.Normal : h.Normal;
        return (ToGodot(h.Point), DirToGodot(normal).Normalized(), world.Weapons.MaterialOf(h.Triangle));
    }

    /// <summary>
    /// <c>--spectate shot</c> (showcase aid): for 1.5 s after a shot, a camera beside the first 14 m of its tracer, looking at the middle of it (the muzzle at one end);
    /// null otherwise (the "action" view then shows).
    /// </summary>
    (Vector3 Eye, Vector3 Target)? FxShotView()
    {
        if (_connectedFor - _fxLastShotAt > 1.5) return null;
        var line = _fxLastEnd - _fxLastFrom;
        float length = line.Length();
        if (length < 1f) return null;
        var dir = line / length;
        var far = _fxLastFrom + dir * MathF.Min(length, 14f);
        var mid = (_fxLastFrom + far) * 0.5f;
        var side = dir.Cross(Vector3.Up);
        side = side.LengthSquared() < 0.01f ? Vector3.Right : side.Normalized();
        float distance = Math.Clamp(MathF.Min(length, 14f) * 0.55f + 1.5f, 3.5f, 11f);
        return (mid + side * distance + Vector3.Up * 0.9f, mid);
    }

    // ---- feel ----

    void FxKick(WeaponSpec weapon, int stage)
    {
        string n = weapon.Name.ToLowerInvariant();
        float k = n.Contains("heavy_bolter") ? 0.004f : n.Contains("plasma_cannon") || n.Contains("las_cannon") || n.Contains("lascannon") ? 0.02f
            : n.Contains("grenade_launcher") || n.Contains("melta") ? 0.014f : n.Contains("plasma_gun") ? (stage > 0 ? 0.018f : 0.007f) : n.Contains("pistol") ? 0.0035f : 0.0045f;
        FxShake(k);
    }

    /// <summary>Adds camera shake (radians of rotation; ours, not data: subtle by design).</summary>
    void FxShake(float amount) => _shake += new Vector3(amount, amount, amount);

    void FxApplyShake(double delta)
    {
        if (_camera is null) return;
        float amount = _shake.X;
        var bob = LandShakeOffset();   // animation pass 1 (NetworkView.Fall.cs): the landing's camera shake, a position offset along the camera's own axes
        bool rotate = amount >= 0.0004f;
        if (!rotate) _shake = Vector3.Zero;
        float punch = PunchNow() + CameraImpulseNow(delta);   // feel/weight-impact: the impulses (NetworkView.CameraImpulse.cs); anim/melee-interaction: the charge connect's camera kick (NetworkView.MeleeInteraction.cs)
        if (!rotate && bob == Vector3.Zero && punch == 0f) return;
        _unshaken = _camera.GlobalTransform;
        var t = _unshaken;
        if (punch != 0f) t.Basis = t.Basis * new Basis(Vector3.Right, -punch) * new Basis(Vector3.Forward, punch * 0.35f);
        if (rotate)
        {
            var rng = _shakeRng;
            float a = (rng.Randf() - 0.5f) * 2f * amount, b = (rng.Randf() - 0.5f) * 2f * amount;
            t.Basis = t.Basis * new Basis(Vector3.Up, a) * new Basis(Vector3.Right, b);
            float decay = MathF.Exp(-14f * (float)delta);
            _shake *= decay;
        }
        if (bob != Vector3.Zero) t.Origin += _unshaken.Basis.X * bob.X + _unshaken.Basis.Y * bob.Y;
        _camera.GlobalTransform = t;
        _shakeApplied = true;
    }

    readonly RandomNumberGenerator _shakeRng = new();

    void UpdateFlash(double delta)
    {
        if (_flash is null) return;
        if (_flashLeft <= 0.0) { _flash.Visible = false; return; }
        _flashLeft -= delta;
        float a = (float)Math.Clamp(_flashLeft / 0.35, 0.0, 1.0);
        _flash.Color = new Color(1f, 0.98f, 0.92f, 0.55f * a * a);
        _flash.Visible = a > 0.01f;
    }

    // ---- map ambience ----

    /// <summary>Starts the map's own effects (god rays, steam, dust, the fake spotlights) as endless ambient effects once the map is known.</summary>
    void FxMapEffects()
    {
        if (_client is null || _fx is null || _client.MapName.Length == 0 || _gameSpace is null) return;
        if (_fxMapLoad is null && _fxMapSpawned != _client.MapName)
        {
            string dir = _gameDir, map = _client.MapName;
            var library = _fx.Library;
            _fxMapLoad = Task.Run(() =>
            {
                var objects = MapEffects.Load(dir, map);
                var failures = new List<string>();
                var systems = library is null ? new Dictionary<string, CSystem>() : CompileSystems(library, objects.Where(o => o.AutoTrigger && o.Fx.Length > 0).Select(o => o.Fx).Distinct(StringComparer.Ordinal), failures);
                var decals = library is null ? new Dictionary<string, byte[]>() : library.Textures(FxPlayer.DecalNames(systems.Values));
                return new MapFxLoad(objects, systems, decals, failures);
            });
            return;
        }
        if (_fxMapLoad is null || !_fxMapLoad.IsCompleted) return;
        var task = _fxMapLoad;
        _fxMapLoad = null;
        _fxMapSpawned = _client.MapName;
        if (task.IsFaulted) { _fxMapNote = "map effects: " + task.Exception?.GetBaseException().Message; GD.Print("  " + _fxMapNote); return; }
        var loaded = task.Result;
        _fx.Preload(loaded.Systems, loaded.DecalBytes);
        foreach (var failure in loaded.Failures.Take(6)) GD.Print("  effect note: does not parse: " + failure);
        var objects = loaded.Objects;
        int started = 0, unknown = 0, godRays = 0;
        var rng = new RandomNumberGenerator { Seed = 12345 };
        foreach (var o in objects)
        {
            if (!o.AutoTrigger || o.Fx.Length == 0) continue;
            if (_fx.System(o.Fx) is not { } placed) { unknown++; continue; }
            if (!FxPlayer.ShowGodRays && FxPlayer.OnlyGodRays(placed)) { godRays++; continue; }   // fx/impact-pass: the map's god rays are removed (the owner's change; --god-rays)
            // The object's own frame (its rows are its X, Y and Z in game space), into Godot space; +Y is the effect's axis.
            Vector3 Row(float x, float y, float z) => DirToGodot(Num.Vector3.Normalize(new Num.Vector3(x, y, z)));
            var m = o.Transform;
            var basis = new Basis(Row(m.M11, m.M12, m.M13), Row(m.M21, m.M22, m.M23), Row(m.M31, m.M32, m.M33));
            var e = _fx.Play(o.Fx, ToGodot(o.Position), basis, o.Scale > 0f ? o.Scale : 1f, null, null, 0f, -(o.PreComputeTime + rng.Randf() * 8f), true, -1, ambient: true);
            if (e is not null) started++;
        }
        _fxMapNote = $"map effects: {started} of {objects.Count} started ({unknown} unknown to the effects package; {godRays} god-ray effects left out{(FxPlayer.ShowGodRays ? "" : " and the others' god-ray beams hidden")}, --god-rays shows them), looping";
        GD.Print("effects: " + _fxMapNote);
    }

    // ---- diagnostics ----

    void FxLog()
    {
        if (_fx is null || !(_bot || _showInfo || FrameProfileOn || FrameStatsLog) || _connectedFor - _fxLogAt < 10.0 || _fx.CostFrames < 60) return;
        _fxLogAt = _connectedFor;
        GD.Print($"t {_connectedFor:F0} s: effects: update avg {_fx.CostSum / _fx.CostFrames:F2} ms, max {_fx.CostMax:F2} ms over {_fx.CostFrames} frames; live {_fx.Live}, quads {_fx.LastQuads} ({_fx.AdditiveQuads} additive, {_fx.AlphaQuads} alpha, dropped {_fx.DroppedTotal}), lights {_fx.LastLights}, decals {_fx.LiveDecals}; " +
                 $"quads a frame avg {(double)_fx.QuadSum / _fx.CostFrames:F0} ({(double)_fx.AlphaQuadSum / _fx.CostFrames:F0} alpha), max {_fx.QuadMax}, smoke {(FxPlayer.ShowSmoke ? "on" : "off")}; " +
                 $"own shots {_fxOwnShots}, remote shots {_fxRemoteShots}, tracers {_fxTracers} ({BulletStats()}; {BarrelStats()}), impacts {_fxImpacts} (on players {_fxBodyImpacts}), detonations {_fxDetonations}, trails {_fxTrails}; {JumpFxLog()}; {MeleeFxLog()}; spawned {_fx.Spawned}, culled {_fx.Culled}; " +
                 (_fx.Missing.Count > 0 ? $"missing: {string.Join(", ", _fx.Missing.Take(6))}" : "nothing missing"));
        _fx.CostSum = 0; _fx.CostMax = 0; _fx.CostFrames = 0; _fx.QuadSum = 0; _fx.AlphaQuadSum = 0; _fx.QuadMax = 0;
    }

    /// <summary>The F1 line on the effects: what is original and what is provisional.</summary>
    string FxInfoLine()
    {
        if (NoFx || PlaceholderFx) return $"Effects: {_fxNote}.";
        string live = _fx is { IsReady: true } fx
            ? $" Now: {fx.Live} effects, {fx.LastQuads} sprites ({fx.AdditiveQuads} additive), {fx.LastLights} lights, {fx.LiveDecals} decals{(fx.DroppedTotal > 0 ? $", {fx.DroppedTotal} dropped" : "")}; update {(fx.CostFrames > 0 ? $"{fx.CostSum / fx.CostFrames:F2}" : "-")} ms avg, {fx.CostMax:F2} ms max."
            : "";
        return $"Effects (M25b): original effect definitions, sprite atlas and decal textures loaded from the install ({_fxNote}); which effect each shot, hit and explosion plays: from the weapon data. " +
               $"Provisional (interpretation of the renderer): curve keys as fractions of an element's duration, a clamped tracer's USpeed as metres per second of its bright head, at least reaching the end in 0.1 s (user's knowledge: fast streaks; impacts arrive with the head), other beams there at once, rotated atlas sprites turned {(FxBatch.RotatedClockwise ? "clockwise" : "counter-clockwise")}, particle rate per normalised time, spray flight by drag and gravity, " +
               $"the effect axis (+Y), light energy, no soft particles, distortion, beam noise, sound or shell meshes; surface of a hit: level material ids read in the global table's order (supported by evidence); camera kick and the krak flash: ours. {_fxMapNote}{live}";
    }
}
