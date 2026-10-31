using System.Numerics;

namespace SmSim;

public enum HitZone : byte { None = 0, Body = 1, Head = 2 }

/// <summary>
/// What one player's tick produced for the world to resolve: a shot that left the barrel (<see cref="ShotStage"/> names
/// it as <see cref="Shots"/> does: 0 basic, k the charge stage k − 1), the melee combo step whose hit lands (0 none), and
/// a remote detonation request (the reload input with grenades out).
/// </summary>
/// <paramref name="Threw"/>: the player's equipment item left the hand this tick (the world launches its projectile).
/// <paramref name="Jump"/>: what the player's jump pack did this tick (lift-off, dive, landing, impact).
/// <paramref name="MeleeHeavy"/>: <see cref="MeleeHitStep"/> indexes the heavy strikes (M28).
/// <paramref name="SlashHit"/>: protocol 30, the jump pack's sword slash hits this tick (<see cref="JumpPack.SlashHits"/>; the world resolves the cone).
public readonly record struct SelfEvents(bool Fired, int ShotStage, int MeleeHitStep, bool Detonate, bool Threw = false, JumpEventKind Jump = JumpEventKind.None, bool MeleeHeavy = false,
    bool SlashHit = false);

/// <summary>
/// Weapon (bolter), health and armour shield rules. Values come from <see cref="SimConfig"/>, read from the install
/// (wargear <c>sm_mp_ranged_bolter</c>, player blueprint <c>health_ext</c>). How they combine is partly **inferred**
/// (spec/behavior.md): shots wear the regenerating armour shield down first at its own multipliers, the remainder goes
/// to health at the health multipliers; damage falls off linearly between the file's fall-off points.
/// </summary>
public static class Combat
{
    /// <summary>
    /// A fresh life for the player's class and loadout (<see cref="PlayerState.Class"/> and the choices, kept; a choice
    /// not valid for the class becomes the class default): full health, shield and ammunition for both weapons, no heat,
    /// the primary in hand (the pistol when there is no primary).
    /// </summary>
    public static PlayerState Respawned(PlayerState s, SimConfig c)
    {
        s = Loadouts.Resolved(c, s);
        var primary = Loadouts.Weapon(c, s, WeaponSlot.Ranged);
        var pistol = Loadouts.Weapon(c, s, WeaponSlot.Pistol);
        var slot = primary is null && pistol is not null ? WeaponSlot.Pistol : WeaponSlot.Ranged;
        var inHand = slot == WeaponSlot.Ranged ? primary : pistol;
        var other = Loadouts.Weapon(c, s, Loadouts.Other(slot));
        var third = Loadouts.Weapon(c, s, Loadouts.Third(slot));   // protocol 44: the second primary (Weapon Versatility) or the primary
        var body = Loadouts.Body(c, s);   // M43: the perks' health and shield (Iron Halo)
        return s with
        {
            Alive = true, Health = body.HealthMax, Shield = body.ShieldMax, BurstLeft = 0,
            Slot = slot, Clip = inHand?.ClipSize ?? 0, Reserve = inHand?.ReserveSize ?? 0,
            OtherClip = other?.ClipSize ?? 0, OtherReserve = other?.ReserveSize ?? 0, SwitchLeft = 0,
            ThirdClip = third?.ClipSize ?? 0, ThirdReserve = third?.ReserveSize ?? 0, ThirdHeat = 0, ThirdOverheated = false, ThirdSinceHeat = 0,
            Cooldown = 0, ReloadLeft = 0, OtherReloadLeft = 0, ThirdReloadLeft = 0, SinceDamage = 0, RespawnLeft = 0, Velocity = Vector3.Zero, Grounded = true,
            MeleeStep = 0, MeleeTime = 0, MeleeQueued = false,
            Heat = 0, Overheated = false, SinceHeat = 0, OtherHeat = 0, OtherOverheated = false, OtherSinceHeat = 0,
            Charging = false, ChargeTime = 0, PendingShot = 0, PendingLeft = 0,
            Grenades = Loadouts.Equipment(c, s)?.SpawnCount ?? 0, GrenadeCooldown = 0, Throwing = false, ThrowTime = 0, StimLeft = 0,
            Jump = JumpPhase.None, JumpTicks = 0, JumpMark = 0, JumpHolding = false, JumpLandMoving = false, JumpCut = false, JumpSlash = false, Aiming = false,
            JumpPower = Loadouts.JumpPack(c, s)?.MaxPower ?? 0f,
            EvadeTicks = 0, EvadeDirection = EvadeDirection.Forward, EvadeLock = 0, EvadeLanded = false, JumpDashDirection = EvadeDirection.Forward,
            MeleeHeavy = false, MeleeQueuedHeavy = false,
            Reaction = ReactionKind.None, ReactionDirection = ReactionDirection.Front, ReactionTicks = 0, PushYaw = 0, PushDistance = 0, PushTicks = 0, BlindTicks = 0,
            Rush = RushPhase.None, RushTicks = 0, RushLock = 0, ReadyTicks = 0, RaiseTicks = 0,
            Brace = BracePhase.None, BraceTicks = 0, Venting = false,
            SpreadBloom = 0f, SpreadSince = 0f, SpreadYaw = 0f, SpreadPitch = 0f,   // weapons/spread: a respawn (a round restart too) rests the weapon
            Buffered = BufferedAction.None, BufferTicks = 0, BufferedCharge = false,   // feel/buffer-speed
            SprintStop = false,   // feel/locks-sprint-stop
        };
    }

    /// <summary>The attack step the player is in (light or heavy list by <see cref="PlayerState.MeleeHeavy"/>), or null.</summary>
    public static MeleeStep? MeleeStepOf(SimConfig c, PlayerState s)
    {
        var list = s.MeleeHeavy ? Loadouts.MeleeHeavy(c, s) : Loadouts.Melee(c, s);
        return s.MeleeStep >= 1 && s.MeleeStep <= list.Count ? list[s.MeleeStep - 1] : null;
    }

    /// <summary>Tolerance, in seconds, for comparing an accumulated timer with a delay (sums of 1/TickRate are inexact).</summary>
    const float TimeEpsilon = 1e-5f;

    /// <summary>
    /// One tick of a weapon's heat (in hand or holstered) before any shot of this tick. Rules (provisional; spec/behavior.md):
    /// the tick covers the time (before, after] since the last heat gain, before = <paramref name="since"/>, after =
    /// before + dt. The delay is <see cref="HeatSpec.CoolDelay"/>, or <see cref="HeatSpec.CoolDelayAfterOverheat"/> while
    /// overheated. Heat falls by CoolRate × the part of the tick after the delay: max(0, min(dt, after − delay)), with
    /// differences within 1e-5 s snapped to 0 or dt (so a delay of a whole number of ticks gives whole-tick steps). Heat is
    /// clamped to [0, Overheat]; once it is at or below <see cref="HeatSpec.Recovery"/>, the weapon is no longer
    /// overheated. Without a spec, heat is 0 and nothing is tracked.
    /// </summary>
    public static (float Heat, bool Overheated, float Since) Cool(HeatSpec? spec, float heat, bool overheated, float since, float dt)
    {
        if (spec is null) return (0f, false, 0f);
        float delay = overheated ? spec.CoolDelayAfterOverheat : spec.CoolDelay;
        float after = since + dt;
        float cooling = after - delay;
        if (cooling <= TimeEpsilon) cooling = 0f;
        else if (cooling >= dt - TimeEpsilon) cooling = dt;
        heat = Math.Clamp(heat - spec.CoolRate * cooling, 0f, spec.Overheat);
        if (overheated && heat <= spec.Recovery) overheated = false;
        return (heat, overheated, after);
    }

    /// <summary>A shot's heat: PerShot more (clamped to Overheat), the delay restarts, and reaching Overheat overheats the weapon.</summary>
    public static (float Heat, bool Overheated, float Since) AddShotHeat(HeatSpec? spec, float heat, bool overheated)
    {
        if (spec is null) return (0f, false, 0f);
        heat = Math.Min(spec.Overheat, heat + spec.PerShot);
        return (heat, overheated || heat >= spec.Overheat, 0f);
    }

    /// <summary>Death: no health or shield, the respawn timer starts, and any reload, switch, melee attack, charge, pending charged shot, raise, evade, reaction or blindness stops.</summary>
    public static PlayerState Killed(PlayerState s, SimConfig c) => s with
    {
        ReadyTicks = 0, RaiseTicks = 0, BurstLeft = 0,
        MeleeHeavy = false, MeleeQueuedHeavy = false,
        Reaction = ReactionKind.None, ReactionDirection = ReactionDirection.Front, ReactionTicks = 0, PushYaw = 0, PushDistance = 0, PushTicks = 0, BlindTicks = 0,
        Rush = RushPhase.None, RushTicks = 0, RushLock = 0,
        Alive = false, Health = 0f, Shield = 0f, RespawnLeft = Loadouts.RespawnTime(c, s), Velocity = Vector3.Zero, ReloadLeft = 0f, OtherReloadLeft = 0f, ThirdReloadLeft = 0f, SwitchLeft = 0f,
        MeleeStep = 0, MeleeTime = 0f, MeleeQueued = false,
        Charging = false, ChargeTime = 0f, PendingShot = 0, PendingLeft = 0f, Throwing = false, ThrowTime = 0f, StimLeft = 0f,
        Jump = JumpPhase.None, JumpTicks = 0, JumpMark = 0, JumpHolding = false, JumpLandMoving = false, JumpCut = false, JumpSlash = false, Aiming = false,
        EvadeTicks = 0, EvadeDirection = EvadeDirection.Forward, EvadeLock = 0, EvadeLanded = false, JumpDashDirection = EvadeDirection.Forward,
        Brace = BracePhase.None, BraceTicks = 0, Venting = false,
        Buffered = BufferedAction.None, BufferTicks = 0, BufferedCharge = false, SprintStop = false,
    };

    /// <summary>
    /// feel/locks-sprint-stop: when movement input applies again during attack <paramref name="m"/> (seconds from its start): its
    /// <see cref="MeleeStep.MoveAt"/> × <see cref="SimConfig.ActionLockScale"/> (<see cref="SimConfig.LockTime"/>).
    /// </summary>
    public static float MoveAtOf(MeleeStep m, SimConfig c) => c.LockTime(m.MoveAt);

    /// <summary>
    /// feel/locks-sprint-stop: whether a Jump press this tick ends attack <paramref name="m"/>, given the attack time <paramref name="played"/> before the tick:
    /// from <see cref="MeleeStep.EvadeAt"/> × the lock scale, but the scale never opens the branch before the attack's hit has landed when the data opens it after
    /// the hit (<see cref="MeleeGateAt"/>). Never without a branch.
    /// </summary>
    public static bool MeleeEvadeOpen(MeleeStep m, float played, SimConfig c)
    {
        if (!float.IsFinite(m.EvadeAt) || played + TimeEpsilon < c.LockTime(m.EvadeAt)) return false;
        return m.EvadeAt < m.HitTime ? played + TimeEpsilon >= m.EvadeAt : played >= m.HitTime;
    }

    /// <summary>
    /// feel/locks-sprint-stop: the attack time from which <see cref="MeleeEvadeOpen"/> lets a Jump press end attack <paramref name="m"/>: <see cref="MeleeStep.EvadeAt"/>
    /// × the lock scale, at least the hit (<see cref="MeleeStep.HitTime"/>) when the data's branch opens after it, else the data's branch; infinity without one.
    /// </summary>
    public static float MeleeGateAt(MeleeStep m, SimConfig c) =>
        !float.IsFinite(m.EvadeAt) ? float.PositiveInfinity : MathF.Max(c.LockTime(m.EvadeAt), MathF.Min(m.EvadeAt, m.HitTime));

    /// <summary>
    /// feel/locks-sprint-stop: whether the player's throw still keeps an evade from starting: throwing, with less played than
    /// <see cref="SimConfig.ThrowDuration"/> × the lock scale (never before the release: the item always leaves). An evade that starts ends the throw.
    /// </summary>
    public static bool ThrowHolds(PlayerState s, SimConfig c) =>
        s.Throwing && (Loadouts.Equipment(c, s) is var item) && s.ThrowTime + TimeEpsilon
            < MathF.Max(c.LockTime(item?.DurationOf(c) ?? c.ThrowDuration), item?.ReleaseAt(c) ?? c.ThrowRelease);   // perks/inert-2: a stim's use has its own times

