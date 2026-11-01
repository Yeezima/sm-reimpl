using System.Numerics;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmSim;

/// <summary>
/// Musou Mode in the World (TF0: the contract and stubs; the owner's decision of 2026-11-01: this war is Musou Mode). <see cref="World.Musou"/> and its pool run a war
/// too; <see cref="_tfw"/>, set only by <see cref="StartTfw"/>, sends the World's Musou hooks here instead of the 5-wave director, which stays reachable as a test aid
/// (<c>--map mp_tdm_09</c>). Without a war every method here returns at once and the Skirmish replay is unchanged (<c>MusouSkirmishPinTests</c>).
/// /// <c>_tfw!.RetainerOf(id) is { } m &amp;&amp; _players.TryGetValue(m.Id, out var p) &amp;&amp; p.Alive</c>.
/// </summary>
public sealed partial class World
{
    TfwRun? _tfw;
    int _tfwKos;

    /// <summary>The war of this match; null unless <see cref="StartTfw"/> ran (every other match, the 5-wave run included).</summary>
    public TfwRun? Tfw => _tfw;

    /// <summary>
    /// Makes this match the war (a Musou match, <see cref="StartMusou"/>, whose hooks go to the war). False, and nothing changed, under the same conditions as
    /// <see cref="StartMusou"/>; a second call keeps the first's.
    /// </summary>
    public bool StartTfw(NavGraph? nav, TfwBattlefield field)
    {
        if (!StartMusou(nav)) return false;
        _tfw ??= new TfwRun(field, new TfwSites(field, nav, Collision));
        _musou!.Swarm = MusouSwarmRules.Default;   // the 5-wave run never gets it (MusouSkirmishPinTests)
        _musou!.WidenBox(field.Hub.Box);   // the Stormbird lies outside the graph
        _musou!.WidenBox(field.Bounds);   // a jump goes above it
        _musou!.Director = default;   // no 5-wave director to show (nothing is sent until the first deploy: TfwShowDirector)
        return true;
    }

    /// <summary>The input of an AI Marine (<see cref="TfwRun.PuppetBrain"/>; none: it stands still, keeping its heading). Other players stay with the server's input source.</summary>
    void TfwPlayerInput(int id, ref PlayerInput input, ref bool handled)
    {
        if (!TfwContracts.IsPuppet(id) || !_players.TryGetValue(id, out var puppet)) return;
        input = _tfw!.PuppetBrain?.Invoke(id, puppet) ?? new PlayerInput(0, 0f, 0f, puppet.Yaw, InputButtons.None);
        handled = true;
    }

    /// <summary>
    /// The war's step, in place of the 5-wave step (from the <c>ExtrasStep</c> hook, inside <see cref="Step"/>, so a respawn here reaches <see cref="LastRespawns"/>): the run in play, the
    /// fight, the boss's abilities, the AI Marines, the drops, what the clients see, and the end the run came to.
    /// </summary>
    void TfwStep()
    {
        var run = _tfw!;
        MusouBeginTick();
        MusouArm();
        TfwCreditKills();   // the strikes and blasts earlier in the tick: a battle may end below
        TfwBoxAmmo();   // World.TfwArsenal.cs: last tick's ammunition boxes reach the weapons in the bank
        if (_state == MatchState.Playing && !run.Idle) run.Step(this);
        else if (!run.Idle) run.Commands(null, Config.TickRate);
        TfwGuardsStep();   // World.TfwGuards.cs
        TfwBossStep();
        TfwPuppetsStep();
        MusouDropsStep();   // World.MusouDrops.cs: pickups (E), expiry
        TfwSlotsStep();   // World.TfwArsenal.cs: a key pressed during a strike waits for its end
        run.HoldRespawns(this);
        if (run.Ended) run.Score.Bank(run.Phase == TfwRunPhase.Won ? TfwMapResult.Won : TfwMapResult.Lost);   // the run's end banks the map under way, however it came
        if (_state == MatchState.Playing && run.Phase == TfwRunPhase.Won) End(Team.SpaceMarines);
        else if (_state == MatchState.Playing && run.Phase == TfwRunPhase.Lost) End(Team.Chaos);
        TfwShowDirector();
    }

