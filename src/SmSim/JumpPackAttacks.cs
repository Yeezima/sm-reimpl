using System.Numerics;

namespace SmSim;

/// <summary>
/// The Assault and Raptor dash attack (protocol 30; the owner's description, 2026-10-28: "while doing a short ground hop by tapping
/// space bar, can right click to extend his hop into a running charge"): the jump-pack wargear's <c>ability_dash_attack</c> and the
/// marine rig's <c>SM_Base_MP</c> "Jump Dash Attack" state, read by <c>SmContent.JumpPackTuning</c>. Rules in <see cref="Rush"/>
/// (phases <see cref="RushPhase.DashAttack"/> to <see cref="RushPhase.DashCollision"/>).
/// </summary>
/// <param name="Charge">
/// The lunge as a charge: its <see cref="RushSpec.Rush"/> is the <c>SM_JMP_DashAtk_F</c> clip up to its <c>BRANCH_toIdle</c> (no input ends it
/// before), its <see cref="RushSpec.Collision"/> the knocked-back stop (<c>SM_NAV_Bullrush2Idle_F_Collision_01</c> on a wall), its hits the
/// ability's (75 + 25 shield, 30 to others in reach; the owner's stun on the primary target), its spacing the ability's 1 s, no hyper armour.
/// </param>
/// <param name="Hit">The hit state after connecting with a player (<c>SM_MEL_PA_XATK_Combo3FIN</c>; <c>BOOL_Dash_Hit</c>).</param>
/// <param name="Stop">The run-out after the lunge (<c>SM_NAV_Sprint_to_Idle_F</c>, the "Dash to Idle" state on <c>BRANCH_toIdle</c>).</param>
public sealed record DashAttackSpec(RushSpec Charge, RushMove Hit, RushMove Stop)
{
    /// <summary>
    /// Highest the feet may be above the ground at the press (metres; the graph's <c>FLT_Distance_Above_Ground in [0, 3]</c> on the
    /// "Jumppack Evade" state's <c>BTN_Melee_Attack</c> transition). Measured down from the feet (<see cref="JumpPack.AboveGround"/>).
    /// </summary>
    public float MaxHeight { get; init; } = 3f;

    internal void Write(BinaryWriter w)
    {
        Charge.Write(w); Hit.Write(w); Stop.Write(w);
        w.Write(MaxHeight);
    }
}

/// <summary>
/// The jump pack's sword slash (protocol 30; the owner's description, 2026-10-28: "a smaller 'sword slash' variant [of the ground pound]
/// where he does a quicker animation and quickly slashes in a cone in front of him with his sword ... does not stun, only knocks the enemy
/// off balance"): the graph <c>MP_SM_NAV_JP_Sim</c>'s "MeleeDash" state, which a melee press in a jump takes below
/// <see cref="MinPoundHeight"/> above the ground (the ground pound from there up), and the wargear's <c>ability_melee_dash</c>: its primary
/// actions are empty, so every target in the cone takes the secondary profile (60 melee, no shield part, the light interrupt; ours,
/// the report's recommendation). Simulated by <see cref="JumpPack"/> (the dive and the recovery) and the world (the cone).
/// </summary>
/// <param name="Ability">The wargear ability (<c>ability_melee_dash</c>).</param>
/// <param name="Animation">The impact clip (<c>SM_MEL_CS_XATK_Hv1_LS_2_7m</c>, also the flight's; the client poses it).</param>
/// <param name="ClipLength">The clip's length in seconds (for the client).</param>
/// <param name="HitTime">Seconds from the impact to the hit (the impact sequencer's <c>ACTN_Hit_Targets_Multiplayer</c>; 0.133 s).</param>
/// <param name="Recovery">Seconds from the impact until the player acts again (the impact sequencer's <c>BRANCH_Run</c>; 0.638 s; ours by analogy with the pound's get-up).</param>
/// <param name="Damage">What every target in the cone takes (the ability's secondary <c>do_damage</c>).</param>
/// <param name="Reaction">… and its reaction (<c>reaction_mp_melee</c>: the light interrupt).</param>
/// <param name="Range">The ability's <c>max_range</c> (4 m; the cone is the world's melee cone, <see cref="SimConfig.MeleeRange"/>).</param>
public sealed record SlashSpec(string Ability, string Animation, float ClipLength, float HitTime, float Recovery, DamageSpec Damage, ReactionEntry Reaction, float Range)
{
    /// <summary>The health a hit gives back to the attacker (the heal-on-hit perks' entries of the ability), 0 without one.</summary>
    public float Heal { get; init; }
    /// <summary>
    /// The height above the ground (metres) from which a melee press in a jump is the ground pound; below it is the slash (the graph's
    /// <c>FLT_Distance_Above_Ground in [8, 1000]</c>; the wargear's <c>action_land_smash_by_height</c> has entries at 0 and 8). Decided at the
    /// press (the owner's answer, 2026-10-28; the graph also switches between the two in flight when the height crosses 8 m: not simulated).
    /// </summary>
    public float MinPoundHeight { get; init; } = 8f;

