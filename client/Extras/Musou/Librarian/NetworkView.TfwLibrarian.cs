using System;
using System.Collections.Generic;
using Godot;
using SmNet.Extras.Musou;
using SmSim;
using SmSim.Extras.Musou;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// The Librarian on the client (Musou Mode only, the war and the 5-wave run alike; tag 16, <see cref="LibrarianWire"/>; docs/musou-expansion.md "Librarian"). The server decides everything: this draws it.
/// <list type="bullet">
/// <item>Chain lightning (pass 3, librarian-lightning 2026-11-10): every frame, each casting Librarian's links are drawn by <see cref="LibrarianBolts"/> and his character takes the cast pose
/// (<see cref="LibrarianCastState"/>, <c>CharacterViewer.LibrarianCast.cs</c>). The own Librarian's chain is foreseen on the client (<see cref="LibrarianPrediction"/>) from the frame Fire is pressed, so the
/// bolt reaches its target at once (owner: ".1 second max between clicking left mouse button and the chain lighting hitting its target"); everyone else's comes from the server, which sends a cast's start at
/// once and its links 20 times a second.</item>
/// <item>Psychic Shockwave: played when the caster's cast count changes: the charged plasma cannon's blast with the Weirdboy's shield-break, recoloured (original effects, their use and colour ours), and the
/// 60 m ring (<see cref="LibrarianShockwaveRing"/>). The Warp Pull was removed (owner 2026-11-10).</item>
/// <item>Sounds (<see cref="LibrarianSounds"/>, original events): the crackle loop while the lightning is held and its release tail, a zap per newly chained target (at most 3 a section, 8 a second in
/// all: <see cref="LibrarianZapBudget"/>), the Shockwave's cast, the overheat vent when the stress locks every power.</item>
/// <item>The gauge (<see cref="LibrarianHud"/>) for the own Librarian, in the weapon box's place and style (he carries no gun).</item>
/// </list>
/// Restore switches: <c>--old-librarian-lightning</c> (the bolts as before 2026-11-10, drawn on each section, no foreseen chain), <c>--no-librarian-predict</c> (the new bolts, the server's links only),
/// <c>--old-librarian-cast</c> (no cast pose). Test aid <c>--tfw-librarian</c>: once in the Stormbird and alive, the client takes the Librarian's class itself (the Esc menu row's own path, <see cref="PickLibrarian"/>).
/// </summary>
public partial class NetworkView
{
    static readonly Color PsychicBlue = new(0.55f, 0.75f, 1f);
    static readonly bool OldLibrarianLightning = OS.GetCmdlineUserArgs().Contains("--old-librarian-lightning");
    static readonly bool NoLibrarianPredict = OldLibrarianLightning || OS.GetCmdlineUserArgs().Contains("--no-librarian-predict");
    LibrarianHud? _librarianHud;
    IReadOnlyList<LibrarianView>? _librarianShown;
    readonly Dictionary<ushort, (byte Pull, byte Shockwave)> _librarianCasts = new();
    /// <summary>The match's Librarian restore switches (<see cref="LibrarianVariant"/>, from the same environment variable as the server's).</summary>
    LibrarianVariant LibrarianSwitches => _config?.LibrarianVariant ?? default;

    /// <summary>What the client last heard of a Librarian for his sounds.</summary>
    sealed class LibrarianSoundState
    {
        public readonly SoundEmitter Emitter = new() { Label = "librarian" };
        public bool Casting, Locked;
        public IReadOnlyList<LibrarianLink> Links = [];
        public bool Seen;
    }

    /// <summary>A Librarian's cast as his character shows it: the pose's state (handed to the character), its eased weight, when the cast began, the shudder's spike.</summary>
    sealed class LibrarianCastTrack
    {
        public readonly LibrarianCastState State = new();
        public float Weight, Spike;
        public double StartedAt = double.NegativeInfinity;
        public bool Was;
        public int Links;
        public Vector3 Target;   // game space
    }

