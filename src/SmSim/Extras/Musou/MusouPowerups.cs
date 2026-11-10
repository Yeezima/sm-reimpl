namespace SmSim.Extras.Musou;

/// <summary>A Musou powerup (owner, 2026-11-10): what an enemy's drop holds since weapons stopped dropping. 0 is none.</summary>
public enum MusouPowerup : byte { None = 0, DoubleDamage = 1, Fear = 2, Supercharged = 3, RapidFire = 4 }

/// <summary>
/// The Musou powerups (owner, 2026-11-10: "they don't drop weapons anymore but powerups, and more rarely. they should drop a 2x damage buff that lasts 30s, a buff that makes all orks run away from you for 15s,
/// a buff that makes your melee attacks supercharged, and a buff that makes your ranged attacks twice as fast and consume no ammunition similar to a stim"; answers: supercharged melee "Double damage + wider
/// cleave", the melee and ranged buffs "20 s each", rarity "Elites and bosses only, ~10%", pickup "Hold the use key"). A drop's item (<see cref="MusouDropItem"/>) is <see cref="Flag"/> plus the kind.
/// Durations, the 10 % and the elite/boss rule are the owner's; every other value is ours.
/// </summary>
public static class MusouPowerupRules
{
    /// <summary>The item bit of a powerup (wire tag 6's item: weapon indices sit far below it, melee items carry <see cref="MusouDropItem.MeleeFlag"/>).</summary>
    public const ushort Flag = 0x4000;
    public static readonly MusouPowerup[] All = [MusouPowerup.DoubleDamage, MusouPowerup.Fear, MusouPowerup.Supercharged, MusouPowerup.RapidFire];

    /// <summary>Seconds each lasts (owner: 30 s, 15 s, 20 s, 20 s).</summary>
    public static float Seconds(MusouPowerup kind) => kind switch
    {
        MusouPowerup.DoubleDamage => 30f,
        MusouPowerup.Fear => 15f,
        MusouPowerup.Supercharged => 20f,
        MusouPowerup.RapidFire => 20f,
        _ => 0f,
    };

    /// <summary>Double Damage: all the player's damage to enemies (owner: 2x).</summary>
    public const float DamageScale = 2f;
    /// <summary>Supercharged Melee: melee damage (owner: "Double damage"), and the cleave's reach and cone (owner: "wider cleave"; the lead's 50 %; ours: reach and half-angle both, the angle at most 180 degrees).</summary>
    public const float MeleeScale = 2f, CleaveScale = 1.5f;
    /// <summary>Rapid Fire: the fire rate (owner: "twice as fast"); no round spent.</summary>
    public const float FireRate = 2f;
    /// <summary>Fear: Orks within this many metres of the player run from it (ours), this much faster than they walk (ours, as the Scrap Mek's flight).</summary>
    public const float FearRadius = 30f, FearSpeedScale = 1.15f;
    /// <summary>The share of elite KOs that drop one (owner: "~10%").</summary>
    public const float EliteChance = 0.1f;

    public static bool IsPowerup(ushort item) => (item & MusouDropItem.MeleeFlag) == 0 && (item & Flag) != 0 && KindOf(item) != MusouPowerup.None;
    public static ushort Item(MusouPowerup kind) => (ushort)(Flag | (byte)kind);
    public static MusouPowerup KindOf(ushort item) => (item & Flag) == 0 || (item & MusouDropItem.MeleeFlag) != 0 ? MusouPowerup.None : (item & 0xFF) switch
    {
        1 => MusouPowerup.DoubleDamage, 2 => MusouPowerup.Fear, 3 => MusouPowerup.Supercharged, 4 => MusouPowerup.RapidFire, _ => MusouPowerup.None,
    };

    /// <summary>The bosses: their KO always drops one (owner: bosses always). The Warboss (the war's commander) and the roaming Boss Orks.</summary>
    public static bool Boss(MusouEnemyType type) => type == MusouEnemyType.Warboss || MusouBossOrks.Is(type);

    /// <summary>The elites (owner: "Nobs and up"; ours: the Nob, the Big Shoota Nob, the Weirdboy and the 5-wave run's Chaos Marine): their KO drops one <see cref="EliteChance"/> of the time.</summary>
    public static bool Elite(MusouEnemyType type) => type is MusouEnemyType.Nob or MusouEnemyType.BigShootaNob or MusouEnemyType.Weirdboy or MusouEnemyType.ChaosMarine;

    /// <summary>Whether the KO of <paramref name="enemy"/> (of <paramref name="type"/>) on <paramref name="tick"/> drops a powerup: a boss always, an elite by a hash of the tick and the enemy (deterministic), nothing else.</summary>
    public static bool Drops(MusouEnemyType type, uint tick, ushort enemy)
    {
        if (Boss(type)) return true;
        if (!Elite(type)) return false;
        ulong h = Mix(Mix(tick) ^ ((ulong)enemy * 0xD6E8FEB86659FD93UL) ^ 0x5851F42D4C957F2DUL);
        return (h >> 11) * (1.0 / (1UL << 53)) < EliteChance;
    }

    /// <summary>The kind of player <paramref name="player"/>'s powerup from that KO: uniform over the four (a hash of the three numbers, independent per player).</summary>
    public static MusouPowerup Roll(uint tick, ushort enemy, int player)
    {
        ulong h = Mix(Mix(tick ^ 0xA5A5A5A5u) ^ ((ulong)enemy * 0x9E3779B97F4A7C15UL) ^ ((ulong)(uint)player * 0xA0761D6478BD642FUL));
        return All[(int)(h % (ulong)All.Length)];
    }

    static ulong Mix(ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }
}

/// <summary>One player's active powerups: the tick each ends on (0: not active). Taking one already active starts its full time again (ours: no stacking).</summary>
public struct MusouBuffs
{
    public uint DoubleDamage, Fear, Supercharged, RapidFire;

    public readonly uint EndOf(MusouPowerup kind) => kind switch
    {
        MusouPowerup.DoubleDamage => DoubleDamage, MusouPowerup.Fear => Fear, MusouPowerup.Supercharged => Supercharged, MusouPowerup.RapidFire => RapidFire, _ => 0,
    };

    public void Set(MusouPowerup kind, uint end)
    {
        switch (kind)
        {
            case MusouPowerup.DoubleDamage: DoubleDamage = end; break;
            case MusouPowerup.Fear: Fear = end; break;
            case MusouPowerup.Supercharged: Supercharged = end; break;
            case MusouPowerup.RapidFire: RapidFire = end; break;
        }
    }

    public readonly bool Active(MusouPowerup kind, uint tick) => EndOf(kind) > tick;
    public readonly bool Any(uint tick) => DoubleDamage > tick || Fear > tick || Supercharged > tick || RapidFire > tick;
}
