using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SmClient.Looks;
using SmContent;
using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// Ork O7a (docs/ork-faction-design.md sections 4, 5 and 8): the networked Ork Boy is drawn as an Ork. A player whose class has the Ork race
/// (<see cref="Races.Of"/> == <see cref="Race.Orks"/>) wears the boy's original skinned meshes (<see cref="OrkAssets"/>) on the boy's own rig; its look is the one its
/// appearance names (<see cref="Races.OrkLookOf"/>) or, for "automatic", one chosen by player id (<see cref="OrkLookRules"/>, tested). Never waits: a look not built yet shows
/// the default marine (the T7 placeholder) until it is, exactly as the marines' looks do (<see cref="SwapRules"/>); the rig (a different skeleton) is replaced, not re-skinned.
/// <para>
/// Weapons: <see cref="WeaponRig"/> on the Ork's own prop bones (<see cref="OrkWeaponMounts"/>, Ork O6), the stikkbomb in hand from the start of a throw to its release.
/// Locomotion (<see cref="PlaceOrk"/>): the boy has no relaxed stance, so the walk and run are the 2HR aim run for the shoota (the 1HM run for the slugga), four directions
/// blended by the M40-T4 blend space (<see cref="GaitSpace"/>, the strides measured on the boy's clips: <see cref="OrkLocomotion"/>), with the aim pitch weighting the aim
/// down / forward / up clips; standing is the idle with the additive aim offsets laid over it by pitch; the sprint is the one 1HM loop for every weapon. All clips are original; which
/// clip and the blend are interpretation (design section 5). **Not yet** (left on the locomotion pose, no T-pose): hit reactions, the charge, melee, the dodge hop, the death clips
/// and the body-turn / run-start / foot-pin rules of the marines (O7c), the shot recoil, reload and the throw's release mapping (O7b); the corpse is the boy's ragdoll.
/// </para>
/// </summary>
public partial class NetworkView
{
    /// <summary><c>--no-ork-model</c> (test aid, Ork O7a): Orks are drawn as the default marine, as before O7a (before / after runs).</summary>
    bool NoOrkModel => _noOrkModel ??= CmdFlag("no-ork-model");
    bool? _noOrkModel;

    OrkAssets? _orks;
    bool _orkFailed;
    Task<OrkLocomotionBundle>? _orkLoad;
    OrkLocomotionPlan? _orkPlan;
    GaitLibrary? _orkGaits;
    string _orkNote = "not used";
    readonly Dictionary<OrkMoveSet, GaitFamily?> _orkFamilies = new(ReferenceEqualityComparer.Instance);   // by the plan's set object: no key string per frame
    readonly Dictionary<OrkMoveSet, string> _orkIdleKeys = new(ReferenceEqualityComparer.Instance);
    string? _orkIdleClip;
    bool _orkIdleClipLooked;
    readonly HashSet<string> _orkNoted = new();
    readonly Dictionary<int, OrkTrack> _orkTracks = new();
    byte _ownOrkLook = Races.OrkLookAutomatic;
    byte _ownRequestedClass = Protocol.ServerDefaultClass;
    int _orkBuilds, _orkPlaced;
    double _orkBuildMs, _orkLogAt;

    /// <summary>What the Ork's pose code remembers per player between frames.</summary>
    sealed class OrkTrack
    {
        public float? Throw;      // seconds into the throw this frame (null: not throwing): the weapon rig shows the stikkbomb until its release
    }

    OrkTrack OrkTrackOf(int id)
    {
        if (!_orkTracks.TryGetValue(id, out var t)) _orkTracks[id] = t = new OrkTrack();
        return t;
    }

    /// <summary>The own look and class the Hello carried (the front end's profile): the own Ork look, and whether the Ork is warmed while the map loads.</summary>
    void SetOwnOrk(ClientOptions options)
    {
        _ownOrkLook = options.OrkLook;
        _ownRequestedClass = options.RequestedClass;
    }