    /// <summary>
    /// The charge stage reached after <paramref name="chargeTime"/> seconds (1-based; 0: none): the last stage whose
    /// MinTime × <see cref="ChargeSpec.MinTimeMultiplier"/> is at most the time (within 1e-5 s). With
    /// <paramref name="firing"/>, only stages with a shot count (the shot a release fires).
    /// </summary>
    public static int ChargeStage(ChargeSpec? charge, float chargeTime, bool firing)
    {
        if (charge is null) return 0;
        int reached = 0;
        for (int i = 0; i < charge.Stages.Count; i++)
        {
            var st = charge.Stages[i];
            if (st.MinTime * charge.MinTimeMultiplier <= chargeTime + TimeEpsilon && (!firing || st.Shot is not null)) reached = i + 1;
        }
        return reached;
    }

    /// <summary>
    /// A blast's intensity at <paramref name="distance"/> from its centre: linear between the fall-off points (in file
    /// order, distances ascending), the first point's value before it, 0 beyond the last point; 1 without points
    /// (**interpretation**: the files do not say whether intensity steps or interpolates; spec/behavior.md).
    /// </summary>
    public static float SplashIntensity(IReadOnlyList<DistancePoint> points, float distance)
    {
        if (points.Count == 0) return 1f;
        if (distance <= points[0].Distance) return points[0].Value;
        for (int i = 1; i < points.Count; i++)
        {
            var (a, b) = (points[i - 1], points[i]);
            if (distance > b.Distance) continue;
            float span = b.Distance - a.Distance;
            return span <= 1e-6f ? b.Value : a.Value + (b.Value - a.Value) * ((distance - a.Distance) / span);
        }
        return 0f;
    }

    /// <summary>
    /// A piecewise-linear table (points ascending by key) at <paramref name="key"/>: linear between points, the first value
    /// before the first point and the last value after the last; 0 without points. Used for the rigid-body wall reductions
    /// (by angle) and the cone's coverage multiplier (by coverage).
    /// </summary>
    public static float PointValue(IReadOnlyList<DistancePoint> points, float key)
    {
        if (points.Count == 0) return 0f;
        if (key <= points[0].Distance) return points[0].Value;
        for (int i = 1; i < points.Count; i++)
        {
            var (a, b) = (points[i - 1], points[i]);
            if (key > b.Distance) continue;
            float span = b.Distance - a.Distance;
            return span <= 1e-6f ? b.Value : a.Value + (b.Value - a.Value) * ((key - a.Distance) / span);
        }
        return points[^1].Value;
    }

    /// <summary>
    /// The factor of gravity on a projectile that has travelled <paramref name="travelled"/>: the value of the last point
    /// at or before that distance (a step lookup; the first point's value before it); 1 without points (ours).
    /// </summary>
    public static float GravityFactor(IReadOnlyList<DistancePoint> points, float travelled)
    {
        if (points.Count == 0) return 1f;
        float factor = points[0].Value;
        for (int i = 1; i < points.Count && points[i].Distance <= travelled; i++) factor = points[i].Value;
        return factor;
    }

    /// <summary>
    /// Everything about one player's tick that does not involve others: movement, timers, regeneration, reloading,
    /// and spending ammunition on a shot. Pure, so client prediction reproduces it exactly. <paramref name="fired"/>
    /// says a shot left the barrel this tick; the world resolves what it hit. <paramref name="world"/> is the level's
    /// collision (null: flat ground at z = 0).
    /// </summary>
    public static PlayerState StepSelf(PlayerState s, PlayerInput input, SimConfig c, out bool fired, CollisionWorld? world = null)
        => StepSelf(s, input, c, out fired, out _, world);

    /// <summary>As <see cref="StepPlayer"/>, reporting only whether a shot left and the melee hit step.</summary>
    public static PlayerState StepSelf(PlayerState s, PlayerInput input, SimConfig c, out bool fired, out int meleeHitStep, CollisionWorld? world = null,
        IReadOnlyDictionary<int, PlayerState>? others = null)
    {
        var next = StepPlayer(s, input, c, out var events, world, others);
        fired = events.Fired;
        meleeHitStep = events.MeleeHitStep;
        return next;
    }

    /// <summary>
    /// One player's tick with everything the world resolves (<see cref="SelfEvents"/>): the shot (above), melee, charged fire and remote
    /// detonation. <see cref="SelfEvents.MeleeHitStep"/> is the combo step (1..N) whose hit lands this tick, or 0;
    /// the world resolves whom it hits. Melee rules (timings original per step, their combination **provisional**;
    /// spec/behavior.md): a melee press edge starts step 1 (cancelling a reload); during an attack a press before
    /// ComboClose is buffered and moves on to the next step (after the last: step 1) once the time reaches ComboOpen;
    /// the attack ends at Duration. Fire is ignored until RangedAt, after which it ends the attack and fires that tick;
    /// reload input is ignored during an attack. Movement input counts as zero until MoveAt; protocol 32 (feel/melee-lock): an attack with
    /// <see cref="MeleeStep.Reach"/> moves the body by its root motion along its heading (until MoveAt, and after it while the stick is idle),
    /// the heading turning to the locked target in its alignment window (<see cref="MeleeLock"/>; <paramref name="others"/> is the attacker's
    /// view of the other players at its input's view tick: the server's rewound world, the client's interpolated one; null: no lock).
    /// Each tick covers the attack time (before, after]: the hit lands in the tick whose interval contains HitTime.
    /// The combo is the player's class's (<see cref="ClassLoadout.Melee"/>; empty: a melee press does nothing).
    /// <para>
    /// Weapons (rules provisional; spec/behavior.md): the weapon in hand (<see cref="PlayerState.Slot"/>) fires, reloads
    /// and is ranged with its own <see cref="WeaponSpec"/> values. A full-automatic weapon fires while Fire is held, a
    /// semi-automatic one only on a press edge. A Switch press edge, when the class has both weapons, no melee attack is
    /// in progress and no switch is under way, puts the other weapon in hand at once (the ammunition states swap), drops
    /// any reload in progress, and blocks firing and reloading for <see cref="SimConfig.WeaponSwitchTime"/>; the first
    /// shot can leave in the tick that time is reached. Presses during an attack or a switch are ignored (not buffered).
    /// The weapons are the player's chosen ones (<see cref="Loadouts.InHand"/>), the combo its chosen one.
    /// </para>
    /// <para>
    /// Slot selection (M22, protocol 14; bindings from the user's knowledge of the original): a press edge of
    /// <see cref="InputButtons.SlotPistol"/> (key 1) or <see cref="InputButtons.SlotPrimary"/> (key 2) that names the slot not
    /// in hand, when that slot holds a weapon, is a switch request exactly like a Switch press edge (<see cref="SwitchRequested"/>):
    /// same blocking, same switch time, same evade branch. Naming the slot in hand, or an empty slot, does nothing (not even
    /// ending an evade). Within one tick a slot press overrides a Switch press, and a SlotPistol press overrides a SlotPrimary press.
    /// </para>
    /// <para>
    /// Heat (rules provisional; spec/behavior.md): each living tick, first both weapons cool (<see cref="Cool"/>), the
    /// holstered one included; a switch then swaps the heat states like the ammunition; an overheated weapon in hand
    /// neither fires nor starts a reload; a shot adds heat (<see cref="AddShotHeat"/>), so the shot that reaches Overheat
    /// still leaves. Dead players' heat is frozen; a respawn zeroes it.
    /// </para>
    /// <para>
    /// Charged fire (rules **interpretation**; spec/behavior.md): Fire drives it whichever ability holds the <see cref="ChargeSpec"/>
    /// (M31, owner's decision 2026-10-28: tap the trigger for the normal shot, hold it for the charged shot). A press edge of Fire
    /// starts a charge when the weapon could fire (no reload, overheat, switch, attack, pending shot; cooldown over) and the clip
    /// holds <see cref="ChargeSpec.AmmoToStart"/> (at least 1); a press that cannot start one (the lascannon with 1 round) fires
    /// the basic shot at once. Every tick the button is down counts one tick of
    /// <see cref="PlayerState.ChargeTime"/> (the press tick included) and adds the reached stage's HeatPerSecond × dt
    /// (restarting the cool-down delay); heat reaching Overheat overheats the weapon and cancels the charge. While charging,
    /// movement is scaled by the reached stage's MoveSpeedMultiplier. Releasing fires the last reached stage that has a shot
    /// (<see cref="ChargeStage"/>); none reached (a tap): the basic shot (the other ability). A shot with a FireDelay leaves that long after the
    /// release (<see cref="PlayerState.PendingShot"/>; the weapon does nothing else meanwhile). A charged shot uses
    /// AmmoCost (at most the clip), adds HeatPerShot and sets its cooldown. Reload input cancels a charge when
    /// <see cref="ChargeSpec.ReloadCancels"/> (and may start a reload in the same tick), else it is ignored while charging. A
    /// switch or a melee attack cancels a charge or a pending shot; so does death. No hold limit (MaxChargeTime unused).
    /// </para>
    /// <para>
    /// Remote detonation: with a <see cref="Shots.RemoteDetonated"/> weapon in hand (the grenade launcher), a Reload press
    /// edge while <see cref="PlayerState.ProjectilesOut"/> &gt; 0 asks the world to detonate them; that press is no reload
    /// request (nor a charge cancel) until the button is released (<see cref="PlayerState.DetonateHeld"/>);
    /// each basic shot of such a weapon counts one more, up to the projectile's MaxActive.
    /// </para>
    /// <para>
    /// Throwing (timings original, rules **interpretation**; spec/behavior.md): a <see cref="InputButtons.Grenade"/> press
    /// edge starts a throw of the player's equipment item when it carries one (<see cref="PlayerState.Grenades"/> &gt; 0), the
    /// item's cooldown is over, no melee attack (one starting this tick included), switch or pending charged shot is under way
    /// and no throw is in progress. Starting cancels a reload and a charge. The press tick is throw time 0; each later tick adds
    /// dt; the item leaves (one fewer carried, its cooldown starts, <see cref="SelfEvents.Threw"/>) in the tick whose interval
    /// contains <see cref="SimConfig.ThrowRelease"/>, and the throw ends at <see cref="SimConfig.ThrowDuration"/>. While
    /// throwing, the weapon neither fires, charges nor reloads, and melee and switch presses are ignored (not buffered).
    /// Death cancels a throw before its release (nothing is spent). Respawn refills to the item's spawn count.
    /// </para>
    /// <para>
    /// Recharging clips (<see cref="WeaponSpec.Recharge"/>, the melta; **interpretation**): no reload input; whenever the
    /// weapon is idle with its clip below capacity and reserve left (and it is not firing this tick), a recharge of
    /// <see cref="RechargeSpec.RoundTime"/> seconds starts and moves one round; it is lost like a reload (switch, melee, throw).
    /// </para>
    /// <para>
    /// Evade (M21; <see cref="Evade"/>): on a class with an <see cref="EvadeSpec"/> (no jump pack), a Jump press may start an
    /// evade roll. While it lasts, the weapon only finishes a reload or recharge in progress (no firing, charging, detonation
    /// or new reload), melee, throw and switch presses are ignored, aim mode is off, and the roll's motion replaces walking.
    /// Starting it cancels a charge and a pending charged shot.
    /// </para>
    /// <para>
    /// Raising the weapon (M31; owner's decision 2026-10-28: the first shot waits for the gun to come up; delays from the data,
    /// the rest **interpretation**, spec/behavior.md "Hip fire"): the weapon is up while <see cref="PlayerState.ReadyTicks"/> &gt; 0, in
    /// aim mode, and always for a weapon that does not wait (<see cref="WeaponSpec.WaitsToRaise"/>). A trigger press that would fire
    /// (or start a charge) with the weapon down starts a raise of <see cref="WeaponSpec.RaiseDelay"/> (in ticks, rounded up): the basic
    /// shot leaves in the tick the raise ends (presses meanwhile are lost; a charge runs on and a release during the raise fires the
    /// tap's basic shot when it ends). While a raise runs the weapon neither reloads nor fires otherwise and the Sprint bit is not applied.
    /// A shot, a charge, a raise or aim mode keep the weapon up for <see cref="SimConfig.ReadyHold"/>; an always-ready weapon is kept up every
    /// tick. Lowered at once (ready and raise cleared): a sprint (the Sprint bit applied with the stick forward on the ground), a weapon
    /// switch, a melee attack, a throw, an evade, a jump-pack jump's start, a ground pound, a reaction and the charge (bull rush).
    /// </para>
    /// <para>
    /// Brace and vent (fidelity gap 7; <see cref="Brace"/>, <see cref="Vent"/>): with a brace weapon in hand (the heavy bolter) Reload changes
    /// stance; the stance masks the input first (<see cref="Brace.Mask"/>) and braced fire shoots <see cref="Brace.Braced"/>. With a vent weapon (the
    /// plasma weapons) Reload, or an overheat, vents its heat on top of the cooling while the player stands or walks (<see cref="Vent.Runs"/>, <see cref="Vent.Free"/>); an overheat's speed modifier scales the movement.
    /// </para>
    /// <para>
    /// Protocol 30: a melee press in a jump-pack dash's window starts the dash attack (<see cref="Rush.CanDashAttack"/>; a charge phase like the bull rush);
    /// a charge leaving the ground is held in the air (<see cref="Rush.Held"/>); a melee press in a jump below 8 m starts the sword slash's dive, whose hit
    /// this tick is <see cref="SelfEvents.SlashHit"/> (<see cref="JumpPack.SlashHits"/>); walking off an edge drops straight down (<see cref="Movement.Step"/>).
    /// </para>
    /// </summary>
    public static PlayerState StepPlayer(PlayerState s, PlayerInput input, SimConfig c, out SelfEvents events, CollisionWorld? world = null,
        IReadOnlyDictionary<int, PlayerState>? others = null)
    {
        // feel/buffer-speed: a buffered press is replayed into the tick (InputBuffer.Begin) and the buffer updated from what started (InputBuffer.End).
        // A replayed melee press goes through the melee lock like a fresh one (the input's MeleeTarget is the tick's own).
        var buffer = InputBuffer.Begin(s, input, c);
        // feel/jump-pack-2: a long buffered Jump press (held past the dash's decision) takes off committed to the small jump; whether the real button is down decides if it holds on.
        var longJump = buffer.Replayed == BufferedAction.JumpLong ? ((input.Buttons & InputButtons.Jump) != 0 ? LongJump.Held : LongJump.Released) : LongJump.None;
        var next = StepPlayerCore(buffer.State, buffer.Input, c, out events, world, others, out int used, longJump);
        next = InputBuffer.End(s, input, next, buffer, used, c);
        return Spread.After(s, next, input, c, events);   // weapons/spread: the bloom and this tick's shot's scatter (Spread.cs)
    }

