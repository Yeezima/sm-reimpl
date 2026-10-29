using System.Numerics;

namespace SmSim;

/// <summary>
/// Where a player is in a jump-pack jump (<see cref="PlayerState.Jump"/>). Phases follow the wargear's <c>jump_info</c>
/// (ascending, apex hang, descending, smash) and the behavior graph's take-off, landing and ground-pound sequencers.
/// </summary>
public enum JumpPhase : byte
{
    /// <summary>Not jumping (also every player without a jump pack).</summary>
    None = 0,
    /// <summary>On the ground between the press and the lift-off (<see cref="JumpPackSpec.TakeOffDelay"/>).</summary>
    TakeOff = 1,
    /// <summary>Rising under <see cref="JumpPackSpec.JumpingGravity"/> until the vertical speed reaches 0 (the apex).</summary>
    Ascent = 2,
    /// <summary>After the apex, <see cref="JumpPackSpec.HangTime"/> under <see cref="JumpPackSpec.HangGravity"/>.</summary>
    Hang = 3,
    /// <summary>Falling under <see cref="JumpPackSpec.FallingGravity"/> until landing.</summary>
    Fall = 4,
    /// <summary>The ground pound's dive (the wargear's smash) until it reaches the ground.</summary>
    Dive = 5,
    /// <summary>Landed from a jump: movement and a new jump wait for the landing sequencer's branches.</summary>
    Landed = 6,
    /// <summary>Recovering from a ground pound's impact: nothing but turning until <see cref="JumpPackSpec.PoundRecovery"/>.</summary>
    Recover = 7,
}

/// <summary>What a player's jump did this tick (for the world: blasts, logs).</summary>
/// <remarks>
/// <see cref="SlashLand"/> (protocol 30): a sword slash's dive reached the ground (<see cref="SlashSpec"/>); the world applies the regular landing's
/// blast for the drop (ours: as any landing today; the data's smash-landing table, <c>action_land_smash_by_height</c>, has no blast), not the pound's.
/// </remarks>
public enum JumpEventKind : byte { None = 0, Launch = 1, Dive = 2, Land = 3, Impact = 4, SlashLand = 5 }

/// <summary>
/// feel/jump-pack-2: a take-off started by a long buffered Jump press (<see cref="BufferedAction.JumpLong"/>): <see cref="Released"/> takes off committed to the
/// small jump, <see cref="Held"/> committed to at least the small jump and still holding (the hold can reach the full jump).
/// </summary>
public enum LongJump : byte { None = 0, Released = 1, Held = 2 }

/// <summary>A regular landing's blast (<c>action_land_regular_by_height</c>): applies when the drop from the apex is at least <see cref="MinDrop"/> m.</summary>
public sealed record JumpLanding(float MinDrop, SplashSpec Blast);

/// <summary>A reaction the ground pound's blast triggers on a target hit at or above <see cref="Intensity"/> (by name; see <see cref="Reaction.KindOf"/>).</summary>
public readonly record struct JumpReaction(float Intensity, string Reaction);

