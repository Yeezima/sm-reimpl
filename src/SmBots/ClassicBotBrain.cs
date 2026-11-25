using System;
using System.Collections.Generic;
using System.Linq;
using SmContent;
using SmNet;
using SmSim;

namespace SmBots;

/// <summary>
/// The Classic PvP test bot's fight layer (test script, **ours**, not the original AI; see <see cref="ClassicBotBrain"/> in ClassicBotBrain.World.cs for the whole brain).
/// <see cref="BotInput"/>: without a target the input is a function of the tick and player id only: a 10 s cycle of running forward while turning (5 s), sprinting while
/// turning (3 s) and standing (2 s); players are offset in phase and alternate turn direction. While a living enemy is visible, the bot aims at the nearest one (from its
/// eye, <see cref="SimConfig.EyeHeight"/> above its feet, to <see cref="IBotWorldView.AimHeightOf"/> above the target's feet, at the interpolated position it is shown at,
/// which is what lag compensation rewinds to), fires the weapon in hand (held for a full-automatic weapon; for a semi-automatic one pressed on a released trigger, so press
/// and release alternate ticks) and strafes sideways instead of running at the target. Reloading on an empty clip is automatic in the sim. Weapons
/// (<see cref="BotWantsSwitch"/>): with the primary empty it switches to a loaded pistol, with the pistol empty back to a primary that has ammunition left. Within
/// <see cref="BotMeleeDistance"/>, if its class has a melee combo, it stands and taps melee instead of firing: pressed for one tick, released, and pressed again while the
/// attack runs (the simulation buffers it and moves on at the combo window), so the combo chains. With <c>--bot-melee</c> it runs (sprints beyond 6 m) straight at the
/// target instead of firing and strafing. M19: on a map "a target" is an enemy it can see within its weapon's range (<see cref="BotCanFight"/>); with none, the strategy
/// runs (<see cref="IBotStrategy.Idle"/>: the route, chase, roam and map centre).
/// </summary>
public sealed partial class ClassicBotBrain
{
    PlayerInput BotInput(uint tick)
    {
        int id = Math.Max(_client?.PlayerId ?? 0, 0);
        double t = tick * (double)_config!.TickSeconds + id * 3.7;
        double phase = t % 10.0;
        var me = _client!.Predicted;
        _botExtrasTick = false;
        BotHazards(me);   // bot-grenade-safety: the bot's own live grenades and waiting rounds this tick (ClassicBotBrain.BotGrenades.cs)
        // The client's test aids that script the whole input (--jump-test, --slash-test, --hop-rules-test, --stab-recovery-test, --swing-test, --stomp-move-test,
        // --camera-test, --fire-test, --carry-test, --dash-wall-test; the client's NetworkView.*Test.cs files), in that order; none on a server.
        if (_hooks.TestAidInput(me, tick) is { } testAid) return testAid;
        // A navigation jump in flight keeps its checked controls (ClassicBotBrain.Nav.cs).
        if (BotNavFlightInput(me, tick) is { } flightInput) return flightInput;
        _strategy.Begin(this, me);   // the Classic strategy: the lives for the lane legs and the idle cycle, the periodic melee push (ClassicBotStrategy)
        // M44a: during the match's "MATCH BEGINS" countdown (attacks do not work yet) the bot holds still in its spawn room: no wasted shots, no stale path.
        if (_client.Match.Effective() == MatchState.Countdown) { _botNote = "countdown: holding"; _navAgent?.Reset(); return new PlayerInput(tick, 0f, 0f, _yaw, InputButtons.None, _pitch); }
        _botNote = "";
        // Added content's own bot (the client's ExtrasBotInput hook, through IBotHooks.AddedContentInput; Musou Mode co-op: deploy with the team, fight the Orks).
        if (_hooks.AddedContentInput(me, tick) is { } extrasInput) { _botExtrasTick = true; return extrasInput; }   // EXTRAS-HOOK (via the client's adapter). bot-grenade-safety: the hold leaves added content's bot alone
        // The strategy ahead of the fight: at low health cover first, then back into the fight; Seize Ground (M16): go to a point not held, fight on the way and hold it.
        if (_strategy.BeforeFight(this, me, tick, phase) is { } strategyInput) return strategyInput;
        // M19: on a map it fights only an enemy it sees within BotFightRange; otherwise it chases (ClassicBotBrain.Bot.cs).
        if (me.Alive && BotFightTarget(me, out _) is { } target)
        {
            float dx = target.Position.X - me.Position.X, dy = target.Position.Y - me.Position.Y;
            float dz = target.Position.Z + AimHeightOf(target) - (me.Position.Z + _config.EyeHeight);
            _yaw = MathF.Atan2(-dx, dy);   // direction (-sin yaw, cos yaw)
            _pitch = Math.Clamp(MathF.Atan2(dz, MathF.Sqrt(dx * dx + dy * dy)), -MaxPitchDown, MaxPitchUp);
            _botTickEnemy = target;   // stream C: the strafing and straight-run measurements (ClassicBotBrain.BotTactics.cs)
            _strategy.NoteFight(this, me, target);   // 2026-11-07: the lane leg ends; afterwards the idle cycle starts at the hold again (ClassicBotBrain.BotRoam.cs)
            // Jump pack (assault classes): jump at targets 8-30 m away and pound from above (ClassicBotBrain.Jump.cs).
            if (BotDashAttackInput(me, target, tick) is { } dashInput) return dashInput;   // protocol 30 test aid --bot-dash-attack (ClassicBotBrain.DashAttack.cs)
            if (BotJumpInput(me, target, tick) is { } jumpInput) return jumpInput;
            // 2026-11-08: a melee push keeps closing in sight instead of strafing until the jump, charge or melee range (ClassicBotBrain.BotPush.cs).
            if (_strategy.FightMove(this, me, target, tick, MathF.Sqrt(dx * dx + dy * dy)) is { } pushInput) return pushInput;
            // Evade roll (Tactical / Devastator classes): sideways every ~6 s (ClassicBotBrain.Evade.cs).
            if (_strategy.Post(this, me) is null && BotEvadeInput(me, tick) is { } evadeInput) return evadeInput;   // 2026-11-08: no rolls off a post's edge
            bool hasMelee = Loadouts.Melee(_config, me).Count > 0;
            // perks/inert-2: a bot carrying combat stims uses them once it has a target within 20 m (melee bots too; ours, test script).
            if (StimOf(me) is not null && BotThrow(me, target, MathF.Sqrt(dx * dx + dy * dy)) is var stimPress and not InputButtons.None)
                return new PlayerInput(tick, 0f, 1f, _yaw, stimPress, _pitch);
            // The charge (M29): sometimes, at 4-9 m, run at the target and press F once moving fast enough (ClassicBotBrain.Reactions.cs).
            // Same floor only: a target more than BotMeleeHeight above or below is not in reach (QA 2026-10-28: bots swung at an
            // enemy 5 m below a ledge for 40 s).
            bool sameFloor = MathF.Abs(target.Position.Z - me.Position.Z) <= BotMeleeHeight;
            // Night batch 2026-11-06: a reaction time before the first melee action or charge on an enemy in reach (ClassicBotBrain.BotAim.cs), also before answering its attack; an action under way goes on.
            bool meleeActive = me.MeleeStep > 0 || me.MeleeHeld || me.MeleeHeavyHeld || me.MeleeQueued;
            bool meleeReady = BotMeleeReady(target, sameFloor && dx * dx + dy * dy <= 81f, MathF.Sqrt(dx * dx + dy * dy));
            // bot-grenade-safety: no charge into the bot's own live grenade (the target inside its blast, or the run through it; ClassicBotBrain.BotGrenades.cs).
            bool chargeHeld = me.Rush == RushPhase.None && BotChargeIntoOwnBlast(me, target);
            if (!chargeHeld && (meleeReady || me.Rush != RushPhase.None || _botChargeRunSince >= 0) && BotRushInput(me, tick, dx * dx + dy * dy) is { } rushInput) return rushInput;
            if (hasMelee && sameFloor && dx * dx + dy * dy <= BotMeleeDistance * BotMeleeDistance)
            {
                // Press on a released key unless a press is already buffered; the next tick releases it. During the second light
                // attack the press is the heavy strike (M28: X X Y, the combo's third heavy strike), when the class has one.
                // bot-grenade-safety: no new swing (and its lunge) at a target inside the bot's own live grenade's blast; the hold backs the bot out.
                bool press = !me.MeleeHeld && !me.MeleeHeavyHeld && !me.MeleeQueued && (meleeReady || meleeActive) && !BotMeleeIntoOwnBlast(me, target);
                // Weapons C: a single-attack combo (the Devastator's kick) goes on to its heavy strike (the stomp), and a heavy strike with a
                // Y,Y finisher chains into it.
                int heavies = Loadouts.MeleeHeavy(_config, me).Count;
                // feel/melee-charge-2 test aid --bot-heavy-chain: F from rest (the backhand), F again during it (the Y,Y chain to the stomp), and so on.
                bool heavy = (BotHeavyChain && heavies >= 1 && (!me.MeleeHeavy || Combat.MeleeStepOf(_config, me) is { YNext: > 0 }))
                    || (me.MeleeStep == 2 && !me.MeleeHeavy && heavies >= 3)
                    || (me.MeleeStep == 1 && !me.MeleeHeavy && Loadouts.Melee(_config, me).Count == 1 && heavies >= 1)
                    || (me.MeleeHeavy && Combat.MeleeStepOf(_config, me) is { YNext: > 0 });
                // Stream C: some swings are deliberately missed (the view turned off the target for that attack; ClassicBotBrain.BotTactics.cs).
                return new PlayerInput(tick, 0f, 0f, BotSwingYaw(me, press, MathF.Sqrt(dx * dx + dy * dy)), press ? heavy ? InputButtons.MeleeHeavy : InputButtons.Melee : InputButtons.None, _pitch);
            }
            if (_botMelee) return new PlayerInput(tick, 0f, 1f, _yaw, dx * dx + dy * dy > 36f ? InputButtons.Sprint : InputButtons.None, _pitch);
            // Strafe only onto walkable ground (navigation graph, ClassicBotBrain.Nav.cs): the other side or none otherwise.
            // Stream C: left and right in legs, toward a flank while an ally engages the same enemy (ClassicBotBrain.BotTactics.cs); --bot-old-tactics: one side, standing 2 s of every 10.
            float strafe = BotOldTactics ? BotWalkableStrafe(me, phase >= 8.0 ? 0f : id % 2 == 0 ? 1f : -1f) : BotStrafe(me, target);
            float horizontal = MathF.Sqrt(dx * dx + dy * dy);
            var grenade = BotThrow(me, target, horizontal);
            // Throwing (pressed now or under way until the release). bot-grenade-safety (Classic PvP, a damaging item): at the predicted landing point, under way never nearer than
            // the one checked at the press (ClassicBotBrain.BotGrenades.cs); else (--old-bot-throws, stims, the blind grenade) at the ground BotThrowShort m short of the target, from
            // before the 2026-10-29 bounce rule (test script heuristic).
            if (grenade != InputButtons.None || (me.Throwing && me.ThrowTime <= _config.ThrowRelease + 0.05f))
            {
                if (BotThrowAimPoint(me, target) is { } landing) { _botThrowAim = landing; BotAimThrow(me, landing); }
                else _pitch = Math.Clamp(MathF.Atan2(target.Position.Z - (me.Position.Z + _config.EyeHeight), BotGrenadeSafety.OldAimDistance(horizontal)), -MaxPitchDown, MaxPitchUp);
            }
            else if (!me.Throwing) { _botThrowPressAim = null; _botThrowAim = null; }   // no throw under way: a press's landing (and an aborted throw's aim) does not outlive it
            // The melta only reaches its cone length: beyond it, run at the target (no fire) instead of strafing.
            if (Loadouts.InHand(_config, me) is { Delivery: DeliveryKind.Cone, Cone: { } cone } && horizontal > cone.Length * 0.9f)
                return BotOldTactics ? new PlayerInput(tick, 0f, 1f, _yaw, grenade, _pitch) : BotMoveInput(tick, BotWeave(me, new Vector2(-MathF.Sin(_yaw), MathF.Cos(_yaw)), horizontal), grenade);   // stream C: weaving
            // A switch in the same tick would win and swallow the throw (and its 4 s spacing), so a throw drops the switch.
            var weaponButtons = BotWeaponButtons(me, tick, target);
            if (grenade != InputButtons.None) weaponButtons &= ~InputButtons.Switch;
            // Stream C: a gunfight at range with a primary weapon is fought in aim mode, as a person does (the data's tighter aiming spread; aiming speeds), not while throwing.
            if (grenade == InputButtons.None && !me.Throwing) weaponButtons |= BotAimButton(me, horizontal);
            // M44a: a ranged shot at an enemy gets the aim error (BotApplyAim); a throw keeps the exact aim.
            _botAimNow = grenade == InputButtons.None && !me.Throwing;
            _botAimTarget = target.PlayerId;   // the trigger discipline's reaction delay (ClassicBotBrain.BotAim.cs)
            BotNoteEngagement(horizontal);
            // M44a: far from the target (more than BotAdvanceBeyond m) the bot keeps firing and walks toward it along its path, so that long-range
            // fights close up (no sprint: it would stop the fire). Without a graph, or at the end of the path, it strafes as before.
            // 2026-11-08: a bot with a post (ramp top, balcony) fights from there, stepping back onto it while firing (ClassicBotBrain.BotPosts.cs).
            if (_strategy.PostReturn(this, me) is { } postSpot && Nav is not null)
            {
                var (back, backSpeed, _, _) = BotSteer(me, postSpot, "back to post");
                if (back.LengthSquared() > 1e-6f) return BotMoveInput(tick, back * backSpeed, grenade | weaponButtons | _navJump);
            }
            if (horizontal > BotAdvanceBeyond && Nav is not null && _strategy.Post(this, me) is null)
            {
                var (advance, speed, _, _) = BotSteer(me, BotPathGoal(target.Position), $"advance on P{target.PlayerId}");
                // Not along a path that flies a jump link (the take-off needs the bot still); drops are fine (a spawn room's exit is one).
                bool flies = _navAgent?.Path is { } adv && adv.Waypoints.Skip(Math.Max(_navAgent.Waypoint, 0)).Take(6).Any(w => w.Kind == NavLinkKind.Jump);
                if (advance.LengthSquared() > 1e-6f && !flies)
                    return BotMoveInput(tick, BotWeave(me, advance, horizontal) * speed, grenade | weaponButtons | _navJump);   // stream C: weaving, not a straight line
            }
            return new PlayerInput(tick, strafe, 0f, _yaw, grenade | weaponButtons, _pitch);
        }
        _botMeleeReaction?.Step(_config.TickSeconds, null);   // no target in sight: the melee reaction forgets it
        _botTargetSwinging = false;
        // Nothing to fight: the strategy (Classic: the --bot-goal / --bot-route test aid's route, else the chase, hunt, roam and map centre); null runs the old circle below.
        if (_strategy.Idle(this, me, tick) is { } idleInput) return idleInput;
        float turnRate = id % 2 == 0 ? 1.1f : -1.1f;   // about a 6 m running circle
        _yaw = (float)(t * turnRate % (2 * Math.PI));
        _pitch = 0f;
        // --bot-fire: keep shooting without a target too (sprinting would stop the fire, so it runs instead). Not on a map:
        // there it fires only at an enemy it sees (M19), so the shots do not go into walls.
        bool idleFire = _botFire && me.Alive && (_client.Collision is null || BotFireWalls);
        var idle = idleFire ? BotWeaponButtons(me, tick, null) : InputButtons.None;
        if (phase >= 8.0) return new PlayerInput(tick, 0f, 0f, _yaw, idle);
        // With --bot-fire it never sprints (released-trigger ticks included), so a charge or the fire is not interrupted.
        return new PlayerInput(tick, 0f, 1f, _yaw, idle != InputButtons.None || idleFire ? idle : phase >= 5.0 ? InputButtons.Sprint : InputButtons.None);
    }

