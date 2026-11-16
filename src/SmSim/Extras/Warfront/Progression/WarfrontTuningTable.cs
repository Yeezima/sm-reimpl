using SmSim.Extras.Musou;

namespace SmSim.Extras.Warfront.Progression;

/// <summary>One row of <c>smtool warfront-tuning</c>: the scaled Orks at one level and what they do to a representative Marine.</summary>
public sealed record WarfrontTuningRow(
    int Level, float HpMult, float DmgMult, IReadOnlyList<WarfrontTuningOrk> Orks);

/// <summary>
/// One Ork at one level. <see cref="Hp"/> and <see cref="Hit"/> are the scaled health and damage per hit; the time to kill is against the bolter and the unbraced heavy bolter (spec weapon rows) with no cards,
/// a balanced build (damage, attack speed, max HP in turn) and an all-damage build, all at 100 % accuracy and no reload (a lower bound); <see cref="HitsToDown"/> counts hits on a 100 health + 100 armour Marine
/// with no cards and with the balanced build's max HP / armour.
/// </summary>
public sealed record WarfrontTuningOrk(
    MusouEnemyType Type, string Name, float Hp, float Hit, double BolterTtkNone, double BolterTtkBalanced, double BolterTtkDamage,
    double HeavyTtkNone, int HitsToDownNone, int HitsToDownBalanced);

public static class WarfrontTuningTable
{
    /// <summary>Spec "Multiplayer weapons" (extracted): bolter 22 damage per 0.133 s; heavy bolter 35 per 0.233 s unbraced.</summary>
    public const float BolterDamage = 22f, BolterInterval = 0.133f, HeavyDamage = 35f, HeavyInterval = 0.233f;
    /// <summary>Spec "Health max" / "Armour shield max": 100 + 100.</summary>
    public const float MarineHealth = 100f, MarineShield = 100f;

    public static readonly (MusouEnemyType Type, string Name)[] Orks =
    [
        (MusouEnemyType.Boy, "Boy"), (MusouEnemyType.ShootaBoy, "Shoota"), (MusouEnemyType.ArdBoy, "'Ard Boy"), (MusouEnemyType.Nob, "Nob"), (MusouEnemyType.BossNob, "Boss Nob"),
    ];

    /// <summary>The cards of the balanced build after <paramref name="picks"/> picks: damage, attack speed, max HP in turn.</summary>
    public static int[] BalancedCards(int picks)
    {
        var l = new List<int>();
        for (int i = 0; i < picks; i++) l.Add(i % 3 == 0 ? (int)WarfrontCard.Damage : i % 3 == 1 ? (int)WarfrontCard.AttackSpeed : (int)WarfrontCard.MaxHp);
        return l.ToArray();
    }

    public static int[] DamageCards(int picks) => Enumerable.Repeat((int)WarfrontCard.Damage, picks).ToArray();

    /// <summary>Levels 1 to <paramref name="maxLevel"/> for <paramref name="humans"/> humans (one pick per level above 1 on the player's side).</summary>
    public static List<WarfrontTuningRow> Build(int maxLevel, int humans, MusouRules? rules = null)
    {
        rules ??= MusouRules.Default;
        var rows = new List<WarfrontTuningRow>();
        for (int level = 1; level <= maxLevel; level++)
        {
            var scale = WarfrontOrkScaling.For(level, humans);
            var none = WarfrontStatBonuses.None;
            var bal = WarfrontStatBonuses.For(BalancedCards(level - 1));
            var dmg = WarfrontStatBonuses.For(DamageCards(level - 1));
            var orks = new List<WarfrontTuningOrk>();
            foreach (var (type, name) in Orks)
            {
                var spec = rules.Of(type);
                float hp = spec.Health * scale.Health;
                float hit = (spec.MeleeDamage > 0f ? spec.MeleeDamage : spec.RangedDamage) * scale.Damage;
                orks.Add(new WarfrontTuningOrk(type, name, hp, hit,
                    Ttk(hp, BolterDamage, BolterInterval, none), Ttk(hp, BolterDamage, BolterInterval, bal), Ttk(hp, BolterDamage, BolterInterval, dmg),
                    Ttk(hp, HeavyDamage, HeavyInterval, none),
                    HitsToDown(hit, none), HitsToDown(hit, bal)));
            }
            rows.Add(new WarfrontTuningRow(level, scale.Health, scale.Damage, orks));
        }
        return rows;
    }

    /// <summary>Seconds to kill: shots needed (damage times the card multiplier) at the card-sped interval; the first shot lands at 0.</summary>
    public static double Ttk(float hp, float damage, float interval, WarfrontStatBonuses b)
    {
        double perShot = damage * b.Damage;
        int shots = (int)Math.Ceiling(hp / perShot);
        return (shots - 1) * (interval / b.AttackSpeed);
    }

    public static int HitsToDown(float hit, WarfrontStatBonuses b) =>
        hit <= 0f ? 0 : (int)Math.Ceiling((MarineHealth * b.MaxHp + MarineShield * b.MaxArmor) / hit);
}