/// <summary>
/// A jump pack (wargear <c>sm_mp_abil_jumppack</c> / <c>csm_mp_abil_jumppack</c>, <c>ability_jumppack/jump_info</c> default
/// entry; read by <c>SmContent.JumpPackTuning</c>). Units as in the file: metres (assumed), seconds (assumed), metres per
/// second (squared) for speeds and gravities (assumed); power in the file's units. Simulated by <see cref="JumpPack"/>.
/// </summary>
/// <param name="HeightMap"><c>jump_height_map</c> (hold time, height) points with height &gt; 0, ascending time.</param>
/// <param name="PowerCost"><c>power_settings/power_used</c> (hold time, power cost) points, ascending time.</param>
/// <param name="MaxPower"><c>power_settings/max_power</c>.</param>
/// <param name="RechargeRate"><c>power_settings/recharge_rate</c> (power per second).</param>
/// <param name="ForwardSpeed"><c>jump_forward_speed</c>: horizontal speed at lift-off for a full stick (interpretation).</param>
/// <param name="MoveGain"><c>in_air_move_gain</c>: fraction of the gap to the wished air velocity closed per 1/30 s with the stick pushed (interpretation).</param>
/// <param name="DriftGain"><c>in_air_drift_gain</c>: the same with the stick released (interpretation).</param>
public sealed record JumpPackSpec(string Name, IReadOnlyList<DistancePoint> HeightMap, IReadOnlyList<DistancePoint> PowerCost,
    float MaxPower, float RechargeRate, float JumpingGravity, float HangGravity, float HangTime, float FallingGravity,
    float ForwardSpeed, float MoveGain, float DriftGain)
{
    /// <summary><c>smash_can_be_used</c> and an <c>ability_groundpound</c>: a melee press in flight starts the ground pound.</summary>
    public bool PoundEnabled { get; init; }
    /// <summary><c>smash_available_delay</c>: seconds after the lift-off before a pound may start (interpretation of the reference point).</summary>
    public float PoundDelay { get; init; }
    /// <summary><c>smash_gravity</c>: the dive's gravity.</summary>
    public float PoundGravity { get; init; }
    /// <summary><c>smash_initial_speed_boost</c>: the dive's speed at its start, along the dive direction (interpretation).</summary>
    public float PoundSpeed { get; init; }
    /// <summary><c>smash_limit_angle</c> (degrees): most the dive direction may lean away from straight down (interpretation).</summary>
    public float PoundLimitAngle { get; init; }
    /// <summary>The impact's blast (<c>actn_gp_hit</c> default entry's <c>area_of_effect</c>), or null.</summary>
    public SplashSpec? Pound { get; init; }
    /// <summary>The impact blast's reactions by intensity (<c>reaction_mp_stun</c> etc.), as named in the file; applied since M28 when <see cref="Pound"/> carries no <see cref="SplashSpec.Reactions"/>.</summary>
    public IReadOnlyList<JumpReaction> PoundReactions { get; init; } = [];
    /// <summary>Regular landings' blasts by drop height, ascending <see cref="JumpLanding.MinDrop"/>.</summary>
    public IReadOnlyList<JumpLanding> Landings { get; init; } = [];
    /// <summary>Protocol 30: the dash attack (a melee press during the dash's flight; <see cref="DashAttackSpec"/>), or null.</summary>
    public DashAttackSpec? DashAttack { get; init; }
    /// <summary>Protocol 30: the sword slash (a melee press in a jump below <see cref="SlashSpec.MinPoundHeight"/>; <see cref="SlashSpec"/>), or null: every press in flight pounds.</summary>
    public SlashSpec? Slash { get; init; }

    // ---- timing from the behavior graph MP_SM_NAV_JP_Sim (placeholders here, read at runtime) ----
    /// <summary>Seconds from the press to the lift-off (take-off sequencer's <c>ACTN_Take_Off</c>; 0.2 s on the install).</summary>
    public float TakeOffDelay { get; init; } = 0.2f;
    /// <summary>After a landing with the stick pushed: movement returns (run landing's <c>BRANCH_Run</c>; 0.333 s).</summary>
    public float LandMoveLock { get; init; } = 1f / 3f;
    /// <summary>After a landing with the stick pushed: a new jump may start (run landing's <c>BRANCH_Jump_To_Jump</c>; 0.132 s).</summary>
    public float LandJumpLock { get; init; } = 0.132f;
    /// <summary>After a landing with the stick released: movement returns (idle landing's <c>BRANCH_Run</c>; 0.462 s).</summary>
    public float LandIdleMoveLock { get; init; } = 0.4615f;
    /// <summary>After a landing with the stick released: a new jump may start (idle landing's <c>BRANCH_Jump_To_Jump</c>; 0.277 s).</summary>
    public float LandIdleJumpLock { get; init; } = 0.277f;
    /// <summary>Seconds from a pound's impact until the player acts again (impact sequencer's <c>BRANCH_to_Getup</c>; 0.356 s).</summary>
    public float PoundRecovery { get; init; } = 0.356f;

    // ---- M45: the middle jump (the owner's knowledge of the original: "let go of Space mid-jump to stop rising") ----
    /// <summary>
    /// M45: letting go of Jump while rising ends the rise (<see cref="JumpPack"/>): false keeps the M14 rules (the hold sets the
    /// apex, a release after it changes nothing). Not in the data; set by <c>SmContent.JumpPackTuning</c> for every pack read
    /// (user's knowledge). Off by default so synthetic specs test the data's hold map alone.
    /// </summary>
    public bool ReleaseCut { get; init; }
    /// <summary>M45: seconds from the release (at the earliest the lift-off) during which the rise goes on unchanged before it decays (ours).</summary>
    public float ReleaseCutDelay { get; init; } = 0.1f;
    /// <summary>M45: time constant (s) of the rise's decay after the delay: the vertical speed is multiplied by exp(−dt / this) each tick before gravity (ours; 0: stops at once).</summary>
    public float ReleaseCutDecay { get; init; } = 0.05f;

    // ---- feel/jump-pack-2: three jumps by the hold (the owner's decision of 2026-10-29: "revert to the old jump behavior. space bar tap makes a hop
    // dash, space bar medium hold makes a small jump, and space bar long hold is a full jump") ----
    /// <summary>
    /// feel/jump-pack-2 (owner's decision; tiers ours): with a dash (<see cref="JumpPack.Begin"/>'s <c>dashAt</c>) the hold picks one of three jumps
    /// (<see cref="JumpPack"/>): Jump let go by the dash's decision tick is the dash; still down then and let go before the hold reaches
    /// <see cref="MaxHold"/> is the small jump (always the apex of the hold at the decision tick: the M44 shortest jump); still down when the hold
    /// reaches <see cref="MaxHold"/> is the full jump. Replaces the M45 middle jump (<see cref="ReleaseCut"/> is then not used for dash jumps). Not in
    /// the data; set by <c>SmContent.JumpPackTuning</c>. Off by default so synthetic specs test the data's hold map alone.
    /// </summary>
    public bool HoldTiers { get; init; }

    // ---- feel/jump-pack: air control (the owner's request of 2026-10-29: "should be able to somewhat move around in the air with WASD") ----
    /// <summary>
    /// Air control's top speed (m/s) toward the stick in the ascent, hang and fall (<see cref="JumpPack.AirSteer"/>): with the stick pushed the
    /// horizontal velocity moves toward stick × max(this, its speed) by at most <see cref="AirAcceleration"/> × dt a tick, so pushing never brakes a
    /// faster flight in that direction and the speed never grows past max(this, the speed before). 0 (the default, synthetic specs): the data's
    /// <see cref="MoveGain"/> rule alone. Not in the data (its gains stay the rule with the stick released); set by <c>SmContent.JumpPackTuning</c>.
    /// </summary>
    public float AirSpeed { get; init; }
    /// <summary>Air control's acceleration (m/s²; see <see cref="AirSpeed"/>).</summary>
    public float AirAcceleration { get; init; }

    /// <summary>Longest hold that still raises the jump: the last height point's time (0.4 s on the install).</summary>
    public float MaxHold => HeightMap.Count > 0 ? HeightMap[^1].Distance : 0f;
    /// <summary>The apex height above the lift-off for a hold of <paramref name="hold"/> s: linear between points, clamped at the ends (interpretation).</summary>
    public float HeightAt(float hold) => Combat.PointValue(HeightMap, hold);
    /// <summary>The power a hold of <paramref name="hold"/> s costs in all: linear between points, clamped at the ends (interpretation).</summary>
    public float CostAt(float hold) => Combat.PointValue(PowerCost, hold);

    /// <summary>Writes every field for a config hash.</summary>
    internal void Write(BinaryWriter w)
    {
        w.Write(Name);
        ProjectileSpec.WritePoints(w, HeightMap); ProjectileSpec.WritePoints(w, PowerCost);
        foreach (float f in new[] { MaxPower, RechargeRate, JumpingGravity, HangGravity, HangTime, FallingGravity, ForwardSpeed, MoveGain, DriftGain,
            PoundDelay, PoundGravity, PoundSpeed, PoundLimitAngle, TakeOffDelay, LandMoveLock, LandJumpLock, LandIdleMoveLock, LandIdleJumpLock, PoundRecovery })
            w.Write(f);
        w.Write(PoundEnabled);
        // M45: only with the cut on, so packs without it (synthetic configs) hash as before.
        if (ReleaseCut) { w.Write("release-cut"); w.Write(ReleaseCutDelay); w.Write(ReleaseCutDecay); }
        // feel/jump-pack-2: only with the tiers on, so packs without them (synthetic configs) hash as before.
        if (HoldTiers) w.Write("hold-tiers");
        // feel/jump-pack: only with air control on, so packs without it (synthetic configs) hash as before.
        if (AirSpeed > 0f) { w.Write("air-control"); w.Write(AirSpeed); w.Write(AirAcceleration); }
        // Protocol 30: only when read, so packs without them (synthetic configs) hash as before.
        if (DashAttack is not null) { w.Write("dash-attack"); DashAttack.Write(w); }
        if (Slash is not null) { w.Write("slash"); Slash.Write(w); }
        ProjectileSpec.WriteSplash(w, Pound);
        w.Write(PoundReactions.Count); foreach (var r in PoundReactions) { w.Write(r.Intensity); w.Write(r.Reaction); }
        w.Write(Landings.Count); foreach (var l in Landings) { w.Write(l.MinDrop); ProjectileSpec.WriteSplash(w, l.Blast); }
        w.Write(TakeOffBlasts.Count); foreach (var b in TakeOffBlasts) ProjectileSpec.WriteSplash(w, b);
        SmSim.Variants.Write(w, Variants, (b, v) => v.Write(b));
    }

    /// <summary>
    /// Blasts at the lift-off (the wargear's <c>actn_take_off</c>, M43): the file has them only in the entry that needs the Blast Off
    /// perk (<c>mp_perk_jump_pack_take_off_damage</c>), so the base pack has none and the perk's variant has them.
    /// </summary>
    public IReadOnlyList<SplashSpec> TakeOffBlasts { get; init; } = [];

    /// <summary>The pack as perks change it (M43: Air-Cooled Thrusters' jump_info, Death From Above's pound, Blast Off's take-off); see <see cref="WeaponSpec.Variants"/>.</summary>
    public IReadOnlyList<UpgradeVariant<JumpPackSpec>> Variants { get; init; } = [];

    /// <summary>The pack for a player with <paramref name="upgrades"/>.</summary>
    public JumpPackSpec With(PerkUpgrades upgrades) => SmSim.Variants.Pick(this, Variants, upgrades);
}