    readonly Dictionary<ushort, LibrarianSoundState> _librarianSounds = new();
    readonly Dictionary<ushort, LibrarianCastTrack> _librarianCastTracks = new();
    readonly LibrarianZapBudget _librarianZaps = new();
    readonly List<ushort> _librarianGone = new();
    readonly List<LibrarianLink> _librarianForeseen = new(), _librarianForeseenHeard = new();
    LibrarianCandidate[] _librarianCandidates = new LibrarianCandidate[256];
    bool[] _librarianStruck = new bool[256];
    Func<Num.Vector3, Num.Vector3, bool>? _librarianBlocked;
    int _librarianZapsPlayed;
    bool _librarianPicked;
    /// <summary>Frames the own chain was foreseen, and the time from the press to the first drawn bolt (test aids: the LIBLAT line).</summary>
    int _librarianForeseenFrames;
    double _librarianPressAt = -1, _librarianFirstBoltMs = -1;
    bool _librarianServerHeard;
    int _librarianFramesLogged;
    bool LibrarianTestPick => _librarianTestPick ??= CmdFlag("tfw-librarian");
    bool? _librarianTestPick;

    void TfwLibrarianFrame(PlayerState me, IReadOnlyList<PlayerState> others, double delta)
    {
        LibrarianClock.Advance(delta);   // the effects' frame clock (LibrarianBolts.cs)
        if (_librarianFramesLogged is > 0 and < 4)
        {
            _librarianFramesLogged++;
            if (_bot || LibrarianDemoOn || LibrarianClipDir is not null) GD.Print($"LIBLAT frame {_librarianFramesLogged - 1} after the press took {delta * 1000.0:F0} ms");
        }
        LibrarianDemoFrame();   // NetworkView.LibrarianDemo.cs: the capture aid, nothing without --librarian-demo
        LibrarianClipFrame();   // NetworkView.LibrarianClip.cs: the clip script, nothing without --librarian-clip
        LibrarianDemoAimFrame();
        if (LibrarianTestPick && !_librarianPicked && _connectedFor > 3.0 && LibrarianOffered(me, out _)) _librarianPicked = PickLibrarian("--tfw-librarian");
        var views = _tfwState.Librarians;
        ushort mine = (ushort)(_client?.PlayerId ?? 0);
        bool fresh = !ReferenceEquals(views, _librarianShown);
        _librarianShown = views;
        LibrarianView? own = null;
        double now = LibrarianClock.Now;
        foreach (var s in _librarianSounds.Values) s.Seen = false;
        foreach (var v in views)
        {
            bool isOwn = v.Player == mine;
            if (isOwn) own = v;
            var at = isOwn ? me : FindPlayer(others, v.Player);
            // The own chain is foreseen from the press; everyone else's (and the own with the restore switches) is the server's latest.
            bool casting = v.Casting;
            IReadOnlyList<LibrarianLink> links = v.Links;
            bool foreseen = isOwn && !NoLibrarianPredict && ForeseeOwnChain(me, v);
            if (foreseen) { casting = true; links = _librarianForeseen; }
            if (isOwn && v.Casting && _librarianPressAt >= 0 && !_librarianServerHeard)
            {
                _librarianServerHeard = true;
                if (_bot || LibrarianDemoOn || LibrarianClipDir is not null) GD.Print($"LIBLAT the server's own links arrived {(Time.GetTicksUsec() / 1e6 - _librarianPressAt) * 1000.0:F0} ms after the press");
            }
            LibrarianSoundFrame(v.Player, at, isOwn, fresh || foreseen, casting, v.Locked, links, now);
            bool known = _librarianCasts.TryGetValue(v.Player, out var casts);
            _librarianCasts[v.Player] = (v.PullCount, v.ShockwaveCount);
            if (fresh && known && v.ShockwaveCount != casts.Shockwave && at is { } shocker) LibrarianShockwaveFx(shocker);   // not the first section this client hears (no replay of old casts)
            if (fresh && known && v.PullCount != casts.Pull && at is { } puller) LibrarianPullFx(puller);   // the restore switch pull only
            if (OldLibrarianLightning) { if (fresh && v.Casting) LibrarianBoltsFeedOld(v); }
            else LibrarianCastFrame(v.Player, isOwn ? _local : _remotes.GetValueOrDefault(v.Player), at, casting, links, v.Stress, isOwn, (float)delta, now);
        }
        LibrarianSoundsGone();
        LibrarianCastsGone();
        if (!Lean) LibrarianLayersFrame((float)delta);
        LibrarianClipCamFrame();   // --librarian-clip --librarian-cam side: the third-person capture view
        if (Lean || _hudView is null) return;
        if (_librarianHud is null) { _librarianHud = new LibrarianHud(); _hudView.AddChild(_librarianHud); }
        _librarianHud.Feed(own, now, LibrarianSwitches.WarpPull);
    }

