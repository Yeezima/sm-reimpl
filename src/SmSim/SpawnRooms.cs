using System.Numerics;

namespace SmSim;

/// <summary>
/// A side's spawn-room blocker (M22): a <c>base/MissionMovementBlocker</c> box of the map's mission layers
/// (<c>marine_blocker_*</c> for the Space Marines' room, <c>chaos_blocker_*</c> for Chaos'). Centre, full size (marker scale,
/// metres) and rotation about Z (radians). They mark the room's exits (<see cref="World.StartAreaFacings"/>) and, as solid
/// boxes for the other side's players only, keep enemies out of the room in both modes (<see cref="CollisionWorld.CharacterFor"/>;
/// spec/behavior.md "Spawn-room blockers"). Shots and thrown items pass through. A level raises every blocker to above its
/// highest reachable point (<see cref="ReachingAbove"/>; M31, a **change from the original**, owner's decision 2026-10-28).
/// </summary>
public sealed record SpawnBlocker(Team Side, Vector3 Center, Vector3 Size, float Yaw)
{
    /// <summary>
    /// Metres above the level's highest point (the top of its character collision) that no player reaches (M31, ours): the highest
    /// jump-pack jump on the install rises 18 m above its lift-off (<c>jump_height_map</c>, spec/behavior.md "Jump pack") and the
    /// capsule is 1.9 m tall, so a jump from the very top of the level stays below 18 + 1.9 = 19.9 m; 25 m leaves room above that.
    /// </summary>
    public const float AboveLevel = 25f;

    /// <summary>
    /// This blocker reaching up to at least <paramref name="top"/> (M31; **change from the original**, owner's decision 2026-10-28:
    /// in the original jump packs could fly over the spawn-room blockers, here no jump, flight or ground pound arc passes over
    /// them): the bottom stays, the top rises to <paramref name="top"/> when lower (centre and height move with it; footprint and
    /// yaw unchanged), so <see cref="Contains"/>, <see cref="PushOut"/>, the side meshes and the drawn shield all use the tall box.
    /// </summary>
    public SpawnBlocker ReachingAbove(float top)
    {
        float bottom = Center.Z - Size.Z / 2f, own = Center.Z + Size.Z / 2f;
        if (!float.IsFinite(top) || top <= own) return this;
        return this with { Center = Center with { Z = (bottom + top) / 2f }, Size = Size with { Z = top - bottom } };
    }

    /// <summary>Whether <paramref name="p"/> lies inside the box.</summary>
    public bool Contains(Vector3 p)
    {
        var d = p - Center;
        float c = MathF.Cos(-Yaw), s = MathF.Sin(-Yaw);
        float lx = d.X * c - d.Y * s, ly = d.X * s + d.Y * c;
        return MathF.Abs(lx) <= Size.X / 2f && MathF.Abs(ly) <= Size.Y / 2f && MathF.Abs(d.Z) <= Size.Z / 2f;
    }

    /// <summary>Whether the blocker stops players of <paramref name="side"/>: those of the other side (not its own, not <see cref="Team.None"/>).</summary>
    public bool Blocks(Team side) => Side is Team.SpaceMarines or Team.Chaos && side is Team.SpaceMarines or Team.Chaos && side != Side;

    /// <summary>Horizontal distance from <paramref name="p"/> to the box's footprint (0 inside it).</summary>
    public float HorizontalDistance(Vector3 p)
    {
        var d = p - Center;
        float c = MathF.Cos(-Yaw), s = MathF.Sin(-Yaw);
        float lx = MathF.Max(MathF.Abs(d.X * c - d.Y * s) - Size.X / 2f, 0f), ly = MathF.Max(MathF.Abs(d.X * s + d.Y * c) - Size.Y / 2f, 0f);
        return MathF.Sqrt(lx * lx + ly * ly);
    }