    /// <param name="used">feel/buffer-speed: the actions whose press started something this tick (bits of <see cref="InputBuffer.Bit"/>; see <see cref="InputBuffer.Locked"/>).</param>
    static PlayerState StepPlayerCore(PlayerState s, PlayerInput input, SimConfig c, out SelfEvents events, CollisionWorld? world,
        IReadOnlyDictionary<int, PlayerState>? others, out int used, LongJump longJump = LongJump.None)
    {
        used = 0;
        var start = s;   // feel/locks-sprint-stop: the state after the last tick (whether a sprint stop may run: SprintStop.Continues)
        c = c.For(s.Class);   // Ork O4: the class's own body (speeds, health, shield, hit volumes); the config itself for every other class
        bool fired = false, detonate = false, hitHeavy = false;
        int shotStage = 0, meleeHitStep = 0;
        float dt = c.TickSeconds;
        var realButtons = input.Buttons;
        // The heavy bolter's braced stance (Brace; fidelity gap 7): its transition this tick, and the input as the stance lets it act
        // (the presses it takes are lost, as in a reaction; the held flags follow the real buttons, below).
        bool rawMeleePress = ((input.Buttons & InputButtons.Melee) != 0 && !s.MeleeHeld) || ((input.Buttons & InputButtons.MeleeHeavy) != 0 && !s.MeleeHeavyHeld);
        var brace = Brace.Begin(s, s.Alive ? Loadouts.InHand(c, s)?.Brace : null,
            (input.Buttons & InputButtons.Reload) != 0 && !s.ReloadHeld && !s.DetonateHeld,
            (input.Buttons & InputButtons.Jump) != 0 && !s.JumpHeld && Evade.StartsFromRest(s, Loadouts.Evade(c, s), rawMeleePress, c), c);
        // A Reload press that entered or left the stance (feel/buffer-speed: used).
        if ((s.Brace == BracePhase.None && brace.Phase == BracePhase.Entering) || (s.Brace == BracePhase.Braced && brace.Phase == BracePhase.Exiting)) used |= InputBuffer.Bit(BufferedAction.Reload);
        var unmasked = input;
        input = Brace.Mask(input, brace);
        bool braceMasked = input != unmasked;
        bool meleeDown = (input.Buttons & InputButtons.Melee) != 0;
        bool meleePress = meleeDown && !s.MeleeHeld;
        bool heavyDown = (input.Buttons & InputButtons.MeleeHeavy) != 0;
        bool heavyPress = heavyDown && !s.MeleeHeavyHeld;
        bool fireDown = (input.Buttons & InputButtons.Fire) != 0;
        bool firePress = fireDown && !s.FireHeld;
        bool switchDown = (input.Buttons & InputButtons.Switch) != 0;
        bool slotPistolDown = (input.Buttons & InputButtons.SlotPistol) != 0;
        bool slotPrimaryDown = (input.Buttons & InputButtons.SlotPrimary) != 0;
        bool slotSecondDown = (input.Buttons & InputButtons.SlotSecond) != 0;
        // A toggle press, or a slot press naming a holstered weapon: one switch request, under the same rules below (protocol 44: to the slot SwitchTarget names).
        var switchTo = SwitchTarget(c, s, switchDown && !s.SwitchHeld, slotPistolDown && !s.SlotPistolHeld, slotPrimaryDown && !s.SlotPrimaryHeld, slotSecondDown && !s.SlotSecondHeld);
        bool switchPress = switchTo is not null;
        bool reloadDown = (input.Buttons & InputButtons.Reload) != 0;
        bool reloadPress = reloadDown && !s.ReloadHeld;
        bool grenadeDown = (input.Buttons & InputButtons.Grenade) != 0;
        bool grenadePress = grenadeDown && !s.GrenadeHeld;
        bool jumpDown = (input.Buttons & InputButtons.Jump) != 0;
        bool threw = false;
        // A Reload press that detonated grenades is not a reload request until the button is released.
        bool reloadBlocked = s.DetonateHeld && reloadDown;
        if (!s.Alive)
        {
            // Dead players do not move or act; the world respawns them when RespawnLeft reaches 0.
            events = default;
            return s with
            {
                RespawnLeft = MathF.Max(0f, s.RespawnLeft - dt), Velocity = Vector3.Zero, MeleeHeld = meleeDown, MeleeStep = 0, MeleeTime = 0f, MeleeQueued = false,
                FireHeld = fireDown, SwitchHeld = switchDown, SlotPistolHeld = slotPistolDown, SlotPrimaryHeld = slotPrimaryDown, SlotSecondHeld = slotSecondDown, SwitchLeft = 0f,
                ReloadHeld = reloadDown, Charging = false, ChargeTime = 0f, PendingShot = 0, PendingLeft = 0f, ReadyTicks = 0, RaiseTicks = 0,
                DetonateHeld = reloadBlocked, GrenadeHeld = grenadeDown, Throwing = false, ThrowTime = 0f, StimLeft = 0f, JumpHeld = jumpDown,
                GrenadeCooldown = MathF.Max(0f, s.GrenadeCooldown - dt), Aiming = false,
                MeleeHeavyHeld = heavyDown, MeleeHeavy = false, MeleeQueuedHeavy = false,
            };
        }

        // A hit reaction (Reaction; M28): over (by time, or ended by a Jump, melee or stick input from its branch times), or
        // holding this tick: then the tick runs with no movement and no buttons (presses are lost), and the knockback moves the body.
        var sanitized = input.Sanitized();
        var (reacting, reacted) = Reaction.Begin(s, c, jumpDown && !s.JumpHeld, meleePress || heavyPress, sanitized.MoveX != 0f || sanitized.MoveY != 0f,
            anyInput: sanitized.MoveX != 0f || sanitized.MoveY != 0f || input.Buttons != InputButtons.None);
        s = reacted with { RushLock = Math.Max(0, reacted.RushLock - 1) };   // the charge's spacing counts down every living tick, as the evade's
        // The charge, i.e. the bull rush (Rush; M29): under way, ended by time or input, or started by a heavy press while running forward.
        // Protocol 30: also the dash attack (a melee press in a jump-pack dash's window), and a charge held in the air (Rush.Held).
        bool rushing = false, rushHeld = false, fireDropped = false;
        if (!reacting)
        {
            // BR-02: the Tactical classes' charge is not ended by Fire (and fires nothing; below); every other class's is, as before.
            // sim/combat-2 (item 31, owner's decision 2026-10-30): W does not end the bull rush (it was started with W held and ended at its stick branch when W was
            // still down); any other stick direction (S, A / D alone) still does.
            bool stickEnds = (sanitized.MoveX != 0f || sanitized.MoveY != 0f) && !(s.Rush == RushPhase.Rush && Loadouts.Rush(c, s) is { } rushSpec && Rush.WHeld(input, rushSpec));
            // sim/no-fire-charging-all (protocol 48; owner 2026-10-31): neither Fire nor Aim ends the charge (for every class); only the stick (not W), a grenade, Jump,
            // melee or the clip's end do. They are ignored while it plays: no shot, no aim state (the camera stays), nothing buffered.
            bool other = stickEnds || grenadePress;
            var rushBefore = s;
            var rush = Rush.Begin(s, input, c, heavyPress, jumpDown && !s.JumpHeld, meleePress, other, world);
            (rushing, rushHeld, s) = (rush.Active, rush.Held, rush.State);
            // A charge ended this tick by the stick, a grenade, Jump or melee before its clip's end is still the charge's tick: the trigger fires nothing in it.
            fireDropped = !rushing && rushBefore.Rush != RushPhase.None && Loadouts.Rush(c, rushBefore) is not null && Rush.MoveOf(c, rushBefore) is { } endMove
                && Rush.Time(rushBefore, c) + 1e-5f < endMove.End;
            if (rush.Started) used |= InputBuffer.Bit(s.Rush == RushPhase.Rush ? BufferedAction.MeleeHeavy : BufferedAction.Melee);   // the bull rush or the dash attack
            // feel/melee-lock (protocol 32): the bull rush steers to its locked target, inside the charge's cone, while it still connects.
            if (rushing && !rushHeld && s.Rush == RushPhase.Rush && s.Grounded && Rush.Connecting(s, c)
                && MeleeLock.Target(s, sanitized, sanitized.MeleeTarget, others, c, world, s.RushTicks <= 1 ? MeleeLock.Phase.RushStart : MeleeLock.Phase.Rush) is { } rushTarget)
                // feel/charge-directions (protocol 39): a locked charge starts along the view (the stick's A / D offset only without a target) and steers from there.
                s = s with { Yaw = MeleeLock.Steer(rush.Started ? sanitized.Yaw : s.Yaw, MeleeLock.Offset(s, rushTarget), c) };
        }
        // sim/combat-pass (item 8; the owner's decision of 2026-10-30; protocol 49: Shift + W + F): A heavy press with Shift and W held that did not start a charge (the spacing
        // since the last one is not over, or the body is in the air) or a combo is under way) is never a stun strike, queued or struck: it is left unused, so the input buffer (0.3 s, an
        // estimate) replays it and the charge starts as soon as it is allowed (owner's follow-up decision).
        if (!rushing && heavyPress && Loadouts.Rush(c, s) is { } rushForKey && Rush.ChargeHeld(input, rushForKey)) heavyPress = false;
        // BR-02 (owner's recollection; for every class since protocol 48): nothing fires from the charge's start to its end. Fire is dropped from the input (below), and what a trigger
        // pressed just before had under way (a held charge, a delayed charged shot) is cancelled with the melee attacks'.
        bool noFire = rushing || fireDropped;
        if (fireDropped) fireDown = firePress = false;
        if (reacting || rushing)
        {
            input = input with { MoveX = 0f, MoveY = 0f, Buttons = InputButtons.None };
            meleeDown = meleePress = heavyDown = heavyPress = fireDown = firePress = switchDown = slotPistolDown = slotPrimaryDown = slotSecondDown = switchPress = false;
            reloadDown = reloadPress = grenadeDown = grenadePress = jumpDown = reloadBlocked = false;
        }

        // feel/locks-sprint-stop: a Jump press from an attack's (scaled) BRANCH_Evade ends the attack, so the evade or the jump-pack take-off below starts in
        // this tick (MeleeEvadeOpen); the attack is kept when neither starts.
        var beforeJumpBranch = s;
        bool meleeJumpBranch = jumpDown && !s.JumpHeld && s.MeleeStep > 0 && MeleeStepOf(c, s) is { } branchStep && MeleeEvadeOpen(branchStep, s.MeleeTime, c);
        if (meleeJumpBranch) s = s with { MeleeStep = 0, MeleeTime = 0f, MeleeQueued = false, MeleeHeavy = false, MeleeQueuedHeavy = false };

        // Jump pack (JumpPack): timers, power, take-off, lift-off and pound start; in a jump melee presses start no attack.
        var pack = Loadouts.JumpPack(c, s);
        var evadeSpec = Loadouts.Evade(c, s);
        // M44: with a jump-pack dash (the pack classes' evade) a tap dashes, a hold jumps; no take-off during the dash's flight.
        float? dashAt = pack is not null && evadeSpec is { Dash: true } ? evadeSpec.ToJumpAt : null;
        // Protocol 30: the height above the ground decides a press in flight between the pound and the sword slash (probed only then).
        // feel/jump-pack: a slash press buffered in the take-off also probes (JumpPack.Begin; it fires on the tick after the lift-off).
        bool heldOver = s.JumpSlash && pack?.Slash is { Snappy: true } && s.Jump == JumpPhase.Ascent;
        float aboveGround = (meleePress || heldOver) && pack?.Slash is not null && JumpPack.InFlight(s.Jump) ? JumpPack.AboveGround(s, world) : float.PositiveInfinity;
        var jump = JumpPack.Begin(s, pack, jumpDown, jumpDown && !s.JumpHeld && Evade.JumpAllowed(s, evadeSpec, c), meleePress, s.MeleeStep > 0, c, dashAt, aboveGround, longJump);
        if (jump.Started) used |= InputBuffer.Bit(BufferedAction.Jump);
        if (meleePress && (jump.PoundStart || (jump.Buffered && !(s.JumpSlash && s.Jump == JumpPhase.TakeOff)))) used |= InputBuffer.Bit(BufferedAction.Melee);   // the pound, the slash or its take-off buffer
        if (jump.BlocksMelee) { meleePress = false; heavyPress = false; }
        bool jumpBusy = jump.Busy;   // dive and recovery: the weapon, switch and throw wait
        // Evade (Evade): end, chaining and start; while it lasts, melee, switch and throw presses do nothing.
        bool aimDown = (input.Buttons & InputButtons.Aim) != 0;
        // M44: the dash's direction is latched from the stick at the take-off's press (a key let go a tick before Space still counts).
        var dashDirection = dashAt is not null && jump.Started ? Evade.DirectionOf(input.Sanitized().MoveX, input.Sanitized().MoveY, evadeSpec!.Diagonals) : s.JumpDashDirection;
        var evade = Evade.Begin(s, evadeSpec, input.Sanitized(), jumpDown && !s.JumpHeld, meleePress || heavyPress, switchPress, fireDown || aimDown || grenadePress, c,
            jump.DashStart, jump.Started, dashDirection);
        if (meleeJumpBranch && !jump.Started && !evade.Started) s = beforeJumpBranch;   // nothing took the press: the attack goes on
        bool evading = evade.Active;
        if (evade.Started) used |= InputBuffer.Bit(BufferedAction.Jump);
        if (evading) { meleePress = false; heavyPress = false; switchPress = false; grenadePress = false; }
        var lightSteps = Loadouts.Melee(c, s);
        var heavySteps = Loadouts.MeleeHeavy(c, s);
        bool heavy = s.MeleeHeavy;
        var steps = heavy ? heavySteps : lightSteps;   // the list the attack step indexes
        var weapon = Loadouts.InHand(c, s);
        if (brace.Phase == BracePhase.Braced && weapon is { Brace: not null }) weapon = Brace.Braced(weapon);   // braced fire (fidelity gap 7)
        var holstered = Loadouts.Holstered(c, s);
        float switchLeft = s.SwitchLeft - dt;
        if (switchLeft <= 1e-6f) switchLeft = 0f;
        // Fire drives the weapon (M31, owner's decision 2026-10-28): on a charge weapon a press starts the charge and a tap's
        // release fires the basic shot (below); on any other weapon it is the basic trigger.
        var charge = weapon?.Charge is { Stages.Count: > 0, FiresOnPress: false } spec ? spec : null;   // sim/instant-fire: the lascannon has no charge to hold, its press fires
        bool chargeDown = charge is not null && fireDown;
        bool chargePress = charge is not null && firePress;
        bool basicDown = charge is null && fireDown;
        bool basicPress = charge is null && firePress;
        // Whether the trigger asks for a shot this tick (the shot still needs ammunition and the cooldown).
        bool shotWanted = weapon is not null && switchLeft <= 0f && (weapon.Trigger == TriggerKind.SemiAuto ? basicPress : basicDown);
        int step = s.MeleeStep >= 1 && s.MeleeStep <= steps.Count ? s.MeleeStep : 0;
        if (step == 0) { heavy = false; steps = lightSteps; }
        float time = step > 0 ? s.MeleeTime : 0f;
        bool queued = step > 0 && s.MeleeQueued;
        bool queuedHeavy = queued && s.MeleeQueuedHeavy;
        bool cancelReload = false;
        bool startedNow = false;   // sim/combat-2 (item 39): an attack (from rest, or the next of a string) began in this very tick
        MeleeStep? motionStep = null;   // feel/melee-lock: the attack whose root motion and alignment this tick covers, over (motionFrom, motionTo]
        float motionFrom = 0f, motionTo = 0f;
        if (step > 0)
        {
            var m = steps[step - 1];
            float before = time;
            time += dt;
            if (m.Reach is not null) (motionStep, motionFrom, motionTo) = (m, MathF.Min(before + dt, m.Duration), MathF.Min(time + dt, m.Duration));   // sim/combat-2: one tick ahead of the clock (below)
            if (before < m.HitTime && m.HitTime <= time) { meleeHitStep = step; hitHeavy = heavy; }
            // Buffering (M28 adds heavy presses): before the combo window closes a press is kept for the next attack; a heavy
            // press during a light attack replaces a buffered light one; heavy presses during a heavy strike do nothing, except in
            // its Y-chain window (Weapons C: the Y,Y finishers, MeleeStep.YNext). A light press after an attack that ends the string does nothing.
            if (heavyPress && !heavy && before < m.ComboClose) { queued = true; queuedHeavy = true; heavyPress = false; used |= InputBuffer.Bit(BufferedAction.MeleeHeavy); }
            if (heavyPress && heavy && m.YNext > 0 && before < m.YComboClose) { queued = true; queuedHeavy = true; heavyPress = false; used |= InputBuffer.Bit(BufferedAction.MeleeHeavy); }
            if (meleePress && before < m.ComboClose && (heavy || !m.StringEnd)) { queued = true; meleePress = false; used |= InputBuffer.Bit(BufferedAction.Melee); }   // a later press does nothing (feel/buffer-speed: InputBuffer keeps it for the next attack)
            // A buffered heavy press chains into heavy strike n + 1 after light attack n (or the step's own target), when the weapon has one; else it is dropped.
            int heavyTarget = queuedHeavy ? MeleeChain.HeavyTarget(m, step, heavy, heavySteps.Count) : 0;
            if (queuedHeavy && heavyTarget == 0) { queued = false; queuedHeavy = false; }
            // The next attack starts only once this one's hit has landed (no data has a hit after its combo window opens).
            var (open, close) = MeleeChain.Window(m, heavy, queuedHeavy);
            if (queued && time >= open && before < close && time >= m.HitTime)
            {
                if (queuedHeavy) { step = heavyTarget; heavy = true; }
                else { step = heavy ? 1 : step % lightSteps.Count + 1; heavy = false; }   // after a heavy strike, light attack 1
                steps = heavy ? heavySteps : lightSteps;
                time = 0f; queued = false; queuedHeavy = false; startedNow = true;
                if (steps[step - 1].HitTime <= 0f) { meleeHitStep = step; hitHeavy = heavy; }
            }
            else if (time >= m.Duration) { step = 0; time = 0f; queued = false; queuedHeavy = false; heavy = false; steps = lightSteps; }
        }
        // From rest a heavy press starts heavy strike 1 (it wins over a light press in the same tick), a light press attack 1.
        if (step == 0 && !s.Throwing && ((heavyPress && heavySteps.Count > 0) || (meleePress && lightSteps.Count > 0)))
        {
            heavy = heavyPress && heavySteps.Count > 0;
            used |= InputBuffer.Bit(heavy ? BufferedAction.MeleeHeavy : BufferedAction.Melee);
            steps = heavy ? heavySteps : lightSteps;
            step = 1; time = 0f; queued = false; queuedHeavy = false; cancelReload = true; startedNow = true;
            if (steps[0].HitTime <= 0f) { meleeHitStep = 1; hitHeavy = heavy; }
        }
        // sim/combat-2 (item 39; the owner's decision of 2026-10-30: "the step forward of melee swings and F stun strikes starts the instant the button is pressed"):
        // the body's root motion runs one tick ahead of the attack's clock (the tick covering (t, t + dt] moves the body by the clip's (t + dt, t + 2dt]), so the press
        // tick itself already steps (0, dt]; before, the lunge began 1/30 s after the press. The attack's own clock, hit time and windows are unchanged.
        if (startedNow && steps[step - 1].Reach is not null) (motionStep, motionFrom, motionTo) = (steps[step - 1], 0f, MathF.Min(dt, steps[step - 1].Duration));
        if (step > 0 && !s.Throwing && (shotWanted || (chargePress && switchLeft <= 0f)) && (time >= steps[step - 1].RangedAt || (s.Slot == WeaponSlot.Pistol && basicPress && !startedNow && s.Clip > 0 && !s.Overheated && s.ReloadLeft <= 1e-6f && MathF.Max(0f, s.Cooldown - dt) <= TimeEpsilon)))   // review: only a shot that really leaves cancels
        {
            step = 0; time = 0f; queued = false; queuedHeavy = false;   // firing (or starting a charge) ends the attack; handled below this tick
            motionStep = null;   // and its root motion
        }

        var slot = s.Slot;
        int clip = s.Clip, reserve = s.Reserve, otherClip = s.OtherClip, otherReserve = s.OtherReserve, thirdClip = s.ThirdClip, thirdReserve = s.ThirdReserve;
        float reloadLeft = cancelReload ? 0f : s.ReloadLeft;
        float otherReloadLeft = s.OtherReloadLeft, thirdReloadLeft = s.ThirdReloadLeft;   // sim/combat-pass: reloads of the holstered weapons
        bool charging = s.Charging && charge is not null;
        float chargeTime = charging ? s.ChargeTime : 0f;
        int pending = weapon is not null && Shots.ChargeShotOf(weapon, s.PendingShot) is not null ? s.PendingShot : 0;
        float pendingLeft = pending > 0 ? s.PendingLeft : 0f;
        int projectilesOut = s.ProjectilesOut;
        if (step > 0 || evade.Started || noFire) { charging = false; chargeTime = 0f; pending = 0; pendingLeft = 0f; }   // a melee attack or an evade cancels them (and a raise: below)
        if (jump.PoundStart) { charging = false; chargeTime = 0f; pending = 0; pendingLeft = 0f; reloadLeft = 0f; }   // and so does a ground pound (and a reload)
        // Venting (Vent; fidelity gap 7): on Reload or by itself while overheated, while the player stands or walks and does nothing else (owner's
        // recollection: everything but walking stops it); it adds to the in-hand weapon's cooling and takes the weapon (no shot, charge or reload).
        bool switching = switchPress && step == 0 && !s.Throwing && !jumpBusy && weapon is not null && holstered is not null;
        bool ventFree = Vent.Free(reacting || rushing || step > 0 || s.Throwing || grenadePress || evading || switching || switchLeft > 0f || jumpBusy || pending > 0
            || (aimDown && weapon is not null) || jump.Started || jump.Phase != JumpPhase.None
            || (charging && !(reloadPress && !reloadBlocked && charge!.ReloadCancels)), input, s, c);
        bool venting = Vent.Runs(weapon, s, ventFree, reloadPress && !reloadBlocked, fireDown);
        if (venting && !s.Venting && reloadPress) used |= InputBuffer.Bit(BufferedAction.Reload);
        if (venting) { charging = false; chargeTime = 0f; }
        // Both weapons cool every tick (each with its own spec), before any switch or shot of this tick.
        var (heat, overheated, sinceHeat) = Cool(weapon?.Heat, s.Heat, s.Overheated, s.SinceHeat, dt);
        if (venting) (heat, overheated) = Vent.Step(weapon!.Vent!, weapon.Heat!, heat, overheated, dt);   // on top of the cooling (owner's recollection)
        var (otherHeat, otherOverheated, otherSinceHeat) = Cool(Loadouts.Weapon(c, s, Loadouts.Other(s.Slot))?.Heat, s.OtherHeat, s.OtherOverheated, s.OtherSinceHeat, dt);
        var (thirdHeat, thirdOverheated, thirdSinceHeat) = Cool(Loadouts.Weapon(c, s, Loadouts.Third(s.Slot))?.Heat, s.ThirdHeat, s.ThirdOverheated, s.ThirdSinceHeat, dt);
        bool switched = false;
        // sim/switch-cancel (owner's recollection 2026-10-31): a request while a switch is under way is not blocked; it cancels that switch and starts the new one (full switch time).
        if (switchPress && step == 0 && !s.Throwing && !jumpBusy && weapon is not null && switchTo!.Value != s.Slot && Loadouts.Weapon(c, s, switchTo.Value) is { } next)
        {
            switched = true;
            used |= InputBuffer.Bit(BufferedAction.Switch);
            // Protocol 44: three stores (in hand, first holstered, second holstered) follow the weapons to their new places (two weapons: a plain swap).
            var from = slot;
            slot = switchTo.Value;
            // sim/combat-pass (item 10, owner's recollection 2026-10-30): a reload in progress is not lost; the holstered weapon's runs on (below) and a weapon
            // taken back in hand has what is left of its own.
            var stores = new (int Clip, int Reserve, float Heat, bool Overheated, float SinceHeat, float Reload)[3];
            stores[(int)from] = (clip, reserve, heat, overheated, sinceHeat, reloadLeft);
            stores[(int)Loadouts.Other(from)] = (otherClip, otherReserve, otherHeat, otherOverheated, otherSinceHeat, otherReloadLeft);
            stores[(int)Loadouts.Third(from)] = (thirdClip, thirdReserve, thirdHeat, thirdOverheated, thirdSinceHeat, thirdReloadLeft);
            (clip, reserve, heat, overheated, sinceHeat, reloadLeft) = stores[(int)slot];
            (otherClip, otherReserve, otherHeat, otherOverheated, otherSinceHeat, otherReloadLeft) = stores[(int)Loadouts.Other(slot)];
            (thirdClip, thirdReserve, thirdHeat, thirdOverheated, thirdSinceHeat, thirdReloadLeft) = stores[(int)Loadouts.Third(slot)];
            weapon = next;
            switchLeft = c.WeaponSwitchTime;
            if (switchLeft <= 1e-6f) switchLeft = 0f;
            shotWanted = false;
            charging = false; chargeTime = 0f; pending = 0; pendingLeft = 0f;   // and so is a charge or a pending shot
        }

        // sim/combat-pass (item 10): the holstered weapons' reloads run on while they are not in hand (owner's recollection: "reloads continue while the weapon
        // is not equipped", all weapons), on their own timers, whatever the player is doing.
        if (Loadouts.Weapon(c, s, Loadouts.Other(slot)) is { } otherSpec) ProgressHolstered(otherSpec, ref otherClip, ref otherReserve, ref otherReloadLeft);
        if (Loadouts.Weapon(c, s, Loadouts.Third(slot)) is { } thirdSpec) ProgressHolstered(thirdSpec, ref thirdClip, ref thirdReserve, ref thirdReloadLeft);
        void ProgressHolstered(WeaponSpec w, ref int hClip, ref int hReserve, ref float hLeft)
        {
            if (hLeft <= 0f) return;
            hLeft -= dt;
            if (hLeft > (w.Recharge is null ? 1e-6f : TimeEpsilon)) return;
            int moved = Math.Min(w.Recharge is null ? w.ClipSize - hClip : Math.Min(1, w.ClipSize - hClip), hReserve);
            hClip += Math.Max(0, moved); hReserve -= Math.Max(0, moved);
            hLeft = w.Recharge is { } r && hClip < w.ClipSize && hReserve > 0 ? r.RoundTime : 0f;   // a recharging clip goes on round by round
        }

        // Throwing (rules provisional; see the summary): progress, release and end of a throw in progress, else a new throw.
        var item = Loadouts.Equipment(c, s);
        int grenades = s.Grenades;
        float grenadeCooldown = MathF.Max(0f, s.GrenadeCooldown - dt);
        if (grenadeCooldown <= TimeEpsilon) grenadeCooldown = 0f;
        bool throwing = s.Throwing && item is not null && !jump.PoundStart && !evade.Started;   // a ground pound cancels a throw (before its release: nothing spent); feel/locks-sprint-stop: so does an evade past the throw's scaled gate (released by then)
        float throwTime = throwing ? s.ThrowTime : 0f;
        float releaseAt = item?.ReleaseAt(c) ?? c.ThrowRelease, useEnd = item?.DurationOf(c) ?? c.ThrowDuration;   // perks/inert-2: a stim's use has its own times
        float stimLeft = s.StimLeft > dt + TimeEpsilon ? s.StimLeft - dt : 0f;   // perks/inert-2: the stim's modifiers wear off
        if (throwing)
        {
            float before = throwTime;
            throwTime += dt;
            if (before + TimeEpsilon < releaseAt && throwTime + TimeEpsilon >= releaseAt) Release();
            if (throwTime + TimeEpsilon >= useEnd) { throwing = false; throwTime = 0f; }
        }
        else if (grenadePress && item is not null && grenades > 0 && grenadeCooldown <= 0f && step == 0 && !jumpBusy && switchLeft <= 0f && pending == 0)
        {
            throwing = true; throwTime = 0f;
            used |= InputBuffer.Bit(BufferedAction.Grenade);
            reloadLeft = 0f;                          // the reload in progress is lost
            charging = false; chargeTime = 0f;        // and so is a charge
            shotWanted = false;
            if (releaseAt <= TimeEpsilon) Release();
            if (useEnd <= TimeEpsilon) { throwing = false; throwTime = 0f; }
        }
        void Release()
        {
            grenades--;
            grenadeCooldown = item!.Cooldown;
            threw = true;
            if (item.Stim is { } stim) stimLeft = stim.Lifetime;   // perks/inert-2: the stim works from its release (nothing is launched)
        }

        // Aim mode (protocol 12; interpretation, spec/behavior.md): the Aim button held with a ranged weapon in hand, outside
        // melee attacks, throws, jump-pack jumps and dives, and evades.
        bool aiming = aimDown && weapon is not null && step == 0 && !throwing && !threw
            && !JumpPack.Jumping(jump.Phase) && !JumpPack.Pounding(jump.Phase) && !evading;
        // Raising the weapon (M31; see the summary): what lowered it this tick, the raise under way, and whether it is up.
        bool lowering = reacting || rushing || step > 0 || throwing || threw || evading || switched || jump.Started || jump.PoundStart || jumpBusy;
        int raise = lowering || weapon is null ? 0 : s.RaiseTicks;
        bool raised = weapon is null || !weapon.WaitsToRaise || aiming || (!lowering && s.ReadyTicks > 0);

        float since = s.SinceDamage + dt;
        var body = Loadouts.Body(c, s);   // M43: the perks' regeneration and shield (Larraman's Blessing, Iron Halo, Artificer Armor)
        float shield = s.SinceDamage >= body.ShieldRegenDelay ? MathF.Min(body.ShieldMax, s.Shield + body.ShieldRegenRate * dt) : s.Shield;
        float health = s.SinceDamage >= body.HealthRegenDelay ? MathF.Min(body.HealthMax, s.Health + body.HealthRegenRate * dt) : s.Health;
        float cooldown = MathF.Max(0f, s.Cooldown - dt);
        // A burst under way (M43, Burst Fire): lost with the weapon in hand changing or being lowered, or without a burst weapon.
        int burstLeft = weapon is { Bursts: true } && !lowering && !switched ? s.BurstLeft : 0;

        void BasicShot(WeaponSpec w)
        {
            clip -= Math.Min(Math.Max(1, w.AmmoPerShot), clip);
            cooldown += w.ShotCooldown;
            fired = true; shotStage = 0;
            (heat, overheated, sinceHeat) = AddShotHeat(w.Heat, heat, overheated);
            if (Shots.RemoteDetonated(w)) projectilesOut = Math.Min(projectilesOut + 1, w.Projectile!.MaxActive);
            if (w.Bursts) burstLeft = w.BurstLength - 1;   // the burst's later rounds follow (BurstRound)
        }
        // A burst's later round (M43): due once BurstInterval × its number has passed since the first (counted on the cooldown,
        // which the first round set to ShotCooldown); spends ammunition and adds heat like the first, no cooldown of its own.
        bool BurstRound(WeaponSpec w)
        {
            int number = w.BurstLength - burstLeft;   // 1 for the second round
            // Once the cooldown has run out every round is due (never a locked weapon, whatever the data).
            if (cooldown > TimeEpsilon && w.ShotCooldown - cooldown + TimeEpsilon < number * w.BurstInterval) return false;
            clip -= Math.Min(Math.Max(1, w.AmmoPerShot), clip);
            fired = true; shotStage = 0;
            (heat, overheated, sinceHeat) = AddShotHeat(w.Heat, heat, overheated);
            burstLeft--;
            return true;
        }
        // A basic shot when the weapon is up; with the weapon down the trigger starts the raise and the shot waits for it (M31).
        void Shoot(WeaponSpec w)
        {
            if (raised) BasicShot(w);
            else raise = RaiseTicksOf(w, c);
        }
        void ChargedShot(WeaponSpec w, int stage)
        {
            var shot = Shots.ChargeShotOf(w, stage)!;
            clip -= Math.Min(Math.Max(0, shot.AmmoCost), clip);
            cooldown += shot.ShotCooldown;
            fired = true; shotStage = stage;
            if (w.Heat is { } hs && shot.HeatPerShot > 0f)
            {
                heat = MathF.Min(hs.Overheat, heat + shot.HeatPerShot); sinceHeat = 0f;
                overheated |= heat >= hs.Overheat;
            }
        }
        // One tick of holding the charge: its time, then the reached stage's heat (an overheat cancels the charge).
        void ChargeTick(WeaponSpec w, ChargeSpec ch)
        {
            chargeTime += dt;
            int reached = ChargeStage(ch, chargeTime, firing: false);
            if (reached == 0 || w.Heat is not { } hs || ch.Stages[reached - 1].HeatPerSecond <= 0f) return;
            heat = MathF.Min(hs.Overheat, heat + ch.Stages[reached - 1].HeatPerSecond * dt); sinceHeat = 0f;
            if (heat >= hs.Overheat) { overheated = true; charging = false; chargeTime = 0f; }
        }

        // A reload (or a recharge) in progress runs down and moves its rounds when done.
        void ProgressReload(WeaponSpec w)
        {
            reloadLeft -= dt;
            // A recharge (M13) uses the whole-tick tolerance of the other timers; a reload keeps its M6 rule.
            if (reloadLeft <= (w.Recharge is null ? 1e-6f : TimeEpsilon))
            {
                // A recharging clip gets one round per recharge; a reload fills the clip.
                int moved = Math.Min(w.Recharge is null ? w.ClipSize - clip : Math.Min(1, w.ClipSize - clip), reserve);
                clip += moved; reserve -= moved; reloadLeft = 0f;
            }
        }

        if (step > 0 || throwing || threw || jumpBusy || noFire)
        {
            // Attacking or throwing: the weapon waits (fire before RangedAt and reload input are ignored).
        }
        else if (evading)
        {
            // Evading: a reload in progress carries on; nothing else of the weapon acts (interpretation).
            if (weapon is not null && switchLeft <= 0f && reloadLeft > 0f) ProgressReload(weapon);
        }
        else if (venting)
        {
            // Venting (fidelity gap 7): the vent takes the weapon this tick.
        }
        else if (weapon is null)
        {
            reloadLeft = 0f;   // nothing in hand
        }
        else if (switchLeft > 0f)
        {
            // Switching: the weapon cannot fire or reload yet.
        }
        else
        {
            // Remote detonation does not depend on what the weapon is doing; the press then starts no reload.
            bool reloadUsed = reloadBlocked;
            if (reloadPress && projectilesOut > 0 && Shots.RemoteDetonated(weapon)) { detonate = true; projectilesOut = 0; reloadUsed = true; used |= InputBuffer.Bit(BufferedAction.Reload); }
            bool busy = false;   // a pending shot or a charge took the weapon this tick
            if (pending > 0)
            {
                busy = true;
                pendingLeft -= dt;
                if (pendingLeft <= 1e-6f) { ChargedShot(weapon, pending); pending = 0; pendingLeft = 0f; }
            }
            else if (charging && charge is not null)
            {
                if (!chargeDown)
                {
                    // Release: the last reached stage with a shot fires (after its delay); none reached (a tap): the basic shot,
                    // which waits for a raise still under way (below).
                    busy = true;
                    int stage = ChargeStage(charge, chargeTime, firing: true);
                    charging = false; chargeTime = 0f;
                    if (stage > 0)
                    {
                        raise = 0;   // the charged shot is the one that leaves; no basic shot after the raise
                        // sim/instant-fire (owner, 2026-10-31): the charged shot leaves on the release tick; the data's FireDelay (plasma cannon 0.1 s) is not applied.
                        ChargedShot(weapon, stage);
                    }
                    else if (raise == 0 && clip > 0 && cooldown <= 1e-6f) BasicShot(weapon);
                }
                else if (reloadDown && !reloadUsed && charge.ReloadCancels) { charging = false; chargeTime = 0f; raise = 0; if (reloadPress) used |= InputBuffer.Bit(BufferedAction.Reload); }   // a reload may start below
                else { busy = true; ChargeTick(weapon, charge); }
            }
            if (!busy && burstLeft > 0)
            {
                // A burst under way (M43): its rounds leave on their own time; nothing else of the weapon meanwhile. An empty
                // clip or an overheat ends it.
                busy = true;
                if (clip <= 0 || overheated) burstLeft = 0;
                else BurstRound(weapon);
            }
            if (raise > 0 && pending == 0)
            {
                // The weapon is coming up (M31): nothing else meanwhile; the shot waiting for it leaves in the tick it is up,
                // unless the trigger still holds a charge (that charge then fires on its release as usual).
                busy = true;
                if (--raise == 0 && !charging && clip > 0 && !overheated) BasicShot(weapon);
            }
            if (busy) { }
            else if (weapon.Recharge is not null && reloadLeft > 0f && shotWanted && clip > 0 && cooldown <= 1e-6f && !overheated)
            {
                reloadLeft = 0f;   // a shot interrupts a recharge (the round under way is lost; the next starts over)
                Shoot(weapon);
            }
            else if (reloadLeft > 0f) ProgressReload(weapon);
            else if (overheated)
            {
                // Overheated: the weapon neither fires nor reloads until its heat falls to the recovery amount.
            }
            else
            {
                // A recharging clip has no reload input: it refills by itself whenever below capacity.
                bool wantsReload = weapon.Recharge is not null || (reloadDown && !reloadUsed) || ((basicDown || chargeDown) && clip == 0);
                bool shootsNow = weapon.Recharge is not null && shotWanted && clip > 0 && cooldown <= 1e-6f;
                if (wantsReload && !shootsNow && clip < weapon.ClipSize && reserve > 0)
                {
                    reloadLeft = weapon.Recharge?.RoundTime ?? weapon.ReloadTime;
                    if (reloadPress && !reloadUsed) used |= InputBuffer.Bit(BufferedAction.Reload);
                }
                else if (chargePress && charge is not null && cooldown <= 1e-6f && clip >= Math.Max(1, charge.AmmoToStart))
                {
                    charging = true; chargeTime = 0f;
                    ChargeTick(weapon, charge);
                    if (!raised) raise = RaiseTicksOf(weapon, c);   // the weapon comes up while the charge builds
                }
                // A charge that cannot start (too few rounds) fires the basic shot on the press instead.
                else if ((shotWanted || chargePress) && clip > 0 && cooldown <= 1e-6f) Shoot(weapon);
            }
        }

        // Movement after the weapon, so a charge started this tick slows it at once.
        float speedScale = charging && charge is not null && ChargeStage(charge, chargeTime, firing: false) is var reachedStage and > 0
            ? charge.Stages[reachedStage - 1].MoveSpeedMultiplier : 1f;
        // An overheat's speed modifier (the plasma cannon; fidelity gap 7), from either weapon (it is on the player).
        speedScale *= Vent.OverheatSpeed(weapon?.Heat, overheated, sinceHeat) * Vent.OverheatSpeed(Loadouts.Weapon(c, s, Loadouts.Other(slot))?.Heat, otherOverheated, otherSinceHeat)
            * Vent.OverheatSpeed(Loadouts.Weapon(c, s, Loadouts.Third(slot))?.Heat, thirdOverheated, thirdSinceHeat);
        var moveInput = step > 0 && time < MoveAtOf(steps[step - 1], c)   // feel/locks-sprint-stop: MoveAt × the lock scale
            ? input with { MoveX = 0f, MoveY = 0f, Buttons = input.Buttons & ~(InputButtons.Sprint | InputButtons.Jump) }
            : input;
        // Hip fire (M27; supported by evidence, spec/behavior.md "Hip fire"): the behaviour graphs have no sprint-fire state, a
        // shot ends the sprint. While the trigger is held with a ranged weapon in hand (or a charge, delayed charged shot or
        // raise runs) the Sprint bit is not applied, so the player moves at run speed and sprints again once the trigger is released.
        // The same for every weapon; the held trigger (not only the press tick) keeps a semi-automatic weapon from flickering.
        bool shooting = weapon is not null && step == 0 && !throwing && switchLeft <= 0f && (basicDown || chargeDown || charging || pending > 0 || raise > 0 || burstLeft > 0);
        if (shooting) moveInput = moveInput with { Buttons = moveInput.Buttons & ~InputButtons.Sprint };
        // feel/melee-lock: an attack that starts from rest this tick with a lock the start's cone accepts faces the direction that found it (the stick's,
        // else the view's: MeleeLock.SearchDirection), so the target stays in the attack's cone while its alignment window turns to it.
        // (feel/melee-charge-2: kept: only a stick pushed to a side turns the body here, the original's directional attack; the turn to the target itself eases.)
        // sim/combat-pass (item 19; the owner's decision of 2026-10-30): directional melee. The stick's way of eight (Evade.DirectionOf, dead zone 0.1) at the tick an
        // attack begins sets the facing it is struck in, relative to the view: S turns the body round and strikes backwards, A / D sideways, the diagonals between,
        // no key forwards. Latched for the attack (PlayerState.MeleeDirection; it follows the view's turns), re-read by each combo attack. A lock the start's cone
        // accepts (the search already goes along the stick) still turns the body to the stick's continuous direction, then to the target.
        bool newAttack = step > 0 && startedNow;
        var meleeDirection = step == 0 ? EvadeDirection.Forward : newAttack ? Evade.DirectionOf(sanitized.MoveX, sanitized.MoveY, diagonals: true) : s.MeleeDirection;
        float meleeTurn = Rush.DashOffset(meleeDirection);
        if (step > 0 && time < MoveAtOf(steps[step - 1], c)) moveInput = moveInput with { Yaw = sanitized.Yaw + meleeTurn };   // while the stick is held off (a stick that moves the body keeps the view's frame)
        if (s.MeleeStep == 0 && step > 0 && sanitized.MeleeTarget > 0
            && MeleeLock.Target(s, sanitized, sanitized.MeleeTarget, others, c, world, MeleeLock.Phase.MeleeStart) is not null)
            moveInput = moveInput with { Yaw = MeleeLock.YawOf(MeleeLock.SearchDirection(sanitized)) };
        // A sprint lowers the weapon (M31): the Sprint bit applied with the stick forward, walking on the ground (no aim mode).
        bool sprinting = !reacting && !rushing && !evading && !aiming && jump.Phase == JumpPhase.None && s.Grounded
            && (moveInput.Buttons & InputButtons.Sprint) != 0 && moveInput.Sanitized().MoveY > 0f;
        bool weaponUp = fired || charging || pending > 0 || raise > 0 || aiming || (weapon is { WaitsToRaise: false } && !lowering && !sprinting);
        int readyTicks = weapon is null ? 0 : weaponUp ? c.ReadyHoldTicks : lowering || sprinting ? 0 : Math.Max(0, s.ReadyTicks - 1);
        // Planting the feet (fidelity gap 7): the press tick of a brace stops the body at once (ours, after the owner: "could not walk"; the entry
        // clip has no root motion), so a sprint does not slide on through the entry.
        if (s.Brace == BracePhase.None && brace.Phase != BracePhase.None) s = s with { Velocity = new Vector3(0f, 0f, s.Velocity.Z) };
        var jumpEvent = JumpEventKind.None;
        bool wallHit = false, dashLanded = false;
        // feel/melee-lock (protocol 32): an attack with reach data moves the body by its root motion along its heading, which turns to the locked
        // target in the attack's alignment window (MeleeLock). On the ground outside the jump pack's phases, until MoveAt, after it while the stick is idle.
        (float Yaw, Vector2 Velocity)? meleeGlide = null;
        if (motionStep is { } attack && motionTo > motionFrom && !reacting && !rushing && !evading && s.Grounded && jump.Phase == JumpPhase.None && !jump.Started
            && (motionFrom - (startedNow ? 0f : dt) + TimeEpsilon < MoveAtOf(attack, c) || (sanitized.MoveX == 0f && sanitized.MoveY == 0f)))
        {
            Vector2? toTarget = MeleeLock.Target(s, sanitized, sanitized.MeleeTarget, others, c, world, s.MeleeStep == 0 ? MeleeLock.Phase.MeleeStart : MeleeLock.Phase.Melee) is { } target ? MeleeLock.Offset(s, target) : null;
            // The press tick starts from the strike's facing (the view turned by the stick's way; before, the next tick started from it), later ticks from the heading they hold.
            float heading = MeleeLock.Align(startedNow ? sanitized.Yaw + meleeTurn : s.Yaw, sanitized.Yaw + meleeTurn, attack, motionFrom, motionTo, toTarget, c);
            float lunge = MeleeLock.LungeStep(attack, motionFrom, motionTo, heading, toTarget, c);
            meleeGlide = (heading, MeleeLock.Forward(heading) * (lunge / dt));
        }
        var beforeMove = s;
        s = (reacting || rushing ? (reacting ? Reaction.Move(s, input.Sanitized().Yaw, c, world) : Rush.Move(s, c, world, rushHeld)) with
            {
                JumpPower = pack is not null ? jump.Power : s.JumpPower, Jump = JumpPhase.None, JumpTicks = 0, JumpMark = 0, JumpHolding = false, JumpLandMoving = false, JumpCut = false,
                JumpSlash = false,
            }
            : evading ? Evade.Step(s, evadeSpec!, evade, c, world, out wallHit, out dashLanded) with
            {
                // M44: a dash (jump-pack classes) keeps the pack's power and recharge; no jump runs during it.
                JumpPower = pack is not null ? jump.Power : s.JumpPower, Jump = JumpPhase.None, JumpTicks = 0, JumpMark = 0, JumpHolding = false, JumpLandMoving = false, JumpCut = false,
                JumpSlash = false,
            }
            : pack is not null ? JumpPack.Move(s, moveInput, c, pack, world, speedScale, jump, out jumpEvent, aiming) : Movement.Step(s, moveInput, c, world, speedScale, aiming))
            with { Pitch = input.Pitch, Aiming = aiming };
        // sim/combat-pass (review note): the chosen direction holds through the hit even when the hit comes after the move-on time (the walk then uses the view's frame, the body keeps the strike's facing).
        if (step > 0 && meleeGlide is null && !reacting && !rushing && !evading && time < MathF.Max(MoveAtOf(steps[step - 1], c), steps[step - 1].HitTime + dt + TimeEpsilon))
            s = s with { Yaw = sanitized.Yaw + meleeTurn };
        if (meleeGlide is { } glide)
        {
            // The attack's motion replaces the walk (the jump pack's bookkeeping of the walk tick stays).
            var glided = Movement.Glide(beforeMove, glide.Yaw, glide.Velocity, c, world);
            // The clip's speed is not momentum: a lunge that left the ground drops from there (the walk-off rule) instead of flying on at the lunge's speed.
            var velocity = glided.Grounded ? glided.Velocity : new Vector3(0f, 0f, glided.Velocity.Z);
            s = s with { Position = glided.Position, Velocity = velocity, Yaw = glided.Yaw, Grounded = glided.Grounded };
        }
        // feel/locks-sprint-stop (the owner's change): the sprint stop (SprintStop). With every movement key let go after a sprint the body slows along its
        // velocity (v² falling at SprintStop.Rate) instead of braking at once; a movement key, Jump, Fire or Aim, or an action that moves the body, cancels it
        // into the normal movement (the walk above). The jump pack's bookkeeping of the walk tick stays, as for the attack's motion.
        bool sprintStopping = false;
        if (SprintStop.Continues(start, c) && !reacting && !rushing && !evading && meleeGlide is null && step == 0 && jump.Phase == JumpPhase.None && !jump.Started
            && brace.Phase == BracePhase.None && beforeMove.Grounded && !SprintStop.Cancels(sanitized, realButtons))
        {
            var slowed = SprintStop.Slowed(new Vector2(beforeMove.Velocity.X, beforeMove.Velocity.Y), c);
            var glided = Movement.Glide(beforeMove, sanitized.Yaw, slowed, c, world);
            s = s with { Position = glided.Position, Velocity = glided.Velocity, Yaw = glided.Yaw, Grounded = glided.Grounded };
            sprintStopping = slowed != Vector2.Zero;
        }
        if (wallHit) evading = false;   // a wall within the hit angle ends the evade (after this tick's motion)
        var (evadeTicks, evadeDirection, evadeLanded) = Evade.After(evade, evading, dashLanded);
        events = new SelfEvents(fired, shotStage, meleeHitStep, detonate, threw, jumpEvent, hitHeavy, JumpPack.SlashHits(s, pack, c));
        var result = s with
        {
            SinceDamage = since, Shield = shield, Health = health, Cooldown = cooldown,
            ReloadLeft = reloadLeft, OtherReloadLeft = otherReloadLeft, ThirdReloadLeft = thirdReloadLeft, Clip = clip, Reserve = reserve,
            Slot = slot, OtherClip = otherClip, OtherReserve = otherReserve, SwitchLeft = switchLeft,
            ThirdClip = thirdClip, ThirdReserve = thirdReserve, ThirdHeat = thirdHeat, ThirdOverheated = thirdOverheated, ThirdSinceHeat = thirdSinceHeat,
            Heat = heat, Overheated = overheated, SinceHeat = sinceHeat, OtherHeat = otherHeat, OtherOverheated = otherOverheated, OtherSinceHeat = otherSinceHeat,
            FireHeld = fireDown, SwitchHeld = switchDown, SlotPistolHeld = slotPistolDown, SlotPrimaryHeld = slotPrimaryDown, SlotSecondHeld = slotSecondDown,
            MeleeStep = step, MeleeTime = time, MeleeQueued = queued, MeleeHeld = meleeDown,
            ReloadHeld = reloadDown, Charging = charging, ChargeTime = chargeTime, PendingShot = pending, PendingLeft = pendingLeft,
            ReadyTicks = readyTicks, RaiseTicks = raise, BurstLeft = burstLeft,
            DetonateHeld = (detonate || reloadBlocked) && reloadDown,
            ProjectilesOut = projectilesOut,
            Grenades = grenades, GrenadeCooldown = grenadeCooldown, Throwing = throwing, ThrowTime = throwTime, GrenadeHeld = grenadeDown, StimLeft = stimLeft,
            JumpHeld = jumpDown,
            EvadeTicks = evadeTicks, EvadeDirection = evadeDirection, EvadeLock = evade.Lock, EvadeLanded = evadeLanded,
            JumpDashDirection = dashAt is not null && s.Jump == JumpPhase.TakeOff ? dashDirection : EvadeDirection.Forward,
            MeleeHeavy = step > 0 && heavy, MeleeQueuedHeavy = queued && queuedHeavy, MeleeHeavyHeld = heavyDown,
            BlindTicks = Math.Max(0, s.BlindTicks - 1),
            Brace = brace.Phase, BraceTicks = brace.Ticks, Venting = venting && heat > 0f,
            SprintStop = sprintStopping, MeleeDirection = meleeDirection,
        };
        if (!reacting && !rushing && !braceMasked && !fireDropped) return result;
        // Reacting, charging or a brace mask (fidelity gap 7): the buttons were ignored, but whether they are held is the real input's (a press during the reaction is lost).
        bool Real(InputButtons b) => (realButtons & b) != 0;
        return result with
        {
            FireHeld = Real(InputButtons.Fire), SwitchHeld = Real(InputButtons.Switch), SlotPistolHeld = Real(InputButtons.SlotPistol),
            SlotPrimaryHeld = Real(InputButtons.SlotPrimary), SlotSecondHeld = Real(InputButtons.SlotSecond), MeleeHeld = Real(InputButtons.Melee), MeleeHeavyHeld = Real(InputButtons.MeleeHeavy),
            ReloadHeld = Real(InputButtons.Reload), DetonateHeld = s.DetonateHeld && Real(InputButtons.Reload),
            GrenadeHeld = Real(InputButtons.Grenade), JumpHeld = Real(InputButtons.Jump),
        };
    }

