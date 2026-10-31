namespace SmSim;

/// <summary>A side in a team match (global <c>multiplayer_settings/teams</c>: "space marine team" and "chaos team").</summary>
public enum Team : byte { None = 0, SpaceMarines = 1, Chaos = 2 }

/// <summary>
/// Where a team match is: <see cref="Warmup"/> until both sides have a player (ours; play goes on, nothing scores), <see cref="Playing"/> (scores count),
/// <see cref="Ended"/> with a winner until the restart. <see cref="None"/>: no <see cref="MatchRules"/> (every player hostile, no scores).
/// <see cref="Countdown"/> (match flow, ours): both sides are in, everyone stands at a spawn and cannot fire for
/// <see cref="MatchRules.CountdownTime"/>; the wire carries it as <see cref="Warmup"/> with ticks left above 0 (<see cref="MatchFlow"/>).
/// </summary>
public enum MatchState : byte { None = 0, Warmup = 1, Playing = 2, Ended = 3, Countdown = 4 }

/// <summary>What killed a player (kill feed; protocol 10).</summary>
public enum KillCause : byte
{
    /// <summary>No attacker: fell out of the level (killer 0).</summary>
    World = 0,
    /// <summary>A weapon's shot, cone or projectile: the index is into <see cref="SimConfig.Weapons"/>.</summary>
    Weapon = 1,
    /// <summary>A thrown item: the index is into <see cref="SimConfig.Thrown"/>.</summary>
    Thrown = 2,
    /// <summary>A melee attack: the index is the attacker's melee choice (<see cref="PlayerState.MeleeChoice"/>).</summary>
    Melee = 3,
    /// <summary>A jump-pack landing or ground pound: the index is into <see cref="SimConfig.JumpPacks"/>.</summary>
    JumpPack = 4,
    /// <summary>A perk's blast (M43: Final Vengeance, Reactive Blast, Blast Off): the index is into <see cref="SimConfig.Perks"/>.</summary>
    Perk = 5,
}

/// <summary>
/// A death: <paramref name="Sequence"/> counts deaths in this world from 1 (the wire carries it modulo 65536 so clients can
/// drop repeats), <paramref name="Killer"/> is the player credited (0: none; the victim itself: a suicide), and the cause.
/// <paramref name="Direction"/> (M23): the killing hit's push (<see cref="HitEvent.Direction"/>; unit, world space), zero for
/// a death without one (fell out of the level or into a kill volume), for the clients' ragdolls.
/// </summary>
public readonly record struct KillEvent(int Sequence, uint Tick, int Killer, int Victim, KillCause Cause, int Index, System.Numerics.Vector3 Direction = default);

/// <summary>A player's kills (enemies killed) and deaths in the current round.</summary>
public readonly record struct PlayerScore(int Kills, int Deaths);