/// <summary>
/// Jump-pack rules for one player's tick, shared by the server and client prediction (pure; integer tick counters so the
/// state round-trips exactly). Values are the wargear's; how they combine is an **interpretation** (spec/behavior.md):
/// <para>
/// A Jump press edge on the ground (phase None, or Landed once its jump lock has passed), with no melee attack in progress
/// or starting, and with at least <see cref="JumpPackSpec.CostAt"/>(0) power, starts the take-off and spends that cost. The
/// hold time counts while the button stays down, up to <see cref="JumpPackSpec.MaxHold"/>; each held tick spends the extra
/// cost of the longer hold (when the power does not cover it, the hold stops growing). At
/// <see cref="JumpPackSpec.TakeOffDelay"/> after the press the player lifts off (when still on the ground; else the jump becomes a fall): vertical speed √(2 g H) for the hold's
/// height H (g = JumpingGravity) and horizontal velocity stick × ForwardSpeed in the view's frame (straight up without
/// stick). While the hold still grows in the ascent, the vertical speed rises to √(v² + 2 g ΔH). The apex (vertical speed
/// ≤ 0, or a ceiling) starts the hang (HangGravity on the HangTime / dt ticks after the apex tick), then the fall (FallingGravity). In the air the horizontal
/// velocity closes MoveGain (stick pushed) or DriftGain (released) of the gap to stick × ForwardSpeed per 1/30 s. Landing
/// (map collision or the flat ground) ends the jump: movement input is ignored until the landing's move lock and a new
/// jump waits for its jump lock (the run landing's values when the stick is pushed at landing, else the idle one's).
/// </para>
/// <para>
/// M45, the middle jump (<see cref="JumpPackSpec.ReleaseCut"/>; user's knowledge, timing ours): Jump up on a tick in the ascent (from the
/// lift-off tick on; a release during the take-off counts at the lift-off) cuts the rise, latched until the apex
/// (<see cref="PlayerState.JumpCut"/>, the mark becomes that tick): the hold stops growing at once, the rise goes on under JumpingGravity
/// for <see cref="JumpPackSpec.ReleaseCutDelay"/>, then each tick the vertical speed is multiplied by exp(−dt / ReleaseCutDecay) before
/// gravity until the apex; the hang and fall follow as usual. Held to the apex, the jump is the M14 jump unchanged.
/// </para>
/// <para>
/// feel/jump-pack-2, three jumps by the hold (<see cref="JumpPackSpec.HoldTiers"/>, with a dash; the owner's decision of 2026-10-29, the tiers'
/// boundaries and the cut's shape ours): with the press as tick 0 and d the dash's decision tick (5 on the install), Jump let go by tick d is the
/// dash (M44); still down on tick d the jump commits; let go after that while the hold still grows (before it reaches
/// <see cref="JumpPackSpec.MaxHold"/>, tick 12) it is the <b>small jump</b>: a release in the take-off keeps the hold at d's (the M44 shortest jump,
/// apex <see cref="JumpPackSpec.HeightAt"/>(d × dt)), a release in the ascent cuts the rise (<see cref="PlayerState.JumpCut"/>, the mark becomes the
/// release tick) and from that tick the ascent's gravity is <see cref="TierCutGravity"/>, the constant deceleration that tops the rise out at the
/// small jump's apex exactly (on the free arc), so every medium hold peaks at the same height; still down when the hold reaches MaxHold it is the
/// <b>full jump</b> (M14 unchanged; a release after that changes nothing). Power: a small jump costs the small hold's cost (what a longer hold spent
/// is given back at the release), the full jump the full hold's.
/// </para>
/// <para>
/// Ground pound: a Melee press edge in the ascent, hang or fall, at least PoundDelay after the lift-off, starts the dive:
/// velocity PoundSpeed along the aim, leaned at most PoundLimitAngle from straight down, then PoundGravity, no air control.
/// Reaching the ground is the impact (<see cref="JumpEventKind.Impact"/>; the world applies the blast): the player stops and
/// does nothing but turn for PoundRecovery; starting a dive cancels a throw in progress. Power recharges at RechargeRate outside a jump (phases None — also airborne after walking off a ledge —, Landed and Recover).
/// During a jump melee presses never start an attack; during the dive and the recovery the weapon, switch and throw wait.
/// </para>
/// </summary>
public static partial class JumpPack
{
    const float Eps = 1e-5f;

    /// <summary>Phases with the pack in use before landing (the slim record's jumping flag).</summary>
    public static bool Jumping(JumpPhase p) => p is JumpPhase.TakeOff or JumpPhase.Ascent or JumpPhase.Hang or JumpPhase.Fall;
    /// <summary>The ground pound's dive and recovery (the slim record's pound flag).</summary>
    public static bool Pounding(JumpPhase p) => p is JumpPhase.Dive or JumpPhase.Recover;
    /// <summary>In the air on the pack's own arc (where a melee press starts a pound).</summary>
    public static bool InFlight(JumpPhase p) => p is JumpPhase.Ascent or JumpPhase.Hang or JumpPhase.Fall;
    /// <summary>The weapon, switch and throw wait (dive and recovery).</summary>
    public static bool Busy(JumpPhase p) => p is JumpPhase.Dive or JumpPhase.Recover;

