using System.Numerics;

namespace SmSim.Extras.Musou;

/// <summary>
/// The Scrap Meks and their contraptions (expansion S4, docs/musou-expansion.md "Scrap Meks"; Musou Mode war battles only). Every value **ours / estimate** (owner brief 2026-11-07) unless the brief names
/// it (the counts, the hit points, the radii, the build time, the beacon's 30 s / 10 / 40, the turret's 4 s / 50 m, the trap's 50 %). Times in seconds, distances in metres. A change raises
/// <see cref="MusouSim.Version"/>.
/// </summary>
public static class MusouMekRules
{
    /// <summary>Meks alive at once in a battle (owner brief: 5), and the wait before a dead one is replaced (owner brief: 300 s, counted from his death).</summary>
    public const int MaxMeks = 5;
    public const float RespawnSeconds = 300f;
    /// <summary>A Mek runs from any player (an AI Marine too) within this (owner brief: 20 m; flat, height counted x3), a little faster than his walk (ours).</summary>
    public const float FleeRadius = 20f, FleeSpeedScale = 1.15f;
    /// <summary>His hiding place (ours): a graph node at least this far from every player, searched on rings <see cref="HideRings"/> round him; nearer walls (the graph's edge distance) count as cover.</summary>
    public const float HideMinPlayerDistance = 30f;
    public static readonly float[] HideRings = [12f, 22f, 32f, 42f];
    /// <summary>A hiding place is given up when a player comes this near it (ours), or when he has not got nearer to it for <see cref="HideStuckSeconds"/>.</summary>
    public const float HideCompromised = 22f, HideStuckSeconds = 8f;
    /// <summary>He is at his place within this (flat), and two Meks' places stay this far apart (ours).</summary>
    public const float HideArrive = 1.5f, MekSpacing = 10f;
    /// <summary>One contraption takes this long to build (owner brief: 15 s); a build broken off by a flight is lost (ours).</summary>
    public const float BuildSeconds = 15f;
    /// <summary>Most contraptions alive per Mek, built or under way (owner brief: 3), and shield generators on the whole map (owner brief: 1).</summary>
    public const int MaxPerMek = 3, MaxShields = 1;

    /// <summary>Shield generator (owner brief): 800 hp, a dome of 10 m round it that stops shots fired from outside it (walk in to fight). The owner, 2026-11-08: it also stops the Guardsmen's fire, Ork blasts
    /// and grenades (every projectile) from outside; never movement or melee.</summary>
    public const float ShieldHealth = 800f, ShieldRadius = 10f;
    /// <summary>Reinforcement beacon (owner brief): 400 hp; every 30 s a drop of 10 Boyz, at most 40 alive from it. Ours: the drop's flight (it lands this long after it is called) and the ring it lands on.</summary>
    public const float BeaconHealth = 400f, BeaconPeriod = 30f, DropFlightSeconds = 3f, DropRingMin = 2f, DropRingMax = 6f;
    public const int BeaconDrop = 10, BeaconMaxAlive = 40;
    /// <summary>Rokkit turret (owner brief): 500 hp, the Rokkit Boy's rocket every 4 s at up to 50 m. Ours: the muzzle's height, how far in front of the turret it leaves, the retry with no target.</summary>
    public const float TurretHealth = 500f, TurretPeriod = 4f, TurretRange = 50f, TurretMuzzleHeight = 1.4f, TurretMuzzleForward = 0.8f, TurretRetrySeconds = 0.5f;
    /// <summary>Scrap trap (owner brief): 150 hp, radius 3; a Mek's enemy stepping in sets it off for 50 % of a player's maximum health (health and armour together, ours), Orks spared. Ours: the height a body
    /// counts in it, and that it is spent when it goes off.</summary>
    public const float TrapHealth = 150f, TrapRadius = 3f, TrapVertical = 2f, TrapShare = 0.5f;
    /// <summary>The trap destroyed by a player (shot, blast, blow, or an Ork blast he caused) goes off as an Ork blast that hurts Orks too (ours: radius 4, 150 at the centre, 25 % at the edge).</summary>
    public static readonly OrkBlastSpec TrapBlast = new(4f, 150f) { EdgeFraction = 0.25f };
    /// <summary>The owner, 2026-11-08: "make ai marines be outright killed by scrap mek traps". An AI Marine in a trap that goes off dies at once (this damage before his tenth, far above any body).</summary>
    public const float TrapAiMarineDamage = 1e7f;
    /// <summary>
    /// The owner, 2026-11-08: "guardsmen should target contraptions only the rokkit turret contraptions". A Guardsman shoots a built Rokkit turret in his range and sight; the even-numbered men of a squad
    /// (half of it, ours) take one before the Orks, the rest only when no Ork is to be shot.
    /// </summary>
    public const bool GuardsShootTurrets = true;

    /// <summary>The players' shots meet a contraption as an upright cylinder of this radius and height (ours; the drawn placeholders are this size).</summary>
    public static (float Radius, float Height) Body(MusouContraptionKind kind) => kind switch
    {
        MusouContraptionKind.ShieldGenerator => (0.9f, 1.8f),
        MusouContraptionKind.Beacon => (0.6f, 3.2f),
        MusouContraptionKind.Turret => (0.9f, 1.6f),
        _ => (1.2f, 0.5f),
    };

    /// <summary>The hit points of a kind.</summary>
    public static float Health(MusouContraptionKind kind) => kind switch
    {
        MusouContraptionKind.ShieldGenerator => ShieldHealth,
        MusouContraptionKind.Beacon => BeaconHealth,
        MusouContraptionKind.Turret => TurretHealth,
        _ => TrapHealth,
    };

