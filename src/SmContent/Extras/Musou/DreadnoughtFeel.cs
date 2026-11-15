using System.Numerics;

namespace SmContent.Extras.Musou;

/// <summary>One axis of a camera shake (the data's <c>camera_shake_entry</c>): <see cref="Amplitude"/> (metres for the position axes, degrees for the angles: **ours**, the file states no unit), <see cref="Frequency"/> in Hz and <see cref="PhaseDegrees"/>.</summary>
public readonly record struct DaShakeAxis(float Amplitude, float Frequency, float PhaseDegrees)
{
    public float At(float t) => Amplitude * MathF.Sin(2f * MathF.PI * Frequency * t + PhaseDegrees * MathF.PI / 180f);
}

/// <summary>
/// A Dreadnought Assault Dreadnought camera effect (the blueprint <c>sm_dreadnought</c>'s <c>camera_effect</c> commands; Musou Mode only; kind: data for every number below, read with <c>smtool baf-dump</c> on
/// <c>ebps.oppc:sm_dreadnought</c>): <see cref="Duration"/> s at full strength, then <see cref="EaseOut"/> s fading; the camera is shaken along five axes by sine waves. <see cref="Near"/> and <see cref="Far"/> are the data's
/// <c>attenuation_start_distance</c> and <c>attenuation_end_distance</c>: full strength within Near metres, none beyond Far (the fall-off between is linear: **ours**, the file names no curve).
/// <see cref="OnlyOnPlayer"/> is <c>apply_only_if_on_player</c> (the effect shakes the camera of the player the action hits, not onlookers).
/// </summary>
public sealed record DaShake(string Name, float Duration, float EaseIn, float EaseOut, DaShakeAxis Horizontal, DaShakeAxis Vertical, DaShakeAxis Pitch, DaShakeAxis Yaw, DaShakeAxis Roll, float Near, float Far, bool OnlyOnPlayer = false)
{
    public float Total => Duration + EaseOut;

    /// <summary>The envelope 0..1 at <paramref name="t"/> seconds after the effect began.</summary>
    public float Envelope(float t)
    {
        if (!(t >= 0f) || t > Total) return 0f;
        float rise = EaseIn > 1e-4f ? MathF.Min(1f, t / EaseIn) : 1f;
        float fall = t <= Duration ? 1f : EaseOut > 1e-4f ? MathF.Max(0f, 1f - (t - Duration) / EaseOut) : 0f;
        return rise * fall;
    }

    /// <summary>Strength 0..1 of the effect <paramref name="distance"/> metres from where it happened.</summary>
    public float Gain(float distance) => distance <= Near ? 1f : distance >= Far ? 0f : 1f - (distance - Near) / (Far - Near);

    /// <summary>The camera offset (metres: horizontal, vertical, 0) and angles (degrees: pitch, yaw, roll) at <paramref name="t"/>, times <paramref name="strength"/>.</summary>
    public (Vector3 Offset, Vector3 Degrees) Sample(float t, float strength)
    {
        float k = Envelope(t) * strength;
        if (k <= 0f) return (Vector3.Zero, Vector3.Zero);
        return (new Vector3(Horizontal.At(t), Vertical.At(t), 0f) * k, new Vector3(Pitch.At(t), Yaw.At(t), Roll.At(t)) * k);
    }
}

/// <summary>What the Dreadnought's actions do to the camera and the senses (Dreadnought Assault Dreadnought's data; docs/discovery.md "DA Dreadnought inventory"; Musou only).</summary>
public enum DreadAction
{
    FootLeft, FootRight, SprintFootLeft, SprintFootRight, LandHeavy, LandLight, HitWall, StompRight, StompLeft, Entry, Buff, BuffEnd, DeathStart, DeathBlast, Hurt66, Hurt33, ArmourBreak, ControlPoint,
}

/// <summary>An effect an action plays: the install's effect name, its scale and the marker (bone point) it plays at (the client places it on the body: marker heights are **ours**).</summary>
public readonly record struct DaEffect(string Name, float Scale, string Marker, float DelaySeconds = 0f);

/// <summary>One action's presentation: sound events (Wwise names), effects and the shake.</summary>
public sealed record DaFeel(DreadAction Action, string Trigger, string[] Sounds, DaEffect[] Effects, DaShake? Shake);