    /// <summary>From the first deploy the clients see the Campaign phase (tag 5: the boss bar, the box); until then nothing, so a server with nobody deployed sends nothing.</summary>
    void TfwShowDirector()
    {
        var run = _tfw!;
        var musou = _musou!;
        int limit = Rules!.ScoreLimit;
        _teamScores[(int)Team.SpaceMarines] = limit > 1 ? Math.Min(_tfwKos, limit - 1) : _tfwKos;
        _teamScores[(int)Team.Chaos] = 0;
        if (!run.Deployed) return;
        ushort boss = run.Battle?.BossId ?? 0;
        float health = boss != 0 && musou.TryGet(boss, out var e) ? e.Health : 0f;
        ushort alive = (ushort)Math.Clamp(musou.Alive, 0, ushort.MaxValue);
        musou.Director = new MusouDirectorView(0, 0, MusouPhase.Campaign, 0, alive, alive, boss, health, run.Battle?.BossMaxHealth ?? 0f, 0, musou.Box);
    }

    /// <summary>
    /// The credit of the KOs, and the battle's count of them: a Guardsman's KO names no player, so <c>orksKilled</c> reads what <see cref="MusouSim.TakeKills"/> gave (<see cref="MusouCreditKills"/>
    /// leaves it in its list). In play each KO may leave a box (owner's item 3) and a human's scores (item 17).
    /// </summary>
    void TfwCreditKills()
    {
        MusouCreditKills();
        foreach (var kill in _musouKills)
        {
            _tfw!.Battle?.NoteKill(kill);
            if (kill.Player != 0) _tfwKos++;
            if (_state != MatchState.Playing || _tfw.Battle is null) continue;
            TfwDropBox(kill);
            if (!TfwContracts.IsPuppet(kill.Player) && _players.ContainsKey(kill.Player)) _tfw.Score.Note(kill, _musou!.Rules, Config.TickRate, Tick);
        }
    }

    /// <summary>
    /// The abilities of the battle's boss (the commander, the Warboss), after the fight. <see cref="MusouBossStep"/> will not do here: it needs the 5-wave director and takes every target
    /// that is no player for dead, so the boss would ignore the squads. A strike on a squad goes to <see cref="TfwGuardHit"/> through <see cref="MusouHit"/>; the Waaagh!s' Boyz come from the
    /// sources.
    /// </summary>
    void TfwBossStep()
    {
        var battle = _tfw!.Battle;
        if (battle is null || battle.BossId == 0) { _musouBoss = null; return; }
        var guards = TfwGuards!;
        int size = _players.Count + GuardSim.MaxSquads;
        if (_musouTargets.Length < size) _musouTargets = new MusouTarget[size * 2];
        _musouIds.Clear();
        foreach (int id in _players.Keys) _musouIds.Add(id);
        _musouIds.Sort();
        int n = 0;
        foreach (int id in _musouIds) { var p = _players[id]; _musouTargets[n++] = new MusouTarget(id, p.Position, p.Alive); }
        n += guards.AppendTargets(_musouTargets.AsSpan(n));
        if (_musouBoss is null || _musouBoss.Id != battle.BossId)
            _musouBoss = new MusouBoss(_musou!, null, battle.BossId, battle.BossMaxHealth, Config.CapsuleRadius);
        _musouBossStrikes.Clear();
        _musouBoss.Step(_musouTargets.AsSpan(0, n), _state == MatchState.Playing, _musouBossStrikes);
        foreach (var strike in _musouBossStrikes)
        {
            if (TfwContracts.IsGuardTarget(strike.Player)) { MusouHit(new MusouAttack(_musouBoss.Id, strike.Player, strike.Damage, true, false, strike.From)); continue; }
            if (!_players.TryGetValue(strike.Player, out var before) || !before.Alive) continue;
            MusouHit(new MusouAttack(_musouBoss.Id, strike.Player, strike.Damage, Melee: true, Staggers: strike.Knockback <= 0f, strike.From));
            if (strike.Knockback > 0f && _players[strike.Player] is { Alive: true } after && (after.Health < before.Health || after.Shield < before.Shield))
                TryReact(0, strike.Player, new ReactionEntry(0f, ReactionKind.Stagger, Backward: strike.Knockback, Duration: _musou!.Rules.Boss.ChargeKnockbackSeconds), strike.From, strike.Push);
        }
        battle.NoteWaaaghs(_musouBoss.Waaaghs);
    }

