using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmClient.ImpactRules;
using SmContent;
using SmNet;
using SmSim;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// anim/melee-interaction, the client's side (rules in <see cref="MeleeInteraction"/>, the simulation's lock in <see cref="ChargeLock"/>; the owner's request of 2026-10-31: "very floaty
/// and disconnected ... improve the violence and effects around the charge ... much weightier/meatier/crunchier ... some interaction between melee participants"):
/// <list type="bullet">
/// <item><b>Charge impact</b> (every view): when a charge connects (the own one from the predicted contact, <see cref="GameClient.LastChargeContact"/>; others' from the phase change
/// into the collision stop, the victim at the lock spot): a hit-stop on both clips (<see cref="MeleeInteraction.ChargeHold"/>), the data's charge impact effects at the contact point
/// (the bull rush's <c>body_smash</c> material container resolved for the victim's armour, else the <c>body_smash</c> effect, and <c>body_smash_directional</c> along the charge; the
/// container's sound already plays: <c>NetworkView.Sound.Melee.cs</c>, now layered with the weapon's hit sound), a camera kick and shake for the charger and the victim (an observer
/// near it: a faint shake).</item>
/// <item><b>Reactions turned to the attacker</b>: a body in a hit reaction turns so the clip's side points at whoever put it there (the charger, else the attacker nearest it), eased.</item>
/// <item><b>The own hit lands on the hit frame</b>: an own melee hit predicted this frame starts the victim's data flinch at once (the server's reaction arrives a round trip later and
/// takes over; <see cref="FlinchHit"/>).</item>
/// </list>
/// Test aids: <c>--no-charge-impact</c> (no hit-stop, kick, impact effects or sound layer: the earlier 0.5 degree charge shake instead), <c>--no-reaction-facing</c>, <c>--no-hit-flinch</c>,
/// <c>--log-charge-impact</c>; the simulation's lock: <c>--no-charge-lock</c> (server and client).
/// </summary>
public partial class NetworkView
{
    static readonly bool NoChargeImpact = OS.GetCmdlineUserArgs().Contains("--no-charge-impact");
    static readonly bool NoReactionFacing = OS.GetCmdlineUserArgs().Contains("--no-reaction-facing");
    static readonly bool NoHitFlinch = OS.GetCmdlineUserArgs().Contains("--no-hit-flinch");
    static readonly bool LogChargeImpact = OS.GetCmdlineUserArgs().Contains("--log-charge-impact");

    /// <summary>The data's charge effects played by name (the effects package): the impact and its directional burst.</summary>
    const string ChargeImpactFx = "body_smash", ChargeDirectionalFx = "body_smash_directional";
    internal static readonly string[] ChargeImpactEffects = [ChargeImpactFx, ChargeDirectionalFx];

    readonly Dictionary<int, RushPhase> _ciRush = new();
    readonly Dictionary<int, double> _chargeStops = new();
    readonly List<int> _chargeStopsDone = new();
    readonly Dictionary<int, (int Source, double At, bool Charge)> _reactionSource = new();
    readonly Dictionary<int, (ReactionKind Kind, int Ticks)> _ciReaction = new();
    readonly List<PlayerState> _ciAll = new();
    readonly HashSet<int> _ciPresent = new();
    readonly List<int> _ciGone = new();
    uint _ciOwnContactTick;
    double _punchAt = double.NegativeInfinity;
    float _punchRadians;
    int _chargeImpacts, _chargeImpactsOwn, _chargeImpactsFx, _reactionsFaced, _hitFlinches;