    /// <summary>
    /// Whether this tick's presses ask for a weapon switch (M22). The slot the presses name: SlotPistol's press first, then
    /// SlotPrimary's (both in one tick: the pistol); without a slot press, the Switch toggle's press asks for the other slot.
    /// A named slot asks for a switch only when it is not in hand and holds a weapon (<see cref="Loadouts.Holstered"/>);
    /// a slot press overrides a Switch press in the same tick. Whether the switch then happens is
    /// <see cref="StepPlayer"/>'s rules for the request (melee, throw, jump dive, a switch under way, evade timing).
    /// </summary>
    public static bool SwitchRequested(SimConfig c, PlayerState s, bool togglePress, bool pistolPress, bool primaryPress, bool secondPress = false) =>
        SwitchTarget(c, s, togglePress, pistolPress, primaryPress, secondPress) is not null;

    /// <summary>
    /// The slot a switch request goes to (protocol 44), or null for none: a named slot (pistol first, then primary, then the second primary) when it is not in hand
    /// and holds a weapon; without a slot press, a toggle press goes to <see cref="Loadouts.ToggleSlot"/>.
    /// </summary>
    public static WeaponSlot? SwitchTarget(SimConfig c, PlayerState s, bool togglePress, bool pistolPress, bool primaryPress, bool secondPress = false)
    {
        WeaponSlot? named = pistolPress ? WeaponSlot.Pistol : primaryPress ? WeaponSlot.Ranged : secondPress ? WeaponSlot.Second : null;
        if (named is not { } slot) return togglePress ? Loadouts.ToggleSlot(c, s) ?? s.Slot : null;   // a toggle press with nothing to go to is still a request (it ends an evade), to the slot in hand
        return slot != s.Slot && Loadouts.Weapon(c, s, slot) is not null ? slot : null;
    }