    /// <summary>Whether a player plays the Ork Boy (its class's race).</summary>
    bool IsOrkPlayer(PlayerState s) => !NoOrkModel && _config is not null && Races.Of(_config, s.Class, s.Team) == Race.Orks;

    /// <summary>Whether a character wears the Ork rig (the shared Ork assets' rig).</summary>
    bool IsOrkCharacter(CharacterViewer character) => _orks is not null && ReferenceEquals(character.Assets?.Rig, _orks.Rig);

    /// <summary>The shared Ork assets, made at the first Ork (the rig is read; the locomotion data on a worker). Null, once noted, when the install lacks the Ork.</summary>
    OrkAssets? Orks()
    {
        if (_orks is not null) return _orks;
        if (_orkFailed) return null;
        if (Lean)
        {
            // perf/lean-bots (NetworkView.Lean.cs): no Ork rig, looks, locomotion, actions, weapons or ragdoll; an Ork is the placeholder marine and vanishes at its death.
            _orkFailed = true;
            _orkNote = "not loaded (lean bot): Orks are the placeholder marine";
            GD.Print("ork: " + _orkNote);
            return null;
        }
        try
        {
            var watch = Stopwatch.StartNew();
            var notes = new List<string>();
            _orks = OrkAssets.Shared(_gameDir, notes);
            foreach (var n in notes) _notes.Add(n);
            string dir = _gameDir;
            _orkLoad = Task.Run(() => OrkLocomotion.Load(dir));
            StartOrkCatalogue(_orks.Rig.Rig.Skeleton.BoneNames);
            _orkNote = "loading";
            GD.Print($"ork: rig {RigProfiles.OrkBoy.Entry} read in {watch.ElapsedMilliseconds} ms ({_orks.Rig.Rig.Skeleton.BoneNames.Count} bones, {_orks.Rig.Rig.AnimationNames.Count} animations)");
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            _orkFailed = true;
            _orks = null;
            _orkNote = "not available: " + ex.Message;
            _notes.Add($"ork: the Ork could not be loaded ({ex.GetType().Name}: {ex.Message}); Orks are drawn as the default marine");
            GD.PrintErr($"ork: could not be loaded: {ex.GetType().Name}: {ex.Message}; Orks are drawn as the default marine");
        }
        return _orks;
    }

    /// <summary>The locomotion plan, once the worker has read it (null before, or when it failed: the Ork then idles in its idle clip).</summary>
    OrkLocomotionPlan? OrkPlan()
    {
        if (_orkPlan is not null) return _orkPlan;
        if (_orkLoad is not { IsCompleted: true } load) return null;
        _orkLoad = null;
        if (load.IsFaulted || load.IsCanceled)
        {
            _orkNote = "not available: " + (load.Exception?.GetBaseException().Message ?? "cancelled");
            GD.PrintErr("ork locomotion: " + _orkNote);
            return null;
        }
        var bundle = load.Result;
        _orkPlan = bundle.Plan;
        _orkGaits = bundle.Gaits;
        // The worker decoded the gait clips: the Ork rig's cache takes them instead of decoding the same clips again on the main thread.
        if (_orks is not null) foreach (var (name, animation) in bundle.Gaits.Animations) _orks.Rig.Preload(bundle.Gaits.RigKey, name, animation);
        _orkNote = $"gun {bundle.Plan.Gun?.Directions.Count ?? 0} directions, pistol {bundle.Plan.Pistol?.Directions.Count ?? 0}, sprint {bundle.Plan.Sprint ?? "none"}, toss {bundle.Plan.Toss ?? "none"}; {bundle.Gaits.Clips.Count} clip gaits measured";
        GD.Print("ork locomotion: " + _orkNote);
        foreach (var n in bundle.Plan.Notes.Concat(bundle.Gaits.Notes)) { GD.Print("  ork locomotion note: " + n); _notes.Add("ork: " + n); }
        TakeOrkActions(bundle);   // Ork O7b / O7c (NetworkView.OrkActions.cs)
        return _orkPlan;
    }

    // ---- the model ----

