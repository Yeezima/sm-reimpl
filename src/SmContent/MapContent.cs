using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using SmFormats.Bod;
using SmFormats.Havok;
using SmFormats.Obpk;
using SmSim;

namespace SmContent;

/// <summary>A team deathmatch spawn marker: position (feet, metres) and facing (sim yaw, radians).</summary>
public readonly record struct MapSpawn(string Name, Vector3 Position, float Yaw);

/// <summary>
/// A Seize Ground point marker (M16): name, position, rotation about Z (radians), scale (metres) and shape number
/// (<c>Shape</c>: 3 cylinder). As a <see cref="ControlPoint"/>: radius half the X scale, height the Z scale centred on the
/// position (interpretation, spec/behavior.md M16).
/// </summary>
public sealed record MapControlPoint(string Name, Vector3 Position, float Yaw, Vector3 Scale, int Shape)
{
    public ControlPoint ToSim() => new(Name, Position, Scale.X / 2f, Scale.Z / 2f);
}

/// <summary>A team start area marker (<c>mkr_start_marine</c>, <c>mkr_start_ork</c> = Chaos; matched ignoring case): shape 1 box, 3 cylinder.</summary>
public sealed record MapStartArea(string Name, Team Side, Vector3 Position, float Yaw, Vector3 Scale, int Shape)
{
    public StartArea ToSim() => new(Side, Position, Scale, Yaw, Shape == 3 ? AreaShape.Cylinder : AreaShape.Box);
}

/// <summary>
/// A side's spawn-room blocker (M22): a <c>base/MissionMovementBlocker</c> named <c>marine_blocker_*</c> (Space Marines) or
/// <c>chaos_blocker_*</c> (Chaos; matched ignoring case) in the region's mission layers, as the region script lists them
/// (<c>blockerMarkersMarine</c> / <c>blockerMarkersChaos</c>). A box: scale is its full size, rotated about Z.
/// </summary>
public sealed record MapBlocker(string Name, Team Side, Vector3 Position, float Yaw, Vector3 Scale)
{
    public SpawnBlocker ToSim() => new(Side, Position, Scale, Yaw);
}

/// <summary>
/// A kill region (M18): a <c>base/KillRegion</c> object in the region's mission layers, read as a box (<c>Shape</c> 0) whose
/// scale is its full size, centred on the position, turned about Z (interpretation, spec/behavior.md M18).
/// </summary>
public sealed record MapKillRegion(string Name, Vector3 Position, float Yaw, Vector3 Scale, int Shape)
{
    public KillVolume ToSim() => new(Position, Scale / 2f, Yaw);
}

