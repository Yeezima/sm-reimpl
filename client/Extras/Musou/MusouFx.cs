using System;
using System.Collections.Generic;
using Godot;
using SmContent;
using SmNet;
using SmNet.Extras.Musou;
using SmSim;
using SmSim.Extras.Musou;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// Added content "Musou", MU3 on the client: the HUD (<see cref="MusouHud"/>), the hit feedback, the attack telegraphs and the Orks' voices, driven by the crowd
/// state's new events each frame (<see cref="MusouPresent"/>, from <c>NetworkView.Musou.cs</c>; only on a client that draws: never headless or on a lean bot).
/// <list type="bullet">
/// <item>Hit feedback: the match HUD's hit marker for the own hits on enemies (a head hit in its warm colour) and the kill marker for an own KO; at enemy hits
/// within <see cref="FxRange"/> the red splash of a body hit (<see cref="BloodSplashAt"/>) and, for an own shot, the weapon's own impact on broken armour (the
/// flesh-penetrating variant, <see cref="EffectCatalogue.Impact"/>), at most <see cref="FxPerSecond"/> a second.</item>
/// <item>Telegraphs (<see cref="MusouTelegraphs"/>): a faint wedge on the ground in front of an enemy winding up a melee blow (its reach and cone, filling over the
/// wind-up); the Warboss's Ground Slam as a ring of its radius filling over its wind-up, its Charge as a lane of its width to its end point; Waaagh! and the
/// enrage as call-outs with a screen flash and a roar.</item>
/// <item>Voices: the Ork Boy's lines (<c>ork_boy_2</c>, the blueprint's tags as the Ork class uses them: the death yell, the taunt at an attack, the roar for the
/// boss) from at most <see cref="VoiceSlots"/> emitters at once; a line with no free emitter is dropped (the boss's take the oldest).</item>
/// <item>The run's own score: the KOs and the points with the server's chain rule (<see cref="MusouRules.ChainSeconds"/>, <see cref="MusouRules.ChainStep"/>,
/// <see cref="MusouRules.ChainMax"/>) counted from the own KOs' death events (the points are not on the wire: an estimate that matches the server's while no
/// death event is lost); the KOs never fall below the scoreboard's kills.</item>
/// </list>
/// Test aid: <c>--musou-hud-demo [wave|break|boss|won|lost]</c> shows the HUD and the telegraphs with made-up values (the phase held, or a 20 s cycle;
/// a made-up enemy winds up beside the own player throughout).
/// All sizes, colours, limits and timings here are ours (kind: ours) except the boss's radius, width and wind-ups (design section B).
/// </summary>
public partial class NetworkView
{
    /// <summary>Enemy hits farther than this from the camera get no blood or impact (m, ours).</summary>
    const float FxRange = 30f;
    /// <summary>Most hit effects a second (a token bucket of <see cref="FxBurst"/>; ours).</summary>
    const float FxPerSecond = 16f, FxBurst = 8f;
    /// <summary>Most Ork voices at once (design section D: ork_boy_2 at most 6), the least time between two new ones (s), and how far they are posted (m); ours.</summary>
    const int VoiceSlots = 6;
    const double VoiceSpacing = 0.1;
    const float VoiceRange = 40f, AttackVoiceRange = 20f, AttackVoiceChance = 0.3f;
    /// <summary>A voice slot whose line length is not known is held this long (s, ours).</summary>
    const double VoiceHold = 1.5;
    MusouHud? _musouHud;
    int _musouLivesMax;
    uint _musouRunTick;
    MusouTelegraphs? _musouTelegraphs;
    MusouDirectorView? _musouPrevDirector;
    double _musouDirectorAt;
    bool _musouEnraged;
    ushort _musouBossId;
    float _musouFxTokens = FxBurst;
    readonly SoundEmitter?[] _musouVoices = new SoundEmitter?[VoiceSlots];
    readonly double[] _musouVoiceUntil = new double[VoiceSlots];
    readonly double[] _musouVoiceStarted = new double[VoiceSlots];
    double _musouLastVoiceAt = double.NegativeInfinity;
    int _musouVoicesPosted, _musouVoicesDropped, _musouHitFx, _musouHitMarkers;
    readonly Random _musouVoiceRoll = new(5);
    readonly MusouScoreTracker _musouScore = new();
    string? _musouDemo;
    bool _musouDemoRead;

