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
/// Animation pass 2 (the owner's playtest of 2026-10-28: "everything about character motion and movement still feels janky"), client only: the clips of the marine's base graph that were never played,
/// read by <see cref="UpperBodyBundle"/> (<c>smtool graph-dump --upper</c>) and laid on the pose layer stack (<see cref="PoseSlots"/>):
/// <list type="bullet">
/// <item><b>Weapon switch</b> (<see cref="PoseSlots.Switch"/>): <c>SM_Swap_Rifle_to_Rifle_01</c> (0.467 s) or, for a switch to the pistol, <c>SM_Swap_Rifle_to_Melee_01</c> (0.667 s), on every bone but the 14 leg bones
/// (the graph's swap mask), stretched over the simulation's switch time (<c>WeaponSwitchTime</c>, 0.6 s: not changed). The weapon model in the hand changes at the graph's <c>Weapons - Ranged Weapon</c> /
/// <c>Weapons - Melee and Ranged</c> tag of the clip (0.5 and 0.6 of it) instead of at half the switch time. The clip is started when the player's weapon slot changes, own and seen alike.</item>
/// <item><b>Hit flinch</b> (<see cref="PoseSlots.Flinch"/>): an additive <c>SM_REC_Flinch_*</c> clip of the victim's weapon set on every non-lethal ranged hit seen (shot records and the own hit reports), chosen by the hit zone
/// (head or body) and the shooter's side and front / back (<see cref="FlinchRules"/>: interpretation), at most one per <c>min_hit_reaction_time</c> (1 s, the blueprint's) and never while a reaction clip shows.</item>
/// <item><b>Death before the ragdoll</b>: the graph's death clip for the blow (<see cref="DeathRules"/>) plays on the body until the graph's powered-ragdoll tag, then the ragdoll starts from that pose with the
/// killing blow's push (the Ork's hold pattern, <c>OrkDeathHold</c>).</item>
/// <item><b>The jump pack in flight and after the pound</b> (<see cref="PoseSlots.Wobble"/> and <see cref="PoseSlots.Action"/>): the flight wobble <c>SM_JMP_Blend_90_Pos_Fast</c> (as an offset from its first pose), the recoil, reload and
/// toss layers over the flight pose, and <c>SM_JMP_Landing_MedRun</c> as the pound's get-up for a marine that is moving at the get-up window.</item>
/// </list>
/// All of it is presentation: the simulation and the protocol are not touched. Test aids: <c>--no-switch-anim</c>, <c>--no-flinch</c>, <c>--no-death-anim</c>, <c>--no-jump-upper</c> (each restores the pass-1 behaviour),
/// <c>--log-upper</c> (prints the events; bots print them anyway).
/// </summary>
public partial class NetworkView
{
    static readonly bool NoSwitchAnim = OS.GetCmdlineUserArgs().Contains("--no-switch-anim");
    static readonly bool NoFlinch = OS.GetCmdlineUserArgs().Contains("--no-flinch");
    static readonly bool NoDeathAnim = OS.GetCmdlineUserArgs().Contains("--no-death-anim");
    static readonly bool NoJumpUpper = OS.GetCmdlineUserArgs().Contains("--no-jump-upper");
    static readonly bool LogUpper = OS.GetCmdlineUserArgs().Contains("--log-upper");

    /// <summary>Seconds the switch clip fades in and out at the ends of the switch (ours; the graph's blend is not a number the reader has).</summary>
    const float SwitchBlendIn = 0.1f, SwitchBlendOut = 0.1f;
    /// <summary>The weight of the flight wobble (ours: the graph's blend weight is not read).</summary>
    const float WobbleWeight = 1f;
    /// <summary>The longest the death clip may hold the body (s; ours): the plasma death is 1.47 s in the graph.</summary>
    const float DeathHoldCap = 1.5f;
    /// <summary>How long a death waits for its kill-feed entry before a clip is chosen without it (s; ours), and the time constant of the held body's slide (s; ours).</summary>
    const float DeathCauseWait = 0.15f, DeathSlideSeconds = 0.2f;

    Task<UpperBodyBundle>? _upperLoad;
    UpperBodyBundle? _upperData;
    string _upperNote = "not loaded";
    int _switchAnims, _flinches, _flinchesNoted, _flinchSkippedGap, _flinchSkippedReaction, _deathHolds, _deathsHeld, _getupRuns, _wobbleLayers, _jumpActionLayers;

    /// <summary>Starts reading the pass-2 clips on a worker thread (the behaviour graphs: slow, never on the main thread).</summary>
    void SetUpUpperBody()
    {
        if ((NoSwitchAnim && NoFlinch && NoDeathAnim && NoJumpUpper) || Lean) { _upperNote = Lean ? "not loaded (lean bot)" : "switched off"; return; }   // perf/lean-bots: poses only
        string gameDir = _gameDir;
        _upperNote = "loading";
        _upperLoad = Task.Run(() => UpperBodyBundle.Load(gameDir));
    }

