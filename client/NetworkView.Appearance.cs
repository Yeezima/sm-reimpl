using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SmClient.Looks;
using SmContent;
using SmContent.Customization;
using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// Customizer T7: every player in the match is drawn from their resolved appearance (docs/customizer-architecture.md sections 5 and 8). The own look comes from the
/// profile (the Hello's codes, <see cref="OwnLooks"/>), others' from <see cref="GameClient.AppearanceOf"/>; a player without a custom look wears the class's default look
/// (the same resolver, so the customizer's preview and the match agree). Nothing waits on the main thread: the files are read on workers
/// (<see cref="AppearanceAssets.Prefetch"/>), <see cref="BuildScheduler"/> spends at most a few milliseconds a frame on <see cref="AppearanceAssets.TryBuild"/>, a character keeps its
/// model until the wanted one is ready and swaps only while alive (<see cref="SwapRules"/>); corpses keep their model (a ragdoll builds its own character from the model
/// it was shown with). The default looks of every class and the own look are built while the map loads. The rules are in <c>client/Looks/LookLogic.cs</c> (tested).
/// </summary>
public partial class NetworkView
{
    /// <summary>Main-thread milliseconds a frame may spend starting new model builds (one build step is at most two pieces, a few ms).</summary>
    const double LookBudgetMs = 5.0;
    /// <summary>Built looks kept (a look is a list of shared meshes and materials: small); the oldest goes first.</summary>
    const int MaxBuiltLooks = 64;

    sealed record LookRequest(string Key, ResolvedAppearance Look);
    sealed record ResolvedEntry(ResolvedAppearance Look, string KeyReady, string KeyWaiting);

    AppearanceAssets? _looks;
    CustomizationCatalog? _lookCatalog;
    Task<CustomizationCatalog?>? _lookCatalogTask;
    OwnLooks _ownLooks = new(AppearanceCode.Default, AppearanceCode.Default);
    EmblemAvailability? _emblemsHave;
    BuildScheduler? _lookScheduler;
    readonly HitchMeter _hitch = new(), _hitchSteady = new();   // all frames since connecting / frames after the first 8 s (startup loads are not look changes)
    readonly HashSet<EmblemHash> _emblemWaitLogged = new();
    readonly Dictionary<string, MarineAssets> _builtLooks = new();
    readonly Queue<string> _builtOrder = new();
    readonly Dictionary<string, ResolvedAppearance> _wantedLooks = new();
    readonly Dictionary<string, ResolvedAppearance> _warmQueue = new();   // the warm-up's looks that were not built during the map load: built by the frame loop, kept until built or failed
    readonly Dictionary<string, ResolvedAppearance> _aheadLooks = new();   // the looks of a loadout change in flight, built before the respawn that needs them
    readonly Dictionary<string, ResolvedEntry> _resolvedLooks = new();
    // A look that failed to build is left alone for FailedLookSeconds (then tried again), or until an emblem arrives; a slow first read must not leave the placeholder for the session.
    readonly Dictionary<string, double> _failedUntil = new();
    const double FailedLookSeconds = 30.0, GiveUpSeconds = 20.0;
    bool IsFailed(string key)
    {
        if (!_failedUntil.TryGetValue(key, out var until)) return false;
        if (_connectedFor < until) return true;
        _failedUntil.Remove(key);
        return false;
    }
    void Fail(string key) => _failedUntil[key] = _connectedFor + FailedLookSeconds;
    readonly Dictionary<int, ClassCustomization?> _classCustomization = new();
    readonly Dictionary<(int, int, int, int, int, int), (IReadOnlyList<string> Names, string Key)> _carried = new();
    bool _lookWork, _lookCatalogNoted;
    int _looksNotes, _prevSwaps, _swapsThisFrame;
    double _runMs, _prevRunMs;
    readonly Dictionary<string, double> _stepStart = new();
    double _closeupLogAt;
    int _slowFramesLogged;
    int _looksBuilt, _looksSwapped, _looksPlaceholders;
    double _lookBuildMs, _lookLogAt;
    ushort _lastSetupSequence;
    bool _setupSent;
    double _setupSentAt;