    /// <summary>Once per frame (player and observer frames): charge connects, reaction sources, the own hit's flinch.</summary>
    void MeleeInteractionFrame(PlayerState me, IReadOnlyList<PlayerState> others, bool observer)
    {
        if (_client is null || _config is null) return;
        double now = _connectedFor;
        var all = _ciAll;
        all.Clear();
        int ownId = observer ? 0 : _client.PlayerId;
        if (!observer && me.PlayerId > 0) all.Add(me);
        for (int i = 0; i < others.Count; i++) all.Add(others[i]);
        _ciPresent.Clear();
        // 1. Charge connects. The own: the predicted contact (exact, on its tick); without the lock the phase change as for the others.
        if (!observer && _config.ChargeLock && _client.LastChargeContact is var (contactTick, target, spot) && contactTick > _ciOwnContactTick)
        {
            _ciOwnContactTick = contactTick;
            if (me.Alive && StateOfPlayer(target, me) is { } victim)
            {
                if (!NoLockDraw) _lockDraw = (target, spot, now, -1.0);
                ChargeImpact(me, victim with { Position = NoLockDraw ? victim.Position : new Num.Vector3(spot.X, spot.Y, victim.Position.Z) }, me, ownId);
            }
        }
        foreach (var p in all)
        {
            _ciPresent.Add(p.PlayerId);
            var before = _ciRush.GetValueOrDefault(p.PlayerId, RushPhase.None);
            _ciRush[p.PlayerId] = p.Alive ? p.Rush : RushPhase.None;
            bool ownByContact = p.PlayerId == ownId && _config.ChargeLock;
            if (!ownByContact && p.Alive && ChargeConnect.Connected(before, p.Rush))
            {
                int v = MeleeInteraction.ChargeVictim(p, all, _config);
                if (v > 0 && all.FirstOrDefault(x => x.PlayerId == v) is { PlayerId: > 0 } victim) ChargeImpact(p, victim, me, ownId);
            }
        }
        // 2. Reaction sources: when a reaction starts (or restarts), who caused it (a charge connect recorded it already).
        foreach (var p in all)
        {
            var was = _ciReaction.GetValueOrDefault(p.PlayerId);
            if (!p.Alive || p.Reaction == ReactionKind.None) { _ciReaction.Remove(p.PlayerId); if (_reactionSource.TryGetValue(p.PlayerId, out var r) && now - r.At > 0.5) _reactionSource.Remove(p.PlayerId); continue; }
            bool started = was.Kind == ReactionKind.None || p.ReactionTicks < was.Ticks || p.Reaction != was.Kind;
            _ciReaction[p.PlayerId] = (p.Reaction, p.ReactionTicks);
            if (!started) continue;
            if (_reactionSource.TryGetValue(p.PlayerId, out var known) && known.Charge && now - known.At < 0.5) continue;
            int source = MeleeInteraction.ReactionSource(p, all);
            if (source > 0) _reactionSource[p.PlayerId] = (source, now, false);
        }
        // 3. The own melee hit: the victim's flinch on the hit frame (the server's reaction follows a round trip later).
        if (!observer && !NoHitFlinch && me.Alive)
            foreach (var (_, events) in _frameOwnEvents)
            {
                if (events.MeleeHitStep <= 0) continue;
                int victim = MeleePresentation.Victim(me, others, _config, _client.MeleeLockTarget);
                if (victim <= 0) continue;
                FlinchHit(victim, me.PlayerId, false, me);
                _reactionSource[victim] = (me.PlayerId, now, false);
                _hitFlinches++;
            }
        ChargeShotsFrame();
        // Expired stops and players gone.
        _chargeStopsDone.Clear();
        foreach (var (id, start) in _chargeStops) if (!MeleeInteraction.ChargeHoldActive((float)(now - start))) _chargeStopsDone.Add(id);
        foreach (int id in _chargeStopsDone) _chargeStops.Remove(id);
        _ciGone.Clear();
        foreach (var id in _ciRush.Keys) if (!_ciPresent.Contains(id)) _ciGone.Add(id);
        foreach (var id in _ciGone) { _ciRush.Remove(id); _ciReaction.Remove(id); _reactionSource.Remove(id); _reactionYaw.Remove(id); }
    }

