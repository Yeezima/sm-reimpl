using System.Numerics;
using SmSim.Extras.Warfront;
using SmSim.Extras.Warfront.Hq;
using SmSim.Extras.Warfront.Fortress;

namespace SmSim.Extras.Musou.Campaign;

/// <summary>
/// Warfront's HQ defense (slice W13b): the owner's battle on the HQ map. W (wall and gate), K (keep) and V (caves) start Secured with the Guardsmen at their posts; no garrison, no commander, no depot. Orks come in waves that muster on the assembly ground
/// and climb the ramp; only the front-most position still held can fall, a fallen position is lost for the battle (its ring is closed: nobody can re-secure it), and the next stage's sources open. The defence is won by surviving
/// <see cref="WarfrontHqBattleRules.SurviveSeconds"/> (owner, 2026-11-17: "The HQ defense is won by surviving 8 minutes"); it is lost when V falls (<see cref="HqLost"/>) or on a wipe (the run's rule). The battle's own crowd and trickle machinery
/// stays off (<c>WarfrontSourceActive</c> answers false); the waves are this file's. Every hook returns at once unless <see cref="WarfrontHq"/> is set.
/// </summary>
public sealed partial class TfwBattle
{
    /// <summary>Whether this battle is the HQ defense (set by the run's hook before <see cref="Start"/>).</summary>
    public bool WarfrontHq { get; internal set; }
    /// <summary>V fell: the defence is lost (the run leaves the battle as lost).</summary>
    public bool HqLost { get; private set; }
    /// <summary>The positions fallen so far (0 to 3: the stage the defence is at).</summary>
    public int HqFallenCount { get; private set; }
    /// <summary>The waves that have left the assembly ground.</summary>
    public int HqWavesSent { get; private set; }
    /// <summary>The message the HUD's banner shows now (<see cref="WarfrontHqBattleRules.MessageNone"/>: none), kept for <see cref="MessageSeconds"/>.</summary>
    public byte HqMessage { get; private set; }
    /// <summary>The Orks the waves have made (tests and the profile).</summary>
    public int HqOrksMade { get; private set; }
    /// <summary>Orks of a wave never made for want of room under the alive cap or the pool.</summary>
    public int HqOrksSkipped { get; private set; }
    /// <summary>The Guardsmen lost to routs so far (<see cref="HqRout"/>; tests and the bench).</summary>
    public int HqGuardsRouted { get; private set; }
    /// <summary>Test aid (the measurement tool): false stops the scheduled waves.</summary>
    public bool HqWavesOn { get; set; } = true;
    /// <summary>What the defence did on its last tick, for the bench's tagged list of the slowest ticks: 1 muster spawns, 2 stage spawns (flood or share), 4 a chunk let go, 8 a wave left, 16 a position fell.</summary>
    public byte HqTickFlags { get; private set; }
    /// <summary>Seconds a banner message stays.</summary>
    public const float MessageSeconds = 4f;

    readonly bool[] _hqFallen = new bool[Positions];
    readonly int[] _hqGoal = new int[GuardSim.MaxSquads];   // the post (position) of each Guard squad this defence placed
    const int HqSquadOwner = 250;    // _squadArmy's mark for a squad this defence owns (the armies are off in an HQ defence, so nothing else reads it)
    const int HqWaveGroupFirst = 1000;   // group of wave w chunk c: 1000 + 100 w + c
    int _hqChunksReleased;
    static int HqGroup(int wave, int chunk) => HqWaveGroupFirst + 100 * wave + chunk;
    int _hqMustered, _hqMessageTicks, _hqRepostTicks;
    float _hqEliteOwed;
    uint _hqMade;

