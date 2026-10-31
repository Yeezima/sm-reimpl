using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SmContent;
using SmFormats.Baf;
using SmFormats.Obpk;
using SmSim;

namespace SmClient;

/// <summary>
/// The heavy bolter's brace and the plasma vent shown in the networked view (fidelity gap 7; the simulation is <see cref="Brace"/> and
/// <see cref="Vent"/>, every record carries the phase and the vent since protocol 29):
/// <list type="bullet">
/// <item><b>Brace poses</b> (data: <c>smtool graph-dump --brace</c>): entering, the entry clip (<c>SM_RNG_TB_Idle_to_BraceIdle</c>) at the playback speed
/// that brings its <c>BRANCH_Idle_to_Brace</c> to the simulation's entry time; braced, the braced idle by the aim's pitch (the brace graph's "Aim Frwd
/// Blend": up, forward, down at +80°, 0°, −80°) with the additive braced recoil (<c>SM_RNG_TB_Brace_Fire</c>) after each shot; exiting, the exit clip over
/// the locomotion with the graph's leg mask (the exit blends the locomotion graph on the legs and the root). The horizontal aim blends and the turn
/// clips are not used (the body turns with the aim as elsewhere; interpretation).</item>
/// <item><b>Brace sounds</b>: the wargear's <c>actn_snd_hb_brace_enter</c> / <c>_exit</c> (<c>play_hb_brace_enter</c>, <c>_exit</c>) at the clips'
/// <c>ACTN_SND_HB_Brace_*</c> tags; a braced shot posts the braced ability's fire sound (<c>play_sm_heavy_bolter_fire</c>) instead of the primary's
/// (<see cref="BracedFireSounds"/>).</item>
/// <item><b>Vent poses</b> (<c>SM_Plasma_Vent</c>): the stage's clip over the locomotion's upper body (low and high heat: looping idle-vent clips; the
/// overheat: the additive overheat vent, once). Own stage from the heat (<see cref="Vent.StageOf"/>); others' heat is not on the wire, so they show
/// the low-heat stage (interpretation).</item>
/// <item><b>Vent sounds and effects</b>: each stage's <c>vent_actions</c> (<c>play_sm_plasma_venting_lowheat</c> / <c>_highheat</c> / <c>_overheat</c>,
/// their stop event <c>play_sm_plasma_vents_close</c> when the stage or the vent ends; <c>plasma_gun_venting</c> at the weapon's four vent markers,
/// restarted while the stage lasts).</item>
/// <item><b>HUD</b>: VENTING / BRACING / BRACED / UNBRACING in the weapon box; at high heat the weapon's Reload action text ("R to Vent Heat", the
/// wargear's <c>ui_data/action2_text</c>, locale).</item>
/// </list>
/// <c>--no-brace-vent</c> switches the presentation off (the simulation is unchanged).
/// </summary>
public partial class NetworkView
{
    bool NoBraceVent => _noBraceVent ??= OS.GetCmdlineUserArgs().Contains("--no-brace-vent");
    bool? _noBraceVent;

    sealed record BraceVentData(BraceTuning.Timings? Brace, VentAnimations? Vent, IReadOnlyDictionary<string, string> ReloadTexts, IReadOnlyList<string> Notes);

    Task<BraceVentData>? _braceLoad;
    BraceVentData? _braceData;
    string _braceNote = "not loaded";
    IReadOnlyDictionary<string, float>? _braceUpperBody;
    readonly Dictionary<int, BraceTrack> _braceTracks = new();
    int _braceEntriesSeen, _braceExitsSeen, _ventsSeen, _ventStageSounds, _ventFx, _bracedShots, _braceSounds;
    double _braceLogAt;

    /// <summary>The blend into and out of the brace clips: the graph's "Blend Fast" (Switch Mode to braced, braced to exit), 0.167 s.</summary>
    const float BraceBlend = 5f / 30f;

    sealed class BraceTrack
    {
        public BracePhase Phase;
        public double PhaseSince;
        public bool EnterSound, ExitSound;
        public bool Venting;
        public VentStage Stage;
        public double StageSince, NextFx;
        public string? StopEvent;
    }

    BraceTrack BraceOf(int id)
    {
        if (!_braceTracks.TryGetValue(id, out var t)) _braceTracks[id] = t = new BraceTrack();
        return t;
    }