    /// <summary>
    /// A capsule (feet <paramref name="feet"/>, <paramref name="radius"/>, <paramref name="height"/>) whose axis is inside the box
    /// (in plan, with the heights overlapping) is moved out horizontally across the nearest side face, to a radius and 1 cm
    /// beyond it (ties: the box's X faces, then the positive side); otherwise <paramref name="feet"/> is returned unchanged.
    /// Ours: a player of the other side should never be inside, but if one is (a box over a spawn point, say) it is put
    /// outside rather than trapped, as the capsule controller would keep it inside a box thicker than its diameter.
    /// </summary>
    public Vector3 PushOut(Vector3 feet, float radius, float height)
    {
        if (!(feet.Z < Center.Z + Size.Z / 2f && feet.Z + height > Center.Z - Size.Z / 2f)) return feet;
        var d = feet - Center;
        float c = MathF.Cos(Yaw), s = MathF.Sin(Yaw);
        float lx = d.X * c + d.Y * s, ly = -d.X * s + d.Y * c;
        float hx = Size.X / 2f, hy = Size.Y / 2f;
        if (!(MathF.Abs(lx) < hx && MathF.Abs(ly) < hy)) return feet;
        if (hx - MathF.Abs(lx) <= hy - MathF.Abs(ly)) lx = (lx >= 0f ? 1f : -1f) * (hx + radius + 0.01f);
        else ly = (ly >= 0f ? 1f : -1f) * (hy + radius + 0.01f);
        return new Vector3(Center.X + lx * c - ly * s, Center.Y + lx * s + ly * c, feet.Z);
    }

    /// <summary>
    /// The box's eight corners: bottom (−Z/2) then top, each going (−X, −Y), (+X, −Y), (+X, +Y), (−X, +Y) in the box's own
    /// frame, turned by <see cref="Yaw"/> about Z (the same turn <see cref="Contains"/> undoes).
    /// </summary>
    public Vector3[] Corners()
    {
        float c = MathF.Cos(Yaw), s = MathF.Sin(Yaw);
        var h = Size / 2f;
        var corners = new Vector3[8];
        (float X, float Y)[] plan = [(-h.X, -h.Y), (h.X, -h.Y), (h.X, h.Y), (-h.X, h.Y)];
        for (int k = 0; k < 8; k++)
        {
            var (x, y) = plan[k % 4];
            corners[k] = Center + new Vector3(x * c - y * s, x * s + y * c, k < 4 ? -h.Z : h.Z);
        }
        return corners;
    }

    /// <summary>The box's 12 triangles over <see cref="Corners"/> (index buffer; triangles are two-sided in <see cref="StaticMesh"/>).</summary>
    public static readonly IReadOnlyList<int> BoxIndices =
    [
        0, 2, 1, 0, 3, 2,   // bottom
        4, 5, 6, 4, 6, 7,   // top
        0, 1, 5, 0, 5, 4,   // −Y side
        1, 2, 6, 1, 6, 5,   // +X side
        2, 3, 7, 2, 7, 6,   // +Y side
        3, 0, 4, 3, 4, 7,   // −X side
    ];

    /// <summary>The list, copied, after checking every blocker is finite with a positive size.</summary>
    internal static SpawnBlocker[] Checked(IEnumerable<SpawnBlocker>? blockers)
    {
        var list = blockers?.ToArray() ?? [];
        foreach (var b in list)
            if (b is null || !Geometry.Finite(b.Center) || !Geometry.Finite(b.Size) || !(b.Size.X > 0f && b.Size.Y > 0f && b.Size.Z > 0f) || !float.IsFinite(b.Yaw))
                throw new ArgumentException("A spawn blocker has a non-finite or empty shape.", nameof(blockers));
        return list;
    }

    /// <summary>
    /// <paramref name="level"/> plus the boxes of the blockers that stop <paramref name="side"/> (in list order); the level
    /// itself when none does.
    /// </summary>
    internal static StaticMesh MeshFor(StaticMesh level, Team side, IReadOnlyList<SpawnBlocker>? blockers)
    {
        var vertices = new List<Vector3>();
        var indices = new List<int>();
        foreach (var b in blockers ?? [])
        {
            if (!b.Blocks(side)) continue;
            int first = vertices.Count;
            vertices.AddRange(b.Corners());
            foreach (int i in BoxIndices) indices.Add(first + i);
        }
        return vertices.Count == 0 ? level : level.With([.. vertices], [.. indices]);
    }

