using System;
using System.Collections.Generic;

namespace SmClient.ImpactRules;

/// <summary>The power weapons that flash on a hit (fx/power-flash).</summary>
public enum PowerKind { Sword, Axe, Hammer, Maul }

/// <summary>What a swing's next hit gets: the one big burst, a small one, or nothing (the cap).</summary>
public enum PowerBurst { None, Lead, Small }

/// <summary>
/// fx/power-flash: the engine-free rules of the power weapons' hit flash (client presentation; tested): which wargear flashes, the scale per weapon, the camera-close reduction and the
/// cap that keeps a Musou swing striking 20 enemies from spawning 20 full clouds.
/// </summary>
public static class PowerFlashRules
{
    /// <summary>A swing's hits within this many seconds share one budget: one big burst, then at most <see cref="SmallPerSwing"/> small ones (ours).</summary>
    public const double SwingSeconds = 0.6;
    public const int SmallPerSwing = 5;
    public const float SmallScale = 0.35f;
    /// <summary>A strike with its own blast (the hammer's slam, the axe's) has the data's displace effects already: our cloud is this much of its size (ours).</summary>
    public const float SlamCloud = 0.7f;
    /// <summary>The burst seen from <see cref="CloseNear"/> m or closer is <see cref="CloseMin"/> of its size, from <see cref="CloseFar"/> m out the whole (ours: no white-out of a camera inside it).</summary>
    public const float CloseNear = 1.2f, CloseFar = 5f, CloseMin = 0.22f;

    /// <summary>
    /// The strength every burst plays at, as a share of its earlier one (the effect's opacity: additive sprites' brightness, blended sprites' alpha, the light's intensity). The owner,
    /// 2026-11-24 (series 37, answers 2 and 3): "40% strength, see-through", in Classic PvP, Musou Mode and Warfront; size, shape and timing unchanged (was 1). Ours.
    /// </summary>
    public const float FlashOpacity = 0.4f;

    /// <summary>
    /// The size every burst plays at, as a share of its earlier one (the data's hit effect, its secondary and our cloud, with their lights' reach, which follow the effect's scale on
    /// the CPU path and on Musou Mode's GPU spray path alike). The owner, 2026-11-25 (series 37, typed in the lead chat): "That blue flash should be reduced in size for all game modes by
    /// 50%." Ours: 1 before; <c>--old-power-flash-strength</c> restores it.
    /// </summary>
    public const float FlashSize = 0.5f;

    /// <summary>
    /// Whether a power weapon's hit shows the blue (the maul's purple) flash at all: only the thunder hammer and the daemon maul (the owner, 2026-11-25: "Only the thunder hammer and daemon
    /// maul should have that blue flash. The rest of the weapons should have no on hit blue flash at all."); the power sword and the power axe none, the sword's contact flare included.
    /// <paramref name="old"/> (<c>--old-power-flash-strength</c>): every power weapon, as before.
    /// </summary>
    public static bool Flashes(PowerKind k, bool old) => old || k is PowerKind.Hammer or PowerKind.Maul;

    /// <summary>Whether a power sword's hit plays its blue contact flare (<c>PowerBlade.FlareName</c>) and the blade's hit flare: no since the owner's 2026-11-25 instruction (no on-hit blue flash but the hammer's and the maul's); <paramref name="old"/> (<c>--old-power-flash-strength</c>): yes. The blade's swing glow is unchanged.</summary>
    public static bool SwordContactFlare(bool old) => Flashes(PowerKind.Sword, old);

    /// <summary>The burst's size share (<see cref="FlashSize"/>, or 1 with <paramref name="old"/>) and its strength (<see cref="FlashOpacity"/>, or 1).</summary>
    public static (float Size, float Opacity) Look(bool old) => old ? (1f, 1f) : (FlashSize, FlashOpacity);
    /// <summary>
    /// A sprite's or light's colour at strength <paramref name="o"/>, applied once (<c>FxEffect.OpacityOnce</c>, used by the hit flash): a sprite keeps its colour and takes
    /// <paramref name="o"/> of its alpha (an additive sprite adds colour x alpha, a blended one covers by alpha: both end at exactly <paramref name="o"/>); a light takes it on its colour.
    /// </summary>
    public static (float R, float G, float B, float A) ShadeOnce((float R, float G, float B, float A) c, float o, bool light)
    {
        o = Math.Clamp(o, 0f, 1f);
        return light ? (c.R * o, c.G * o, c.B * o, c.A) : (c.R, c.G, c.B, c.A * o);
    }

    /// <summary>The power weapon a melee wargear file is (<c>sm_mp_melee_2hand_thunder_hammer</c>, <c>csm_mp_melee_2hand_power_mace</c> = the daemon maul, <c>..._power_axe</c>, <c>..._powersword</c>), or null.</summary>
    public static PowerKind? KindOf(string? wargear)
    {
        if (wargear is null) return null;
        if (wargear.Contains("thunder_hammer", StringComparison.OrdinalIgnoreCase)) return PowerKind.Hammer;
        if (wargear.Contains("power_mace", StringComparison.OrdinalIgnoreCase)) return PowerKind.Maul;
        if (wargear.Contains("power_axe", StringComparison.OrdinalIgnoreCase)) return PowerKind.Axe;
        if (wargear.Contains("powersword", StringComparison.OrdinalIgnoreCase)) return PowerKind.Sword;
        return null;
    }

    /// <summary>Scale of the effects per weapon (ours, estimates: the hammer is the reference clip).</summary>
    public static float Scale(PowerKind k) => k switch { PowerKind.Hammer => 1f, PowerKind.Maul => 1f, PowerKind.Axe => 0.75f, _ => 0.6f };

    /// <summary>The share of the burst's size shown to a camera <paramref name="distance"/> m away (smooth).</summary>
    public static float CloseFactor(float distance)
    {
        if (!(distance > CloseNear)) return CloseMin;
        if (distance >= CloseFar) return 1f;
        float t = (distance - CloseNear) / (CloseFar - CloseNear);
        return CloseMin + (1f - CloseMin) * t * t * (3f - 2f * t);
    }

    /// <summary>Per attacker: the first hit of a swing is the lead, the next <see cref="SmallPerSwing"/> within <see cref="SwingSeconds"/> of the swing's first are small, the rest nothing.</summary>
    public sealed class Budget
    {
        readonly Dictionary<int, (double At, int Count)> _swings = new();

        public PowerBurst Take(int attacker, double now)
        {
            _swings.TryGetValue(attacker, out var swing);
            if (swing.Count == 0 || now - swing.At > SwingSeconds) swing = (now, 0);
            int n = swing.Count;
            _swings[attacker] = (swing.At, n + 1);
            return n == 0 ? PowerBurst.Lead : n <= SmallPerSwing ? PowerBurst.Small : PowerBurst.None;
        }

        public void Forget(int attacker) => _swings.Remove(attacker);
    }
}
