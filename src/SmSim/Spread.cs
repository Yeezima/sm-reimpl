using System.Numerics;

namespace SmSim;

/// <summary>
/// One <c>climb_sway_scatter</c> tuning set's scatter (wargear <c>kick_back_and_scatter_values</c> names the set per stance; values as
/// in <c>data/packages/climb_sway_scatter.oppc</c>). Angles in degrees and rates in degrees per second (**interpretation**: the file gives
/// no units; degrees fit the sizes, e.g. the bolter's 0.25 initial and 1.5 maximum), delays in seconds.
/// <see cref="InitialHorizontal"/>/<see cref="InitialVertical"/>: the half-widths of the scatter of a rested weapon; each shot adds
/// <see cref="IncreasePerShot"/> to both, up to <see cref="MaxHorizontal"/>/<see cref="MaxVertical"/>; <see cref="DecreaseDelay"/> s after
/// the last shot the added part falls by <see cref="DecreasePerSecond"/> (rules: <see cref="Spread"/>).
/// </summary>
public sealed record ScatterSet(float InitialHorizontal, float InitialVertical, float IncreasePerShot, float MaxHorizontal, float MaxVertical,
    float DecreasePerSecond, float DecreaseDelay)
{
    /// <summary>No scatter at all (a set the data does not name).</summary>
    public static readonly ScatterSet None = new(0f, 0f, 0f, 0f, 0f, 0f, 0f);

    /// <summary>The largest bloom (degrees added to the initial half-widths) this set can reach.</summary>
    public float MaxBloom => MathF.Max(0f, MathF.Max(MaxHorizontal - InitialHorizontal, MaxVertical - InitialVertical));

    internal void Write(BinaryWriter w)
    {
        w.Write(InitialHorizontal); w.Write(InitialVertical); w.Write(IncreasePerShot); w.Write(MaxHorizontal); w.Write(MaxVertical);
        w.Write(DecreasePerSecond); w.Write(DecreaseDelay);
    }
}

/// <summary>
/// One tuning set's <c>climb</c> and <c>sway</c> blocks (the "handling": the view kick per shot), as stored (degrees and degrees per
/// second assumed; delays in seconds). The simulation does not use them (the view is the client's); the client applies them to its own
/// camera (<c>client/NetworkView.Spread.cs</c>, <c>HandlingFrame</c>; **interpretation**).
/// </summary>
public sealed record HandlingSet(
    float PermanentClimbPerShot, float TemporaryClimbPerShot, float MaxTemporaryClimb, float ClimbRate, float ClimbRecoveryRate,
    float MinTemporarySwayPerShot, float MaxTemporarySwayPerShot, float SwayRate, float SwayRecoveryRate, float MaxTemporarySway,
    float MinPermanentSwayPerShot, float MaxPermanentSwayPerShot, float TemporarySwayIncreasePerShot, float TemporarySwayDecreasePerSecond,
    float TemporarySwayDecreaseDelay)
{
    /// <summary>No kick at all.</summary>
    public static readonly HandlingSet None = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    internal void Write(BinaryWriter w)
    {
        foreach (float v in new[] { PermanentClimbPerShot, TemporaryClimbPerShot, MaxTemporaryClimb, ClimbRate, ClimbRecoveryRate,
            MinTemporarySwayPerShot, MaxTemporarySwayPerShot, SwayRate, SwayRecoveryRate, MaxTemporarySway, MinPermanentSwayPerShot,
            MaxPermanentSwayPerShot, TemporarySwayIncreasePerShot, TemporarySwayDecreasePerSecond, TemporarySwayDecreaseDelay }) w.Write(v);
    }
}

/// <summary>A named <c>climb_sway_scatter</c> set: its scatter and its handling.</summary>
public sealed record SpreadSet(string Name, ScatterSet Scatter, HandlingSet Handling)
{
    internal void Write(BinaryWriter w) { w.Write(Name); Scatter.Write(w); Handling.Write(w); }
}

/// <summary>The stance a shot is fired in (wargear <c>kick_back_and_scatter_values</c>: <c>standing</c> = hip fire, <c>aiming</c> = aim mode; <c>crouching</c> has no MP use).</summary>
public enum SpreadStance : byte { Standing = 0, Aiming = 1 }