    /// <summary>FNV-1a 64 of <paramref name="h"/> and each blocker's side and floats (centre, size, yaw); none: <paramref name="h"/>.</summary>
    internal static ulong Hash(ulong h, IReadOnlyList<SpawnBlocker>? blockers)
    {
        foreach (var b in blockers ?? [])
        {
            h = Geometry.Fnv(h, (uint)b.Side);
            foreach (float f in new[] { b.Center.X, b.Center.Y, b.Center.Z, b.Size.X, b.Size.Y, b.Size.Z, b.Yaw })
                h = Geometry.Fnv(h, BitConverter.SingleToUInt32Bits(f));
        }
        return h;
    }
}

/// <summary>How a start area's spawn facing was found (M22).</summary>
public enum SpawnFacingSource : byte
{
    /// <summary>Out of the room toward its exit (the side's blockers), along the walkable path.</summary>
    Exit = 0,
    /// <summary>The start-area marker's own rotation (no level, no blockers for the side, or no path to them).</summary>
    MarkerRotation = 1,
}

/// <summary>
/// The facing of each spawn spot of a start area (M22, user's decision: "face out of the spawn room toward its exit"), with
/// the exit point reached from the area's centre (null without a path) and how it was found.
/// </summary>
public sealed record SpawnFacing(SpawnFacingSource Source, Vector3? Exit, IReadOnlyList<float> Yaws, string Note);

/// <summary>Spawn-room facing (M22; server-side, computed once per start area from the level).</summary>
public sealed partial class World
{
    SpawnBlocker[]? _blockers;
    readonly Dictionary<int, SpawnFacing> _facings = new();

    /// <summary>
    /// The sides' spawn-room blockers (finite, positive size); they mark the rooms' exits for <see cref="StartAreaFacing"/>.
    /// Unset: the level's (<see cref="CollisionWorld.Blockers"/>, which also make them solid for the other side).
    /// </summary>
    public IReadOnlyList<SpawnBlocker> SpawnBlockers
    {
        get => _blockers ?? Collision?.Blockers ?? [];
        init => _blockers = SpawnBlocker.Checked(value);
    }

    /// <summary>Grid step of the room's walkable floor (metres).</summary>
    public const float RoomGridStep = 0.5f;
    /// <summary>Open view (metres, at eye height) every spot must have along an area's shared facing (ours).</summary>
    public const float MinClearView = 5f;

    /// <summary>
    /// The facing of a player spawned at <paramref name="spot"/> in start area <paramref name="areaIndex"/> (M22, user's
    /// decision): out of the spawn room toward its exit (<see cref="StartAreaFacings"/>); a spot that is not one of the area's
    /// spots gets the facing of the nearest one.
    /// </summary>
    public float StartAreaFacing(int areaIndex, Vector3 spot)
    {
        var spots = SpotsIn(areaIndex);
        var f = StartAreaFacings(areaIndex);
        int best = 0;
        for (int i = 1; i < spots.Count; i++)
            if (Vector3.DistanceSquared(spots[i], spot) < Vector3.DistanceSquared(spots[best], spot)) best = i;
        return f.Yaws[best];
    }