    /// <summary>
    /// The run's KOs, chain and points of the own player as the client counts them (the server's rule, <see cref="MusouDirector.OnKill"/>): a KO within
    /// <see cref="MusouRules.ChainSeconds"/> of the last (in server ticks) chains, the multiplier rises by <see cref="MusouRules.ChainStep"/> a chained KO up to
    /// <see cref="MusouRules.ChainMax"/>, and a KO earns its type's points times the multiplier.
    /// </summary>
    sealed class MusouScoreTracker
    {
        public int Kos, Points, Chain;
        public float Multiplier = 1f;
        public double LastAt = double.NegativeInfinity;
        uint _lastTick;

        public void Reset() { Kos = Points = Chain = 0; Multiplier = 1f; LastAt = double.NegativeInfinity; _lastTick = 0; }

        public void Ko(MusouEnemyType type, uint tick, double now, MusouRules rules, int tickRate)
        {
            long gap = Math.Abs((long)tick - _lastTick);
            bool chained = Kos > 0 && gap <= (long)MathF.Round(rules.ChainSeconds * tickRate);
            Chain = chained ? Chain + 1 : 0;
            Multiplier = MathF.Min(rules.ChainMax, 1f + rules.ChainStep * Chain);
            int index = (int)type;
            int points = index >= 0 && index < rules.Types.Count ? rules.Types[index].Points : 0;
            Points += (int)MathF.Round(points * Multiplier);
            Kos++;
            if (tick >= _lastTick) _lastTick = tick;
            LastAt = now;
        }

        /// <summary>The chain window's fraction left (0..1).</summary>
        public float Left(double now, MusouRules rules) => rules.ChainSeconds <= 0f ? 0f : (float)Math.Clamp(1.0 - (now - LastAt) / rules.ChainSeconds, 0.0, 1.0);
    }