    /// <summary>
    /// The Meks' cues (<see cref="MusouEventKind.Telegraph"/>'s value, after S3's 32 to 37; the event's enemy is the Mek, its player the contraption's id, its vector the contraption's place, unless named):
    /// a build begun, a contraption finished, one destroyed, a trap set off (by stepping in), a trap's blast (the credited player in the event's enemy field is not used: the player field is the contraption), a
    /// beacon's drop called (it lands in <see cref="DropFlightSeconds"/>), the drop landed, and a turret's rocket (the event's enemy is the rocket's serial, its vector the aim point).
    /// </summary>
    public const byte BuildCue = 38, BuiltCue = 39, DestroyedCue = 40, TrapCue = 41, TrapBlastCue = 42, DropCue = 43, DropLandCue = 44, TurretFireCue = 45;
}

/// <summary>The four contraptions (wire byte).</summary>
public enum MusouContraptionKind : byte { ShieldGenerator = 0, Beacon = 1, Turret = 2, Trap = 3 }

/// <summary>A contraption under way or standing.</summary>
public enum MusouContraptionState : byte { Building = 0, Active = 1 }

/// <summary>
/// One contraption as the store hands it out (and tag 17 carries it): its id (1 to 255), kind, state, the Mek who built it, where it stands, its health, the build's progress (0 to 1), the turret's aim
/// (yaw, radians), and a beacon's drop (whether one is in flight, and the Boyz queued for it).
/// </summary>
public readonly record struct MusouContraption(byte Id, MusouContraptionKind Kind, MusouContraptionState State, ushort Mek, Vector3 Position, float Health, float Progress, float Aim,
    bool DropInFlight, byte Queued)
{
    public float MaxHealth => MusouMekRules.Health(Kind);
}

/// <summary>A trap set off by stepping in (the World hurts the Mek's enemies in its radius, Orks spared).</summary>
public readonly record struct MusouTrapTrigger(byte Contraption, ushort Mek, Vector3 Point);

/// <summary>A trap destroyed by a player: an Ork blast at <paramref name="Point"/> credited to <paramref name="Credit"/>.</summary>
public readonly record struct MusouTrapBlast(byte Contraption, Vector3 Point, int Credit);

/// <summary>
/// The Scrap Meks of one battle and their contraptions (expansion S4; the battle owns one, <c>Campaign/TfwBattle.Meks.cs</c>, and the World steps it, <c>Campaign/World.TfwMeks.cs</c>). Deterministic: the Meks
/// in slot order, the contraptions in the order they were begun.
/// <list type="bullet">
/// <item>Five Meks (<see cref="MusouMekRules.MaxMeks"/>), each in a slot: at the start all five come at the spawn spots farthest from the players; a dead one's slot waits
/// <see cref="MusouMekRules.RespawnSeconds"/> from his death, then a new Mek comes.</item>
/// <item>A Mek roams to a hiding place far from players (<see cref="MusouSim.MekOrder"/>), runs from players within <see cref="MusouMekRules.FleeRadius"/> (the pool does that,
/// <c>MusouSim.Mek.cs</c>), and at his place builds: one contraption at a time, <see cref="MusouMekRules.BuildSeconds"/> each, at most <see cref="MusouMekRules.MaxPerMek"/> alive, at most
/// <see cref="MusouMekRules.MaxShields"/> shield generator on the map. A flight breaks a build off (it is lost). His death destroys all his contraptions (no trap goes off).</item>
/// <item>The beacon: every <see cref="MusouMekRules.BeaconPeriod"/> its queue takes a drop of <see cref="MusouMekRules.BeaconDrop"/> Boyz (it holds one drop at most); a drop flies when none is in flight and
/// fewer than <see cref="MusouMekRules.BeaconMaxAlive"/> of its Boyz live, carrying what the cap leaves, and lands <see cref="MusouMekRules.DropFlightSeconds"/> later round the beacon. The beacon destroyed:
/// the drop in flight and the queue are cancelled; Boyz already landed stay and fight as ordinary Orks.</item>
/// <item>The turret: every <see cref="MusouMekRules.TurretPeriod"/> the Rokkit Boy's rocket (<see cref="MusouSim.LaunchTurretRocket"/>, the same projectile, warning and Ork blast) at the nearest target in
/// <see cref="MusouMekRules.TurretRange"/> it can see.</item>
/// <item>The trap: a player or a Guardsman within <see cref="MusouMekRules.TrapRadius"/> sets it off (<see cref="TakeTriggers"/>; it is spent). Destroyed by a player it goes off as an Ork blast
/// (<see cref="TakeBlasts"/>).</item>
/// <item>The shield generator: its dome stops shots from outside (<see cref="DomeEntry"/>, <see cref="Shielded"/>).</item>
/// </list>
/// </summary>
public sealed class MusouMeks
{
    sealed class Mek
    {
        public ushort Id;
        public int RespawnAt;                 // the step a dead slot's Mek comes back on
        public Vector3 Hide;
        public bool HasHide;
        public float Best;                    // his nearest to his place since it was picked
        public int BestStep;
        public int Rotation;                  // the next kind to build (an index into Order)
        public byte Building;                 // the contraption under way (0: none)
    }

