using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Godot;
using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// M14 (protocol 9): the jump pack in the networked view. Space is the Jump button, sent as held every tick while down
/// (the hold length sets the height; the simulation finds the press edge and counts the hold); a melee press (V / middle
/// mouse) in the air starts the ground pound. Animation uses the original clips of the jump-pack behavior graph
/// <c>MP_SM_NAV_JP_Sim</c> (<see cref="SetUpJump"/>): take-off, flight by stick direction and aim, the pound's dive,
/// impact and get-up, and the run / idle landings. The own player's view follows its predicted jump phase; other
/// players' follows their slim flags (jumping, pounding), Grounded and velocity (<see cref="TrackJump"/>), with the
/// landing and get-up timed on the client. The choice of clip, its timing, the HUD readout and the rings drawn at
/// impacts and landings are a **placeholder demonstration**; the pound and landing blasts are not on the wire, so their
/// rings are drawn where the client sees a jump end (own player: predicted; others: inferred from their flags), sized by
/// the blasts' radii from the install.
/// </summary>
public partial class NetworkView
{
    /// <summary>What a marine's jump looks like now (client view; see <see cref="TrackJump"/>).</summary>
    enum JumpView : byte { None, TakeOff, Air, Dive, Impact, Landing, Getup }

    /// <summary>Per player: the jump view, when it started, and the heights of the current jump.</summary>
    sealed class JumpTrack
    {
        public JumpView View;
        public double Since;        // connected time the view started
        public double PressAt;      // connected time of the take-off's press (estimated for other players)
        public double ImpactAt;     // connected time of the pound's impact
        public float LiftZ, ApexZ;  // height at the lift-off and the highest point since (game Z, metres)
        public bool LandMoving;     // the landing is the run landing
        public bool ApexLogged;
        // M45: the flight clip's pose (JumpFlightPose): the damped time fraction (NaN: a new flight), when it was last stepped, the low set shown.
        public float Fraction = float.NaN;
        public double FractionAt;
        public bool Low;
        // Animation pass 2: the pound's get-up window (JumpGetupDecide): decided once per pound; the run get-up when moving.
        public bool GetupDecided, GetupRun;
        // Protocol 30: the dive (and its impact and get-up) is the sword slash's; when the dive started (connected time).
        public bool Slash;
        public double DiveAt;
        // feel/jump-pack: the slash's hit mark was laid for this slash.
        public bool SlashMarked;
        // feel/jump-pack-2: the slash's dive's expected length (s; SmNet.SlashPose.ExpectedDive at its start), which sets the wind-up's rate.
        public float DiveExpected = float.PositiveInfinity;
        // feel/jump-pack-2: the wind-up's clip time when the dive landed (the impact eases from it to the strike's; NaN: none).
        public float LandedClip = float.NaN;
        // anim/air-slash-character: the wind-up's rate at the landing (clip s per s; NaN: none, the old catch-up).
        public float LandedRate = float.NaN;
        // anim/feel-pass (NetworkView.Feel.cs): the slash's follow-through handed to the upper body when the marine moved on (connected time; NaN: none) and that slash's impact time.
        public double SlashHandAt = double.NaN, SlashHandImpactAt;
    }

    // Clip names of the jump-pack behavior graph (docs/discovery.md, M14).
    const string TakeOffClipName = FlightPoseRules.TakeOffClip;   // fix/hitboxes: shared with the server's hit volumes
    static readonly string[] DiveClipNames = ["SM_JMP_GP_Loop90Degrees_01", "SM_JMP_GP_Loop30Degrees_01"];
    const string ImpactClipName = "SM_JMP_GP_Impact_01";
    const string GetupClipName = "SM_JMP_GP_ReturnToIdle";
    const string LandRunClipName = "SM_JMP_Landing_LowRun";
    const string LandIdleClipName = "SM_JMP_Landing_Low";
    /// <summary>The take-off clip's <c>BRANCH_To_Jump</c> (s after the press): the flight clips take over from here.</summary>
    const float TakeOffBranch = FlightPoseRules.TakeOffBranch;
    /// <summary>Pitch (radians) beyond which a flight clip's aim is up or down rather than level (client choice).</summary>
    const float FlightAimThreshold = FlightPoseRules.AimThreshold;
    /// <summary>Horizontal speed (m/s) relative to the facing below which the neutral (U) flight clip is used (client choice).</summary>
    const float FlightNeutralSpeed = FlightPoseRules.NeutralSpeed;
    const double RingSeconds = 0.7;             // how long an impact / landing ring fades (client-side, not an original value)

    // Bot jump script (test aid, not original behaviour).
    const float BotJumpMin = 8f, BotJumpMax = 30f;   // metres (horizontal) to the target in which the bot jumps at it
    const float BotPoundDistance = 6f;               // metres (horizontal) within which the bot starts a pound in the air
    const float BotPoundMinHeight = 2.5f;            // metres above the target's feet the bot must be to pound
    const double BotJumpEvery = 2.0;                 // seconds between the bot's jump presses at least
    const double BotFreeJumpEvery = 5.0;            // --bot-jump-free: seconds between free jumps at least
    const float BotFreeJumpHold = 0.75f;             // --bot-jump-free: hold (s) of a free jump (about 13 m on the install; M45: was 0.25 s before a release cut the rise)
    readonly bool _botJumpFree;
    const float BotJumpClearance = 8f;               // metres free above the head the bot wants before jumping (on a map)