    /// <summary>
    /// This frame's HUD, hit feedback, telegraphs and voices (a client that draws only; before the crowd state's new events are taken). The view is single-use
    /// for Musou: its config (so <see cref="MusouActive"/>) is set once per connection; a new run on the same server is found from the director (back to
    /// waiting, out of a won or lost run, or the wave counting back; a late or repeated director is ignored by the crowd state) and forgets the crowd's enemies,
    /// the run's own counts and the telegraphs, keeping the new director and this frame's events. The events of a frame
    /// are always read (the KOs count even before the HUD or the game space exist); the effects only once both exist.
    /// </summary>
    void MusouPresent(PlayerState me, IReadOnlyList<MusouShown> shown, float delta)
    {
        if (_client is not { } client || _config is null) return;
        if (!_musouDemoRead) { _musouDemoRead = true; _musouDemo = CmdFlag("musou-hud-demo") ? CmdArg("musou-hud-demo") ?? "" : null; }
        if (_musouHud is null && _hudView is not null && _gameSpace is not null)
        {
            _musouHud = new MusouHud();
            _hudView.AddChild(_musouHud);
            _musouTelegraphs = new MusouTelegraphs { Name = "MusouTelegraphs" };
            _gameSpace.AddChild(_musouTelegraphs);
            GD.Print("musou: HUD and telegraphs added");
        }
        double now = _connectedFor;
        var rules = _config.MusouRules ?? MusouRules.Default;
        var boss = rules.Boss;
        int ownId = client.PlayerId;
        var camera = _camera is null ? Num.Vector3.Zero : new Num.Vector3(_camera.GlobalPosition.X, -_camera.GlobalPosition.Z, _camera.GlobalPosition.Y);   // Godot to game space
        _musouFxTokens = MathF.Min(FxBurst, _musouFxTokens + FxPerSecond * delta);
        bool present = _musouHud is not null && _musouTelegraphs is not null;

        // The director: a new run forgets the crowd's enemies, the run's own counts and the telegraphs.
        var d = _musouState.Director;
        if (d is { } dir && !Nullable.Equals(dir, _musouPrevDirector))
        {
            if (_musouPrevDirector is { } prev && MusouNewRun(prev, dir))
            {
                _musouScore.Reset();
                _musouLivesMax = 0;
                _musouEnraged = false;
                _musouRunTick = _musouState.NewestTick;   // a scoreboard older than this still counts the last run's kills
                _musouTelegraphs?.Clear();
                _musouState.ForgetEnemies();   // the server cleared its pool without deaths: no enemy of the last run lingers for the silence time
            }
            if (dir.BossId != _musouBossId) { _musouBossId = dir.BossId; _musouEnraged = false; }
            _musouPrevDirector = dir;
            _musouDirectorAt = now;
        }

        // Events: the score always; the hit marker, the effects, the telegraphs and the voices once the HUD is there.
        var events = _musouState.NewEvents;
        for (int i = 0; i < events.Count; i++)
        {
            var e = events[i];
            switch (e.Kind)
            {
                case MusouEventKind.Hit:
                    if (!present) break;
                    if (e.Player == ownId) { _hudView?.ShowHitMarker(false, e.Value == 2); _musouHitMarkers++; }
                    MusouHitFx(e, shown, camera, own: e.Player == ownId, me);
                    break;
                case MusouEventKind.Death:
                    var (place, type) = MusouDeathOf(e);
                    if (e.Player == ownId && e.Player != 0)
                    {
                        if (present) _hudView?.ShowHitMarker(true);
                        _musouScore.Ko(type, e.Tick, now, rules, _config.TickRate);
                    }
                    if (present && Num.Vector3.DistanceSquared(place, camera) <= VoiceRange * VoiceRange) MusouVoice(OrkSounds.DeathTag, place, force: false);
                    break;
                case MusouEventKind.Telegraph:
                    if (present) MusouTelegraph(e, shown, camera, boss);
                    break;
            }
        }
        if (_musouHud is null || _musouTelegraphs is null) return;
        _musouHud.Fed();
        _musouHud.Visible = _endPhase == MatchEndPhase.Splash;   // the match's own end screens after the splash (count-up, record, scoreboard) cover the screen

        // The match HUD's score box: Musou has no team scores (the owner, 2026-11-01: removed; the KOs and points are in the run panel).
        if (_hudView is not null) _hudView.Data.NoScorePanel = true;

        // The HUD's frame.
        var f = _musouHud.Data;
        f.Director = d;
        f.Alive = me.Alive;
        float rate = Math.Max(1, _config.TickRate);
        f.BreakLeft = d is { } b ? (float)Math.Max(0.0, b.BreakTicksLeft / rate - (now - _musouDirectorAt)) : 0f;
        // The scoreboard's kills are the server's KOs (reset when a run's round starts: World.StartRound); one from before this run's start is stale.
        int boardKills = 0;
        if (client.Scoreboard is { } board && board.ServerTick >= _musouRunTick)
            foreach (var entry in board.Players) if (entry.PlayerId == ownId) boardKills = entry.Kills;
        f.Kos = Math.Max(_musouScore.Kos, boardKills);
        f.Points = _musouScore.Points;
        f.Chain = _musouScore.Chain;
        f.Multiplier = _musouScore.Multiplier;
        f.ChainLeft = _musouScore.Left(now, rules);
        f.BossEnraged = _musouEnraged || d is { BossId: > 0, BossMaxHealth: > 0f } bb && bb.BossHealth < boss.EnrageBelow * bb.BossMaxHealth;
        if (d is { } l) _musouLivesMax = Math.Max(_musouLivesMax, l.Lives);
        f.LivesMax = _musouLivesMax;
        // After a won or lost run the match has ended: the server starts the next run when the match's restart time runs out (World.Musou: Ended, then warm-up).
        f.NextRunIn = client.Match.State == MatchState.Ended ? (float)SecondsLeft(client.Match) : -1f;
        if (_musouDemo is not null)
        {
            MusouDemo(f, me, rules, now);
            // A made-up Boy winding up beside the own player, again and again (its wedge on the ground).
            _musouDemoShown.Clear();
            _musouDemoShown.AddRange(shown);
            var beside = me.Position + new Num.Vector3(MathF.Cos(me.Yaw), MathF.Sin(me.Yaw), 0f) * 2.5f;
            _musouDemoShown.Add(new MusouShown(ushort.MaxValue, beside, me.Yaw + MathF.PI / 2f, MusouEnemyType.Boy, MusouEnemyState.WindUp, (float)(now % 0.6)));
            shown = _musouDemoShown;
        }

        _musouTelegraphs.Frame(shown, camera, rules, delta);
    }

    /// <summary>Whether the director went from <paramref name="prev"/> into a new run: back to waiting, out of a won or lost run, or the wave counting back.</summary>
    static bool MusouNewRun(in MusouDirectorView prev, in MusouDirectorView dir) =>
        dir.Phase == MusouPhase.Waiting && prev.Phase != MusouPhase.Waiting
        || prev.Phase is MusouPhase.Won or MusouPhase.Lost && dir.Phase is not (MusouPhase.Won or MusouPhase.Lost)
        || dir.Wave < prev.Wave;