/// <summary>
/// The rules of Annihilation (team deathmatch), server-side. What the install says and what is ours
/// (spec/behavior.md, M15): the two sides and the class races are data; the team score counting the opposing side's deaths
/// (any cause: suicides and team kills score for the other side) is an **interpretation** of the multiplayer scripts
/// (<c>util/mp/scripts/data/objectives.lua</c>: objectives <c>TeamDeathmatchMarine</c>/<c>Chaos</c> of type <c>Deaths</c> on the
/// enemy group, <c>isDeathMatch</c>, limit from <c>Network_GetScoreLimit</c>). The limits and friendly fire are host options
/// whose values the executable supplies (front end <c>HostOptions.gfx</c>), so the defaults here are **provisional**.
/// </summary>
public sealed record MatchRules
{
    /// <summary>Team score that ends the match (provisional: the host option's values are not in the data).</summary>
    public int ScoreLimit { get; init; } = 41;
    /// <summary>Match length in seconds; 0: no limit (the script's timer takes minutes from <c>Network_GetTimeLimit</c>, -1 none; value provisional).</summary>
    public float TimeLimit { get; init; } = 600f;
    /// <summary>A host option in the original (<c>friendly_fire_btn</c>); off here unless asked for (provisional default).</summary>
    public bool FriendlyFire { get; init; }
    /// <summary>Seconds from the end of a match to the next (ours; the original returns to the lobby).</summary>
    public float RestartDelay { get; init; } = 15f;
    /// <summary>
    /// Seconds of "MATCH BEGINS" before play (ours: the original's length is in the executable; its HUD clip shows a mm:ss number). 0 (the
    /// default here): none, a round starts as soon as both sides have a player (the behavior before the match flow). The server's default is
    /// <see cref="DefaultCountdown"/>.
    /// </summary>
    public float CountdownTime { get => _countdownTime; init => _countdownTime = float.IsFinite(value) ? Math.Clamp(value, 0f, MaxCountdown) : 0f; }
    float _countdownTime;
    /// <summary>The server's countdown when none is asked for (ours).</summary>
    public const float DefaultCountdown = 5f;
    /// <summary>Longest countdown the server accepts (s): ticks left must stay far below the wire's "no limit" value (<c>uint.MaxValue</c>).</summary>
    public const float MaxCountdown = 600f;
    /// <summary>Most players per side (8v8).</summary>
    public int MaxPerSide { get; init; } = 8;
    /// <summary>
    /// Test aid (ours; server option <c>--honor-side-requests</c>, default off): a joining player's requested side is kept up to
    /// <see cref="MaxPerSide"/>, and the balance (<see cref="World.AssignClass"/>) applies only to joins that name no side.
    /// For filling a server with bots in uneven teams.
    /// </summary>
    public bool HonorSideRequests { get; init; }
    /// <summary>The mode (M16): Annihilation (deaths score) or Seize Ground (owned control points score).</summary>
    public GameMode Mode { get; init; } = GameMode.Annihilation;
    /// <summary>Seize Ground: seconds one side needs on a neutral point to own it (domination objective <c>captureDuration</c> 20).</summary>
    public float CaptureTime { get; init; } = 20f;
    /// <summary>Seize Ground: seconds one side needs on the other side's point to make it neutral (domination <c>decapDuration</c> 10).</summary>
    public float NeutraliseTime { get; init; } = 10f;
    /// <summary>Seize Ground: team score per owned point per <see cref="ScoreInterval"/> (domination <c>scoreSpeed</c> 2; the unit is an interpretation).</summary>
    public int PointScore { get; init; } = 2;
    /// <summary>Seize Ground: seconds between score awards (provisional; not in the data).</summary>
    public float ScoreInterval { get; init; } = 1f;

    /// <summary>Score limit Seize Ground uses when none is given (provisional: the host option's values are in the executable).</summary>
    public const int SeizeGroundScoreLimit = 1000;

    /// <summary>The default rules of a mode (Seize Ground: <see cref="SeizeGroundScoreLimit"/>).</summary>
    public static MatchRules For(GameMode mode) => mode == GameMode.SeizeGround ? new MatchRules { Mode = mode, ScoreLimit = SeizeGroundScoreLimit } : new MatchRules();

    public static Team Other(Team t) => t switch { Team.SpaceMarines => Team.Chaos, Team.Chaos => Team.SpaceMarines, _ => Team.None };
}

/// <summary>Teams, scoring and match flow (deterministic; the server runs it, clients see it in snapshots).</summary>
public sealed partial class World
{
    /// <summary>Ticks a death stays in <see cref="RecentKills"/> (so snapshots, sent every 1 or 2 ticks, carry it several times).</summary>
    public const int KillMemoryTicks = 15;

    MatchState _state = MatchState.Warmup;
    uint _endedTick;
    readonly int[] _teamScores = new int[3];
    readonly Dictionary<int, PlayerScore> _scores = new();
    readonly List<KillEvent> _kills = new(), _recentKills = new(), _killLog = new();
    int _killSequence;