    /// <summary>
    /// Most ticks a raise takes (protocol 33: 6 bits of the own record's raise byte, the other two carry the input buffer's ticks; 2.1 s at 30 Hz,
    /// against the data's longest raise of 0.2 s; it was 255 before).
    /// </summary>
    public const int MaxRaiseTicks = 63;

    /// <summary>Ticks a raise of <paramref name="w"/> takes (M31): its <see cref="WeaponSpec.RaiseDelay"/> rounded up to whole ticks, 1 to <see cref="MaxRaiseTicks"/>.</summary>
    public static int RaiseTicksOf(WeaponSpec w, SimConfig c) =>
        Math.Clamp((int)MathF.Ceiling((float.IsFinite(w.RaiseDelay) ? w.RaiseDelay : 0f) / c.TickSeconds - 1e-3f), 1, MaxRaiseTicks);

    /// <summary>
    /// Whether this tick's Sprint bit would be applied (M31's charge gate until protocol 39, which needs only W; still the vent's sprint rule): held, and not taken away by aim mode (the
    /// Aim button with a ranged weapon in hand) or by the trigger rule of <see cref="StepPlayer"/> (Fire held with a weapon in hand, a
    /// charge, a delayed charged shot or a raise under way).
    /// </summary>
    public static bool SprintApplies(PlayerState s, PlayerInput input, SimConfig c)
    {
        if ((input.Buttons & InputButtons.Sprint) == 0) return false;
        bool armed = Loadouts.InHand(c, s) is not null;
        // Fire takes the sprint away only outside a weapon switch, as in StepPlayer's trigger rule.
        if (armed && ((input.Buttons & InputButtons.Aim) != 0 || ((input.Buttons & InputButtons.Fire) != 0 && s.SwitchLeft <= 0f))) return false;
        return !s.Charging && s.PendingShot == 0 && s.RaiseTicks == 0;
    }

