namespace SmContent.Extras.Musou;

/// <summary>The arm's strikes by the data's abilities: <c>melee_x01</c>, <c>melee_x02</c>, <c>melee_y02_stomp</c>, <c>melee_y01</c> (the ground slam) and <c>melee_x_rush</c>.</summary>
public enum DaArmKey { Light1, Light2, Stomp, Slam, Rush }

/// <summary>The beats of a strike: its start (the ability's <c>start_audio</c>), the anticipation, the swing and the hit (the <c>triggered_actions</c> the animation tags fire).</summary>
public enum DaArmBeat { Start, Antic, Swing, Hit }

/// <summary>One beat's sounds and effects (<see cref="DaEffect.Marker"/> = where the effect plays).</summary>
public sealed record DaArmCue(DaArmKey Key, DaArmBeat Beat, string[] Sounds, DaEffect[] Effects);

/// <summary>A pad vibration (<c>controller_rumble</c>: the small and the large motor, 0 to 1, for <see cref="Seconds"/>; a data duration of 0 is <see cref="FootSeconds"/>, ours).</summary>
public readonly record struct DaRumble(float Small, float Large, float Seconds);

/// <summary>
/// The Dreadnought's arm cues, rumble, lights and exhaust (docs/discovery.md "DA Dreadnought inventory"; the entries of <c>wargear.oppc:dread_melee_default</c> and <c>ebps.oppc:sm_dreadnought</c>; kind: data for names, scales, markers and rumble
/// values; **ours** for when in a strike the antic and swing beats fire: the animation tags that fire them are not decoded, so they are placed at <see cref="AnticAt"/> and <see cref="SwingLead"/> of the hit time).
/// </summary>
public static class DreadnoughtArm
{
    /// <summary>The antic beat fires at this share of the strike's hit time, the swing this many seconds before it (ours).</summary>
    public const float AnticAt = 0.35f, SwingLead = 0.15f;
    public const string LeftHand = "fx_muzzle_l_hand", LeftFoot = "fx_l_foot", Torso = "fx_torso";

    static DaEffect E(string n, float s, string m) => new(n, s, m);

    public static readonly DaArmCue[] Cues =
    [
        new(DaArmKey.Light1, DaArmBeat.Start, ["play_dreadnought_melee_pwrup_x1"], []),
        new(DaArmKey.Light1, DaArmBeat.Antic, [], [E("thunderhammer_antic_light_center", 2f, LeftHand)]),
        new(DaArmKey.Light1, DaArmBeat.Swing, ["play_dreadnought_melee_swing_x1", "play_sm_foley_swing", "play_sm_thunder_hammer_swing", "play_ch_power_mace_antic"], [E("thunderhammer_swing_light", 1f, LeftHand)]),
        new(DaArmKey.Light2, DaArmBeat.Start, ["play_dreadnought_melee_pwrup_x1"], []),
        new(DaArmKey.Light2, DaArmBeat.Antic, ["play_sm_thunder_hammer_antic"], [E("thunderhammer_antic_light_center", 2f, LeftHand)]),
        new(DaArmKey.Light2, DaArmBeat.Swing, ["play_dreadnought_melee_swing_x2", "play_sm_foley_swing", "play_sm_thunder_hammer_swing", "play_sm_power_axe_swing_heavy"], [E("thunderhammer_swing_light", 1f, LeftHand)]),
        new(DaArmKey.Stomp, DaArmBeat.Start, ["play_sm_stomp_displace_build", "play_dreadnought_melee_pwrup_y2"], []),
        new(DaArmKey.Stomp, DaArmBeat.Antic, ["play_sm_stomp_displace_antic"], []),
        new(DaArmKey.Stomp, DaArmBeat.Swing, ["play_sm_thunder_hammer_swing", "play_sm_stomp_displace_swing", "play_dreadnought_melee_swing_01"], [E("interrupt_kick_grnd_swing", 1f, LeftFoot)]),
        new(DaArmKey.Stomp, DaArmBeat.Hit, ["play_sm_stomp_displace"], [E("knockback_stomp_dmg_furied", 1f, LeftFoot), E("interrupt_stomp_dmg", 1.15f, LeftFoot)]),
        new(DaArmKey.Slam, DaArmBeat.Start, ["play_dreadnought_melee_pwrup_y1"], []),
        new(DaArmKey.Slam, DaArmBeat.Antic, ["play_dreadnought_swing_end"], [E("thunderhammer_antic_light_center", 2f, LeftHand), E("thunderhammer_swing_light", 0.5f, LeftHand)]),
        new(DaArmKey.Slam, DaArmBeat.Swing, ["play_dreadnought_melee_swing_y1", "play_sm_powersword_swing"], [E("thunderhammer_swing_light", 1f, LeftHand), E("plasma_charging", 1f, LeftHand)]),
        new(DaArmKey.Slam, DaArmBeat.Hit, ["play_sm_thunderhammer_displace_fury"], [E("thunderhammer_dmg_displace_heavy", 2f, LeftHand), E("thunderhammer_dmg_displace_heavy", 2f, LeftHand)]),
        new(DaArmKey.Rush, DaArmBeat.Start, ["play_dreadnought_melee_antic_x1", "play_bull_rush_dash"], [E("bullrush_swing", 3f, Torso)]),
    ];