    /// <summary>The field is empty: no enemy, Guardsman, drop or AI Marine (recoveries and orders stay). A new round does not clear the Guardsmen by itself.</summary>
    internal void TfwClearField()
    {
        _musou!.Clear();   // the enemies fire no projectile: their rounds are rolled hits (MusouSim.Fire)
        TfwGuards!.Clear();
        _projectiles.Clear(); _areas.Clear(); _perkBlasts.Clear();   // the players' own: nothing of this battle lands in the next or in the Stormbird
        CountProjectilesOut();
        MusouClearDrops();
        ClearPickups();   // the boxes stay with their battle (no events: the clients drop what the next message no longer lists)
        TfwDismissMarines();
        _musouBoss = null;
    }

    /// <summary>Whether <paramref name="id"/>'s retainer stands and can revive it.</summary>
    internal bool TfwRetainerUp(int id) => _tfw!.RetainerOf(id) is { } m && _players.TryGetValue(m.Id, out var p) && p.Alive;

    /// <summary>A dead player waits (the core's timer would bring it back in the Stormbird).</summary>
    internal void TfwHoldRespawn(int id)
    {
        if (_players.TryGetValue(id, out var p) && !p.Alive && p.RespawnLeft < TfwBattleRules.RespawnHold) _players[id] = p with { RespawnLeft = TfwBattleRules.RespawnHold };
    }

    /// <summary>Test aid: a new run at once, as the end of the last one brings.</summary>
    public void TfwNewRunForTests() { if (_tfw is not null) { TfwNewRun(); MusouRearm(); } }

    /// <summary>Test aid: player <paramref name="id"/> dies where it stands; false outside a war, for an unknown or dead player.</summary>
    public bool TfwKillForTests(int id)
    {
        if (_tfw is null || !_players.TryGetValue(id, out var p) || !p.Alive) return false;
        _players[id] = Combat.Killed(p, Config);
        return true;
    }

    /// <summary>Test aid (<c>SM_TFW_WIN_TEST</c>): every enemy within <paramref name="radius"/> metres of <paramref name="at"/> falls, credited to <paramref name="by"/>, the boss too; how many. Nothing outside a war.</summary>
    public int TfwSmiteForTests(int by, Vector3 at, float radius)
    {
        if (_tfw is null || _musou is not { } enemies) return 0;
        var near = new ushort[MusouSim.Capacity];
        int n = enemies.Query(at, radius, near), fell = 0;
        for (int k = 0; k < n; k++)
            if (enemies.TryGet(near[k], out var e) && e.Alive && enemies.Damage(near[k], by, e.Health, Vector3.Zero, out bool killed, marker: 0) && killed) fell++;
        return fell;
    }

    /// <summary>Test aid (<c>SM_TFW_STRESS</c>): tops the enemies up to <paramref name="orks"/> in a ring round the landing zone and the Guard squads up to <paramref name="squads"/>. Nothing outside a battle.</summary>
    public void TfwFillForTests(int orks, int squads)
    {
        if (_tfw?.Battle is not { } battle || _musou is not { } enemies || TfwGuards is not { } guards) return;
        var lz = battle.Layout.LandingZone;
        var nav = enemies.Nav;
        float boy = enemies.Rules.Of(MusouEnemyType.Boy).Health;
        for (int k = 0; enemies.Alive < orks && k < orks; k++)
        {
            float angle = k * 2.399963f, ring = 25f + k % 7 * 2f;
            var at = lz + new Vector3(MathF.Cos(angle) * ring, MathF.Sin(angle) * ring, 0f);
            if (nav?.Nearest(at, 6f, 8f) is int node and >= 0) at = nav.Position(node);
            else if (nav is not null) at = lz;
            if (enemies.Spawn(MusouEnemyType.Boy, at, angle, boy) == 0) break;
        }
        while (guards.SquadsAlive < squads && guards.SpawnSquad(lz) >= 0) { }
    }