    /// <summary>The defence's opening (<c>WarfrontStart</c>): the three positions Secured, the Guardsmen at their posts, nothing else.</summary>
    partial void WarfrontStart(ref bool handled)
    {
        if (_enemies.Warfront?.Fortress is { } setup) { handled = true; FortressStart(setup); return; }   // the fortress battle (W14b), before the HQ branch
        if (!WarfrontHq) return;
        handled = true;
        for (int k = 0; k < Positions; k++) _objectives[k] = new TfwObjective((byte)k, TfwPositionState.Secured, 1f, _layout.Positions[k], _layout.Radius(k));
        _objectives[CommanderObjective] = new TfwObjective(CommanderObjective, TfwPositionState.Locked, 0f, _layout.Hq, TfwMap.HqRadius);
        Secured = Positions;
        Armies = false;     // no armies, garrisons or retreats
        Trickle = false;    // no trickle: the waves are this file's
        WeirdboyzOn = false;   // no Weirdboyz in the HQ defence [ours]: Orks teleported inside a held ring would remove the stage the player stands on (review of W13b, ruling 3)
        _hqHold = 1f;
        _reserves = 0;
        Array.Fill(_hqGoal, -1);
        for (int k = 0; k < Positions && _guardsmen; k++)
        {
            float ground = WarfrontHqLayout.Build.W.Z;
            var posts = WarfrontHqLayout.PostsOf(k).Where(p => p.Z <= ground + 0.5f).ToArray();   // ground posts (the parapet posts are on the wall walk)
            for (int n = 0; n < WarfrontHqBattleRules.SquadsAt[k]; n++)
            {
                var post = posts.Length > 0 ? posts[n % posts.Length] : _layout.Positions[k];
                int row = posts.Length > 0 ? n / posts.Length : 0;
                var at = Ground(post + new Vector3(0f, -2f * row, 0f), post);
                int s = _guards.SpawnSquad(at, FaceCentre(at), GuardSim.SquadSize, WarfrontHqBattleRules.GuardStrengthAt[k]);
                if (s < 0) continue;
                _squadArmy[s] = HqSquadOwner;
                _hqGoal[s] = k;
                Send(s, k);
            }
        }
    }

    /// <summary>No position loses progress by Musou's rule in an HQ defence: the fall is the defence's own weighted hold (<see cref="StepHqHold"/>), so Musou's "Orks alone for 20 s" never fires (<c>WarfrontRetake</c>).</summary>
    partial void WarfrontRetake(int position, ref bool allowed)
    {
        if (_fortress is not null) { allowed = false; return; }   // the fortress waypoints are never retaken (W14b)
        if (!WarfrontHq) return;
        allowed = false;
    }

    /// <summary>The battle's own crowds and trickle stay off in an HQ defence: its sources are opened by stage (<see cref="HqSourceOpen"/>) and fed by the waves (<c>WarfrontSourceActive</c>).</summary>
    partial void WarfrontSourceActive(int source, ref bool active, ref bool handled)
    {
        if (_fortress is not null) { handled = true; active = FortressSourceActive(source); return; }   // the fortress's stages (W14b)
        if (!WarfrontHq) return;
        handled = true;
        active = false;
    }

    /// <summary>The front-most position still held (0 W, 1 K, 2 V; 3 when all have fallen).</summary>
    public int HqFront
    {
        get
        {
            for (int k = 0; k < Positions; k++) if (!_hqFallen[k]) return k;
            return Positions;
        }
    }

    /// <summary>Whether position <paramref name="k"/> has fallen (lost for the battle).</summary>
    public bool HqFell(int k) => k is >= 0 and < Positions && _hqFallen[k];

    /// <summary>Whether defence stage <paramref name="stage"/>'s sources are open: 0 (the assembly ground) always, 1 (the courtyard) once W has fallen, 2 (the hall) once K has.</summary>
    public bool HqSourceOpen(int stage) => stage == 0 || (stage is 1 or 2 && _hqFallen[stage - 1]);

    /// <summary>The seconds since the battle began.</summary>
    public float HqSeconds => _now * Dt;
    /// <summary>The seconds left to survive (0 once won or lost).</summary>
    public float HqSecondsLeft => Won || HqLost ? 0f : MathF.Max(0f, WarfrontHqBattleRules.SurviveSeconds - HqSeconds);
    /// <summary>The number of the wave next to leave (1 based; the last scheduled once every scheduled wave has left) and the seconds until it leaves (0: none left to come).</summary>
    public (int Wave, float Seconds) HqNextWave
    {
        get
        {
            int waves = WarfrontHqBattleRules.ScheduledWaves;
            if (HqWavesSent >= waves) return (waves, 0f);
            return (HqWavesSent + 1, MathF.Max(0f, WarfrontHqBattleRules.WaveLeaves(HqWavesSent + 1) - HqSeconds));
        }
    }