    /// <summary>The cues of <paramref name="key"/> at <paramref name="beat"/> (empty when the data has none).</summary>
    public static DaArmCue? Cue(DaArmKey key, DaArmBeat beat) => Cues.FirstOrDefault(c => c.Key == key && c.Beat == beat);

    /// <summary>The ability a strike is: light steps 1 and 2, the heavy's stomp and the slam; <paramref name="heavy"/> with <paramref name="step"/> 1-based.</summary>
    public static DaArmKey KeyOf(bool heavy, int step) => heavy ? (step <= 1 ? DaArmKey.Stomp : DaArmKey.Slam) : (step <= 1 ? DaArmKey.Light1 : DaArmKey.Light2);

    /// <summary>The seconds into a strike with hit time <paramref name="hit"/> at which <paramref name="beat"/> fires.</summary>
    public static float BeatTime(DaArmBeat beat, float hit) => beat switch { DaArmBeat.Start => 0f, DaArmBeat.Antic => hit * AnticAt, DaArmBeat.Swing => MathF.Max(hit * AnticAt, hit - SwingLead), _ => hit };

    // ---- rumble (data values; the client plays the own player's only) ----
    /// <summary>A footfall's rumble has a data duration of 0 (an instant pulse): played this long (ours).</summary>
    public const float FootSeconds = 0.1f;
    public static readonly DaRumble FootLeft = new(0.1f, 0.4f, FootSeconds), FootRight = new(0.4f, 0.1f, FootSeconds);
    public static readonly DaRumble StompSelf = new(0.6f, 0.35f, 0.5f), StompAreaHit = new(0.1f, 0.15f, 0.1f), ArmHit = new(0.1f, 0.5f, 0.5f), PushedAside = new(0.025f, 0f, 0.025f);

    /// <summary>The share of a rumble played: none with Reduce motion, else the Camera shake option (the game has no vibration option of its own).</summary>
    public static float RumbleScale(float cameraShakeOption, bool reduceMotion) => DreadnoughtFeel.Scale(cameraShakeOption, reduceMotion);

    /// <summary>Overcharge's wave at the hit (the stomp's displace effects at the size of the 3 m wave: knockback_stomp_dmg_furied's orange ring runs 1.2 to 6.2 m at scale 1, so 0.5 reaches 3 m; plus the footfall's dust ring; ours, an estimate), at the arm's hand marker; played on each swing's hit while the power-up runs.</summary>
    public static readonly DaEffect[] OverchargeWave = [E("knockback_stomp_dmg_furied", 0.5f, LeftHand), E("interrupt_stomp_dmg", 0.5f, LeftHand), E("jump_land", 2f, LeftHand)];