    static PlayerState? FindPlayer(IReadOnlyList<PlayerState> players, ushort id)
    {
        foreach (var p in players) if (p.PlayerId == id) return p;
        return null;
    }

    // ------------------------------------------------------------------ the own chain, foreseen

    /// <summary>Whether the own player holds Fire now: the test aid's buttons, the last input sent (bots), or the live binding (a human, the mouse captured).</summary>
    bool OwnLightningHeld()
    {
        if ((_tfwTestButtons & InputButtons.Fire) != 0) return true;
        if (_client is { } c && (_sentInputs.TryGet(c.Tick, out var sent) || _sentInputs.TryGet(c.Tick - 1, out sent)) && (sent.Buttons & InputButtons.Fire) != 0) return true;
        return !_bot && MouseCaptured && !OwnDead && !PickerBusy && ControlsInput.Held(GameAction.Fire);
    }

    /// <summary>
    /// The own chain as the server will strike it (<see cref="LibrarianPrediction"/>, on the Orks as they are drawn), into <see cref="_librarianForeseen"/>, while Fire is held and the server would cast
    /// (alive, not locked, not striking). False when it would not.
    /// </summary>
    bool ForeseeOwnChain(PlayerState me, in LibrarianView v)
    {
        if (_config is not { } config || !me.Alive || v.Locked || me.MeleeStep > 0 || !LibrarianRules.IsLibrarian(config, me.Class) || !OwnLightningHeld())
        {
            _librarianPressAt = -1;
            return false;
        }
        var shown = _tfwShown;
        if (_librarianCandidates.Length < shown.Count) { _librarianCandidates = new LibrarianCandidate[shown.Count * 2]; _librarianStruck = new bool[shown.Count * 2]; }
        var rules = config.MusouRules ?? MusouRules.Default;
        int n = 0;
        foreach (var e in shown)
            if (e.State != MusouEnemyState.Dying) _librarianCandidates[n++] = new LibrarianCandidate(e.Id, e.Position, LibrarianPrediction.HeightOf(rules, e.Type));
        _librarianBlocked ??= LibrarianBlocked;
        var eye = me.Position + new Num.Vector3(0f, 0f, config.EyeHeight);
        LibrarianPrediction.Chain(me.Position, eye, _yaw, _pitch, _librarianCandidates.AsSpan(0, n), _client?.Collision is null ? null : _librarianBlocked, _librarianForeseen, _librarianStruck);
        _librarianForeseenFrames++;
        if (_librarianPressAt < 0)
        {
            _librarianPressAt = Time.GetTicksUsec() / 1e6; _librarianFirstBoltMs = -1; _librarianServerHeard = false;
            SendLibrarianCast();
            _librarianFramesLogged = 1;
        }
        return true;
    }