    /// <summary>The index of the Ork look a player wears: its appearance's (own: the Hello's), "automatic" by player id (<see cref="OrkLookRules"/>).</summary>
    int OrkLookOf(int playerId, bool local)
    {
        byte wire = local ? _ownOrkLook
            : _client?.AppearanceOf(playerId) is { Race: Race.Orks } a ? Races.OrkLookOf(a.Code) : Races.OrkLookAutomatic;
        return OrkLookRules.Resolve(wire, playerId);
    }

    /// <summary>
    /// <see cref="EnsureModel"/> for an Ork: the character wears its look's boy model once built (the files are read on a worker, <see cref="OrkAssets.Prefetch"/>); until then (and
    /// when the Ork assets cannot be made) the default marine stands in, and a character keeps its model until the wanted one is ready and the player is alive.
    /// </summary>
    CharacterViewer EnsureOrkModel(CharacterViewer? current, int playerId, bool local, PlayerState state)
    {
        string? currentKey = current is null ? null : current.GetMeta("look_key").AsString();
        var orks = Orks();
        if (orks is null) return current ?? Spawn(playerId, local, _assets!, LookKeys.Placeholder);
        int index = OrkLookOf(playerId, local);
        string look = OrkLookRules.Names[index], key = OrkLookRules.Keys[index];   // no string made per frame
        if (currentKey == key) return current!;
        orks.Prefetch(look);
        bool failed = IsFailed(key);
        bool ready = !failed && orks.IsReady(look) && (local || current is null || HeavyLeft);
        MarineAssets? built = null;
        if (ready)
        {
            var watch = Stopwatch.StartNew();
            long heavy = HeavyStart();
            try { built = orks.Build(look); }
            catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
            {
                Fail(key);
                _notes.Add($"ork: look {look} could not be built: {ex.GetType().Name}: {ex.Message}; the placeholder stays");
                GD.PrintErr($"ork: look {look} could not be built: {ex.GetType().Name}: {ex.Message}; the placeholder stays");
            }
            HeavyEnd(heavy);
            if (built is not null)
            {
                _orkBuilds++; _orkBuildMs += watch.Elapsed.TotalMilliseconds;
                GD.Print($"t {_connectedFor:F1} s: ork: look '{look}' built for P{playerId} in {watch.Elapsed.TotalMilliseconds:F0} ms ({OrkAssets.Describe(built)})");
            }
        }
        bool deathPending = _pendingDeaths.Any(d => d.Id == playerId);
        // A race change at the respawn (marine to Ork): until the Ork look is built the old marine rig must not stand in for the Ork, nor be posed by the Ork code: the placeholder at once.
        if (built is null && current is not null && SwapRules.DropWrongRig(IsOrkCharacter(current), wantsOrk: true, currentKey, state.Alive, deathPending))
        {
            current.QueueFree();
            _looksPlaceholders++;
            return Spawn(playerId, local, _assets!, LookKeys.Placeholder);
        }
        switch (SwapRules.Decide(currentKey, key, built is not null, state.Alive, deathPending))
        {
            case LookAction.Create:
                if (built is null) _looksPlaceholders++;
                return Spawn(playerId, local, built ?? _assets!, built is null ? LookKeys.Placeholder : key);
            case LookAction.Swap:
                _looksSwapped++;
                if (_bot || local || CmdFlag("look-log")) GD.Print($"t {_connectedFor:F1} s: P{playerId} changes its look ({(currentKey == LookKeys.Placeholder ? "placeholder" : "model")} -> Ork {look})");
                if (current!.SetLook(built!))
                {
                    current.SetMeta("look_key", key);
                    return current;
                }
                current.QueueFree();   // another rig (the placeholder marine, or a marine's model): a new character
                return Spawn(playerId, local, built!, key);
            default:
                return current!;
        }
    }

    /// <summary>The map load's whole warm-up (the marines' looks, then the Ork) may take this long from <see cref="_warmClock"/>; the handshake times out at 5 s.</summary>
    const int WarmTotalMs = 3000;
    Stopwatch? _warmClock;

