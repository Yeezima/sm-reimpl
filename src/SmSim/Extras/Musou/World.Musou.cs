using System.Numerics;
using SmSim.Extras.Musou;

namespace SmSim;

/// <summary>
/// Added content "Musou" in the World (design sections A and B): the World's hooks, each returning at once unless <see cref="_musou"/> is set, which only
/// <see cref="StartMusou"/> does and only under <see cref="ContentExtras.Musou"/> (the server calls it from its <c>ExtrasCreated</c> hook). Without it every
/// existing mode runs exactly as before (pinned in <c>MusouIsolationTests</c>). The side is forced (Space Marines only, no balance) and the match flow is
/// one-sided (warm-up, the countdown, play, the end, the restart). MU1: in play the director (<see cref="MusouDirector"/>) runs the waves, the enemies move and
/// attack (<see cref="MusouSim.Step"/>), the players' hit-scan shots hit them (<c>World.MusouCombat.cs</c>), KOs score, and the shared lives pool decides
/// the respawns; every player dead with no life left loses (<see cref="Team.Chaos"/> wins), the Warboss's death wins. MU3 (<c>World.MusouCombat.cs</c>):
/// cones, projectiles, blasts, charges, the cleave and the boss's abilities.
/// </summary>
public sealed partial class World
{
    MusouSim? _musou;
    MusouDirector? _musouDirector;
    readonly List<MusouAttack> _musouAttacks = new();
    readonly List<MusouKill> _musouKills = new();
    MusouTarget[] _musouTargets = [];
    readonly List<int> _musouIds = new();
    readonly List<(float RespawnLeft, int Id)> _musouDead = new();

    /// <summary>The Musou enemies of this match; null unless <see cref="StartMusou"/> ran (every match that is not Musou).</summary>
    public MusouSim? Musou => _musou;
    /// <summary>The Musou director of this match (waves, lives, scores); null unless <see cref="StartMusou"/> ran.</summary>
    public MusouDirector? MusouDirector => _musouDirector;

    /// <summary>Seconds a held respawn waits (the pool is empty or another player takes the last life); short, so it goes the tick a life is free (ours).</summary>
    const float MusouRespawnHold = 0.1f;

    /// <summary>
    /// Makes this match a Musou match with enemies on <paramref name="nav"/> (the server's walking graph; null on the flat ground). Only when the config
    /// runs <see cref="ContentExtras.Musou"/> and the world has match rules; false (and nothing changed) otherwise. A second call keeps the first's.
    /// </summary>
    public bool StartMusou(NavGraph? nav)
    {
        if (!Config.Extras.HasFlag(ContentExtras.Musou) || Rules is null) return false;
        if (_musou is not null) return true;
        _musou = new MusouSim(nav, Config.MusouRules) { TickRate = Config.TickRate };
        _musouDirector = new MusouDirector(_musou, _spawns.Select(s => s.Position).ToList());
        // The Warboss comes out of the Chaos spawn room (owner, 2026-10-31): the spot of the map's Chaos start area nearest its centre.
        int chaosArea = Array.FindIndex(_startAreas, a => a.Side == Team.Chaos);
        if (chaosArea >= 0) _musouDirector.BossSpawn = SpotsIn(chaosArea)[0];
        _musou.Director = _musouDirector.View();
        return true;
    }