    float _hqHold = 1f;
    /// <summary>The front position's hold, 1 (firm) to 0 (it falls): drains while the Orks in its ring outweigh the defenders, recovers otherwise (<see cref="StepHqHold"/>).</summary>
    public float HqHold => HqFront >= Positions ? 0f : _hqHold;
    /// <summary>The hold in percent, in steps of 5 (the Battle section's byte).</summary>
    public byte HqHoldPercent => (byte)(Math.Clamp((int)MathF.Round(HqHold * 20f), 0, 20) * 5);
    /// <summary>The Orks' weight and the defenders' weight inside the front position's ring on the last tick (tests and the bench).</summary>
    public (int Orks, int Defenders) HqWeights { get; private set; }

    /// <summary>The positions still held as bits (bit 0 W, 1 K, 2 V).</summary>
    public byte HqHeldBits
    {
        get
        {
            byte bits = 0;
            for (int k = 0; k < Positions; k++) if (!_hqFallen[k]) bits |= (byte)(1 << k);
            return bits;
        }
    }

    /// <summary>One tick of the defence, after the positions were stepped (called from <c>WarfrontObjectives</c>).</summary>
    void StepHq(ReadOnlySpan<Vector3> players, ref TfwBattleChange change)
    {
        if (HqMessage != 0 && --_hqMessageTicks <= 0) HqMessage = 0;
        HqTickFlags = 0;
        if (Won || HqLost) return;
        for (int k = 0; k < Positions; k++)
            if (!_hqFallen[k] && _objectives[k].State != TfwPositionState.Secured) HqPositionFell(k, players);
        if (HqLost) return;
        StepHqHold(players);
        if (HqLost) return;
        float t = HqSeconds;
        if (t >= WarfrontHqBattleRules.SurviveSeconds)
        {
            Won = true;
            change |= TfwBattleChange.PositionSecured | TfwBattleChange.Won;   // the positions still held pay their XP through the run's capture rule
            HqSay(WarfrontHqBattleRules.MessageHolds);
            return;
        }
        if (HqWavesOn) StepHqWaves(t, players);
        if (--_hqRepostTicks <= 0) { _hqRepostTicks = Ticks(1f); HqRepost(); }
    }

    /// <summary>
    /// The fall rule (designer review of W13b, ruling 2; every number ours): inside the front position's ring each Ork counts 1 (a Nob or bigger 3) against the defenders' weight, a standing human 6, a standing squad Marine 2, a Guardsman 1.
    /// While the Orks outweigh the defenders the hold drains from 1 to 0 in <see cref="WarfrontHqBattleRules.HoldDrainSeconds"/>, otherwise it recovers in <see cref="WarfrontHqBattleRules.HoldRecoverSeconds"/>; at 0 the position falls.
    /// </summary>
    void StepHqHold(ReadOnlySpan<Vector3> players)
    {
        int k = HqFront;
        if (k >= Positions) return;
        var at = _layout.Positions[k];
        float radius = _layout.Radius(k), reach = _layout.Reach(k);
        // Reads at most 192 Orks in the ring (W13c, review 2 low 5). The defenders' greatest weight is about 136: four humans 4 x 6 = 24, a squad of 36 Marines 36 x 2 = 72, and the 40 Guardsmen of the four squads 40 x 1 = 40. Orks counted
        // below that weight never outweigh the defenders, and 192 Orks weigh at least 192 (a Nob or bigger 3), so cutting the read at 192 never changes the answer. A later change of a weight, of the squads or of the humans' number must
        // keep 192 above the defenders' greatest weight (or raise this number).
        Span<ushort> ids = stackalloc ushort[192];
        int n = _enemies.Query(at, radius, ids, reach);
        int orks = 0;
        for (int i = 0; i < n; i++)
            if (_enemies.TryGet(ids[i], out var e) && e.Alive && !_enemies.Leashed(ids[i]))
                orks += TfwBattleRules.Big(e.Type) ? WarfrontHqBattleRules.BigOrkWeight : WarfrontHqBattleRules.OrkWeight;
        int defenders = 0;
        if (orks > 0)
        {
            float r2 = radius * radius;
            foreach (var p in Capturers(players))
                if (MathF.Abs(p.Z - at.Z) <= reach && new Vector2(p.X - at.X, p.Y - at.Y).LengthSquared() <= r2) defenders += WarfrontHqBattleRules.HumanWeight;
            defenders += _guards.CountInside(at, radius) * WarfrontHqBattleRules.GuardsmanWeight;
            if (_enemies.Warfront?.SquadTargets is { } source)
            {
                Span<MusouTarget> marines = stackalloc MusouTarget[WarfrontBattleRules.MaxSquadTargets];
                int m = Math.Clamp(source(marines), 0, marines.Length);
                for (int i = 0; i < m; i++)
                    if (MathF.Abs(marines[i].Position.Z - at.Z) <= reach && new Vector2(marines[i].Position.X - at.X, marines[i].Position.Y - at.Y).LengthSquared() <= r2) defenders += WarfrontHqBattleRules.MarineWeight;
            }
        }
        HqWeights = (orks, defenders);
        if (orks > defenders) _hqHold -= Dt / WarfrontHqBattleRules.HoldDrainSeconds;
        else _hqHold = MathF.Min(1f, _hqHold + Dt / WarfrontHqBattleRules.HoldRecoverSeconds);
        if (_hqHold > 0f) return;
        _objectives[k] = _objectives[k] with { State = TfwPositionState.Held, Progress = 0f };
        Secured--;
        HqPositionFell(k, players);
    }