    /// <summary>
    /// A death's place and type: where and what the crowd last showed (its body this frame); for an enemy this client never saw, far away (no voice) and a Boy
    /// (its points an estimate).
    /// </summary>
    (Num.Vector3 Place, MusouEnemyType Type) MusouDeathOf(in MusouEvent e)
    {
        var deaths = _musouState.NewDeaths;
        for (int i = 0; i < deaths.Count; i++)
            if (deaths[i].Id == e.Enemy && deaths[i].Tick == e.Tick) return (deaths[i].Position, deaths[i].Type);
        return (new Num.Vector3(float.MaxValue), MusouEnemyType.Boy);
    }

    static int MusouFind(IReadOnlyList<MusouShown> shown, ushort id)
    {
        int lo = 0, hi = shown.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            int c = shown[mid].Id.CompareTo(id);
            if (c == 0) return mid;
            if (c < 0) lo = mid + 1; else hi = mid - 1;
        }
        return -1;
    }

    /// <summary>The blood and the own weapon's flesh impact at an enemy hit (within <see cref="FxRange"/>, within the budget).</summary>
    void MusouHitFx(in MusouEvent e, IReadOnlyList<MusouShown> shown, Num.Vector3 camera, bool own, PlayerState me)
    {
        var at = e.Vector;
        int i = MusouFind(shown, e.Enemy);
        // A hit reported at the feet (the enemy's place) is raised to the chest; a real hit point higher up stays.
        if (i >= 0 && at.Z < shown[i].Position.Z + 0.5f) at = new Num.Vector3(at.X, at.Y, shown[i].Position.Z + 1.1f);
        else if (i < 0 && own) at = new Num.Vector3(at.X, at.Y, at.Z + 1.1f);
        if (e.Value == 3) { MusouPowerFlash(e, at, camera, me); MusouMeleeHitSound(e, at, camera, me); }   // fx/power-flash: a power weapon's cleave (the swing's budget caps the bursts)
        if (_musouFxTokens < 1f || Num.Vector3.DistanceSquared(at, camera) > FxRange * FxRange) return;
        _musouFxTokens -= 1f;
        _musouHitFx++;
        var end = ToGodot(at);
        var from = _camera?.GlobalPosition ?? end + Vector3.Up;
        var dir = (end - from).Normalized();
        BloodSplashAt(end, dir, 0.0);
        if (!own || e.Value == 3 || _fx is null || _catalogue is null || Loadouts.InHand(_config!, me) is not { } weapon) return;
        var plays = _catalogue.Impact(weapon.Name, e.Value == 2 ? ShotHit.Head : ShotHit.Body, 0, armourUp: false);
        for (int k = 0; k < plays.Count; k++)
            if (plays[k] is var p) _fx.Play(p.Name, end - dir * 0.05f, AxesFromY(-dir), p.Scale <= 0f ? 1f : p.Scale, null, null, p.Lifetime, 0.0, false, 0);
    }

    /// <summary>fx/power-flash: the flash of a melee hit (marker 3) by a player holding a power weapon, on the enemy at <paramref name="at"/> (game space); within <see cref="FxRange"/> only.</summary>
    void MusouPowerFlash(in MusouEvent e, Num.Vector3 at, Num.Vector3 camera, PlayerState me)
    {
        if (e.Player == 0 || _client is null || Num.Vector3.DistanceSquared(at, camera) > FxRange * FxRange) return;
        PlayerState? attacker = e.Player == _client.PlayerId ? me : null;
        if (attacker is null) for (int k = 0; k < _lastOthers.Count; k++) if (_lastOthers[k].PlayerId == e.Player) { attacker = _lastOthers[k]; break; }
        if (attacker is not { } who || MeleeWargear(who) is not { } wargear) return;
        var contact = ToGodot(at);
        var toward = ToGodot(who.Position) - contact; toward.Y = 0f;
        PowerHit(e.Player, wargear, contact, (toward.LengthSquared() > 1e-6f ? toward.Normalized() : Vector3.Up) + Vector3.Up * 0.3f, "musou hit");
    }

    /// <summary>fx/melee-hit-sound: the generated hit sound of a melee hit (marker 3) on a Musou enemy at <paramref name="at"/> (game space); one swing sounds at most three times (ours).</summary>
    readonly SmClient.ImpactRules.PowerFlashRules.Budget _musouHitSoundSwing = new();
    void MusouMeleeHitSound(in MusouEvent e, Num.Vector3 at, Num.Vector3 camera, PlayerState me)
    {
        if (e.Player == 0 || _client is null || Num.Vector3.DistanceSquared(at, camera) > FxRange * FxRange) return;
        PlayerState? attacker = e.Player == _client.PlayerId ? me : null;
        if (attacker is null) for (int k = 0; k < _lastOthers.Count; k++) if (_lastOthers[k].PlayerId == e.Player) { attacker = _lastOthers[k]; break; }
        if (attacker is not { } who || ProceduralHitSound(who, charge: false, dash: false) is not { } synth) return;
        var burst = _musouHitSoundSwing.Take(e.Player, _connectedFor);
        if (burst == SmClient.ImpactRules.PowerBurst.Lead) _musouHitSoundsThisSwing = 0;
        else if (burst != SmClient.ImpactRules.PowerBurst.Small || ++_musouHitSoundsThisSwing > 2) return;
        string played = PlayProceduralHit(synth, ToGodot(at), $"musou melee hit by P{e.Player}");
        CountSound("melee hit");
        if (LogSound("melee")) GD.Print($"MELEE HIT SOUND t {_connectedFor:F3} P{e.Player} musou hit event {played}");
    }
    int _musouHitSoundsThisSwing;

    /// <summary>A telegraph: the boss's ring, lane and call-outs, the attack taunt of a melee wind-up.</summary>
    void MusouTelegraph(in MusouEvent e, IReadOnlyList<MusouShown> shown, Num.Vector3 camera, MusouBossRules boss)
    {
        int i = MusouFind(shown, e.Enemy);
        var from = i >= 0 ? shown[i].Position : e.Vector;
        switch ((MusouBossAbility)e.Value)
        {
            case MusouBossAbility.GroundSlam:
                _musouTelegraphs?.Ring(e.Vector, boss.SlamRadius, boss.SlamWindUp);
                MusouVoice(OrkSounds.RoarTag, from, force: true);
                break;
            case MusouBossAbility.Charge:
                _musouTelegraphs?.Lane(from, e.Vector, boss.ChargeWidth, boss.ChargeWindUp);
                MusouVoice(OrkSounds.RoarTag, from, force: true);
                break;
            case MusouBossAbility.Waaagh:
                _musouHud?.Waaagh();
                MusouVoice(OrkSounds.RoarTag, from, force: true);
                break;
            case MusouBossAbility.Enrage:
                _musouEnraged = true;
                _musouHud?.Enraged();
                MusouVoice(OrkSounds.RoarTag, from, force: true);
                break;
            default:   // MusouSim.MeleeWindUpTelegraph: the wedge comes from the enemy's state (MusouTelegraphs); a taunt now and then
                if (_musouVoiceRoll.NextSingle() < AttackVoiceChance && Num.Vector3.DistanceSquared(from, camera) <= AttackVoiceRange * AttackVoiceRange) MusouVoice(OrkSounds.BarkTag, from, force: false);
                break;
        }
    }

    /// <summary>
    /// One Ork line (the boy blueprint's tag: its sound events and its voice lines for <c>ork_boy_2</c>) from a free voice slot at <paramref name="at"/> (game space);
    /// without a free slot it is dropped, unless <paramref name="force"/> (the boss), which cuts the oldest. False when nothing was posted.
    /// </summary>
    bool MusouVoice(string tag, Num.Vector3 at, bool force)
    {
        double now = _connectedFor;
        if (!force && now - _musouLastVoiceAt < VoiceSpacing) { _musouVoicesDropped++; return false; }   // the cheap check first
        if (_sound is null || _soundTriggers is null || !float.IsFinite(at.X) || at.X == float.MaxValue) return false;
        _musouOrkFile ??= _soundTriggers.File(OrkSounds.Blueprint);
        _musouOrkCharacter ??= _soundTriggers.SpeechCharacter(OrkClass.Key);
        if (_musouOrkFile is not { } file || _musouOrkCharacter is not { } character) return false;
        int slot = -1, oldest = 0;
        for (int i = 0; i < VoiceSlots; i++)
        {
            _musouVoices[i] ??= new SoundEmitter { Label = $"musou voice {i}" };
            bool busy = now < _musouVoiceUntil[i] || _sound.SpeechBusy(_musouVoices[i]!);
            if (!busy) { slot = i; break; }
            if (_musouVoiceStarted[i] < _musouVoiceStarted[oldest]) oldest = i;
        }
        if (slot < 0)
        {
            if (!force) { _musouVoicesDropped++; return false; }
            slot = oldest;
            _sound.StopSpeech(_musouVoices[slot]!);
            _sound.StopEmitter(_musouVoices[slot]!);
        }
        var emitter = _musouVoices[slot]!;
        emitter.Position = ToGodot(at) + new Vector3(0f, 1.6f, 0f);
        bool posted = false;
        var events = file.Tag(tag);
        for (int k = 0; k < events.Count; k++) posted |= _sound.Post(events[k].StartEvent, emitter) is { Found: true };
        var lines = file.SpeechTag(tag);
        for (int k = 0; k < lines.Count; k++) posted |= _sound.PostSpeech(lines[k], character, emitter) == SpeechResult.Started;
        if (!posted) return false;
        _musouVoiceStarted[slot] = now;
        _musouVoiceUntil[slot] = now + VoiceHold;
        _musouLastVoiceAt = now;
        _musouVoicesPosted++;
        return true;
    }

    /// <summary>The note for the 10 s log line.</summary>
    string MusouPresentNote() => _musouHud is null ? "hud none" : $"hud on, hit markers {_musouHitMarkers}, hit fx {_musouHitFx}, voices {_musouVoicesPosted} (+{_musouVoicesDropped} dropped), " +
                                                                  $"telegraphs {_musouTelegraphs?.Shown ?? 0}, KOs {_musouScore.Kos} pts {_musouScore.Points}";

    /// <summary><c>--musou-hud-demo</c>: made-up values for screenshots (the phase held, or wave, break, boss, won, lost in turn every 4 s).</summary>
    void MusouDemo(MusouHud.Frame f, PlayerState me, MusouRules rules, double now)
    {
        int cycle = (int)(now / 4.0);
        string phase = _musouDemo is { Length: > 0 } held ? held : DemoPhases[cycle % DemoPhases.Length];
        bool fresh = _musouDemoPhase != phase || cycle != _musouDemoCycle;   // a held boss phase replays its telegraphs every 4 s
        _musouDemoPhase = phase;
        _musouDemoCycle = cycle;
        var box = f.Director?.Box ?? new MusouBox(new Num.Vector3(-100f), new Num.Vector3(100f));
        f.Director = phase switch
        {
            "break" => new MusouDirectorView(2, 5, MusouPhase.Break, (ushort)(12 * _config!.TickRate), 0, 0, 0, 0f, 0f, 5, box),
            "boss" => new MusouDirectorView(5, 5, MusouPhase.Boss, 0, 41, 41, 77, 2480f, 4000f, 3, box),
            "won" => new MusouDirectorView(5, 5, MusouPhase.Won, 0, 0, 0, 0, 0f, 4000f, 2, box),
            "lost" => new MusouDirectorView(4, 5, MusouPhase.Lost, 0, 112, 112, 0, 0f, 0f, 0, box),
            _ => new MusouDirectorView(3, 5, MusouPhase.Wave, 0, 87, 64, 0, 0f, 0f, 4, box),
        };
        f.LivesMax = 5;
        f.NextRunIn = phase is "won" or "lost" ? 15f - (float)(now % 4.0) : -1f;
        f.BreakLeft = 12f - (float)(now % 4.0);
        f.Kos = 214; f.Points = 3120;
        f.Chain = 6; f.Multiplier = MathF.Min(rules.ChainMax, 1f + rules.ChainStep * 6); f.ChainLeft = 0.65f;
        f.BossEnraged = false;
        if (phase == "boss" && fresh)
        {
            _musouHud?.Waaagh();
            float yaw = me.Yaw;
            var ahead = new Num.Vector3(-MathF.Sin(yaw), MathF.Cos(yaw), 0f);
            var side = new Num.Vector3(ahead.Y, -ahead.X, 0f);
            _musouTelegraphs?.Ring(me.Position + ahead * 9f, rules.Boss.SlamRadius, 3.5f);
            _musouTelegraphs?.Lane(me.Position + ahead * 6f + side * 7f, me.Position + ahead * 10f - side * 8f, rules.Boss.ChargeWidth, 3.5f);
        }
    }

    static readonly string[] DemoPhases = ["wave", "break", "boss", "won", "lost"];
    string? _musouDemoPhase;
    int _musouDemoCycle = -1;
    SoundFile? _musouOrkFile;
    string? _musouOrkCharacter;
    readonly List<MusouShown> _musouDemoShown = new();
}