    UpperBodyBundle? UpperData()
    {
        if (_upperData is not null) return _upperData;
        if (_upperLoad is not { IsCompleted: true } load) return null;
        _upperLoad = null;
        if (load.IsFaulted || load.IsCanceled)
        {
            _upperNote = "not available: " + (load.Exception?.GetBaseException().Message ?? "cancelled");
            GD.PrintErr("upper body: " + _upperNote);
            return null;
        }
        _upperData = load.Result;
        var d = _upperData;
        _upperNote = $"switch {d.Switch.ToRifle?.Clip ?? "-"} {d.Switch.ToRifle?.Length:0.###} s (swap {d.Switch.ToRifle?.SwapAt:0.###} s) / {d.Switch.ToPistol?.Clip ?? "-"} {d.Switch.ToPistol?.Length:0.###} s (swap {d.Switch.ToPistol?.SwapAt:0.###} s); " +
                     $"flinch sets {d.Flinch.Sets.Count} (min time between hits {d.Flinch.MinHitReactionTime:0.##} s); death clips {d.Death.Clips.Count}; jump pack: get-up run {d.JumpPack.RunClip ?? "-"}, wobble {d.JumpPack.NoiseClip ?? "-"}{(d.JumpPack.NoiseAdditive ? " (additive)" : "")}";
        GD.Print("upper body: " + _upperNote);
        // fix/stow-right: the two possible switch plans, built once (the weapon update asks for them every frame for every player).
        _planToPistol = SwitchPlan.For(d.Switch, WeaponSlot.Ranged, WeaponSlot.Pistol, WeaponRig.OldStow);
        _planToPrimary = SwitchPlan.For(d.Switch, WeaponSlot.Pistol, WeaponSlot.Ranged, WeaponRig.OldStow);
        foreach (var c in new[] { d.Switch.ToRifle, d.Switch.ToPistol })
            if (c is not null)
                GD.Print($"  upper body: switch clip {c.Clip}: sequencer speed {c.Speed:0.###}, animation {ClipLength(c.Clip, c.Length):0.###} s{(Math.Abs(c.Speed - 1f) > 1e-3f ? " (not 1: the switch pieces sample the animation at playback x speed)" : "")}");
        foreach (var n in d.Switch.Notes.Concat(d.Flinch.Notes).Concat(d.Death.Notes).Concat(d.JumpPack.Notes)) GD.Print("  upper body note: " + n);
        if (LogUpper) GD.Print("  upper body: " + ClipOffsets());
        return _upperData;
    }

    /// <summary>Test aid: how far the flight wobble and the flinches move the bones (the largest bone rotation away from the clip's first pose, degrees, and translation, metres), for choosing a weight.</summary>
    string ClipOffsets()
    {
        string One(string? name)
        {
            if (name is null || _assets?.Animation(name) is not { } clip) return "-";
            int tracks = clip.Animation.TrackCount;
            var first = new HavokTransform[tracks]; var at = new HavokTransform[tracks];
            clip.Animation.SampleInto(0f, first);
            float maxDeg = 0f, maxMetres = 0f; int maxBone = -1;
            for (int k = 1; k <= 16; k++)
            {
                clip.Animation.SampleInto(clip.Animation.Duration * k / 16f, at);
                for (int i = 0; i < tracks; i++)
                {
                    var a = new Quaternion(first[i].Rotation.X, first[i].Rotation.Y, first[i].Rotation.Z, first[i].Rotation.W).Normalized();
                    var b = new Quaternion(at[i].Rotation.X, at[i].Rotation.Y, at[i].Rotation.Z, at[i].Rotation.W).Normalized();
                    float deg = Mathf.RadToDeg(a.AngleTo(b));
                    if (deg > maxDeg) { maxDeg = deg; maxBone = clip.TrackToBone[i]; }
                    maxMetres = MathF.Max(maxMetres, (new Vector3(at[i].Translation.X, at[i].Translation.Y, at[i].Translation.Z) - new Vector3(first[i].Translation.X, first[i].Translation.Y, first[i].Translation.Z)).Length());
                }
            }
            return $"{name}: up to {maxDeg:F1} deg (bone {maxBone}), {maxMetres * 100f:F1} cm from its first pose";
        }
        return "offsets: wobble " + One(_upperData?.JumpPack.NoiseClip);
    }

    bool UpperLog => LogUpper || _bot;

    // ------------------------------------------------------------------------------------------------------------------------------------------ per player, once per placed frame