/// <summary>
/// A multiplayer map read from the install: its region's static collision (all Havok layers, by name) and the TDM spawn
/// markers. The region archive is <c>data/chunks/&lt;map&gt;__wrld.oppc</c> or <c>__r000_main.oppc</c>, holding
/// <c>&lt;region&gt;.hkt</c> and <c>mp/&lt;map&gt;/&lt;region&gt;/d_tdm.Layer</c>. Which layer characters and shots use is
/// an interpretation (<see cref="CharacterLayer"/>, <see cref="WeaponLayer"/>; spec/behavior.md).
/// </summary>
/// <param name="Hash">Identifies the map for the handshake: SHA-256 of the collision file's bytes, the spawn layer's
/// bytes, the mission layers' and region script's bytes (M16), the layer roles and <see cref="LoaderVersion"/> (not of
/// computed floats, which could round differently on another CPU or runtime).</param>
/// <remarks>
/// Seize Ground (M16): the region script (<c>&lt;region&gt;.lua</c>, compiled Lua) is only searched for whole strings of the
/// form <c>mkr_objective_N</c> / <c>mkr_cp_N</c>, in byte order (its constant pool; the instructions are not read, decoded or
/// run); those names, found as cylinder markers in the region's <c>d_mission*</c> layers, are the control points in that
/// order. Without the script, every cylinder marker with such a name is taken, in name order (a note says so).
/// </remarks>
public sealed record MapContent(string Name, string Region, IReadOnlyList<HavokCollisionLayer> Layers, IReadOnlyList<MapSpawn> Spawns, ulong Hash)
{
    /// <summary>Bump when the way collision, spawns, points or start areas are derived from the files changes.</summary>
    public const int LoaderVersion = 8;   // 4: spawn-room blockers (M22); 5: the blockers are solid for the other side; 6: weapon-layer surface materials (M25a; shot events name them); 7: the blockers reach above the level (M31); 8: movable props (the green crates) are solid and pierceable (M39)
    /// <summary>Control points in the script's order (Seize Ground; empty when the map has none).</summary>
    public IReadOnlyList<MapControlPoint> ControlPoints { get; init; } = [];
    /// <summary>The sides' start areas.</summary>
    public IReadOnlyList<MapStartArea> StartAreas { get; init; } = [];
    /// <summary>The sides' spawn-room blockers (M22: they mark the rooms' exits for the spawn facing; solid for the other side's players).</summary>
    public IReadOnlyList<MapBlocker> Blockers { get; init; } = [];
    /// <summary>Kill regions from the mission layers (M18; a living player who enters one dies).</summary>
    public IReadOnlyList<MapKillRegion> KillRegions { get; init; } = [];
    /// <summary>Movable props (M39: the green ammo crates; <see cref="MapProps"/>): solid for every player and every shot, pierced by shots with a <see cref="PierceSpec"/>.</summary>
    public IReadOnlyList<MapProp> Props { get; init; } = [];
    /// <summary>What did not go as expected while reading points and start areas.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
    static readonly System.Text.RegularExpressions.Regex PointName = new(@"(?<![A-Za-z0-9_])(mkr_objective_[0-9]{1,3}|mkr_cp_[0-9]{1,3})(?![A-Za-z0-9_])");
    /// <summary>Interpretation: the largest layer, with the low step and ramp helpers, is what characters walk on.</summary>
    public const string CharacterLayer = "2";
    /// <summary>Interpretation: the other full-coverage layer, without those helpers but with extra thin panels, stops shots.</summary>
    public const string WeaponLayer = "8";
    /// <summary>
    /// What characters collide with (M18): <see cref="CharacterLayer"/> and <see cref="WeaponLayer"/> together. Interpretation:
    /// layer "8" holds thin vertical panels that layer "2" lacks at the open edges (up to 100 m tall at the outer edges,
    /// 3 m railings round the pits); without them players walk off. Shots still use layer "8" alone.
    /// </summary>
    public static readonly IReadOnlyList<string> CharacterLayers = [CharacterLayer, WeaponLayer];
    static readonly string[] Regions = ["wrld", "r000_main"];

    /// <summary>Maps whose region archive exists in this install (the mp_dlc3 maps keep their collision elsewhere; not read yet).</summary>
    public static IReadOnlyList<string> Available(string gameDir)
    {
        string chunks = Path.Combine(gameDir, "data", "chunks");
        if (!Directory.Exists(chunks)) return [];
        return Directory.GetFiles(chunks, "mp_*.oppc")
            .Select(Path.GetFileNameWithoutExtension)
            .Select(f => f!.Split("__"))
            .Where(p => p.Length == 2 && Regions.Contains(p[1]))
            .Select(p => p[0]).Distinct().Order(StringComparer.Ordinal).ToList();
    }