    /// <summary>The match rules; null (the default): no teams, everyone hostile, no team scores (the behavior before M15).</summary>
    public MatchRules? Rules { get; init; }
    public MatchState State => Rules is null ? MatchState.None : _state;
    /// <summary>The winning side of an ended match (<see cref="Team.None"/>: a draw, or not ended).</summary>
    public Team Winner { get; private set; }
    /// <summary>Playing: ticks until the time limit (<see cref="uint.MaxValue"/>: none). Ended: ticks until the restart. Else 0.</summary>
    public uint TicksLeft { get; private set; }
    public int ScoreOf(Team side) => _teamScores[(int)side];
    /// <summary>Kills and deaths of every player in the world (this round).</summary>
    public IReadOnlyDictionary<int, PlayerScore> Scores => _scores;
    /// <summary>Deaths during the last <see cref="Step"/>.</summary>
    public IReadOnlyList<KillEvent> LastKills => _kills;
    /// <summary>Deaths during the last <see cref="KillMemoryTicks"/> ticks, oldest first.</summary>
    public IReadOnlyList<KillEvent> RecentKills => _recentKills;
    /// <summary>The last <see cref="KillLogLength"/> deaths, oldest first (the server sends each client the ones it has not acknowledged).</summary>
    public IReadOnlyList<KillEvent> KillLog => _killLog;
    /// <summary>The sequence of the newest death (0: none yet).</summary>
    public int LastKillSequence => _killSequence;
    /// <summary>Deaths kept in <see cref="KillLog"/> (ours: a client further behind skips the older ones).</summary>
    public const int KillLogLength = 256;

    /// <summary>Whether players of these sides may damage each other without friendly fire (a side of None is everyone's enemy).</summary>
    public static bool Hostile(Team a, Team b) => a == Team.None || b == Team.None || a != b;

    /// <summary>
    /// Whether <paramref name="attacker"/>'s damage reaches <paramref name="target"/>: never after the match ended; always on
    /// oneself (self damage is unchanged); else unless both are allies and friendly fire is off. An attacker no longer in
    /// the world (its projectile outlived it) harms everyone.
    /// </summary>
    public bool Harms(int attacker, int target)
    {
        if (State is MatchState.Ended or MatchState.Countdown) return false;   // nobody is hurt before the match begins (ours)
        if (attacker == target || Rules is null || Rules.FriendlyFire) return true;
        return !_players.TryGetValue(attacker, out var a) || !_players.TryGetValue(target, out var t) || Hostile(a.Team, t.Team);
    }

    /// <summary>Players on a side (living or dead).</summary>
    public int CountSide(Team side, int except = 0)
    {
        int n = 0;
        foreach (var (id, p) in _players) if (p.Team == side && id != except) n++;
        return n;
    }

    /// <summary>
    /// The class a joining player gets (rules **ours**, spec/behavior.md): its class's side, or <paramref name="requestedSide"/>
    /// when one is asked for, unless that side already has more players than the other or is full; then the other side. A
    /// player moved to the other side gets that side's class of the same role (else the class at the same position among the
    /// side's classes, else its first). Without rules, or for a class without a side, the request stands.
    /// </summary>
    public int AssignClass(int requestedClass, Team requestedSide = Team.None)
    {
        var classes = Config.Classes;
        if (Rules is null || requestedClass < 0 || requestedClass >= classes.Count) return requestedClass;
        var side = classes[requestedClass].Side;
        if (side == Team.None) return requestedClass;
        var want = requestedSide is Team.SpaceMarines or Team.Chaos ? requestedSide : side;
        bool forced = false;
        ExtrasJoinSide(ref want, ref forced);   // EXTRAS-HOOK: added content that fixes the side (Musou: Space Marines only, no balance)
        if (!forced)
        {
            int mine = CountSide(want), other = CountSide(MatchRules.Other(want));
            bool asked = Rules.HonorSideRequests && requestedSide is Team.SpaceMarines or Team.Chaos;   // --honor-side-requests (test aid)
            if ((mine > other && !asked) || mine >= Rules.MaxPerSide) want = MatchRules.Other(want);
        }
        return want == side ? requestedClass : Equivalent(requestedClass, want);
    }