    /// <summary>
    /// Once per placed frame, before the early returns of <see cref="Place"/>: follows the player's weapon slot (a change starts the switch clip), remembers what the death clip needs (sprinting, charging, the weapon in
    /// hand) and lays the flinch. Not for the dead (their tracks are cleared).
    /// </summary>
    void UpperObserve(CharacterViewer character, PlayerState s, LocoTrack t, bool own)
    {
        if (!s.Alive || _config is null) { t.SwSeen = false; t.Switch = null; t.Flinch = null; return; }
        t.WasSprint = t.Sprint;
        t.WasCharging = s.Rush != RushPhase.None;
        // The death hold needs it: a victim in the air goes straight to the ragdoll. The debounced fall (not the raw Grounded flag, which blips on a stair or ramp crest and is snapshot-granular for
        // remote players) or a jump-pack flight or dive (the take-off still stands on the ground).
        t.WasAirborne = t.Fall.Falling || (s.Jump != JumpPhase.TakeOff && (JumpPack.Jumping(s.Jump) || JumpPack.Pounding(s.Jump)) && !s.Grounded);
        t.WeaponName = Loadouts.InHand(_config, s)?.Name;
        if (t.SwSeen && s.Slot != t.SwSlot) BeginSwitch(s, t, own);
        t.SwSlot = s.Slot; t.SwSeen = true;
        FlinchLayer(character, s, t);
    }

    /// <summary>A dead or respawning player starts afresh: no switch is seen from the dead slot to the new one, and no flinch or switch layer carries over.</summary>
    void UpperReset(int id)
    {
        var t = LocoOf(id);
        t.SwSeen = false; t.Switch = null; t.Flinch = null;   // WasSprint, WasCharging and WasAirborne stay: the death hold reads the last living frame
    }

    // ------------------------------------------------------------------------------------------------------------------------------------------ weapon switch

    void BeginSwitch(PlayerState s, LocoTrack t, bool own)
    {
        t.Switch = null;
        if (NoSwitchAnim || UpperData() is not { } data || _config is null || _config.WeaponSwitchTime <= 0.05f) return;
        var plan = SwitchPlanFor(s.Slot);
        if (plan is null) return;
        t.Switch = plan;
        t.SwitchAt = _connectedFor;
        t.SwitchDuration = _config.WeaponSwitchTime;
        _switchAnims++;
        if (UpperLog)
            GD.Print($"t {_connectedFor:F2} s: P{s.PlayerId} SWITCH to {s.Slot}: " + string.Join(" then ", plan.Segments.Select(g => $"{g.Clip} {g.From:F3}->{g.To:F3} s")) +
                     $" over {t.SwitchDuration:F2} s; the old weapon is stowed at {plan.Release * t.SwitchDuration:F2} s, the new one in hand at {plan.Take * t.SwitchDuration:F2} s ({(own ? "own" : "seen")}{(WeaponRig.OldStow ? ", --old-stow" : "")})");
    }

    SwitchPlan? _planToPistol, _planToPrimary;

    /// <summary>
    /// The switch to <paramref name="slot"/> from the other ranged slot (a class has two): <see cref="SwitchPlan.For"/> (<c>--old-stow</c>: the new weapon's clip alone, as before fix/stow-right),
    /// built once when the clips load (<see cref="UpperData"/>); null before that. No allocation per call.
    /// </summary>
    SwitchPlan? SwitchPlanFor(WeaponSlot slot) => slot == WeaponSlot.Pistol ? _planToPistol : _planToPrimary;

    /// <summary>
    /// When the weapon models change during a switch to the player's slot (fractions of the switch time): the old weapon onto its stow marker, the new one into the hand (<see cref="SwitchPlan"/>);
    /// 0.5 for both (the old rule) without the clips or with the aid off.
    /// </summary>
    (float Release, float Take) SwapFractionsFor(PlayerState s)
    {
        if (NoSwitchAnim || UpperData() is null || SwitchPlanFor(s.Slot) is not { } plan) return (0.5f, 0.5f);
        return (plan.Release, plan.Take);
    }

    /// <summary>Seconds of cross-fade between the pieces of a switch (the old weapon's stow and the new one's draw start from different poses; ours).</summary>
    const float SwitchJointBlend = 0.08f;