    /// <summary>One charge connect, shown once: hit-stop, effects, camera, the victim's source.</summary>
    void ChargeImpact(PlayerState charger, PlayerState victim, PlayerState me, int ownId)
    {
        double now = _connectedFor;
        _chargeImpacts++;
        ChargeShotsBegin();
        bool ownCharger = charger.PlayerId == ownId && ownId > 0, ownVictim = victim.PlayerId == ownId && ownId > 0;
        if (ownCharger) _chargeImpactsOwn++;
        _reactionSource[victim.PlayerId] = (charger.PlayerId, now, true);
        if (NoChargeImpact) { if (ownCharger || ownVictim) FxShake(ChargeShakeRadians); return; }   // before: anim/melee-flow's 0.5 degree shake
        _chargeStops[charger.PlayerId] = now;
        _chargeStops[victim.PlayerId] = now;
        // Camera: a kick and a shake for the two; a faint shake near it.
        if (ownCharger) { Punch(MeleeInteraction.ChargerPunchDegrees); FxShake(MeleeInteraction.ChargerShakeDegrees * MathF.PI / 180f); }
        else if (ownVictim) { Punch(-MeleeInteraction.VictimPunchDegrees); FxShake(MeleeInteraction.VictimShakeDegrees * MathF.PI / 180f); }
        else if (_camera is not null)
        {
            var mid = (charger.Position + victim.Position) * 0.5f;
            float shake = MeleeInteraction.ObserverShake(_camera.GlobalPosition.DistanceTo(ToGodot(mid + new Num.Vector3(0f, 0f, 1.2f))));
            if (shake > 0f) FxShake(shake);
        }
        // The data's impact effects at the contact point: on the victim's chest surface toward the charger.
        var line = new Num.Vector2(victim.Position.X - charger.Position.X, victim.Position.Y - charger.Position.Y);
        if (line.LengthSquared() < 1e-6f) line = new Num.Vector2(-MathF.Sin(charger.Yaw), MathF.Cos(charger.Yaw));
        line = Num.Vector2.Normalize(line);
        var contact = victim.Position + new Num.Vector3(-line.X * 0.4f, -line.Y * 0.4f, 1.25f);
        int played = ChargeImpactFxPlay(charger, contact, new Num.Vector3(line.X, line.Y, 0f), victim.Shield > 0f || victim.PlayerId != ownId);
        if (LogChargeImpact || _bot)
            GD.Print($"t {now:F2} s: charge impact P{charger.PlayerId} -> P{victim.PlayerId}{(ownCharger ? " (own charge)" : ownVictim ? " (on me)" : "")}: apart {Num.Vector2.Distance(new(charger.Position.X, charger.Position.Y), new(victim.Position.X, victim.Position.Y)):F2} m, " +
                     $"charger facing off {MathF.Abs(MeleeInteraction.Wrap(charger.Yaw - MeleeInteraction.Bearing(charger.Position, victim.Position))) * 180f / MathF.PI:F1} deg, effects {played}; impacts {_chargeImpacts}");
    }

    /// <summary>Plays the charge's impact effects; returns how many started.</summary>
    int ChargeImpactFxPlay(PlayerState charger, Num.Vector3 contact, Num.Vector3 along, bool armourUp)
    {
        if (_fx is null || !FxActive) return 0;
        var at = ToGodot(contact);
        var dir = DirToGodot(along);
        var outward = -dir;
        int n = 0;
        // The material container the charging wargear names (body_smash), resolved for the victim's armour; its effects when this client compiled them.
        string? wargear = Rush.IsDash(charger.Rush) ? Loadouts.JumpPack(_config!, charger)?.Name : MeleeWargear(charger);
        if (_catalogue is not null && _soundTriggers is not null && wargear is not null && _soundTriggers.ChargeHitContainers.TryGetValue(wargear, out var container))
            foreach (var p in _catalogue.Resolve(new FxMaterialTrigger(container, ""), ShotHit.Body, 0, armourUp))
                if (_fx.System(p.Name) is not null && _fx.Play(p.Name, at, AxesFromY(outward), p.Scale <= 0f ? 1f : p.Scale, null, null, p.Lifetime, 0.0, false, 2) is not null) n++;
        if (n == 0 && _fx.System(ChargeImpactFx) is not null && _fx.Play(ChargeImpactFx, at, AxesFromY(outward), 1f, null, null, 0f, 0.0, false, 2) is not null) n++;
        if (_fx.System(ChargeDirectionalFx) is not null && _fx.Play(ChargeDirectionalFx, at, AxesFromY(dir), 1f, null, null, 0f, 0.0, false, 2) is not null) n++;
        if (n > 0) _chargeImpactsFx++;
        return n;
    }