    /// <summary>Unit aim direction from yaw (0 faces +Y, as in movement) and pitch (positive up).</summary>
    public static Vector3 AimDirection(float yaw, float pitch)
    {
        float cp = MathF.Cos(pitch);
        return new Vector3(-MathF.Sin(yaw) * cp, MathF.Cos(yaw) * cp, MathF.Sin(pitch));
    }

    /// <summary>
    /// The eye: <see cref="SimConfig.EyeHeight"/> above the player's feet (placeholder height). Shots leave from here moved by
    /// the input's origin offset (<see cref="ShotOrigin"/>); the client sends that offset relative to this point.
    /// </summary>
    public static Vector3 EyePosition(PlayerState s, SimConfig c) => s.Position + new Vector3(0, 0, c.EyeHeight);

    /// <summary>
    /// Where a shot, projectile, thrown item or melta cone leaves from (protocol 12; rules ours, spec/behavior.md): the eye
    /// moved by the input's <see cref="PlayerInput.ShotOrigin"/>, shortened to <see cref="SimConfig.MaxShotOriginOffset"/>;
    /// the eye itself when level weapon collision (<paramref name="world"/>) lies on the way from the eye to that point.
    /// </summary>
    public static Vector3 ShotOrigin(PlayerState s, PlayerInput input, SimConfig c, CollisionWorld? world = null)
    {
        var eye = EyePosition(s, c);
        var offset = input.Sanitized().ShotOrigin;
        float length = offset.Length();
        if (length < 1e-6f) return eye;
        float max = float.IsFinite(c.MaxShotOriginOffset) ? MathF.Max(0f, c.MaxShotOriginOffset) : 0f;
        if (length > max) { offset *= max / length; length = max; }
        if (length < 1e-6f) return eye;
        if (world?.Weapons.Raycast(eye, offset / length, length) is { } wall && wall.Distance <= length) return eye;
        return eye + offset;
    }

