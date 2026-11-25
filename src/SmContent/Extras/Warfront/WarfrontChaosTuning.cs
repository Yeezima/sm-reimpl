using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Warfront.Chaos;
using SmSim.Extras.Warfront.Faction;

namespace SmContent.Extras.Warfront;

/// <summary>
/// The Chaos army's numbers (Chaos Warfront C2; design "Army aliasing"; spec "Warfront Chaos roster"): the Musou type table of a Chaos stage, each aliased Ork row replaced by its Chaos unit's. Every
/// value is ours (kind: ours, confidence: provisional, to tune in playtest), guided by the relative durability of the original co-op blueprints (docs/warfront-chaos-roster.md: renegade 60, Bloodletter
/// 200, Havoc 100 + 500 armour, Tactical 500 + 400, elite 1000 + 1000) and the brief's "fewer but tougher". Bodies (radius, height) are the Ork rows' so the clients' shot and contact tests, which read
/// the Orks' table, still agree with the server's (the Daemon alone is bigger: a Bloodletter is a man-sized daemon, not a Gretchin; C3/C4 take the Chaos table on the client for it).
/// The Chosen (the four Boss Ork rows) and the Chaos Lord (the Warboss row) keep the Orks' numbers but take marine-sized bodies (C5, below); the Ork-only rows (Weirdboy, Bomb Squig, Scrap Mek: never spawned against Chaos) are kept as they are.
/// </summary>
public static class WarfrontChaosTuning
{
    /// <summary>
    /// <paramref name="config"/> for a stage that runs <paramref name="faction"/>: Orks gives the config back unchanged (the Orks' table, <see cref="SimConfig.WarfrontFaction"/> Orks); Chaos sets the faction
    /// and the Chaos type table on the config's Musou rules (the Orks' default when it has none).
    /// </summary>
    public static SimConfig Apply(SimConfig config, WarfrontFaction faction) =>
        faction != WarfrontFaction.Chaos ? config : config with { WarfrontFaction = WarfrontFaction.Chaos, MusouRules = Rules(config.MusouRules ?? MusouRules.Default) };

    /// <summary>The Chaos type table on <paramref name="orks"/> (every other Musou rule kept).</summary>
    public static MusouRules Rules(MusouRules orks)
    {
        var types = orks.Types.ToArray();
        foreach (var row in Rows(orks)) types[(int)row.Type] = row;
        return orks with { Types = types, BossScale = LordBossScale };   // Chaos Warfront C5: the Lord's body is a marine's x1.35 (the Orks' table keeps its Warboss x4.5)
    }