    /// <summary><c>--look-churn &lt;seconds&gt;</c> (test aid): every player, the own one too, gets a new random look every so many seconds (client only; measures the frame times of look changes).</summary>
    double LookChurn => _lookChurn ??= double.TryParse(CmdArg("look-churn"), System.Globalization.CultureInfo.InvariantCulture, out var s) && s > 0 ? s : 0;
    double? _lookChurn;
    readonly Dictionary<int, (long Epoch, AppearanceCode Code)> _churnCodes = new();
    readonly Random _churnRandom = new(20261028);

    /// <summary>The catalogue is ready (loaded on a worker, or taken from the front end's services) and the simulation's classes are known.</summary>
    bool LooksReady
    {
        get
        {
            if (_lookCatalog is null && _lookCatalogTask is { IsCompleted: true } task)
            {
                _lookCatalog = task.IsCompletedSuccessfully ? task.Result : null;
                if (_lookCatalog is null && !_lookCatalogNoted) { _lookCatalogNoted = true; _notes.Add("looks: the customization catalogue could not be read; players are drawn in the default marine (Chaos tinted)"); GD.Print("  note: looks unavailable"); }
            }
            return _looks is not null && _lookCatalog is not null && _config is not null;
        }
    }

    /// <summary>First thing after the shared marine assets exist: the shared look cache, the emblem source and the catalogue (the front end's when it has one, else read on a worker).</summary>
    void StartLooks()
    {
        var notes = new List<string>();
        _looks = AppearanceAssets.Shared(_gameDir, notes);
        foreach (var n in notes) _notes.Add(n);
        _looks.EmblemSource = EmblemBytes;
        _looksNotes = _looks.Notes.Count;
        _lookScheduler = new BuildScheduler(() => Time.GetTicksUsec() / 1000.0, LookBudgetMs);
        _emblemsHave = new EmblemAvailability(h => _client?.Emblem(h) is not null, () => Time.GetTicksMsec() / 1000.0);
        if (SmClient.FrontEnd.FrontEndLaunch.Current?.Services.Catalog is { } shared) _lookCatalog = shared;
        else
        {
            string gameDir = _gameDir;
            _lookCatalogTask = Task.Run<CustomizationCatalog?>(() =>
            {
                try { var n = new List<string>(); return CustomizationCatalog.Load(gameDir, n, MarineAssets.Schemes(gameDir, n)); }
                catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException)) { GD.PrintErr("looks: catalogue: " + ex.Message); return null; }
            });
        }
    }

    byte[]? EmblemBytes(EmblemHash hash) => _client?.Emblem(hash);

    /// <summary>The own look is the Hello's (the front end's profile, <c>--profile</c>); a plain <c>--connect</c> wears the defaults.</summary>
    void SetOwnLooks(ClientOptions options)
    {
        _ownLooks = new OwnLooks(options.SpaceMarineAppearance, options.ChaosAppearance);
        SetOwnOrk(options);   // Ork O7a: the own Ork look and class
    }

    // ---- what a player wears ----

    ClassCustomization? CustomizationOf(int playerClass)
    {
        if (_classCustomization.TryGetValue(playerClass, out var known)) return known;
        ClassCustomization? found = null;
        if (_lookCatalog is { } catalog && _config is not null && playerClass >= 0 && playerClass < _config.Classes.Count)
        {
            var info = _config.Classes[playerClass];
            string race = info.Side == Team.Chaos ? CustomizationCatalog.Chaos : CustomizationCatalog.SpaceMarine;
            found = catalog.Class(info.Name) is { } byName && byName.Race == race ? byName
                : catalog.Classes.FirstOrDefault(c => c.Race == race && c.Role.Equals(info.Role, StringComparison.OrdinalIgnoreCase));
            if (found is null) _notes.Add($"looks: class '{info.Name}' has no customization entry; its players use the default marine");
        }
        return _classCustomization[playerClass] = found;
    }

    /// <summary>The wargear and perk names a player carries (the resolver's class overrides ask for some: the Devastator's weapon pack, the Havoc's Iron Halo).</summary>
    (IReadOnlyList<string> Names, string Key) CarriedBy(PlayerState s)
    {
        var id = (s.Class, s.Primary, s.Pistol, s.MeleeChoice, s.Perk1, s.Perk2);
        if (_carried.TryGetValue(id, out var known)) return known;
        var names = new List<string>();
        foreach (var slot in new[] { WeaponSlot.Ranged, WeaponSlot.Pistol })
            if (Loadouts.WeaponIndex(_config!, s, slot) is var w and >= 0 && w < _config!.Weapons.Count) names.Add(_config.Weapons[w].Name);
        if (MeleeWargear(s) is { } melee) names.Add(melee);
        for (int slot = 1; slot <= 2; slot++)
            if (Loadouts.PerkIndex(_config!, s, slot) is var p and >= 0 && p < _config!.Perks.Count) names.Add(_config.Perks[p].Name);
        if (_carried.Count > 512) _carried.Clear();
        return _carried[id] = (names, LookKeys.CarriedKey(names));
    }

    AppearanceCode CodeFor(PlayerState s, bool own, Team race)
    {
        if (LookChurn > 0) return ChurnCode(s.PlayerId, race);
        if (own) return _ownLooks.Active(race);
        return _client?.AppearanceOf(s.PlayerId) is { } a && a.Race == Races.OfSide(race) ? a.Code : AppearanceCode.Default;   // protocol 28: a Race (an Ork's look is O7's)
    }

    /// <summary>What was resolved for a player last frame: when the class, loadout and code are the same nothing is looked up, formatted or allocated again.</summary>
    sealed record PlayerLook(int Class, (int, int, int, int, int, int) Loadout, AppearanceCode Code, ResolvedEntry Entry, LookRequest Ready, LookRequest Waiting);
    readonly Dictionary<int, PlayerLook> _playerLooks = new();
    readonly HashSet<string> _failedInputs = new();

    /// <summary>
    /// The look <paramref name="s"/> should wear with <paramref name="code"/>, resolved once per input (null: the catalogue is not ready, the class unknown or the resolver
    /// refused it: the player keeps the placeholder or its model). A real player (id above 0) is cached by class, loadout and code, so a frame costs two comparisons.
    /// </summary>
    LookRequest? LookFor(PlayerState s, AppearanceCode code)
    {
        if (!LooksReady || CustomizationOf(s.Class) is not { } cls) return null;
        var loadout = (s.Class, s.Primary, s.Pistol, s.MeleeChoice, s.Perk1, s.Perk2);
        PlayerLook? cached = s.PlayerId > 0 && _playerLooks.TryGetValue(s.PlayerId, out var c) && c.Class == s.Class && c.Loadout == loadout && c.Code == code ? c : null;
        if (cached is null)
        {
            var (names, carriedKey) = CarriedBy(s);
            string input = LookKeys.InputOf(cls.Race, cls.Key, code, carriedKey);
            if (_failedInputs.Contains(input)) return null;
            if (!_resolvedLooks.TryGetValue(input, out var entry))
            {
                try
                {
                    var look = AppearanceResolver.Resolve(code, cls.Key, _lookCatalog!, _looks!.ResolveOptions(names));
                    entry = new ResolvedEntry(look, LookKeys.Of(look, true), LookKeys.Of(look, false));
                }
                catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
                {
                    // A code the resolver refuses (a hostile peer's, or a catalogue mismatch) must not stop the frame: the placeholder stays, said once.
                    _failedInputs.Add(input);
                    _notes.Add($"looks: P{s.PlayerId} ({cls.Key}): the appearance could not be resolved ({ex.GetType().Name}: {ex.Message}); the default marine stays");
                    GD.PrintErr($"looks: P{s.PlayerId} ({cls.Key}): the appearance could not be resolved: {ex.Message}");
                    return null;
                }
                if (_resolvedLooks.Count > 1024) _resolvedLooks.Clear();
                _resolvedLooks[input] = entry;
            }
            cached = new PlayerLook(s.Class, loadout, code, entry, new LookRequest(entry.KeyReady, entry.Look), new LookRequest(entry.KeyWaiting, entry.Look));
            if (s.PlayerId > 0) _playerLooks[s.PlayerId] = cached;
        }
        bool emblems = true;
        foreach (var decal in new[] { cached.Entry.Look.LeftDecal, cached.Entry.Look.RightDecal })
            if (decal.Kind == DecalKind.Emblem && !decal.Emblem.IsNone && _emblemsHave?.Has(decal.Emblem) != true)
            {
                emblems = false;
                if (_bot && _emblemWaitLogged.Add(decal.Emblem)) GD.Print($"t {_connectedFor:F1} s: P{s.PlayerId} wears emblem {decal.Emblem.ToHex()[..8]}; the fallback badge shows until its bytes arrive");
            }
        return emblems ? cached.Ready : cached.Waiting;
    }

    LookRequest? WantedLook(PlayerState s, bool own)
    {
        if (!LooksReady || CustomizationOf(s.Class) is not { } cls) return null;
        return LookFor(s, CodeFor(s, own, cls.Race == CustomizationCatalog.Chaos ? Team.Chaos : Team.SpaceMarines));
    }

    /// <summary>
    /// Keeps one character per player wearing the model its side, class and look call for. Never waits: a new character gets the model at once if it is built, else the
    /// default marine (the placeholder; a Chaos player is tinted) until it is; an existing one keeps its model until the wanted one is ready and the player is alive.
    /// </summary>
    CharacterViewer EnsureModel(CharacterViewer? current, int playerId, bool local, PlayerState state)
    {
        if (IsOrkPlayer(state)) return EnsureOrkModel(current, playerId, local, state);   // Ork O7a (NetworkView.Ork.cs): the boy's own rig and looks
        string? currentKey = current is null ? null : current.GetMeta("look_key").AsString();
        var want = WantedLook(state, local);
        // Ork O7a: a race change at the respawn (Ork to marine): the Ork rig must not stand in for a marine whose look is not built; the placeholder at once (SwapRules.DropWrongRig).
        if (current is not null && (want is null || !_builtLooks.ContainsKey(want.Key)) && SwapRules.DropWrongRig(IsOrkCharacter(current), wantsOrk: false, currentKey, state.Alive, _pendingDeaths.Any(d => d.Id == playerId)))
        {
            current.QueueFree();
            _looksPlaceholders++;
            if (want is not null && !IsFailed(want.Key)) { _wantedLooks[want.Key] = want.Look; _lookScheduler!.Want(want.Key, local ? 0 : 1); }
            return Spawn(playerId, local, _assets!, LookKeys.Placeholder);
        }
        if (want is null) return current ?? Spawn(playerId, local, _assets!, LookKeys.Placeholder);
        _builtLooks.TryGetValue(want.Key, out var built);
        if (built is null && currentKey != want.Key && !IsFailed(want.Key))
        {
            _wantedLooks[want.Key] = want.Look;
            _lookScheduler!.Want(want.Key, local ? 0 : 1);
        }
        bool deathPending = _pendingDeaths.Any(d => d.Id == playerId);
        switch (SwapRules.Decide(currentKey, want.Key, built is not null, state.Alive, deathPending))
        {
            case LookAction.Create:
                _lookWork = true;
                if (built is null) _looksPlaceholders++;
                return Spawn(playerId, local, built ?? _assets!, built is null ? LookKeys.Placeholder : want.Key);
            case LookAction.Swap:
                _lookWork = true; _looksSwapped++; _swapsThisFrame++;
                if (_bot || local || CmdFlag("look-log")) GD.Print($"t {_connectedFor:F1} s: P{playerId} changes its look ({(currentKey == LookKeys.Placeholder ? "placeholder" : "model")} -> {want.Look.ClassKey}{(want.Look.PredefinedSet is { } set ? ", set " + set : "")}; head {want.Look.Piece("head")?.Name}, head paint {want.Look.Paint.GetValueOrDefault("head")?.Primary?.Name}, left pad {want.Look.LeftDecal.Kind}, right pad {want.Look.RightDecal.Kind})");
                // The same character in another look: only its armour pieces are replaced (CharacterViewer.SetLook), so the skeleton, the pose, the weapons and every
                // reference to the character stay; a character that cannot take it (another rig) is replaced.
                if (current!.SetLook(built!))
                {
                    current.SetMeta("look_key", want.Key);
                    // SetLook dropped the placeholder tint; the side look is worked out again for the real model (ApplyTeamLook runs after this in the same frame).
                    if (current.HasMeta("team_look")) current.RemoveMeta("team_look");
                    return current;
                }
                current.QueueFree();
                return Spawn(playerId, local, built!, want.Key);
            default:
                return current!;
        }
    }

    // ---- per frame ----

    /// <summary>Start of the connected frame: re-asserts the emblem source (the picker's preview borrows the shared assets), notes arrivals and the acknowledged setup, promotes the own pending look at the respawn.</summary>
    void LooksBeginFrame(PlayerState me)
    {
        if (_looks is null || _client is null) return;
        _looks.EmblemSource = EmblemBytes;
        var changes = _client.TakeAppearanceChanges();
        if (changes.Count > 0 && (_bot || LookChurn > 0)) GD.Print($"t {_connectedFor:F1} s: id {_client.PlayerId} sees new appearances of {string.Join(", ", changes.Select(c => "P" + c))}");
        foreach (var hash in _client.TakeEmblemArrivals()) { _emblemsHave?.Arrived(hash); _failedUntil.Clear(); _lookWork = true; GD.Print($"t {_connectedFor:F1} s: id {_client.PlayerId} received emblem {hash.ToHex()[..8]}"); }
        if (_setupSent && _client.LastSetupAck is { } ack && ack.Sequence == _lastSetupSequence && !_client.SetupPending && !_ownLooks.Acked && _ownLooks.Pending is not null)
            _ownLooks.Acknowledged(ack.AppearanceVersion, (ack.Flags & SetupFlags.EmblemRejected) != 0);
        var ownRow = _client.Scoreboard?.Players.Where(r => r.PlayerId == _client.PlayerId).Select(r => (ScoreEntry?)r).FirstOrDefault();
        if (_ownLooks.Update(me.Alive, ownRow?.AppearanceVersion ?? 0, ownRow is not null)) { _lookWork = true; GD.Print($"t {_connectedFor:F1} s: id {_client.PlayerId} wears the new look from this life"); }
    }

    /// <summary>End of the connected frame (after the players were placed): builds the wanted looks within the budget, keeps the hitch statistics.</summary>
    void LooksEndFrame(double delta)
    {
        if (_lookScheduler is null) return;
        LookCloseupFrame();
        WarmLooksLater();
        if (_playerLooks.Count > _remotes.Count + 2) foreach (var id in _playerLooks.Keys.Where(k => !_remotes.ContainsKey(k) && k != _client?.PlayerId).ToList()) _playerLooks.Remove(id);
        // The warm queue stays until each look is built or failed; the loadout change's looks also until the own pending change is gone (promoted or replaced).
        foreach (var (queue, priority) in new[] { (_warmQueue, 2), (_aheadLooks, 1) })
            foreach (var (key, look) in queue.ToList())
            {
                if (_builtLooks.ContainsKey(key) || IsFailed(key)) { queue.Remove(key); continue; }
                _wantedLooks[key] = look;
                _lookScheduler.Want(key, priority);
            }
        int before = _lookScheduler.Attempts;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        _lookScheduler.Run(StepLook);
        // What the scheduler no longer wants (the player changed again, left, or the look was built) is not kept here either.
        foreach (var key in _wantedLooks.Keys.Where(k => !_lookScheduler.IsPending(k)).ToList()) { _wantedLooks.Remove(key); _stepStart.Remove(key); }
        if (_ownLooks.Pending is null && _aheadLooks.Count > 0) _aheadLooks.Clear();
        AssertNoStrayTint();
        _runMs = watch.Elapsed.TotalMilliseconds;
        if (_lookScheduler.Attempts != before) { _lookWork = true; _lookBuildMs += _runMs; }
        if (delta * 1000.0 > HitchMeter.LimitMs && _slowFramesLogged++ < 12 && (_bot || _showInfo || LookChurn > 0))
            GD.Print($"t {_connectedFor:F1} s: slow frame {delta * 1000.0:F0} ms (look work in the frame before: {_lookWork}, its build step {_prevRunMs:F1} ms, {_prevSwaps} swaps; {_lookScheduler.Pending} looks pending; {_builtLooks.Count} cached)");
        _prevRunMs = _runMs; _prevSwaps = _swapsThisFrame; _swapsThisFrame = 0;
        _hitch.Add(delta * 1000.0, _lookWork);
        if (_connectedFor > 8.0) _hitchSteady.Add(delta * 1000.0, _lookWork);
        _lookWork = false;
        if ((_bot || _showInfo || LookChurn > 0) && _connectedFor - _lookLogAt >= 10.0)
        {
            _lookLogAt = _connectedFor;
            GD.Print($"t {_connectedFor:F0} s: looks: {LookStats()}");
        }
    }

    string LookStats() =>
        $"built {_looksBuilt} ({_lookBuildMs:F0} ms of main-thread build work), cached {_builtLooks.Count}, swaps {_looksSwapped}, placeholders {_looksPlaceholders}, failed {_failedUntil.Count}, pending {_lookScheduler?.Pending ?? 0}, " +
        $"steps deferred by the budget {_lookScheduler?.Deferred ?? 0}; frames since connecting: {_hitch.Summary()}; after 8 s: {_hitchSteady.Summary()}";

    /// <summary>One try at a wanted look: true when finished (built, or given up after an error).</summary>
    bool StepLook(string key)
    {
        if (!_wantedLooks.TryGetValue(key, out var look)) return true;
        try
        {
            if (_looks!.TryBuild(look, 1) is not { } built)
            {
                // A package that cannot be read keeps TryBuild at null: give up after GiveUpSeconds of trying, once, keep the old model, and try again later (Fail expires).
                if (!_stepStart.TryGetValue(key, out var since)) _stepStart[key] = since = _connectedFor;
                if (_connectedFor - since < GiveUpSeconds) return false;
                _stepStart.Remove(key);
                Fail(key);
                _wantedLooks.Remove(key);
                _notes.Add($"looks: {look.ClassKey} could not be built in {GiveUpSeconds:F0} s (a package is unreadable?); the old model stays, tried again in {FailedLookSeconds:F0} s");
                GD.PrintErr($"looks: {look.ClassKey} could not be built in {GiveUpSeconds:F0} s; the old model stays");
                return true;
            }
            _stepStart.Remove(key);
            StoreBuilt(key, built);
            if (_bot || CmdFlag("look-log"))
                foreach (var decal in new[] { look.LeftDecal, look.RightDecal })
                    if (decal.Kind == DecalKind.Emblem && !decal.Emblem.IsNone)
                        GD.Print($"t {_connectedFor:F1} s: built {look.ClassKey} with emblem {decal.Emblem.ToHex()[..8]}: texture {(_looks.Pieces.Materials.CachedEmblem(decal.Emblem) is null ? "NOT cached (badge shown)" : "cached")}; key ends {(key.EndsWith("|e") ? "ready" : "waiting")}; left {look.LeftDecal.Kind} '{look.LeftDecal.Badge?.Texture}' on {look.Piece("leftshoulder")?.Name}, right {look.RightDecal.Kind} on {look.Piece("rightshoulder")?.Name}");
            for (; _looksNotes < _looks.Notes.Count; _looksNotes++) GD.Print("  look note: " + _looks.Notes[_looksNotes]);
            _wantedLooks.Remove(key);
            return true;
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            Fail(key);
            _wantedLooks.Remove(key);
            _notes.Add($"looks: {look.ClassKey} could not be built: {ex.GetType().Name}: {ex.Message}; the placeholder stays");
            GD.PrintErr($"looks: {look.ClassKey} could not be built: {ex.GetType().Name}: {ex.Message}");
            return true;
        }
    }

    void StoreBuilt(string key, MarineAssets built)
    {
        if (_builtLooks.TryAdd(key, built)) { _builtOrder.Enqueue(key); _looksBuilt++; }
        else _builtLooks[key] = built;
        while (_builtLooks.Count > MaxBuiltLooks && _builtOrder.TryDequeue(out var oldest)) _builtLooks.Remove(oldest);   // characters still wearing it keep their reference
    }

    /// <summary>
    /// Logged assertion (customizer T7 review): the red placeholder tint belongs to the default marine only. A character wearing a built look that still has an overlay means a swap
    /// left it behind. Counted and reported once per player.
    /// </summary>
    void AssertNoStrayTint()
    {
        void Check(int id, CharacterViewer? c)
        {
            if (c is null || !IsInstanceValid(c) || c.Overlay is null || ReferenceEquals(c.Assets, _assets) || !_strayTintLogged.Add(id)) return;
            _strayTints++;
            GD.PrintErr($"looks: P{id} wears a built look but still has the placeholder tint (swap bug)");
        }
        if (_client is not null) Check(_client.PlayerId, _local);
        foreach (var (id, c) in _remotes) Check(id, c);
    }

    readonly HashSet<int> _strayTintLogged = new();
    int _strayTints;

    /// <summary>Leaving the view: the shared assets must not keep asking this client for emblem bytes.</summary>
    void LooksExit()
    {
        if (_looks is not null) _looks.EmblemSource = null;   // unconditionally: a closure that still points at this client or at the customizer's services would pin them
    }

    // ---- warm-up (map load) ----

    /// <summary>
    /// While the map loads (the connection is going through its handshake anyway): the default look of every class and the own look for each side, read in parallel and built,
    /// so the first Chaos player of a match does not stall a frame (this replaces the old preload of the three Chaos armour sets).
    /// </summary>
    void WarmLooks()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        // The handshake is waiting for this (the connection times out after 5 s): wait for the catalogue at most WarmCatalogueWaitMs, build at most WarmBuildMs, and let the rest
        // be done by the frame loop's budget (the placeholder covers the gap).
        if (_lookCatalogTask is { } task && !task.IsCompleted) task.Wait(WarmCatalogueWaitMs);
        if (!LooksReady)
        {
            _warmLater = _lookCatalogTask is { IsCompleted: false };
            GD.Print($"looks: warm-up {(_warmLater ? "postponed: the catalogue is still loading" : "skipped: the catalogue is not available")} after {watch.ElapsedMilliseconds} ms");
            return;
        }
        long catalogueMs = watch.ElapsedMilliseconds;
        var wanted = WarmSet();
        foreach (var look in wanted.Values) _looks!.Prefetch(look);
        int built = 0, left = 0;
        foreach (var (key, look) in wanted)
        {
            if (_builtLooks.ContainsKey(key)) { built++; continue; }
            if (watch.ElapsedMilliseconds > WarmBuildMs) { _warmQueue[key] = look; left++; continue; }
            try { StoreBuilt(key, _looks!.Build(look)); built++; }
            catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
            {
                Fail(key);
                _notes.Add($"looks: warm-up of {look.ClassKey}: {ex.GetType().Name}: {ex.Message}");
                GD.PrintErr($"looks: warm-up of {look.ClassKey}: {ex.Message}");
            }
        }
        GD.Print($"looks warmed: {built} of {wanted.Count} (the default and the own look of {_config!.Classes.Count} classes) in {watch.ElapsedMilliseconds} ms (catalogue wait {catalogueMs} ms)" + (left > 0 ? $"; {left} left to the frame budget" : ""));
    }

    /// <summary>The looks worth having ready: the default look of every class and the own look for each side.</summary>
    Dictionary<string, ResolvedAppearance> WarmSet()
    {
        var wanted = new Dictionary<string, ResolvedAppearance>();
        for (int i = 0; i < _config!.Classes.Count; i++)
        {
            var state = Loadouts.Choose(_config, new PlayerState(0, default, default, 0f, true) { Class = i, Team = _config.Classes[i].Side }, -1, -1, -1);
            var race = _config.Classes[i].Side == Team.Chaos ? Team.Chaos : Team.SpaceMarines;
            foreach (var code in new[] { AppearanceCode.Default, _ownLooks.Active(race) })
                if (LookFor(state, code) is { } look) wanted.TryAdd(look.Key, look.Look);
        }
        return wanted;
    }

    const int WarmCatalogueWaitMs = 1500, WarmBuildMs = 2000;
    bool _warmLater;

    /// <summary>A warm-up that had to wait for the catalogue: once it is ready the looks are prefetched and built by the frame loop (own look first).</summary>
    void WarmLooksLater()
    {
        if (!_warmLater || !LooksReady) return;
        _warmLater = false;
        foreach (var (key, look) in WarmSet()) { _looks!.Prefetch(look); if (!_builtLooks.ContainsKey(key)) _warmQueue[key] = look; }
        GD.Print($"t {_connectedFor:F1} s: looks: the postponed warm-up is queued ({_warmQueue.Count} looks)");
    }

    /// <summary>A loadout change in flight (the picker's Deploy): the look the player will wear from the next life is read and built now, so the respawn swaps at once.</summary>
    void PrebuildLooks(LoadoutChoice choice, AppearanceCode spaceMarine, AppearanceCode chaos)
    {
        if (!LooksReady || _client is null || choice.Class < 0 || choice.Class >= _config!.Classes.Count) return;
        var state = Loadouts.Choose(_config, new PlayerState(0, default, default, 0f, true) { Class = choice.Class, Team = _config.Classes[choice.Class].Side },
            choice.Primary, choice.Pistol, choice.Melee, choice.Equipment, choice.Perk1, choice.Perk2);
        var race = _config.Classes[choice.Class].Side == Team.Chaos ? Team.Chaos : Team.SpaceMarines;
        if (LookFor(state, race == Team.Chaos ? chaos : spaceMarine) is not { } next) return;
        _looks!.Prefetch(next.Look);
        _aheadLooks.Clear();   // an older change in flight is replaced
        if (!_builtLooks.ContainsKey(next.Key)) _aheadLooks[next.Key] = next.Look;
    }

    // ---- test aid: a close-up camera on the nearest teammate ----

    /// <summary>
    /// <c>--look-closeup left|right|front|back</c> (with <c>--spectate cam:0,0,0,0,0,1</c>, whose fixed view this replaces every frame): the camera looks at the nearest living
    /// teammate's shoulder pad (or the front or back) from about a metre and a half, to see badges and emblems in a screenshot.
    /// </summary>
    void LookCloseupFrame()
    {
        if (_client is null || _gameSpace is null || CmdArg("look-closeup") is not { } side) return;
        var me = _client.Predicted;
        PlayerState? best = null;
        float bestDistance = float.MaxValue;
        foreach (var o in _lastOthers)
        {
            if (!o.Alive || o.Team != me.Team) continue;
            float d = System.Numerics.Vector3.DistanceSquared(o.Position, me.Position);
            if (d < bestDistance) { bestDistance = d; best = o; }
        }
        if (best is not { } t) return;
        var p = new Vector3(t.Position.X, t.Position.Y, t.Position.Z);
        var forward = new Vector3(-MathF.Sin(t.Yaw), MathF.Cos(t.Yaw), 0f);   // yaw 0 faces +Y; Z is up
        var left = new Vector3(-MathF.Cos(t.Yaw), -MathF.Sin(t.Yaw), 0f);
        var chest = new Vector3(0, 0, 1.45f);
        (Vector3 eye, Vector3 target) = side.ToLowerInvariant() switch
        {
            "left" => (p + left * 1.6f + forward * 0.2f + chest, p + left * 0.3f + chest),
            "right" => (p - left * 1.6f + forward * 0.2f + chest, p - left * 0.3f + chest),
            "padl" => (p + forward * 1.0f + left * 0.9f + new Vector3(0, 0, 1.75f), p + left * 0.38f + new Vector3(0, 0, 1.58f)),
            "padr" => (p + forward * 1.0f - left * 0.9f + new Vector3(0, 0, 1.75f), p - left * 0.38f + new Vector3(0, 0, 1.58f)),
            "back" => (p - forward * 2.0f + new Vector3(0, 0, 1.4f), p + new Vector3(0, 0, 1.3f)),
            _ => (p + forward * 2.0f + new Vector3(0, 0, 1.4f), p + new Vector3(0, 0, 1.3f)),
        };
        _spectateFixedParsed = true;
        _spectateFixed = (_gameSpace.Transform * eye, _gameSpace.Transform * target);
        if (_connectedFor - _closeupLogAt > 3.0) { _closeupLogAt = _connectedFor; GD.Print($"close-up on P{t.PlayerId} ({side}): yaw {t.Yaw:F2}, at {p.X:F1},{p.Y:F1},{p.Z:F1}, eye {eye.X:F1},{eye.Y:F1},{eye.Z:F1}"); }
    }

    // ---- the test aid: a new random look for everyone every so often ----

    AppearanceCode ChurnCode(int playerId, Team race)
    {
        long epoch = (long)Math.Floor((_connectedFor + playerId * 0.37) / LookChurn);
        if (_churnCodes.TryGetValue(playerId, out var known) && known.Epoch == epoch) return known.Code;
        var limits = ProfileWire.LimitsFor(_lookCatalog!.Race(race == Team.Chaos ? CustomizationCatalog.Chaos : CustomizationCatalog.SpaceMarine)!, _lookCatalog);
        var code = AppearanceCode.Default;
        for (int slot = 0; slot < AppearanceCode.Slots; slot++)
        {
            int pieces = slot < limits.PiecesPerSlot.Count ? limits.PiecesPerSlot[slot] : 0;
            if (pieces > 0) code = code.WithPiece((ArmourSlot)slot, (byte)_churnRandom.Next(pieces));
            code = code.WithPalette((ArmourSlot)slot, (byte)_churnRandom.Next(limits.Palette), (byte)_churnRandom.Next(limits.Palette), (byte)_churnRandom.Next(limits.Palette));
            code = code.WithPattern((ArmourSlot)slot, _churnRandom.Next(2) == 0);
        }
        code = code.WithDecals((byte)_churnRandom.Next(Math.Max(1, limits.Badges)), AppearanceCode.DecalNone);
        _churnCodes[playerId] = (epoch, code);
        return code;
    }
}