    // ---- lights and eyes (the blueprint's lifetime action; played while he lives, on the model's markers) ----
    public static readonly DaEffect[] Lights =
    [
        new("light_thunderhawk_red_medium", 0.7f, "fx_light_r_red"), new("light_thunderhawk_red_large", 1f, "fx_light_back_red"),
        new("tainted_psyker_skull_eye", 1f, "fx_light_center_skull01"), new("tainted_psyker_skull_eye", 1f, "fx_light_center_skull02"),
        new("light_red_pulse", 0.2f, "fx_lefteye_csm"), new("light_red_pulse", 0.2f, "fx_righteye_csm"),
        new("light_headlight_red", 0.2f, "fx_righteye_csm"), new("light_headlight_red", 0.2f, "fx_lefteye_csm"),
        new("tainted_psyker_skull_eye", 1f, "fx_light_l_skull01"), new("tainted_psyker_skull_eye", 1f, "fx_light_l_skull02"),
        new("tainted_psyker_skull_eye", 1f, "fx_light_r_skull01"), new("tainted_psyker_skull_eye", 1f, "fx_light_r_skull02"),
    ];

    // ---- exhaust (the blueprint's actn_effort_* actions reuse the jump-pack thrust effects at the exhausts; the data picks one of several scales at random: the middle one is used, ours) ----
    public static readonly DaEffect[] EffortStart = [new("autocannon_dmg_explosive_small", 0.1f, "fx_l_exhaust"), new("autocannon_dmg_explosive_small", 0.1f, "fx_r_exhaust"), new("amb_smoke_gushing", 0.25f, "fx_l_exhaust"), new("amb_smoke_gushing", 0.25f, "fx_r_exhaust")];
    public static readonly DaEffect[] EffortSprint = [new("stormboy_flying", 0.25f, "fx_l_exhaust"), new("stormboy_flying", 0.25f, "fx_r_exhaust"), new("autocannon_dmg_explosive_small", 0.2f, "fx_l_exhaust02"), new("autocannon_dmg_explosive_small", 0.2f, "fx_r_exhaust02")];
    public static readonly DaEffect[] EffortBig = [new("stormboy_flying", 0.25f, "fx_l_exhaust"), new("stormboy_flying", 0.25f, "fx_r_exhaust"), new("autocannon_dmg_explosive_small", 0.2f, "fx_l_exhaust"), new("autocannon_dmg_explosive_small", 0.2f, "fx_r_exhaust")];

    /// <summary>Every effect name the feel plays (the effect player only plays what was compiled up front: these are added to the effect load; before, the lights, eyes, death blasts and others were refused as unknown).</summary>
    public static IEnumerable<string> AllEffectNames() =>
        Cues.SelectMany(c => c.Effects).Concat(Lights).Concat(EffortStart).Concat(EffortSprint).Concat(EffortBig).Concat(DreadnoughtFeel.Table.SelectMany(f => f.Effects))
            .Select(e => e.Name).Concat(DreadnoughtFeel.MeltaEffects).Concat(DreadnoughtFeel.ChaosMeltaEffects).Append(DreadnoughtFeel.CannonTrail).Distinct();

    /// <summary>Every marker name the feel places effects on (read from the model's object tree by the client).</summary>
    public static IEnumerable<string> AllMarkers() =>
        Cues.SelectMany(c => c.Effects).Concat(Lights).Concat(EffortStart).Concat(EffortSprint).Concat(EffortBig).Concat(DreadnoughtFeel.Table.SelectMany(f => f.Effects))
            .Select(e => e.Marker).Concat(["fx_muzzle_assault_cannon", "fx_shells_eject_assault_cannon", "fx_muzzle", "fx_muzzle_autocannon_top", "fx_muzzle_autocannon_bottom"]).Distinct();
}