    // ---- feel/jump-pack (the owner's request of 2026-10-29: "the air slash feels terrible to use"; "when air slash landing you should also move
    // forward a bit"). Defaults: off, the protocol 30 rules (the pound's dive, a dead stop, the pound's delay, a press in the take-off lost). Set by
    // SmContent.JumpPackAttackTuning; every value ours.
    /// <summary>
    /// The slash's own dive (m/s; 0: the pound's dive): a straight line at this speed (no gravity for <see cref="DiveReach"/> / this seconds, then the
    /// pound's) from the feet toward the point under the aim (<see cref="JumpPack.SlashDirection"/>), at least <see cref="DiveMinDown"/> below level.
    /// With it on, a slash press does not wait for the pound's <c>smash_available_delay</c>, and a melee press edge in the take-off or on the lift-off
    /// tick (which swallow presses) is buffered and starts the slash on the tick after the lift-off (<see cref="PlayerState.JumpSlash"/>).
    /// </summary>
    public float DiveSpeed { get; init; }
    /// <summary>Least angle (degrees) the slash's dive points below level (40: the data's 50° smash limit from straight down).</summary>
    public float DiveMinDown { get; init; } = 40f;
    /// <summary>How far (m) the aim ray looks for the point the dive goes to; past it the dive follows the aim (clamped).</summary>
    public float DiveReach { get; init; } = 10f;
    /// <summary>The forward lunge on landing (m/s; 0: a dead stop): the body glides on along the dive's heading at this speed for <see cref="LungeTime"/>, then the ground motor stops it.</summary>
    public float LungeSpeed { get; init; }
    /// <summary>Seconds of the landing lunge (from the impact).</summary>
    public float LungeTime { get; init; }

    // ---- feel/jump-pack-2 (client presentation; the graph's clip generators, read by SmContent.JumpPackAttackTuning): where the clip plays ----
    /// <summary>
    /// The clip time (s) the impact sequencer starts <see cref="Animation"/> at (its clip generator's <c>cropStartAmountLocalTime</c>; 0.4667 on the
    /// install): the landing plays the clip from here, the overhead strike (the hand comes down 0.55 to 0.65 s into the clip). 0: from the start.
    /// </summary>
    public float ImpactClipStart { get; init; }
    /// <summary>
    /// The clip time (s) the flight sequencer plays <see cref="Animation"/> up to (its length less the generator's <c>cropEndAmountLocalTime</c>; 0.4667 on the
    /// install): the dive shows the clip's wind-up from 0 to here. 0: not read (the dive holds the first frame).
    /// </summary>
    public float FlightClipEnd { get; init; }

    /// <summary>The slash's hit as a melee step for the world's cone (<c>World.ResolveMelee</c>): every target the same profile.</summary>
    public MeleeStep Step => new(MathF.Max(Recovery, HitTime), HitTime, 0f, 0f, Recovery, Recovery, Damage.Damage, Damage.Damage, 0f, Damage.BodyMultiplier, Damage.ShieldBodyMultiplier)
    {
        PrimaryReaction = Reaction, SecondaryReaction = Reaction, Heal = Heal, SecondaryHeal = Heal,
        DamageType = Damage.Type == DamageType.Unknown ? DamageType.Melee : Damage.Type,
        ShieldDamage = Damage.ShieldDamage, SecondaryShieldDamage = Damage.ShieldDamage,
    };

    internal void Write(BinaryWriter w)
    {
        w.Write(Ability); w.Write(Animation);
        foreach (float f in new[] { ClipLength, HitTime, Recovery, Range, Heal, MinPoundHeight }) w.Write(f);
        DamageSpec.Write(w, Damage); Reaction.Write(w);
        // feel/jump-pack: only with the slash's own dive, so slashes without it (synthetic configs) hash as before.
        if (DiveSpeed > 0f) { w.Write("slash-feel"); foreach (float f in new[] { DiveSpeed, DiveMinDown, DiveReach, LungeSpeed, LungeTime }) w.Write(f); }
        // feel/jump-pack-2: only when read, so slashes without them (synthetic configs) hash as before.
        if (ImpactClipStart != 0f || FlightClipEnd != 0f) { w.Write("slash-clip"); w.Write(ImpactClipStart); w.Write(FlightClipEnd); }
    }