    /// <summary>
    /// At the press: the cast command (<see cref="LibrarianWire.CastCommandTag"/>) with the tick of the input that carries the press, so the server casts from its next tick instead of waiting the input lead
    /// (<c>World.LibrarianCastEarly</c>). One unacknowledged datagram: lost, the input path still casts a few ticks later. Not with <c>--no-librarian-early</c> (test aid: the input path alone).
    /// </summary>
    void SendLibrarianCast()
    {
        if (NoLibrarianEarly || LibrarianSwitches.NoEarly || _client is not { } c) return;
        uint tick = c.Tick;
        if (_sentInputs.TryGet(c.Tick, out var now) && (now.Buttons & InputButtons.Fire) != 0) tick = c.Tick;
        else if (_sentInputs.TryGet(c.Tick - 1, out var last) && (last.Buttons & InputButtons.Fire) != 0) tick = c.Tick - 1;
        if (c.SendExtrasCommand(LibrarianWire.CastCommandTag, LibrarianWire.WriteCast(tick))) _librarianCastsSent++;
    }

    static readonly bool NoLibrarianEarly = OS.GetCmdlineUserArgs().Contains("--no-librarian-early");
    int _librarianCastsSent;

    /// <summary>The server's line-of-sight test (World.Blocked) on the client's level.</summary>
    bool LibrarianBlocked(Num.Vector3 from, Num.Vector3 to)
    {
        if (_client?.Collision is not { } world) return false;
        var line = to - from;
        float length = line.Length();
        return length > 1e-4f && world.Weapons.Raycast(from, line / length, length) is { } hit && hit.Distance < length - 1e-3f;
    }

    // ------------------------------------------------------------------ the cast: pose and bolts

    readonly Dictionary<ushort, LibrarianBolts> _librarianBolts = new();
    readonly Dictionary<ushort, LibrarianBoltsOld> _librarianBoltsOld = new();
    readonly (Vector3 From, Vector3 To)[] _librarianLinks = new (Vector3, Vector3)[LibrarianBolts.MaxLinks];

    /// <summary>
    /// One Librarian's cast this frame: the pose's weight eased in (<see cref="CastInSeconds"/>) and out (<see cref="CastOutSeconds"/>), his palms aimed at the first link's end, the shudder's strength
    /// (the cast's build-up over <see cref="ShudderRampSeconds"/>, the targets, the stress, a spike on each new target), and the bolts from his palms (when the pose has them) along the links.
    /// </summary>
    void LibrarianCastFrame(ushort id, CharacterViewer? character, PlayerState? at, bool casting, IReadOnlyList<LibrarianLink> links, float stress, bool own, float delta, double now)
    {
        if (!_librarianCastTracks.TryGetValue(id, out var track)) _librarianCastTracks[id] = track = new LibrarianCastTrack();
        bool canPose = at is { Alive: true, MeleeStep: 0 };
        if (casting && !track.Was)
        {
            track.StartedAt = now;
            if (own) FxShake(0.009f);   // the release kicks the own camera (ours)
        }
        track.Was = casting;
        float want = casting && canPose ? 1f : 0f;
        track.Weight = Mathf.MoveToward(track.Weight, want, delta / (want > track.Weight ? CastInSeconds : CastOutSeconds));
        if (casting && links.Count > 0)
        {
            track.Target = G(links[0].To);
            if (links.Count > track.Links) track.Spike = 1f;
            track.Links = links.Count;
        }
        else if (!casting) track.Links = 0;
        track.Spike = MathF.Max(0f, track.Spike - delta * 6f);
        float age = (float)(now - track.StartedAt);
        float ramp = Math.Clamp(age / ShudderRampSeconds, 0f, 1f);
        float intensity = (0.45f + 0.55f * ramp) * (0.8f + 0.4f * Math.Clamp(stress / LibrarianRules.StressMax, 0f, 1f)) + 0.25f * Math.Min(1f, links.Count / 12f) + 0.35f * track.Spike;
        if (character is not null && IsInstanceValid(character) && _gameSpace is not null)
        {
            character.LibrarianCast = track.State;
            track.State.Weight = track.Weight;
            track.State.Target = _gameSpace.ToGlobal(track.Target);
            track.State.Intensity = casting ? intensity : intensity * track.Weight;
            track.State.CastAge = casting ? age : 99f;
        }
        if (own && casting) FxShake(0.0014f);   // a held cast trembles the own view (ours; Musou's shakes do not stack)

        if (_gameSpace is null || Lean || DisplayServer.GetName() == "headless") return;
        if (!_librarianBolts.TryGetValue(id, out var bolts))
        {
            bolts = new LibrarianBolts(); _gameSpace.AddChild(bolts); _librarianBolts[id] = bolts;   // made when he is first seen: it warms its materials up before his first cast
            if (!_librarianRings.ContainsKey(id)) { var ring = new LibrarianShockwaveRing(); _gameSpace.AddChild(ring); _librarianRings[id] = ring; ring.WarmUp(); }
        }
        var cast = character?.LibrarianCast;
        bool hands = cast is { PalmsValid: true } && track.Weight > 0f;
        bolts.SetHands(hands, hands ? _gameSpace.ToLocal(cast!.PalmLeft) : Vector3.Zero, hands ? _gameSpace.ToLocal(cast!.PalmRight) : Vector3.Zero, casting ? Math.Max(track.Weight, 0.5f) : track.Weight, now);
        if (!casting) return;
        int n = Math.Min(links.Count, _librarianLinks.Length);
        for (int i = 0; i < n; i++) _librarianLinks[i] = (G(links[i].From), G(links[i].To));
        bolts.Feed(_librarianLinks.AsSpan(0, n), now, delta);
        if (own && _librarianPressAt >= 0 && _librarianFirstBoltMs < 0)
        {
            _librarianFirstBoltMs = (Time.GetTicksUsec() / 1e6 - _librarianPressAt) * 1000.0;
            if (_bot || LibrarianDemoOn || LibrarianClipDir is not null) GD.Print($"LIBLAT own bolt drawn {_librarianFirstBoltMs:F0} ms after the press (foreseen {_librarianForeseen.Count} links)");
        }
    }