/// <summary>
/// Added content "Musou": the attack telegraphs on the ground (in game space, a child of the view's game space): rings (the Warboss's Ground Slam), lanes (its
/// Charge) and the faint wedges of the enemies' melee wind-ups (from their shown state: reach and cone of their type, at most <see cref="MaxWedges"/> within
/// <see cref="WedgeRange"/>). Each is a flat quad with a small shader of its own (a fill grows over the wind-up; it fades after). Ours.
/// </summary>
public sealed partial class MusouTelegraphs : Node3D
{
    public const int MaxWedges = 24, MaxMarks = 6;
    public const float WedgeRange = 25f;
    /// <summary>How long a ring or lane stays after its wind-up (s), and how high above the floor marks lie (m).</summary>
    const double After = 0.3;
    const float Lift = 0.06f;

    const string ShaderCode = @"shader_type spatial;
render_mode unshaded, blend_mix, depth_draw_never, cull_disabled, shadows_disabled, fog_disabled;
uniform vec4 tint : source_color = vec4(1.0, 0.25, 0.05, 1.0);
uniform float fill = 0.0;
uniform float shape = 0.0;
uniform float half_angle = 0.5;
uniform float fade = 1.0;
void fragment() {
	vec2 p = vec2(UV.x * 2.0 - 1.0, 1.0 - UV.y * 2.0);
	float a = 0.0;
	if (shape < 0.5) {
		float r = length(p);
		if (r > 1.0) discard;
		float rim = smoothstep(0.9, 0.96, r);
		float front = 1.0 - smoothstep(0.0, 0.025, abs(r - fill));
		a = max(rim * 0.95, max(r < fill ? 0.28 : 0.1, front * 0.85));
	} else if (shape < 1.5) {
		float t = (p.y + 1.0) * 0.5;
		float rim = smoothstep(0.78, 0.9, abs(p.x));
		float chevron = step(0.6, fract(p.y * 3.0 - abs(p.x) * 0.6 - TIME * 2.5)) * 0.14;
		a = max(rim * 0.9, (t < fill ? 0.34 : 0.1) + chevron);
	} else {
		float r = length(p);
		float ang = abs(atan(p.x, p.y));
		if (r > 1.0 || r < 0.15 || ang > half_angle) discard;
		float rim = smoothstep(0.86, 0.97, r);
		a = max(rim * 0.6, r < fill ? 0.26 : 0.07);
	}
	ALBEDO = tint.rgb;
	ALPHA = clamp(a, 0.0, 1.0) * tint.a * fade;
}";