    /// <summary>
    /// The facings of start area <paramref name="areaIndex"/>'s spots (<see cref="StartAreaSpots"/> order), computed once
    /// (interpretation; spec/behavior.md M22). With a level and blockers of the area's side: the room's walkable floor is a
    /// <see cref="RoomGridStep"/> grid flood-filled from the area's centre over the character collision (a step between cells
    /// rises or falls at most <see cref="SimConfig.StepHeight"/> + 0.1 m, nothing in the way at 1.0 and 1.8 m), the side's
    /// blockers acting as walls; cells next to a blocker are the exit, and a breadth-first distance from them gives each cell
    /// its shortest walkable way out. From each spot that way is followed and the spot faces, horizontally, the farthest point
    /// of it still in plain view at eye height (so it looks down the corridor toward the exit, not at a wall). When every
    /// spot's way out ends at the same blocker and every spot has <see cref="MinClearView"/> of open view along the first spot's
    /// facing (the spot nearest the centre), all take that facing; else each spot keeps its own. Otherwise (no level, no
    /// blockers for the side, a spot with no way out): the marker's own rotation.
    /// </summary>
    public SpawnFacing StartAreaFacings(int areaIndex)
    {
        if (_facings.TryGetValue(areaIndex, out var cached)) return cached;
        var area = _startAreas[areaIndex];
        var spots = SpotsIn(areaIndex);
        SpawnFacing Marker(string why) => new(SpawnFacingSource.MarkerRotation, null, spots.Select(_ => area.Yaw).ToArray(), why);
        var blockers = SpawnBlockers.Where(b => b.Side == area.Side).ToArray();
        SpawnFacing result;
        if (Collision is null) result = Marker("no level");
        else if (blockers.Length == 0) result = Marker($"no blockers for {area.Side}");
        else result = ExitFacing(area, spots, blockers) ?? Marker("no walkable way from the area to its blockers");
        _facings[areaIndex] = result;
        return result;
    }