public static class DreadnoughtFeel
{
    static DaShakeAxis A(float amp, float freq, float phase) => new(amp, freq, phase);
    const string Bp = "ebps.oppc:sm_dreadnought";

    // ---- camera shakes (kind: data; each from the entity action's camera_effect command) ----
    public static readonly DaShake Footfall = new("footfall", 0.1f, 0f, 0.05f, A(0, 12, 10), A(0.15f, 10, 10), A(0.75f, 4, 90), A(0.5f, 15, 15), A(0.5f, 25, 10), 2.5f, 15f);
    public static readonly DaShake SprintFootfall = new("sprint footfall", 0.15f, 0f, 0.075f, A(0, 12, 10), A(0.15f, 10, 10), A(1f, 4, 90), A(0.6f, 15, 180), A(0.65f, 25, 100), 2.5f, 15f);
    public static readonly DaShake LandHeavy = new("heavy landing", 0.25f, 0f, 0.3f, A(0, 12, 0), A(0.1f, 12, 230), A(1f, 5, 180), A(0.35f, 25, 120), A(0.4f, 20, 0), 10f, 30f);
    public static readonly DaShake LandLight = new("light landing", 0.15f, 0f, 0.1f, A(0, 12, 0), A(0f, 12, 230), A(0.5f, 8, 180), A(0.2f, 25, 120), A(0.25f, 20, 0), 10f, 30f);
    public static readonly DaShake HitWall = new("wall hit", 0.25f, 0f, 0.25f, A(0.01f, 25, 10), A(0.5f, 5, 50), A(1.5f, 4, 180), A(0.3f, 20, 10), A(0.35f, 20, 200), 10f, 20f);
    public static readonly DaShake Stomp = new("stomp", 0.3f, 0f, 0.15f, A(0, 5, 10), A(0.5f, 6, 0), A(1.5f, 7, 100), A(0.8f, 20, 10), A(0.85f, 25, 0), 5f, 25f);
    /// <summary>The stomp's area shake: the camera effect inside the 10 m damage area's hit actions (only for a player the stomp hits; <c>apply_only_if_on_player</c>).</summary>
    public static readonly DaShake StompArea = new("stomp area", 0.05f, 0f, 0f, A(0, 6, 0), A(0f, 12, 0), A(0.45f, 5, 0), A(0.5f, 3, 30), A(0.25f, 2, 15), 10f, 30f, OnlyOnPlayer: true);
    public static readonly DaShake Entry = new("entrance", 0.15f, 0f, 0f, A(0, 6, 0), A(0f, 12, 0), A(0.65f, 5, 0), A(0.8f, 3, 30), A(1f, 2, 15), 10f, 30f);
    public static readonly DaShake DeathBlastLight = new("death blast (small)", 0.15f, 0f, 0.2f, A(0, 12, 0), A(0f, 12, 0), A(0.85f, 10, 200), A(0.55f, 20, 100), A(0.72f, 15, 50), 5f, 25f);
    public static readonly DaShake DeathBlastLong = new("death blast (torso)", 0.35f, 0f, 0.65f, A(0, 12, 0), A(0f, 12, 0), A(0.85f, 15, 200), A(0.75f, 25, 100), A(1f, 10, 50), 5f, 25f);

    /// <summary>Every shake, for the table and the tests.</summary>
    /// <summary>Overcharge's wave (the power-up's addition, owner 2026-11-15): the stomp area's shake at a quarter of its amplitudes (**ours (estimate)**), on the own camera.</summary>
    public static readonly DaShake OverchargeShake = StompArea with { Name = "overcharge wave", Horizontal = Q(StompArea.Horizontal), Vertical = Q(StompArea.Vertical), Pitch = Q(StompArea.Pitch), Yaw = Q(StompArea.Yaw), Roll = Q(StompArea.Roll) };
    static DaShakeAxis Q(DaShakeAxis a) => a with { Amplitude = a.Amplitude * 0.25f };
    public static readonly DaShake[] Shakes = [Footfall, SprintFootfall, LandHeavy, LandLight, HitWall, Stomp, StompArea, Entry, DeathBlastLight, DeathBlastLong];