    /// <summary>After the locomotion pose: lays the switch's clip pieces over everything but the legs, fading in and out at its ends and across the joint between the pieces.</summary>
    void SwitchLayer(CharacterViewer character, PlayerState s, LocoTrack t)
    {
        if (t.Switch is not { } plan) return;
        float elapsed = (float)(_connectedFor - t.SwitchAt), duration = t.SwitchDuration;
        if (!s.Alive || elapsed < 0f || elapsed >= duration) { t.Switch = null; return; }
        float weight = CharacterViewer.Smooth(Math.Clamp(Math.Min(elapsed / SwitchBlendIn, (duration - elapsed) / SwitchBlendOut), 0f, 1f));   // animation pass 3: the smooth blend curve
        float fraction = elapsed / duration;
        var segment = plan.SegmentAt(fraction);
        if (plan.Crossfade(fraction, SwitchJointBlend / duration) is { } joint)
        {
            // The piece before holds its last pose underneath while the new piece fades in over it.
            character.SetLayer(PoseSlots.Switch, joint.Previous.Clip, joint.Previous.To, weight, false, joint.Previous.Mask);
            character.SetLayer(PoseSlots.SwitchNext, segment.Clip, segment.ClipTime(fraction), weight * CharacterViewer.Smooth(joint.Progress), false, segment.Mask);
        }
        else character.SetLayer(PoseSlots.Switch, segment.Clip, segment.ClipTime(fraction), weight, false, segment.Mask);
        if (LogUpper && ReferenceEquals(character, _local) && _connectedFor - t.SwitchTraceAt >= 0.05)
        {
            // Test aid: where the hand that carries the weapon is against the pelvis (last frame's pose), to see the clip move the arm.
            t.SwitchTraceAt = _connectedFor;
            if (character.Skeleton is { } skeleton && skeleton.FindBone("RIG_R_Hand") is var hand and >= 0 && skeleton.FindBone("RIG_Pelvis") is var pelvis and >= 0)
            {
                var rel = skeleton.GetBoneGlobalPose(pelvis).AffineInverse() * skeleton.GetBoneGlobalPose(hand).Origin;
                GD.Print($"t {_connectedFor:F2} s: switch trace {elapsed:F2}/{duration:F2} s, {segment.Clip} at {segment.ClipTime(fraction):F3} s, weight {weight:F2}: right hand {rel.X * 100f:F0},{rel.Y * 100f:F0},{rel.Z * 100f:F0} cm from the pelvis");
            }
        }
    }

    // ------------------------------------------------------------------------------------------------------------------------------------------ hit flinch

    readonly List<(int Victim, int Shooter, bool Head)> _flinchOwn = new();

    /// <summary>Own hit reports (from <see cref="UpdateHits"/>): a non-lethal ranged hit on another player flinches it on the next frame.</summary>
    void FlinchNoteOwnHit(HitReport hits)
    {
        if (NoFlinch || hits.Hits <= 0 || hits.LastVictim <= 0 || hits.LastMelee || hits.LastSplash || _client is null) return;
        if (hits.LastKilled) { if (hits.LastHead) LocoOf(hits.LastVictim).HeadHitAt = _connectedFor; return; }   // a killing hit: the head note counts for the death clip, no flinch (the death takes the body)
        _flinchOwn.Add((hits.LastVictim, _client.PlayerId, hits.LastHead));
    }

    /// <summary>Once per frame after the shots are drained (<c>HipFireBegin</c>): turns this frame's hits into flinches.</summary>
    void FlinchFeed(PlayerState me)
    {
        if (_client is null || _config is null) return;
        foreach (var shot in _frameShots)
        {
            if (shot.Hit is not (ShotHit.Body or ShotHit.Head)) continue;
            int victim = VictimAt(shot.End, shot.Shooter);
            if (victim > 0) FlinchHit(victim, shot.Shooter, shot.Hit == ShotHit.Head, me);
        }
        if (_flinchOwn.Count > 0)
        {
            foreach (var (victim, shooter, head) in _flinchOwn) FlinchHit(victim, shooter, head, me);
            _flinchOwn.Clear();
        }
    }

    PlayerState? StateOfPlayer(int id, PlayerState me)
    {
        if (_client is not null && id == _client.PlayerId) return me;
        foreach (var o in _lastOthers) if (o.PlayerId == id) return o;
        return null;
    }

    void FlinchHit(int victimId, int shooterId, bool head, PlayerState me)
    {
        if (victimId == shooterId) return;
        var t = LocoOf(victimId);
        if (head) t.HeadHitAt = _connectedFor;   // the death clip's head-shot pair reads it, flinch or not
        if (NoFlinch || UpperData() is not { } data || StateOfPlayer(victimId, me) is not { Alive: true } victim) return;
        if (victim.Class >= 0 && victim.Class < _config!.Classes.Count && _config.Classes[victim.Class].Race == Race.Orks) return;   // the boy's rig has its own clips (not in this pass)
        _flinchesNoted++;
        if (victim.Reaction != ReactionKind.None) { _flinchSkippedReaction++; return; }   // a stun or stagger shows; the flinch is for the hits that do not react
        if (_connectedFor - t.FlinchLast < data.Flinch.MinHitReactionTime) { _flinchSkippedGap++; return; }
        var shooter = StateOfPlayer(shooterId, me);
        var (behind, left) = shooter is { } sh ? FlinchRules.Side(victim.Position.X, victim.Position.Y, victim.Yaw, sh.Position.X, sh.Position.Y) : (false, false);
        string setName = FlinchRules.SetFor(t.WeaponName ?? "", victim.Charging);
        var set = data.Flinch.For(setName) ?? data.Flinch.For("Bolter Flinch") ?? data.Flinch.Sets.Values.FirstOrDefault();
        if (set is null) return;
        int slot = FlinchRules.Choose(set, head, behind, left);
        if (set.Slot(slot) is not { } clip) return;
        t.Flinch = clip; t.FlinchAt = t.FlinchLast = _connectedFor;
        _flinches++;
        if (UpperLog) GD.Print($"t {_connectedFor:F2} s: P{victimId} FLINCH {clip.Clip} ({setName} slot {slot}: {(head ? "head" : "body")} hit from {(behind ? "behind" : "the front")}, {(left ? "left" : "right")}; {clip.Length:F2} s)");
    }

