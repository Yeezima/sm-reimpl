using System;
using System.Diagnostics;
using System.Globalization;
using Godot;

namespace SmClient;

/// <summary>
/// M40-T9: hitch removal (client only, no protocol or simulation change; spec "Frame hitches (M40-T9)", ours).
/// <list type="bullet">
/// <item><b>Spread heavy work.</b> Building a character and starting its weapon rig each cost several ms; joining a match with seven players
/// did all of them in one frame (215 ms measured). <see cref="HeavyLeft"/> is a per-frame budget (<see cref="HeavyBudgetMs"/>): once the first
/// builds of a frame have used it, the remaining remote players' characters and weapon rigs wait for the next frames (a player appears a few
/// frames later; their state is read from the snapshots as before). The own character is never delayed. Deaths are spread the same way
/// (<c>ProcessDeaths</c>).</item>
/// <item><b>JIT.</b> The first run of a code path compiles it on the main thread. <see cref="JitPrewarm"/> compiles the client's methods on a background
/// thread at start (<c>RuntimeHelpers.PrepareMethod</c>), so the first shot, death, reaction or effect no longer waits for the compiler.</item>
/// <item><b>Ragdoll.</b> <see cref="PrewarmRagdollStep"/> builds one hidden ragdoll from the own character a little after joining, lets it take one physics
/// step and frees it, so the first real death finds the physics objects, the corpse's character and the weapon clone paths warm.</item>
/// <item><c>--no-hitch-fixes</c> switches all three off (before / after runs of the same build); <c>--frame-profile</c> is the measuring aid
/// (<see cref="FrameProfile"/>).</item>
/// </list>
/// </summary>
public partial class NetworkView
{
    /// <summary><c>--frame-profile [--frame-profile-warmup s]</c> (test aid): the per-subsystem frame-time profile (<see cref="FrameProfile"/>).</summary>
    bool FrameProfileOn => _frameProfileOn ??= CmdFlag("frame-profile");
    bool? _frameProfileOn;