    /// <summary>
    /// Hook for added content (docs/added-content.md): the side a join asks for (<paramref name="want"/>, in <see cref="AssignClass"/> and for a class
    /// without a side in <see cref="AssignSide"/>); set <paramref name="forced"/> to take <paramref name="want"/> as it is, without the balance or the
    /// per-side limit. Implemented in an <c>Extras/</c> folder; returns at once (both unchanged) unless its feature runs.
    /// </summary>
    partial void ExtrasJoinSide(ref Team want, ref bool forced);

    /// <summary>
    /// Hook for added content: at the top of <see cref="StepMatch"/> (with rules); set <paramref name="handled"/> when the feature ran the match flow
    /// itself this tick, and the core's warm-up, countdown, play and restart are skipped. Implemented in an <c>Extras/</c> folder; returns at once
    /// (unhandled) unless its feature runs.
    /// </summary>
    partial void ExtrasStepMatch(ref bool handled);

    /// <summary>
    /// The side a joining player of <paramref name="playerClass"/> plays for (Ork O4; design section 1.2; rules **ours**): none without
    /// rules; a class with a side keeps it (<see cref="AssignClass"/> has already moved it to the side the balance allows); a class
    /// without a side (the Ork Boy, who plays on either team) gets <paramref name="requestedSide"/> under the same balance rule as
    /// <see cref="AssignClass"/> (the other side when the requested one already has more players than the other, unless
    /// <see cref="MatchRules.HonorSideRequests"/>, or when it is full), and with no request the smaller side (ties: Space Marines).
    /// Pass the result to <see cref="Spawn"/>.
    /// </summary>
    public Team AssignSide(int playerClass, Team requestedSide = Team.None)
    {
        if (Rules is null) return Team.None;
        var classes = Config.Classes;
        var side = playerClass >= 0 && playerClass < classes.Count ? classes[playerClass].Side : Team.None;
        if (side != Team.None) return side;
        var want = requestedSide;
        bool forced = false;
        ExtrasJoinSide(ref want, ref forced);   // EXTRAS-HOOK: added content that fixes the side (Musou)
        if (forced) return want;
        if (requestedSide is not (Team.SpaceMarines or Team.Chaos)) return SmallerSide();
        int mine = CountSide(requestedSide), other = CountSide(MatchRules.Other(requestedSide));
        bool full = mine >= Rules.MaxPerSide;
        return (mine > other && !Rules.HonorSideRequests) || full ? MatchRules.Other(requestedSide) : requestedSide;
    }

    /// <summary>The side with fewer players (ties: Space Marines).</summary>
    Team SmallerSide() => CountSide(Team.Chaos) < CountSide(Team.SpaceMarines) ? Team.Chaos : Team.SpaceMarines;

    /// <summary>
    /// <paramref name="playerClass"/> for a player of <paramref name="side"/>: the class itself when it plays for that side (or either
    /// has no side, or the index is out of range); else the side's class of the same role, else the one at the same position among
    /// the side's classes, else the side's first (as a balanced join; choice indices then carry over by position: interpretation).
    /// </summary>
    public int ClassForSide(int playerClass, Team side)
    {
        var classes = Config.Classes;
        if (playerClass < 0 || playerClass >= classes.Count || side == Team.None) return playerClass;
        var own = classes[playerClass].Side;
        return own == Team.None || own == side ? playerClass : Equivalent(playerClass, side);
    }