    sealed class Structure
    {
        public byte Id;
        public MusouContraptionKind Kind;
        public MusouContraptionState State;
        public ushort Mek;
        public Vector3 Position;
        public float Health, Progress, Aim;
        public int NextDrop, LandAt, Cooldown;  // steps
        public bool InFlight;
        public int Queued, Carried;
        public readonly List<ushort> Boyz = new();
    }

    static readonly MusouContraptionKind[] Order = [MusouContraptionKind.Trap, MusouContraptionKind.Turret, MusouContraptionKind.Beacon, MusouContraptionKind.ShieldGenerator];

    readonly MusouSim _sim;
    readonly Mek[] _meks = new Mek[MusouMekRules.MaxMeks];
    readonly List<Structure> _structures = new();
    readonly List<MusouTrapTrigger> _triggers = new();
    readonly List<MusouTrapBlast> _blasts = new();
    int _now;
    byte _lastId;

    public MusouMeks(MusouSim sim)
    {
        _sim = sim;
        for (int k = 0; k < _meks.Length; k++) _meks[k] = new Mek { Rotation = k % Order.Length };
    }

    int Ticks(float seconds) => Math.Max(1, (int)MathF.Round(seconds * Math.Max(1, _sim.TickRate)));
    float Dt => 1f / Math.Max(1, _sim.TickRate);

    /// <summary>Capture aid (ours; the server's <c>SM_MUSOU_S4_DEMO</c>): when set, each Mek's hiding place is this function's answer for his slot (null: his own search), and a new beacon's first drop is due at once.</summary>
    public Func<int, Vector3?>? DemoHide { get; set; }

    /// <summary>Test aid (ours: always): whether a Mek at his place begins a new build (false: he finishes the one under way and stands).</summary>
    public bool Builds { get; set; } = true;

    /// <summary>
    /// Test aid: a finished contraption of <paramref name="kind"/> for slot <paramref name="slot"/>'s living Mek at <paramref name="at"/>, as his own build would leave it (the limits kept: null over
    /// <see cref="MusouMekRules.MaxPerMek"/> for him, over <see cref="MusouMekRules.MaxShields"/> on the map, or with no Mek in the slot).
    /// </summary>
    public MusouContraption? PlaceBuilt(int slot, MusouContraptionKind kind, Vector3 at)
    {
        var m = _meks[slot];
        if (m.Id == 0 || OwnedBy(m.Id) >= MusouMekRules.MaxPerMek || (kind == MusouContraptionKind.ShieldGenerator && ShieldsNow() >= MusouMekRules.MaxShields)) return null;
        byte id = NextId();
        if (id == 0) return null;
        var s = new Structure
        {
            Id = id, Kind = kind, State = MusouContraptionState.Active, Mek = m.Id, Position = at, Health = MusouMekRules.Health(kind), Progress = 1f,
            NextDrop = _now + Ticks(MusouMekRules.BeaconPeriod), Cooldown = _now,
        };
        _structures.Add(s);
        Built++;
        return View(s);
    }

    /// <summary>Steps taken (the battle's own clock for the Meks).</summary>
    public int Now => _now;
    /// <summary>Meks that came so far, contraptions finished, contraptions destroyed (cleanup included), beacon drops landed and their Boyz, turret rockets, traps set off.</summary>
    public int MeksCome { get; private set; }
    public int Built { get; private set; }
    public int Destroyed { get; private set; }
    public int DropsLanded { get; private set; }
    public int BoyzDropped { get; private set; }
    public int TurretRockets { get; private set; }
    public int TrapsSprung { get; private set; }

    /// <summary>The Mek of slot <paramref name="slot"/> (0: none now).</summary>
    public ushort MekId(int slot) => _meks[slot].Id;
    /// <summary>Steps until slot <paramref name="slot"/>'s Mek comes back (0 when he lives or is due).</summary>
    public int RespawnStepsLeft(int slot) => _meks[slot].Id != 0 ? 0 : Math.Max(0, _meks[slot].RespawnAt - _now);
    /// <summary>Meks alive now.</summary>
    public int AliveMeks { get { int n = 0; foreach (var m in _meks) if (m.Id != 0) n++; return n; } }
    /// <summary>Where slot <paramref name="slot"/>'s Mek is heading to hide (null: none picked).</summary>
    public Vector3? HideOf(int slot) => _meks[slot].HasHide ? _meks[slot].Hide : null;
    /// <summary>Contraptions now, built or under way.</summary>
    public int Count => _structures.Count;

    /// <summary>The contraptions in the order they were begun, written into <paramref name="into"/> (cleared first).</summary>
    public void CopyTo(List<MusouContraption> into)
    {
        into.Clear();
        foreach (var s in _structures) into.Add(View(s));
    }

    /// <summary>The contraption with <paramref name="id"/>.</summary>
    public bool TryGet(byte id, out MusouContraption c)
    {
        var s = Find(id);
        c = s is null ? default : View(s);
        return s is not null;
    }

    static MusouContraption View(Structure s) =>
        new(s.Id, s.Kind, s.State, s.Mek, s.Position, s.Health, s.Progress, s.Aim, s.InFlight, (byte)Math.Clamp(s.Queued + (s.InFlight ? s.Carried : 0), 0, 255));

    /// <summary>The Boyz a beacon dropped that still live (empty for another kind or an unknown id).</summary>
    public IReadOnlyList<ushort> BoyzOf(byte id) => Find(id)?.Boyz ?? (IReadOnlyList<ushort>)[];

