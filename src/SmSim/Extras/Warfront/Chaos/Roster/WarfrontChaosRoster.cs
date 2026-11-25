using SmSim.Extras.Musou;

namespace SmSim.Extras.Warfront.Chaos;

/// <summary>
/// The Chaos army of a Warfront battle (Chaos Warfront C2, owner decision 2026-11-25; design "Army aliasing"; added content, ours). A Chaos stage runs the same enemy sim as an Orks stage: each Ork
/// kind's row of the type table is replaced by a Chaos unit's (<c>SmContent.Extras.Warfront.WarfrontChaosTuning</c>), so the type bytes on the wire stay the same and the Hub's faction byte tells the
/// clients to draw Chaos. This object is attached to the run's <see cref="WarfrontBattleRules.ChaosRoster"/> only when the stage's config says Chaos (<see cref="SimConfig.WarfrontFaction"/>); an
/// Orks stage never has one, so every hook below returns at once there and Orks battles stay as they were by construction.
/// <list type="bullet">
/// <item>The kind map at the spawn chokepoint (<see cref="Alias"/>): the 'Ard Boy slot becomes the Chaos Marine, a Gretchin outside a warp breach becomes a Cultist (the Daemon is breach-spawned
/// only, <see cref="MusouSim.SpawnDaemon"/>), and the Ork-only kinds (Weirdboy, Scrap Mek, Bomb Squig) are refused.</item>
/// <item>The Ork-only spawners (the Weirdboyz, the Scrap Meks) are off (<see cref="OrkSpawnerOn"/>); the Squigs are already off in every Warfront battle (<see cref="WarfrontRules.NoSquigs"/>).</item>
/// <item>Pressure, not headcount: the battle's caps of enemies alive are <see cref="EnemyScale"/> of the Orks'.</item>
/// <item>The damage gate (<see cref="Shield"/>, <see cref="AcceptOnly"/>): an enemy may refuse every hit, stagger and throw, or accept them from one attacker alone (C5's leashed and duelling Lord).</item>
/// </list>
/// </summary>
public sealed class WarfrontChaosRoster
{
    /// <summary>The Chaos army's numbers against the Orks': the caps of enemies alive (crowds and swarms, the trickle, the armies' real units, the HQ defence) are this share of the Orks' (design: "about 0.6 of the Ork numbers, with tougher units"; ours, provisional).</summary>
    public const float EnemyScale = 0.6f;

    /// <summary>The kind <see cref="Alias"/> gives an Ork-only kind: outside the type table, so <see cref="MusouSim.Spawn"/> refuses it (returns 0) and nothing spawns.</summary>
    public const MusouEnemyType Refused = (MusouEnemyType)byte.MaxValue;

    /// <summary>
    /// The kind an enemy of <paramref name="ork"/> spawns as in a Chaos battle: the 'Ard Boy slot as the Chaos Marine (the army's tactical marine), a Gretchin as a Cultist unless it is a breach's Daemon
    /// (<paramref name="daemon"/>), the Weirdboy, the Scrap Mek and the Bomb Squig <see cref="Refused"/>; every other kind keeps its number (its row is the Chaos unit's).
    /// </summary>
    public static MusouEnemyType Alias(MusouEnemyType ork, bool daemon) => ork switch
    {
        MusouEnemyType.ArdBoy => MusouEnemyType.ChaosMarine,
        MusouEnemyType.Gretchin => daemon ? MusouEnemyType.Gretchin : MusouEnemyType.Boy,
        MusouEnemyType.Weirdboy or MusouEnemyType.ScrapMek or MusouEnemyType.BombSquig => Refused,
        _ => ork,
    };

    /// <summary>The kinds a Chaos battle may field (every other kind is an Ork's and never spawns there): the aliased rows and the Chaos Marine. The Gretchin row is the breach's Daemon.</summary>
    public static bool Fields(MusouEnemyType kind) => kind is MusouEnemyType.Boy or MusouEnemyType.ShootaBoy or MusouEnemyType.ChaosMarine or MusouEnemyType.Nob or MusouEnemyType.BigShootaNob
        or MusouEnemyType.RokkitBoy or MusouEnemyType.Stormboy or MusouEnemyType.Gretchin or MusouEnemyType.BossBoy or MusouEnemyType.BossShootaBoy or MusouEnemyType.BossArdBoy
        or MusouEnemyType.BossNob or MusouEnemyType.Warboss;