    int Equivalent(int playerClass, Team side)
    {
        var classes = Config.Classes;
        var own = Enumerable.Range(0, classes.Count).Where(i => classes[i].Side == classes[playerClass].Side).ToList();
        var theirs = Enumerable.Range(0, classes.Count).Where(i => classes[i].Side == side).ToList();
        if (theirs.Count == 0) return playerClass;
        string role = classes[playerClass].Role;
        if (role.Length > 0 && theirs.FirstOrDefault(i => classes[i].Role == role, -1) is int same and >= 0) return same;
        int position = own.IndexOf(playerClass);
        return position >= 0 && position < theirs.Count ? theirs[position] : theirs[0];
    }

    /// <summary>The side of a player of this class: none without rules; the class's side; else the smaller side (ties: Space Marines).</summary>
    Team TeamFor(int playerClass)
    {
        if (Rules is null) return Team.None;
        var side = playerClass >= 0 && playerClass < Config.Classes.Count ? Config.Classes[playerClass].Side : Team.None;
        if (side != Team.None) return side;
        return SmallerSide();
    }

    /// <summary>
    /// With rules and living enemies of <paramref name="team"/>: the free spawn point (no living player within 2 m) whose
    /// nearest living enemy is farthest (ties: the first from the rotation cursor), or null (provisional: the original picks
    /// among the map's <c>tdm_mkr_spawn_*</c> markers by script, <c>RandomizeTeamSpawning</c>, whose rule is not decoded).
    /// </summary>
    int? FarthestFromEnemies(int playerId, Team team)
    {
        if (Rules is null || team == Team.None || _spawns.Length == 0) return null;
        var enemies = _players.Values.Where(p => p.Alive && p.PlayerId != playerId && Hostile(p.Team, team)).Select(p => p.Position).ToList();
        if (enemies.Count == 0) return null;
        int best = -1; float bestDistance = -1f;
        for (int k = 0; k < _spawns.Length; k++)
        {
            int i = (_spawnCursor + k) % _spawns.Length;
            if (Occupied(_spawns[i].Position, playerId)) continue;
            float nearest = enemies.Min(e => System.Numerics.Vector3.DistanceSquared(e, _spawns[i].Position));
            if (nearest > bestDistance) { bestDistance = nearest; best = i; }
        }
        return best >= 0 ? best : null;
    }

    /// <summary>Start of a tick: forget old deaths; returns who is alive before the tick (for <see cref="ResolveDeaths"/>).</summary>
    List<int> BeginMatchTick()
    {
        _kills.Clear();
        _captures.Clear();
        _recentKills.RemoveAll(k => Tick - k.Tick >= KillMemoryTicks);
        var alive = new List<int>();
        foreach (var (id, p) in _players) if (p.Alive) alive.Add(id);
        alive.Sort();
        return alive;
    }

