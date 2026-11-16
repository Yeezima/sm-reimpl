using System.Numerics;
using SmSim.Extras.Warfront;
using SmSim.Extras.Warfront.Hq;

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
    /// <summary>Test aid (the measurement tool): false stops the scheduled waves.</summary>
    public bool HqWavesOn { get; set; } = true;
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
        // FORTRESS-SPOT (W14b): if (rules.Fortress is not null) { ...fortress call...; return; } goes here, before the HQ branch
        if (!WarfrontHq) return;
        handled = true;
        for (int k = 0; k < Positions; k++) _objectives[k] = new TfwObjective((byte)k, TfwPositionState.Secured, 1f, _layout.Positions[k], _layout.Radius(k));
        _objectives[CommanderObjective] = new TfwObjective(CommanderObjective, TfwPositionState.Locked, 0f, _layout.Hq, TfwMap.HqRadius);
        Secured = Positions;
        Armies = false;     // no armies, garrisons or retreats
        Trickle = false;    // no trickle: the waves are this file's
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
                int s = _guards.SpawnSquad(at, FaceCentre(at));
                if (s < 0) continue;
                _squadArmy[s] = HqSquadOwner;
                _hqGoal[s] = k;
                Send(s, k);
            }
        }
    }

    /// <summary>Only the front-most position still held can lose progress toward Held (<c>WarfrontRetake</c>).</summary>
    partial void WarfrontRetake(int position, ref bool allowed)
    {
        // FORTRESS-SPOT (W14b): if (rules.Fortress is not null) { ...fortress call...; return; } goes here, before the HQ branch
        if (!WarfrontHq) return;
        allowed = position == HqFront;
    }

    /// <summary>The battle's own crowds and trickle stay off in an HQ defence: its sources are opened by stage (<see cref="HqSourceOpen"/>) and fed by the waves (<c>WarfrontSourceActive</c>).</summary>
    partial void WarfrontSourceActive(int source, ref bool active, ref bool handled)
    {
        // FORTRESS-SPOT (W14b): if (rules.Fortress is not null) { ...fortress call...; return; } goes here, before the HQ branch
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
        if (Won || HqLost) return;
        for (int k = 0; k < Positions; k++)
            if (!_hqFallen[k] && _objectives[k].State != TfwPositionState.Secured) HqPositionFell(k, players);
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

    void HqSay(byte message)
    {
        HqMessage = message;
        _hqMessageTicks = Ticks(MessageSeconds);
    }

    /// <summary>Position <paramref name="k"/> fell: lost for the battle (its ring closed), the next stage's sources open, a flood of Orks at them.</summary>
    void HqPositionFell(int k, ReadOnlySpan<Vector3> players)
    {
        _hqFallen[k] = true;
        HqFallenCount++;
        if (_layout.Radii is { } radii && k < radii.Length) radii[k] = 0.01f;   // a ring of a centimetre: nobody is inside it, so no capture, no retake, no Guardsman holding it (the position's place stays, for the markers and the Marines' goal)
        if (k == Positions - 1)
        {
            HqLost = true;
            HqSay(WarfrontHqBattleRules.MessageFallen);
            return;
        }
        HqSay(k == 0 ? WarfrontHqBattleRules.MessageWallFell : WarfrontHqBattleRules.MessageKeepFell);
        HqSpawnAtStage(k + 1, WarfrontHqBattleRules.BreachFlood, players);
        _hqRepostTicks = 0;
    }

    /// <summary>Every second: a squad whose position fell goes to the front-most one still held; one that stopped outside its post is sent again.</summary>
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

    /// <summary>The waves: a wave musters (leashed) on the assembly ground <see cref="WarfrontHqBattleRules.MusterLeadSeconds"/> before it leaves, and is let go at its time; from then each open stage sends its share at once.</summary>
    void StepHqWaves(float t, ReadOnlySpan<Vector3> players)
    {
        HqReleaseChunks(t);   // the chunks of the wave being let go (the earlier wave's rest goes on while the next one musters)
        int wave = HqWavesSent + 1;
        float leaves = WarfrontHqBattleRules.WaveLeaves(wave);
        if (leaves >= WarfrontHqBattleRules.SurviveSeconds) return;
        if (_hqMustered < wave && t >= leaves - WarfrontHqBattleRules.MusterLeadSeconds) HqMuster(wave);
        if (_hqMustered != wave || t < leaves) return;
        HqWavesSent = wave;
        _hqReleasing = wave;
        _hqChunksReleased = 0;
        HqSay(WarfrontHqBattleRules.MessageWave);
        for (int stage = 1; stage <= 2; stage++)
            if (HqSourceOpen(stage)) HqSpawnAtStage(stage, HqStageShare(wave), players);
        HqReleaseChunks(t);
    }

    int _hqReleasing;

    /// <summary>Lets the next chunk of the wave leaving go when its time has come (<see cref="WarfrontHqBattleRules.ChunkGapSeconds"/> apart).</summary>
    void HqReleaseChunks(float t)
    {
        int wave = Math.Min(63, _hqReleasing);
        if (wave == 0) return;
        float leaves = WarfrontHqBattleRules.WaveLeaves(wave);
        int chunks = _hqChunkCount[wave];
        while (_hqChunksReleased < chunks && t >= leaves + _hqChunksReleased * WarfrontHqBattleRules.ChunkGapSeconds)
            _enemies.ReleaseGroup(HqGroup(wave, _hqChunksReleased++));
        if (_hqChunksReleased >= chunks) _hqReleasing = 0;
    }

    readonly int[] _hqChunkCount = new int[64];

    static int HqStageShare(int wave) => (int)MathF.Round(WarfrontHqBattleRules.WaveSize(wave) * WarfrontHqBattleRules.StageShare);
    int HqStageOrks(int wave) => HqStageShare(wave) * ((HqSourceOpen(1) ? 1 : 0) + (HqSourceOpen(2) ? 1 : 0));

    /// <summary>Wave <paramref name="wave"/> musters: its assembly-ground share (all of it before a stage opens) made leashed; the Boss Orks come with the wave from <see cref="WarfrontHqBattleRules.BossWave"/> on.</summary>
    void HqMuster(int wave)
    {
        _hqMustered = wave;
        int size = WarfrontHqBattleRules.WaveSize(wave) - HqStageOrks(wave);
        var sources = WarfrontHqLayout.SourcesOf(0).Where(p => MathF.Abs(p.X) <= WarfrontHqBattleRules.MusterHalfWidth).ToArray();   // straight in front of the ramp: a wave mustered at the corners piled against the ramp's side
        int cap = TfwBattleRules.AliveCap(_humansNow);
        for (int n = 0; n < size; n++)
        {
            if (Counted() >= cap || PoolUsed >= TfwBattleRules.CrowdPoolRoom) { HqOrksSkipped += size - n; break; }
            HqSpawnOne(sources[n % sources.Length], leashed: true, HqGroup(wave, n / WarfrontHqBattleRules.ChunkSize));
        }
        _hqChunkCount[Math.Min(63, wave)] = Math.Max(1, (size + WarfrontHqBattleRules.ChunkSize - 1) / WarfrontHqBattleRules.ChunkSize);
        if (wave < WarfrontHqBattleRules.BossWave) return;
        for (int n = 0; n < WarfrontHqBattleRules.BossNobsPerHuman * _humansNow; n++)
        {
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

    /// <summary>
    /// <paramref name="count"/> Orks (unleashed: they walk at once) at the sources of <paramref name="stage"/>, a source only while no human is within <see cref="WarfrontHqBattleRules.SourceSightMetres"/> of it (so none within
    /// <see cref="WarfrontHqBattleRules.SourceClearanceMetres"/>). What no open source could take is made on the assembly ground instead. Returns the number made.
    /// </summary>
    int HqSpawnAtStage(int stage, int count, ReadOnlySpan<Vector3> players)
    {
        int made = 0;
        var open = new List<Vector3>();
        foreach (var s in WarfrontHqLayout.SourcesOf(stage)) if (HqClear(s, players)) open.Add(s);
        var home = WarfrontHqLayout.SourcesOf(0).ToArray();
        int cap = TfwBattleRules.AliveCap(_humansNow);
        for (int n = 0; n < count; n++)
        {
            if (Counted() >= cap || PoolUsed >= TfwBattleRules.CrowdPoolRoom) { HqOrksSkipped += count - n; break; }
            var from = open.Count > 0 ? open[n % open.Count] : home[n % home.Length];
            if (HqSpawnOne(from, leashed: false, 0) != 0) made++;
        }
        return made;
    }

    /// <summary>Test aid: <paramref name="count"/> unleashed Orks made on the assembly ground now (they walk at once); the number made.</summary>
    public int HqSendForTests(int count)
    {
        var sources = WarfrontHqLayout.SourcesOf(0).ToArray();
        int made = 0;
        for (int n = 0; n < count; n++) if (HqSpawnOne(sources[n % sources.Length], leashed: false, 0) != 0) made++;
        return made;
    }

    /// <summary>Whether no human is within <see cref="WarfrontHqBattleRules.SourceSightMetres"/> of <paramref name="at"/>.</summary>
    static bool HqClear(Vector3 at, ReadOnlySpan<Vector3> players)
    {
        foreach (var p in players)
            if (Vector3.Distance(p, at) < WarfrontHqBattleRules.SourceSightMetres) return false;
        return true;
    }
}