    string? _jmpTakeOff, _jmpDive, _jmpImpact, _jmpGetup, _jmpLandRun, _jmpLandIdle;
    readonly Dictionary<(char Dir, char Aim), string> _jmpFlight = new();
    /// <summary>M45: the low flight set (<c>SM_JMP_ZeroLo_*</c>, the graph's 'Jump Move Blend' weight 9) beside the high one above (<c>SM_JMP_Zero_*</c>, weight 18).</summary>
    readonly Dictionary<(char Dir, char Aim), string> _jmpFlightLo = new();
    /// <summary>M45: the map's collision, for the flight pose's height above the ground (null: the high set).</summary>
    CollisionWorld? _jumpGround;
    int _lastPoseDecile = -1;   // bot log: the own flight pose's last tenth logged
    /// <summary>M45 test aid <c>--loop-flight</c>: the M14 flight (the clip looping, not posed by the vertical angle), for before/after comparisons.</summary>
    static readonly bool LoopFlight = OS.GetCmdlineUserArgs().Contains("--loop-flight");
    readonly Dictionary<int, JumpTrack> _jumpTracks = new();
    readonly List<(MeshInstance3D Node, StandardMaterial3D Material, double Age)> _rings = new();
    readonly bool _waitJump;
    uint _botJumpHoldUntil;
    bool _botJumpHolding;
    double _botLastJump = double.NegativeInfinity;
    int _liftOffs, _pounds, _impacts, _landings, _remoteLiftOffs, _remotePounds, _remoteImpacts, _remoteLandings;
    float _lastApex;