    /// <summary>The traps set off since the last call, moved into <paramref name="into"/>.</summary>
    public void TakeTriggers(List<MusouTrapTrigger> into) { into.AddRange(_triggers); _triggers.Clear(); }
    /// <summary>The traps destroyed by players since the last call, moved into <paramref name="into"/>.</summary>
    public void TakeBlasts(List<MusouTrapBlast> into) { into.AddRange(_blasts); _blasts.Clear(); }

    Structure? Find(byte id)
    {
        foreach (var s in _structures) if (s.Id == id) return s;
        return null;
    }

    int ShieldsNow() { int n = 0; foreach (var s in _structures) if (s.Kind == MusouContraptionKind.ShieldGenerator) n++; return n; }
    int OwnedBy(ushort mek) { int n = 0; foreach (var s in _structures) if (s.Mek == mek) n++; return n; }

    // ------------------------------------------------------------------ the step

    /// <summary>
    /// One step (from the World, in a battle in play): the dead Meks' contraptions destroyed and their slots' timers started, the due Meks brought in at the <paramref name="spawns"/> farthest from the players,
    /// every Mek's place and build, the beacons' drops, the turrets' rockets and the traps. <paramref name="targets"/>: the players (ids above 0, AI Marines too) and the Guard squads (below 0), as the pool's;
    /// <paramref name="blocked"/>: whether a line between two points is blocked (null: never); <paramref name="guardsNear"/>: whether a living Guardsman stands within a radius of a point (null: never).
    /// </summary>
    public void Step(ReadOnlySpan<MusouTarget> targets, IReadOnlyList<Vector3> spawns, Func<Vector3, Vector3, bool>? blocked = null, Func<Vector3, float, bool>? guardsNear = null)
    {
        _now++;
        for (int k = 0; k < _meks.Length; k++)
        {
            var m = _meks[k];
            if (m.Id != 0 && !(_sim.TryGet(m.Id, out var e) && e.Alive && e.Type == MusouEnemyType.ScrapMek)) Died(m);
            if (m.Id == 0 && _now >= m.RespawnAt) Come(k, targets, spawns);
        }
        for (int k = 0; k < _meks.Length; k++) if (_meks[k].Id != 0) Think(k, targets);
        for (int i = 0; i < _structures.Count; i++)
        {
            var s = _structures[i];
            if (s.State != MusouContraptionState.Active) continue;
            switch (s.Kind)
            {
                case MusouContraptionKind.Beacon: StepBeacon(s); break;
                case MusouContraptionKind.Turret: StepTurret(s, targets, blocked); break;
                case MusouContraptionKind.Trap:
                    if (Sprung(s, targets, guardsNear)) { Remove(i--, s, MusouMekRules.TrapCue); TrapsSprung++; _triggers.Add(new MusouTrapTrigger(s.Id, s.Mek, s.Position)); }
                    break;
            }
        }
    }

    void Died(Mek m)
    {
        ushort id = m.Id;
        for (int i = _structures.Count - 1; i >= 0; i--) if (_structures[i].Mek == id) Remove(i, _structures[i], MusouMekRules.DestroyedCue);
        m.Id = 0; m.HasHide = false; m.Building = 0;
        m.RespawnAt = _now + Ticks(MusouMekRules.RespawnSeconds);
    }

    /// <summary>A new Mek for slot <paramref name="k"/>: at the spawn spot farthest from every player (ties: the list's order, from the slot's own turn), a little off it by slot, on the graph.</summary>
    void Come(int k, ReadOnlySpan<MusouTarget> targets, IReadOnlyList<Vector3> spawns)
    {
        if (spawns.Count == 0 || _sim.Alive + 1 > Campaign.TfwBattleRules.CrowdPoolRoom) { _meks[k].RespawnAt = _now + Ticks(1f); return; }
        int best = -1; float bestScore = float.NegativeInfinity;
        for (int j = 0; j < spawns.Count; j++)
        {
            int s = (j + k) % spawns.Count;
            float score = MathF.Min(PlayerDistance(spawns[s], targets), 200f);
            if (score > bestScore + 1f) { bestScore = score; best = s; }
        }
        float angle = k * MathF.Tau / MusouMekRules.MaxMeks;
        var spot = spawns[best] + new Vector3(MathF.Cos(angle) * 4f, MathF.Sin(angle) * 4f, 0f);
        int node = -1;
        if (_sim.Walk is { } walk)
        {
            node = walk.Nearest(spot, 3f, 6f);
            if (node < 0) node = walk.Nearest(spawns[best], 3f, 6f);
            if (node >= 0) spot = walk.Position(node);
        }
        ushort id = _sim.Spawn(MusouEnemyType.ScrapMek, spot, angle, _sim.Rules.Of(MusouEnemyType.ScrapMek).Health, node);
        if (id == 0) { _meks[k].RespawnAt = _now + Ticks(1f); return; }
        _meks[k].Id = id; _meks[k].HasHide = false; _meks[k].Building = 0;
        MeksCome++;
    }

    /// <summary>The nearest living player (id above 0) to <paramref name="at"/>, flat with height x3 (infinite with none).</summary>
    static float PlayerDistance(Vector3 at, ReadOnlySpan<MusouTarget> targets)
    {
        float best = float.PositiveInfinity;
        foreach (var t in targets)
        {
            if (!t.Alive || t.Goal || t.Id <= 0) continue;
            var d = t.Position - at;
            best = MathF.Min(best, MathF.Sqrt(d.X * d.X + d.Y * d.Y + 9f * d.Z * d.Z));
        }
        return best;
    }