    /// <summary>Starts the own camera's kick (degrees of pitch; positive dips the view, negative throws it up).</summary>
    void Punch(float degrees) { _punchAt = _connectedFor; _punchRadians = degrees * MathF.PI / 180f; }

    /// <summary>The own camera's kick now (radians of pitch; 0 without one).</summary>
    float PunchNow() => double.IsFinite(_punchAt) ? _punchRadians * MeleeInteraction.PunchAt((float)(_connectedFor - _punchAt)) : 0f;

    /// <summary>Seconds a player's clips are held back by a charge connect now (0 without one).</summary>
    float ChargeStopHold(int id) => _chargeStops.TryGetValue(id, out double start) ? MeleeInteraction.ChargeHold((float)(_connectedFor - start)) : 0f;

    readonly Dictionary<int, float> _reactionYaw = new();

    static readonly bool NoLockDraw = OS.GetCmdlineUserArgs().Contains("--no-lock-draw");
    /// <summary>The own charge's victim drawn at the lock spot: id, spot, when the contact was predicted, when the drawn victim first showed its reaction (-1: not yet).</summary>
    (int Victim, Num.Vector3 Spot, double At, double ReactedAt) _lockDraw = (0, default, double.NegativeInfinity, -1.0);

    /// <summary>
    /// The own view's half of the lock: the victim of the own predicted contact is drawn at the lock spot (where the server puts it) from the contact tick, instead of where the interpolated
    /// snapshots still have it a round trip behind; once the drawn victim shows its reaction (the snapshots reached the contact) the override fades over
    /// <see cref="MeleeInteraction.LockDrawFade"/> (the server's position is the spot by then), or after <see cref="MeleeInteraction.LockDrawMax"/> (no stun: hyper armour).
    /// </summary>
    IReadOnlyList<PlayerState> LockDrawn(IReadOnlyList<PlayerState> drawn)
    {
        if (_lockDraw.Victim <= 0) return drawn;
        double now = _connectedFor;
        int index = -1;
        for (int i = 0; i < drawn.Count; i++) if (drawn[i].PlayerId == _lockDraw.Victim) { index = i; break; }
        if (index < 0 || !drawn[index].Alive) { _lockDraw.Victim = 0; return drawn; }
        var v = drawn[index];
        if (_lockDraw.ReactedAt < 0 && v.Reaction != ReactionKind.None) _lockDraw.ReactedAt = now;
        float w = MeleeInteraction.LockDrawWeight((float)(now - _lockDraw.At), _lockDraw.ReactedAt < 0 ? -1f : (float)(now - _lockDraw.ReactedAt));
        if (w <= 0f) { _lockDraw.Victim = 0; return drawn; }
        var list = drawn as List<PlayerState> ?? drawn.ToList();
        var at = new Num.Vector3(_lockDraw.Spot.X, _lockDraw.Spot.Y, v.Position.Z);
        list[index] = v with { Position = Num.Vector3.Lerp(v.Position, at, w) };
        return list;
    }

