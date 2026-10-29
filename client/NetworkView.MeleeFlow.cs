using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SmContent;
using SmFormats.Havok;
using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// anim/melee-flow (the owner's requests of 2026-10-29; client presentation only, no simulation or protocol change):
/// <list type="bullet">
/// <item><b>The strikes' blends from their graphs</b> (<see cref="MeleeFlowBlends"/>, <c>smtool graph-dump --melee-flow</c>): a strike entered from the navigation takes the graph's entry blend
/// (the hammer's first swing 'Transition to 1' 0.4 s; the others 0.167 / 0.1 s), a strike chained from another takes that transition's blend ('Melee to 180' 0.333 s into the hammer's
/// sweeps, 'Blend In to Slam' 0.267 s ...; none: <see cref="ClipBlends.MeleeChain"/>), and the way back to the locomotion takes the attack state's exit (the chainsword's, power sword's
/// 'Standard Blend' 0.333 s, the hammer's 0.1 s played out and 0.333 s moving on; the knife: <see cref="ExitBlendOf"/>, ours). The hand-over to the legs at the move gate fades the
/// strike off the upper body over the graph's move-on exit too (the graph leaves the whole attack state on its move branch), instead of keeping it to the clip's end.</item>
/// <item><b>The clip foot lock</b> on melee strikes and the charge on the ground (<c>CharacterViewer.ClipFootLock.cs</c>, <see cref="ClipFootLockRules"/>).</item>
/// <item><b>The hammer in both hands</b> (the owner: "Assault Hammer running animation missing"): a marine carrying the thunder hammer or the daemon maul whose pistol is not up stands,
/// runs and sprints on the hammer graph's own navigation (<see cref="LocomotionAnimations.Hammer"/>, <c>SM_TH_NuSprint</c>), the pistol holstered (<see cref="WeaponRig.StowPistol"/>).</item>
/// <item><b>Stun strike shake</b> (the owner: "add a tiny bit of screen shake to all stun strike moves"; ours, small and switchable): the own camera shakes a little at the impact of every
/// heavy (F) strike (<see cref="StunShakeRadians"/>), more at a blast strike's (the stomps, the slams: <see cref="StompShakeRadians"/>) and when the own charge or dash attack connects
/// (<see cref="ChargeShakeRadians"/>). Not the damage-taken shake, not the melee hit-stop shake (both off by default, owner).</item>
/// <item><b>Ledge charge landing</b>: a charge that has been held in the air off a ledge never shows its rush clip again in the same charge (the simulation ends it on landing; a tick of it
/// left over would bash a second time): the fall pose lands into the locomotion with the fall's landing.</item>
/// </list>
/// <c>--no-melee-flow</c> (test aid) switches every part off for before / after runs; <c>--no-stun-shake</c>, <c>--stun-shake-scale f</c>, <c>--no-clip-foot-lock</c> single parts.
/// </summary>
public partial class NetworkView
{
    static bool NoMeleeFlow => CharacterViewer.NoMeleeFlow;
    static readonly bool NoStunShake = CharacterViewer.NoMeleeFlow || OS.GetCmdlineUserArgs().Contains("--no-stun-shake");
    static readonly float StunShakeScale = CmdFloat("--stun-shake-scale", 1f);
    /// <summary><c>--log-stun-shake</c> (test aid): a line per stun-strike or charge shake.</summary>
    static readonly bool LogStunShake = OS.GetCmdlineUserArgs().Contains("--log-stun-shake");

    /// <summary>The own camera's shake at a heavy (F) strike's impact (radians; ours: 0.35 degrees, under the knife's own hit shake of 0.5).</summary>
    public const float StunShakeRadians = 0.35f * MathF.PI / 180f;
    /// <summary>At a blast strike's impact (the stomps and slams; radians, ours: 0.6 degrees).</summary>
    public const float StompShakeRadians = 0.6f * MathF.PI / 180f;
    /// <summary>When the own charge or dash attack connects (radians, ours: 0.5 degrees).</summary>
    public const float ChargeShakeRadians = 0.5f * MathF.PI / 180f;