    partial void ExtrasStep()
    {
        if (_musou is null || _musouDirector is null) return;
        LibrarianStep();   // Librarian/World.Librarian.cs: a Librarian's powers (nobody else has any)
        if (_tfw is not null) { long whole = SimProfile.Begin(); TfwStep(); SimProfile.End("tfw: whole step", whole); return; }   // Campaign/World.Tfw.cs: the war, not the 5-wave run (musou/perf-pass: SM_TICK_STATS section)
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        var director = _musouDirector;
        MusouBeginTick();   // World.MusouCombat.cs: once a tick (a cleave or a blast earlier in the tick may have begun it)
        MusouArm();   // World.MusouLoadout.cs: anyone not armed yet (also at the end of the match step)
        bool playing = _state == MatchState.Playing;

        // The players this tick, in id order (deterministic).
        int n = 0;
        if (_musouTargets.Length < _players.Count) _musouTargets = new MusouTarget[Math.Max(8, _players.Count * 2)];
        _musouIds.Clear();
        foreach (int id in _players.Keys) _musouIds.Add(id);
        _musouIds.Sort();
        foreach (int id in _musouIds) { var p = _players[id]; _musouTargets[n++] = new MusouTarget(id, p.Position, p.Alive); }
        var targets = _musouTargets.AsSpan(0, n);

        if (playing)
        {
            MusouPlayerShots();   // World.MusouCombat.cs (MU3 takes it over)
            if (director.Phase == MusouPhase.Waiting) director.Start(n);
            director.CountPlayers(n);
        }
        MusouCreditKills();
        // The pool before this tick's attacks: a death now is scored this tick (ResolveDeaths), and a loss it makes is called the next.
        if (playing) MusouLives();
        if (playing && director.Phase is MusouPhase.Wave or MusouPhase.Break or MusouPhase.Boss)
        {
            var change = director.Step(targets);
            if (change.HasFlag(MusouDirectorChange.BreakStarted)) MusouRefill();
        }

        _musouAttacks.Clear();
        bool fight = playing && director.Phase is MusouPhase.Wave or MusouPhase.Break or MusouPhase.Boss;
        _musou.Step(targets, Collision, fight, _musouAttacks);
        foreach (var attack in _musouAttacks) MusouHit(attack);
        MusouBossStep(targets, fight);   // World.MusouCombat.cs: the Warboss's abilities
        MusouCreditKills();   // KOs of this tick's step
        MusouDropsStep();   // World.MusouDrops.cs: pickups (E), expiry

        if (playing && director.Phase == MusouPhase.Won) End(Team.SpaceMarines);
        else if (playing && director.Phase == MusouPhase.Lost) End(Team.Chaos);
        else if (_state == MatchState.Ended && director.Phase == MusouPhase.Lost) MusouHoldRespawns(0);   // nobody comes back on the end screen of a loss

        // Scores: the Space Marines' team score is the KOs (kept under the score limit when it allows one), Chaos has none; ResolveDeaths still adds this
        // tick's marine deaths after this hook, and a core end that brings about is undone in the Musou flow (ExtrasStepMatch), whatever the host's limits.
        int limit = Rules!.ScoreLimit;
        _teamScores[(int)Team.SpaceMarines] = limit > 1 ? Math.Min(director.Kos, limit - 1) : director.Kos;
        _teamScores[(int)Team.Chaos] = 0;
        _musou.Director = director.View();
        _musou.LastStepMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    /// <summary>KOs since the last call: each counts for the director (wave, boss) and, made by a player in the world, as a kill on its score.</summary>
    void MusouCreditKills()
    {
        _musouKills.Clear();
        _musou!.TakeKills(_musouKills);
        foreach (var kill in _musouKills)
        {
            if (_state != MatchState.Playing) continue;
            _musouDirector!.OnKill(kill);
            MusouDropFor(kill);   // World.MusouDrops.cs: a Nob's or Chaos Marine's weapon drop
            if (kill.Player != 0 && _players.ContainsKey(kill.Player))
            {
                var score = _scores.GetValueOrDefault(kill.Player);
                _scores[kill.Player] = score with { Kills = score.Kills + 1 };
            }
        }
    }

    /// <summary>
    /// An enemy's attack on a player: its damage through the player's received-damage multipliers and <see cref="Combat.ApplyDamage(PlayerState, float, float, float, SimConfig)"/>
    /// (shield first), recorded as a hit by nobody (shooter 0) so the usual feedback plays; a death by it is the world's (the hit is not marked as the killing
    /// one, so the kill feed shows no killer). A Nob's strike also staggers.
    /// </summary>
    void MusouHit(MusouAttack attack)
    {
        if (attack.Player < 0) { TfwGuardHit(attack); return; }   // a Guard squad (Campaign/World.Tfw.cs): before the player lookup, which would drop it
        if (!_players.TryGetValue(attack.Player, out var before) || !before.Alive) return;
        if (_tfw is { } run && run.InRange.Contains(attack.Player)) return;   // the owner, 2026-11-02: at the Firing Range the Orks deal no damage
        var type = attack.Melee ? DamageType.Melee : DamageType.Ranged;
        float damage = attack.Damage * _musou!.Rules.PlayerDamageScale * Loadouts.Body(Config, before).Received(type) * DamageTakenScale(attack.Player);   // Musou Mode: an AI Marine takes a tenth
        if (!(damage > 0f)) return;
        var push = before.Position - attack.From;
        var direction = new Vector3(push.X, push.Y, 0f);
        direction = direction.LengthSquared() > 1e-8f ? Vector3.Normalize(direction) : Vector3.UnitY;
        var (after, shieldDamage, healthDamage) = Combat.ApplyDamage(before, damage, 1f, Stims.HealthTaken(Config, before, type), Config);
        _players[attack.Player] = after;
        _hits.Add(new HitEvent(Tick, 0, attack.Player, HitZone.Body, Vector3.Distance(before.Position, attack.From), shieldDamage, healthDamage, Killed: false,
            Melee: attack.Melee, Direction: direction, Armoured: before.Shield > 0f));
        ShieldBroke(attack.Player, before, after, shieldDamage);
        if (after.Alive && attack.Staggers && (attack.Enemy == 0 || EnemiesImpede))   // an enemy's (Enemy above 0) only when the war's rule allows it; the barrage (0) keeps its stagger
            TryReact(0, attack.Player, new ReactionEntry(0f, ReactionKind.Stagger), attack.From, new Vector2(direction.X, direction.Y));
    }

    /// <summary>
    /// Whether an enemy's attack may put a player in a reaction: always in the 5-wave run (as before); in Musou Mode only by <see cref="Extras.Musou.Campaign.TfwCombatRules.EnemiesImpede"/>
    /// (the owner, 2026-11-06: no Ork ever stuns, staggers or knocks a player back; their damage stays).
    /// </summary>
    bool EnemiesImpede => _musou?.War is not { } war || war.EnemiesImpede;

    /// <summary>
    /// A boss's strike (the Warboss's slam or charge) on a living player: its damage, the slam's stagger (a strike without a knockback) and the charge's knockback (the core's stagger with a push, when the hit
    /// landed: an enemy's damage scale of 0 hurts nobody and throws nobody); both only while <see cref="EnemiesImpede"/>.
    /// </summary>
    void MusouBossHit(ushort boss, MusouBossStrike strike)
    {
        if (!_players.TryGetValue(strike.Player, out var before) || !before.Alive) return;
        MusouHit(new MusouAttack(boss, strike.Player, strike.Damage, Melee: true, Staggers: strike.Knockback <= 0f, strike.From));
        if (!EnemiesImpede || strike.Knockback <= 0f) return;
        if (_players[strike.Player] is { Alive: true } after && (after.Health < before.Health || after.Shield < before.Shield))
            TryReact(0, strike.Player, new ReactionEntry(0f, ReactionKind.Stagger, Backward: strike.Knockback, Duration: _musou!.Rules.Boss.ChargeKnockbackSeconds), strike.From, strike.Push);
    }

    /// <summary>Test aid: <paramref name="attack"/> lands as an enemy's landed attack does (<see cref="MusouSim.Step"/>'s list); nothing without Musou.</summary>
    public void MusouLandAttack(MusouAttack attack) { if (_musou is not null) MusouHit(attack); }

    /// <summary>Test aid: boss <paramref name="boss"/>'s <paramref name="strike"/> lands as one of its abilities' does; nothing without Musou.</summary>
    public void MusouLandBossStrike(ushort boss, MusouBossStrike strike) { if (_musou is not null) MusouBossHit(boss, strike); }

    /// <summary>
    /// The shared lives pool: each respawn this tick takes a life (the core places it, in the Space Marines' spawn room); while the pool cannot cover every
    /// dead player the rest wait (the soonest first); every player dead with no life left loses.
    /// </summary>
    void MusouLives()
    {
        var director = _musouDirector!;
        if (director.Phase is not (MusouPhase.Wave or MusouPhase.Break or MusouPhase.Boss)) return;
        for (int k = 0; k < _respawns.Count; k++)
        {
            int id = _respawns[k];
            if (!_players.TryGetValue(id, out var p) || !p.Alive) continue;
            if (director.Lives == 0)
            {
                // The core respawned someone the hold did not catch (a respawn time under a tick, a death with the timer already short): back to dead, held.
                _players[id] = Combat.Killed(p, Config) with { RespawnLeft = MusouRespawnHold };
                _respawns.RemoveAt(k--);
                continue;
            }
            director.SpendLife();   // where: the core's, the Space Marines' spawn room (owner, 2026-10-31)
        }
        MusouHoldRespawns(director.Lives);
        bool anyAlive = false;
        foreach (var p in _players.Values) anyAlive |= p.Alive;
        if (_players.Count > 0 && !anyAlive && director.Lives == 0) director.Lose();
    }

    /// <summary>Lets at most <paramref name="lives"/> dead players' timers run out (the soonest first, ties by id); the others wait at the hold.</summary>
    void MusouHoldRespawns(int lives)
    {
        _musouDead.Clear();
        foreach (var (id, p) in _players) if (!p.Alive) _musouDead.Add((p.RespawnLeft, id));
        if (_musouDead.Count <= lives) return;
        _musouDead.Sort();   // the soonest first, ties by id
        for (int k = Math.Max(0, lives); k < _musouDead.Count; k++)
        {
            int id = _musouDead[k].Id;
            if (_musouDead[k].RespawnLeft < MusouRespawnHold) _players[id] = _players[id] with { RespawnLeft = MusouRespawnHold };
        }
    }

    /// <summary>
    /// The break's refill: every living player's health and shield back to full, and the reserve of each weapon it carries (clips as they are). A slot the
    /// Musou loadout left empty stays empty, and grenades are not given back (it spawns without them).
    /// </summary>
    void MusouRefill()
    {
        _musouIds.Clear();
        foreach (int id in _players.Keys) _musouIds.Add(id);
        _musouIds.Sort();
        foreach (int id in _musouIds)
        {
            var p = _players[id];
            if (!p.Alive) continue;
            var body = Loadouts.Body(Config, p);
            var inHand = Loadouts.Weapon(Config, p, p.Slot);
            var other = Loadouts.Weapon(Config, p, Loadouts.Other(p.Slot));
            var third = Loadouts.Weapon(Config, p, Loadouts.Third(p.Slot));
            _players[id] = p with
            {
                Health = body.HealthMax, Shield = body.ShieldMax,
                Reserve = inHand?.ReserveSize ?? p.Reserve, OtherReserve = other?.ReserveSize ?? p.OtherReserve, ThirdReserve = third?.ReserveSize ?? p.ThirdReserve,
            };
        }
    }

    partial void ExtrasSampled(int id, in PlayerInput input) => LibrarianSampled(id, input);

    partial void ExtrasPlayerInput(int id, ref PlayerInput input, ref bool handled)
    {
        if (_tfw is null) return;
        TfwPlayerInput(id, ref input, ref handled);   // Campaign/World.Tfw.cs: the AI Marines have no client
    }

    partial void ExtrasMelee(int attacker, PlayerState state, MeleeStep step)
    {
        if (_musou is null) return;
        int librarian = LibrarianMeleeBegin(attacker);   // Librarian/World.Librarian.cs: a swing that strikes takes stress off
        MusouCleave(attacker, state, step);   // World.MusouCombat.cs
        TfwMekMelee(attacker, state, step);   // Campaign/World.TfwMeks.cs: the contraptions (expansion S4)
        LibrarianMeleeEnd(attacker, librarian);
    }

    partial void ExtrasBlast(int owner, Vector3 point, SplashSpec splash, bool melee)
    {
        if (_musou is null) return;
        MusouBlast(owner, point, splash, melee);   // World.MusouCombat.cs
        TfwMekBlast(owner, point, splash);   // Campaign/World.TfwMeks.cs: the contraptions (expansion S4)
    }

    partial void ExtrasDamageTaken(int target, ref float scale)
    {
        if (_tfw is null) return;
        TfwDamageTaken(target, ref scale);   // Campaign/World.TfwSupply.cs: the AI Marines take a tenth
    }

    partial void ExtrasJoinSide(ref Team want, ref bool forced)
    {
        if (_musou is null) return;
        // Players are Space Marines only (lead's default for Musou): every join is a loyalist, without the balance or the per-side limit.
        want = Team.SpaceMarines;
        forced = true;
    }

    /// <summary>
    /// The one-sided match flow: the core's needs both sides, so Musou runs its own. Warm-up until a Space Marine is in, then the countdown (as the core's,
    /// without attacks), then play until the director's end (the Warboss dead: won; every player dead with no life left: lost). Every player gone sends it
    /// back to warm-up; an ended match restarts after the rules' delay, as the core's. Each new round clears the enemies and the director.
    /// </summary>
    partial void ExtrasStepMatch(ref bool handled)
    {
        if (_musou is null) return;
        handled = true;
        if (_tfw is not null && TfwStepMatch()) return;   // Campaign/World.Tfw.cs: the war's own flow, once it has one
        bool marines = CountSide(Team.SpaceMarines) > 0;
        switch (_state)
        {
            case MatchState.Warmup:
                if (marines) { MusouNewRound(); MusouRearm(); BeginRound(respawnWithoutCountdown: false); }
                break;
            case MatchState.Countdown:
                if (!marines) { _state = MatchState.Warmup; TicksLeft = 0; }
                else if (--TicksLeft == 0)
                {
                    _projectiles.Clear(); _areas.Clear(); _perkBlasts.Clear();
                    CountProjectilesOut();
                    StartRound(respawn: false);
                }
                break;
            case MatchState.Playing:
                if (!marines) { _state = MatchState.Warmup; TicksLeft = 0; MusouNewRound(); }
                else TicksLeft = uint.MaxValue;   // no time limit, whatever the host's (only the director ends the match)
                break;
            case MatchState.Ended:
                if (_musouDirector!.Phase is MusouPhase.Wave or MusouPhase.Break or MusouPhase.Boss)
                {
                    // The core's score limit ended it this tick (a host limit at or under the KOs or this tick's deaths): only the director ends a Musou match.
                    _state = marines ? MatchState.Playing : MatchState.Warmup;
                    Winner = Team.None;
                    TicksLeft = marines ? uint.MaxValue : 0;
                    if (!marines) MusouNewRound();
                    break;
                }
                if (Tick > _endedTick && TicksLeft > 0) TicksLeft--;
                if (TicksLeft == 0)
                {
                    _state = MatchState.Warmup;
                    Winner = Team.None;
                    Array.Clear(_teamScores);
                    ResetPoints();
                    MusouNewRound();
                    MusouRearm();
                    if (marines) BeginRound(respawnWithoutCountdown: true);
                }
                break;
        }
        MusouArm();   // World.MusouLoadout.cs: every new life with the pistol alone
        // As the snapshot after this tick shows it: no time limit from the tick play begins, and no Chaos score (ResolveDeaths added this tick's deaths).
        if (_state == MatchState.Playing) TicksLeft = uint.MaxValue;
        _teamScores[(int)Team.Chaos] = 0;
    }

    void MusouNewRound()
    {
        _musou!.Clear();
        MusouClearDrops();   // World.MusouDrops.cs
        _musouDirector!.Reset();
        _musou.Director = _tfw is null ? _musouDirector.View() : default;   // the war has no 5-wave director to show
    }
}