    /// <summary>A player who left: a vent sound still running gets its stop event, then the track goes.</summary>
    void BraceForget(int id)
    {
        // On a one-shot emitter where the player was: the player's own emitter is stopped this frame (UpdateSound stops gone players' emitters).
        if (_braceTracks.TryGetValue(id, out var t) && t.StopEvent is { Length: > 0 } stop && _sound is not null && _soundTracks.TryGetValue(id, out var track))
            _sound.Post(stop, new SoundEmitter { Position = track.Emitter.Position, OneShot = true, Label = "vent stop" });
        _braceTracks.Remove(id);
    }

    /// <summary>Starts reading the brace and vent clips (the rig's graphs) and the Reload action texts on a worker thread.</summary>
    void SetUpBraceVent()
    {
        if (NoBraceVent || Lean) { _braceNote = Lean ? "not loaded (lean bot)" : "switched off (--no-brace-vent)"; return; }   // perf/lean-bots: poses, sounds and texts only
        if (_config is null || _config.Weapons.All(w => w.Brace is null && w.Vent is null)) { _braceNote = "no brace or vent weapon in the configuration"; return; }
        string dir = _gameDir;
        var names = _config.Weapons.Where(w => w.Brace is not null || w.Vent is not null).Select(w => w.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _braceNote = "loading";
        _braceLoad = Task.Run(() =>
        {
            var notes = new List<string>();
            var (brace, vent) = VentAnimations.LoadPresentation(dir, notes);
            return new BraceVentData(brace, vent, ReloadTexts(dir, names, notes), notes);
        });
    }

    /// <summary>
    /// Each brace or vent weapon's Reload action text: its wargear's <c>ui_data/action2_text</c> ("%ABILITY_RELOAD% to Vent Heat", "… to Change Stance")
    /// from the install's English locale, with the key named (R; the PC binding of Reload in this client).
    /// </summary>
    static IReadOnlyDictionary<string, string> ReloadTexts(string dir, HashSet<string> names, List<string> notes)
    {
        var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var ids = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            using (var archive = ObpkArchive.Open(System.IO.Path.Combine(dir, GameplayTuning.WargearPackage)))
                archive.ReadEntries(e => archive.GroupName(e) == "attr_pc" && names.Contains(e.FileName), (e, d) =>
                {
                    if (BafDocument.Read(d).Root.Table("ui_data")?["action2_text"] is { Type: BafType.Int } t && t.Raw > 0) ids[e.FileName] = t.Raw;
                });
            var locale = SmFormats.Locale.LocaleTable.Open(System.IO.Path.Combine(dir, "data", "locale", "english", "ucs.pc"));
            foreach (var (name, id) in ids)
                if (locale.Get(id) is { Length: > 0 } text) texts[name] = text.Replace("%ABILITY_RELOAD%", "R", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is System.IO.IOException or System.IO.InvalidDataException or UnauthorizedAccessException or KeyNotFoundException)
        {
            notes.Add($"the Reload action texts were not read ({ex.Message})");
        }
        return texts;
    }

    /// <summary>The loaded data, or null while loading or when it failed.</summary>
    BraceVentData? BraceData()
    {
        if (_braceData is not null) return _braceData;
        if (_braceLoad is not { IsCompleted: true } load) return null;
        _braceLoad = null;
        if (load.IsFaulted || load.IsCanceled)
        {
            _braceNote = "not available: " + (load.Exception?.GetBaseException().Message ?? "cancelled");
            GD.PrintErr("brace and vent: " + _braceNote);
            return null;
        }
        _braceData = load.Result;
        if (_braceData.Brace is { } b)
            _braceUpperBody = b.ExitLegBones.ToDictionary(n => n, _ => 0f, StringComparer.OrdinalIgnoreCase);
        _braceNote = $"brace {(_braceData.Brace is { } bt ? $"{bt.EnterClip} / {bt.Idle?.Forward ?? "no idle"} / {bt.ExitClip}" : "not read")}; " +
                     $"vent {(_braceData.Vent is { } v ? string.Join(", ", v.Clips.Select(kv => $"{kv.Key} {kv.Value.Clip}")) : "not read")}; " +
                     $"Reload texts {_braceData.ReloadTexts.Count}";
        GD.Print("brace and vent: " + _braceNote);
        foreach (var n in _braceData.Notes) GD.Print("  brace and vent note: " + n);
        return _braceData;
    }

    /// <summary>
    /// Seconds into the stance's current transition: others' since the phase was seen; the own player's from the simulation's ticks, drawn between the
    /// last two predicted ticks by the fraction of a tick elapsed (as the own model's position, M36), so it does not step at 30 Hz.
    /// </summary>
    double BraceElapsed(PlayerState state, bool own, BraceTrack t) =>
        own ? Math.Max(0.0, (state.BraceTicks - 1 + (_client?.ClipFraction ?? 1f)) * _config!.TickSeconds) : _connectedFor - t.PhaseSince;

    /// <summary>
    /// From <see cref="Place"/>, after the throw and before the landing poses: the stance's pose (see the class summary). False when the player is
    /// not in a stance or the data is not there (the usual pose follows).
    /// </summary>
    bool BracePose(CharacterViewer character, PlayerState state, bool own)
    {
        if (NoBraceVent || !state.Alive || state.Brace == BracePhase.None || _config is null) return false;
        if (BraceData()?.Brace is not { } b || Loadouts.InHand(_config, state)?.Brace is not { } spec) return false;
        var t = BraceOf(state.PlayerId);
        float elapsed = (float)Math.Max(0.0, BraceElapsed(state, own, t));
        switch (state.Brace)
        {
            case BracePhase.Entering:
                return character.Pose(b.EnterClip, elapsed * b.EnterRate(spec.EnterTime), BraceBlend);
            case BracePhase.Braced:
                if (b.Idle is not { } idle) return false;
                float pitchT = Math.Clamp(state.Pitch * (180f / MathF.PI) / 80f, -1f, 1f);   // the blend's points are ±80°
                if (!character.Stance("brace idle", idle.Down, idle.Forward, idle.Up, pitchT, 1f, BraceBlend)) return false;
                double since = _connectedFor - HipOf(state.PlayerId).RecoilAt;
                if (b.FireClip is { } fire && _assets?.Animation(fire) is { } recoil && since >= 0.0 && since < recoil.Animation.Duration)
                    character.SetPoseLayer(fire, (float)since, RecoilWeight(state), additive: true);
                return true;
            case BracePhase.Exiting:
                PlaceLocomotion(character, state, own);
                character.SetPoseLayer(b.ExitClip, elapsed * b.ExitRate(spec.ExitTime), 1f, additive: false, _braceUpperBody);
                return true;
            default:
                return false;
        }
    }

    /// <summary>From <see cref="Place"/> after the locomotion: the vent stage's clip over the upper body (the overheat's added).</summary>
    void VentPose(CharacterViewer character, PlayerState state, bool own)
    {
        if (NoBraceVent || !state.Alive || !state.Venting || _config is null || BraceData()?.Vent is not { } anims) return;
        var t = BraceOf(state.PlayerId);
        var stage = t.Stage == VentStage.NoHeat ? VentStage.Low : t.Stage;
        if (!anims.Clips.TryGetValue(stage, out var clip) && !anims.Clips.TryGetValue(VentStage.Low, out clip)) return;
        if (_assets?.Animation(clip.Clip) is not { } a) return;
        double since = Math.Max(0.0, _connectedFor - t.StageSince);
        float length = MathF.Max(a.Animation.Duration, 1e-3f);
        float time = clip.Looping ? (float)(since % length) : (float)Math.Min(since, length);
        character.SetPoseLayer(clip.Clip, time, 1f, clip.Additive, clip.Additive ? null : _braceUpperBody);
    }

    /// <summary>The fire sounds of a braced shot: the braced ability's <c>fire_audio</c> (null when the file has none: the primary's then play).</summary>
    IReadOnlyList<SoundBinding>? BracedFireSounds(string wargear) =>
        _soundTriggers?.File(wargear) is { } f && f.Of("fire_audio").Where(b => b.Ability == WeaponTuning.BracedAbility).ToList() is { Count: > 0 } list ? list : null;

    /// <summary>Whether another player is braced in this frame's remote states (read before the tracks are updated, so a stance seen this frame counts for its first shot).</summary>
    bool BracedNow(int id)
    {
        foreach (var o in _lastOthers) if (o.PlayerId == id) return o.Alive && o.Brace == BracePhase.Braced;
        return _braceTracks.TryGetValue(id, out var t) && t.Phase == BracePhase.Braced;
    }

    /// <summary>Once per frame after the characters are placed: the stance's and the vent's sounds and effects, and the bot log's counters.</summary>
    void UpdateBraceVent(PlayerState me, IReadOnlyList<PlayerState> others)
    {
        if (NoBraceVent || _config is null || _client is null) return;
        var data = BraceData();
        BraceVentPlayer(me, own: true, data);
        foreach (var o in others) BraceVentPlayer(o, own: false, data);
        foreach (var (_, ev) in _frameOwnEvents) if (ev.Fired && me.Brace == BracePhase.Braced) _bracedShots++;
        if (_bot && _connectedFor - _braceLogAt >= 10.0)
        {
            _braceLogAt = _connectedFor;
            GD.Print($"t {_connectedFor:F0} s: id {_client.PlayerId} brace/vent: own {me.Brace}{(me.Venting ? " venting" : "")} heat {me.Heat:F1}{(me.Overheated ? " OVERHEATED" : "")}; " +
                     $"seen entries {_braceEntriesSeen}, exits {_braceExitsSeen}, braced own shots {_bracedShots}, brace sounds {_braceSounds}; vents {_ventsSeen}, stage sounds {_ventStageSounds}, vent effects {_ventFx}");
        }
    }

    void BraceVentPlayer(PlayerState s, bool own, BraceVentData? data)
    {
        var t = BraceOf(s.PlayerId);
        var weapon = Loadouts.InHand(_config!, s);
        var phase = s.Alive ? s.Brace : BracePhase.None;
        if (phase != t.Phase)
        {
            if (phase == BracePhase.Entering) _braceEntriesSeen++;
            if (t.Phase == BracePhase.Exiting && phase == BracePhase.None) _braceExitsSeen++;
            t.Phase = phase;
            t.PhaseSince = _connectedFor - (own ? s.BraceTicks * _config!.TickSeconds : 0.0);
            t.EnterSound = t.ExitSound = false;
        }
        // The stance's sounds at the clips' tags.
        if (data?.Brace is { } b && weapon?.Brace is { } spec && _soundTriggers?.File(weapon.Name) is { } file && (phase is BracePhase.Entering or BracePhase.Exiting))
        {
            bool entering = phase == BracePhase.Entering;
            float clipTime = (float)BraceElapsed(s, own, t) * (entering ? b.EnterRate(spec.EnterTime) : b.ExitRate(spec.ExitTime));
            float at = (entering ? b.EnterSoundAt * b.EnterLength : b.ExitSoundAt * b.ExitLength);
            ref bool done = ref (entering ? ref t.EnterSound : ref t.ExitSound);
            if (!done && float.IsFinite(at) && clipTime >= at)
            {
                done = true;
                PostAll(file.Tag(entering ? "actn_snd_hb_brace_enter" : "actn_snd_hb_brace_exit"), Track(s.PlayerId, own).Emitter, own, "brace");
                _braceSounds++;
            }
        }
        // The vent: its stage, the stage's sounds and effects.
        bool venting = s.Alive && s.Venting && weapon is { Vent: not null, Heat: not null };
        var stage = !venting ? VentStage.NoHeat : own ? Vent.StageOf(weapon!.Vent!, weapon.Heat!, s.Heat, s.Overheated) : VentStage.Low;
        if (venting && stage == VentStage.NoHeat) stage = VentStage.Low;
        if (venting && !t.Venting) _ventsSeen++;
        if (stage != t.Stage || venting != t.Venting)
        {
            if (t.StopEvent is { Length: > 0 } stop && _sound is not null) _sound.Post(stop, Track(s.PlayerId, own).Emitter);
            t.StopEvent = null;
            t.Stage = stage; t.StageSince = _connectedFor; t.NextFx = _connectedFor;
            if (venting && _soundTriggers?.File(weapon!.Name) is { } vf)
            {
                foreach (var bnd in vf.Bindings.Where(x => x.IsDefault && x.Trigger == "vent_actions" && x.Ability == weapon.Vent!.Ability && StageMatches(x.Path, stage)))
                {
                    PostBinding(bnd, Track(s.PlayerId, own).Emitter, own, "vent");
                    if (bnd.StopEvent.Length > 0) t.StopEvent = bnd.StopEvent;
                    _ventStageSounds++;
                }
            }
        }
        t.Venting = venting;
        if (venting && _fx is not null && _catalogue?.Weapon(weapon!.Name) is { } fx && _connectedFor >= t.NextFx)
        {
            double life = 1.0;
            foreach (var p in fx.Bindings.Where(x => x.IsDefault && x.Play is not null && x.Ability == weapon.Vent!.Ability && x.Path.Contains("/vent_stages/", StringComparison.Ordinal) && StageMatches(x.Path, stage)).Select(x => x.Play!))
            {
                if (FxPlace(s.PlayerId, p.Marker) is not { } place) continue;
                int id = s.PlayerId;
                string marker = p.Marker;
                if (_fx.Play(p.Name, place.Position, place.Axes, p.Scale <= 0f ? 1f : p.Scale, null, p.Attached ? () => FxPlace(id, marker) : null, p.Lifetime) is { } e)
                {
                    life = Math.Min(life, double.IsFinite(e.Life) ? Math.Max(0.2, e.Life - 0.05) : 1.0);
                    _ventFx++;
                }
            }
            t.NextFx = _connectedFor + life;
        }
    }

    bool BotBraceVent => _botBraceVent ??= OS.GetCmdlineUserArgs().Contains("--bot-brace");
    bool? _botBraceVent;
    double _botBraceFireAt = double.NegativeInfinity;
    int _botBracePresses, _botVentPresses;

    /// <summary>
    /// Test aid (<c>--bot-brace</c>, with <c>--bot</c>): the bot uses Reload as a player would. Heavy bolter: it braces when it starts firing at a
    /// target and leaves the stance 2 s after its last trigger pull. Plasma weapons: it vents when its heat is at half or more and it has not pulled the trigger for 1 s (an
    /// overheat vents by itself). The key is pressed only after a tick with it released (the simulation acts on the press edge). Ours.
    /// </summary>
    PlayerInput BotBraceVentInput(PlayerInput input)
    {
        if (!BotBraceVent || _client is null || _config is null) return input;
        var me = _client.Predicted;
        if (!me.Alive || Loadouts.InHand(_config, me) is not { } w || me.ReloadHeld) return input;
        bool firing = (input.Buttons & InputButtons.Fire) != 0;
        if (firing) _botBraceFireAt = _connectedFor;
        bool press = false;
        if (w.Brace is not null)
            press = (me.Brace == BracePhase.None && firing) || (me.Brace == BracePhase.Braced && _connectedFor - _botBraceFireAt > 2.0);
        else if (w.Vent is not null && w.Heat is { } h)
            press = _connectedFor - _botBraceFireAt > 1.0 && !me.Venting && !me.Overheated && me.Heat >= 0.5f * h.Overheat;   // 1 s after its last trigger pull (a semi-automatic bot releases every other tick)
        if (!press) return input;
        if (w.Brace is not null) _botBracePresses++; else _botVentPresses++;
        if (_bot && (_botBracePresses + _botVentPresses) % 10 == 1)
            GD.Print($"t {_connectedFor:F1} s: id {_client.PlayerId} bot presses Reload ({(w.Brace is not null ? $"brace, {me.Brace}" : $"vent at heat {me.Heat:F0}")}; presses brace {_botBracePresses}, vent {_botVentPresses})");
        return input with { Buttons = input.Buttons | InputButtons.Reload };
    }

    /// <summary>Whether a vent binding's path is the stage's (<c>vent_stages/&lt;n&gt;. low_heat</c>, <c>high_heat</c>, <c>overheat</c>).</summary>
    static bool StageMatches(string path, VentStage stage)
    {
        string key = stage switch { VentStage.Low => "low_heat", VentStage.High => "high_heat", VentStage.Overheat => "overheat", _ => "no_heat" };
        foreach (var part in path.Split('/'))
            if (part.EndsWith(key, StringComparison.OrdinalIgnoreCase) && part.Contains(". ", StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>The HUD's word for the weapon's stance or vent (null: none) and the Reload hint at high heat (the weapon's action text).</summary>
    (string? State, string? Hint) BraceVentHud(PlayerState me, WeaponSpec inHand)
    {
        string? state = me.Venting ? "VENTING" : inHand.Brace is null ? null : me.Brace switch
        {
            BracePhase.Entering => "BRACING", BracePhase.Braced => "BRACED", BracePhase.Exiting => "UNBRACING", _ => null,
        };
        string? hint = null;
        if (!NoBraceVent && inHand.Vent is not null && inHand.Heat is { } h && !me.Venting && !me.Overheated && me.Heat >= h.HighHeat
            && _braceData?.ReloadTexts.GetValueOrDefault(inHand.Name) is { Length: > 0 } text)
            hint = text.ToUpperInvariant();
        return (state, hint);
    }
}