    /// <summary>The cast pose's fade in and out, and the shudder's build-up (s, ours).</summary>
    const float CastInSeconds = 0.09f, CastOutSeconds = 0.25f, ShudderRampSeconds = 0.3f;

    /// <summary>A Librarian the sections no longer name (dead, gone, another class): his pose is let go.</summary>
    void LibrarianCastsGone()
    {
        _librarianGone.Clear();
        foreach (var id in _librarianCastTracks.Keys) if (!_librarianSounds.ContainsKey(id)) _librarianGone.Add(id);
        foreach (var id in _librarianGone)
        {
            var character = id == (ushort)(_client?.PlayerId ?? 0) ? _local : _remotes.GetValueOrDefault(id);
            if (character is not null && IsInstanceValid(character) && ReferenceEquals(character.LibrarianCast, _librarianCastTracks[id].State)) character.LibrarianCast = null;
            _librarianCastTracks.Remove(id);
        }
    }

    /// <summary>The restore switch's bolts (<c>--old-librarian-lightning</c>): a section's links into <see cref="LibrarianBoltsOld"/>, as before 2026-11-10.</summary>
    void LibrarianBoltsFeedOld(in LibrarianView v)
    {
        if (_gameSpace is null || Lean || DisplayServer.GetName() == "headless") return;
        if (!_librarianBoltsOld.TryGetValue(v.Player, out var bolts)) { bolts = new LibrarianBoltsOld(); _gameSpace.AddChild(bolts); _librarianBoltsOld[v.Player] = bolts; }
        int n = Math.Min(v.Links.Count, _librarianLinks.Length);
        for (int i = 0; i < n; i++) _librarianLinks[i] = (G(v.Links[i].From), G(v.Links[i].To));
        bolts.Feed(_librarianLinks.AsSpan(0, n), Time.GetTicksMsec() / 1000.0);
    }

    // ------------------------------------------------------------------ sounds