    /// <summary>
    /// The bot's weapon buttons: a switch press edge when <see cref="BotWantsSwitch"/>, else the trigger.
    /// <para>
    /// Charge weapons (<see cref="WeaponSpec.Charge"/>; Fire for both shots since M31): in the last <see cref="BotChargeWindow"/>
    /// seconds of every <see cref="BotChargeCycle"/> it holds Fire until the last stage with a shot is reached
    /// (<see cref="Combat.ChargeStage"/>), then releases it (it aims at the target every tick, so the release is aimed); otherwise it
    /// taps (presses on a released button, releases on the next tick: the basic shot). It does nothing while a released shot is
    /// pending or a raise is under way (the tap's shot waits for the weapon to come up).
    /// </para>
    /// <para>
    /// Remote detonation (grenade launcher): with grenades out it presses Reload (on a released key) when a grenade of
    /// this weapon is drawn within half its blast radius of the target, or once grenades have been out for
    /// <see cref="BotDetonateAfter"/> s. Snapshots do not say whose a grenade is, so any grenade of this weapon counts.
    /// </para>
    /// </summary>
    public InputButtons BotWeaponButtons(PlayerState me, uint tick, PlayerState? target)
    {
        var c = _config!;
        // bot-grenade-safety (Classic PvP): a splash weapon held at close range gives way to a loaded pistol, back when the range allows (ClassicBotBrain.BotGrenades.cs); else the old rule.
        var splashSwitch = BotSplashSwitch(me, target);
        if (splashSwitch == true || (splashSwitch is null && BotWantsSwitch(me))) return me.SwitchHeld ? InputButtons.None : InputButtons.Switch;   // press edge; released on the next tick
        if (Loadouts.InHand(c, me) is not { } weapon) return InputButtons.None;
        var buttons = InputButtons.None;

        // Not during a switch or a melee attack: the simulation ignores the press then.
        if (Shots.RemoteDetonated(weapon) && me.ProjectilesOut > 0 && !me.ReloadHeld && me.SwitchLeft <= 0f && me.MeleeStep == 0
            && BotWantsDetonation(me, weapon, target) is { } why)
        {
            buttons |= InputButtons.Reload;
            _detonateRequests++;
            GD.Print($"t {_connectedFor:F1} s: id {_client!.PlayerId} detonates {me.ProjectilesOut} grenade(s) ({why}; requests {_detonateRequests})");
        }

        if (weapon.Charge is { Stages.Count: > 0, FiresOnPress: false } charge)   // sim/instant-fire: the lascannon press-fires like any basic weapon
        {
            int lastFiring = 0;
            for (int i = 0; i < charge.Stages.Count; i++) if (charge.Stages[i].Shot is not null) lastFiring = i + 1;
            if (me.PendingShot > 0 || (me.RaiseTicks > 0 && !me.Charging)) return buttons;   // the released shot is about to leave, or waits for the raise (a charge held through the raise goes on)
            if (me.Charging)
            {
                // A tap releases at once (the basic shot); a hold lasts until the firing stage (aimed: the bot turns to the target every tick).
                if (_botTap) return buttons;
                bool ready = lastFiring > 0 && Combat.ChargeStage(charge, me.ChargeTime, firing: true) >= lastFiring;
                // bot-grenade-safety (Classic PvP): a charged shot that would burst within its own blast of the bot is held on (an overheat ends the charge unfired).
                if (ready && !BotSplashShotHeld(me, weapon, lastFiring, target)) return buttons;
                return buttons | InputButtons.Fire;
            }
            if (me.FireHeld || me.Overheated) return buttons;   // release first: a charge starts on a press edge
            bool chargeNow = BotChargeAll || (tick * (double)c.TickSeconds + Math.Max(_client?.PlayerId ?? 0, 0) * 1.3) % BotChargeCycle >= BotChargeCycle - BotChargeWindow;
            // bot-grenade-safety (Classic PvP; ClassicBotBrain.BotGrenades.cs): a charged shot that would burst within its own blast of the bot is not charged (the tap's basic shot instead),
            // and a basic shot that would is not fired.
            if (chargeNow && lastFiring > 0 && BotSplashShotHeld(me, weapon, lastFiring, target, note: false)) chargeNow = false;
            if (BotSplashShotHeld(me, weapon, 0, target)) return buttons;
            // Too little in the clip to start a charge (the lascannon needs 2) but some in reserve: reload first.
            if (chargeNow && me.Clip > 0 && me.Clip < Math.Max(1, charge.AmmoToStart) && me.Reserve > 0 && me.ReloadLeft <= 0f)
                return buttons | (me.ReloadHeld ? InputButtons.None : InputButtons.Reload);
            _botTap = !chargeNow;
            return buttons | InputButtons.Fire;
        }
        if (BotSplashShotHeld(me, weapon, 0, target)) return buttons;   // bot-grenade-safety (Classic PvP): not a projectile that would burst within its own blast of the bot (the grenade launcher)
        return buttons | (weapon.Trigger == TriggerKind.FullAuto || !me.FireHeld ? InputButtons.Fire : InputButtons.None);
    }