    void HqSay(byte message)
    {
        HqMessage = message;
        _hqMessageTicks = Ticks(MessageSeconds);
    }

    /// <summary>Position <paramref name="k"/> fell: lost for the battle (its ring closed), the next stage's sources open, a flood of Orks at them.</summary>
    void HqPositionFell(int k, ReadOnlySpan<Vector3> players)
    {
        _hqFallen[k] = true;
        HqTickFlags |= 16;
        HqFallenCount++;
        _hqHold = 1f;
        HqRout(k);
        if (_layout.Radii is { } radii && k < radii.Length) radii[k] = 0.01f;   // a ring of a centimetre: nobody is inside it, so no capture, no retake, no Guardsman holding it (the position's place stays, for the markers and the Marines' goal)
        if (k == Positions - 1)
        {
            HqLost = true;
            HqSay(WarfrontHqBattleRules.MessageFallen);
            return;
        }
        HqSay(k == 0 ? WarfrontHqBattleRules.MessageWallFell : WarfrontHqBattleRules.MessageKeepFell);
        _hqStageQueue.Enqueue((k + 1, WarfrontHqBattleRules.BreachFlood));   // made over the next ticks, SpawnsPerTick at a time
        _hqRepostTicks = 0;
    }

    /// <summary>
    /// A fall is a rout (W13c, designer review of W13b ruling c; ours, an estimate): every Guardsman posted to position <paramref name="k"/> is lost, one <see cref="GuardSim.KillOne"/> at a time so the deaths are seen (not <see cref="GuardSim.Dismiss"/>,
    /// which takes a squad out with no death). Squads posted to a later position stay. Before this rule the survivors went on to the next position and the defence grew stronger as it retreated (a passive human won with K never reached).
    /// </summary>
    void HqRout(int k)
    {
        if (!_guardsmen) return;
        for (int s = 0; s < GuardSim.MaxSquads; s++)
        {
            if (_squadArmy[s] != HqSquadOwner || _hqGoal[s] != k) continue;
            while (_guards.AliveIn(s) > 0 && _guards.KillOne(s, _guards.Centre(s))) HqGuardsRouted++;
            _hqGoal[s] = -1;   // nobody left to re-post
        }
    }

    /// <summary>Every second: a squad whose position fell goes to the front-most one still held (a squad of a fallen position is gone, <see cref="HqRout"/>, so this serves the squads whose own position still stands); one that stopped outside its post is sent again.</summary>
    void HqRepost()
    {
        int front = HqFront;
        for (int s = 0; s < GuardSim.MaxSquads; s++)
        {
            if (_squadArmy[s] != HqSquadOwner || _guards.AliveIn(s) == 0) continue;
            int goal = _hqGoal[s];
            if (goal < 0 || goal >= Positions || _hqFallen[goal]) goal = front;
            if (goal >= Positions) continue;
            if (goal != _hqGoal[s]) { _hqGoal[s] = goal; Send(s, goal); }
            else if (_guards.Arrived(s) && !OnGoal(s, goal)) Send(s, goal);
        }
    }