    FrameProfile NewFrameProfile() =>
        new(_className ?? "client", CmdArg("frame-profile-warmup") is { } w && double.TryParse(w, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s >= 0 ? s : 8.0);

    /// <summary><c>--no-hitch-fixes</c> (test aid): no spreading, no JIT prewarm, no ragdoll prewarm (the behaviour before M40-T9).</summary>
    bool HitchFixes => _hitchFixes ??= !CmdFlag("no-hitch-fixes");
    bool? _hitchFixes;

    // ---- the per-frame budget for heavy first builds ----

    /// <summary>Milliseconds of heavy first builds (a character, a weapon rig, a corpse) after which the rest of the frame's waits for the next frame.</summary>
    public const double HeavyBudgetMs = 6.0;
    long _heavyTicks, _frameStartTicks;
    int _heavyDeferred;
    int _heavyDeferredMax;

    /// <summary>Start of a frame: the budget is full again.</summary>
    void HeavyReset()
    {
        _heavyTicks = 0;
        _frameStartTicks = Stopwatch.GetTimestamp();
        if (_heavyDeferred > _heavyDeferredMax) _heavyDeferredMax = _heavyDeferred;
        _heavyDeferred = 0;
    }

    /// <summary>True while this frame's heavy builds have used less than <see cref="HeavyBudgetMs"/> (always true without the fixes).</summary>
    bool HeavyLeft
    {
        get
        {
            if (!HitchFixes) return true;
            bool left = Stopwatch.GetElapsedTime(0, _heavyTicks).TotalMilliseconds < HeavyBudgetMs;
            if (!left) _heavyDeferred++;
            return left;
        }
    }

    /// <summary>The time of a heavy build that is about to start.</summary>
    static long HeavyStart() => Stopwatch.GetTimestamp();

    /// <summary>A heavy build that started at <paramref name="start"/> is over; its time is charged to this frame's budget.</summary>
    void HeavyEnd(long start) => _heavyTicks += Stopwatch.GetTimestamp() - start;

    /// <summary>One line for the end of a profile run: the most builds deferred in one frame.</summary>
    string HeavyReport() => $"heavy-build budget {HeavyBudgetMs:F0} ms per frame: most deferrals in one frame {_heavyDeferredMax}";

    // ---- the background JIT ----

    JitPrewarm? _jit;

    /// <summary>Starts compiling the client's methods on a background thread (once; not with <c>--no-hitch-fixes</c>).</summary>
    void StartJitPrewarm()
    {
        if (!HitchFixes || _jit is not null || Lean) return;   // perf/lean-bots: a lean bot runs a small part of the client's code
        _jit = JitPrewarm.Start(typeof(NetworkView).Assembly, typeof(SmContent.GameplayTuning).Assembly, typeof(SmNet.GameClient).Assembly,
            typeof(SmSim.World).Assembly, typeof(SmFormats.Havok.HavokCharacterFile).Assembly);
    }

    // ---- the weapon models ----

    readonly System.Collections.Generic.Queue<SmContent.WeaponAttachment> _weaponWarm = new();
    bool _weaponWarmBuilt;
    int _weaponWarmed;

    /// <summary>
    /// <see cref="WeaponAssets.Get"/> reads a weapon's model from the install and builds its meshes on first use (2 to 24 ms each), which a character's first
    /// weapon rig paid on the main thread. The models of every class's default loadout are fetched during the map load (the handshake is waiting for that
    /// anyway, a few hundred ms); the rest of the models any loadout can pick follow one per frame (<see cref="PrewarmWeaponsStep"/>).
    /// </summary>
    void PrewarmWeaponDefaults()
    {
        StartAnimationPrefetch();
        if (!HitchFixes || _weaponAssets is null || _config is null) return;
        var watch = Stopwatch.StartNew();
        BuildWeaponWarmQueue();
        int total = _weaponWarm.Count, defaults = _weaponWarmDefaults;
        while (_weaponWarm.Count > total - defaults && _weaponWarm.Count > 0 && watch.ElapsedMilliseconds < 600) WarmOne();
        GD.Print($"weapon models warmed for the default loadouts: {_weaponWarmed} of {defaults} in {watch.ElapsedMilliseconds} ms; {_weaponWarm.Count} more follow in the first frames");
    }

    int _weaponWarmDefaults, _weaponWarmGap = 4;

    void BuildWeaponWarmQueue()
    {
        if (_weaponWarmBuilt) return;
        _weaponWarmBuilt = true;
        var catalogue = _weaponAssets!.Catalogue;
        var keys = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? wargear)
        {
            if (string.IsNullOrEmpty(wargear)) return;
            if (catalogue.Held(wargear) is { } held && keys.Add(held.VisualPackage + "|" + held.VisualName)) _weaponWarm.Enqueue(held);
            foreach (var worn in catalogue.Worn(wargear)) if (keys.Add(worn.VisualPackage + "|" + worn.VisualName)) _weaponWarm.Enqueue(worn);
        }
        for (int i = 0; i < _config!.Classes.Count; i++)
        {
            var state = SmSim.Loadouts.Choose(_config, new SmSim.PlayerState(0, default, default, 0f, true) { Class = i, Team = _config.Classes[i].Side }, -1, -1, -1);
            int primary = SmSim.Loadouts.WeaponIndex(_config, state, SmSim.WeaponSlot.Ranged), pistol = SmSim.Loadouts.WeaponIndex(_config, state, SmSim.WeaponSlot.Pistol);
            Add(primary >= 0 ? _config.Weapons[primary].Name : null);
            Add(pistol >= 0 ? _config.Weapons[pistol].Name : null);
            Add(MeleeWargear(state));
            Add(SmSim.Loadouts.JumpPack(_config, state)?.Name);
        }
        _weaponWarmDefaults = _weaponWarm.Count;
        foreach (var w in _config.Weapons) Add(w.Name);
        foreach (var c in _config.Classes) foreach (var m in c.MeleeChoices) Add(m.Wargear);
        foreach (var j in _config.JumpPacks) Add(j.Name);
    }

    void WarmOne()
    {
        var attachment = _weaponWarm.Dequeue();
        try { _weaponAssets!.Get(attachment); _weaponWarmed++; }
        catch (Exception ex) when (ex is not (StackOverflowException or AccessViolationException or OutOfMemoryException)) { GD.PrintErr($"weapon prewarm {attachment.VisualName}: {ex.Message}"); }
    }

    /// <summary>One more model per frame while the frame has budget (called every frame).</summary>
    void PrewarmWeaponsStep()
    {
        if (!HitchFixes || _weaponAssets is null || _config is null) return;
        if (!_weaponWarmBuilt) BuildWeaponWarmQueue();
        if (_weaponWarm.Count == 0 || _connectedFor < 1.0 || ++_weaponWarmGap < 4 || !HeavyLeft) return;
        // Only in a frame that has been light so far (a model takes up to 25 ms): the join and the first shots come first.
        if (Stopwatch.GetElapsedTime(_frameStartTicks).TotalMilliseconds > 5.0) { _weaponWarmGap = 3; return; }
        _weaponWarmGap = 0;
        long heavy = HeavyStart();
        WarmOne();
        HeavyEnd(heavy);
    }

    /// <summary>Every frame: the prewarm steps that wait for the join to be over (called from the ragdoll bookkeeping).</summary>
    void PrewarmFrame()
    {
        StartAnimationPrefetch();
        PrewarmRagdollStep();
        PrewarmWeaponsStep();
    }

    // ---- the crosshair ray ----

