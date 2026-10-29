using System.Numerics;

namespace SmSim;

/// <summary>
/// The team modes the dedicated server runs (front end game types, <c>multiplayer_map</c> <c>objective_game_type</c>):
/// <see cref="Annihilation"/> ("teamdeathmatch") and <see cref="SeizeGround"/> ("domination", control points).
/// </summary>
public enum GameMode : byte { Annihilation = 0, SeizeGround = 1 }

/// <summary>
/// A Seize Ground control point: a vertical cylinder (the map's <c>mkr_objective_N</c> / <c>mkr_cp_N</c> marker; radius = half
/// the marker's X scale, height = its Z scale centred on its position — both an **interpretation** of the marker scale, see
/// spec/behavior.md M16). A living player stands on it when its feet are inside the cylinder.
/// </summary>
public sealed record ControlPoint(string Name, Vector3 Center, float Radius, float HalfHeight)
{
    public bool Contains(Vector3 feet)
    {
        float dx = feet.X - Center.X, dy = feet.Y - Center.Y;
        return dx * dx + dy * dy <= Radius * Radius && MathF.Abs(feet.Z - Center.Z) <= HalfHeight;
    }
}

/// <summary>Shape of a team start area marker (<c>Shape</c> 1: box, 3: cylinder).</summary>
public enum AreaShape : byte { Box = 0, Cylinder = 1 }

/// <summary>
/// A side's start area (<c>mkr_start_marine</c>, <c>mkr_start_ork</c> for Chaos): centre, full size (marker scale, metres),
/// rotation about Z (radians, also the facing given to players spawned in it) and shape.
/// </summary>
public sealed record StartArea(Team Side, Vector3 Center, Vector3 Size, float Yaw, AreaShape Shape = AreaShape.Box);

/// <summary>
/// A control point's state: <paramref name="Owner"/> (None: neutral); <paramref name="Side"/>, whose progress
/// <paramref name="Progress"/> counts (ticks; an owned point: the owner and its hold, full at the neutralise time; a neutral
/// point: the side capturing it, full at the capture time; None and 0: untouched); <paramref name="Contested"/>: both sides
/// stood on it this tick.
/// </summary>
public readonly record struct PointState(Team Owner, Team Side, int Progress, bool Contested);

public enum CaptureKind : byte { Captured = 0, Neutralised = 1 }

/// <summary>A point changed hands: <paramref name="Side"/> captured it, or neutralised the other side's point.</summary>
public readonly record struct CaptureEvent(uint Tick, int Point, CaptureKind Kind, Team Side);

/// <summary>Seize Ground (server-side; clients see the points in snapshots and never predict them).</summary>
public sealed partial class World
{
    /// <summary>Most control points a world holds (the maps have 3 or 4; snapshots carry every point, 2 bytes each).</summary>
    public const int MaxControlPoints = 8;

    ControlPoint[] _pointSpecs = [];
    PointState[] _points = [];
    StartArea[] _startAreas = [];
    readonly Dictionary<int, List<Vector3>> _areaSpots = new();
    readonly int[] _areaCursor = new int[3];
    readonly List<CaptureEvent> _captures = new();
    int _scoreClock;

    /// <summary>The map's control points (at most <see cref="MaxControlPoints"/>; finite, radius and half height positive).</summary>
    public IReadOnlyList<ControlPoint> ControlPoints
    {
        get => _pointSpecs;
        init
        {
            if (value.Count > MaxControlPoints) throw new ArgumentException($"At most {MaxControlPoints} control points ({value.Count} given).");
            foreach (var p in value)
                if (!Finite(p.Center) || !(p.Radius > 0f) || !(p.HalfHeight > 0f) || !float.IsFinite(p.Radius) || !float.IsFinite(p.HalfHeight))
                    throw new ArgumentException($"Control point '{p.Name}' has a non-finite centre or a non-positive radius or height.");
            _pointSpecs = value.ToArray();
            _points = new PointState[_pointSpecs.Length];
        }
    }

    /// <summary>The sides' start areas: with <see cref="Rules"/> (both modes, M22) every player spawns in its side's area.</summary>
    public IReadOnlyList<StartArea> StartAreas
    {
        get => _startAreas;
        init
        {
            foreach (var a in value)
                if (!Finite(a.Center) || !Finite(a.Size) || a.Size.X <= 0f || a.Size.Y <= 0f || !float.IsFinite(a.Yaw))
                    throw new ArgumentException("A start area has a non-finite or empty shape.");
            _startAreas = value.ToArray();
        }
    }

    static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    /// <summary>Each control point's state, in <see cref="ControlPoints"/> order.</summary>
    public IReadOnlyList<PointState> Points => _points;
    /// <summary>Captures and neutralisations during the last <see cref="Step"/>.</summary>
    public IReadOnlyList<CaptureEvent> LastCaptures => _captures;

    bool Seizing => Rules is { Mode: GameMode.SeizeGround };