/// <summary>The movement a shot is fired in (<c>idle</c>, <c>walking</c>, <c>running</c>; thresholds: <see cref="Spread.MovementOf"/>).</summary>
public enum SpreadMovement : byte { Idle = 0, Walking = 1, Running = 2 }

/// <summary>
/// The sets of one firing ability by stance and movement (6: standing idle / walking / running, aiming idle / walking / running); a
/// null slot uses the nearest named one (<see cref="SetFor"/>).
/// </summary>
public sealed record SpreadStances(IReadOnlyList<SpreadSet?> Sets)
{
    /// <summary>The set for a stance and movement: that slot, else the stance's idle slot, else the standing slot of that movement, else standing idle; null when the ability names none.</summary>
    public SpreadSet? SetFor(SpreadStance stance, SpreadMovement movement)
    {
        SpreadSet? At(SpreadStance s, SpreadMovement m) { int i = (int)s * 3 + (int)m; return i < Sets.Count ? Sets[i] : null; }
        return At(stance, movement) ?? At(stance, SpreadMovement.Idle) ?? At(SpreadStance.Standing, movement) ?? At(SpreadStance.Standing, SpreadMovement.Idle);
    }

    internal void Write(BinaryWriter w)
    {
        w.Write(Sets.Count);
        foreach (var s in Sets) { w.Write(s is not null); s?.Write(w); }
    }
}

/// <summary>
/// A weapon's spread (weapons/spread, owner's decision 2026-10-28, reversing M27's "hip = aimed"): the basic shot's sets and, per charge
/// stage (index 0 = stage 1, as <see cref="ChargeSpec.Stages"/>), the charged shot's (null: that stage uses <see cref="Basic"/>). Read by
/// <c>SmContent.SpreadTuning</c>.
/// </summary>
public sealed record SpreadSpec(SpreadStances Basic, IReadOnlyList<SpreadStances?> Stages)
{
    /// <summary>The sets a shot of <paramref name="stage"/> (0 = the basic shot) uses.</summary>
    public SpreadStances For(int stage) => stage > 0 && stage - 1 < Stages.Count && Stages[stage - 1] is { } s ? s : Basic;

    /// <summary>Writes the spread for a config hash; nothing for null, so a config without spread hashes as before weapons/spread.</summary>
    internal static void Write(BinaryWriter w, SpreadSpec? s)
    {
        if (s is null) return;
        w.Write("weapons/spread"); w.Write(Spread.RulesVersion);
        s.Basic.Write(w);
        w.Write(s.Stages.Count);
        foreach (var st in s.Stages) { w.Write(st is not null); st?.Write(w); }
    }
}