    /// <summary>Ticks are counted up to this (u16 on the wire).</summary>
    public const int MaxTicks = ushort.MaxValue;

    /// <summary>The first part of a tick: timers, power, hold, take-off start, lift-off and pound start (before the weapon).</summary>
    internal readonly record struct Tick(JumpPhase Phase, int Ticks, int Mark, float Power, bool Holding, bool LandMoving,
        float OldHold, float NewHold, bool Launch, bool PoundStart, bool Started, bool DashStart = false, bool Cut = false, bool Slash = false, bool Buffered = false,
        float CutGravity = 0f)
    {
        /// <summary>Melee presses do not start an attack (in a jump, the pound's included).</summary>
        public bool BlocksMelee => Phase is not (JumpPhase.None or JumpPhase.Landed);
        public bool Busy => JumpPack.Busy(Phase);
    }

    /// <param name="dashAt">
    /// M44: the class's dash decision time (<see cref="EvadeSpec.ToJumpAt"/>), or null without a dash (then the rules above, unchanged).
    /// With a dash a press starts the take-off without spending or needing power; released before the decision tick (the first whose
    /// time reaches min(dashAt, TakeOffDelay)) the take-off ends and <see cref="Tick.DashStart"/> asks the evade for the dash (owner's
    /// request: the small hop is the dash; the data's <c>BRANCH_Evade_to_Jump</c> with the "holding evade" condition); still held then,
    /// the jump commits and spends the cost of the hold so far (a zero hold's when short; with less than that the press is a dash).
    /// </param>
    /// <param name="aboveGround">
    /// Protocol 30: the feet's height above the ground at this tick's start (<see cref="AboveGround"/>): a melee press in flight below the slash's
    /// <see cref="SlashSpec.MinPoundHeight"/> starts the slash's dive instead of the pound's (<see cref="Slashes"/>; the owner's answer: decided at
    /// the press). Infinity (the default): the pound.
    /// </param>
    /// <param name="longJump">
    /// feel/jump-pack-2: a take-off this tick by a long buffered press (<see cref="BufferedAction.JumpLong"/>, with the three jumps): committed at once to the
    /// small jump (its hold and cost; the dash's decision is skipped: <c>mark</c> is the decision tick, ahead of the tick count), still growing toward the full
    /// jump while Jump stays down (<see cref="LongJump.Held"/>). With too little power for the small jump it starts as a normal press.
    /// </param>
    internal static Tick Begin(PlayerState s, JumpPackSpec? pack, bool jumpDown, bool jumpPress, bool meleePress, bool meleeBusy, SimConfig c, float? dashAt = null,
        float aboveGround = float.PositiveInfinity, LongJump longJump = LongJump.None)
    {
        if (pack is null) return default;
        float dt = c.TickSeconds;
        float T(int n) => n * dt;
        var phase = s.Jump;
        int ticks = s.JumpTicks, mark = s.JumpMark;
        float power = s.JumpPower;
        bool holding = s.JumpHolding, landMoving = s.JumpLandMoving;
        if (phase != JumpPhase.None) ticks = Math.Min(ticks + 1, MaxTicks);
        if (phase == JumpPhase.Landed && T(ticks) + Eps >= MathF.Max(MoveLock(pack, landMoving, c), JumpLock(pack, landMoving, c)))
        { phase = JumpPhase.None; ticks = 0; landMoving = false; }
        else if (phase == JumpPhase.Recover && T(ticks) + Eps >= (s.JumpSlash && pack.Slash is { } recovering ? recovering.Recovery : pack.PoundRecovery)) { phase = JumpPhase.None; ticks = 0; }
        if (phase is JumpPhase.None or JumpPhase.Landed or JumpPhase.Recover) power = MathF.Min(pack.MaxPower, power + pack.RechargeRate * dt);
        // The hang covers the ticks after the apex tick: HangTime / dt of them (15 at 30 Hz) under HangGravity, then the fall.
        if (phase == JumpPhase.Hang && T(ticks - mark - 1) + Eps >= pack.HangTime) phase = JumpPhase.Fall;

        bool started = false, dashStart = false, decided = false;
        // feel/jump-pack-2: the three jumps by the hold (JumpPackSpec.HoldTiers): d is the dash's decision tick, its hold the small jump's.
        bool tiers = pack.HoldTiers && dashAt is not null;
        int decision = tiers ? DecisionTick(pack, dashAt!.Value, dt) : 0;
        bool canStart = phase == JumpPhase.None || (phase == JumpPhase.Landed && T(ticks) + Eps >= JumpLock(pack, landMoving, c));
        if (canStart && jumpPress && s.Grounded && !meleeBusy && !meleePress && (dashAt is not null || power + Eps >= pack.CostAt(0f)))
        {
            phase = JumpPhase.TakeOff; ticks = 0; mark = 0; landMoving = false; started = true;
            if (dashAt is null) power = MathF.Max(0f, power - pack.CostAt(0f));   // with a dash: spent when the jump commits (below)
            holding = pack.MaxHold > Eps;
            // feel/jump-pack-2: a long buffered press commits at once (mark ahead of the ticks: the decision below is skipped).
            if (tiers && longJump != LongJump.None && decision > 0 && power + Eps >= pack.CostAt(T(decision)))
            {
                power = MathF.Max(0f, power - pack.CostAt(T(decision)));
                mark = decision;
                holding = longJump == LongJump.Held && T(decision) + Eps < pack.MaxHold;
            }
        }

        float oldHold = MathF.Min(T(mark), pack.MaxHold);
        // M44: the dash's decision. Before the decision tick nothing is spent and the hold grows freely; a release then is the dash.
        if (dashAt is float dash && phase == JumpPhase.TakeOff && !started && mark < ticks && T(ticks - 1) + Eps < MathF.Min(dash, pack.TakeOffDelay))
        {
            decided = true;
            if (!jumpDown) { phase = JumpPhase.None; ticks = 0; mark = 0; holding = false; dashStart = true; }
            else
            {
                if (holding) { mark = ticks; if (T(ticks) + Eps >= pack.MaxHold) holding = false; }
                if (T(ticks) + Eps >= MathF.Min(dash, pack.TakeOffDelay))
                {
                    float hold = MathF.Min(T(mark), pack.MaxHold), cost = pack.CostAt(hold);
                    if (power + Eps >= cost) power = MathF.Max(0f, power - cost);
                    else if (power + Eps >= pack.CostAt(0f)) { power = MathF.Max(0f, power - pack.CostAt(0f)); mark = 0; holding = false; }
                    else { phase = JumpPhase.None; ticks = 0; mark = 0; holding = false; dashStart = true; }   // too little power to jump: the dash
                }
            }
        }
        bool tierCut = false;
        if (!decided && holding && phase is JumpPhase.TakeOff or JumpPhase.Ascent && !started)
        {
            if (!jumpDown)
            {
                holding = false;
                if (tiers && T(mark) > T(decision) + Eps)
                {
                    // A medium hold: the small jump. What the longer hold spent comes back; in the take-off the hold falls back to the
                    // decision tick's (the lift-off then launches the small jump), in the ascent the rise is cut (below).
                    power = MathF.Min(pack.MaxPower, power + MathF.Max(0f, pack.CostAt(MathF.Min(T(mark), pack.MaxHold)) - pack.CostAt(T(decision))));
                    if (phase == JumpPhase.TakeOff) mark = decision;
                    else tierCut = true;
                }
            }
            else if (ticks > mark)   // feel/jump-pack-2: a long buffered press's hold starts at the decision tick's (the mark is ahead until then)
            {
                float hold = MathF.Min(T(ticks), pack.MaxHold);
                float extra = MathF.Max(0f, pack.CostAt(hold) - pack.CostAt(oldHold));
                if (extra > power + Eps) holding = false;
                else { power = MathF.Max(0f, power - extra); mark = ticks; }
                if (T(ticks) + Eps >= pack.MaxHold) holding = false;
            }
        }
        else if (!(phase is JumpPhase.TakeOff or JumpPhase.Ascent)) holding = false;
        float newHold = phase is JumpPhase.TakeOff or JumpPhase.Ascent ? MathF.Min(T(mark), pack.MaxHold) : 0f;

        bool launch = false;
        if (phase == JumpPhase.TakeOff && T(ticks) + Eps >= pack.TakeOffDelay)
        {
            // Lift-off needs the ground: a player who left it during the take-off (walked off a ledge) just falls (power stays spent).
            if (s.Grounded) { phase = JumpPhase.Ascent; launch = true; }
            else { phase = JumpPhase.Fall; holding = false; mark = ticks; }
        }

        // M45 (user's knowledge): Jump up in the ascent (from the lift-off tick on) cuts the rise, latched until the apex; the mark
        // becomes the tick the cut's delay counts from (the hold no longer grows: newHold above already has the last held tick's).
        // feel/jump-pack-2: with the tiers the cut is the small jump's (a medium release in the ascent, above), latched the same way; M45's rule is not used.
        bool cut = (pack.ReleaseCut || tiers) && phase == JumpPhase.Ascent && s.JumpCut && !launch;
        if (tiers) { if (tierCut && phase == JumpPhase.Ascent && !cut) { cut = true; mark = ticks; } }
        else if (pack.ReleaseCut && phase == JumpPhase.Ascent && !cut && !jumpDown) { cut = true; holding = false; mark = ticks; }

        bool pound = false, slash = s.JumpSlash && phase is JumpPhase.Dive or JumpPhase.Recover;
        // Protocol 30: below the slash's height the press is the sword slash (the same dive; ours), from it up the pound.
        // feel/jump-pack (ours): with the slash's feel rules a melee press edge made in the take-off (which swallows it) or on the lift-off tick is
        // buffered (PlayerState.JumpSlash in those phases) and starts the slash on the tick after the lift-off, held or not; a button already down
        // at the jump's press is no press. A slash does not wait for the pound's delay (smash_available_delay is the smash's).
        bool snappy = pack.Slash is { Snappy: true };
        bool had = s.JumpSlash && s.Jump == JumpPhase.TakeOff;
        bool buffered = snappy && !started && (phase == JumpPhase.TakeOff || launch) && (had || meleePress);
        bool heldOver = snappy && s.JumpSlash && s.Jump == JumpPhase.Ascent && phase == JumpPhase.Ascent && T(s.JumpTicks) - pack.TakeOffDelay < dt - Eps;
        if ((meleePress || heldOver) && InFlight(phase) && !launch)
        {
            bool slashes = Slashes(pack, aboveGround);
            bool delayed = T(ticks) - pack.TakeOffDelay + Eps >= pack.PoundDelay;
            if ((slashes && (snappy || delayed) && (meleePress || heldOver)) || (!slashes && pack.PoundEnabled && meleePress && delayed))
            {
                phase = JumpPhase.Dive; holding = false; pound = true; cut = false; slash = slashes;
                if (slashes && snappy) mark = ticks;   // the slash's own dive counts its glide from here
            }
        }
        float cutGravity = tiers && cut && phase == JumpPhase.Ascent ? TierCutGravity(pack, c, decision, mark) : 0f;
        return new Tick(phase, ticks, mark, power, holding, landMoving, oldHold, newHold, launch, pound, started, dashStart, cut, slash, buffered, cutGravity);
    }