    /// <summary>
    /// The shot's heading and elevation (radians) for an input (protocol 12): the view (Yaw, Pitch) turned by the input's
    /// shot offsets, kept within <see cref="SimConfig.ShotConeDegrees"/> of the view (a farther one is turned back onto the
    /// cone's edge, toward the requested side). Offsets of 0 give the view exactly. Rules ours (spec/behavior.md).
    /// </summary>
    public static (float Yaw, float Pitch) ShotAim(PlayerInput input, SimConfig c) => ShotAim(input, c, 0f, 0f);

    /// <summary>
    /// As <see cref="ShotAim(PlayerInput, SimConfig)"/> for the shot <paramref name="shooter"/> fired this tick: the aim turned by its
    /// scatter (<see cref="PlayerState.SpreadYaw"/>, <see cref="PlayerState.SpreadPitch"/>; weapons/spread, <see cref="Spread"/>) on top of
    /// the input's offsets, then kept within the cone of the view.
    /// </summary>
    public static (float Yaw, float Pitch) ShotAim(PlayerInput input, SimConfig c, PlayerState shooter) => ShotAim(input, c, shooter.SpreadYaw, shooter.SpreadPitch);

    /// <summary>As <see cref="ShotAim(PlayerInput, SimConfig)"/> with a scatter turn of <paramref name="across"/> and <paramref name="up"/> radians (<see cref="Spread.Turn"/>).</summary>
    public static (float Yaw, float Pitch) ShotAim(PlayerInput input, SimConfig c, float across, float up)
    {
        input = input.Sanitized();
        if (input.ShotYawOffset == 0f && input.ShotPitchOffset == 0f && across == 0f && up == 0f) return (input.Yaw, input.Pitch);
        var view = AimDirection(input.Yaw, input.Pitch);
        var wanted = Spread.Turn(AimDirection(input.Yaw + input.ShotYawOffset, Math.Clamp(input.Pitch + input.ShotPitchOffset, -MathF.PI / 2, MathF.PI / 2)),
            float.IsFinite(across) ? across : 0f, float.IsFinite(up) ? up : 0f);
        float max = Math.Clamp(float.IsFinite(c.ShotConeDegrees) ? c.ShotConeDegrees : 0f, 0f, 180f) * (MathF.PI / 180f);
        float cos = Math.Clamp(Vector3.Dot(view, wanted), -1f, 1f);
        var direction = wanted;
        if (MathF.Acos(cos) > max)
        {
            var side = wanted - view * cos;   // the part of the request across the view
            if (side.LengthSquared() < 1e-12f) return (input.Yaw, input.Pitch);   // straight behind: no side to turn to
            direction = view * MathF.Cos(max) + Vector3.Normalize(side) * MathF.Sin(max);
        }
        return (MathF.Atan2(-direction.X, direction.Y), MathF.Asin(Math.Clamp(direction.Z, -1f, 1f)));
    }