    /// <summary>Whether a battle's own spawner of <paramref name="kind"/> runs against Chaos: the Weirdboyz and the Scrap Meks do not (Ork-only units; no Chaos alias).</summary>
    public static bool OrkSpawnerOn(MusouEnemyType kind) => kind is not (MusouEnemyType.Weirdboy or MusouEnemyType.ScrapMek or MusouEnemyType.BombSquig);

    /// <summary>The kind whose rule rows a Chaos unit follows in the tables keyed by kind (target class, powerup and weapon drops, Librarian tier, the crowds' big test): the Chaos Marine of the 'Ard Boy slot follows the 'Ard Boy's (C2 review R3; ours); every other kind its own.</summary>
    public static MusouEnemyType RuleKind(MusouEnemyType kind) => kind == MusouEnemyType.ChaosMarine ? MusouEnemyType.ArdBoy : kind;

    /// <summary>The Chaos fodder a weapon's own blast (the autocannon's) kills in one, as it kills the Ork fodder in those slots (the owner's rule, 2026-11-11; by kind, C2 review R4): Cultists and Traitor Guardsmen (and the Gretchin row, a Daemon, is not fodder).</summary>
    public static bool BlastFodder(MusouEnemyType kind) => kind is MusouEnemyType.Boy or MusouEnemyType.ShootaBoy;

    /// <summary>The Chaos unit a kind's row stands for (the log, the tests and C3's labels); "-" for an Ork-only kind.</summary>
    public static string Unit(MusouEnemyType kind) => kind switch
    {
        MusouEnemyType.Boy => "Cultist",
        MusouEnemyType.ShootaBoy => "Traitor Guardsman",
        MusouEnemyType.ChaosMarine => "Chaos Tactical Marine",
        MusouEnemyType.Nob => "Aspiring Champion",
        MusouEnemyType.BigShootaNob => "Havoc (heavy bolter)",
        MusouEnemyType.RokkitBoy => "Havoc (missile launcher)",
        MusouEnemyType.Stormboy => "Raptor",
        MusouEnemyType.Gretchin => "Daemon (Bloodletter)",
        MusouEnemyType.BossBoy or MusouEnemyType.BossShootaBoy or MusouEnemyType.BossArdBoy or MusouEnemyType.BossNob => "Chosen",
        MusouEnemyType.Warboss => "Chaos Lord",
        _ => "-",
    };

    /// <summary>Set by <see cref="MusouSim.SpawnDaemon"/> for the one spawn it makes: the Gretchin row is then the breach's Daemon, not a Cultist.</summary>
    internal bool DaemonPending;

    /// <summary>Spawns whose kind the map changed, and spawns it refused (an Ork-only kind), since the start (the log, the tests).</summary>
    public int Substituted { get; internal set; }
    public int Refusals { get; internal set; }

    readonly int[] _spawned = new int[256];
    /// <summary>Enemies of <paramref name="kind"/> spawned in this run so far (after the map; the tests' census).</summary>
    public int Spawned(MusouEnemyType kind) => _spawned[(byte)kind];
    internal void Count(MusouEnemyType kind) => _spawned[(byte)kind]++;

    readonly HashSet<ushort> _shielded = new();
    readonly Dictionary<ushort, int> _only = new();

    /// <summary>Enemy <paramref name="id"/> refuses every hit, stagger and throw (<paramref name="on"/>), or takes them again. Forgotten when the slot is taken by a new enemy.</summary>
    public void Shield(ushort id, bool on) { if (on) _shielded.Add(id); else _shielded.Remove(id); }

    /// <summary>Enemy <paramref name="id"/> takes hits, staggers and throws from <paramref name="attacker"/> alone (a player id above 0), or from anyone again (0). Forgotten when the slot is taken by a new enemy.</summary>
    public void AcceptOnly(ushort id, int attacker) { if (attacker > 0) _only[id] = attacker; else _only.Remove(id); }

    /// <summary>Whether a hit, stagger or throw by <paramref name="attacker"/> (0: unknown, a source without a player) lands on enemy <paramref name="id"/>.</summary>
    public bool Admits(ushort id, int attacker) => !_shielded.Contains(id) && (!_only.TryGetValue(id, out int only) || only == attacker);

    /// <summary>A new enemy took slot <paramref name="id"/>: the gate of the one before is forgotten.</summary>
    internal void Took(ushort id, MusouEnemyType kind)
    {
        _shielded.Remove(id);
        _only.Remove(id);
        Count(kind);
    }
}