    static float CmdFloat(string name, float fallback)
    {
        var a = OS.GetCmdlineUserArgs();
        int i = Array.IndexOf(a, name);
        return i >= 0 && i + 1 < a.Length && float.TryParse(a[i + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && float.IsFinite(v) && v >= 0f ? v : fallback;
    }

    Task<Dictionary<string, MeleeFlowBlends>>? _flowLoad;
    Dictionary<string, MeleeFlowBlends>? _flowByGraph;
    readonly Dictionary<string, MeleeFlowBlends?> _flowByWargear = new(StringComparer.OrdinalIgnoreCase);
    string _flowNote = "not loaded";

    /// <summary>Per player: the strike shown last (light / heavy and its index) and when, for the chain blends; the hand-over's start; a charge that went airborne.</summary>
    sealed class FlowTrack
    {
        public int Step; public bool Heavy; public double At = double.NegativeInfinity;
        public float HandFrom = -1f;   // the strike's time when the legs were handed over (-1: not handed over)
        public int HandStep; public bool HandHeavy;
        public bool ChargeAirborne;    // this charge has been held in the air
        public RushPhase LastRush;
    }
    readonly Dictionary<int, FlowTrack> _flow = new();
    FlowTrack FlowOf(int id) { if (!_flow.TryGetValue(id, out var t)) _flow[id] = t = new FlowTrack(); return t; }
    void MeleeFlowForget(int id) { _flow.Remove(id); _hammerDraw.Remove(id); }

    int _stunShakes, _stompShakes, _chargeShakes, _rebashesHeld;
    RushPhase _ownRushSeen;

    /// <summary>Starts reading the melee graphs' blends on a worker (the behaviour graphs: slow, never on the main thread).</summary>
    void SetUpMeleeFlow()
    {
        if (NoMeleeFlow || Lean) { _flowNote = Lean ? "not loaded (lean bot)" : "off (--no-melee-flow)"; return; }
        if (_locoLoad is not { } loco) { _flowNote = "no locomotion load"; return; }
        _flowNote = "loading";
        // The graphs the locomotion worker read (LocomotionBundle.Graphs): no second read of the rig and its behaviour graphs.
        _flowLoad = loco.ContinueWith(t =>
        {
            var graphs = t.Result.Graphs;
            var result = new Dictionary<string, MeleeFlowBlends>(StringComparer.OrdinalIgnoreCase);
            foreach (var wargear in new[] { "sm_mp_melee_combat_knife", "sm_mp_melee_chainsword", "sm_mp_melee_powersword", "sm_mp_melee_power_axe", "sm_mp_melee_2hand_thunder_hammer", "sm_mp_core_abil_heavy_evade" })
                if (MpClasses.MeleeMapFor(wargear) is { } map && !result.ContainsKey(map.Graph) && MeleeFlowBlends.Read(graphs, map) is { } flow) result[map.Graph] = flow;
            return result;
        }, TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    /// <summary>The blends of a player's melee weapon, or null (not loaded yet, off, or a weapon the reader does not know).</summary>
    MeleeFlowBlends? FlowBlendsOf(PlayerState s)
    {
        if (NoMeleeFlow) return null;
        if (_flowByGraph is null && _flowLoad is { IsCompleted: true } load)
        {
            _flowLoad = null;
            if (load.IsFaulted || load.IsCanceled) { _flowNote = "not available: " + (load.Exception?.GetBaseException().Message ?? "cancelled"); GD.PrintErr("melee flow: " + _flowNote); _flowByGraph = new(); }
            else
            {
                _flowByGraph = load.Result;
                _flowNote = string.Join(", ", _flowByGraph.Values.Select(f => $"{f.Graph}: exit {f.ExitEnd?.ToString("0.###") ?? "-"} / {f.ExitRun?.ToString("0.###") ?? "-"} s, {f.Chains.Count} chains"));
                GD.Print("melee flow: " + _flowNote);
            }
        }
        if (_flowByGraph is null || MeleeWargear(s) is not { } wargear) return null;
        if (_flowByWargear.TryGetValue(wargear, out var cached)) return cached;
        var blends = MpClasses.MeleeMapFor(wargear) is { } map && _flowByGraph.TryGetValue(map.Graph, out var b) ? b : null;
        _flowByWargear[wargear] = blends;
        return blends;
    }

    /// <summary>
    /// The blend a strike's clip enters with: the chain's from the strike shown just before (within 0.3 s: the same string), else the entry's; the graph's value where read, else
    /// <see cref="ClipBlends.Melee"/>. Notes the strike shown.
    /// </summary>
    float MeleeEntryBlend(PlayerState s)
    {
        float fallback = ClipBlends.Melee(s.MeleeStep);
        var t = FlowOf(s.PlayerId);
        bool same = t.Step == s.MeleeStep && t.Heavy == s.MeleeHeavy;
        bool chained = !same && t.Step > 0 && _connectedFor - t.At < 0.3;
        int fromStep = t.Step; bool fromHeavy = t.Heavy;
        t.Step = s.MeleeStep; t.Heavy = s.MeleeHeavy; t.At = _connectedFor;
        if (FlowBlendsOf(s) is not { } flow || s.MeleeStep <= 0) return fallback;
        if (chained) return flow.ChainOf(fromHeavy, fromStep - 1, s.MeleeHeavy, s.MeleeStep - 1) is float c && c > 0f ? c : ClipBlends.MeleeChain;
        return flow.EntryOf(s.MeleeHeavy, s.MeleeStep - 1) ?? fallback;
    }

    /// <summary>The blend from a melee strike's clip back into the locomotion (the graph's exit: played out, or moving on), or null (not a strike, no data: the caller's).</summary>
    float? MeleeExitBlendOf(PlayerState s, string clip)
    {
        if (NoMeleeFlow || !clip.StartsWith("SM_MEL_", StringComparison.OrdinalIgnoreCase) || FlowBlendsOf(s) is not { } flow) return null;
        bool moving = MathF.Sqrt(s.Velocity.X * s.Velocity.X + s.Velocity.Y * s.Velocity.Y) > 0.5f;
        return moving ? flow.ExitRun ?? flow.ExitEnd : flow.ExitEnd;
    }

    /// <summary>
    /// The upper-body weight of a strike handed over to the legs at <paramref name="time"/> into it: the hand-over's tail (<see cref="ActionHandoff.TailWeight"/>) and, since the hand-over,
    /// the graph's move-on exit (the whole attack state leaves to the navigation on its move branch). <paramref name="handedNow"/>: the legs were handed over this frame.
    /// </summary>
    float HandoffUpperWeight(PlayerState s, MeleeStep step, float time)
    {
        float tail = ActionHandoff.TailWeight(step.Duration, time);
        if (NoMeleeFlow || FlowBlendsOf(s) is not { ExitRun: float exit } || exit <= 1e-3f) return tail;
        var t = FlowOf(s.PlayerId);
        if (t.HandFrom < 0f || t.HandStep != s.MeleeStep || t.HandHeavy != s.MeleeHeavy || time < t.HandFrom) { t.HandFrom = time; t.HandStep = s.MeleeStep; t.HandHeavy = s.MeleeHeavy; }
        float fade = 1f - CharacterViewer.Smooth((time - t.HandFrom) / exit);
        return MathF.Min(tail, fade);
    }

    /// <summary>The legs are no longer handed over (a new strike, or the clip keeps the whole body again).</summary>
    void HandoffEnded(int id) { if (_flow.TryGetValue(id, out var t)) t.HandFrom = -1f; }

    /// <summary>Whether a strike's or a charge's clip holds its put-down feet (on the ground, alive, not the Ork's rig).</summary>
    bool ClipFootLockFor(PlayerState s, bool melee) => !NoMeleeFlow && s.Alive && (s.Jump is JumpPhase.None or JumpPhase.Landed or JumpPhase.Recover) && (melee || s.Grounded);   // a strike holds its feet over a step or a curb too (the body may leave the ground for a tick)

    /// <summary>
    /// The strike's extracted root speed forward (m/s per second of the clip <paramref name="clipLength"/> long, posed for <paramref name="time"/> s of the attack) at the blend the
    /// strike is posed at (<see cref="AttackVariants"/>' start distance, else the start blend), for the clip foot lock's still-foot test; NaN without reach data.
    /// </summary>
    float MeleeRootSpeed(PlayerState s, float time, float clipLength)
    {
        if (_config is null || Combat.MeleeStepOf(_config, s) is not { Reach: { Variants.Count: > 0 } reach } step || !(clipLength > 1e-3f)) return float.NaN;
        float blend = _meleeShows.TryGetValue(s.PlayerId, out var show) && show.Step == s.MeleeStep && show.Heavy == s.MeleeHeavy
            ? show.StartDistance : Math.Clamp(reach.DefaultDistance, reach.Range.Min, reach.Range.Max);
        const float h = 1f / 60f;
        float perSecond = (reach.ForwardAt(time + h, step.Duration, blend) - reach.ForwardAt(MathF.Max(0f, time - h), step.Duration, blend)) / (time + h - MathF.Max(0f, time - h));
        return float.IsFinite(perSecond) ? perSecond * step.Duration / clipLength : float.NaN;
    }

    /// <summary>
    /// A charge held in the air off a ledge: the rush clip is not shown again for the rest of that charge (its landing goes to the locomotion with the fall's landing, no second bash).
    /// Updated every frame from <see cref="Place"/>; true while that charge goes on.
    /// </summary>
    bool RushRebashHeld(PlayerState s)
    {
        var t = FlowOf(s.PlayerId);
        if (NoMeleeFlow) return false;
        if (!s.Alive || s.Rush is RushPhase.None || (t.LastRush is RushPhase.None && s.Rush is RushPhase.Rush or RushPhase.DashAttack)) t.ChargeAirborne = false;   // no charge, or a new one
        t.LastRush = s.Alive ? s.Rush : RushPhase.None;
        if (ChargeHeldInAir(s)) t.ChargeAirborne = true;
        bool held = t.ChargeAirborne && s.Grounded && s.Rush is RushPhase.Rush or RushPhase.DashAttack;
        if (held) _rebashesHeld++;
        return held;
    }

    /// <summary>
    /// The time constant (s; ours) with which the shown body reaches a charge's heading (feel/charge-directions: the body is the simulation's heading through a charge, formerly in one
    /// frame): 63 % in 0.03 s, 95 % in 0.09 s, whatever the angle, so a 25 degree side charge or a lunge to the back still turns at once to the eye but not in one frame.
    /// <c>--charge-turn-snap</c> (test aid) or <c>--no-melee-flow</c>: the one-frame snap.
    /// </summary>
    public const float ChargeTurnSeconds = 0.03f;
    static readonly bool ChargeTurnSnap = CharacterViewer.NoMeleeFlow || OS.GetCmdlineUserArgs().Contains("--charge-turn-snap");

    /// <summary>The body heading this frame on the way from <paramref name="body"/> to the charge's <paramref name="heading"/> (radians; <paramref name="dt"/> seconds since the last frame).</summary>
    static float ChargeTurn(float body, float heading, float dt) =>
        ChargeTurnSnap || !(dt > 0f) || dt > 0.25f || !float.IsFinite(body) ? heading
        : ViewSmoothing.WrapPi(body + ViewSmoothing.WrapPi(heading - body) * (1f - MathF.Exp(-dt / ChargeTurnSeconds)));

    // ------------------------------------------------------------------ the hammer

    /// <summary>
    /// A marine with the thunder hammer or the daemon maul (a two-handed melee weapon) whose pistol is not up: the base graph's "Melee - ThunderHammer" state (the pistol state hands back to it
    /// after its timeout or on a sprint: <c>EVT_Ranged_TH_Timeout</c>, <c>BTN_Sprint</c>), so it stands, runs and sprints with the hammer in both hands. The pistol is up while the simulation
    /// holds the weapon ready after a shot (<see cref="HipReady"/>) or in aim mode.
    /// </summary>
    bool HammerStance(PlayerState s) =>
        !NoMeleeFlow && s.Alive && !PistolUp(s) && _config is not null
        && MeleeWargear(s) is { } w && w.Contains("_2hand_", StringComparison.OrdinalIgnoreCase) && Loadouts.ClassOf(_config, s.Class)?.Race != Race.Orks;

    /// <summary>
    /// The pistol is up: aim mode, or the simulation holding the weapon ready after a shot, raising it, charging or with a shot pending. Not <see cref="HipReady"/>, which is also false
    /// through reactions, throws, switches and melee steps (the pistol would be holstered for a stagger and pop back after it).
    /// </summary>
    static bool PistolUp(PlayerState s) => s.Aiming || s.ReadyTicks > 0 || s.RaiseTicks > 0 || s.Charging || s.PendingShot > 0;

    string? _hammerIdle; bool _hammerIdleResolved;

    /// <summary>The hammer's idle when the marine stands in the hammer stance and the data is in, else null (resolved once against the rig).</summary>
    string? HammerIdleOf(PlayerState s)
    {
        if (!_hammerIdleResolved && _locoAnims is { } loco) { _hammerIdle = loco.HammerIdle is { } idle && RigHasClip(idle) ? idle : null; _hammerIdleResolved = true; }
        return _hammerIdle is { } hammerIdle && HammerStance(s) ? hammerIdle : null;
    }

    /// <summary>The hammer graph's connecting clip from the pistol to the hammer in both hands ("ThunderHammer From Pistol": <c>SM_1HR_NAV_Pistol2TH</c>, 0.833 s, on the upper body over the hammer's navigation).</summary>
    public const string PistolToHammerClip = "SM_1HR_NAV_Pistol2TH";
    /// <summary>Its blends: in '5 Frame Blend' 0.167 s (the pistol state's way into the hammer state), out from its <c>BRANCH_ToNav</c> (0.92 of the clip) over 'Blend Long' 0.5 s.</summary>
    const float PistolToHammerIn = 5f / 30f, PistolToHammerBranch = 0.92f, PistolToHammerOut = 0.5f;
    /// <summary>
    /// Where in the draw clip the pistol leaves the hand for its holster (fraction; measured: the clip has no weapon tag, and its pistol hand, <c>RIG_Prop_LHand</c>, is lowest, passing the hip,
    /// at 0.62 to 0.64 of it before it rises to the hammer's haft). Before it the pistol stays in hand.
    /// </summary>
    const float PistolToHammerStow = 0.62f;
    readonly Dictionary<int, (bool Was, double Since)> _hammerDraw = new();

    /// <summary>
    /// When a marine's pistol comes down and the hammer goes back into both hands (the hammer stance begins while it stands or runs), the graph's connecting clip plays over the
    /// upper body: the pistol is put away and the hammer taken up instead of the hands jumping to the hammer's grip. Called after the locomotion is placed.
    /// </summary>
    void HammerDrawPose(CharacterViewer character, PlayerState s)
    {
        if (NoMeleeFlow || _assets is null) return;
        bool now = HammerStance(s) && s.MeleeStep == 0 && s.Rush == RushPhase.None;
        // A death or a respawn starts afresh: a marine that died with the pistol up does not draw the hammer on its fresh spawn.
        if (!s.Alive) { _hammerDraw.Remove(s.PlayerId); return; }
        var (was, since) = _hammerDraw.GetValueOrDefault(s.PlayerId, (now, double.NegativeInfinity));
        if (now && !was && s.Alive && _connectedFor > 1.0) since = _connectedFor;   // the pistol state handed back to the hammer
        if (!now) since = double.NegativeInfinity;
        _hammerDraw[s.PlayerId] = (now, since);
        if (!double.IsFinite(since) || _assets.Animation(PistolToHammerClip) is not { } clip || ActionMask() is not { } mask || character.PoseLayerClip is not null) return;
        float t = (float)(_connectedFor - since), length = clip.Animation.Duration, branch = PistolToHammerBranch * length;
        if (t > branch + PistolToHammerOut) return;
        float w = CharacterViewer.Smooth(t / PistolToHammerIn) * (t <= branch ? 1f : 1f - CharacterViewer.Smooth((t - branch) / PistolToHammerOut));
        if (w > 1e-3f) character.SetPoseLayer(PistolToHammerClip, MathF.Min(t, length), w, additive: false, mask);
    }

    /// <summary>
    /// The charge's clip for a marine with the hammer in both hands: the hammer's sprint graph has its own sprint attack (<c>SM_TH_NuSprint</c> "Sprint Attack":
    /// <c>SM_TH_BUL_Hit_M_2.5</c> / <c>_5</c>, 2.167 s) with the same tags as the generic one (<c>SM_BUL_Hit_M_2.5m</c> / <c>_5m</c>, 1.667 s; the hit at 0.14 of either), so
    /// it is posed at the same fraction of itself (the simulation's charge times are the generic clip's). Else the clip and time as given.
    /// </summary>
    (string Clip, float Time) HammerRush(PlayerState s, string clip, float time)
    {
        if (NoMeleeFlow || !s.Alive || MeleeWargear(s) is not { } w || !w.Contains("_2hand_", StringComparison.OrdinalIgnoreCase) || !clip.StartsWith("SM_BUL_Hit_M_", StringComparison.OrdinalIgnoreCase)) return (clip, time);
        string hammer = clip.EndsWith("2.5m", StringComparison.OrdinalIgnoreCase) ? "SM_TH_BUL_Hit_M_2.5" : "SM_TH_BUL_Hit_M_5";
        float from = ClipLength(clip, 0f), to = ClipLength(hammer, 0f);
        return from > 1e-3f && to > 1e-3f ? (hammer, time * to / from) : (clip, time);
    }

    /// <summary>Whether the pistol goes to its holster: the hammer stance (from the draw clip's stow point on while it plays), or a strike with the hammer (both hands on it).</summary>
    bool HammerHoldsBothHands(PlayerState s)
    {
        if (NoMeleeFlow || !s.Alive || MeleeWargear(s) is not { } w || !w.Contains("_2hand_", StringComparison.OrdinalIgnoreCase)) return false;
        if (s.MeleeStep > 0) return true;
        if (!HammerStance(s)) return false;
        if (_hammerDraw.TryGetValue(s.PlayerId, out var draw) && double.IsFinite(draw.Since))
            return _connectedFor - draw.Since >= PistolToHammerStow * ClipLength(PistolToHammerClip, 0.833f);
        return true;
    }

    // ------------------------------------------------------------------ once per frame

    /// <summary>The own stun-strike shakes (see the class comment), once per frame after the own events are drained.</summary>
    void MeleeFlowFrame(PlayerState me)
    {
        if (_config is null) return;
        if (!NoStunShake && me.Alive && !_bot)
        {
            foreach (var (_, events) in _frameOwnEvents)
            {
                if (events.MeleeHitStep <= 0 || !events.MeleeHeavy) continue;
                var heavy = Loadouts.MeleeHeavy(_config, me);
                bool blast = events.MeleeHitStep <= heavy.Count && heavy[events.MeleeHitStep - 1].Stomp is not null;
                FxShake((blast ? StompShakeRadians : StunShakeRadians) * StunShakeScale);
                if (blast) _stompShakes++; else _stunShakes++;
                if (LogStunShake) GD.Print($"t {_connectedFor:F2} s: stun-strike shake ({(blast ? "blast strike" : "heavy strike")} {events.MeleeHitStep}, {(blast ? StompShakeRadians : StunShakeRadians) * StunShakeScale * 180f / MathF.PI:F2} deg)");
            }
            // A connect is a hit on a player: the dash attack's hit state, or the rush's collision stop with an enemy in reach in front (a wall stop shakes neither; presentation guess, the server decides).
            bool connected = (_ownRushSeen == RushPhase.Rush && me.Rush == RushPhase.Collision && MeleePresentation.Victim(me, _lastOthers, _config) > 0)
                || (_ownRushSeen == RushPhase.DashAttack && me.Rush == RushPhase.DashHit);
            if (connected) { FxShake(ChargeShakeRadians * StunShakeScale); _chargeShakes++; if (LogStunShake) GD.Print($"t {_connectedFor:F2} s: charge-connect shake ({me.Rush})"); }
        }
        _ownRushSeen = me.Alive ? me.Rush : RushPhase.None;
    }

    /// <summary>
    /// <c>--bot-turn-rate &lt;deg/s&gt;</c> (test aid, ours): a fighting bot's view turns toward where its script points it at most this fast, instead of in one tick, so recorded fights
    /// measure the animation's transitions and not the bot's one-tick view snaps (the transition probe counted 90 to 180 degree body turns in a frame at the bots' charges and strikes).
    /// </summary>
    static readonly float BotTurnRate = CmdFloat("--bot-turn-rate", 0f);
    float _botTurnYaw = float.NaN;

    PlayerInput BotTurnLimit(PlayerInput input)
    {
        if (!(BotTurnRate > 0f) || _config is null) return input;
        float max = BotTurnRate * MathF.PI / 180f * _config.TickSeconds;
        if (!float.IsFinite(_botTurnYaw) || !_client!.Predicted.Alive) _botTurnYaw = input.Yaw;
        _botTurnYaw = ViewSmoothing.WrapPi(_botTurnYaw + Math.Clamp(ViewSmoothing.WrapPi(input.Yaw - _botTurnYaw), -max, max));
        _yaw = _botTurnYaw; _sampledYaw = _botTurnYaw;
        return input with { Yaw = _botTurnYaw };
    }

    /// <summary>The bot log's part and the F1 line.</summary>
    string MeleeFlowLogText() => $"melee flow: blends {_flowNote}; stun shakes {_stunShakes}, stomp {_stompShakes}, charge {_chargeShakes}; ledge re-bashes held {_rebashesHeld}; clip pins held {CharacterViewer.ClipPinsHeld}, dragged {CharacterViewer.ClipPinsDragged}";
}