    /// <summary>
    /// The waves: a wave begins to muster (leashed) on the assembly ground <see cref="WarfrontHqBattleRules.MusterLeadSeconds"/> before it leaves, at most <see cref="WarfrontHqBattleRules.SpawnsPerTick"/> Orks a tick; at its time what is mustered is let
    /// go in chunks and each open stage's share is made at the stages' sources (the same spread). Three waves in all (<see cref="WarfrontHqBattleRules.ScheduledWaves"/>).
    /// </summary>
    void StepHqWaves(float t, ReadOnlySpan<Vector3> players)
    {
        HqReleaseChunks(t);   // the chunks of the wave being let go (the earlier wave's rest goes on while the next one musters)
        int budget = WarfrontHqBattleRules.SpawnsPerTick;
        StepHqStageQueue(players, ref budget);
        int wave = HqWavesSent + 1;
        if (wave > WarfrontHqBattleRules.ScheduledWaves) return;
        float leaves = WarfrontHqBattleRules.WaveLeaves(wave);
        if (_hqMustered < wave && t >= leaves - WarfrontHqBattleRules.MusterLeadSeconds) HqBeginMuster(wave);
        if (_hqMustered == wave && (_hqMusterLeft > 0 || _hqBossLeft > 0)) StepHqMuster(wave, ref budget);
        if (_hqMustered != wave || t < leaves) return;
        // it leaves: what could not be made under the cap is lost, the rest goes in chunks
        HqOrksSkipped += _hqMusterLeft;
        _hqMusterLeft = 0;
        _hqBossLeft = 0;
        HqWavesSent = wave;
        HqTickFlags |= 8;
        _hqChunkCount[Math.Min(63, wave)] = Math.Max(1, (_hqMusterMade + WarfrontHqBattleRules.ChunkSize - 1) / WarfrontHqBattleRules.ChunkSize);
        _hqReleasing = wave;
        _hqChunksReleased = 0;
        HqSay(WarfrontHqBattleRules.MessageWave);
        for (int stage = 1; stage <= 2; stage++)
            if (HqSourceOpen(stage)) _hqStageQueue.Enqueue((stage, HqStageShare(wave)));
        HqReleaseChunks(t);
    }

    int _hqReleasing, _hqMusterLeft, _hqMusterMade, _hqBossLeft;
    readonly Queue<(int Stage, int Left)> _hqStageQueue = new();
    readonly List<Vector3> _hqOpen = new();   // HqSpawnAtStage's list of open sources, reused

    /// <summary>Lets the next chunk of the wave leaving go when its time has come (<see cref="WarfrontHqBattleRules.ChunkGapSeconds"/> apart).</summary>
    void HqReleaseChunks(float t)
    {
        int wave = Math.Min(63, _hqReleasing);
        if (wave == 0) return;
        float leaves = WarfrontHqBattleRules.WaveLeaves(wave);
        int chunks = _hqChunkCount[wave];
        while (_hqChunksReleased < chunks && t >= leaves + _hqChunksReleased * WarfrontHqBattleRules.ChunkGapSeconds)
        { _enemies.ReleaseGroup(HqGroup(wave, _hqChunksReleased++)); HqTickFlags |= 4; }
        if (_hqChunksReleased >= chunks) _hqReleasing = 0;
    }

    readonly int[] _hqChunkCount = new int[64];

    static int HqStageShare(int wave) => (int)MathF.Round(WarfrontHqBattleRules.WaveSize(wave) * WarfrontHqBattleRules.StageShare);
    int HqStageOrks(int wave) => HqStageShare(wave) * ((HqSourceOpen(1) ? 1 : 0) + (HqSourceOpen(2) ? 1 : 0));

    /// <summary>The Orks the defence may have alive: the lesser of <see cref="WarfrontHqBattleRules.AliveCapHq"/> and the battle's own cap for the humans in the field.</summary>
    int HqCap => Math.Min(WarfrontHqBattleRules.AliveCapHq, TfwBattleRules.AliveCap(_humansNow));