    /// <summary>The feel rules are on (<see cref="DiveSpeed"/>).</summary>
    public bool Snappy => DiveSpeed > 0f;
}

public static partial class JumpPack
{
    /// <summary>How far above the feet the ground probe starts (metres; ours: so feet standing on the ground find it at 0).</summary>
    const float ProbeLift = 0.5f;
    /// <summary>Farthest the ground probe looks down (metres; ours: past the pound's 8 m and any drop on the alpha map).</summary>
    public const float ProbeReach = 60f;

    /// <summary>
    /// The feet's height above the ground below them (metres; the graphs' <c>FLT_Distance_Above_Ground</c>, interpretation): a ray straight
    /// down from the feet against the level's character collision (<see cref="CollisionWorld.Character"/>), at most <see cref="ProbeReach"/>;
    /// infinity with nothing below. Without a level, the height above the flat ground at z = 0. Deterministic (prediction agrees).
    /// </summary>
    public static float AboveGround(PlayerState s, CollisionWorld? world)
    {
        if (world is null) return MathF.Max(0f, s.Position.Z);
        var from = s.Position + new Vector3(0f, 0f, ProbeLift);
        return world.Character.Raycast(from, -Vector3.UnitZ, ProbeReach + ProbeLift) is { } hit ? MathF.Max(0f, hit.Distance - ProbeLift) : float.PositiveInfinity;
    }

    /// <summary>
    /// feel/jump-pack (ours): the unit direction of the slash's own dive (<see cref="SlashSpec.DiveSpeed"/>). The aim ray from the eye (yaw, pitch) is cast
    /// against the level's character collision (without a level: the flat ground z = 0) up to <see cref="SlashSpec.DiveReach"/>; the dive heads from the
    /// feet to the point it meets, so a straight dive lands where the crosshair is (its line clear). Nothing met: along the aim. Either way leaned to at
    /// least <see cref="SlashSpec.DiveMinDown"/> below level (aiming level or up dives at that angle along the view's heading). Deterministic.
    /// </summary>
    public static Vector3 SlashDirection(PlayerState s, float yaw, float pitch, SlashSpec slash, SimConfig c, CollisionWorld? world)
    {
        var aim = Combat.AimDirection(yaw, pitch);
        var eye = Combat.EyePosition(s, c);
        float reach = MathF.Max(0f, slash.DiveReach);
        float? along = null;
        if (world is not null) { if (world.Character.Raycast(eye, aim, reach) is { } hit) along = hit.Distance; }
        else if (aim.Z < -1e-4f && -eye.Z / aim.Z <= reach) along = MathF.Max(0f, -eye.Z / aim.Z);
        var dir = aim;
        if (along is float d)
        {
            var to = eye + aim * d - s.Position;
            if (to.LengthSquared() > 1e-4f) dir = Vector3.Normalize(to);
        }
        var flat = new Vector2(dir.X, dir.Y);
        float flatLength = flat.Length();
        float minDown = Math.Clamp(slash.DiveMinDown, 0f, 90f) * (MathF.PI / 180f);
        // Below the least angle (or level, or up): the least angle along the heading (the view's when the direction has none).
        if (-dir.Z < MathF.Sin(minDown) * MathF.Sqrt(dir.Z * dir.Z + flatLength * flatLength) - 1e-6f)
        {
            flat = flatLength > 1e-5f ? flat / flatLength : new Vector2(-MathF.Sin(yaw), MathF.Cos(yaw));
            dir = new Vector3(flat * MathF.Cos(minDown), -MathF.Sin(minDown));
        }
        return Vector3.Normalize(dir);
    }

    /// <summary>Whether a melee press in a jump at <paramref name="aboveGround"/> metres is the slash rather than the ground pound (<see cref="SlashSpec.MinPoundHeight"/>).</summary>
    public static bool Slashes(JumpPackSpec pack, float aboveGround) => pack.Slash is { } slash && !(aboveGround >= slash.MinPoundHeight);

    /// <summary>
    /// Whether the player's slash hit lands in the tick it just played (<paramref name="next"/>, after the tick): in the slash's recovery, the
    /// tick whose interval ((ticks − 1) × dt, ticks × dt] since the impact (the impact tick counting 0) holds <see cref="SlashSpec.HitTime"/>.
    /// </summary>
    public static bool SlashHits(PlayerState next, JumpPackSpec? pack, SimConfig c)
    {
        if (!next.JumpSlash || next.Jump != JumpPhase.Recover || pack?.Slash is not { } slash) return false;
        float dt = c.TickSeconds, time = next.JumpTicks * dt;
        return time - dt + Eps < slash.HitTime && time + Eps >= slash.HitTime;
    }
}