    static bool WaitQuiet(Task? task, int milliseconds) => task is null || task.IsCompleted || ((IAsyncResult)task).AsyncWaitHandle.WaitOne(Math.Max(0, milliseconds));   // never throws: a faulted task is read by its user

    /// <summary>
    /// While the map loads, when the configuration has an Ork class (every match does): the Ork rig is read (<see cref="Orks"/>) and the locomotion and weapon catalogue start on
    /// workers, so a client that is not an Ork meets its first Ork mid-match without a rig read or a data read on the main thread. An Ork player also has its look built (the one it
    /// asked for, or, for "automatic", whose index depends on an id not known yet, every automatic look: four builds of 8 to 30 ms), the locomotion and the weapons waited for, so the
    /// first frame shows the Ork; all within what is left of <see cref="WarmTotalMs"/> after the marines' warm-up (the rest follows on demand).
    /// </summary>
    void WarmOrk()
    {
        if (NoOrkModel || _config is null || !_config.Classes.Any(c => c.Race == Race.Orks)) return;
        var watch = Stopwatch.StartNew();
        if (Orks() is not { } orks) return;
        if (_ownRequestedClass == Protocol.ServerDefaultClass || Races.Of(_config, _ownRequestedClass, Team.None) != Race.Orks) return;
        int Left() => WarmTotalMs - (int)(_warmClock?.ElapsedMilliseconds ?? watch.ElapsedMilliseconds);
        var indices = _ownOrkLook < Races.OrkLooks ? new[] { (int)_ownOrkLook } : Enumerable.Range(0, OrkLookRules.AutomaticLooks).ToArray();
        foreach (int i in indices) orks.Prefetch(OrkLookRules.Names[i]);
        int built = 0;
        foreach (int i in indices)
        {
            if (Left() <= 0) break;
            string look = OrkLookRules.Names[i];
            try { orks.Build(look); built++; }
            catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
            {
                Fail(OrkLookRules.Keys[i]);
                _notes.Add($"ork: warm-up of {look}: {ex.GetType().Name}: {ex.Message}");
                GD.PrintErr($"ork: warm-up of {look}: {ex.Message}");
            }
        }
        // The workers' reads (the locomotion plan, the weapon catalogue) are needed by the first frame; each is waited for without throwing (a failure is noted by OrkPlan / OrkWeaponAssets).
        WaitQuiet(_orkLoad, Left());
        WaitQuiet(_orkCatalogueLoad, Left());
        OrkPlan();
        OrkWeaponAssets();
        GD.Print($"ork: warmed {built} of {indices.Length} looks, the locomotion ({(_orkPlan is null ? "not yet" : "ready")}) and the weapons ({(_orkWeapons is null ? "not yet" : "ready")}) in {watch.ElapsedMilliseconds} ms; with the marines' warm-up {_warmClock?.ElapsedMilliseconds ?? watch.ElapsedMilliseconds} ms of {WarmTotalMs} ms");
    }

    // ---- weapons ----

    WeaponAssets? _orkWeapons;
    Task<(WeaponModels Catalogue, List<string> Notes, double Milliseconds)>? _orkCatalogueLoad;
    bool _orkWeaponsFailed;

    /// <summary>Starts reading the Ork's weapon catalogue on a worker (it is engine-free; the read took 180 ms on the main thread at the first Ork's first frame, a visible hitch).</summary>
    void StartOrkCatalogue(IReadOnlyList<string> bones)
    {
        if (NoWeapons || _orkCatalogueLoad is not null) return;
        string dir = _gameDir;
        _orkCatalogueLoad = Task.Run(() =>
        {
            var watch = Stopwatch.StartNew();
            var notes = new List<string>();
            var catalogue = WeaponModels.Load(dir, RigProfiles.OrkBoy, notes, RigProfiles.OrkBoy.DefaultWargear, bones);
            return (catalogue, notes, watch.Elapsed.TotalMilliseconds);
        });
    }