    /// <summary>feel/jump-pack-2: the dash's decision tick (the first whose time reaches min(<paramref name="dashAt"/>, the take-off delay); 5 on the install).</summary>
    public static int DecisionTick(JumpPackSpec pack, float dashAt, float dt) => FirstTickReaching(MathF.Min(dashAt, pack.TakeOffDelay), dt);

    /// <summary>
    /// The first tick n (from 0) whose time n × dt reaches <paramref name="time"/> by <see cref="Begin"/>'s own comparison (the same float product and its
    /// 1e-5 s tolerance), so the tier ticks are the ticks the simulation decides on.
    /// </summary>
    static int FirstTickReaching(float time, float dt)
    {
        if (!(dt > 0f) || !float.IsFinite(time)) return 0;
        int n = 0;
        while (n < MaxTicks && n * dt + Eps < time) n++;
        return n;
    }

    /// <summary>feel/jump-pack-2: the tick of the lift-off (the first whose time reaches the take-off delay; 6 on the install).</summary>
    public static int LiftOffTick(JumpPackSpec pack, float dt) => FirstTickReaching(pack.TakeOffDelay, dt);

    /// <summary>
    /// feel/jump-pack-2: the ticks from the press (tick 0) through which Jump must stay down for each jump with <see cref="JumpPackSpec.HoldTiers"/>:
    /// the dash's decision tick (down on it: a jump; let go by it: the dash) and the full hold's tick (down on it: the full jump; let go after the
    /// decision tick and by it: the small jump). On the install 5 and 12.
    /// </summary>
    public static (int Jump, int Full) TierTicks(JumpPackSpec pack, float dashAt, float dt) =>
        (DecisionTick(pack, dashAt, dt), Math.Max(DecisionTick(pack, dashAt, dt) + 1, FirstTickReaching(pack.MaxHold, dt)));