    /// <summary>
    /// The places a wave musters round: the assembly sources within <see cref="WarfrontHqBattleRules.MusterHalfWidth"/> of the ramp's centre line (the ramp is 30 m wide; a wave mustered wider piled against its side), and, so that the wave stands in depth
    /// (rows behind each other, not 200 Orks on one 11 m disc), a place every <see cref="WarfrontHqBattleRules.MusterRowMetres"/> along the line between the deepest and the nearest of them.
    /// </summary>
    static Vector3[] HqHome() => HomeSpots.Value;

    /// <summary>The muster places, computed once (it allocated on every tick of a muster and in every stage spawn; lazy so a battle that is not the HQ never builds the HQ map).</summary>
    static readonly Lazy<Vector3[]> HomeSpots = new(BuildHome);

    static Vector3[] BuildHome()
    {
        var home = WarfrontHqLayout.SourcesOf(0).Where(p => MathF.Abs(p.X) <= WarfrontHqBattleRules.MusterHalfWidth).ToArray();
        if (home.Length == 0) return [.. WarfrontHqLayout.SourcesOf(0)];
        float lo = home.Min(p => p.Y), hi = home.Max(p => p.Y);
        var z = home[0].Z;
        var rows = new List<Vector3>();
        for (float y = lo; y <= hi + 0.01f; y += WarfrontHqBattleRules.MusterRowMetres) rows.Add(new Vector3(home[0].X, y, z));
        return [.. rows];
    }

    /// <summary>Wave <paramref name="wave"/> begins to muster: its assembly-ground share (all of it before a stage opens) is owed, and the Boss Orks, from the wave <see cref="WarfrontHqBattleRules.BossWave"/> on, one per human.</summary>
    void HqBeginMuster(int wave)
    {
        _hqMustered = wave;
        _hqMusterLeft = WarfrontHqBattleRules.WaveSize(wave) - HqStageOrks(wave);
        _hqMusterMade = 0;
        _hqBossLeft = wave >= WarfrontHqBattleRules.BossWave ? WarfrontHqBattleRules.BossNobsPerHuman * _humansNow : 0;
    }

    /// <summary>One tick of the muster: up to <paramref name="budget"/> leashed Orks (none while the alive cap or the pool is full: it waits, and what is still owed when the wave leaves is lost), then the Boss Orks.</summary>
    void StepHqMuster(int wave, ref int budget)
    {
        var sources = HqHome();
        int cap = HqCap;
        while (budget > 0 && _hqMusterLeft > 0)
        {
            if (Counted() >= cap || PoolUsed >= TfwBattleRules.CrowdPoolRoom) break;
            budget--;
            _hqMusterLeft--;
            HqTickFlags |= 1;
            if (HqSpawnOne(sources[_hqMusterMade % sources.Length], leashed: true, HqGroup(wave, _hqMusterMade / WarfrontHqBattleRules.ChunkSize)) != 0) _hqMusterMade++;
        }
        while (budget > 0 && _hqMusterLeft == 0 && _hqBossLeft > 0)
        {
            budget--;
            int n = --_hqBossLeft;
            var at = DiscSpot(sources[n % sources.Length]);
            ushort id = _enemies.SpawnLeashed(MusouEnemyType.BossNob, at, FaceCentre(at), Health(MusouEnemyType.BossNob), HqGroup(wave, 0), TfwBattleRules.CrowdAggro);
            if (id == 0) continue;
            TrackBig(id);
            HqOrksMade++;
        }
    }

    /// <summary>One Ork of a wave: a Boy or a Shoota, or, by the elite share, an 'Ard Boy or a Nob; on a disc round <paramref name="source"/>. Returns its id (0: no room).</summary>
    ushort HqSpawnOne(Vector3 source, bool leashed, int group)
    {
        _hqEliteOwed += EliteShare;
        MusouEnemyType type;
        if (_hqEliteOwed >= 1f) { _hqEliteOwed -= 1f; type = (_hqMade++ & 1) == 0 ? MusouEnemyType.ArdBoy : MusouEnemyType.Nob; }
        else type = Fodder(_picked++);
        var at = DiscSpot(source);
        ushort id = leashed
            ? _enemies.SpawnLeashed(type, at, FaceCentre(at), Health(type), group, TfwBattleRules.CrowdAggro)
            : _enemies.Spawn(type, at, FaceCentre(at), Health(type));
        if (id == 0) { HqOrksSkipped++; return 0; }
        if (TfwBattleRules.Big(type)) TrackBig(id);
        HqOrksMade++;
        return id;
    }