    // ---- the per-action table (the checklist docs/discovery.md "DA Dreadnought inventory" mirrors; the client plays exactly these) ----
    public static readonly DaFeel[] Table =
    [
        new(DreadAction.FootLeft, "evt_left_foot", ["play_dreadnought_footstep", "play_or_warboss_footstep"], [new("jump_land", 0.5f, "fx_l_foot")], Footfall),
        new(DreadAction.FootRight, "evt_right_foot", ["play_dreadnought_footstep", "play_or_warboss_footstep"], [new("jump_land", 0.5f, "fx_r_foot")], Footfall),
        new(DreadAction.SprintFootLeft, "evt_left_foot_sprint", ["play_dreadnought_sprint_footstep", "play_or_warboss_footstep"], [new("jump_land", 0.7f, "fx_l_foot")], SprintFootfall),
        new(DreadAction.SprintFootRight, "evt_right_foot_sprint", ["play_dreadnought_sprint_footstep", "play_or_warboss_footstep"], [new("jump_land", 0.7f, "fx_r_foot")], SprintFootfall),
        new(DreadAction.LandHeavy, "evt_land_heavy", ["play_power_klaw_smash"], [new("jumppack_land_heavy", 1f, "fx_cntr")], LandHeavy),
        new(DreadAction.LandLight, "evt_land_light", ["play_dreadnought_footstep", "play_or_warboss_footstep_bullrush"], [new("jumppack_land_light", 0.75f, "fx_cntr"), new("jumppack_land_heavy", 1f, "fx_cntr")], LandLight),
        new(DreadAction.HitWall, "actn_hit_wall", ["play_power_klaw_smash"], [new("prop_smash", 2f, "fx_torso")], HitWall),
        new(DreadAction.StompRight, "evt_spawn_r_stomp", ["play_sm_stomp_displace"], [new("knockback_stomp_dmg_furied", 0.5f, "fx_r_foot"), new("interrupt_stomp_dmg", 0.75f, "fx_r_foot")], Stomp),
        new(DreadAction.StompLeft, "evt_spawn_l_stomp", ["play_sm_stomp_displace"], [new("knockback_stomp_dmg_furied", 0.5f, "fx_l_foot"), new("interrupt_stomp_dmg", 0.75f, "fx_l_foot")], Stomp),
        new(DreadAction.Entry, "evt_spawn_in", [], [new("inq_weapon_explode_center", 0.25f, "fx_torso"), new("amb_chaos_portal_clouds", 1.5f, "fx_torso"), new("krak_grenade_explosion", 1f, "fx_cntr"),
            new("thunderhammer_swing_light", 1.1f, "fx_muzzle_l_hand"), new("thunderhammer_swing_light", 1.25f, "fx_muzzle_l_hand"), new("csm_plasma_charging_ready", 1f, "fx_muzzle_l_hand"), new("cl_force_field", 3f, "fx_torso")], Entry),
        new(DreadAction.Buff, "dread_buff", ["play_sm_stimpack"], [new("powersource_glow", 12f, "fx_torso"), new("powersource_glow", 11f, "fx_torso"), new("plasma_gun_high_heat", 1f, "fx_torso"), new("inq_weapon_center_lightning", 1f, "fx_torso"), new("stimpack_heal_icon_looping", 1f, "fx_torso")], null),
        new(DreadAction.BuffEnd, "dread_buff end", [], [new("perkfx_reactiveblast_exp", 1f, "fx_torso"), new("iron_halo_break_shoulders", 1f, "fx_torso")], null),
        new(DreadAction.DeathStart, "death_action", ["play_or_horde_bodyfall", "stop_temp_or_horde_ambient_speech"], [], null),
        new(DreadAction.DeathBlast, "death_action delayed", [], [new("chaos_marine_death", 0.25f, "fx_l_shoulder"), new("chaos_marine_death", 0.2f, "fx_r_thigh", 0.15f), new("chaos_marine_death", 0.35f, "fx_pelvis", 0.35f),
            new("chaos_marine_death", 1f, "fx_torso", 0.5f), new("chaos_marine_death", 0.15f, "fx_l_thigh", 0.85f), new("amb_fire", 0.5f, "fx_torso", 0.85f)], DeathBlastLight),
        new(DreadAction.Hurt66, "health_actions 66%", [], [new("warboss_exhaust", 0.3f, "fx_torso"), new("sm_armor_break_shoulders_loop", 2f, "fx_torso")], null),
        new(DreadAction.Hurt33, "health_actions 33%", [], [new("amb_fire_gushing_extreme", 0.3f, "fx_torso"), new("warboss_exhaust", 0.4f, "fx_torso"), new("sm_armor_break_shoulders_loop", 1f, "fx_torso")], null),
        new(DreadAction.ArmourBreak, "health_actions 1/3 buffer down_into", ["play_armor_break"], [new("sm_armor_break_shoulders", 3.25f, "fx_torso")], null),
        new(DreadAction.ControlPoint, "evt_dread_cap", ["play_sm_stimpack"], [new("fury_activated_titus_start", 2f, "fx_pelvis")], null),
    ];