    /// <summary>
    /// feel/jump-pack-2: the vertical arc of a jump on its own (no level, ceiling or power limit), as <see cref="Begin"/> and <see cref="Move"/> step it:
    /// Jump down on ticks 0 .. <paramref name="lastHeld"/>, launched at the lift-off with the hold so far, the hold growing on each held tick after it
    /// (up to MaxHold), each tick's speed then the jumping gravity. Returns the height above the lift-off and the vertical speed after
    /// tick <paramref name="tick"/> (nothing before the lift-off).
    /// </summary>
    static (float Height, float Speed) FreeArc(JumpPackSpec pack, SimConfig c, int lastHeld, int tick)
    {
        float dt = c.TickSeconds, g = MathF.Max(pack.JumpingGravity, 0f);
        int lift = LiftOffTick(pack, dt);
        float h = 0f, v = 0f;
        float Hold(int k) => MathF.Min(Math.Max(0, k) * dt, pack.MaxHold);
        for (int k = lift; k <= tick; k++)
        {
            if (k == lift) v = MathF.Sqrt(2f * g * MathF.Max(pack.HeightAt(Hold(Math.Min(lastHeld, lift))), 0f));
            else if (k <= lastHeld && v > 0f && Hold(k) > Hold(k - 1))
            {
                float more = pack.HeightAt(Hold(k)) - pack.HeightAt(Hold(k - 1));
                if (more > 0f) v = MathF.Sqrt(v * v + 2f * g * more);
            }
            v -= g * dt;
            h += v * dt;
        }
        return (h, v);
    }

    /// <summary>The highest point from (<paramref name="h"/>, <paramref name="v"/>) under a constant <paramref name="gravity"/>, stepped as <see cref="Movement.Fly"/> (speed, then position) to the apex tick.</summary>
    static float ApexFrom(float h, float v, float gravity, float dt)
    {
        float top = h;
        for (int k = 0; k < 10000 && v > 0f; k++) { v -= gravity * dt; h += v * dt; top = MathF.Max(top, h); }
        return top;
    }

    /// <summary>
    /// feel/jump-pack-2: the ascent's gravity after a medium release on tick <paramref name="releaseTick"/> (Jump last down on the tick before), so
    /// that the rise tops out at the small jump's apex (the free arc of a jump let go right after the decision tick <paramref name="decisionTick"/>):
    /// found by bisection on the free arcs (<see cref="FreeArc"/>), at least the jumping gravity. Deterministic (fixed steps), so prediction agrees.
    /// </summary>
    public static float TierCutGravity(JumpPackSpec pack, SimConfig c, int decisionTick, int releaseTick)
    {
        float dt = c.TickSeconds, g = MathF.Max(pack.JumpingGravity, 1e-3f);
        int lift = LiftOffTick(pack, dt);
        var small = FreeArc(pack, c, decisionTick, lift);
        float target = ApexFrom(small.Height, small.Speed, g, dt);
        var (h, v) = FreeArc(pack, c, releaseTick - 1, Math.Max(lift, releaseTick - 1));
        if (ApexFrom(h, v, g, dt) <= target + 1e-5f) return g;
        float lo = g, hi = g * 256f;
        if (ApexFrom(h, v, hi, dt) > target) return hi;
        for (int i = 0; i < 40; i++)
        {
            float mid = 0.5f * (lo + hi);
            if (ApexFrom(h, v, mid, dt) > target) lo = mid; else hi = mid;
        }
        return hi;
    }

    /// <summary>
    /// A regular landing's blast for a drop of <paramref name="drop"/> m from the apex: the <see cref="JumpPackSpec.Landings"/> entry with the
    /// largest MinDrop not above the drop (1e-4 m tolerance), or null.
    /// </summary>
    public static SplashSpec? LandingBlast(JumpPackSpec pack, float drop)
    {
        SplashSpec? blast = null;
        foreach (var l in pack.Landings) if (l.MinDrop <= drop + 1e-4f) blast = l.Blast;
        return blast;
    }

    /// <summary>
    /// M45: ticks from the press (tick 0) through which Jump must stay down for the full jump when a release cuts the rise
    /// (<see cref="JumpPackSpec.ReleaseCut"/>): the lift-off plus the full hold's rise time √(2 H / g), rounded up, and 2 ticks of margin
    /// (49 on the install's values; the apex tick is 47). Holding longer changes nothing. For scripts (bots), not the simulation.
    /// </summary>
    public static int FullRiseTicks(JumpPackSpec pack, SimConfig c)
    {
        float g = MathF.Max(pack.JumpingGravity, 1e-3f), height = MathF.Max(pack.HeightAt(pack.MaxHold), 0f);
        float seconds = pack.TakeOffDelay + MathF.Sqrt(2f * height / g);
        return (int)MathF.Ceiling(seconds / c.TickSeconds - 1e-3f) + 2;
    }

    /// <summary>The landing's move lock for a landing with (<paramref name="moving"/>) or without the stick pushed.</summary>
    /// <remarks>feel/jump-pack-2 (the owner's request, an estimate): with <paramref name="c"/>, × its lock scale (<see cref="SimConfig.LockTime"/>; the
    /// install's 0.7: run landing 0.333 → 0.233 s, idle 0.462 → 0.323 s).</remarks>
    public static float MoveLock(JumpPackSpec pack, bool moving, SimConfig? c = null)
    {
        float data = moving ? pack.LandMoveLock : pack.LandIdleMoveLock;
        return c is null ? data : c.LockTime(data);
    }
    /// <summary>The landing's jump lock.</summary>
    /// <remarks>feel/jump-pack-2: with <paramref name="c"/>, × its lock scale (the install's 0.7: run landing 0.132 → 0.093 s, idle 0.277 → 0.194 s).</remarks>
    public static float JumpLock(JumpPackSpec pack, bool moving, SimConfig? c = null)
    {
        float data = moving ? pack.LandJumpLock : pack.LandIdleJumpLock;
        return c is null ? data : c.LockTime(data);
    }