    /// <summary>The direction (flat) from <paramref name="at"/> to the nearest living player (null with none).</summary>
    static Vector2? TowardPlayer(Vector3 at, ReadOnlySpan<MusouTarget> targets)
    {
        float best = float.PositiveInfinity; Vector2? way = null;
        foreach (var t in targets)
        {
            if (!t.Alive || t.Goal || t.Id <= 0) continue;
            var d = new Vector2(t.Position.X - at.X, t.Position.Y - at.Y);
            float l = d.LengthSquared();
            if (l < best && l > 1e-6f) { best = l; way = Vector2.Normalize(d); }
        }
        return way;
    }

    /// <summary>One Mek's step: a flight breaks off his build; else to his place (picked afresh when he has none, a player comes near it or he cannot get there), and there he builds.</summary>
    void Think(int k, ReadOnlySpan<MusouTarget> targets)
    {
        var m = _meks[k];
        if (!_sim.TryGet(m.Id, out var e)) return;
        if (_sim.MekFleeing(m.Id))
        {
            if (m.Building != 0 && Find(m.Building) is { } lost) Remove(_structures.IndexOf(lost), lost, MusouMekRules.DestroyedCue);
            m.Building = 0; m.HasHide = false;
            _sim.MekWork(m.Id, false);
            return;
        }
        if (e.State == MusouEnemyState.Spawning) return;
        var demo = DemoHide?.Invoke(k);
        if (demo is { } forced && (!m.HasHide || Vector3.DistanceSquared(m.Hide, forced) > 0.25f)) { m.Hide = forced; m.HasHide = true; m.Best = float.PositiveInfinity; m.BestStep = _now; }
        float far = Flat(e.Position, m.Hide);
        if (m.HasHide && demo is null && m.Building == 0)
        {
            if (PlayerDistance(m.Hide, targets) < MusouMekRules.HideCompromised) m.HasHide = false;
            else if (far < m.Best - 0.5f) { m.Best = far; m.BestStep = _now; }
            else if (far > MusouMekRules.HideArrive && _now - m.BestStep > Ticks(MusouMekRules.HideStuckSeconds)) m.HasHide = false;
        }
        if (!m.HasHide)
        {
            m.Hide = PickHide(k, e.Position, targets);
            m.HasHide = true; m.Best = float.PositiveInfinity; m.BestStep = _now;
            far = Flat(e.Position, m.Hide);
        }
        if (far > MusouMekRules.HideArrive)
        {
            _sim.MekOrder(m.Id, m.Hide);
            _sim.MekWork(m.Id, false);
            return;
        }
        _sim.MekOrder(m.Id, null);
        if (m.Building == 0)
        {
            if (!Builds || OwnedBy(m.Id) >= MusouMekRules.MaxPerMek || NextKind(m) is not { } kind) { _sim.MekWork(m.Id, false); return; }
            var s = Begin(m, kind, targets);
            if (s is null) { _sim.MekWork(m.Id, false); return; }
            m.Building = s.Id;
        }
        var build = Find(m.Building);
        if (build is null) { m.Building = 0; _sim.MekWork(m.Id, false); return; }
        _sim.MekWork(m.Id, true, build.Position);
        build.Progress = MathF.Min(1f, build.Progress + Dt / MusouMekRules.BuildSeconds);
        if (build.Progress < 1f) return;
        build.State = MusouContraptionState.Active;
        build.NextDrop = DemoHide is not null ? _now + Ticks(2f) : _now + Ticks(MusouMekRules.BeaconPeriod);
        build.Cooldown = _now;
        Built++;
        _sim.AddEvent(new MusouEvent(MusouEventKind.Telegraph, _sim.Tick, m.Id, build.Id, MusouMekRules.BuiltCue, build.Position));
        m.Building = 0;
        _sim.MekWork(m.Id, false);
    }

    static float Flat(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Y - b.Y).Length();

    MusouContraptionKind? NextKind(Mek m)
    {
        for (int j = 0; j < Order.Length; j++)
        {
            var kind = Order[(m.Rotation + j) % Order.Length];
            if (kind == MusouContraptionKind.ShieldGenerator && ShieldsNow() >= MusouMekRules.MaxShields) continue;
            m.Rotation = (m.Rotation + j + 1) % Order.Length;
            return kind;
        }
        return null;
    }

    /// <summary>
    /// The hiding place of slot <paramref name="k"/>'s Mek standing at <paramref name="at"/>: of the graph's nodes on <see cref="MusouMekRules.HideRings"/> round him (12 directions), reachable, at least
    /// <see cref="MusouMekRules.MekSpacing"/> from the other Meks' places, the one farthest from the players (counted up to 80 m), nearer walls better, nearer him better; places under
    /// <see cref="MusouMekRules.HideMinPlayerDistance"/> from a player only when there is nothing else. Without a graph the points themselves. None: where he stands.
    /// </summary>
    Vector3 PickHide(int k, Vector3 at, ReadOnlySpan<MusouTarget> targets)
    {
        var best = at; float bestScore = float.NegativeInfinity;
        float start = (k * 0.7f + _now * 0.013f) % MathF.Tau;
        foreach (float r in MusouMekRules.HideRings)
        {
            for (int a = 0; a < 12; a++)
            {
                float angle = start + a * MathF.Tau / 12f;
                var spot = at + new Vector3(MathF.Cos(angle) * r, MathF.Sin(angle) * r, 0f);
                float cover = 0f;
                if (_sim.Walk is { } walk)
                {
                    int node = walk.Nearest(spot, 3f, 6f);
                    if (node < 0 || !_sim.Reachable(node)) continue;
                    spot = walk.Position(node);
                    cover = MathF.Min(walk.EdgeDistance(node), 3f);
                }
                bool crowded = false;
                for (int j = 0; j < _meks.Length && !crowded; j++)
                    if (j != k && _meks[j].Id != 0 && _meks[j].HasHide && Flat(_meks[j].Hide, spot) < MusouMekRules.MekSpacing) crowded = true;
                if (crowded) continue;
                float players = MathF.Min(PlayerDistance(spot, targets), 80f);
                float score = players - 4f * cover - 0.1f * r - (players < MusouMekRules.HideMinPlayerDistance ? 1000f : 0f);
                if (score > bestScore) { bestScore = score; best = spot; }
            }
        }
        return best;
    }