    sealed class Mark
    {
        public required MeshInstance3D Node;
        public required ShaderMaterial Material;
        public double Start, WindUp;
        public bool Used;
    }

    static Shader? _shader;
    static QuadMesh? _quad;
    // The shader's parameter names, made once (a string would be turned into a StringName at every call).
    static readonly StringName FillName = "fill", FadeName = "fade", ShapeName = "shape", TintName = "tint", HalfAngleName = "half_angle";
    static readonly Color MarkTint = new(1f, 0.22f, 0.04f, 1f), WedgeTint = new(1f, 0.3f, 0.06f, 1f);
    readonly List<Mark> _marks = new(), _wedges = new();
    double _time;
    ulong _fedFrame;

    /// <summary>Marks drawn in the last frame (rings, lanes and wedges).</summary>
    public int Shown { get; private set; }

    Mark NewMark(float shape, Color tint)
    {
        _shader ??= new Shader { Code = ShaderCode };
        _quad ??= new QuadMesh { Size = new Vector2(2f, 2f) };
        var material = new ShaderMaterial { Shader = _shader };
        material.SetShaderParameter(ShapeName, shape);
        material.SetShaderParameter(TintName, tint);
        var node = new MeshInstance3D { Mesh = _quad, MaterialOverride = material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Visible = false };
        AddChild(node);
        return new Mark { Node = node, Material = material };
    }