    /// <summary>The wished air velocity: the stick (at most unit length) × ForwardSpeed in the view's frame.</summary>
    static Vector2 Wish(PlayerInput input, float speed)
    {
        var forward = new Vector2(-MathF.Sin(input.Yaw), MathF.Cos(input.Yaw));
        var right = new Vector2(forward.Y, -forward.X);
        var stick = new Vector2(input.MoveX, input.MoveY);
        if (stick.LengthSquared() > 1f) stick = Vector2.Normalize(stick);
        return (right * stick.X + forward * stick.Y) * speed;
    }

    /// <summary>
    /// feel/jump-pack, air control (ours; the owner's request of 2026-10-29): one tick of steering with the stick pushed. Target velocity: the stick
    /// direction in the view's frame (the stick is clamped to unit length, so W+A asks for the same speed as W) times max(<see cref="JumpPackSpec.AirSpeed"/>,
    /// the current horizontal speed). The horizontal velocity moves toward the target by at most <see cref="JumpPackSpec.AirAcceleration"/> × dt.
    /// So a stick across a fast flight turns it without slowing it much, a stick against it brakes it and then pushes back to AirSpeed, and the speed
    /// never exceeds max(AirSpeed, the speed before) (no strafe gain). Horizontal only: no height is gained.
    /// </summary>
    public static Vector2 AirSteer(Vector2 horizontal, PlayerInput input, JumpPackSpec pack, float dt)
    {
        var target = Wish(input, MathF.Max(pack.AirSpeed, horizontal.Length()));
        var gap = target - horizontal;
        float step = pack.AirAcceleration * dt, length = gap.Length();
        return length <= step || length < 1e-6f ? target : horizontal + gap * (step / length);
    }

    /// <summary>The slash's landing lunge heading: the given horizontal velocity's direction, or the view's heading when it has none.</summary>
    static Vector2 LungeHeading(Vector2 horizontal, float yaw) =>
        horizontal.LengthSquared() > 1e-4f ? Vector2.Normalize(horizontal) : new Vector2(-MathF.Sin(yaw), MathF.Cos(yaw));

    /// <summary>The dive's unit direction: the aim, leaned at most <paramref name="limitDegrees"/> away from straight down.</summary>
    public static Vector3 DiveDirection(float yaw, float pitch, float limitDegrees)
    {
        var aim = Combat.AimDirection(yaw, pitch);
        float limit = Math.Clamp(limitDegrees, 0f, 180f) * (MathF.PI / 180f);
        float fromDown = MathF.Acos(Math.Clamp(-aim.Z, -1f, 1f));
        if (fromDown <= limit + 1e-6f) return aim;
        var flat = new Vector2(aim.X, aim.Y);
        flat = flat.LengthSquared() > 1e-10f ? Vector2.Normalize(flat) : new Vector2(-MathF.Sin(yaw), MathF.Cos(yaw));
        return new Vector3(flat * MathF.Sin(limit), -MathF.Cos(limit));
    }

    /// <summary>
    /// M44: whether a melee press in the player's next tick would start a ground pound (<see cref="Begin"/>'s rule: the pack has one,
    /// the player is in the ascent, hang or fall, and <see cref="JumpPackSpec.PoundDelay"/> has passed since the lift-off by then).
    /// Reactions, charges and death are not looked at (callers draw only for a living player).
    /// </summary>
    public static bool CanPound(PlayerState s, JumpPackSpec pack, SimConfig c) =>
        pack.PoundEnabled && s.Alive && InFlight(s.Jump) && Math.Min(s.JumpTicks + 1, MaxTicks) * c.TickSeconds - pack.TakeOffDelay + Eps >= pack.PoundDelay;

    /// <summary>
    /// Protocol 30: <see cref="CanPound(PlayerState, JumpPackSpec, SimConfig)"/> and the press would be the pound, not the sword slash (the feet at least
    /// <see cref="SlashSpec.MinPoundHeight"/> above the ground of <paramref name="world"/>; the client's pound target marker shows only then).
    /// </summary>
    public static bool CanPound(PlayerState s, JumpPackSpec pack, SimConfig c, CollisionWorld? world) =>
        CanPound(s, pack, c) && !Slashes(pack, AboveGround(s, world));

    /// <summary>
    /// M44: where a ground pound started in the player's next tick would land (the impact point, feet), aimed at
    /// <paramref name="yaw"/> / <paramref name="pitch"/>: the simulation's own dive (<see cref="Move"/>: the start velocity
    /// <see cref="DiveDirection"/> × <see cref="JumpPackSpec.PoundSpeed"/>, then <see cref="JumpPackSpec.PoundGravity"/>, no air control)
    /// stepped through <see cref="Movement.Fly"/> on <paramref name="world"/> (or the flat ground) until it is grounded, so it is the
    /// impact the server would find with nothing else in the way (other players and later input are not foreseen). Null when it does
    /// not land within <paramref name="maxTicks"/>, leaves the finite numbers, or crosses a kill region (no impact there).
    /// </summary>
    public static Vector3? PoundLanding(PlayerState s, float yaw, float pitch, JumpPackSpec pack, SimConfig c, CollisionWorld? world, int maxTicks = 300)
    {
        var dive = DiveDirection(yaw, pitch, pack.PoundLimitAngle) * pack.PoundSpeed;
        var horizontal = new Vector2(dive.X, dive.Y);
        float vz = dive.Z;
        var p = s with { Grounded = false };
        for (int k = 0; k < maxTicks; k++)
        {
            var from = p.Position;
            p = Movement.Fly(p, yaw, horizontal, vz, pack.PoundGravity, c, world);
            if (!float.IsFinite(p.Position.X) || !float.IsFinite(p.Position.Y) || !float.IsFinite(p.Position.Z)) return null;
            if (world is not null && world.KillsPlayer(from, p.Position, c.CapsuleHeight)) return null;
            if (p.Grounded) return p.Position;
            horizontal = new Vector2(p.Velocity.X, p.Velocity.Y);
            vz = p.Velocity.Z;
        }
        return null;
    }