    /// <summary>
    /// A new contraption of <paramref name="kind"/> for Mek <paramref name="m"/> at his place: the shield on it (its dome over him), the turret 3 m and the beacon 4 m to his sides, the trap 8 m towards the
    /// nearest player (where they will come from); turned in 45-degree steps until it stands 2.5 m clear of every other contraption, on the graph. Null when the 255 ids are all taken or nowhere will do.
    /// </summary>
    Structure? Begin(Mek m, MusouContraptionKind kind, ReadOnlySpan<MusouTarget> targets)
    {
        if (_structures.Count >= 255) return null;
        var toward = TowardPlayer(m.Hide, targets) ?? new Vector2(1f, 0f);
        (float distance, float turn) = kind switch
        {
            MusouContraptionKind.ShieldGenerator => (0f, 0f),
            MusouContraptionKind.Turret => (3f, MathF.PI / 3f),
            MusouContraptionKind.Beacon => (4f, -MathF.PI / 2f),
            _ => (8f, 0f),
        };
        Vector3? place = null;
        for (int t = 0; t < 8 && place is null; t++)
        {
            float a = MathF.Atan2(toward.Y, toward.X) + turn + (t + 1) / 2 * (t % 2 == 0 ? 1f : -1f) * MathF.PI / 4f;
            var spot = m.Hide + new Vector3(MathF.Cos(a) * distance, MathF.Sin(a) * distance, 0f);
            if (_sim.Walk is { } walk)
            {
                int node = walk.Nearest(spot, 2f, 3f);
                if (node < 0) { if (distance == 0f) spot = m.Hide; else continue; }
                else spot = walk.Position(node);
            }
            bool clear = true;
            foreach (var other in _structures) if (Flat(other.Position, spot) < 2.5f) { clear = false; break; }
            if (clear) place = spot;
            if (distance == 0f && place is null) break;
        }
        if (place is not { } at) return null;
        byte id = NextId();
        if (id == 0) return null;
        var s = new Structure { Id = id, Kind = kind, State = MusouContraptionState.Building, Mek = m.Id, Position = at, Health = MusouMekRules.Health(kind) };
        s.Aim = MathF.Atan2(-toward.X, toward.Y);
        _structures.Add(s);
        _sim.AddEvent(new MusouEvent(MusouEventKind.Telegraph, _sim.Tick, m.Id, id, MusouMekRules.BuildCue, at));
        return s;
    }

    byte NextId()
    {
        for (int n = 0; n < 255; n++)
        {
            _lastId = (byte)(_lastId % 255 + 1);
            if (Find(_lastId) is null) return _lastId;
        }
        return 0;
    }

    void Remove(int index, Structure s, byte cue)
    {
        if (index < 0) return;
        _structures.RemoveAt(index);
        s.InFlight = false; s.Queued = 0; s.Carried = 0;   // a beacon's drop in flight and its queue go with it
        if (cue == MusouMekRules.DestroyedCue) Destroyed++;
        _sim.AddEvent(new MusouEvent(MusouEventKind.Telegraph, _sim.Tick, s.Mek, s.Id, cue, s.Position));
        foreach (var m in _meks) if (m.Building == s.Id) m.Building = 0;
    }

    // ------------------------------------------------------------------ the contraptions

    void StepBeacon(Structure s)
    {
        s.Boyz.RemoveAll(id => !(_sim.TryGet(id, out var e) && e.Alive));
        if (_now >= s.NextDrop)
        {
            s.Queued = Math.Min(MusouMekRules.BeaconDrop, s.Queued + MusouMekRules.BeaconDrop);
            s.NextDrop += Ticks(MusouMekRules.BeaconPeriod);
        }
        if (s.InFlight)
        {
            if (_now < s.LandAt) return;
            Land(s);
            return;
        }
        int room = MusouMekRules.BeaconMaxAlive - s.Boyz.Count;
        if (s.Queued <= 0 || room <= 0) return;
        s.Carried = Math.Min(s.Queued, room);
        s.Queued -= s.Carried;
        s.InFlight = true;
        s.LandAt = _now + Ticks(MusouMekRules.DropFlightSeconds);
        _sim.AddEvent(new MusouEvent(MusouEventKind.Telegraph, _sim.Tick, s.Mek, s.Id, MusouMekRules.DropCue, s.Position));
    }