    /// <summary>The additive flinch clip over whatever shows, until its end.</summary>
    void FlinchLayer(CharacterViewer character, PlayerState s, LocoTrack t)
    {
        if (t.Flinch is not { } clip) return;
        float age = (float)(_connectedFor - t.FlinchAt);
        if (NoFlinch || !s.Alive || s.Reaction != ReactionKind.None || age >= clip.Length) { t.Flinch = null; return; }
        float rate = clip.Length > 1e-3f ? ClipLength(clip.Clip, clip.Length) / clip.Length : 1f;
        character.SetLayer(PoseSlots.Flinch, clip.Clip, age * rate, 1f, clip.Additive, null);
        if (LogUpper && _connectedFor - _flinchTraceAt >= 0.04 && _local is { } me && ReferenceEquals(character, me))
        {
            _flinchTraceAt = _connectedFor;
            GD.Print($"t {_connectedFor:F2} s: flinch trace {age:F2}/{clip.Length:F2} s ({clip.Clip}): {BoneTrace(character, "RIG_Head", "RIG_SpineD")}");
        }
    }

    // ------------------------------------------------------------------------------------------------------------------------------------------ death before the ragdoll

    /// <summary>One death under way before its ragdoll: when the death was first seen, when the clip began (after waiting for the kill's cause), the clip, and the last frame's time for the slide.</summary>
    sealed class MarineDeath
    {
        public double Seen, Start = double.NaN, Last;
        public DeathClip? Clip;
        public MarineDeathKind Kind;
        public bool Declined;   // no clip for this death: straight to the ragdoll
    }

    readonly Dictionary<int, MarineDeath> _marineDeaths = new();

    /// <summary>Forgets a death clip under way (the player is alive again or gone).</summary>
    void MarineDeathForget(int id) => _marineDeaths.Remove(id);

    /// <summary>The time the death was first seen (a held death's ragdoll exists later; the death effects and sounds count from this), or <paramref name="fallback"/>.</summary>
    double DeathSeenAt(int id, double fallback) => _marineDeaths.TryGetValue(id, out var d) ? d.Seen : fallback;

    /// <summary>The body's last horizontal velocity decayed over the hold it has had, for the ragdoll to start with (it slid that far already).</summary>
    Vector3 HeldVelocity(int id, Vector3 velocity) =>
        _marineDeaths.TryGetValue(id, out var d) && !d.Declined && !double.IsNaN(d.Start) ? new Vector3(velocity.X, velocity.Y, velocity.Z) * DeathRules.HeldVelocityFactor((float)(_connectedFor - d.Start), DeathSlideSeconds) : velocity;

    static DeathClip? ResolveDeath(DeathAnimations death, MarineDeathKind kind)
    {
        if (death.Get(kind) is { } exact) return exact;
        // A missing special clip falls back to the default pair of the same side.
        bool back = kind is MarineDeathKind.HeadshotBack or MarineDeathKind.LasBack or MarineDeathKind.MeltaBack;
        return death.Get(back ? MarineDeathKind.DefaultBack : MarineDeathKind.DefaultFront) ?? death.Get(MarineDeathKind.DefaultFront);
    }