    public static MapContent Load(string gameDir, string map)
    {
        if (map.Length == 0 || map.Length > 64 || map.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_')))
            throw new ArgumentException($"'{map}' is not a map name.", nameof(map));
        foreach (var region in Regions)
        {
            string path = Path.Combine(gameDir, "data", "chunks", $"{map}__{region}.oppc");
            if (File.Exists(path)) return Read(path, map, region);
        }
        throw new FileNotFoundException($"No region archive for '{map}' (looked for {string.Join(", ", Regions.Select(r => $"{map}__{r}.oppc"))}).");
    }

    public static MapContent Read(string archivePath, string map, string region)
    {
        IReadOnlyList<HavokCollisionLayer>? layers = null;
        var spawns = new List<MapSpawn>();
        byte[]? collisionBytes = null, spawnBytes = null, scriptBytes = null;
        var mission = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        var props = new List<MapProp>();
        var propSources = new List<byte[]>();
        var propNotes = new List<string>();
        using (var archive = ObpkArchive.Open(archivePath))
        {
            archive.ReadEntries(
                e => (archive.GroupName(e) == "hkt" && e.FileName == region) || (archive.GroupName(e) == "lua" && e.FileName == region)
                     || (archive.GroupName(e) == "Layer" && (e.FileName == "d_tdm" || e.FileName.StartsWith("d_mission", StringComparison.Ordinal))),
                (e, data) =>
                {
                    if (archive.GroupName(e) == "hkt")
                    {
                        if (collisionBytes is not null) throw new InvalidDataException($"{Path.GetFileName(archivePath)} has more than one {region}.hkt.");
                        collisionBytes = data.ToArray();
                        layers = HavokWorldCollision.Read(new HavokPackfile(collisionBytes));
                    }
                    else if (archive.GroupName(e) == "lua")
                    {
                        if (scriptBytes is not null) throw new InvalidDataException($"{Path.GetFileName(archivePath)} has more than one {region}.lua.");
                        scriptBytes = data.ToArray();
                    }
                    else if (e.FileName != "d_tdm")
                    {
                        if (!mission.TryAdd(e.FileName, data.ToArray())) throw new InvalidDataException($"{Path.GetFileName(archivePath)} has more than one {e.FileName}.Layer.");
                    }
                    else
                    {
                        if (spawnBytes is not null) throw new InvalidDataException($"{Path.GetFileName(archivePath)} has more than one d_tdm.Layer.");
                        spawnBytes = data.ToArray();
                        CollectSpawns(BodDocument.Read(data).Root, spawns, 0);
                    }
                });
            // M39: movable props (the green crates), solid for characters and shots; not in the level's baked collision.
            (props, propSources) = MapProps.Read(archive, propNotes);
        }
        if (layers is null || collisionBytes is null) throw new InvalidDataException($"{Path.GetFileName(archivePath)} has no {region}.hkt.");
        foreach (var required in new[] { CharacterLayer, WeaponLayer })
            if (layers.FirstOrDefault(l => l.Name == required) is not { TriangleCount: > 0 })
                throw new InvalidDataException($"{map}: collision layer '{required}' is missing or empty.");
        spawns.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));