    /// <summary>Points owned by a side.</summary>
    public int OwnedBy(Team side) { int n = 0; foreach (var p in _points) if (p.Owner == side && side != Team.None) n++; return n; }

    /// <summary>A point's progress as a fraction of its phase (owned: hold over the neutralise time; neutral: capture over the capture time).</summary>
    public float ProgressOf(int point)
    {
        var p = _points[point];
        int full = (int)(p.Owner != Team.None ? Ticks(Rules?.NeutraliseTime ?? 1f) : Ticks(Rules?.CaptureTime ?? 1f));
        return Math.Clamp((float)p.Progress / full, 0f, 1f);
    }

    /// <summary>
    /// One tick of Seize Ground while playing (rules: spec/behavior.md M16). Per point, the living players whose feet are in
    /// it: both sides → contested, frozen; nobody → unchanged; one side S → an owned point of S regains hold (to full), a
    /// point of the other side loses hold (empty: neutral), a neutral point gains S's capture progress (full: S owns it at
    /// full hold) or first loses the other side's partial progress (one tick per tick). Then, every
    /// <see cref="MatchRules.ScoreInterval"/>, each side scores <see cref="MatchRules.PointScore"/> per point it owns, and the
    /// score limit is checked as in Annihilation (both over: the higher wins, equal a draw).
    /// </summary>
    void StepCapture()
    {
        if (!Seizing || State != MatchState.Playing || _points.Length == 0) return;
        int full = (int)Ticks(Rules!.CaptureTime), hold = (int)Ticks(Rules.NeutraliseTime);
        for (int i = 0; i < _points.Length; i++)
        {
            int marines = 0, chaos = 0;
            foreach (var p in _players.Values)
            {
                if (!p.Alive || !_pointSpecs[i].Contains(p.Position)) continue;
                if (p.Team == Team.SpaceMarines) marines++;
                else if (p.Team == Team.Chaos) chaos++;
            }
            var s = _points[i];
            if (marines > 0 && chaos > 0) { _points[i] = s with { Contested = true }; continue; }
            s = s with { Contested = false };
            var side = marines > 0 ? Team.SpaceMarines : chaos > 0 ? Team.Chaos : Team.None;
            if (side != Team.None)
            {
                if (s.Owner == side) s = s with { Progress = Math.Min(hold, s.Progress + 1) };
                else if (s.Owner != Team.None)
                {
                    s = s with { Progress = s.Progress - 1 };
                    if (s.Progress <= 0)
                    {
                        _captures.Add(new CaptureEvent(Tick, i, CaptureKind.Neutralised, side));
                        s = default;
                    }
                }
                else if (s.Side == side || s.Side == Team.None)
                {
                    s = s with { Side = side, Progress = s.Progress + 1 };
                    if (s.Progress >= full)
                    {
                        _captures.Add(new CaptureEvent(Tick, i, CaptureKind.Captured, side));
                        s = new PointState(side, side, hold, false);
                    }
                }
                else
                {
                    s = s with { Progress = s.Progress - 1 };
                    if (s.Progress <= 0) s = default;
                }
            }
            _points[i] = s;
        }
        if (++_scoreClock < Ticks(Rules.ScoreInterval)) return;
        _scoreClock = 0;
        _teamScores[(int)Team.SpaceMarines] += Rules.PointScore * OwnedBy(Team.SpaceMarines);
        _teamScores[(int)Team.Chaos] += Rules.PointScore * OwnedBy(Team.Chaos);
        CheckScoreLimit();
    }

    /// <summary>Points back to neutral and the scoring clock to zero (a new round).</summary>
    void ResetPoints()
    {
        Array.Clear(_points);
        _scoreClock = 0;
    }

    /// <summary>
    /// The middle of the map (M22; spawns faced it until the user chose "out of the room toward its exit", see
    /// <see cref="StartAreaFacing"/>; kept for comparison): the centroid of the control points; without points,
    /// the centroid of the start areas' centres (at least two); else null. On Manufactorum the points' centroid is
    /// (200, −800, 2.67): point B's centre in plan, and the two start areas' midpoint is (199.7, −800.9); the map is
    /// point-symmetric about it (the tdm spawn markers 01/02, 03/04 and 05/06 pair about it too).
    /// </summary>
    public Vector3? MapMiddle
    {
        get
        {
            if (_pointSpecs.Length > 0) return _pointSpecs.Aggregate(Vector3.Zero, (sum, p) => sum + p.Center) / _pointSpecs.Length;
            if (_startAreas.Length >= 2) return _startAreas.Aggregate(Vector3.Zero, (sum, a) => sum + a.Center) / _startAreas.Length;
            return null;
        }
    }