    /// <summary>
    /// Every player alive before the tick and dead after it died this tick: the last killing hit names the killer and cause
    /// (none: fell out of the level). Deaths count for the victim and a kill for a killer of another side (or anyone without
    /// rules), never for a suicide or team kill (ours); personal scores do not change after the match ended. While playing,
    /// each death scores one for the victim's opposing side (interpretation of the scripts' Deaths objective). All of the
    /// tick's deaths are added before the limit is checked (ours): one side at or over the limit wins; both: the higher score
    /// wins, equal scores are a draw.
    /// </summary>
    void ResolveDeaths(List<int> aliveBefore)
    {
        var tickScores = new int[3];
        bool ended = State == MatchState.Ended;
        foreach (int victim in aliveBefore)
        {
            if (!_players.TryGetValue(victim, out var dead) || dead.Alive) continue;
            HitEvent? fatal = null;
            for (int i = _hits.Count - 1; i >= 0; i--) if (_hits[i].Target == victim && _hits[i].Killed) { fatal = _hits[i]; break; }
            int killer = fatal?.Shooter ?? 0;
            var (cause, index) = fatal is not { } h ? (KillCause.World, -1)
                : h.Perk >= 0 ? (KillCause.Perk, h.Perk)   // M43: a perk's blast
                : h.Landing ? (KillCause.JumpPack, _players.TryGetValue(killer, out var k1) ? Loadouts.ClassOf(Config, k1.Class)?.JumpPack ?? -1 : -1)
                : h.Melee ? (KillCause.Melee, _players.TryGetValue(killer, out var k2) ? k2.MeleeChoice : -1)
                : h.ThrownItem ? (KillCause.Thrown, h.Weapon)
                : (KillCause.Weapon, h.Weapon);
            var e = new KillEvent(++_killSequence, Tick, killer, victim, cause, index, fatal?.Direction ?? default);
            _kills.Add(e);
            _recentKills.Add(e);
            _killLog.Add(e);
            if (_killLog.Count > KillLogLength) _killLog.RemoveRange(0, _killLog.Count - KillLogLength);
            if (ended) continue;

            var score = _scores.GetValueOrDefault(victim);
            _scores[victim] = score with { Deaths = score.Deaths + 1 };
            if (killer != 0 && killer != victim && _players.TryGetValue(killer, out var by) && (Rules is null || Hostile(by.Team, dead.Team)))
            {
                var ks = _scores.GetValueOrDefault(killer);
                _scores[killer] = ks with { Kills = ks.Kills + 1 };
            }
            // Seize Ground: deaths do not score (interpretation: the mode's preset has no Deaths objectives).
            if (State == MatchState.Playing && dead.Team != Team.None && Rules!.Mode == GameMode.Annihilation) tickScores[(int)MatchRules.Other(dead.Team)]++;
        }
        if (State != MatchState.Playing || Rules!.Mode != GameMode.Annihilation) return;
        for (int side = 1; side < 3; side++) _teamScores[side] += tickScores[side];
        CheckScoreLimit();
    }

    /// <summary>One side at or over the score limit wins; both: the higher score wins, equal scores are a draw (ours).</summary>
    void CheckScoreLimit()
    {
        int marines = ScoreOf(Team.SpaceMarines), chaos = ScoreOf(Team.Chaos);
        bool a = marines >= Rules!.ScoreLimit, b = chaos >= Rules.ScoreLimit;
        if (a && b) End(marines > chaos ? Team.SpaceMarines : chaos > marines ? Team.Chaos : Team.None);
        else if (a) End(Team.SpaceMarines);
        else if (b) End(Team.Chaos);
    }
    void End(Team winner)
    {
        _state = MatchState.Ended;
        Winner = winner;
        TicksLeft = Ticks(Rules!.RestartDelay);
        _endedTick = Tick;
    }

    /// <summary>
    /// Both sides are in: with a <see cref="MatchRules.CountdownTime"/> everyone respawns at a spawn point and the countdown runs (match
    /// flow, ours; Annihilation too); without one the round starts at once, respawning only when asked (<paramref name="respawnWithoutCountdown"/>).
    /// </summary>
    void BeginRound(bool respawnWithoutCountdown)
    {
        if (Rules!.CountdownTime > 0f) StartRound(respawn: true, countdown: true);
        else StartRound(respawn: respawnWithoutCountdown);
    }

    uint Ticks(float seconds) => (uint)Math.Max(1, MathF.Round(seconds * Config.TickRate));