    /// <summary>
    /// The bot's throw: a Grenade press (on a released key) when it carries one, the item's cooldown is over, nothing else
    /// is under way (throw, melee, switch, charge or pending shot), its last throw press was <see cref="BotThrowEvery"/> s ago or more and the distance rule allows it:
    /// bot-grenade-safety (Classic PvP, a damaging item, <see cref="BotGrenadeSafety"/>): the target's chest and the predicted landing point at least the item's blast radius
    /// + margin from the bot's chest (3D) and the target within <see cref="_botThrowMax"/> m horizontally; else (stims, the blind grenade, <c>--old-bot-throws</c>)
    /// <see cref="BotThrowMin"/> to <see cref="_botThrowMax"/> m away horizontally (stims at any distance).
    /// </summary>
    InputButtons BotThrow(PlayerState me, PlayerState target, float distance)
    {
        if (me.GrenadeHeld || me.Grenades <= 0 || me.GrenadeCooldown > 0f || me.Throwing || me.MeleeStep > 0 || me.SwitchLeft > 0f
            || me.Charging || me.PendingShot > 0 || _connectedFor - _botLastThrow < BotThrowEvery)
            return InputButtons.None;
        bool stim = StimOf(me) is not null;
        if (!stim && BotThrowSafety(me) is { } safe)
        {
            var verdict = BotThrowCheck(me, target, safe.Item, safe.Safety, out var landing);
            if (verdict != BotGrenadeSafety.ThrowVerdict.Throw)
            {
                // A refusal is counted when the old rule would have thrown (at most once per BotThrowEvery), so the count is the throws avoided.
                if (verdict != BotGrenadeSafety.ThrowVerdict.OutOfRange && BotGrenadeSafety.OldThrowAllowed(distance, false, _botThrowMax) && _connectedFor - _botLastRefusal >= BotThrowEvery)
                {
                    _throwsRefused++; _botLastRefusal = _connectedFor;
                    BotSafetyLog($"no throw at P{target.PlayerId} ({verdict}: target {V3Distance(BotChest(me), BotChest(target)):F1} m, landing {V3Distance(BotChest(me), landing):F1} m, safety {safe.Safety:F1} m)");
                }
                return InputButtons.None;
            }
            _botLastThrow = _connectedFor;
            _botThrowAim = landing; _botThrowPressAim = landing;
            GD.Print($"t {_connectedFor:F1} s: id {_client!.PlayerId} bot presses throw at a target {distance:F1} m away ({ThrowText(me)} 3D {V3Distance(BotChest(me), BotChest(target)):F1} m, " +
                     $"landing {V3Distance(BotChest(me), landing):F1} m from the chest; safety {safe.Safety:F1} m)");
            return InputButtons.Grenade;
        }
        if (!BotGrenadeSafety.OldThrowAllowed(distance, stim, _botThrowMax)) return InputButtons.None;   // perks/inert-2: stims at any distance
        _botLastThrow = _connectedFor;
        GD.Print($"t {_connectedFor:F1} s: id {_client!.PlayerId} bot presses throw at a target {distance:F1} m away ({ThrowText(me)})");
        return InputButtons.Grenade;
    }