    /// <summary>
    /// A team match (<see cref="Rules"/>, either mode; M22) with a start area for <paramref name="team"/>: a spot in it (free
    /// of living players within 2 m when one is, in rotation), facing out of the spawn room toward its exit (<see cref="StartAreaFacing"/>).
    /// Every spawn of a side's player (first spawn, respawn, round start) is in its start area: the marker the scripts make the
    /// side's spawn marker (<c>multiplayer.lua</c> OnStart: <c>SetSpawnLocation</c> → <c>Mission_SetSpawnMarker</c> with
    /// <c>mkr_start_marine</c> / <c>mkr_start_ork</c>), inside the room the per-race movement blockers seal. Annihilation's
    /// re-spawning over the shared <c>tdm_mkr_spawn_*</c> markers (<c>RandomizeTeamSpawning</c>) is not used: the user asked for
    /// spawn-room spawns (spec/behavior.md M22). Spots are a 2 m grid inside the area, 1 m in from its edge, nearest the
    /// centre first; with a level each is dropped onto the character collision below it (spots without walkable ground within
    /// the area's height (at least ±1 m) and 1 m under it, or whose lowest such surface lacks a capsule's headroom, are left out; none left: the centre). Ours: the original places players in the
    /// area by code that is not decoded.
    /// </summary>
    bool TryStartAreaSpawn(int playerId, Team team, out Vector3 position, out float yaw)
    {
        position = default; yaw = 0f;
        if (Rules is null || team is not (Team.SpaceMarines or Team.Chaos)) return false;
        int index = Array.FindIndex(_startAreas, a => a.Side == team);
        if (index < 0) return false;
        var spots = SpotsIn(index);
        int cursor = _areaCursor[(int)team] % spots.Count, chosen = cursor;
        for (int k = 0; k < spots.Count; k++)
        {
            int i = (cursor + k) % spots.Count;
            if (!Occupied(spots[i], playerId)) { chosen = i; break; }
        }
        _areaCursor[(int)team] = (chosen + 1) % spots.Count;
        position = spots[chosen];
        yaw = StartAreaFacing(index, position);
        return true;
    }

    /// <summary>
    /// A place to stand at (x, y) between <paramref name="top"/> and <paramref name="bottom"/>: the lowest walkable surface
    /// (normal Z ≥ 0.7) with a capsule's height of headroom (nothing within <see cref="SimConfig.CapsuleHeight"/> straight
    /// up), so a roof, gantry or ceiling inside the area's height is passed over for the floor under it, and a hit inside
    /// a slab (its underside, which a ray from above also reports) is passed over for the slab's top. Null when none.
    /// </summary>
    Vector3? LowestStandable(float x, float y, float top, float bottom)
    {
        Vector3? best = null;
        float z = top;
        for (int guard = 0; guard < 64 && z > bottom; guard++)
        {
            if (Collision!.Character.Raycast(new Vector3(x, y, z), -Vector3.UnitZ, z - bottom) is not { } hit) break;
            if (hit.Normal.Z >= 0.7f && Collision.Character.Raycast(hit.Point + new Vector3(0, 0, 0.05f), Vector3.UnitZ, Config.CapsuleHeight) is null)
                best = hit.Point;
            z = hit.Point.Z - 0.01f;
        }
        return best;
    }

    /// <summary>The spawn spots of start area <paramref name="areaIndex"/> (index into <see cref="StartAreas"/>), as Seize Ground uses them.</summary>
    public IReadOnlyList<Vector3> StartAreaSpots(int areaIndex) => SpotsIn(areaIndex);

    List<Vector3> SpotsIn(int areaIndex)
    {
        if (_areaSpots.TryGetValue(areaIndex, out var cached)) return cached;
        var area = _startAreas[areaIndex];
        const float step = 2f, margin = 1f;
        var half = area.Size / 2f;
        float halfHeight = MathF.Max(half.Z, 1f);
        var grid = new List<(float Distance, int K, Vector2 Local)>();
        int nu = (int)MathF.Floor(MathF.Max(0f, half.X - margin) / step), nv = (int)MathF.Floor(MathF.Max(0f, half.Y - margin) / step);
        int k = 0;
        for (int u = -nu; u <= nu; u++)
            for (int v = -nv; v <= nv; v++, k++)
            {
                var local = new Vector2(u * step, v * step);
                if (area.Shape == AreaShape.Cylinder && local.Length() > MathF.Max(0f, MathF.Min(half.X, half.Y) - margin)) continue;
                grid.Add((local.LengthSquared(), k, local));
            }
        grid.Sort((a, b) => a.Distance != b.Distance ? a.Distance.CompareTo(b.Distance) : a.K.CompareTo(b.K));
        float c = MathF.Cos(area.Yaw), s = MathF.Sin(area.Yaw);
        var spots = new List<Vector3>();
        foreach (var (_, _, local) in grid)
        {
            var at = new Vector3(area.Center.X + local.X * c - local.Y * s, area.Center.Y + local.X * s + local.Y * c, area.Center.Z);
            if (Collision is null) { spots.Add(at); continue; }
            if (LowestStandable(at.X, at.Y, area.Center.Z + halfHeight, area.Center.Z - halfHeight - 1f) is { } ground) spots.Add(ground);
        }
        if (spots.Count == 0) spots.Add(area.Center);
        _areaSpots[areaIndex] = spots;
        return spots;
    }
}