    /// <summary>
    /// The Ork's weapon models (shoota, slugga, choppa, stikkbomb) on the Ork's bones, once the worker has read the catalogue (null before: the Ork shows no weapons for those
    /// frames; null for good, noted once, when they cannot be read).
    /// </summary>
    WeaponAssets? OrkWeaponAssets()
    {
        if (_orkWeapons is not null || _orkWeaponsFailed || NoWeapons || _orks is null) return _orkWeapons;
        if (_orkCatalogueLoad is not { IsCompleted: true } load) return null;
        try
        {
            var (catalogue, notes, milliseconds) = load.GetAwaiter().GetResult();
            var wrap = Stopwatch.StartNew();
            _orkWeapons = WeaponAssets.FromCatalogue(_gameDir, _orks.Rig.Rig.Skeleton.BoneNames, catalogue, notes, milliseconds);
            GD.Print($"ork weapons: the main-thread wrapper (WeaponAssets.FromCatalogue) took {wrap.Elapsed.TotalMilliseconds:F2} ms");
            foreach (var n in notes) { _notes.Add(n); GD.Print("  note: " + n); }
            GD.Print($"ork weapons: {_orkWeapons.Catalogue.ByWargear.Count} wargear files, catalogue {milliseconds:F0} ms (read on a worker)");
        }
        catch (Exception ex) when (ex is not (StackOverflowException or AccessViolationException or OutOfMemoryException))
        {
            _orkWeapons = null;
            _orkWeaponsFailed = true;
            var cause = ex is AggregateException a ? a.GetBaseException() : ex;
            _notes.Add("ork weapon models: " + cause.Message + "; no weapons are drawn on Orks");
            GD.PrintErr("ork weapons: " + cause.Message + "; no weapons are drawn on Orks");
        }
        return _orkWeapons;
    }

    /// <summary>Shows the stikkbomb in hand for the throw this frame (from its start to the release, <see cref="SimConfig.ThrowRelease"/>); called by <see cref="SyncWeapons"/> before the rig updates.</summary>
    void OrkWeaponState(WeaponRig rig, PlayerState state)
    {
        float? throwTime = _orkTracks.TryGetValue(state.PlayerId, out var t) ? t.Throw : null;
        rig.SetGrenadeHeld(state.Alive && throwTime is { } tt && tt < _config!.ThrowRelease);
    }

    // ---- the corpse ----

    RagdollDef? _orkRagdoll;
    bool _orkRagdollTried;

    /// <summary>The Ork rig's ragdoll (<see cref="RagdollModel"/> of the boy's rig, 15 bodies), built at the first Ork death; null, noted once, when it cannot be (the corpse then vanishes, as a marine's does).</summary>
    RagdollDef? OrkRagdollDefinition(MarineAssets assets)
    {
        if (_orkRagdollTried) return _orkRagdoll;
        _orkRagdollTried = true;
        if (Lean) return null;   // perf/lean-bots: never reached (no Ork rig in lean mode); kept so a lean bot builds no corpse
        var notes = new List<string>();
        var watch = Stopwatch.StartNew();
        try
        {
            if (RagdollModel.Load(_gameDir, RigProfiles.OrkBoy, notes) is { } model) _orkRagdoll = RagdollDef.Build(model, assets, notes);
        }
        catch (Exception ex) when (ex is not (StackOverflowException or AccessViolationException or OutOfMemoryException))
        {
            _orkRagdoll = null;
            notes.Add($"ork ragdoll: not available ({ex.GetType().Name}: {ex.Message}); dead Orks disappear instead");
        }
        foreach (var n in notes) { _notes.Add(n); GD.Print("  note: " + n); }
        GD.Print(_orkRagdoll is null ? "ork ragdoll: not available" : $"ork ragdoll: {_orkRagdoll.Description} ({watch.ElapsedMilliseconds} ms)");
        return _orkRagdoll;
    }

    // ---- the pose ----