    /// <summary>
    /// The first frame's crosshair (<c>CenterRayTarget</c>: a weapon-mesh ray and the players' hit shapes) took 26 to 30 ms the first time it ran (one-off
    /// setup in the simulation's hit shapes and collision queries). A few dummy queries during the map load pay it before the first frame.
    /// </summary>
    void PrewarmCollision(SmSim.CollisionWorld world)
    {
        if (!HitchFixes || _config is null) return;
        var watch = Stopwatch.StartNew();
        try
        {
            var up = new System.Numerics.Vector3(0f, 0f, 1.6f);
            world.Weapons.Raycast(up, System.Numerics.Vector3.UnitX, 30f);
            world.Weapons.Raycast(up, -System.Numerics.Vector3.UnitY, 30f);
            SmSim.Combat.HitShapes(up, System.Numerics.Vector3.UnitX, new System.Numerics.Vector3(10f, 0f, 0f), _config, 30f, 0.5f);
            SmSim.Combat.HitShapes(up, System.Numerics.Vector3.UnitX, new System.Numerics.Vector3(0.2f, 0f, 0f), _config, 30f, 1.5f);
        }
        catch (Exception ex) when (ex is not (StackOverflowException or AccessViolationException or OutOfMemoryException)) { GD.PrintErr($"collision prewarm: {ex.GetType().Name}: {ex.Message}"); }
        if (FrameProfileOn || _bot) GD.Print($"collision queries warmed in {watch.Elapsed.TotalMilliseconds:F1} ms");
    }

    // ---- the animations ----

    System.Threading.Tasks.Task<int>? _animationPrefetch;
    readonly System.Threading.CancellationTokenSource _animationCancel = new();

    /// <summary>Decodes every rig animation on a worker thread (<see cref="MarineAssets.PrefetchAnimations"/>), once.</summary>
    void StartAnimationPrefetch()
    {
        if (!HitchFixes || _animationPrefetch is not null || _assets is null || Lean) return;   // perf/lean-bots: clips are decoded when first shown
        _animationPrefetch = _assets.PrefetchAnimations(_animationCancel.Token);
        var watch = Stopwatch.StartNew();
        _animationPrefetch.ContinueWith(t => GD.Print(t.IsFaulted ? $"animation prefetch failed: {t.Exception?.GetBaseException().Message}" : $"animation prefetch: {t.Result} clips decoded in the background in {watch.Elapsed.TotalSeconds:F1} s"));
    }

    // ---- the dormant ragdoll ----

    int _ragdollWarmStage;       // 0 not yet, 1 built (living), 2 done
    int _ragdollWarmFrames;
    Ragdoll? _ragdollWarm;

    /// <summary>
    /// A moment after joining (the join's own work is over): builds one hidden ragdoll from the own character, lets it live for two frames (one physics
    /// step on the map's ragdoll world) and frees it, so no real death builds the first ragdoll. Called every frame from <c>UpdateRagdolls</c>.
    /// </summary>
    /// <summary>The ragdoll definition that fits the own character's rig (Ork O7a: the Ork's own, else the marine's); null when there is none.</summary>
    RagdollDef? WarmRagdollDef() => _local is { } own && IsOrkCharacter(own) && own.Assets is { } orkAssets ? OrkRagdollDefinition(orkAssets) : _ragdollDef;

    void PrewarmRagdollStep()
    {
        if (!HitchFixes || _ragdollWarmStage >= 2 || Lean) return;   // perf/lean-bots: a lean bot has no ragdolls
        if (_ragdollWarmStage == 0)
        {
            if (_connectedFor < 2.0 || _local is null || !IsInstanceValid(_local) || _gameSpace is null || WarmRagdollDef() is null || OwnDead || _client is not { Predicted.Alive: true }) return;
            if (_pendingDeaths.Count > 0 || !HeavyLeft) return;
            var watch = Stopwatch.StartNew();
            long heavy = HeavyStart();
            Ragdoll? rd = null;
            try
            {
                EnsureRagdollWorld();
                rd = new Ragdoll { Name = "RagdollWarm", PlayerId = -1, Visible = false };
                _gameSpace.AddChild(rd);
                rd.Start(WarmRagdollDef()!, _local, Ragdoll.MaxSlots - 1, Vector3.Zero, Vector3.Zero);
                _ragdollWarm = rd;
                _ragdollWarmStage = 1;
                HeavyEnd(heavy);
                if (_bot || FrameProfileOn) GD.Print($"t {_connectedFor:F1} s: ragdoll prewarm built in {watch.Elapsed.TotalMilliseconds:F1} ms");
            }
            catch (Exception ex) when (ex is not (StackOverflowException or AccessViolationException or OutOfMemoryException))
            {
                _ragdollWarmStage = 2;   // a failed prewarm is not tried again; real deaths report their own failures
                _ragdollWarm = null;
                if (rd is not null && IsInstanceValid(rd)) rd.QueueFree();   // whatever was built before the failure
                GD.PrintErr($"ragdoll prewarm failed: {ex.GetType().Name}: {ex.Message}");
            }
            return;
        }
        if (++_ragdollWarmFrames < 3) return;
        if (_ragdollWarm is { } warm && IsInstanceValid(warm)) { warm.Bake(); warm.QueueFree(); }
        _ragdollWarm = null;
        _ragdollWarmStage = 2;
    }
}
