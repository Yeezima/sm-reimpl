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