    /// <summary>A beacon's drop lands: its Boyz round the beacon (on the graph; as many as the pool's reserve allows), ordinary Orks from then on.</summary>
    void Land(Structure s)
    {
        s.InFlight = false;
        int landed = 0;
        for (int n = 0; n < s.Carried; n++)
        {
            if (_sim.Alive + 1 > Campaign.TfwBattleRules.CrowdPoolRoom) break;
            float angle = n * 2.399963f + s.Id;
            float r = MusouMekRules.DropRingMin + (MusouMekRules.DropRingMax - MusouMekRules.DropRingMin) * ((n * 0.618034f) % 1f);
            var spot = s.Position + new Vector3(MathF.Cos(angle) * r, MathF.Sin(angle) * r, 0f);
            int node = -1;
            if (_sim.Walk is { } walk)
            {
                node = walk.Nearest(spot, 2f, 3f);
                if (node < 0) node = walk.Nearest(s.Position, 3f, 3f);
                if (node >= 0) spot = walk.Position(node);
            }
            ushort id = _sim.Spawn(MusouEnemyType.Boy, spot, angle, _sim.Rules.Of(MusouEnemyType.Boy).Health, node);
            if (id == 0) break;
            s.Boyz.Add(id);
            landed++;
        }
        s.Carried = 0;
        DropsLanded++;
        BoyzDropped += landed;
        _sim.AddEvent(new MusouEvent(MusouEventKind.Telegraph, _sim.Tick, s.Mek, s.Id, MusouMekRules.DropLandCue, s.Position));
    }

    void StepTurret(Structure s, ReadOnlySpan<MusouTarget> targets, Func<Vector3, Vector3, bool>? blocked)
    {
        if (_now < s.Cooldown) return;
        var eye = s.Position + new Vector3(0f, 0f, MusouMekRules.TurretMuzzleHeight);
        int pick = -1; float best = float.PositiveInfinity; Vector3 aim = default;
        for (int t = 0; t < targets.Length; t++)
        {
            var target = targets[t];
            if (!target.Alive || target.Goal) continue;
            var chest = target.Position + new Vector3(0f, 0f, MusouRokkitRules.ChestHeight);
            float d = MathF.Max(0f, Vector3.Distance(chest, eye) - target.Radius);
            if (d > MusouMekRules.TurretRange || d >= best) continue;
            if (blocked is not null && blocked(eye, chest)) continue;
            best = d; pick = t; aim = chest;
        }
        if (pick < 0) { s.Cooldown = _now + Ticks(MusouMekRules.TurretRetrySeconds); return; }
        var flat = new Vector2(aim.X - s.Position.X, aim.Y - s.Position.Y);
        if (flat.LengthSquared() > 1e-6f) s.Aim = MathF.Atan2(-flat.X, flat.Y);
        var forward = flat.LengthSquared() > 1e-6f ? Vector2.Normalize(flat) : new Vector2(-MathF.Sin(s.Aim), MathF.Cos(s.Aim));
        var muzzle = eye + new Vector3(forward * MusouMekRules.TurretMuzzleForward, 0f);
        if (_sim.LaunchTurretRocket(s.Mek, s.Id, muzzle, aim) != 0) TurretRockets++;
        s.Cooldown = _now + Ticks(MusouMekRules.TurretPeriod);
    }

    static bool Sprung(Structure s, ReadOnlySpan<MusouTarget> targets, Func<Vector3, float, bool>? guardsNear)
    {
        float r2 = MusouMekRules.TrapRadius * MusouMekRules.TrapRadius;
        foreach (var t in targets)
        {
            if (!t.Alive || t.Goal || t.Id <= 0) continue;
            var d = t.Position - s.Position;
            if (d.X * d.X + d.Y * d.Y <= r2 && MathF.Abs(d.Z) <= MusouMekRules.TrapVertical) return true;
        }
        return guardsNear is not null && guardsNear(s.Position, MusouMekRules.TrapRadius);
    }

    // ------------------------------------------------------------------ damage and the dome

    /// <summary>
    /// Damage to a contraption (built or under way) by <paramref name="attacker"/> (a player's id; 0 for no one). At 0 it is destroyed (<see cref="MusouMekRules.DestroyedCue"/>); a built trap destroyed by
    /// a player goes off as an Ork blast (<see cref="TakeBlasts"/>, credited to him). False for an unknown contraption or no damage.
    /// </summary>
    public bool Damage(byte id, int attacker, float amount, out bool destroyed)
    {
        destroyed = false;
        var s = Find(id);
        if (s is null || !(amount > 0f) || DemoHide is not null) return false;   // the capture aid: nothing is destroyed
        s.Health = MathF.Max(0f, s.Health - amount);
        if (s.Health > 0f) return true;
        destroyed = true;
        bool blast = s.Kind == MusouContraptionKind.Trap && s.State == MusouContraptionState.Active && attacker > 0;
        Remove(_structures.IndexOf(s), s, MusouMekRules.DestroyedCue);
        if (blast)
        {
            _blasts.Add(new MusouTrapBlast(s.Id, s.Position, attacker));
            _sim.AddEvent(new MusouEvent(MusouEventKind.Telegraph, _sim.Tick, s.Mek, s.Id, MusouMekRules.TrapBlastCue, s.Position));
        }
        return true;
    }

    /// <summary>The nearest contraption a ray from <paramref name="origin"/> along <paramref name="direction"/> (unit) meets within <paramref name="max"/> (its upright cylinder), and where (0: none).</summary>
    public byte Raycast(Vector3 origin, Vector3 direction, float max, out float distance)
    {
        distance = float.PositiveInfinity;
        byte hit = 0;
        foreach (var s in _structures)
        {
            var (radius, height) = MusouMekRules.Body(s.Kind);
            if (RayCylinder(origin, direction, s.Position, radius, height) is { } t && t <= max && t < distance) { distance = t; hit = s.Id; }
        }
        return hit;
    }