    /// <summary>
    /// End of a tick: warm-up starts a round once both sides have a player; a round ends when a side empties (the other
    /// wins; the scripts' <c>EmptyTeamGameOver</c>, interpretation) or at the time limit (higher score wins; equal: a draw);
    /// an ended match restarts after <see cref="MatchRules.RestartDelay"/>.
    /// </summary>
    void StepMatch()
    {
        if (Rules is null) return;
        bool handled = false;
        ExtrasStepMatch(ref handled);   // EXTRAS-HOOK: added content that runs its own match flow (Musou: one side only)
        if (handled) return;
        StepCapture();
        int marines = CountSide(Team.SpaceMarines), chaos = CountSide(Team.Chaos);
        bool both = marines > 0 && chaos > 0;
        switch (_state)
        {
            case MatchState.Warmup:
                // Seize Ground: everyone into their start areas, so nobody holds a point from warm-up (Annihilation plays on).
                if (both) BeginRound(respawnWithoutCountdown: Seizing);
                break;
            case MatchState.Countdown:
                // Both sides are needed to the end; a side that empties sends the match back to warm-up (ours).
                if (!both) { _state = MatchState.Warmup; TicksLeft = 0; }
                else if (--TicksLeft == 0)
                {
                    // Everyone already stands at a spawn: the scores and the clock start. Nothing from the countdown (a blast, a perk's blast) carries into play.
                    _projectiles.Clear(); _areas.Clear(); _perkBlasts.Clear();
                    CountProjectilesOut();
                    StartRound(respawn: false);
                }
                break;
            case MatchState.Playing:
                if (!both) { End(marines > 0 ? Team.SpaceMarines : chaos > 0 ? Team.Chaos : Team.None); break; }
                if (TicksLeft != uint.MaxValue && --TicksLeft == 0)
                {
                    int a = ScoreOf(Team.SpaceMarines), b = ScoreOf(Team.Chaos);
                    End(a > b ? Team.SpaceMarines : b > a ? Team.Chaos : Team.None);
                }
                break;
            case MatchState.Ended:
                if (Tick > _endedTick && TicksLeft > 0) TicksLeft--;
                if (TicksLeft == 0)
                {
                    _state = MatchState.Warmup;
                    Winner = Team.None;
                    Array.Clear(_teamScores);   // last round's scores and points do not linger into warm-up
                    ResetPoints();
                    if (both) BeginRound(respawnWithoutCountdown: true);
                }
                break;
        }
    }

    /// <summary>
    /// A new round: scores to zero and the clock set. After an ended match (<paramref name="respawn"/>) projectiles,
    /// lingering blasts, jump tracking and the rewind history are also cleared, every player leaves play and then respawns
    /// (full health and ammunition) at a spawn point chosen against those already respawned, in id order; from warm-up the
    /// players play on where they are in Annihilation, and respawn in their start areas in Seize Ground (ours).
    /// </summary>
    void StartRound(bool respawn, bool countdown = false)
    {
        _state = countdown ? MatchState.Countdown : MatchState.Playing;
        Winner = Team.None;
        Array.Clear(_teamScores);
        foreach (var id in _scores.Keys.ToList()) _scores[id] = default;
        TicksLeft = countdown ? Ticks(Rules!.CountdownTime) : Rules!.TimeLimit > 0f ? Ticks(Rules.TimeLimit) : uint.MaxValue;
        ResetPoints();
        if (!respawn) return;
        _projectiles.Clear();
        _areas.Clear();
        _perkBlasts.Clear();   // M43: a death or armour-break blast never carries into the next round
        ClearPickups();   // fidelity gap 1: no drop carries into the next round (ours)
        _jumpApex.Clear();
        _history.Clear();   // no shot may rewind into the previous match
        _flightHistory.Clear();
        // Everyone leaves play first, so each spawn is chosen against the players already respawned, not old positions.
        var ids = _players.Keys.Order().ToList();
        foreach (var id in ids) _players[id] = Combat.Killed(_players[id], Config);
        foreach (var id in ids)
        {
            var p = WithNextLoadout(_players[id]);   // a loadout change waiting for a respawn applies here too
            if (!_respawns.Contains(id)) _respawns.Add(id);   // once, also after a death-timer respawn in the same tick
            var (position, yaw) = TryStartAreaSpawn(id, p.Team, out var areaAt, out var areaYaw) ? (areaAt, areaYaw)
                : _spawns.Length > 0 ? ChooseSpawn(id, p.Team) : (PlaceholderSpawn(id), p.Yaw);
            _players[id] = Combat.Respawned(p with { Position = position, Yaw = yaw }, Config);
        }
        CountProjectilesOut();
    }
}