    /// <summary>
    /// Called by <see cref="ProcessDeaths"/> before a marine's ragdoll starts: poses the body in the death clip for the blow until the graph hands it to the ragdoll (the clip's powered-ragdoll tag; at most
    /// <see cref="DeathHoldCap"/>), and returns true while it does, so the death waits; false for anything else (no data, a fall out of the level, a victim that was in the air, a rig that cannot show it), and then
    /// the ragdoll starts from the pose the clip left, with the killing blow's push (<see cref="DeathKick"/>, unchanged). The clip waits up to <see cref="DeathCauseWait"/> for the kill's cause (the kill feed may
    /// arrive a snapshot after the death). The held body slides on with its last velocity, decaying (<see cref="DeathSlideSeconds"/>, ours), not through a wall. The own body shows too, from 0.12 s.
    /// </summary>
    bool MarineDeathHold(int id, CharacterViewer character, PlayerState state, bool own)
    {
        if (NoDeathAnim || UpperData() is not { } data || data.Death.Clips.Count == 0 || _config is null || character.Assets is null) return false;
        if (!_marineDeaths.TryGetValue(id, out var entry)) _marineDeaths[id] = entry = new MarineDeath { Seen = _connectedFor };
        if (entry.Declined) return false;
        if (entry.Clip is null)
        {
            var kill = _deathCauses.TryGetValue(id, out var c) && _connectedFor - c.At < 3.0 ? c.Kill : (KillView?)null;
            var t = LocoOf(id);
            if (kill is null && _connectedFor - entry.Seen < DeathCauseWait) { character.Visible = !own; return true; }   // the cause may still come: wait a frame or two (the body keeps its last pose; ProcessDeaths runs after the per-frame `character.Visible = state.Alive` reset of the remote loop, so this holds)
            if (!DeathRules.Holds(kill is { Cause: KillCause.World }, t.WasAirborne))
            {
                entry.Declined = true;
                if (UpperLog) GD.Print($"t {_connectedFor:F2} s: P{id} dies {(t.WasAirborne ? "in the air" : "out of the level")}: no standing clip, straight to the ragdoll ({(kill is { } kw ? kw.Cause.ToString() : "cause unknown")}{(own ? ", own" : "")})");
                return false;
            }
            bool has = kill is { HasDirection: true };
            float along = 0f, right = 0f;
            if (has)
            {
                var d = kill!.Value.Direction;
                along = d.X * -MathF.Sin(state.Yaw) + d.Y * MathF.Cos(state.Yaw);
                right = d.X * MathF.Cos(state.Yaw) + d.Y * MathF.Sin(state.Yaw);
            }
            var weapon = kill is { Cause: KillCause.Weapon } k && k.Index >= 0 && k.Index < _config.Weapons.Count ? DeathRules.WeaponOf(_config.Weapons[k.Index].Name) : WeaponDeath.Bolter;
            bool head = kill is { Cause: KillCause.Weapon } && _connectedFor - t.HeadHitAt < 0.5;
            var kind = DeathRules.Choose(t.WasSprint, t.WasCharging, head, weapon, has, along, right);
            if (ResolveDeath(data.Death, kind) is not { } clip || _assets?.Animation(clip.Clip) is null) { entry.Declined = true; return false; }
            entry.Clip = clip; entry.Kind = kind; entry.Start = entry.Last = _connectedFor;
            character.ClearPoseLayers();   // the placed frame's layers (recoil, fall, switch ...) are not part of the death
            _deathsHeld++;
            if (UpperLog || own) GD.Print($"t {_connectedFor:F2} s: P{id} dies: {clip.Clip} ({kind}{(head ? ", head shot" : "")}{(t.WasSprint ? ", sprinting" : "")}{(t.WasCharging ? ", charging" : "")}) for {Math.Min(clip.RagdollAt, DeathHoldCap):F2} s, then the ragdoll ({(kill is { } kv ? kv.Cause.ToString() : "cause unknown")}{(own ? ", own" : "")}; seen {_connectedFor - entry.Seen:F2} s ago)");
        }
        var held = entry.Clip!;
        float age = (float)(_connectedFor - entry.Start);
        if (age >= Math.Min(held.RagdollAt, DeathHoldCap)) return false;
        character.Visible = !own || age > 0.12f;
        SlideDeadBody(id, character, state.Team, (float)(_connectedFor - entry.Last));
        entry.Last = _connectedFor;
        float rate = held.Length > 1e-3f ? ClipLength(held.Clip, held.Length) / held.Length : 1f;
        character.Pose(held.Clip, age * rate, 0.167f);   // "Fast Blend" of the graph's transitions
        _deathHolds++;
        if (LogUpper && _connectedFor - _deathTraceAt >= 0.1) { _deathTraceAt = _connectedFor; GD.Print($"t {_connectedFor:F2} s: death trace P{id} {age:F2}/{Math.Min(held.RagdollAt, DeathHoldCap):F2} s: {BoneTrace(character, "RIG_Head", "RIG_Prop_Rhand")}"); }
        return true;
    }