    /// <summary>
    /// A Librarian's sounds (<see cref="LibrarianSounds"/>): his emitter follows him every frame; a change (a new section, or the own foreseen chain every frame) starts or ends the crackle, zaps its new
    /// targets within the budget and plays the lock's vent. The Shockwave's sound plays with its effect (<see cref="LibrarianShockwaveFx"/>).
    /// </summary>
    void LibrarianSoundFrame(ushort player, PlayerState? at, bool own, bool changed, bool casting, bool locked, IReadOnlyList<LibrarianLink> links, double now)
    {
        if (!_librarianSounds.TryGetValue(player, out var s)) _librarianSounds[player] = s = new LibrarianSoundState { Emitter = { Own = own, Label = $"librarian {player}" } };
        s.Seen = true;
        if (at is { } p) s.Emitter.Position = ToGodot(p.Position) + Vector3.Up * 1.4f;
        if (!changed || _sound is null || Lean) return;
        bool layers = LibrarianLayers;   // the decoded and synthesized layers on top of the install's events (NetworkView.LibrarianAudio.cs)
        if (casting && !s.Casting) { _sound.Post(LibrarianSounds.Loop, s.Emitter); if (layers) LibrarianLayerStart(player, own, s.Emitter.Position); }
        else if (!casting && s.Casting) { _sound.Post(LibrarianSounds.Tail, s.Emitter); if (layers) LibrarianLayerStop(player, own, s.Emitter.Position); }
        if (locked && !s.Locked) _sound.Post(LibrarianSounds.Locked, s.Emitter);
        if (casting)
        {
            Span<int> hits = stackalloc int[LibrarianSounds.ZapsPerSection];
            int n = LibrarianSounds.NewHits(s.Casting ? s.Links : [], links, hits, LibrarianSounds.ZapsPerSection);
            for (int k = 0; k < n; k++)
            {
                if (!_librarianZaps.Take(now)) break;
                var to = links[hits[k]].To;
                _sound.Post(LibrarianSounds.Zap, new SoundEmitter { Position = ToGodot(to), OneShot = true, Label = "librarian zap" });
                if (layers) LibrarianLayerSnap(ToGodot(to));
                _librarianZapsPlayed++;
            }
        }
        s.Casting = casting;
        s.Locked = locked;
        if (ReferenceEquals(links, _librarianForeseen)) { _librarianForeseenHeard.Clear(); _librarianForeseenHeard.AddRange(links); s.Links = _librarianForeseenHeard; }   // the foreseen list is refilled every frame: keep a copy to compare with
        else s.Links = links;
    }

    /// <summary>A Librarian the sections no longer name (dead, gone, another class): his crackle ends with its tail.</summary>
    void LibrarianSoundsGone()
    {
        _librarianGone.Clear();
        foreach (var (id, s) in _librarianSounds) if (!s.Seen) _librarianGone.Add(id);
        foreach (var id in _librarianGone)
        {
            if (_librarianSounds[id].Casting && _sound is not null) _sound.Post(LibrarianSounds.Tail, _librarianSounds[id].Emitter);
            LibrarianLayersGone(id);
            _librarianSounds.Remove(id);
            _librarianCasts.Remove(id);
        }
    }

    SoundEmitter? LibrarianEmitter(PlayerState p) => _librarianSounds.TryGetValue((ushort)p.PlayerId, out var s) ? s.Emitter : null;

    readonly Dictionary<int, LibrarianShockwaveRing> _librarianRings = new();

    /// <summary>The Warp Pull (restore switch <c>pull</c> only, as pass 2): the Weirdboy's summons ring at his feet, tinted blue, and the psyker bolt's cast.</summary>
    void LibrarianPullFx(PlayerState p)
    {
        if (_sound is not null && LibrarianEmitter(p) is { } e) _sound.Post(LibrarianSounds.Pull, e);
        if (_fx is null || !FxActive) return;
        _fx.Play("weirdboy_summon_staff", ToGodot(p.Position), Basis.Identity, 1.5f, priority: 2, tint: PsychicBlue);
    }