    /// <summary>One tick of the stages' queue (the breach floods and the waves' stage shares): up to <paramref name="budget"/> Orks across the entries, in order.</summary>
    void StepHqStageQueue(ReadOnlySpan<Vector3> players, ref int budget)
    {
        int entries = _hqStageQueue.Count;
        while (budget > 0 && entries-- > 0)
        {
            var (stage, left) = _hqStageQueue.Dequeue();
            int take = Math.Min(budget, left);
            int tried = HqSpawnAtStage(stage, take, players);
            if (tried > 0) HqTickFlags |= 2;
            budget -= tried;
            left -= tried;
            if (left > 0 && tried >= take) _hqStageQueue.Enqueue((stage, left));
            else if (left > 0) HqOrksSkipped += left;   // the alive cap or the pool stopped it: the rest is dropped (counted)
        }
    }

    /// <summary>
    /// Up to <paramref name="count"/> Orks (unleashed: they walk at once) at the sources of <paramref name="stage"/>, a source only while no human is within <see cref="WarfrontHqBattleRules.SourceSightMetres"/> of it (so none within
    /// <see cref="WarfrontHqBattleRules.SourceClearanceMetres"/>). What no open source could take is made on the assembly ground instead. Returns the number tried (fewer than <paramref name="count"/>: the alive cap or the pool stopped it).
    /// </summary>
    int HqSpawnAtStage(int stage, int count, ReadOnlySpan<Vector3> players)
    {
        var open = _hqOpen;
        open.Clear();
        foreach (var s in WarfrontHqLayout.SourcesOf(stage)) if (HqClear(s, players)) open.Add(s);
        var home = HqHome();
        int cap = HqCap;
        for (int n = 0; n < count; n++)
        {
            if (Counted() >= cap || PoolUsed >= TfwBattleRules.CrowdPoolRoom) return n;
            var from = open.Count > 0 ? open[n % open.Count] : home[n % home.Length];
            HqSpawnOne(from, leashed: false, 0);
        }
        return count;
    }

    /// <summary>Test aid: <paramref name="count"/> unleashed Orks made on the assembly ground now (they walk at once); the number made.</summary>
    public int HqSendForTests(int count)
    {
        var sources = HqHome();
        int made = 0;
        for (int n = 0; n < count; n++) if (HqSpawnOne(sources[n % sources.Length], leashed: false, 0) != 0) made++;
        return made;
    }

    /// <summary>
    /// Whether no human may see a stage source at <paramref name="at"/> (W13c, review 2 item 8): none within <see cref="WarfrontHqBattleRules.SourceClearanceMetres"/>, and none with a straight eye-to-eye line to it over the stage's weapon collision
    /// (<c>WarfrontBattleRules.HqSight</c>, the fortress's own ray). With no ray supplied the 60 m stand-in applies: no human within <see cref="WarfrontHqBattleRules.SourceSightMetres"/>.
    /// </summary>
    /// <summary>Whether no human may see a stage source at <paramref name="at"/> now (<see cref="HqClear"/>; the bench's open-seconds print and tests).</summary>
    public bool HqSourceClear(Vector3 at, ReadOnlySpan<Vector3> players) => HqClear(at, players);

    bool HqClear(Vector3 at, ReadOnlySpan<Vector3> players)
    {
        var sight = _enemies.Warfront?.HqSight;
        var eyeAt = at + new Vector3(0f, 0f, WarfrontHqBattleRules.EyeHeight);
        foreach (var p in players)
        {
            float d = Vector3.Distance(p, at);
            if (d < WarfrontHqBattleRules.SourceClearanceMetres) return false;
            if (sight is null) { if (d < WarfrontHqBattleRules.SourceSightMetres) return false; }
            else if ((WarfrontHqBattleRules.SightRangeMetres <= 0f || d <= WarfrontHqBattleRules.SightRangeMetres) && sight(p + new Vector3(0f, 0f, WarfrontHqBattleRules.EyeHeight), eyeAt)) return false;
        }
        return true;
    }
}