    /// <summary>
    /// Resolves the jump-pack clips against the rig (exact name, else the shortest name starting with it followed by a
    /// non-letter) and checks they decode; a missing one is noted and its view falls back (flight to another flight clip
    /// or the take-off, everything else to the idle / run choice). Flight clips are every
    /// <c>SM_JMP_Zero*_&lt;F|B|L|R|U&gt;_Aim_&lt;U|F|D&gt;</c> (thunder-hammer <c>_TH_</c> variants left out), the shortest per pair.
    /// </summary>
    void SetUpJump()
    {
        var names = _assets!.Model.Rig.AnimationNames;
        string? Resolve(string name, string what)
        {
            string? found = names.FirstOrDefault(n => n.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? names.Where(n => n.StartsWith(name, StringComparison.OrdinalIgnoreCase) && n.Length > name.Length && !char.IsLetter(n[name.Length]))
                        .OrderBy(n => n.Length).FirstOrDefault();
            if (found is null) { _notes.Add($"jump animation '{name}' ({what}) not in the rig; {what} falls back"); return null; }
            if (_assets.Animation(found) is null) { _notes.Add($"jump animation '{found}' ({what}) could not be decoded; {what} falls back"); return null; }
            return found;
        }
        _jmpTakeOff = Resolve(TakeOffClipName, "take-off");
        foreach (var dive in DiveClipNames) if ((_jmpDive = Resolve(dive, "dive")) is not null) break;
        _jmpImpact = Resolve(ImpactClipName, "pound impact");
        _jmpGetup = Resolve(GetupClipName, "pound get-up");
        _jmpLandRun = Resolve(LandRunClipName, "run landing");
        _jmpLandIdle = Resolve(LandIdleClipName, "idle landing");
        // M45: the high set (SM_JMP_Zero_*) and the low set (SM_JMP_ZeroLo_*) apart; before M45 the shortest name of either won (the high set).
        var pattern = new Regex(@"^SM_JMP_Zero(Lo)?_.*?([FBLRU])_Aim_([UFD])(_\d+)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        foreach (var n in names.Where(n => !n.Contains("_TH_", StringComparison.OrdinalIgnoreCase)).OrderBy(n => n.Length))
        {
            var m = pattern.Match(n);
            if (!m.Success) continue;
            var key = (char.ToUpperInvariant(m.Groups[2].Value[0]), char.ToUpperInvariant(m.Groups[3].Value[0]));
            var set = m.Groups[1].Success ? _jmpFlightLo : _jmpFlight;
            if (set.ContainsKey(key)) continue;
            if (_assets.Animation(n) is null) { _notes.Add($"flight animation '{n}' could not be decoded; left out"); continue; }
            set[key] = n;
        }
        if (_jmpFlight.Count == 0) foreach (var (k, v) in _jmpFlightLo) _jmpFlight[k] = v;   // only a low set: it is the high one too
        if (_jmpFlight.Count == 0) _notes.Add("no SM_JMP_Zero*_<dir>_Aim_<aim> flight animation in the rig; flight shows the take-off clip");
        else if (_jmpFlight.Count < 15) _notes.Add($"{_jmpFlight.Count} of 15 flight animations found; missing pairs use the nearest one");
    }

    /// <summary>The jump clips chosen, for the start-up log.</summary>
    string JumpClipsText() =>
        $"take-off {_jmpTakeOff ?? "none"}, dive {_jmpDive ?? "none"}, impact {_jmpImpact ?? "none"}, get-up {_jmpGetup ?? "none"}, " +
        $"landing run {_jmpLandRun ?? "none"} / idle {_jmpLandIdle ?? "none"}, flight {_jmpFlight.Count} high + {_jmpFlightLo.Count} low (posed by the vertical angle{(LoopFlight ? "; --loop-flight: looping instead" : "")}): " +
        string.Join(", ", _jmpFlight.OrderBy(k => k.Key.Dir).ThenBy(k => k.Key.Aim).Select(k => $"{k.Key.Dir}{k.Key.Aim}={k.Value}"));

    float ClipLength(string? clip, float fallback) => clip is not null && _assets?.Animation(clip) is { } a ? a.Animation.Duration : fallback;

    /// <summary>
    /// The flight clip for a marine in the air: direction from its horizontal velocity relative to its facing (F/B/L/R,
    /// or U below <see cref="FlightNeutralSpeed"/>), aim from its pitch (U/F/D beyond ±<see cref="FlightAimThreshold"/>).
    /// A missing pair falls back to the level aim, then the neutral direction, then any flight clip.
    /// </summary>
    string? FlightClip(PlayerState s, bool low = false)
    {
        if (low && _jmpFlightLo.Count > 0) return FlightClipIn(_jmpFlightLo, s);
        return FlightClipIn(_jmpFlight, s);
    }

    string? FlightClipIn(Dictionary<(char Dir, char Aim), string> set, PlayerState s)
    {
        if (set.Count == 0) return null;
        // fix/hitboxes: the keys in lookup order come from the rule the server's hit volumes use (FlightPoseRules.Candidates).
        var (k1, k2, k3, k4) = FlightPoseRules.Candidates(s);
        foreach (var key in new[] { k1, k2, k3, k4 })
            if (set.TryGetValue(key, out var clip)) return clip;
        return set.Values.First();
    }

    /// <summary>
    /// Follows one player's jump for animation, rings and the log. Own player (<paramref name="own"/>): its predicted
    /// <see cref="PlayerState.Jump"/>. Others: the slim record's representative phase (Ascent = jumping: TakeOff while
    /// grounded, else Air; Dive = pounding: Dive in the air, Impact on the ground). A jump that ends (own: Landed or None;
    /// others: the flag falls) shows the landing until its clip ends or the marine runs off after the run landing's move
    /// lock; a pound that ends shows the rest of the impact clip and the get-up clip, unless the marine runs off.
    /// </summary>
    void TrackJump(PlayerState s, bool own)
    {
        if (!_jumpTracks.TryGetValue(s.PlayerId, out var t)) _jumpTracks[s.PlayerId] = t = new JumpTrack();
        if (!s.Alive) { t.View = JumpView.None; return; }
        float speed = MathF.Sqrt(s.Velocity.X * s.Velocity.X + s.Velocity.Y * s.Velocity.Y);
        double now = _connectedFor;
        var raw = own
            ? s.Jump switch
            {
                JumpPhase.TakeOff => JumpView.TakeOff,
                JumpPhase.Ascent or JumpPhase.Hang or JumpPhase.Fall => JumpView.Air,
                JumpPhase.Dive => JumpView.Dive,
                JumpPhase.Recover => JumpView.Impact,
                _ => JumpView.None,   // Landed included: the landing below
            }
            : JumpPack.Jumping(s.Jump) ? (s.Grounded ? JumpView.TakeOff : JumpView.Air)
            : JumpPack.Pounding(s.Jump) ? (s.Grounded ? JumpView.Impact : JumpView.Dive)
            : JumpView.None;
        var prev = t.View;
        // anim/feel-pass: a remote pound's dive and recovery share one flag, told apart by the grounded bit; a recovery that leaves the ground for a tick (the slash's landing
        // lunge over an edge, a step) went back to the dive and restarted the slash clip at its wind-up. Once landed, the pound stays landed until its flag drops.
        if (!own && !CharacterViewer.NoFeel && raw == JumpView.Dive && prev is JumpView.Impact) raw = JumpView.Impact;
        var next = raw != JumpView.None ? raw : prev switch
        {
            JumpView.Air or JumpView.Landing => JumpView.Landing,
            JumpView.Dive or JumpView.Impact or JumpView.Getup => JumpView.Getup,
            _ => JumpView.None,
        };
        // An own take-off that ends on the ground without lifting off (death, or a missed tick) is no landing.
        if (prev == JumpView.TakeOff && raw == JumpView.None) next = JumpView.None;
        // sim/movement-fixes: a dash started in the air (a Space tap at the apex or in the fall) ends the jump with no landing: its flight and landing are the evade's.
        if (prev == JumpView.Air && raw == JumpView.None && Evade.Evading(s) && !s.Grounded) next = JumpView.None;

        if (next != prev)
        {
            t.Since = now; t.Fraction = float.NaN;   // M45: a new view starts its flight pose at the target
            if (next != JumpView.Getup) { t.GetupDecided = false; t.GetupRun = false; }   // pass 2: a new pound decides again
            string who = own ? $"id {_client!.PlayerId}" : $"id {_client!.PlayerId} sees P{s.PlayerId}";
            var pack = Loadouts.JumpPack(_config!, s);
            switch (next)
            {
                case JumpView.TakeOff:
                    t.PressAt = now; t.LiftZ = t.ApexZ = s.Position.Z; t.ApexLogged = false;
                    break;
                case JumpView.Air when own && prev == JumpView.TakeOff && s.Jump == JumpPhase.Fall:
                    // Left the ground during the take-off: the jump became a fall (no lift-off).
                    t.LiftZ = t.ApexZ = s.Position.Z; t.ApexLogged = false;
                    if (_bot) GD.Print($"t {now:F1} s: {who} fell off a ledge during the take-off (no lift-off; power left {s.JumpPower:F0})");
                    break;
                case JumpView.Air:
                    if (prev != JumpView.TakeOff) t.PressAt = now - (pack?.TakeOffDelay ?? 0.2f);   // the press was not seen
                    t.LiftZ = t.ApexZ = s.Position.Z; t.ApexLogged = false;
                    if (own) _liftOffs++; else _remoteLiftOffs++;
                    if (_bot) GD.Print($"t {now:F1} s: {who} lift off at {s.Position.X:F1},{s.Position.Y:F1},{s.Position.Z:F1} " +
                                       $"(vertical {s.Velocity.Z:F1} m/s, horizontal {speed:F1} m/s{(own ? $", power left {s.JumpPower:F0}{(s.JumpHolding ? ", still holding" : "")}" : "")}; lift-offs {(own ? _liftOffs : _remoteLiftOffs)}{(own ? "" : " seen")}; " +
                                       $"ceiling {CeilingAbove(s):F1} m above the head)");
                    break;
                case JumpView.Dive:
                    t.Slash = s.JumpSlash; t.DiveAt = now; t.SlashMarked = false;   // protocol 30: the sword slash's dive (NetworkView.DashAttack.cs)
                    t.DiveExpected = SmNet.SlashPose.ExpectedDive(_jumpGround is null ? MathF.Max(0f, s.Position.Z) : AboveGround(s), s.Velocity.Z);   // feel/jump-pack-2: the wind-up's rate (no map: the flat ground at 0)
                    if (t.Slash) { SlashStarted(s, own, s.Position.Z - t.LiftZ); break; }
                    if (own) _pounds++; else _remotePounds++;
                    if (_bot) GD.Print($"t {now:F1} s: {who} start a ground pound {s.Position.Z - t.LiftZ:F1} m above the lift-off (apex {t.ApexZ - t.LiftZ:F1} m; dive velocity {s.Velocity.X:F1},{s.Velocity.Y:F1},{s.Velocity.Z:F1}; pounds {(own ? _pounds : _remotePounds)}{(own ? "" : " seen")})");
                    break;
                case JumpView.Impact:
                case JumpView.Getup when prev == JumpView.Dive:   // the recovery was not seen
                    if (t.Slash && prev == JumpView.Dive)
                    {
                        // anim/air-slash-character: where the wind-up was and how fast it ran at the landing (the strike curve carries on from there to the hit).
                        t.LandedClip = SlashDiveTimeOf(s, (float)(now - t.DiveAt), t.DiveExpected);
                        t.LandedRate = SlashDiveRateOf(s, (float)(now - t.DiveAt), t.DiveExpected);
                    }
                    else t.LandedClip = t.LandedRate = float.NaN;
                    t.ImpactAt = now;
                    t.Slash |= s.JumpSlash;
                    if (t.Slash) { SlashLanded(s, own, pack, MathF.Max(0f, t.ApexZ - s.Position.Z)); break; }   // protocol 30: no pound blast, crater or ring
                    if (own) _impacts++; else _remoteImpacts++;
                    if (pack?.Pound is { } pound) AddRing(s.Position, pound.Radius, new Color(1f, 0.45f, 0.15f));   // the placeholder ring: --show-markers only
                    PoundImpactPresentation(s, own, pack);   // M41: the original impact effect, the dark ring, the chunks (NetworkView.PoundImpact.cs)
                    if (_bot) GD.Print($"t {now:F1} s: {who} pound impact at {s.Position.X:F1},{s.Position.Y:F1},{s.Position.Z:F1} after a {t.ApexZ - s.Position.Z:F1} m drop " +
                                       $"(blast radius {(pack?.Pound is { } b ? b.Radius.ToString("F1") : "none")}; impacts {(own ? _impacts : _remoteImpacts)}{(own ? "; predicted" : " seen; inferred from the flags")})");
                    break;
                case JumpView.Landing:
                {
                    float drop = MathF.Max(0f, t.ApexZ - s.Position.Z);
                    t.LandMoving = own && s.Jump == JumpPhase.Landed ? s.JumpLandMoving : speed > RunThreshold;
                    if (own) _landings++; else _remoteLandings++;
                    var blast = pack is null ? null : JumpPack.LandingBlast(pack, drop);   // the server's rule
                    if (blast is not null) AddRing(s.Position, blast.Radius, new Color(0.95f, 0.85f, 0.4f));
                    LandingPresentation(s, own, pack, drop);   // M41: the data's landing effect for the drop
                    if (_bot) GD.Print($"t {now:F1} s: {who} land at {s.Position.X:F1},{s.Position.Y:F1},{s.Position.Z:F1} after a {drop:F1} m drop (apex {t.ApexZ - t.LiftZ:F1} m above the lift-off; " +
                                       $"{(t.LandMoving ? "run" : "idle")} landing; blast radius {(blast is null ? "none" : blast.Radius.ToString("F1"))}; landings {(own ? _landings : _remoteLandings)}{(own ? "; predicted" : " seen")})");
                    break;
                }
            }
            t.View = next;
        }
        if (next is JumpView.Air or JumpView.Dive or JumpView.TakeOff) t.ApexZ = MathF.Max(t.ApexZ, s.Position.Z);
        // feel/jump-pack: the slash's hit mark when its cone resolves (client time from the impact seen).
        if (t.Slash && !t.SlashMarked && t.View is JumpView.Impact or JumpView.Getup && now - t.ImpactAt >= (Loadouts.JumpPack(_config!, s)?.Slash?.HitTime ?? 0.133f))
        {
            t.SlashMarked = true;
            SlashHitPresentation(s, own, Loadouts.JumpPack(_config!, s));
        }
        if (next == JumpView.None) t.Slash = false;
        if (own && !t.ApexLogged && s.Jump is JumpPhase.Hang or JumpPhase.Dive && t.View is JumpView.Air or JumpView.Dive)
        {
            t.ApexLogged = true;
            _lastApex = t.ApexZ - t.LiftZ;
            if (_bot) GD.Print($"t {now:F1} s: id {_client!.PlayerId} apex {_lastApex:F2} m above the lift-off ({(s.Jump == JumpPhase.Dive ? "pound started on the way up" : "hang starts")}; power {s.JumpPower:F0}; cut {s.JumpCut}, holding {s.JumpHolding})");
        }

        // Endings timed on the client.
        double age = now - t.Since;
        if (t.View == JumpView.Landing)
        {
            var pack = Loadouts.JumpPack(_config!, s);
            float moveLock = pack is null ? 0f : JumpPack.MoveLock(pack, t.LandMoving, _config);
            if (age >= ClipLength(t.LandMoving ? _jmpLandRun : _jmpLandIdle, 0.9f) || (age >= moveLock && speed > RunThreshold)) t.View = JumpView.None;
        }
        else if (t.View == JumpView.Getup)
        {
            double sinceImpact = now - t.ImpactAt;
            if (t.Slash)
            {
                double slashEnds = SlashAfterImpact(s);   // feel/jump-pack-2: the clip from the impact's crop start to its end
                if (sinceImpact >= slashEnds || speed > RunThreshold)
                {
                    if (speed > RunThreshold) SlashMovedOn(t, now, sinceImpact, slashEnds);   // anim/feel-pass: the follow-through goes on over the upper body (NetworkView.Feel.cs)
                    t.View = JumpView.None;
                }
            }
            else
            {
                JumpGetupDecide(t, sinceImpact, speed, s);   // pass 2: moving at the graph's get-up window = the run get-up (SM_JMP_Landing_MedRun) over its own length
                if (t.GetupRun && UpperJump is { } up) { if (sinceImpact - up.FromImpact >= up.RunEnd) t.View = JumpView.None; }
                else if (sinceImpact >= ClipLength(_jmpImpact, 1.067f) + ClipLength(_jmpGetup, 0f) || (speed > RunThreshold && (UpperJump is null || t.GetupDecided))) t.View = JumpView.None;
            }
        }
    }

    /// <summary>
    /// The jump clip to show and its time (null time: play it looping), or null for the normal choice. With
    /// <paramref name="airborneOnly"/>, only take-off, flight, dive and impact (which win over a throw); otherwise the
    /// landing and get-up (which lose to it).
    /// </summary>
    (string Clip, float? Time)? JumpPose(PlayerState s, bool airborneOnly)
    {
        if (!_jumpTracks.TryGetValue(s.PlayerId, out var t) || !s.Alive) return null;
        double now = _connectedFor;
        float sincePress = (float)(now - t.PressAt), sinceImpact = (float)(now - t.ImpactAt);
        switch (t.View)
        {
            case JumpView.TakeOff when _jmpTakeOff is not null:
                return (_jmpTakeOff, sincePress);
            case JumpView.Air:
                if (_jmpTakeOff is not null && sincePress < TakeOffBranch) return (_jmpTakeOff, sincePress);
                if (LoopFlight) return FlightClip(s) is { } looped ? (looped, null) : _jmpTakeOff is not null ? (_jmpTakeOff, sincePress) : null;
                // M45: the flight clip posed by the vertical angle of the motion (the graph's user-controlled time; JumpFlightPose): rising at the
                // clip's start, the apex tuck in the middle, falling at its end; the low set within about 13.5 m of the ground.
                {
                    float horizontal = MathF.Sqrt(s.Velocity.X * s.Velocity.X + s.Velocity.Y * s.Velocity.Y);
                    float target = SmContent.JumpFlightPose.Target(s.Velocity.Z, horizontal);
                    t.Fraction = SmContent.JumpFlightPose.Damp(t.Fraction, target, (float)(now - t.FractionAt));
                    t.FractionAt = now;
                    t.Low = SmContent.JumpFlightPose.Low(AboveGround(s), t.Low);
                    if (FlightClip(s, t.Low) is { } flight)
                    {
                        if (_bot && s.PlayerId == _client?.PlayerId && (int)(t.Fraction * 10f) != _lastPoseDecile)
                        {
                            _lastPoseDecile = (int)(t.Fraction * 10f);
                            GD.Print($"t {now:F2} s: flight pose {t.Fraction:F2} of {flight} (target {target:F2}; vertical {s.Velocity.Z:F1} m/s, horizontal {horizontal:F1}; {s.Jump}{(s.JumpCut ? ", cut" : "")}; {AboveGround(s):F1} m above the ground)");
                        }
                        return (flight, SmContent.JumpFlightPose.Time(t.Fraction, ClipLength(flight, 0f)));
                    }
                }
                return _jmpTakeOff is not null ? (_jmpTakeOff, sincePress) : null;
            // protocol 30: the slash's clip. feel/jump-pack-2 (SmNet.SlashPose): the dive plays the flight sequencer's part, the wind-up (0 to the flight's crop
            // end), fitted to the dive; the impact the impact sequencer's part, the strike, from its crop start. (feel/jump-pack held frame 0 through the
            // dive and restarted the clip at 0 on landing: the landing showed the wind-up, the blade rising, as the swing seen backwards.)
            case JumpView.Dive when t.Slash && SlashClipOf(s) is { } slashDive:
                return (slashDive, SlashDiveTimeOf(s, (float)(now - t.DiveAt), t.DiveExpected));   // anim/air-slash-character: the strike curve (NetworkView.Feel.cs)
            case JumpView.Impact when t.Slash && SlashClipOf(s) is { } slashImpact:
                return (slashImpact, SlashImpactTime(s, sinceImpact, t.LandedClip, t.LandedRate));
            case JumpView.Dive when _jmpDive is not null:
                return (_jmpDive, null);
            case JumpView.Impact when _jmpImpact is not null:
                return (_jmpImpact, sinceImpact);
        }
        if (airborneOnly) return null;
        switch (t.View)
        {
            case JumpView.Landing when (t.LandMoving ? _jmpLandRun ?? _jmpLandIdle : _jmpLandIdle ?? _jmpLandRun) is { } land:
                return (land, (float)(now - t.Since));
            case JumpView.Getup when t.Slash && SlashClipOf(s) is { } slashGetup:
                return (slashGetup, SlashImpactTime(s, sinceImpact));
            case JumpView.Getup when t.GetupRun && UpperJump is { RunClip: not null } run:
                return (run.RunClip!, (float)(sinceImpact - run.FromImpact));
            case JumpView.Getup:
                float impactLength = ClipLength(_jmpImpact, 0f);
                if (_jmpImpact is not null && sinceImpact < impactLength) return (_jmpImpact, sinceImpact);
                if (_jmpGetup is not null) return (_jmpGetup, sinceImpact - impactLength);
                break;
        }
        return null;
    }

    /// <summary>feel/jump-pack-2: the player's sword slash spec (its clip's crops), or null.</summary>
    SlashSpec? SlashOf(PlayerState s) => _config is null ? null : Loadouts.JumpPack(_config, s)?.Slash;

    /// <summary>feel/jump-pack-2: the slash clip's time <paramref name="sinceImpact"/> s after the landing (SmNet.SlashPose.ImpactTime from the impact's crop start).</summary>
    float SlashImpactTime(PlayerState s, float sinceImpact, float landedAt = float.NaN, float landedRate = float.NaN) =>
        HammerSlash(s) ? HammerSlashPose.ImpactTime(sinceImpact, landedAt, ClipLength(_hammerSlashClip, 2.333f), NoMeleeFlow ? SmNet.SlashPose.ImpactCatchUp : SmNet.SlashPose.EasedCatchUp)   // anim/hammer-swing: the hammer's own air slash clip
        : StrikeCurve && SlashOf(s) is { } slash && float.IsFinite(landedAt) && float.IsFinite(landedRate)
            ? SmNet.SlashPose.StrikeImpactTime(sinceImpact, landedAt, landedRate, slash.ImpactClipStart, slash.HitTime, ClipLength(_slashClip, 1.733f))   // anim/air-slash-character
            : SmNet.SlashPose.ImpactTime(sinceImpact, SlashOf(s)?.ImpactClipStart ?? 0f, ClipLength(_slashClip, 1.733f), landedAt,
                NoMeleeFlow ? SmNet.SlashPose.ImpactCatchUp : SmNet.SlashPose.EasedCatchUp);   // anim/melee-flow: the early landing's catch-up eased over 0.1 s

    /// <summary>feel/jump-pack-2: how long after the landing the slash clip has left to show.</summary>
    double SlashAfterImpact(PlayerState s) => HammerSlash(s) ? HammerSlashPose.AfterImpact(ClipLength(_hammerSlashClip, 2.333f)) : SmNet.SlashPose.AfterImpact(SlashOf(s)?.ImpactClipStart ?? 0f, ClipLength(_slashClip, 1.733f));

    /// <summary>feel/jump-pack-2 (bot log): metres from the head (2 m above the feet) up to the map's character collision (60 m at most; none: infinity).</summary>
    float CeilingAbove(PlayerState s)
    {
        if (_jumpGround is null) return float.PositiveInfinity;
        var from = s.Position + new System.Numerics.Vector3(0f, 0f, 2f);
        return _jumpGround.Character.Raycast(from, new System.Numerics.Vector3(0f, 0f, 1f), 60f) is { } hit ? hit.Distance : float.PositiveInfinity;
    }

    /// <summary>M45: metres from the feet down to the map's character collision (60 m at most; no map or nothing below: infinity).</summary>
    float AboveGround(PlayerState s)
    {
        return FlightPoseRules.AboveGround(s.Position, _jumpGround);   // fix/hitboxes: the probe the server's hit volumes use
    }

    /// <summary>Shows a jump clip (posed at its time, or looping); false when it cannot be shown.</summary>
    static bool ShowJumpPose(CharacterViewer character, (string Clip, float? Time)? pose)
    {
        if (pose is not { } p) return false;
        if (p.Time is { } time) return character.Pose(p.Clip, time, ClipBlends.Jump(p.Clip));
        character.Play(p.Clip, ClipBlends.Jump(p.Clip));
        return string.Equals(character.Playing, p.Clip, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A placeholder ring of a pound's or landing's blast radius lying on the ground at <paramref name="feet"/> (game space).</summary>
    void AddRing(System.Numerics.Vector3 feet, float radius, Color colour)
    {
        if (_projectileRoot is null || radius <= 0f || !ZoneMarkers) return;
        var material = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled, AlbedoColor = new Color(colour, 0.8f),
        };
        var node = new MeshInstance3D
        {
            Mesh = new TorusMesh { InnerRadius = MathF.Max(0.05f, radius - 0.3f), OuterRadius = radius, Rings = 48, RingSegments = 8 },
            MaterialOverride = material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            // The torus's axis (local +Y) along game +Z.
            Transform = new Transform3D(new Basis(new Vector3(1, 0, 0), new Vector3(0, 0, 1), new Vector3(0, -1, 0)), new Vector3(feet.X, feet.Y, feet.Z + 0.1f)),
        };
        _projectileRoot.AddChild(node);
        _rings.Add((node, material, 0.0));
    }

    void UpdateRings(double delta)
    {
        for (int i = _rings.Count - 1; i >= 0; i--)
        {
            var (node, material, age) = _rings[i];
            age += delta;
            if (age >= RingSeconds) { node.QueueFree(); _rings.RemoveAt(i); continue; }
            material.AlbedoColor = new Color(material.AlbedoColor, 0.8f * (1f - (float)(age / RingSeconds)));
            _rings[i] = (node, material, age);
        }
    }

    /// <summary>
    /// <c>--wait-jump</c>: the own marine in the air from a jump (flight or dive) at least 3 m above its lift-off, inside the
    /// camera's view, the own marine only; 15 s after <c>--screenshot-at</c> any marine in the air.
    /// </summary>
    bool JumpWorthCapturing
    {
        get
        {
            if (_camera is null || _client is null) return false;
            double since = _connectedFor - (_screenshotAt ?? 4.0);
            foreach (var (id, t) in _jumpTracks)
            {
                if (t.View is not (JumpView.Air or JumpView.Dive)) continue;
                // Only the own marine until the fallback: it is the one the camera follows, and on a map others may be
                // hidden behind the level (the frustum test ignores occlusion).
                if (id != _client.PlayerId && since < 15.0) continue;
                var node = id == _client.PlayerId ? _local : _remotes.GetValueOrDefault(id);
                if (node is null || !node.IsInsideTree() || !node.Visible && node != _local) continue;
                var feet = node.GlobalPosition;
                if (!_camera.IsPositionInFrustum(feet + Vector3.Up)) continue;
                float height = node.Position.Z - t.LiftZ;   // the node's local position is game space
                if (height >= 3f || since >= 15.0) return true;
            }
            return false;
        }
    }

    /// <summary>Phase text for the HUD and the log.</summary>
    static string JumpPhaseText(PlayerState me) => me.Jump switch
    {
        JumpPhase.TakeOff => me.JumpHolding ? "taking off (holding)" : "taking off",
        JumpPhase.Ascent => me.JumpHolding ? "rising (holding)" : me.JumpCut ? "rising (released: the rise is cut)" : "rising",
        JumpPhase.Hang => "hanging",
        JumpPhase.Fall => "falling",
        JumpPhase.Dive => "diving",
        JumpPhase.Landed => "landing",
        JumpPhase.Recover => "recovering",
        _ => "",
    };

    /// <summary>
    /// HUD line of the own jump pack (placeholder readout): <c>Jump pack power 65 / 100</c> with a bar, LOW below the cost
    /// of a jump (a zero hold's), and the phase.
    /// </summary>
    string? JumpHudLine(PlayerState me)
    {
        if (Loadouts.JumpPack(_config!, me) is not { } pack) return null;
        float start = pack.CostAt(0f);
        string phase = JumpPhaseText(me);
        return $"Jump pack power {me.JumpPower:F0} / {pack.MaxPower:F0} {Bar(me.JumpPower, pack.MaxPower)}" +
               (me.JumpPower + 1e-3f < start ? $"   LOW (a jump needs {start:F0})" : "") +
               (phase.Length > 0 ? "   " + phase.ToUpperInvariant() : "   (Space; V in the air: ground pound)");
    }

    /// <summary>Jump state and counts for the bot's 2 s log line.</summary>
    string JumpLogText(PlayerState me)
    {
        var pack = Loadouts.JumpPack(_config!, me);
        string own = pack is null ? "no jump pack;" : $"jump {(me.Jump == JumpPhase.None ? "none" : JumpPhaseText(me))} power {me.JumpPower:F0}/{pack.MaxPower:F0} height {me.Position.Z:F1};";
        return $"{own} lift-offs {_liftOffs} last apex {_lastApex:F1} pounds {_pounds} impacts {_impacts} landings {_landings}; seen: lift-offs {_remoteLiftOffs} pounds {_remotePounds} impacts {_remoteImpacts} landings {_remoteLandings}";
    }

    /// <summary>
    /// On a map (the client's collision world): a clear line from the bot's chest to the target's and
    /// <see cref="BotJumpClearance"/> m free above its head, so the bot does not jump into a wall or a ceiling. Always true
    /// on the flat ground.
    /// </summary>
    bool BotJumpClear(PlayerState me, PlayerState target)
    {
        if (_client?.Collision is not { } world) return true;
        var from = me.Position + new System.Numerics.Vector3(0, 0, BotTargetHeight);
        var to = target.Position + new System.Numerics.Vector3(0, 0, BotTargetHeight);
        var line = to - from;
        float length = line.Length();
        if (length > 1e-3f && world.Character.Raycast(from, line / length, length) is not null) return false;
        return world.Character.Raycast(me.Position + new System.Numerics.Vector3(0, 0, 2f), System.Numerics.Vector3.UnitZ, BotJumpClearance) is null;
    }

    /// <summary>
    /// The bot's jump-pack script, or null for its other behaviour (test aid, not the original AI). On the ground with a
    /// target <see cref="BotJumpMin"/>–<see cref="BotJumpMax"/> m away (horizontally), enough power for a jump, nothing
    /// else under way and its last jump press <see cref="BotJumpEvery"/> s ago or more, it presses Jump with the stick
    /// forward (it faces the target) and holds it for a time scaled by the distance (0 at 8 m to the pack's longest hold
    /// at 30 m; the simulation stops the hold where the power runs out). During the take-off it keeps the stick forward;
    /// in the air it steers at the target while farther than half <see cref="BotPoundDistance"/>, fires the weapon in
    /// hand, and within <see cref="BotPoundDistance"/> m horizontally and at least <see cref="BotPoundMinHeight"/> m above
    /// the target presses melee (the ground pound). During the dive and the recovery it does nothing.
    /// </summary>
    PlayerInput? BotJumpInput(PlayerState me, PlayerState target, uint tick)
    {
        var c = _config!;
        if (Loadouts.JumpPack(c, me) is not { } pack) return null;
        float dx = target.Position.X - me.Position.X, dy = target.Position.Y - me.Position.Y;
        float horizontal = MathF.Sqrt(dx * dx + dy * dy), above = me.Position.Z - target.Position.Z;
        bool hold = _botJumpHolding && tick <= _botJumpHoldUntil;
        if (!hold) _botJumpHolding = false;
        var jump = hold ? InputButtons.Jump : InputButtons.None;
        switch (me.Jump)
        {
            case JumpPhase.TakeOff:
                return new PlayerInput(tick, 0f, 1f, _yaw, jump, _pitch);
            case JumpPhase.Ascent or JumpPhase.Hang or JumpPhase.Fall:
            {
                float stick = horizontal > BotPoundDistance * 0.5f ? 1f : 0f;
                if (pack.PoundEnabled && horizontal <= BotPoundDistance && above >= BotPoundMinHeight && !me.MeleeHeld)
                {
                    if (_bot) GD.Print($"t {_connectedFor:F1} s: id {_client!.PlayerId} bot presses pound {above:F1} m above P{target.PlayerId}, {horizontal:F1} m away horizontally");
                    return new PlayerInput(tick, 0f, stick, _yaw, jump | InputButtons.Melee, _pitch);
                }
                var weapon = BotWeaponButtons(me, tick, target) & ~(InputButtons.Melee | InputButtons.Switch);
                return new PlayerInput(tick, 0f, stick, _yaw, jump | weapon, _pitch);
            }
            case JumpPhase.Dive or JumpPhase.Recover:
                return new PlayerInput(tick, 0f, 0f, _yaw, InputButtons.None, _pitch);
            case JumpPhase.None:
            {
                if (!me.Grounded || me.JumpHeld || me.JumpPower + 1e-3f < pack.CostAt(0f)
                    || me.MeleeStep > 0 || me.Throwing || me.SwitchLeft > 0f || me.Charging || me.PendingShot > 0 || _connectedFor - _botLastJump < BotJumpEvery)
                    return null;
                bool atTarget = horizontal >= BotJumpMin && horizontal <= BotJumpMax && BotJumpClear(me, target);
                // --bot-jump-free (test aid): otherwise a jump toward the target every BotFreeJumpEvery s (on a map, over
                // whatever is in the way; a ceiling simply stops the rise), with BotFreeJumpHold s of hold.
                bool free = !atTarget && _botJumpFree && _connectedFor - _botLastJump >= BotFreeJumpEvery;
                if (!atTarget && !free) return null;
                // M45: a release while rising cuts the rise, so the hold scales over the whole ascent (the full jump: JumpPack.FullRiseTicks).
                float longest = pack.ReleaseCut ? JumpPack.FullRiseTicks(pack, c) * c.TickSeconds : pack.MaxHold;
                float wanted = atTarget ? Math.Clamp((horizontal - BotJumpMin) / (BotJumpMax - BotJumpMin), 0f, 1f) * longest : BotFreeJumpHold;
                // A press released before the dash decision tick is a horizontal dash with the jump-pack branch (protocol 24): the hold is at least NavJumpFlight.ShortestJumpHold ticks (M44a).
                int holdTicks = Math.Max((int)MathF.Round(wanted / c.TickSeconds), NavJumpFlight.ShortestJumpHold(c, me.Class));
                float holdSeconds = holdTicks * c.TickSeconds;   // the hold the simulation counts (held ticks after the press tick)
                _botJumpHoldUntil = tick + (uint)holdTicks; _botJumpHolding = true;
                _botLastJump = _connectedFor;
                if (_bot) GD.Print($"t {_connectedFor:F1} s: id {_client!.PlayerId} bot presses jump{(free ? " (free jump)" : "")} at P{target.PlayerId} {horizontal:F1} m away: hold {holdTicks} ticks ({holdSeconds:F2} s, {(pack.HoldTiers ? (holdSeconds + 1e-4f >= pack.MaxHold ? "the full jump" : "the small jump") : pack.ReleaseCut ? "height set by the release" : $"height {pack.HeightAt(holdSeconds):F1} m")}, cost {pack.CostAt(holdSeconds):F0}); power {me.JumpPower:F0}");
                return new PlayerInput(tick, 0f, 1f, _yaw, InputButtons.Jump, _pitch);
            }
            default:
                return null;   // Landed: the usual behaviour (the simulation holds the movement until the move lock)
        }
    }
}