    double _botLastRefusal = double.NegativeInfinity;
    static float V3Distance(System.Numerics.Vector3 a, System.Numerics.Vector3 b) => System.Numerics.Vector3.Distance(a, b);

    /// <summary>Why the bot's grenades should go off now (see <see cref="BotWeaponButtons"/>), or null.</summary>
    string? BotWantsDetonation(PlayerState me, WeaponSpec weapon, PlayerState? target)
    {
        // bot-grenade-safety: never with the bot's chest inside a waiting round's blast reach (ClassicBotBrain.BotGrenades.cs).
        if (BotDetonationHeld(me, weapon) is { } near)
        {
            if (_connectedFor - _botLastDetonationHold >= 1.0) { _detonationsHeld++; _botLastDetonationHold = _connectedFor; BotSafetyLog($"no detonation: a waiting round {near:F1} m from the chest"); }
            return null;
        }
        if (target is { } t && _client is not null && GrenadeNear(me, weapon, t) is { } g)
            return $"grenade #{g.Id} at {g.Position.X:F1},{g.Position.Y:F1},{g.Position.Z:F1}{((g.Flags & ProjectileFlags.Stuck) != 0 ? " stuck" : (g.Flags & ProjectileFlags.Attached) != 0 ? " attached" : "")} near P{t.PlayerId} at {t.Position.X:F1},{t.Position.Y:F1},{t.Position.Z:F1}";
        return _grenadesOutFor >= BotDetonateAfter ? $"out for {_grenadesOutFor:F1} s" : null;
    }