/// <summary>
/// Weapon scatter (weapons/spread; the owner's decision 2026-10-28: "Add the game data's weapon spread, so Bolter Targeter, Improved
/// Twin-Link, Burst Fire and Lascannon Stabilizers can work"). Rules (values from the data, the rules **interpretation**, spec/behavior.md
/// "Weapon spread"):
/// <list type="bullet">
/// <item>Each player carries a <b>bloom</b> (<see cref="PlayerState.SpreadBloom"/>, degrees added to the set's initial half-widths) and
/// the seconds since its last shot (<see cref="PlayerState.SpreadSince"/>). Every living tick, once the time since the last shot has
/// reached the set's <see cref="ScatterSet.DecreaseDelay"/>, the bloom falls by <see cref="ScatterSet.DecreasePerSecond"/> × dt (not below 0).</item>
/// <item>A shot leaving this tick (a basic shot, a burst round or a charged shot) is turned off its aim by a point drawn uniformly in the
/// ellipse of half-widths min(max, initial + bloom) (horizontal, vertical; degrees, in the shot's own frame); then the bloom grows by
/// <see cref="ScatterSet.IncreasePerShot"/> up to the set's largest and the time since the last shot restarts.</item>
/// <item>The set is the weapon in hand's (its perk variant: Bolter Targeter, Improved Twin-Link and Burst Fire switch sets) for the shot's
/// stage, in the stance (aim mode or not) and movement (<see cref="MovementOf"/>) after the tick's step.</item>
/// <item>A weapon switch, death or a weapon without spread clears the bloom.</item>
/// <item>The draw is deterministic: seeded from the player id and the input's tick (<see cref="Draw"/>), so the client's prediction of its
/// own shot reproduces the server's exactly. The turned shot stays inside <see cref="SimConfig.ShotConeDegrees"/> of the view
/// (<see cref="Combat.ShotAim(PlayerInput, SimConfig, PlayerState)"/>).</item>
/// </list>
/// The bloom and the shot's turn are not on the wire: the client keeps its own predicted values across a snapshot
/// (<see cref="Carry"/>; protocol unchanged).
/// </summary>
public static class Spread
{
    /// <summary>Below this horizontal speed (m/s) a shot is fired "idle" (ours; the locomotion's idle pose threshold, M40-T4).</summary>
    public const float IdleSpeed = 1f;
    /// <summary>At and above this horizontal speed (m/s) a shot is fired "running" (ours: between the walk band 2 and the run speed 6.5 of the NAV graphs).</summary>
    public const float RunSpeed = 4.25f;
    /// <summary>The largest time since the last shot kept (any value past every set's delay).</summary>
    public const float Rested = 1000f;
    /// <summary>
    /// The version of these rules (movement bands, the draw, the bloom's order), written into the hashed <see cref="SpreadSpec"/>: raise it with
    /// any rule change here so a code-only change also changes the config hash and old clients are refused (review, weapons/spread).
    /// 2: sim/light-melee-flinch (the flinch's extra on a shot; protocol 52).
    /// </summary>
    public const int RulesVersion = 2;

    const float Deg = MathF.PI / 180f;

    /// <summary>
    /// The movement band of a horizontal velocity (<see cref="IdleSpeed"/>, <see cref="RunSpeed"/>). feel/buffer-speed: both thresholds × <paramref name="speedScale"/>
    /// (<see cref="SimConfig.MoveSpeedScale"/>), so with the owner's slower movement every gait lands in the band it did before (the full backpedal, 4.5 → 4.05 m/s,
    /// stays "running"); 1 (and anything not finite and positive): the thresholds as they were. A scaled config already hashes differently (its MoveSpeedScale), so
    /// <see cref="RulesVersion"/> stays: at scale 1 nothing changed.
    /// </summary>
    public static SpreadMovement MovementOf(Vector3 velocity, float speedScale = 1f)
    {
        float k = float.IsFinite(speedScale) && speedScale > 0f ? speedScale : 1f;
        float speed = new Vector2(velocity.X, velocity.Y).Length();
        return !float.IsFinite(speed) || speed < IdleSpeed * k ? SpreadMovement.Idle : speed < RunSpeed * k ? SpreadMovement.Walking : SpreadMovement.Running;
    }

    /// <summary>The set a player's shot of <paramref name="stage"/> would use now (null: no scatter).</summary>
    /// <param name="speedScale"><see cref="SimConfig.MoveSpeedScale"/> (the movement bands' scale; <see cref="MovementOf"/>).</param>
    public static SpreadSet? SetFor(WeaponSpec? weapon, PlayerState s, int stage = 0, float speedScale = 1f) =>
        weapon?.Spread?.For(stage).SetFor(s.Aiming ? SpreadStance.Aiming : SpreadStance.Standing, MovementOf(s.Velocity, speedScale));

    /// <summary>
    /// The half-widths (degrees, horizontal and vertical) the next shot of <paramref name="stage"/> would be drawn in (for the crosshair); with
    /// <paramref name="c"/>, the flinch's extra (<see cref="FlinchDegrees"/>) on top (sim/light-melee-flinch).
    /// </summary>
    public static (float Horizontal, float Vertical) HalfWidths(WeaponSpec? weapon, PlayerState s, int stage = 0, float speedScale = 1f, SimConfig? c = null)
    {
        float flinch = c is null ? 0f : FlinchDegrees(c, s.FlinchTicks);
        if (SetFor(weapon, s, stage, speedScale)?.Scatter is not { } sc) return weapon is null ? (0f, 0f) : (flinch, flinch);
        float bloom = MathF.Max(0f, s.SpreadBloom);
        return (MathF.Min(sc.MaxHorizontal, sc.InitialHorizontal + bloom) + flinch, MathF.Min(sc.MaxVertical, sc.InitialVertical + bloom) + flinch);
    }