    /// <summary>
    /// The body yaw of a reacting player turned toward its attacker (<see cref="MeleeInteraction.FacingFor"/>, eased from <paramref name="current"/>), or null: not reacting, no known
    /// attacker, <c>--no-reaction-facing</c>.
    /// </summary>
    float? ReactionFacingYaw(PlayerState s, float current, float dt)
    {
        if (NoReactionFacing || _client is null || !s.Alive || s.Reaction == ReactionKind.None || !_reactionSource.TryGetValue(s.PlayerId, out var src)) return null;
        if (StateOfPlayer(src.Source, _client.Predicted) is not { Alive: true } attacker) return null;
        float target = MeleeInteraction.FacingFor(s.ReactionDirection, MeleeInteraction.Bearing(s.Position, attacker.Position));
        float yaw = MeleeInteraction.Ease(_reactionYaw.TryGetValue(s.PlayerId, out var last) ? last : current, target, dt);
        if (!_reactionYaw.ContainsKey(s.PlayerId)) _reactionsFaced++;
        _reactionYaw[s.PlayerId] = yaw;
        return yaw;
    }

    /// <summary>Forgets the eased reaction yaw once the reaction is over (the turn-in-place body takes over from the yaw it was given).</summary>
    void ReactionFacingDone(int id) => _reactionYaw.Remove(id);

    /// <summary>
    /// <c>--charge-shots &lt;dir&gt;</c> (capture aid, a windowed client): after each charge connect it shows, saves the frames at <see cref="ChargeShotTimes"/> seconds after it as
    /// <c>charge&lt;n&gt;_&lt;ms&gt;.png</c> (and one 0.1 s before the impact's frame is not possible: the strip starts at the impact). At most <c>--charge-shots-max</c> (default 4) connects.
    /// </summary>
    static readonly string? ChargeShotsDir = OS.GetCmdlineUserArgs() is var a && Array.IndexOf(a, "--charge-shots") is int i && i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
    static readonly int ChargeShotsMax = (int)CmdFloat("--charge-shots-max", 4f);
    static readonly double[] ChargeShotTimes = [0.0, 0.033, 0.067, 0.1, 0.15, 0.25, 0.4, 0.6, 0.9];
    double _chargeShotAt = double.NegativeInfinity;
    int _chargeShotSet, _chargeShotNext;

    void ChargeShotsBegin()
    {
        if (ChargeShotsDir is null || _chargeShotSet >= ChargeShotsMax || _chargeShotNext < ChargeShotTimes.Length && double.IsFinite(_chargeShotAt)) return;
        _chargeShotSet++; _chargeShotAt = _connectedFor; _chargeShotNext = 0;
    }

    /// <summary>Per frame after drawing: the strip's frames that are due.</summary>
    void ChargeShotsFrame()
    {
        if (ChargeShotsDir is null || !double.IsFinite(_chargeShotAt) || _chargeShotNext >= ChargeShotTimes.Length) return;
        double since = _connectedFor - _chargeShotAt;
        if (since + 1e-6 < ChargeShotTimes[_chargeShotNext]) return;
        System.IO.Directory.CreateDirectory(ChargeShotsDir);
        string path = System.IO.Path.Combine(ChargeShotsDir, $"charge{_chargeShotSet}_{(int)Math.Round(since * 1000):D4}.png");
        var err = GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print(err == Error.Ok ? $"charge shot -> {path}" : $"charge shot failed: {err}");
        while (_chargeShotNext < ChargeShotTimes.Length && ChargeShotTimes[_chargeShotNext] <= since + 1e-6) _chargeShotNext++;
    }

    /// <summary>The bot log's part.</summary>
    string MeleeInteractionLog() => $"melee interaction: charge impacts {_chargeImpacts} (own {_chargeImpactsOwn}, with effects {_chargeImpactsFx}); predicted contacts {_client?.ChargeContactsPredicted ?? 0}; reactions faced {_reactionsFaced}; hit-frame flinches {_hitFlinches}";
}