    /// <summary>The unit shot direction of <see cref="ShotAim"/>.</summary>
    public static Vector3 ShotDirection(PlayerInput input, SimConfig c)
    {
        var (yaw, pitch) = ShotAim(input, c);
        return AimDirection(yaw, pitch);
    }

    /// <summary>The unit shot direction of <see cref="ShotAim(PlayerInput, SimConfig, PlayerState)"/> (the shooter's scatter applied).</summary>
    public static Vector3 ShotDirection(PlayerInput input, SimConfig c, PlayerState shooter)
    {
        var (yaw, pitch) = ShotAim(input, c, shooter);
        return AimDirection(yaw, pitch);
    }

    /// <summary>Damage multiplier for a hit at this distance (×1 to FallOffStart, linear to the end multiplier, then constant).</summary>
    public static float FallOff(float distance, SimConfig c)
    {
        if (distance <= c.FallOffStart) return 1f;
        if (distance >= c.FallOffEnd || c.FallOffEnd <= c.FallOffStart) return c.FallOffEndMultiplier;
        float t = (distance - c.FallOffStart) / (c.FallOffEnd - c.FallOffStart);
        return 1f + (c.FallOffEndMultiplier - 1f) * t;
    }

    public static float DamageAt(float distance, SimConfig c) => c.Damage * FallOff(distance, c);

    /// <summary>As <see cref="FallOff(float, SimConfig)"/> for a weapon of a loadout.</summary>
    public static float FallOff(float distance, WeaponSpec w)
    {
        if (distance <= w.FallOffStart) return 1f;
        if (distance >= w.FallOffEnd || w.FallOffEnd <= w.FallOffStart) return w.FallOffEndMultiplier;
        float t = (distance - w.FallOffStart) / (w.FallOffEnd - w.FallOffStart);
        return 1f + (w.FallOffEndMultiplier - 1f) * t;
    }

    public static float DamageAt(float distance, WeaponSpec w) => w.Damage * FallOff(distance, w);

    /// <summary>
    /// Ray against a player's hit volumes standing at <paramref name="feet"/>: the install's ragdoll-derived volumes
    /// (<see cref="SimConfig.HitVolumes"/>, M20; this overload faces them along yaw 0) or the placeholder head sphere and
    /// vertical body capsule. Returns the nearer hit within range. A shot fired from inside a target's volumes (players do
    /// not collide yet) does not hit that target.
    /// </summary>
    public static (HitZone Zone, float Distance) HitTest(Vector3 origin, Vector3 direction, Vector3 feet, SimConfig c)
        => HitTest(origin, direction, feet, c, c.MaxRange);

    /// <summary>
    /// Whether a point is inside the space a player's hit volumes occupy: with <see cref="SimConfig.HitVolumes"/>, inside one
    /// of them (the player facing <paramref name="yaw"/>); with the placeholders, an upright cylinder of the body radius from
    /// BodyBottom to the top of the head.
    /// </summary>
    public static bool InsideHitVolumes(Vector3 point, Vector3 feet, SimConfig c, float yaw = 0f)
    {
        if (c.HitVolumes is { } volumes) return HitVolumes.Contains(volumes, point, feet, yaw) != HitZone.None;
        var local = point - feet;
        return local.X * local.X + local.Y * local.Y <= c.BodyRadius * c.BodyRadius && local.Z >= c.BodyBottom && local.Z <= c.HeadHeight + c.HeadRadius;
    }

    /// <summary>
    /// The hit shape a point is inside (the head sphere first, then the body capsule; the same shapes <see cref="HitTest"/>
    /// rays hit), or <see cref="HitZone.None"/>. Unlike <see cref="InsideHitVolumes"/> (the hit-scan exclusion, a larger
    /// upright cylinder), a point beside the head or over the shoulder is outside.
    /// </summary>
    public static HitZone InsideHitShapes(Vector3 point, Vector3 feet, SimConfig c, float yaw = 0f)
    {
        if (c.HitVolumes is { } volumes) return HitVolumes.Contains(volumes, point, feet, yaw);
        if (Vector3.DistanceSquared(point, feet + new Vector3(0, 0, c.HeadHeight)) <= c.HeadRadius * c.HeadRadius) return HitZone.Head;
        float low = c.BodyBottom + c.BodyRadius, high = MathF.Max(low + 1e-3f, c.BodyTop - c.BodyRadius);
        var local = point - feet;
        var axis = new Vector3(0, 0, Math.Clamp(local.Z, low, high));
        return Vector3.DistanceSquared(local, axis) <= c.BodyRadius * c.BodyRadius ? HitZone.Body : HitZone.None;
    }

    /// <summary>As above, within <paramref name="maxRange"/> (the weapon's).</summary>
    public static (HitZone Zone, float Distance) HitTest(Vector3 origin, Vector3 direction, Vector3 feet, SimConfig c, float maxRange, float yaw = 0f)
        => InsideHitVolumes(origin, feet, c, yaw) ? (HitZone.None, float.MaxValue) : HitShapes(origin, direction, feet, c, maxRange, yaw);

    /// <summary>
    /// A ray against the hit shapes (head sphere, body capsule) within <paramref name="maxRange"/>, without
    /// <see cref="HitTest"/>'s exclusion of origins inside the hit-scan cylinder (a ray starting inside a shape hits it at 0).
    /// </summary>
    public static (HitZone Zone, float Distance) HitShapes(Vector3 origin, Vector3 direction, Vector3 feet, SimConfig c, float maxRange, float yaw = 0f)
    {
        if (c.HitVolumes is { } volumes) return HitVolumes.Raycast(volumes, origin, direction, feet, yaw, maxRange);
        var zone = HitZone.None; float best = maxRange;
        if (RaySphere(origin, direction, feet + new Vector3(0, 0, c.HeadHeight), c.HeadRadius) is { } h && h <= best) { best = h; zone = HitZone.Head; }
        // BodyBottom and BodyTop are the capsule's outer extents, so the body ends where the head begins.
        float low = c.BodyBottom + c.BodyRadius, high = MathF.Max(low + 1e-3f, c.BodyTop - c.BodyRadius);
        if (RayCapsule(origin, direction, feet + new Vector3(0, 0, low), feet + new Vector3(0, 0, high), c.BodyRadius) is { } b && b < best) { best = b; zone = HitZone.Body; }
        return (zone, zone == HitZone.None ? float.MaxValue : best);
    }

    /// <summary>
    /// Shield first, at the shield multiplier for the zone; whatever the shield cannot absorb is converted back to raw
    /// damage and applied to health at the health multiplier. Health 0 kills.
    /// </summary>
    public static (PlayerState After, float ShieldDamage, float HealthDamage) ApplyDamage(PlayerState s, float damage, HitZone zone, SimConfig c) =>
        ApplyDamage(s, damage,
            zone == HitZone.Head ? c.ShieldHeadMultiplier : c.ShieldBodyMultiplier,
            zone == HitZone.Head ? c.HeadMultiplier : c.BodyMultiplier, c);

    /// <summary>As above with the multipliers of a loadout weapon.</summary>
    public static (PlayerState After, float ShieldDamage, float HealthDamage) ApplyDamage(PlayerState s, float damage, HitZone zone, WeaponSpec w, SimConfig c) =>
        ApplyDamage(s, damage,
            zone == HitZone.Head ? w.ShieldHeadMultiplier : w.ShieldBodyMultiplier,
            zone == HitZone.Head ? w.HeadMultiplier : w.BodyMultiplier, c);

    /// <summary>As above with explicit multipliers (melee weapons carry their own).</summary>
    public static (PlayerState After, float ShieldDamage, float HealthDamage) ApplyDamage(PlayerState s, float damage, float shieldMultiplier, float healthMultiplier, SimConfig c)
    {
        if (!s.Alive || damage <= 0f) return (s, 0f, 0f);
        float shieldDamage = 0f, raw = damage;
        if (s.Shield > 0f && shieldMultiplier > 0f)
        {
            shieldDamage = MathF.Min(s.Shield, raw * shieldMultiplier);
            raw -= shieldDamage / shieldMultiplier;
        }
        float healthDamage = MathF.Max(0f, raw) * healthMultiplier;
        float health = s.Health - healthDamage;
        var after = s with { Shield = s.Shield - shieldDamage, Health = MathF.Max(0f, health), SinceDamage = 0f };
        if (health <= 0f) after = Killed(after, c);
        return (after, shieldDamage, healthDamage);
    }

    static float? RaySphere(Vector3 o, Vector3 d, Vector3 center, float r)
    {
        var m = o - center;
        float b = Vector3.Dot(m, d), cc = Vector3.Dot(m, m) - r * r;
        if (cc > 0f && b > 0f) return null;
        float disc = b * b - cc;
        if (disc < 0f) return null;
        return MathF.Max(0f, -b - MathF.Sqrt(disc));
    }

    /// Ray against a capsule (segment a–b, radius r): nearest distance along the ray, or null.
    static float? RayCapsule(Vector3 o, Vector3 d, Vector3 a, Vector3 b, float r)
    {
        // Sample the closest approach between the ray and the segment, then refine with the end spheres and the cylinder.
        float? best = null;
        void Take(float? t) { if (t is { } v && (best is null || v < best)) best = v; }
        Take(RaySphere(o, d, a, r));
        Take(RaySphere(o, d, b, r));
        var axis = b - a; float len = axis.Length(); var u = axis / len;
        // Cylinder: project onto the plane perpendicular to the axis.
        var dp = d - Vector3.Dot(d, u) * u;
        var op = (o - a) - Vector3.Dot(o - a, u) * u;
        float A = Vector3.Dot(dp, dp), B = Vector3.Dot(op, dp), C = Vector3.Dot(op, op) - r * r;
        if (A > 1e-9f)
        {
            float disc = B * B - A * C;
            if (disc >= 0f)
            {
                float t = (-B - MathF.Sqrt(disc)) / A;
                if (t < 0f && C <= 0f) t = 0f;   // starts inside
                if (t >= 0f)
                {
                    float along = Vector3.Dot(o + d * t - a, u);
                    if (along >= 0f && along <= len) Take(t);
                }
            }
        }
        return best;
    }
}