    /// <summary>
    /// The held body keeps moving with its last horizontal velocity, slowing (ours: the original's powered ragdoll and its momentum are not modelled). Stopped by anything in its way: three rays (foot, middle and
    /// head height, like the movement code's capsule) along the step against the level the player's side collides with (<see cref="CollisionWorld.CharacterFor"/>: walls, crates, railings, the other side's
    /// spawn-room blockers); it follows the ground (a ray down from the new place sets its height) and stops at an edge instead of sliding over a drop.
    /// </summary>
    void SlideDeadBody(int id, CharacterViewer character, Team team, float dt)
    {
        if (!_motion.TryGetValue(id, out var m) || dt <= 0f || dt > 0.25f) return;
        float since = _marineDeaths.TryGetValue(id, out var d) && !double.IsNaN(d.Start) ? (float)(_connectedFor - d.Start) : 0f;
        var velocity = new Vector3(m.Velocity.X, m.Velocity.Y, 0f) * DeathRules.HeldVelocityFactor(since, DeathSlideSeconds);
        float speed = velocity.Length();
        if (speed < 0.05f) return;
        if (speed > MaxRagdollSpeed) velocity *= MaxRagdollSpeed / speed;
        var step = velocity * dt;
        var at = character.Position;
        if (_client?.Collision is { } world)
        {
            var level = world.CharacterFor(team);
            float length = step.Length();
            var dir = System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(step.X, step.Y, 0f));
            foreach (float height in SlideRayHeights)
                if (level.Raycast(new System.Numerics.Vector3(at.X, at.Y, at.Z + height), dir, length + 0.45f) is not null) return;
            var down = level.Raycast(new System.Numerics.Vector3(at.X + step.X, at.Y + step.Y, at.Z + 0.6f), new System.Numerics.Vector3(0f, 0f, -1f), 1.2f);
            if (down is null) return;   // an edge: the body stays
            character.Position = new Vector3(at.X + step.X, at.Y + step.Y, at.Z + 0.6f - down.Value.Distance);
            return;
        }
        character.Position = at + step;
    }

    static readonly float[] SlideRayHeights = [0.3f, 0.9f, 1.6f];

    double _deathTraceAt = double.NegativeInfinity, _flinchTraceAt = double.NegativeInfinity;

    /// <summary>Test aid: the named bones' places in the character's frame (game axes, centimetres), from the last drawn pose.</summary>
    static string BoneTrace(CharacterViewer character, params string[] bones)
    {
        if (character.Skeleton is not { } skeleton) return "no skeleton";
        var parts = new List<string>();
        foreach (var name in bones)
        {
            int bone = skeleton.FindBone(name);
            if (bone < 0) bone = skeleton.FindBone(name.Replace("Rhand", "RHand", StringComparison.Ordinal));
            if (bone < 0) continue;
            var p = skeleton.GetBoneGlobalPose(bone).Origin;
            parts.Add($"{name} {p.X * 100f:F0},{p.Y * 100f:F0},{p.Z * 100f:F0}");
        }
        return string.Join("; ", parts);
    }

    // ------------------------------------------------------------------------------------------------------------------------------------------ the jump pack in flight

    /// <summary>The jump pack's pass-2 data while the feature is on and loaded, else null.</summary>
    JumpPackAnimations? UpperJump => NoJumpUpper ? null : UpperData()?.JumpPack;

    /// <summary>
    /// After the flight pose is shown: the flight wobble and the upper-body actions of a marine in the air. The wobble is the graph's <c>Noise</c> blend (<c>SM_JMP_Blend_90_Pos_Fast</c>, looping) taken as the offset
    /// from its own first pose; the toss, the recoil of a shot and the reload go over the flight pose as they go over the locomotion (the jump pack's behaviour states take them in the air).
    /// </summary>
    void JumpUpperLayers(CharacterViewer character, PlayerState s, float? throwTime)
    {
        if (UpperJump is not { } jp || !_jumpTracks.TryGetValue(s.PlayerId, out var jt) || jt.View != JumpView.Air || _connectedFor - jt.PressAt < TakeOffBranch) return;
        if (jp.NoiseClip is not null && jp.NoiseLength > 1e-3f)
        {
            float time = (float)((_connectedFor - jt.Since) % jp.NoiseLength);
            if (character.SetLayer(PoseSlots.Wobble, jp.NoiseClip, time, WobbleWeight, jp.NoiseAdditive, null, jp.NoiseAdditive ? -1f : 0f)) _wobbleLayers++;
        }
        if (throwTime is { } tt && _tossAnimation is not null && _config is not null && TossShown(s) && ActionMask() is { } mask)   // perks/inert-2: no toss for a stim or an unknown item
        {
            if (character.SetPoseLayer(_tossAnimation, tt, ActionHandoff.ThrowWeight(_config.ThrowDuration, tt), additive: false, mask)) _jumpActionLayers++;
        }
        else
        {
            HipRecoil(character, s, HipOf(s.PlayerId));
            ReloadPose(character, s);
            StimPose(character, s, throwTime);   // perks/inert-2: a stim used in flight
            if (character.PoseLayerClip is not null) _jumpActionLayers++;
        }
    }

    /// <summary>The pound's get-up window (<c>BRANCH_to_Getup</c> of the impact clip): whether the marine is moving decides between the run get-up and the idle one (the graph's transition condition).</summary>
    void JumpGetupDecide(JumpTrack t, double sinceImpact, float speed, PlayerState s)
    {
        if (t.GetupDecided || UpperJump is not { RunClip: not null } jp || sinceImpact < jp.FromImpact) return;
        t.GetupDecided = true;
        t.GetupRun = speed >= jp.MinSpeed && _assets?.Animation(jp.RunClip!) is not null;
        if (t.GetupRun) _getupRuns++;
        if (UpperLog) GD.Print($"t {_connectedFor:F2} s: P{s.PlayerId} pound get-up at {sinceImpact:F2} s into the impact: {(t.GetupRun ? $"{jp.RunClip} (moving, {speed:F1} m/s)" : $"idle get-up (speed {speed:F1} m/s)")}");
    }

    // ------------------------------------------------------------------------------------------------------------------------------------------ screenshot aids

    /// <summary><c>--wait-switch</c> / <c>--wait-flinch</c> / <c>--wait-death</c> (screenshot aids): the screenshot waits for the own marine to be 0.2 to 0.4 s into a weapon switch / 0.06 to 0.14 s into a flinch / 0.2 to 0.4 s into its death clip.</summary>
    static readonly bool WaitSwitchShot = OS.GetCmdlineUserArgs().Contains("--wait-switch"), WaitFlinchShot = OS.GetCmdlineUserArgs().Contains("--wait-flinch"), WaitDeathShot = OS.GetCmdlineUserArgs().Contains("--wait-death");

    /// <summary><c>--wait-switch-window a,b</c> (fix/stow-right, with <c>--wait-switch</c> and <c>--screenshot-every</c>): the seconds into a switch the screenshots wait for (default 0.2 to 0.4).</summary>
    static readonly (double From, double To) WaitSwitchWindow = CmdArg("wait-switch-window")?.Split(',') is [var a, var b]
        && double.TryParse(a, System.Globalization.CultureInfo.InvariantCulture, out var from) && double.TryParse(b, System.Globalization.CultureInfo.InvariantCulture, out var to) ? (from, to) : (0.2, 0.4);

    /// <summary>
    /// <c>--bot-switch-every s</c> (fix/stow-right test aid): from 4 s after joining the bot stands still, looks ahead and presses Switch once every <c>s</c> seconds (primary → pistol → primary ...),
    /// so the own marine's switches can be watched and captured from the follow camera.
    /// </summary>
    static readonly double BotSwitchEvery = double.TryParse(CmdArg("bot-switch-every"), System.Globalization.CultureInfo.InvariantCulture, out var every) && every >= 0.7 ? every : 0;
    long _botSwitchPeriod = -1;

    PlayerInput BotSwitchInput(uint tick)
    {
        long period = (long)Math.Floor((_connectedFor - 4.0) / BotSwitchEvery);
        bool press = period != _botSwitchPeriod;
        _botSwitchPeriod = period;
        if (press && UpperLog) GD.Print($"t {_connectedFor:F2} s: bot-switch-every: Switch pressed");
        return new PlayerInput(tick, 0f, 0f, _yaw, press ? InputButtons.Switch : InputButtons.None, _pitch);
    }

    bool UpperWorthCapturing
    {
        get
        {
            if (!WaitSwitchShot && !WaitFlinchShot && !WaitDeathShot) return true;
            if (_client is null || !_loco.TryGetValue(_client.PlayerId, out var t)) return false;
            if (WaitSwitchShot) return t.Switch is not null && _connectedFor - t.SwitchAt >= WaitSwitchWindow.From && _connectedFor - t.SwitchAt <= WaitSwitchWindow.To;
            if (WaitFlinchShot) return t.Flinch is not null && _connectedFor - t.FlinchAt is >= 0.06 and <= 0.14;
            return _marineDeaths.TryGetValue(_client.PlayerId, out var d) && _connectedFor - d.Start is >= 0.2 and <= 0.4;
        }
    }

    // ------------------------------------------------------------------------------------------------------------------------------------------ log and info

    /// <summary>The bot log's pass-2 counters.</summary>
    string UpperStats() =>
        $"upper body: switches {_switchAnims}, flinches {_flinches} of {_flinchesNoted} hits (skipped: {_flinchSkippedGap} too soon, {_flinchSkippedReaction} reacting), deaths held {_deathsHeld} ({_deathHolds} frames), pound run get-ups {_getupRuns}, wobble layers {_wobbleLayers}, flight action layers {_jumpActionLayers}";

    /// <summary>The F1 line about animation pass 2.</summary>
    string UpperInfoLine() =>
        "Upper body (animation pass 2): " +
        $"weapon switch clip over the upper body{(NoSwitchAnim ? " (off)" : "")}, hit flinch{(NoFlinch ? " (off)" : "")}, death clip before the ragdoll{(NoDeathAnim ? " (off)" : "")}, jump-pack wobble, actions in flight and run get-up{(NoJumpUpper ? " (off)" : "")}; " +
        $"which clip for which hit or blow, the 0.1 s fades, the held death's length cap and the wobble's weight: ours. Loaded: {_upperNote}.";
}