    void LibrarianShockwaveFx(PlayerState p)
    {
        if (_sound is not null && LibrarianEmitter(p) is { } e)
        {
            _sound.Post(LibrarianSounds.Shockwave, e);
            if (LibrarianLayers) LibrarianLayerShockwave((ushort)p.PlayerId, p.PlayerId == (_client?.PlayerId ?? 0), e.Position);   // the thunderclap under it
        }
        if (_gameSpace is not null && !Lean && DisplayServer.GetName() != "headless")
        {
            if (!_librarianRings.TryGetValue(p.PlayerId, out var ring)) { ring = new LibrarianShockwaveRing(); _gameSpace.AddChild(ring); _librarianRings[p.PlayerId] = ring; }
            ring.Play(G(p.Position), LibrarianSwitches.OldShockwave ? LibrarianRules.Pass2.ShockwaveRadius : LibrarianRules.ShockwaveRadius);
            if (p.PlayerId == (_client?.PlayerId ?? 0)) FxShake(0.02f);   // ours: the caster feels his 60 m blast
        }
        if (_fx is null || !FxActive) return;
        var at = ToGodot(p.Position + new Num.Vector3(0f, 0f, 1f));
        _fx.Play("plasma_gun_mp_overcharged_dmg", at, Basis.Identity, 1.2f, priority: 2, tint: PsychicBlue);   // the 15 m blast plays at 1.5; at the caster's feet, the ring carries the 60 m reach
        _fx.Play("weirdboy_shield_break", at, Basis.Identity, 3f, priority: 2, tint: PsychicBlue);
    }
}

/// <summary>
/// The Librarian's gauge (Musou Mode only; <see cref="LibrarianHudModel"/>), drawn for the own Librarian in the weapon box's place, size and style (he carries no gun, so the match HUD leaves the box empty):
/// the box's dark chamfered panel and gold line turned 2.5 degrees, the stress in the reserve's large digits, PSYCHIC STRESS beside them, the stress as the heat bar along the bottom (the heat bar's red,
/// its dark track, blinking as an overheated weapon while every power is locked, then PSYCHIC OVERLOAD), and the Shockwave (reload key) as a cell left of the grenade box, with a draining bar for its cooldown
/// (pass 3: the Warp Pull's cell went with the power, and the match HUD's own grenade box shows his equipment where the Shockwave's cell was). Colours are the match HUD's (Hud.cs); layout ours, waiting for
/// the owner's screenshot approval.
/// </summary>
public sealed partial class LibrarianHud : Control
{
    const float RefHeight = 1080f, BoxW = 388f, BoxH = 122f, Tilt = 2.5f;
    static readonly Color PanelFill = new(0.02f, 0.02f, 0.025f, 0.84f);
    static readonly Color FrameGold = new(0.74f, 0.64f, 0.32f, 0.95f);
    static readonly Color DimGold = new(0.45f, 0.4f, 0.2f, 0.9f);
    static readonly Color RedFill = new(0.86f, 0.04f, 0.04f);
    static readonly Color RedText = new(0.9f, 0.08f, 0.07f);
    static readonly Color Track = new(0.25f, 0.02f, 0.02f, 0.55f);
    static readonly Color DigitWhite = new(0.93f, 0.93f, 0.95f);
    static readonly Color Grey = new(0.62f, 0.62f, 0.64f);
    static readonly Color Gold = new(0.98f, 0.8f, 0.12f);
    static readonly Color Psychic = new(0.62f, 0.8f, 1f);
    FontVariation? _font, _fontBold;
    LibrarianView? _view;
    double _time;

    public LibrarianHud()
    {
        Name = "LibrarianHud";
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
    }

    public override void _Ready() => (_font, _fontBold) = TfwHud.MakeFonts();

    bool _pull;

    /// <summary>This frame's view of the own Librarian (null when the client is not one), the time for the blink, and whether the Warp Pull is back (restore switch <c>pull</c>: its cell too).</summary>
    public void Feed(LibrarianView? view, double time, bool pull = false)
    {
        _view = view;
        _time = time;
        _pull = pull;
        Visible = view is not null;
        QueueRedraw();
    }

    static Vector2[] Chamfered(float x, float y, float w, float h, float cut) =>
        [new(x, y), new(x + w - cut, y), new(x + w, y + cut), new(x + w, y + h), new(x + cut, y + h), new(x, y + h - cut)];