    SpawnFacing? ExitFacing(StartArea area, IReadOnlyList<Vector3> spots, SpawnBlocker[] blockers)
    {
        var mesh = Collision!.Character;
        const float step = RoomGridStep;
        float maxRise = Config.StepHeight + 0.1f;
        // The region: the area and its side's blockers, 15 m around.
        var lo = new Vector2(area.Center.X, area.Center.Y) - new Vector2(15f);
        var hi = new Vector2(area.Center.X, area.Center.Y) + new Vector2(15f);
        foreach (var b in blockers)
        {
            float r = MathF.Max(b.Size.X, b.Size.Y) / 2f + 15f;
            lo = Vector2.Min(lo, new Vector2(b.Center.X - r, b.Center.Y - r));
            hi = Vector2.Max(hi, new Vector2(b.Center.X + r, b.Center.Y + r));
        }
        (int, int) Cell(Vector3 p) => ((int)MathF.Round(p.X / step), (int)MathF.Round(p.Y / step));
        Vector3 At((int X, int Y) c, float z) => new(c.X * step, c.Y * step, z);
        bool InRegion((int X, int Y) c) => c.X * step >= lo.X && c.X * step <= hi.X && c.Y * step >= lo.Y && c.Y * step <= hi.Y;
        float? Ground((int X, int Y) c, float from)
        {
            var hit = mesh.Raycast(new Vector3(c.X * step, c.Y * step, from), -Vector3.UnitZ, maxRise + 1.5f + 1.2f);
            return hit is { } h && h.Normal.Z >= 0.6f ? h.Point.Z : null;
        }

        // Flood fill of the room's floor from the area's centre (and every spot, so each has a cell).
        var z = new Dictionary<(int, int), float>();
        var links = new Dictionary<(int, int), List<(int, int)>>();
        var exits = new Dictionary<(int, int), int>();   // exit cell → the blocker it leads to
        var queue = new Queue<(int, int)>();
        foreach (var start in spots.Prepend(area.Center))
        {
            var c = Cell(start);
            if (z.ContainsKey(c) || Ground(c, start.Z + 1.2f) is not { } g) continue;
            z[c] = g; links[c] = new(); queue.Enqueue(c);
        }
        (int, int)[] dirs = [(1, 0), (-1, 0), (0, 1), (0, -1)];
        while (queue.Count > 0 && z.Count < 60000)
        {
            var c = queue.Dequeue();
            float zc = z[c];
            var from = At(c, zc);
            foreach (var (dx, dy) in dirs)
            {
                var n = (c.Item1 + dx, c.Item2 + dy);
                if (!InRegion(n)) continue;
                var probe = At(n, zc + 1f);
                int blocker = Array.FindIndex(blockers, b => b.Contains(probe));
                if (blocker >= 0) { exits.TryAdd(c, blocker); continue; }
                if (Ground(n, zc + maxRise + 1.2f) is not { } zn || MathF.Abs(zn - zc) > maxRise) continue;
                if (z.TryGetValue(n, out float known) && MathF.Abs(known - zn) > 0.05f) continue;   // another level at the same place
                var dir = new Vector3(dx, dy, 0f);
                if (mesh.Raycast(from + new Vector3(0, 0, 1.0f), dir, step) is not null || mesh.Raycast(from + new Vector3(0, 0, 1.8f), dir, step) is not null) continue;
                if (!z.ContainsKey(n)) { z[n] = zn; links[n] = new(); queue.Enqueue(n); }
                if (!links[c].Contains(n)) links[c].Add(n);
                if (!links[n].Contains(c)) links[n].Add(c);
            }
        }
        if (exits.Count == 0) return null;

        // Distance from the exits over the room's floor: first from every blocker, to find the area's exit (the blocker
        // nearest the area's centre by walking), then from that exit alone, so every spot faces the same way out.
        var distance = new Dictionary<(int, int), int>();
        void Distances(IEnumerable<(int, int)> from)
        {
            distance.Clear();
            var bfs = new Queue<(int, int)>();
            foreach (var e in from.OrderBy(e => e.Item1).ThenBy(e => e.Item2)) { distance[e] = 0; bfs.Enqueue(e); }
            while (bfs.Count > 0)
            {
                var c = bfs.Dequeue();
                foreach (var n in links[c])
                    if (!distance.ContainsKey(n)) { distance[n] = distance[c] + 1; bfs.Enqueue(n); }
            }
        }
        Distances(exits.Keys);

        List<(int, int)>? WayOut(Vector3 p)
        {
            var c = Cell(p);
            if (!distance.ContainsKey(c)) return null;
            var path = new List<(int, int)> { c };
            while (distance[c] > 0)
            {
                c = links[c].Where(distance.ContainsKey).OrderBy(n => distance[n]).ThenBy(n => n.Item1).ThenBy(n => n.Item2).First();
                path.Add(c);
            }
            return path;
        }

        var first = WayOut(area.Center) ?? WayOut(spots[0]);
        if (first is null) return null;
        int door = exits[first[^1]];
        Distances(exits.Where(e => e.Value == door).Select(e => e.Key));

        float eye = Config.EyeHeight;
        var yaws = new float[spots.Count];
        for (int i = 0; i < spots.Count; i++)
        {
            var path = WayOut(spots[i]);
            if (path is null || path.Count < 2) return null;
            var origin = spots[i] + new Vector3(0, 0, eye);
            var target = At(path[1], z[path[1]]);
            foreach (var cell in path.Skip(1))
            {
                var point = At(cell, z[cell] + eye);
                var d = point - origin;
                float length = d.Length();
                if (length > 1e-3f && mesh.Raycast(origin, d / length, length) is not null) break;   // the first waypoint out of view
                target = point;
            }
            var flat = new Vector2(target.X - spots[i].X, target.Y - spots[i].Y);
            if (flat.LengthSquared() < 1e-6f) return null;
            yaws[i] = MathF.Atan2(-flat.X, flat.Y);
        }
        // The area shares the first spot's facing (the spot nearest the area's centre) when every spot then still sees at
        // least MinClearView ahead at eye height; else each spot keeps its own.
        string note;
        float shared = yaws[0];
        var ahead = new Vector3(-MathF.Sin(shared), MathF.Cos(shared), 0f);
        if (spots.All(q => mesh.Raycast(q + new Vector3(0, 0, eye), ahead, MinClearView) is null)) { Array.Fill(yaws, shared); note = $"shared; exit blocker at {blockers[door].Center.X:0.#},{blockers[door].Center.Y:0.#}"; }
        else note = $"per spot (the shared facing is blocked from some spot); exit blocker at {blockers[door].Center.X:0.#},{blockers[door].Center.Y:0.#}";
        var centrePath = WayOut(area.Center) ?? WayOut(spots[0]);
        Vector3? exit = centrePath is null ? null : At(centrePath[^1], z[centrePath[^1]]);
        return new SpawnFacing(SpawnFacingSource.Exit, exit, yaws, note);
    }
}