    /// <summary>
    /// <see cref="Place"/> for a character wearing the Ork rig, in the marines' order (NetworkView.cs <c>Place</c>): a reaction, the charge, a melee swing, the dodge hop,
    /// the stikkbomb toss, else the locomotion pose with the shot's recoil and the reload over it. Every clip is the boy's own (NetworkView.OrkActions.cs holds the choices);
    /// the jump-pack poses do not exist for the Ork. Without the action data (not read yet, or the install lacks it) the Ork shows its movement pose (never the rest pose).
    /// </summary>
    void PlaceOrk(CharacterViewer character, PlayerState state, float? throwTime, bool own, SmoothClip? smooth = null)
    {
        character.ClearPoseLayer();
        character.ClearTurnPose();
        float yaw = own ? DisplayYaw(state) : state.Yaw;
        // The dodge hop (O7c): the simulation lifts the body by the hop's height and the clip already lifts the feet, so the model is drawn that height lower to show it once (as the marine's).
        float hop = state.Alive && Evade.Move(state, _config!) is { LiftsPose: true } hopMove ? hopMove.HeightAt(Evade.Time(state, _config!)) : 0f;
        character.Transform = new Transform3D(new Basis(new Vector3(0, 0, 1), yaw), new Vector3(state.Position.X, state.Position.Y, state.Position.Z - hop));
        if (character.GetNodeOrNull<Node3D>("ChargeGlow") is { } glow) glow.Visible = false;
        var track = OrkTrackOf(state.PlayerId);
        track.Throw = state.Alive ? throwTime : null;
        _orkPlaced++;
        if (OrkPlan() is not { } plan) { OrkIdleFallback(character); return; }
        if (_orkActions is { } actions && character.Assets is { } assets && OrkActionPose(character, state, throwTime, own, smooth, plan, actions, assets)) return;
        if (_orkActions is null && throwTime is { } tt && plan.Toss is { } toss && character.Pose(toss, tt, ClipBlends.Throw)) return;
        if (!OrkLocomote(character, state, plan, own)) OrkStand(character, state, plan);
        if (_orkActions is { } a) OrkRecoil(character, state, a);   // O7b: each shot restarts the boy's additive recoil
        ReloadPose(character, state);   // O7b: the reload clip over the legs' movement (NetworkView.Reload.cs)
        if (_reloads.TryGetValue(state.PlayerId, out var reloading) && reloading.Player.Shown) OrkNote(state, KindReload, reloading.Player.ClipFraction, own);
    }

    /// <summary>Before the plan is read: the rig's idle clip (the clip table's first that exists), so the Ork is never in the rest pose.</summary>
    void OrkIdleFallback(CharacterViewer character)
    {
        if (!_orkIdleClipLooked && _orks is not null) { _orkIdleClipLooked = true; _orkIdleClip = _orks.Rig.FindAnimation(RigProfiles.OrkBoy.Clips.IdleCandidates, "Idle"); }   // looked up once
        if (_orkIdleClip is { } idle) character.Play(idle);
    }