    // ---- sim/light-melee-flinch (protocol 52; the owner, 2026-11-01): a light melee hit throws the victim's aim off and does nothing else ----

    /// <summary>The ticks a flinch lasts: <see cref="SimConfig.MeleeFlinchSeconds"/> rounded up to whole ticks (at least 1 when the flinch is on; at most 255, one byte on the wire).</summary>
    public static int FlinchTicksOf(SimConfig c) =>
        !(c.MeleeFlinchDegrees > 0f) || !float.IsFinite(c.MeleeFlinchSeconds) || c.MeleeFlinchSeconds <= 0f ? 0
        : Math.Clamp((int)MathF.Ceiling(c.MeleeFlinchSeconds / c.TickSeconds - 1e-3f), 1, MaxFlinchTicks);

    /// <summary>Largest <see cref="PlayerState.FlinchTicks"/> (one byte on the wire).</summary>
    public const int MaxFlinchTicks = byte.MaxValue;

    /// <summary>
    /// The extra scatter (degrees, on both half-widths, past the set's maximum) of a shot fired with <paramref name="ticksLeft"/> flinch ticks left at
    /// its tick's start: <see cref="SimConfig.MeleeFlinchDegrees"/> × ticksLeft / the full flinch, so a hit's first tick takes the whole value and it falls
    /// linearly to nothing (ours).
    /// </summary>
    public static float FlinchDegrees(SimConfig c, int ticksLeft)
    {
        int total = FlinchTicksOf(c);
        if (total <= 0 || ticksLeft <= 0) return 0f;
        return c.MeleeFlinchDegrees * Math.Min(ticksLeft, total) / total;
    }

    /// <summary>
    /// <paramref name="s"/> flinched by a light melee hit (applied by the world after the victim's tick, as a reaction is): its flinch restarts at the full
    /// <see cref="FlinchTicksOf"/> (a second hit never shortens one under way). A dead player, or a config without the flinch, is unchanged. Nothing else
    /// changes: no reaction, no knockback, no action ended (the owner's rule: a light hit interrupts nothing).
    /// </summary>
    public static PlayerState Flinch(PlayerState s, SimConfig c)
    {
        int total = FlinchTicksOf(c);
        return !s.Alive || total <= 0 || s.FlinchTicks >= total ? s : s with { FlinchTicks = total };
    }

    /// <summary>
    /// A point uniform in the unit disc, from the player id, the tick and the shot's index in the tick (SplitMix64 on the three; the
    /// same arguments always give the same point).
    /// </summary>
    public static Vector2 Draw(int playerId, uint tick, int shot = 0)
    {
        ulong x = ((ulong)(uint)playerId << 32) ^ tick ^ ((ulong)(uint)shot << 56) ^ 0x5EED_5CA7_7E25_0001UL;
        ulong a = Mix(ref x), b = Mix(ref x);
        float u = (a >> 40) / (float)(1 << 24), v = (b >> 40) / (float)(1 << 24);   // [0, 1)
        float r = MathF.Sqrt(u), t = 2f * MathF.PI * v;
        return new Vector2(r * MathF.Cos(t), r * MathF.Sin(t));
    }