    public static DaFeel For(DreadAction action) => Table.First(f => f.Action == action);

    // ---- the engine and the cannon / melta / arm presentation (DA's own; the autocannon borrowing is gone) ----
    /// <summary>The engine loop of the blueprint's lifetime action (bank Weapon_Dreadnought; the earlier finding's <c>play_engine_sm_dreadnought</c> is the same bank's loop), the lights it plays (markers on the model).</summary>
    public const string EngineEvent = "play_dreadnought_engine", EarlierEngineEvent = "play_engine_sm_dreadnought";   // both exist in bank Weapon_Dreadnought; the blueprint's is used (lead 27, 2026-11-15)

    /// <summary>The cannon's shot (the wargear's own lists; docs/discovery.md "DA Dreadnought inventory"): sound events, muzzle flash (scale 1.75), casing effect (scale 3), the tracer trail and the hit container.</summary>
    public static readonly string[] CannonSounds = ["play_sm_heavy_bolter_fire", "play_dreadnought_sm_assault_cannon"];
    public static readonly DaEffect CannonMuzzle = new("autocannon_muzzle", 1.75f, "fx_muzzle_assault_cannon"), CannonCasing = new("shell_casing", 3f, "fx_shells_eject_assault_cannon");
    /// <summary>How far ahead of the muzzle the tracer trail runs (ours: the shot's hit point is not known to the effect).</summary>
    public const float TracerMetres = 60f;
    public const string CannonTrail = "chaos_kraken_trail", CannonHit = "dlc3_dread_assault_cannon", ChaosCannonHit = "dlc3_dread_reaper_cannon";
    public static readonly string[] MeltaEffects = ["meltagun_cylinder_blast_3", "meltagun_cylinder_blast_4"], ChaosMeltaEffects = ["csm_meltagun_cylinder_blast_3", "csm_meltagun_cylinder_blast_4"];
    /// <summary>The Chaos Dreadnought's melta plays the same wargear's <c>csm_</c> cylinder blasts (the install holds a second <c>dread_melta</c> entry with them): the effect renames the effect catalogue applies.</summary>
    public static readonly IReadOnlyDictionary<string, string> ChaosMeltaNames = new Dictionary<string, string> { [MeltaEffects[0]] = ChaosMeltaEffects[0], [MeltaEffects[1]] = ChaosMeltaEffects[1] };
    /// <summary>A marker the wargear's effect names first that the model lacks, and the model's own for it (the data lists <c>fx_muzzle_autocannon_top</c> then <c>fx_muzzle_assault_cannon</c> on the flash; the loyalist has the second, the Chaos model both). The melta's <c>fx_muzzle</c> is on the model as <c>fx_Muzzle</c> (names compare without case).</summary>
    public static readonly IReadOnlyDictionary<string, string> MarkerFallback = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["fx_muzzle_autocannon_top"] = "fx_muzzle_assault_cannon", ["fx_muzzle_autocannon_bottom"] = "fx_muzzle_assault_cannon" };
    /// <summary>
    /// The cannon's effect marker <c>fx_muzzle_assault_cannon</c> sits this far (m) ahead of the barrel's drawn tip along the aim (**ours**, measured in the film of 2026-11-15: the tracer, which starts at the marker, began 1.5 m past the barrel;
    /// the model's barrels are shorter than the marker the data places effects on). Effects at the cannon's muzzle are brought back to the tip by it.
    /// </summary>
    public const float MarkerToTip = 1.4f;
    /// <summary>
    /// The flash's bright core (bloom ring offset 0.809 m at scale 1 times the data's 1.75, <c>autocannon_muzzle</c>; the beams and billboard volumes start 0.74 to 1.37 m ahead of the effect's origin too) is this far ahead of the effect's origin.
    /// </summary>
    public const float FlashCoreAhead = 0.809f * 1.75f;
    /// <summary>The flash's origin goes back this far from the marker so that the core sits at the barrel's tip: the marker's distance to the tip plus the core's distance ahead of the origin.</summary>
    public const float FlashPullback = MarkerToTip + FlashCoreAhead - FlashSmearRelief;
    /// <summary>The flash core is left this far (m) ahead of the tip (**ours**): the full pullback put the flash's long side beams 1.4 m behind the tip, where from the side they smeared over the hull.</summary>
    public const float FlashSmearRelief = 0.4f;
    /// <summary>Whether <paramref name="marker"/> is the cannon flash's marker (the effect's own list names the top and bottom ones; the model has the assault cannon's).</summary>
    public static bool IsFlashMarker(string marker) => marker.Equals("fx_muzzle_autocannon_top", StringComparison.OrdinalIgnoreCase) || marker.Equals("fx_muzzle_autocannon_bottom", StringComparison.OrdinalIgnoreCase);
    /// <summary>
    /// How far back along the aim a shot effect placed on <paramref name="marker"/> goes. Loyalist: the tracer's marker to the tip, the flash's (a fallback to the same marker) to the tip less the flash's own forward offset. Chaos twin: its model has the
    /// flash markers on the barrel tips (the tracer shares them), so both go back only by the flash's forward offset (the flash core at the tip; the tracer starts inside the barrel). 0 for every other marker.
    /// </summary>
    public static float TipShift(string marker, bool chaos = false) =>
        IsFlashMarker(marker) ? (chaos ? FlashCoreAhead - FlashSmearRelief : FlashPullback) : !chaos && marker.Equals("fx_muzzle_assault_cannon", StringComparison.OrdinalIgnoreCase) ? MarkerToTip : 0f;
    /// <summary>The longest reach (m) of the lights of the Dreadnought's effects (**ours**): their data lights scale with the effect (x12 for the power-up glow), lit the volumetric fog and tinted the whole view.</summary>
    public const float LightReach = 5f;
    /// <summary>The death fire: the data's <c>amb_fire</c> 0.85 s after the death (<see cref="DeathFireDelay"/>); the client loops it on the wreck.</summary>
    public const string DeathFireEffect = "amb_fire";
    public const float DeathFireDelay = 0.85f;
    /// <summary>The entrance cue is due: a living body (not a corpse) in the first 0.3 s of a life that has not played it yet.</summary>
    public static bool EntryDue(bool alive, bool corpse, double sinceBorn, bool entered) => alive && !corpse && !entered && sinceBorn < 0.3;
    public const string MeltaPumpEvent = "play_melta_gun_pump";
    /// <summary>The pump sounds this long after the melta's shot (ours: the pump state follows the shot).</summary>
    public const float MeltaPumpDelay = 0.45f;

    // ---- the shake's scaling (Options: Camera shake; Reduce motion) ----
    /// <summary>The share of a shake drawn: nothing with Reduce Motion, else the Camera shake option (0..1).</summary>
    public static float Scale(float cameraShakeOption, bool reduceMotion) => reduceMotion ? 0f : Math.Clamp(cameraShakeOption, 0f, 1f);

    /// <summary>Sums the active shakes (<paramref name="active"/>: shake, age in seconds, distance from the camera's player in metres, whether it is aimed at that player, whether it hit that player) with the settings' scale.</summary>
    public static (Vector3 Offset, Vector3 Degrees) Mix(IEnumerable<(DaShake Shake, float Age, float Distance, bool Hit)> active, float cameraShakeOption, bool reduceMotion)
    {
        float scale = Scale(cameraShakeOption, reduceMotion);
        Vector3 off = Vector3.Zero, deg = Vector3.Zero;
        if (scale <= 0f) return (off, deg);
        foreach (var (shake, age, distance, hit) in active)
        {
            if (shake.OnlyOnPlayer && !hit) continue;
            var (o, d) = shake.Sample(age, shake.Gain(distance) * scale);
            off += o; deg += d;
        }
        return (off, deg);
    }
}
