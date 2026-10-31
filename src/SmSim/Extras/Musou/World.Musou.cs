using System.Numerics;
using SmSim.Extras.Musou;

namespace SmSim;

/// <summary>
/// Added content "Musou" in the World (design sections A and B): the World's hooks, each returning at once unless <see cref="_musou"/> is set, which only
/// <see cref="StartMusou"/> does and only under <see cref="ContentExtras.Musou"/> (the server calls it from its <c>ExtrasCreated</c> hook). Without it every
/// existing mode runs exactly as before (pinned in <c>MusouIsolationTests</c>). The side is forced (Space Marines only, no balance) and the match flow is
/// one-sided (warm-up, the countdown, play, the end, the restart). MU1: in play the director (<see cref="MusouDirector"/>) runs the waves, the enemies move and
/// attack (<see cref="MusouSim.Step"/>), the players' hit-scan shots hit them (<c>World.MusouCombat.cs</c>), KOs score, and the shared lives pool decides
/// the respawns; every player dead with no life left loses (<see cref="Team.Chaos"/> wins), the Warboss's death wins. MU3: cones, projectiles, blasts, the
/// cleave and the boss's abilities.
/// </summary>
public sealed partial class World
{
    MusouSim? _musou;
    MusouDirector? _musouDirector;
    readonly List<MusouAttack> _musouAttacks = new();
    readonly List<MusouKill> _musouKills = new();
    MusouTarget[] _musouTargets = [];

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
        _musou.Director = _musouDirector.View();
        return true;
    }

    partial void ExtrasStep()
    {
        if (_musou is null || _musouDirector is null) return;
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        var director = _musouDirector;
        _musou.BeginTick(Tick);
        bool playing = _state == MatchState.Playing;

        // The players this tick, in id order (deterministic).
        int n = 0;
        if (_musouTargets.Length < _players.Count) _musouTargets = new MusouTarget[Math.Max(8, _players.Count * 2)];
        foreach (int id in _players.Keys.Order()) { var p = _players[id]; _musouTargets[n++] = new MusouTarget(id, p.Position, p.Alive); }
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
        _musou.Step(targets, Collision, fight: playing && director.Phase is MusouPhase.Wave or MusouPhase.Break or MusouPhase.Boss, _musouAttacks);
        foreach (var attack in _musouAttacks) MusouHit(attack);
        MusouCreditKills();   // KOs of this tick's step (none in MU1; MU3's reactions may add some)

        if (playing && director.Phase == MusouPhase.Won) End(Team.SpaceMarines);
        else if (playing && director.Phase == MusouPhase.Lost) End(Team.Chaos);
        else if (_state == MatchState.Ended && director.Phase == MusouPhase.Lost) MusouHoldRespawns(0);   // nobody comes back on the end screen of a loss

        // Scores: the Space Marines' team score is the KOs; marine deaths never feed a Chaos score that could end the match (ResolveDeaths adds this tick's
        // deaths after this hook, and both stay below the score limit).
        int limit = Math.Max(1, Rules!.ScoreLimit - 1);
        _teamScores[(int)Team.SpaceMarines] = Math.Min(director.Kos, limit);
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
        if (!_players.TryGetValue(attack.Player, out var before) || !before.Alive) return;
        var type = attack.Melee ? DamageType.Melee : DamageType.Ranged;
        float damage = attack.Damage * _musou!.Rules.PlayerDamageScale * Loadouts.Body(Config, before).Received(type);
        if (!(damage > 0f)) return;
        var push = before.Position - attack.From;
        var direction = new Vector3(push.X, push.Y, 0f);
        direction = direction.LengthSquared() > 1e-8f ? Vector3.Normalize(direction) : Vector3.UnitY;
        var (after, shieldDamage, healthDamage) = Combat.ApplyDamage(before, damage, 1f, Stims.HealthTaken(Config, before, type), Config);
        _players[attack.Player] = after;
        _hits.Add(new HitEvent(Tick, 0, attack.Player, HitZone.Body, Vector3.Distance(before.Position, attack.From), shieldDamage, healthDamage, Killed: false,
            Melee: attack.Melee, Direction: direction, Armoured: before.Shield > 0f));
        ShieldBroke(attack.Player, before, after, shieldDamage);
        if (after.Alive && attack.Staggers)
            TryReact(0, attack.Player, new ReactionEntry(0f, ReactionKind.Stagger), attack.From, new Vector2(direction.X, direction.Y));
    }

    /// <summary>
    /// The shared lives pool: each respawn this tick takes a life and moves the player to the spawn farthest from the horde; while the pool cannot cover every
    /// dead player the rest wait (the soonest first); every player dead with no life left loses.
    /// </summary>
    void MusouLives()
    {
        var director = _musouDirector!;
        if (director.Phase is not (MusouPhase.Wave or MusouPhase.Break or MusouPhase.Boss)) return;
        foreach (int id in _respawns)
        {
            if (!_players.TryGetValue(id, out var p) || !p.Alive) continue;
            director.SpendLife();
            if (MusouSafestSpawn() is { } spawn) _players[id] = p with { Position = spawn.Position, Yaw = spawn.Yaw };
        }
        MusouHoldRespawns(director.Lives);
        bool anyAlive = false;
        foreach (var p in _players.Values) anyAlive |= p.Alive;
        if (_players.Count > 0 && !anyAlive && director.Lives == 0) director.Lose();
    }

    /// <summary>Lets at most <paramref name="lives"/> dead players' timers run out (the soonest first, ties by id); the others wait at the hold.</summary>
    void MusouHoldRespawns(int lives)
    {
        var dead = _players.Values.Where(p => !p.Alive).OrderBy(p => p.RespawnLeft).ThenBy(p => p.PlayerId).ToList();
        for (int k = lives; k < dead.Count; k++)
            if (dead[k].RespawnLeft < MusouRespawnHold) _players[dead[k].PlayerId] = dead[k] with { RespawnLeft = MusouRespawnHold };
    }

    /// <summary>The map spawn whose nearest enemy is farthest (ties: the first); null without spawns or enemies.</summary>
    SpawnPoint? MusouSafestSpawn()
    {
        if (_spawns.Length == 0 || _musou!.Alive == 0) return null;
        int best = -1; float far = -1f;
        for (int k = 0; k < _spawns.Length; k++)
        {
            float d = _musou.NearestSquared(_spawns[k].Position);
            if (d > far) { far = d; best = k; }
        }
        return best >= 0 ? _spawns[best] : null;
    }

    /// <summary>The break's refill: every living player's health, shield, reserves and grenades back to full (clips as they are).</summary>
    void MusouRefill()
    {
        foreach (int id in _players.Keys.Order().ToList())
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
                Grenades = Math.Max(p.Grenades, Loadouts.Equipment(Config, p)?.SpawnCount ?? 0),
            };
        }
    }

    partial void ExtrasMelee(int attacker, PlayerState state, MeleeStep step)
    {
        if (_musou is null) return;
        // MU3: the cleave (every enemy within MeleeRange + 0.5 m and cone x1.5 takes step.Damage, up to 8).
    }

    partial void ExtrasBlast(int owner, Vector3 point, SplashSpec splash, bool melee)
    {
        if (_musou is null) return;
        // MU3: the blast on the enemies within its radius.
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
        bool marines = CountSide(Team.SpaceMarines) > 0;
        switch (_state)
        {
            case MatchState.Warmup:
                if (marines) { MusouNewRound(); BeginRound(respawnWithoutCountdown: false); }
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
                break;
            case MatchState.Ended:
                if (Tick > _endedTick && TicksLeft > 0) TicksLeft--;
                if (TicksLeft == 0)
                {
                    _state = MatchState.Warmup;
                    Winner = Team.None;
                    Array.Clear(_teamScores);
                    ResetPoints();
                    MusouNewRound();
                    if (marines) BeginRound(respawnWithoutCountdown: true);
                }
                break;
        }
    }

    void MusouNewRound()
    {
        _musou!.Clear();
        _musouDirector!.Reset();
        _musou.Director = _musouDirector.View();
    }
}