    /// <summary>
    /// The second part: movement for the tick and the transitions it causes (apex, landing, impact). <paramref name="moveInput"/>
    /// is the input movement uses (the melee and charge rules applied); the jump's own locks are applied here.
    /// </summary>
    internal static PlayerState Move(PlayerState s, PlayerInput moveInput, SimConfig c, JumpPackSpec pack, CollisionWorld? world, float speedScale,
        Tick t, out JumpEventKind happened, bool aiming = false)
    {
        happened = t.Launch ? JumpEventKind.Launch : t.PoundStart ? JumpEventKind.Dive : JumpEventKind.None;
        float dt = c.TickSeconds;
        var phase = t.Phase;
        int ticks = t.Ticks, mark = t.Mark;
        bool holding = t.Holding, landMoving = t.LandMoving;
        var input = moveInput.Sanitized();
        PlayerState next;
        if (phase is JumpPhase.Ascent or JumpPhase.Hang or JumpPhase.Fall or JumpPhase.Dive)
        {
            var horizontal = new Vector2(s.Velocity.X, s.Velocity.Y);
            float vz = s.Velocity.Z;
            var wish = Wish(input, pack.ForwardSpeed);
            if (t.Launch)
            {
                vz = MathF.Sqrt(2f * MathF.Max(pack.JumpingGravity, 0f) * MathF.Max(pack.HeightAt(t.NewHold), 0f));
                horizontal = wish;
            }
            else if (phase == JumpPhase.Ascent && vz > 0f && t.NewHold > t.OldHold && !t.Cut)
            {
                float more = pack.HeightAt(t.NewHold) - pack.HeightAt(t.OldHold);
                if (more > 0f) vz = MathF.Sqrt(vz * vz + 2f * MathF.Max(pack.JumpingGravity, 0f) * more);
            }
            // M45: a cut rise decays from ReleaseCutDelay after the cut's mark (the release, at the earliest the lift-off) to the apex.
            // feel/jump-pack-2: a tiered cut has its own gravity instead (t.CutGravity, below).
            if (t.Cut && t.CutGravity <= 0f && phase == JumpPhase.Ascent && vz > 0f && (ticks - mark) * dt + Eps >= pack.ReleaseCutDelay)
                vz *= pack.ReleaseCutDecay > 0f ? MathF.Exp(-dt / pack.ReleaseCutDecay) : 0f;
            var snappy = t.Slash && pack.Slash is { Snappy: true } own ? own : null;
            if (t.PoundStart)
            {
                // feel/jump-pack: the slash's own dive, a straight line toward the point under the aim (SlashDirection); the pound's otherwise.
                var dive = snappy is not null ? SlashDirection(s, input.Yaw, input.Pitch, snappy, c, world) * snappy.DiveSpeed
                    : DiveDirection(input.Yaw, input.Pitch, pack.PoundLimitAngle) * pack.PoundSpeed;
                horizontal = new Vector2(dive.X, dive.Y); vz = dive.Z;
            }
            else if (phase != JumpPhase.Dive && !t.Launch)
            {
                bool pushed = input.MoveX != 0f || input.MoveY != 0f;
                if (pushed && pack.AirSpeed > 0f && pack.AirAcceleration > 0f) horizontal = AirSteer(horizontal, input, pack, dt);   // feel/jump-pack
                else
                {
                    float gain = Math.Clamp(pushed ? pack.MoveGain : pack.DriftGain, 0f, 1f);
                    float k = 1f - MathF.Pow(1f - gain, 30f * dt);   // per 1/30 s in the file (assumed)
                    horizontal += (wish - horizontal) * k;
                }
            }
            float gravity = phase switch
            {
                JumpPhase.Ascent => t.Cut && t.CutGravity > 0f ? t.CutGravity : pack.JumpingGravity,
                JumpPhase.Hang => pack.HangGravity,
                JumpPhase.Fall => pack.FallingGravity,
                // feel/jump-pack: the slash's dive is straight (no gravity) until it has flown its reach, then the pound's gravity.
                _ when snappy is not null => (ticks - mark) * dt + Eps < snappy.DiveReach / snappy.DiveSpeed ? 0f : pack.PoundGravity,
                _ => pack.PoundGravity,
            };
            next = Movement.Fly(s, input.Yaw, horizontal, vz, gravity, c, world);
            if (next.Grounded)
            {
                if (phase == JumpPhase.Dive)
                {
                    phase = JumpPhase.Recover; happened = t.Slash ? JumpEventKind.SlashLand : JumpEventKind.Impact;
                    // feel/jump-pack (the owner: "when air slash landing you should also move forward a bit"): the slash lunges on along the dive's heading.
                    next = next with { Velocity = snappy is { LungeSpeed: > 0f } ? new Vector3(LungeHeading(horizontal, input.Yaw) * snappy.LungeSpeed, 0f) : Vector3.Zero };
                }
                else { phase = JumpPhase.Landed; landMoving = input.MoveX != 0f || input.MoveY != 0f; happened = happened == JumpEventKind.None ? JumpEventKind.Land : happened; }
                ticks = 0; mark = 0; holding = false;
            }
            else if (phase == JumpPhase.Ascent && next.Velocity.Z <= 0f) { phase = JumpPhase.Hang; mark = ticks; holding = false; }
        }
        else
        {
            bool locked = phase == JumpPhase.Recover || (phase == JumpPhase.Landed && ticks * dt + Eps < MoveLock(pack, landMoving, c));
            var walk = input with { Buttons = input.Buttons & ~InputButtons.Jump };
            if (locked) walk = walk with { MoveX = 0f, MoveY = 0f, Buttons = walk.Buttons & ~InputButtons.Sprint };
            // feel/jump-pack: the slash's landing lunge, for LungeTime from the impact (the impact tick counts 0), then the ground motor stops the body.
            // Only on the ground: lunging off a ledge, the walk-off rule takes the body (Movement.Step).
            if (phase == JumpPhase.Recover && s.JumpSlash && s.Grounded && pack.Slash is { Snappy: true, LungeSpeed: > 0f } lunge && ticks * dt <= lunge.LungeTime + Eps)
                next = Movement.Glide(s, input.Yaw, LungeHeading(new Vector2(s.Velocity.X, s.Velocity.Y), input.Yaw) * lunge.LungeSpeed, c, world);
            else next = Movement.Step(s, walk, c, world, speedScale, aiming);
        }
        if (phase is JumpPhase.None) { ticks = 0; mark = 0; }
        if (phase is JumpPhase.Landed or JumpPhase.Recover) mark = 0;
        return next with
        {
            Jump = phase, JumpTicks = ticks, JumpMark = mark, JumpPower = t.Power, JumpHolding = holding, JumpLandMoving = phase == JumpPhase.Landed && landMoving,
            JumpCut = t.Cut && phase == JumpPhase.Ascent,
            JumpSlash = t.Slash && phase is JumpPhase.Dive or JumpPhase.Recover || t.Buffered && phase is JumpPhase.TakeOff or JumpPhase.Ascent,
        };
    }
}