    /// <summary>The contraptions within <paramref name="radius"/> of <paramref name="point"/> (their centre, flat with height), with their distance, written into <paramref name="into"/> (cleared first).</summary>
    public void Near(Vector3 point, float radius, List<(byte Id, float Distance)> into)
    {
        into.Clear();
        foreach (var s in _structures)
        {
            var centre = Centre(s);
            float d = Vector3.Distance(centre, point);
            if (d <= radius) into.Add((s.Id, d));
        }
    }

    /// <summary>A contraption's middle (half its height up).</summary>
    public static Vector3 Centre(in MusouContraption c) => c.Position + new Vector3(0f, 0f, MusouMekRules.Body(c.Kind).Height * 0.5f);
    static Vector3 Centre(Structure s) => s.Position + new Vector3(0f, 0f, MusouMekRules.Body(s.Kind).Height * 0.5f);

    /// <summary>
    /// How far along a ray from <paramref name="origin"/> (unit <paramref name="direction"/>, at most <paramref name="max"/>) it enters a built shield generator's dome from outside (infinite: never). A shot
    /// fired inside a dome is never stopped by it.
    /// </summary>
    public float DomeEntry(Vector3 origin, Vector3 direction, float max)
    {
        float best = float.PositiveInfinity;
        foreach (var s in _structures)
        {
            if (s.Kind != MusouContraptionKind.ShieldGenerator || s.State != MusouContraptionState.Active) continue;
            if (Vector3.DistanceSquared(origin, s.Position) <= MusouMekRules.ShieldRadius * MusouMekRules.ShieldRadius) continue;
            if (RaySphere(origin, direction, s.Position, MusouMekRules.ShieldRadius) is { } t && t <= max) best = MathF.Min(best, t);
        }
        return best;
    }

    /// <summary>Whether a built dome stands between <paramref name="from"/> (outside it) and <paramref name="to"/> (inside it): a blast or a cone from outside does not reach in.</summary>
    public bool Shielded(Vector3 from, Vector3 to)
    {
        float r2 = MusouMekRules.ShieldRadius * MusouMekRules.ShieldRadius;
        foreach (var s in _structures)
        {
            if (s.Kind != MusouContraptionKind.ShieldGenerator || s.State != MusouContraptionState.Active) continue;
            if (Vector3.DistanceSquared(to, s.Position) <= r2 && Vector3.DistanceSquared(from, s.Position) > r2) return true;
        }
        return false;
    }

    /// <summary>The nearest built Rokkit turret whose middle lies within <paramref name="range"/> of <paramref name="from"/> (flat), and its middle (0: none). For the Guardsmen.</summary>
    public byte NearestTurret(Vector3 from, float range, out Vector3 centre)
    {
        centre = default;
        byte best = 0;
        float bestD = range * range;
        foreach (var s in _structures)
        {
            if (s.Kind != MusouContraptionKind.Turret || s.State != MusouContraptionState.Active) continue;
            float dx = s.Position.X - from.X, dy = s.Position.Y - from.Y, d = dx * dx + dy * dy;
            if (d > bestD) continue;
            bestD = d; best = s.Id; centre = Centre(s);
        }
        return best;
    }

    /// <summary>Whether any built dome stands now (a quick skip for the shot hooks).</summary>
    public bool AnyDome
    {
        get { foreach (var s in _structures) if (s.Kind == MusouContraptionKind.ShieldGenerator && s.State == MusouContraptionState.Active) return true; return false; }
    }

    static float? RaySphere(Vector3 o, Vector3 d, Vector3 centre, float r)
    {
        var m = o - centre;
        float b = Vector3.Dot(m, d), c = Vector3.Dot(m, m) - r * r;
        if (c > 0f && b > 0f) return null;
        float disc = b * b - c;
        if (disc < 0f) return null;
        return MathF.Max(0f, -b - MathF.Sqrt(disc));
    }

    /// <summary>A ray against an upright cylinder from <paramref name="foot"/> up <paramref name="height"/> (its side and its top); the distance, or null.</summary>
    static float? RayCylinder(Vector3 o, Vector3 d, Vector3 foot, float radius, float height)
    {
        float best = float.PositiveInfinity;
        var m = new Vector2(o.X - foot.X, o.Y - foot.Y);
        var dd = new Vector2(d.X, d.Y);
        float a = Vector2.Dot(dd, dd), b = Vector2.Dot(m, dd), c = Vector2.Dot(m, m) - radius * radius;
        if (c <= 0f && o.Z >= foot.Z && o.Z <= foot.Z + height) return 0f;   // starts inside
        if (a > 1e-8f)
        {
            float disc = b * b - a * c;
            if (disc >= 0f)
            {
                float t = (-b - MathF.Sqrt(disc)) / a;
                if (t >= 0f) { float z = o.Z + d.Z * t; if (z >= foot.Z && z <= foot.Z + height) best = t; }
            }
        }
        if (MathF.Abs(d.Z) > 1e-6f)
        {
            float t = (foot.Z + height - o.Z) / d.Z;
            if (t >= 0f && t < best)
            {
                var p = new Vector2(o.X + d.X * t - foot.X, o.Y + d.Y * t - foot.Y);
                if (p.LengthSquared() <= radius * radius) best = t;
            }
        }
        return float.IsPositiveInfinity(best) ? null : best;
    }
}