    /// <summary>The sprint or the walk / run blend; false when the Ork is (nearly) standing or the data is missing (the caller then shows the idle).</summary>
    bool OrkLocomote(CharacterViewer character, PlayerState s, OrkLocomotionPlan plan, bool own)
    {
        var t = LocoOf(s.PlayerId);
        float speed = MathF.Sqrt(s.Velocity.X * s.Velocity.X + s.Velocity.Y * s.Velocity.Y);
        float fx = -MathF.Sin(s.Yaw), fy = MathF.Cos(s.Yaw);
        float forward = s.Velocity.X * fx + s.Velocity.Y * fy, right = s.Velocity.X * fy - s.Velocity.Y * fx;
        // The sprint: the same rule as the marines' (a forward speed past a quarter of the way from run to sprint), with the Ork's own speeds.
        var body = _config!.For(s.Class);
        bool other = !s.Grounded || s.Rush != RushPhase.None || s.Reaction != ReactionKind.None || Evade.Evading(s) || s.Jump != JumpPhase.None;
        if (other) t.SprintBlockedUntil = _connectedFor + SprintLockout;
        t.Sprint = s.Alive && ViewSmoothing.Sprinting(t.Sprint, !other && _connectedFor >= t.SprintBlockedUntil, speed, forward, body.RunSpeed, body.SprintSpeed, SprintEnterFraction, SprintLeaveFraction);
        if (t.Sprint && plan.Sprint is { } sprint)
        {
            var gait = _orkGaits?.Get(sprint);
            float rate = gait is { Speed: > 0.1f } g ? Math.Clamp(speed / g.Speed, 0.7f, 1.4f) : 1f;
            if (character.Stance("ork:sprint", null, sprint, null, 0f, rate, _fadeOverride > 0f ? _fadeOverride : plan.SprintBlend)) return true;
        }
        var set = plan.For(s.Slot == WeaponSlot.Pistol);
        if (set is null) return false;
        if (!NoGaitBlend && OrkGaitFamily(set, character.Assets!) is { } family)
        {
            if (t.GaitMoving)
            {
                if (GaitPlace(character, s, t, family, s.Pitch, OrkFade, true, own, out _)) return true;
                t.GaitMoving = false;
            }
            if (speed > RunThreshold && GaitPlace(character, s, t, family, s.Pitch, OrkFade, false, own, out _)) { t.GaitMoving = true; return true; }
            return false;
        }
        // No measured gaits: the run clip of the nearest direction (the old M36 style), at the speed ratio.
        if (speed <= RunThreshold) return false;
        float angle = MathF.Atan2(right, forward) * 180f / MathF.PI;
        var near = set.Directions.MinBy(d => MathF.Abs(AngleDifference(d.AngleDegrees, angle)))!;
        return character.Stance("ork:near:" + near.AngleDegrees, null, near.Run.Level, null, 0f, Math.Clamp(speed / 6f, 0.7f, 1.4f), OrkFade);
    }

    static float AngleDifference(float a, float b)
    {
        float d = (a - b) % 360f;
        return d > 180f ? d - 360f : d < -180f ? d + 360f : d;
    }

    /// <summary>Standing: the idle of the weapon in hand, the aim pitch laid over it as the additive down / up offsets (ours: the graph blends whole walk-on-the-spot poses instead).</summary>
    void OrkStand(CharacterViewer character, PlayerState s, OrkLocomotionPlan plan)
    {
        var set = plan.For(s.Slot == WeaponSlot.Pistol);
        if (set is null) { OrkIdleFallback(character); return; }
        if (!_orkIdleKeys.TryGetValue(set, out var idleKey)) _orkIdleKeys[set] = idleKey = "ork:idle:" + set.Idle;
        if (!character.Stance(idleKey, null, set.Idle, null, 0f, 1f, OrkFade)) { OrkIdleFallback(character); return; }
        float degrees = s.Pitch * 180f / MathF.PI;
        string? offset = degrees < 0f ? set.IdleDown : set.IdleUp;
        if (offset is not null && MathF.Abs(degrees) > 1f) character.SetPoseLayer(offset, 0f, Math.Clamp(MathF.Abs(degrees) / OrkPitchFull, 0f, 1f), additive: true);
    }

    /// <summary>The aim pitch (degrees) at which the additive idle offset has its whole weight (the walk and run clips' pitch entries are at 80).</summary>
    const float OrkPitchFull = 80f;

    bool OrkHasClip(MarineAssets assets, string clip) => assets.Rig.Rig.AnimationNames.Contains(clip, StringComparer.OrdinalIgnoreCase) && assets.Animation(clip) is not null;