    /// <summary>The replaced rows, each built on the Orks' row of its slot (so its body and any field not named here stay the Orks').</summary>
    public static IEnumerable<MusouTypeSpec> Rows(MusouRules orks)
    {
        MusouTypeSpec Of(MusouEnemyType t) => orks.Of(t);
        // Cultist (the Boy slot): a melee zealot; renegade guard-weight but a fodder that fights (ours).
        yield return Of(MusouEnemyType.Boy) with { Health = CultistHealth, Speed = 5.0f, Points = 15, MeleeDamage = 10f, MeleePeriod = 1.4f };
        // Traitor Guardsman (the Shoota Boy slot): the renegade's lasgun, 2 a shot every 0.1 s and an ideal band of 20-35 m in the data (chaos_coop_lasgun); ours: 3 a round, 4 rounds, hit chances kept.
        yield return Of(MusouEnemyType.ShootaBoy) with
        {
            Health = TraitorHealth, Speed = 4.8f, Points = 15,
            RangedDamage = 3f, RangedRounds = 4, RangedRoundInterval = 0.1f, RangedPeriod = 1.8f, RangedMin = 20f, RangedMax = 35f,
        };
        // Chaos Tactical Marine (the Chaos Marine row; the 'Ard Boy slot spawns it): the bolter's band 15-50 m in the data, ours 15-40 m in the lanes; tougher than Musou Mode's 250.
        yield return Of(MusouEnemyType.ChaosMarine) with { Health = MarineHealth, Points = 60, RangedMin = 15f, RangedMax = 40f };
        // Aspiring Champion (the Nob slot): the army's leader, a heavy staggering swing.
        yield return Of(MusouEnemyType.Nob) with { Health = ChampionHealth, Points = 120, MeleeDamage = 28f, MeleePeriod = 2.0f, MeleeStaggers = true };
        // Havoc, heavy bolter (the Big Shoota Nob slot: the burst and the overheat are its code's).
        yield return Of(MusouEnemyType.BigShootaNob) with { Health = HavocBolterHealth, Points = 160 };
        // Havoc, missile launcher (the Rokkit Boy slot: real rockets, its code's).
        yield return Of(MusouEnemyType.RokkitBoy) with { Health = HavocMissileHealth, Points = 40 };
        // Raptor (the Stormboy slot: the hop and the squad's Hunt order are the jump-pack gap-closer, its code's); a chainsword marine.
        yield return Of(MusouEnemyType.Stormboy) with { Health = RaptorHealth, Points = 40, MeleeDamage = 14f, MeleePeriod = 1.4f };
        // Daemon, Bloodletter (the Gretchin slot, breach-spawned only): 200 in the data (chaos_coop_bloodletter), the hellblade 15 light / 25 heavy at 1-3 m; ours: 15 a strike every 1.2 s, reach 1.8 m.
        yield return Of(MusouEnemyType.Gretchin) with
        {
            Health = DaemonHealth, Speed = 6.0f, Radius = 0.45f, Height = 2.2f, Points = 30,
            MeleeDamage = 15f, MeleePeriod = 1.2f, MeleeRange = 1.8f,
        };
        // Chaos Warfront C5: the Chosen (the four Boss Ork rows) and the Chaos Lord (the Warboss row) get bodies as C3 draws them, marine-rig units at x1.2 and x1.35 of a marine (0.5 m x 2.3 m), not the
        // Boss Orks' x3 and the Warboss's x4.5 (C3 review note: shots hit invisible bodies); their reach follows the body (a marine's 1.3 m past it). Their health, damage and kit are the Orks' rows' still.
        foreach (var kind in MusouBossOrks.Kinds)
            yield return Of(kind) with { Radius = ChosenRadius, Height = ChosenHeight, MeleeRange = ChosenReach };
        yield return Of(MusouEnemyType.Warboss) with { Radius = MarineRadius, Height = MarineBodyHeight, MeleeRange = LordReach };
    }

    /// <summary>
    /// The Chosen's and the Lord's bodies (C5; ours, provisional): a marine's 0.5 m x 2.3 m (C3's drawn marine height); the Chosen x1.2 (0.6 m x 2.76 m, reach 2.2 m); the Lord's row the marine's, scaled by
    /// <see cref="LordBossScale"/> x1.35 through <see cref="MusouRules.BossScale"/> (0.675 m x 3.105 m, as <c>MusouSim.Body</c>, the shot body and the solids scale the Warboss), reach 2.7 m (his 1.15 m past the
    /// players' ring of the Orks' tuning, kept: 0.675 + 2.0).
    /// </summary>
    public const float MarineRadius = 0.5f, MarineBodyHeight = 2.3f, ChosenScale = 1.2f, LordBossScale = 1.35f;
    public const float ChosenRadius = MarineRadius * ChosenScale, ChosenHeight = MarineBodyHeight * ChosenScale, ChosenReach = 2.2f, LordReach = 2.7f;

    /// <summary>The Chaos rows' health (ours, provisional; "fewer but tougher") against the row of each slot: Cultist and Traitor 1.3x the Boyz' 85; Chaos Marine 1.4x Musou's Chaos Marine 250 (2.1x the 'Ard Boy whose slot it takes); Champion 1.25x the Nob's 600; heavy-bolter Havoc 1.17x the Big Shoota Nob's 600; missile Havoc 2.4x the Rokkit Boy's 85; Raptor 1.8x the Stormboy's 120; Daemon 6.7x the Gretchin's 30 (the data's 200).</summary>
    public const float CultistHealth = 110f, TraitorHealth = 110f, MarineHealth = 350f, ChampionHealth = 750f, HavocBolterHealth = 700f, HavocMissileHealth = 200f, RaptorHealth = 220f, DaemonHealth = 200f;

    /// <summary>The startup log's line for a Chaos stage.</summary>
    public static string Note(MusouRules chaos) =>
        "Warfront Chaos roster: " + string.Join(", ", Rows(MusouRules.Default).Select(r => $"{WarfrontChaosRoster.Unit(r.Type)} {chaos.Of(r.Type).Health:0}")) +
        $"; the 'Ard Boy slot fields Chaos Marines, no Weirdboyz, Meks or Squigs; caps x{WarfrontChaosRoster.EnemyScale:0.##} of the Orks' (ours, provisional)";
}