    /// <summary>A new run: the war from its start and an empty field.</summary>
    void TfwNewRun()
    {
        TfwResetMarines();
        _tfw!.Reset();
        TfwForgetArsenals();
        _tfwKos = 0;
        MusouNewRound();
        TfwGuards!.Clear();
        _musouBoss = null;
    }

    bool TfwHumans()
    {
        foreach (var (id, p) in _players) if (!TfwContracts.IsPuppet(id) && p.Team == Team.SpaceMarines) return true;
        return false;
    }

    /// <summary>
    /// The war's match flow, in place of the one-sided Musou flow: warm-up until a human is in (the AI Marines do not count), the countdown, play until the run ends (no time limit), the
    /// end and, after the rules' delay, a new run. Every human gone sends it back to warm-up with a new run.
    /// </summary>
    bool TfwStepMatch()
    {
        var run = _tfw!;
        bool humans = TfwHumans();
        switch (_state)
        {
            case MatchState.Warmup:
                if (humans) { TfwNewRun(); MusouRearm(); BeginRound(respawnWithoutCountdown: false); }
                break;
            case MatchState.Countdown:
                if (!humans) { _state = MatchState.Warmup; TicksLeft = 0; }
                else if (--TicksLeft == 0)
                {
                    _projectiles.Clear(); _areas.Clear(); _perkBlasts.Clear();
                    CountProjectilesOut();
                    StartRound(respawn: false);
                }
                break;
            case MatchState.Playing:
                if (!humans) { _state = MatchState.Warmup; TicksLeft = 0; TfwNewRun(); }
                break;
            case MatchState.Ended:
                if (!run.Ended)
                {
                    // The core's score limit ended it this tick: only the run ends a war.
                    _state = humans ? MatchState.Playing : MatchState.Warmup;
                    Winner = Team.None;
                    TicksLeft = 0;
                    if (!humans) TfwNewRun();
                    break;
                }
                if (Tick > _endedTick && TicksLeft > 0) TicksLeft--;
                if (TicksLeft == 0)
                {
                    _state = MatchState.Warmup;
                    Winner = Team.None;
                    Array.Clear(_teamScores);
                    ResetPoints();
                    TfwNewRun();
                    MusouRearm();
                    if (humans) BeginRound(respawnWithoutCountdown: true);
                }
                break;
        }
        MusouArm();   // World.TfwArsenal.cs: every new life with the full loadout and the run's kit
        if (_state == MatchState.Playing) TicksLeft = uint.MaxValue;
        _teamScores[(int)Team.Chaos] = 0;
        return true;
    }

    /// <summary>An Ork's attack on a Guard squad (<see cref="MusouAttack.Player"/> below 0: <see cref="TfwContracts.SquadOf"/>): it lands on the squad's nearest man (World.TfwGuards.cs).</summary>
    void TfwGuardHit(MusouAttack attack) => TfwGuardsHit(attack);

    /// <summary>
    /// A fresh life for player <paramref name="id"/> at <paramref name="at"/>, alive or dead (the Deploy, an extraction): the core has no respawn routine to call (its respawn is inline in
    /// <see cref="Step"/>), so this does what that does: the waiting loadout change (a customizer Setup) applies, <see cref="Combat.Respawned"/> restores the player, and the tick's
    /// <see cref="LastRespawns"/> gets it so the server promotes the pending look; the next <see cref="MusouArm"/> gives it the run's kit (<see cref="TfwArmAll"/>). Call it inside the step
    /// (<see cref="TfwStep"/>): <see cref="LastRespawns"/> is cleared when a tick begins. False, and nothing changed, outside a war or for an unknown player.
    /// </summary>
    public bool TfwRespawnAt(int id, Vector3 at, float yaw)
    {
        if (_tfw is null || !_players.TryGetValue(id, out var p)) return false;
        if (_nextLoadouts.ContainsKey(id) && _tfwArsenals.TryGetValue(id, out var arsenal)) arsenal.LoadedPrimary = arsenal.LoadedPistol = -1;   // a Setup's choice stands: no extra is left to send home
        _players[id] = Combat.Respawned(WithNextLoadout(p) with { Position = at, Yaw = yaw }, Config);
        _respawns.Add(id);
        _musouArmed.Remove(id);
        return true;
    }
}