        var notes = new List<string>(propNotes);
        var markers = new List<Marker>();
        foreach (var bytes in mission.Values) CollectMarkers(BodDocument.Read(bytes).Root, markers, 0);
        var points = ReadPoints(markers, scriptBytes, notes);
        var areas = new List<MapStartArea>();
        foreach (var (key, side) in new[] { ("mkr_start_marine", Team.SpaceMarines), ("mkr_start_ork", Team.Chaos) })
        {
            var found = markers.Where(m => m.Name.Equals(key, StringComparison.OrdinalIgnoreCase)).ToList();
            if (found.Count == 0) { notes.Add($"no start area {key}"); continue; }
            if (found.Count > 1) notes.Add($"{found.Count} markers named {key}; the first is used");
            var a = found[0];
            if (a.Shape is not (1 or 3)) notes.Add($"{a.Name}: shape {a.Shape} (not a box or cylinder), read as a box");
            if (!(a.Scale.X > 0f && a.Scale.Y > 0f)) { notes.Add($"{a.Name}: empty scale; not used"); continue; }
            areas.Add(new MapStartArea(a.Name, side, a.Position, a.Yaw, a.Scale, a.Shape));
        }
        var kills = new List<MapKillRegion>();
        foreach (var bytes in mission.Values) CollectKillRegions(BodDocument.Read(bytes).Root, kills, notes, 0);
        var blockers = new List<MapBlocker>();
        foreach (var bytes in mission.Values) CollectBlockers(BodDocument.Read(bytes).Root, blockers, notes, 0);
        blockers.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        var extra = mission.Values.Append(scriptBytes ?? []).Concat(propSources).ToArray();
        return new MapContent(map, region, layers, spawns, ComputeHash(collisionBytes, spawnBytes ?? [], [.. mission.Keys], extra))
        { ControlPoints = points, StartAreas = areas, KillRegions = kills, Blockers = blockers, Props = props, Notes = notes };
    }

    readonly record struct Marker(string Name, Vector3 Position, float Yaw, Vector3 Scale, int Shape);

    /// <summary>
    /// Control points: the point names in the script's bytes (first occurrence order) that name an enabled cylinder marker
    /// (shape 3) with a positive X and Z scale; without a script, every such marker with a point name, by name (numerically).
    /// At most <see cref="World.MaxControlPoints"/>.
    /// </summary>
    static List<MapControlPoint> ReadPoints(List<Marker> markers, byte[]? script, List<string> notes)
    {
        IEnumerable<string> names;
        if (script is null)
        {
            notes.Add("no region script; the control points are the cylinder markers named mkr_objective_N / mkr_cp_N");
            names = markers.Select(m => m.Name).Where(n => PointName.Match(n) is { Success: true } x && x.Length == n.Length)
                .OrderBy(n => n.Length).ThenBy(n => n, StringComparer.Ordinal);
        }
        else names = PointName.Matches(Encoding.Latin1.GetString(script)).Select(m => m.Value);
        var points = new List<MapControlPoint>();
        foreach (var name in names.Distinct())
        {
            var found = markers.Where(m => m.Name == name).ToList();
            if (found.Count == 0) { notes.Add($"the script names {name}, which no mission layer has"); continue; }
            var m = found[0];
            if (m.Shape != 3 || !(m.Scale.X > 0f) || !(m.Scale.Z > 0f)) { notes.Add($"{name}: shape {m.Shape}, scale {m.Scale} is not a cylinder; skipped"); continue; }
            if (MathF.Abs(m.Scale.X - m.Scale.Y) > 1e-3f) notes.Add($"{name}: X and Y scale differ ({m.Scale.X}, {m.Scale.Y}); X is used");
            if (points.Count == World.MaxControlPoints) { notes.Add($"more than {World.MaxControlPoints} control points; {name} and later ones are left out"); break; }
            points.Add(new MapControlPoint(m.Name, m.Position, m.Yaw, m.Scale, m.Shape));
        }
        return points;
    }

    /// <summary>Every enabled marker-like struct (Name, Position, Rotation, Scale; Shape when present), anywhere in the layer's tree.</summary>
    static void CollectMarkers(BodValue value, List<Marker> markers, int depth)
    {
        if (depth > 64) return;
        switch (value)
        {
            case BodStruct s:
                if (s["Name"] is BodString { Text: { Length: > 0 } name } && s["Disabled"] is not BodBool { Value: true }
                    && Floats(s["Position"]) is [var x, var y, var z, ..] && Floats(s["Rotation"]) is [_, _, var degrees, ..] && Floats(s["Scale"]) is [var sx, var sy, var sz, ..])
                    markers.Add(new Marker(name, new Vector3(x, y, z), degrees * MathF.PI / 180f, new Vector3(sx, sy, sz), s["Shape"] is BodInt { Value: var shape } ? shape : -1));
                foreach (var f in s.Fields) CollectMarkers(f.Value, markers, depth + 1);
                break;
            case BodList l: foreach (var item in l.Items) CollectMarkers(item.Value, markers, depth + 1); break;
            case BodRef r: CollectMarkers(r.Value, markers, depth + 1); break;
        }
    }

    /// <summary>
    /// Every <c>base/KillRegion</c> (class matched ignoring case) with a name, position, rotation and positive scale, not
    /// <c>Disabled</c>, not <c>Enable</c> false and not <c>Exclude</c> true (meaning unknown; such regions are noted and skipped).
    /// Shape 0 is read as a box; other shapes and X/Y rotations are noted and skipped.
    /// </summary>
    static void CollectKillRegions(BodValue value, List<MapKillRegion> kills, List<string> notes, int depth)
    {
        if (depth > 64) return;
        switch (value)
        {
            case BodStruct s:
                if (string.Equals(s.Class.Text, "base/KillRegion", StringComparison.OrdinalIgnoreCase) && s["Disabled"] is not BodBool { Value: true })
                {
                    string name = s["Name"] is BodString { Text: { } n } ? n : "";
                    int shape = s["Shape"] is BodInt { Value: var sh } ? sh : -1;
                    if (s["Enable"] is BodBool { Value: false }) notes.Add($"kill region {name}: Enable false; skipped");
                    else if (s["Exclude"] is BodBool { Value: true }) notes.Add($"kill region {name}: Exclude true (meaning unknown); skipped");
                    else if (Floats(s["Position"]) is not [var x, var y, var z, ..] || Floats(s["Rotation"]) is not [var rx, var ry, var rz, ..]
                             || Floats(s["Scale"]) is not [var sx, var sy, var sz, ..])
                        notes.Add($"kill region {name}: no position, rotation or scale; skipped");
                    else if (shape != 0) notes.Add($"kill region {name}: shape {shape} (only 0, read as a box, is known); skipped");
                    else if (rx != 0f || ry != 0f) notes.Add($"kill region {name}: rotated about X or Y ({rx}, {ry}); skipped");
                    else if (!(sx > 0f && sy > 0f && sz > 0f) || !float.IsFinite(sx + sy + sz + x + y + z + rz)) notes.Add($"kill region {name}: bad scale {sx}, {sy}, {sz}; skipped");
                    else kills.Add(new MapKillRegion(name, new Vector3(x, y, z), rz * MathF.PI / 180f, new Vector3(sx, sy, sz), shape));
                }
                foreach (var f in s.Fields) CollectKillRegions(f.Value, kills, notes, depth + 1);
                break;
            case BodList l: foreach (var item in l.Items) CollectKillRegions(item.Value, kills, notes, depth + 1); break;
            case BodRef r: CollectKillRegions(r.Value, kills, notes, depth + 1); break;
        }
    }

    /// <summary>
    /// Every enabled <c>base/MissionMovementBlocker</c> named <c>marine_blocker_*</c> / <c>chaos_blocker_*</c> with a position,
    /// a rotation about Z only and a positive scale (others are noted and skipped).
    /// </summary>
    static void CollectBlockers(BodValue value, List<MapBlocker> blockers, List<string> notes, int depth)
    {
        if (depth > 64) return;
        switch (value)
        {
            case BodStruct s:
                if (string.Equals(s.Class.Text, "base/MissionMovementBlocker", StringComparison.OrdinalIgnoreCase) && s["Disabled"] is not BodBool { Value: true }
                    && s["Name"] is BodString { Text: { } name })
                {
                    var side = name.StartsWith("marine_blocker", StringComparison.OrdinalIgnoreCase) ? Team.SpaceMarines
                        : name.StartsWith("chaos_blocker", StringComparison.OrdinalIgnoreCase) ? Team.Chaos : Team.None;
                    if (side == Team.None) { }
                    else if (Floats(s["Position"]) is not [var x, var y, var z, ..] || Floats(s["Rotation"]) is not [var rx, var ry, var rz, ..]
                             || Floats(s["Scale"]) is not [var sx, var sy, var sz, ..])
                        notes.Add($"blocker {name}: no position, rotation or scale; skipped");
                    else if (rx != 0f || ry != 0f) notes.Add($"blocker {name}: rotated about X or Y ({rx}, {ry}); skipped");
                    else if (!(sx > 0f && sy > 0f && sz > 0f) || !float.IsFinite(sx + sy + sz + x + y + z + rz)) notes.Add($"blocker {name}: bad scale; skipped");
                    else blockers.Add(new MapBlocker(name, side, new Vector3(x, y, z), rz * MathF.PI / 180f, new Vector3(sx, sy, sz)));
                }
                foreach (var f in s.Fields) CollectBlockers(f.Value, blockers, notes, depth + 1);
                break;
            case BodList l: foreach (var item in l.Items) CollectBlockers(item.Value, blockers, notes, depth + 1); break;
            case BodRef r: CollectBlockers(r.Value, blockers, notes, depth + 1); break;
        }
    }

    static ulong ComputeHash(byte[] collision, byte[] spawns, string[] missionNames, byte[][] extra)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.ASCII.GetBytes($"sm-reimpl map v{LoaderVersion}; characters {string.Join("+", CharacterLayers)}; kill regions box; spawn-room blockers box raised {SpawnBlocker.AboveLevel} m above the level, solid for the other side; shots {WeaponLayer}; props: moving bodies with single-part ppk shapes, solid for characters and shots, pierceable; mission {string.Join(",", missionNames)}\n"));
        Span<byte> length = stackalloc byte[8];
        foreach (var part in new[] { collision, spawns }.Concat(extra))
        {
            BinaryPrimitives.WriteInt64LittleEndian(length, part.Length);
            hash.AppendData(length);
            hash.AppendData(part);
        }
        return BinaryPrimitives.ReadUInt64LittleEndian(hash.GetHashAndReset());
    }

    /// <summary>Marker spheres named <c>*_spawn_*</c>, anywhere in the layer's tree. Rotation is in degrees about Z.</summary>
    static void CollectSpawns(BodValue value, List<MapSpawn> spawns, int depth)
    {
        if (depth > 64) return;
        switch (value)
        {
            case BodStruct s:
                if (s.Class.Text == "base/missionmarkersphere" && s["Name"] is BodString { Text: { } name } && name.Contains("_spawn_", StringComparison.Ordinal)
                    && s["Disabled"] is not BodBool { Value: true }
                    && Floats(s["Position"]) is [var x, var y, var z, ..] && Floats(s["Rotation"]) is [_, _, var degrees, ..])
                    spawns.Add(new MapSpawn(name, new Vector3(x, y, z), degrees * MathF.PI / 180f));
                foreach (var f in s.Fields) CollectSpawns(f.Value, spawns, depth + 1);
                break;
            case BodList l: foreach (var item in l.Items) CollectSpawns(item.Value, spawns, depth + 1); break;
            case BodRef r: CollectSpawns(r.Value, spawns, depth + 1); break;
        }
    }

    static float[]? Floats(BodValue? v) =>
        v is BodArray { Items: var items } && items.All(i => i is BodFloat) ? items.Select(i => ((BodFloat)i).Value).ToArray() : null;

    public HavokCollisionLayer? Layer(string name) => Layers.FirstOrDefault(l => l.Name == name);

    /// <summary>
    /// The simulation's view: the character mesh (<see cref="CharacterLayers"/> merged in that order), the weapon mesh, the
    /// kill regions and the spawn-room blockers (solid for the other side; both layers are checked to exist when loading).
    /// </summary>
    public CollisionWorld ToCollisionWorld()
    {
        var vertices = new List<Vector3>();
        var indices = new List<int>();
        foreach (var name in CharacterLayers)
        {
            var l = Layer(name)!;
            int first = vertices.Count;
            vertices.AddRange(l.Vertices);
            foreach (int i in l.Indices) indices.Add(first + i);
        }
        var weapons = Layer(WeaponLayer)!;
        // M25a: shots report the surface material of the triangle they stop at (when the layer's materials were read).
        var surfaces = weapons.Materials.Length == weapons.TriangleCount ? weapons.Materials : null;
        return new CollisionWorld(new StaticMesh([.. vertices], [.. indices]), new StaticMesh(weapons.Vertices, weapons.Indices, surfaces),
            KillRegions.Select(k => k.ToSim()).ToList(), SimBlockers(), Props.Select(p => p.ToSim()).ToList());
    }

    public IReadOnlyList<SpawnPoint> SpawnPoints() => Spawns.Select(s => new SpawnPoint(s.Position, s.Yaw)).ToList();
    /// <summary>The control points as the simulation takes them.</summary>
    public IReadOnlyList<ControlPoint> SimControlPoints() => ControlPoints.Select(p => p.ToSim()).ToList();
    /// <summary>The start areas as the simulation takes them.</summary>
    public IReadOnlyList<StartArea> SimStartAreas() => StartAreas.Select(a => a.ToSim()).ToList();
    /// <summary>The spawn-room blockers as the simulation takes them.</summary>
    public IReadOnlyList<SpawnBlocker> SimBlockers() => Blockers.Select(b => b.ToSim()).ToList();
}