    Mark FreeMark(float shape, Color tint)
    {
        foreach (var m in _marks) if (!m.Used) { m.Material.SetShaderParameter(ShapeName, shape); m.Material.SetShaderParameter(TintName, tint); return m; }
        if (_marks.Count < MaxMarks) { var m = NewMark(shape, tint); _marks.Add(m); return m; }
        Mark oldest = _marks[0];
        foreach (var m in _marks) if (m.Start < oldest.Start) oldest = m;
        oldest.Material.SetShaderParameter(ShapeName, shape); oldest.Material.SetShaderParameter(TintName, tint);
        return oldest;
    }

    static Basis Flat(float yaw, float sx, float sy) => new Basis(new Vector3(0, 0, 1), yaw) * Basis.FromScale(new Vector3(sx, sy, 1f));

    static Vector3 V(Num.Vector3 v) => new(v.X, v.Y, v.Z);

    /// <summary>A ring of <paramref name="radius"/> around <paramref name="centre"/> (game space) that fills over <paramref name="windUp"/> s.</summary>
    public void Ring(Num.Vector3 centre, float radius, double windUp)
    {
        var m = FreeMark(0f, MarkTint);
        m.Used = true; m.Start = _time; m.WindUp = Math.Max(0.05, windUp);
        m.Node.Transform = new Transform3D(Flat(0f, radius, radius), V(centre) + new Vector3(0, 0, Lift));
        m.Node.Visible = true;
    }