    /// <summary>
    /// The blend space of an Ork move set (cached): per direction a walk and a run band, each a down / level / up pitch triple, and the idle as the pose below
    /// <see cref="IdleBlendSpeed"/> (as <see cref="HipGaitFamily"/> does for the marine's ready run; same layout, so <see cref="GaitPlace"/> runs it). Null, noted once, when a clip or gait is missing.
    /// </summary>
    GaitFamily? OrkGaitFamily(OrkMoveSet set, MarineAssets assets)
    {
        if (_orkFamilies.TryGetValue(set, out var cached)) return cached;
        string key = "ork:" + set.Family;
        GaitFamily? result = null;
        if (_orkGaits is { } gaits)
        {
            var directions = new List<(float, IReadOnlyList<ClipGait>)>();
            var clips = new List<CharacterViewer.BlendClip>();
            var pitch = new List<(float, float, bool, bool)>();
            bool ok = true;
            foreach (var d in set.Directions)
            {
                var bands = new List<ClipGait>();
                foreach (var band in new[] { d.Walk, d.Run })
                {
                    var gait = gaits.Get(band.Level);
                    if (gait is null || !OrkHasClip(assets, band.Level)) { ok = false; break; }
                    bool hasLow = band.Down is not null && OrkHasClip(assets, band.Down), hasHigh = band.Up is not null && OrkHasClip(assets, band.Up);
                    bands.Add(gait);
                    clips.Add(new CharacterViewer.BlendClip(hasLow ? band.Down : null, hasLow ? gait : null));
                    clips.Add(new CharacterViewer.BlendClip(band.Level, gait));
                    clips.Add(new CharacterViewer.BlendClip(hasHigh ? band.Up : null, hasHigh ? gait : null));
                    pitch.Add((hasLow ? band.DownDegrees : -80f, hasHigh ? band.UpDegrees : 80f, hasLow, hasHigh));
                }
                if (!ok) break;
                directions.Add((d.AngleDegrees, bands));
            }
            if (ok && directions.Count > 0 && OrkHasClip(assets, set.Idle))
            {
                int idle = clips.Count;
                clips.Add(new CharacterViewer.BlendClip(set.Idle, null));
                result = new GaitFamily
                {
                    Set = new CharacterViewer.BlendSet("gait:" + key, clips.ToArray()), Space = new GaitSpace(directions, cyclic: true), Slots = 3,
                    IdleStart = idle, IdleSlots = 1, Weights = new float[clips.Count], Pitch = pitch.ToArray(),
                };
            }
        }
        if (result is null && _orkNoted.Add(key)) { _notes.Add($"ork gait blend: no usable clips or gaits for {set.Family}; the nearest-clip pose shows"); GD.Print($"ork gait blend: no usable clips or gaits for {set.Family}; the nearest-clip pose shows"); }
        else if (result is not null) GD.Print($"ork gait blend: {set.Family}: {set.Directions.Count} directions x walk and run, {result.Set.Clips.Length} clips, idle {set.Idle}");
        _orkFamilies[set] = result;
        return result;
    }

    /// <summary>Once in a while (bots and F1): how many Orks were placed and what the Ork models cost to build.</summary>
    void OrkLog()
    {
        if (_orkShown.Count > 0) OrkCaptureFrame();   // --ork-capture (test aid, NetworkView.OrkActions.cs): nothing runs without it
        if (_orks is null || !(_bot || _showInfo) || _connectedFor - _orkLogAt < 10.0) return;
        _orkLogAt = _connectedFor;
        GD.Print($"t {_connectedFor:F0} s: ork: {_orkBuilds} looks built ({_orkBuildMs:F0} ms of main-thread build work), {_orkPlaced} Ork placements; {_orkNote}; {OrkActionStats()}; {string.Join(", ", _remotes.Where(r => IsOrkCharacter(r.Value)).Select(r => $"P{r.Key} {r.Value.Playing}"))}");
    }

    /// <summary>The F1 line about the Ork.</summary>
    string OrkInfoLine() =>
        "Ork (O7a): the boy's original meshes and clips on its own rig; walk and run = the 2HR aim run (1HM for the slugga) in four directions through the blend space, the aim pitch weighting the " +
        $"aim down / forward / up clips, the additive aim offsets standing, one 1HM sprint; clip choice and blend: interpretation. Reactions, charge, melee, hop, reload, recoil and death clips: O7b / O7c. Loaded: {_orkNote}.";
}