    /// <summary>A grenade of the weapon in hand drawn within half its blast radius of the target's chest, or null.</summary>
    ProjectileView? GrenadeNear(PlayerState me, WeaponSpec weapon, PlayerState t)
    {
        int index = Loadouts.WeaponIndex(_config!, me, me.Slot);
        float radius = Shots.ProjectileOf(weapon, 0)?.Splash?.Radius ?? 2f;
        var centre = t.Position + new System.Numerics.Vector3(0, 0, 1f);
        foreach (var q in _client!.Projectiles())
            if (!q.Thrown && q.Weapon == index && q.Stage == 0 && System.Numerics.Vector3.DistanceSquared(q.Position, centre) <= radius * radius * 0.25f) return q;
        return null;
    }

    /// <summary>
    /// Whether the bot should switch weapons now: no attack or switch under way, the player has a weapon in the other slot,
    /// and either the primary is empty (reloading or not) while the pistol is loaded, or the pistol is empty while the
    /// primary has ammunition (loaded or in reserve; it reloads once in hand). The holstered weapon does not reload, so
    /// after one round trip the bot stays on the primary and reloads it.
    /// Heat: with the primary overheated (locked) and a loaded pistol, it switches to the pistol, and back once the
    /// holstered primary is released (heat at or below its recovery value), staying on the pistol (which reloads itself)
    /// until then. Without a loaded pistol it keeps the trigger down on the locked primary (the simulation refuses the
    /// shots) until it is released.
    /// </summary>
    bool BotWantsSwitch(PlayerState me)
    {
        if (BarrelTest) return false;   // fx/barrel-origin: the weapon asked for stays in hand
        if (me.MeleeStep > 0 || me.SwitchLeft > 0f || me.Charging || me.PendingShot > 0) return false;   // a switch would cancel the charge
        if (Loadouts.Holstered(_config!, me) is null) { _botCoolingPrimary = false; return false; }
        if (me.Slot == WeaponSlot.Ranged)
        {
            // Fidelity gap 7 (--bot-brace): a vent weapon vents its overheat (2 to 4 s) instead of being switched away.
            if (me.Overheated && me.OtherClip > 0 && !(BotBraceVent && Loadouts.InHand(_config!, me)?.Vent is not null)) { _botCoolingPrimary = true; return true; }
            _botCoolingPrimary = false;
        }
        else if (_botCoolingPrimary)
        {
            if (!me.OtherOverheated) { _botCoolingPrimary = false; return true; }
            return false;   // the primary is still locked: stay on the pistol (it reloads itself when empty)
        }
        if (me.Clip > 0) return false;
        // A self-recharging weapon (the melta) in hand with reserve left waits for its recharge (a switch would lose it).
        if (Loadouts.InHand(_config!, me)?.Recharge is not null && me.Reserve > 0) return false;
        return me.Slot == WeaponSlot.Ranged ? me.OtherClip > 0 : me.OtherClip + me.OtherReserve > 0;
    }
}