    /// <summary>A lane <paramref name="width"/> wide from <paramref name="from"/> to <paramref name="to"/> (game space) that fills over <paramref name="windUp"/> s.</summary>
    public void Lane(Num.Vector3 from, Num.Vector3 to, float width, double windUp)
    {
        var d = new Vector2(to.X - from.X, to.Y - from.Y);
        float length = d.Length();
        if (length < 0.1f) return;
        var m = FreeMark(1f, MarkTint);
        m.Used = true; m.Start = _time; m.WindUp = Math.Max(0.05, windUp);
        float yaw = MathF.Atan2(-d.X, d.Y);   // the simulation's facing: local +Y toward the end
        var mid = (V(from) + V(to)) / 2f;
        m.Node.Transform = new Transform3D(Flat(yaw, width / 2f, length / 2f), mid + new Vector3(0, 0, Lift));
        m.Node.Visible = true;
    }

    /// <summary>Hides every mark at once (a new run).</summary>
    public void Clear()
    {
        foreach (var m in _marks) { m.Used = false; m.Node.Visible = false; }
        foreach (var m in _wedges) m.Node.Visible = false;
        Shown = 0;
    }

    /// <summary>
    /// The view stops calling <see cref="Frame"/> while it is not connected (its frame returns before the added content's hook): the marks hide themselves
    /// after two frames without one, and show again with the next.
    /// </summary>
    public override void _Process(double delta) => Visible = Engine.GetProcessFrames() - _fedFrame <= 2;

    /// <summary>Per frame: the rings and lanes fill and fade; the wedges follow the enemies winding up a melee blow (not the Warboss's while a ring or lane of its own shows).</summary>
    public void Frame(IReadOnlyList<MusouShown> shown, Num.Vector3 camera, MusouRules rules, float delta)
    {
        _fedFrame = Engine.GetProcessFrames();
        _time += delta;
        int count = 0;
        foreach (var m in _marks)
        {
            if (!m.Used) continue;
            double since = _time - m.Start;
            if (since > m.WindUp + After) { m.Used = false; m.Node.Visible = false; continue; }
            m.Material.SetShaderParameter(FillName, (float)Math.Clamp(since / m.WindUp, 0.0, 1.0));
            m.Material.SetShaderParameter(FadeName, since <= m.WindUp ? 1f : (float)(1.0 - (since - m.WindUp) / After));
            count++;
        }
        int w = 0;
        for (int i = 0; i < shown.Count && w < MaxWedges; i++)
        {
            var e = shown[i];
            if (e.State != MusouEnemyState.WindUp || (int)e.Type >= rules.Types.Count || e.Type == MusouEnemyType.Warboss && count > 0) continue;
            var spec = rules.Types[(int)e.Type];
            if (!spec.Melee || e.StateSeconds > spec.MeleeWindUp + 0.05f || Num.Vector3.DistanceSquared(e.Position, camera) > WedgeRange * WedgeRange) continue;
            if (w >= _wedges.Count) _wedges.Add(NewMark(2f, WedgeTint));
            var m = _wedges[w++];
            float reach = spec.MeleeRange;
            m.Node.Transform = new Transform3D(Flat(e.Yaw, reach, reach), V(e.Position) + new Vector3(0, 0, Lift));
            m.Material.SetShaderParameter(HalfAngleName, Mathf.DegToRad(spec.MeleeConeDegrees / 2f));
            m.Material.SetShaderParameter(FillName, Math.Clamp(e.StateSeconds / MathF.Max(0.05f, spec.MeleeWindUp), 0f, 1f));
            m.Node.Visible = true;
        }
        for (int i = w; i < _wedges.Count; i++) _wedges[i].Node.Visible = false;
        Shown = count + w;
    }
}