    void Panel(float x, float w, float cut, bool lit)
    {
        var pts = Chamfered(x, 0f, w, BoxH, cut);
        DrawPolygon(pts, [PanelFill, PanelFill, new Color(0f, 0f, 0f, 0.86f), new Color(0f, 0f, 0f, 0.9f), new Color(0f, 0f, 0f, 0.9f), PanelFill]);
        DrawPolyline([.. pts, pts[0]], lit ? FrameGold : DimGold, 2f, true);
    }

    void Txt(string s, float x, float y, int size, Color c, int align = -1, bool bold = false) => TfwHud.Text(this, bold ? _fontBold : _font, s, x, y, size, c, align, 3);

    public override void _Draw()
    {
        if (_view is not { } v || _font is null) return;
        float scale = Size.Y / RefHeight;
        if (scale <= 0f) return;
        var vs = Size / scale;
        DrawSetTransform(new Vector2(vs.X - 514f, vs.Y - 248f) * scale, Mathf.DegToRad(Tilt), new Vector2(scale, scale));

        // The box: the stress in the reserve's digits, the label, the bar along the bottom.
        Panel(0f, BoxW, 11f, true);
        float fill = LibrarianHudModel.StressFill(v.Stress), blink = LibrarianHudModel.Blink(v.Locked, _time);
        Txt(((int)MathF.Ceiling(v.Stress)).ToString(System.Globalization.CultureInfo.InvariantCulture), 16f, 46f, 42, v.Locked ? RedText : DigitWhite, -1, true);
        Txt(v.Locked ? "PSYCHIC OVERLOAD" : "PSYCHIC STRESS", BoxW - 18f, 40f, 20, v.Locked ? new Color(1f, 0.3f, 0.25f, blink) : Psychic, 1, true);
        Txt(v.Locked ? "powers locked until the stress is gone" : v.Casting ? "chain lightning" : "hold fire: chain lightning", BoxW - 18f, 66f, 15, Grey, 1);
        DrawRect(new Rect2(16f, 99f, 334f, 10f), Track);
        DrawRect(new Rect2(16f, 98f, 334f * fill, 12f), v.Locked ? new Color(1f, blink * 0.35f, blink * 0.3f) : RedFill);

        // The Shockwave: a cell left of the match HUD's grenade box (which shows his equipment at -86).
        Cell(-86f - 92f, "WAVE", "RELOAD", v.ShockwaveCooldown, LibrarianRules.ShockwaveCooldown, v.Locked);
        if (_pull) Cell(-86f, "PULL", "GRENADE", v.PullCooldown, LibrarianRules.Pass2.PullCooldown, v.Locked);   // restore switch pull: no equipment, so the grenade box's place is free
        DrawSetTransform(Vector2.Zero, 0f, Vector2.One);
    }

    void Cell(float x, string name, string key, float left, float cooldown, bool locked)
    {
        const float w = 80f;
        bool ready = left <= 0f && !locked;
        Panel(x, w, 9f, ready);
        Txt(name, x + w / 2f, 30f, 17, ready ? Gold : Grey, 0, true);
        if (locked) Txt("LOCKED", x + w / 2f, 64f, 15, RedText, 0, true);
        else if (left > 0f) Txt(left.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture), x + w / 2f, 66f, 26, DigitWhite, 0, true);
        else Txt("READY", x + w / 2f, 64f, 16, Psychic, 0, true);
        Txt(key, x + w / 2f, 90f, 12, Grey, 0);
        float f = LibrarianHudModel.CooldownFill(left, cooldown);
        DrawRect(new Rect2(x + 8f, 100f, w - 16f, 8f), Track);
        DrawRect(new Rect2(x + 8f, 100f, (w - 16f) * f, 8f), locked ? new Color(0.5f, 0.1f, 0.1f) : ready ? Psychic : new Color(0.75f, 0.75f, 0.78f));
    }
}