    static ulong Mix(ref ulong state)
    {
        ulong z = state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>
    /// The spread part of a tick (called by <see cref="Combat.StepPlayer"/> after the step): the bloom's decay, and for a shot leaving this
    /// tick its turn (<see cref="PlayerState.SpreadYaw"/>, <see cref="PlayerState.SpreadPitch"/>: radians across and up in the shot's frame)
    /// and the bloom's growth. See the class for the rules.
    /// </summary>
    public static PlayerState After(PlayerState before, PlayerState next, PlayerInput input, SimConfig c, SelfEvents events)
    {
        var weapon = next.Alive ? Loadouts.InHand(c, next) : null;
        bool reset = weapon?.Spread is null || !before.Alive || before.Slot != next.Slot || before.Class != next.Class;
        float dt = c.TickSeconds;
        float bloom = reset ? 0f : MathF.Max(0f, before.SpreadBloom);
        // A reset leaves every spread field 0 (a weapon without spread never changes them, so such states match the wire's).
        float since = reset || !float.IsFinite(before.SpreadSince) ? 0f : MathF.Min(Rested, MathF.Max(0f, before.SpreadSince) + dt);
        float yaw = next.SpreadYaw, pitch = next.SpreadPitch;
        // sim/light-melee-flinch: the flinch left at this tick's start scatters this tick's shot (past the set's maximum; a weapon without spread too), then one tick passes.
        int flinchLeft = next.Alive ? Math.Clamp(before.FlinchTicks, 0, MaxFlinchTicks) : 0;
        float flinch = FlinchDegrees(c, flinchLeft);
        // The decay follows the set the weapon would shoot with now.
        if (!reset && SetFor(weapon, next, 0, c.MoveSpeedScale)?.Scatter is { } now && since >= now.DecreaseDelay)
            bloom = MathF.Max(0f, bloom - MathF.Max(0f, now.DecreasePerSecond) * dt);
        if (events.Fired && weapon is not null)
        {
            var sc = SetFor(weapon, next, events.ShotStage, c.MoveSpeedScale)?.Scatter ?? ScatterSet.None;
            float h = MathF.Max(0f, MathF.Min(sc.MaxHorizontal, sc.InitialHorizontal + bloom)) + flinch;
            float v = MathF.Max(0f, MathF.Min(sc.MaxVertical, sc.InitialVertical + bloom)) + flinch;
            var p = Draw(next.PlayerId, input.Tick);
            yaw = p.X * h * Deg; pitch = p.Y * v * Deg;
            bloom = MathF.Min(bloom + MathF.Max(0f, sc.IncreasePerShot), sc.MaxBloom);
            since = 0f;
        }
        if (reset && !events.Fired) { yaw = 0f; pitch = 0f; }
        return next with { SpreadBloom = bloom, SpreadSince = since, SpreadYaw = yaw, SpreadPitch = pitch, FlinchTicks = Math.Max(0, flinchLeft - 1) };
    }

    /// <summary>Whether two states of one player hold the same bloom's owner: alive alike, same class, weapon slot and chosen primary and pistol.</summary>
    public static bool SameBloomOwner(PlayerState a, PlayerState b) =>
        a.Alive == b.Alive && a.Class == b.Class && a.Slot == b.Slot && a.Primary == b.Primary && a.Pistol == b.Pistol;

    /// <summary>
    /// <paramref name="authoritative"/> (a snapshot's own record, which does not carry the spread) with the spread fields of
    /// <paramref name="predicted"/> (the client's prediction of the same tick): the client's replay then draws the same scatter as the server.
    /// When the two disagree on what the bloom belongs to (alive, class, weapon slot or the chosen primary / pistol: a mispredicted switch, death or
    /// respawn), the prediction's bloom is not the server's: the spread fields are reset to rest (0) instead (review, weapons/spread).
    /// </summary>
    public static PlayerState Carry(PlayerState authoritative, PlayerState predicted)
    {
        if (!SameBloomOwner(authoritative, predicted))
            return authoritative with { SpreadBloom = 0f, SpreadSince = 0f, SpreadYaw = 0f, SpreadPitch = 0f };
        return authoritative with
        {
            SpreadBloom = predicted.SpreadBloom, SpreadSince = predicted.SpreadSince, SpreadYaw = predicted.SpreadYaw, SpreadPitch = predicted.SpreadPitch,
        };
    }

    /// <summary>
    /// <paramref name="direction"/> (unit) turned by a spread of <paramref name="across"/> (radians, to its right) and <paramref name="up"/>
    /// (radians, up) in its own frame (right = direction × Z; straight up or down: X).
    /// </summary>
    public static Vector3 Turn(Vector3 direction, float across, float up)
    {
        if (across == 0f && up == 0f) return direction;
        var right = Vector3.Cross(direction, Vector3.UnitZ);
        right = right.LengthSquared() < 1e-10f ? Vector3.UnitX : Vector3.Normalize(right);
        var top = Vector3.Cross(right, direction);
        return Vector3.Normalize(direction + right * MathF.Tan(across) + top * MathF.Tan(up));
    }
}
