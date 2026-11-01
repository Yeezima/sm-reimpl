using System.Numerics;

namespace SmSim.Extras.Musou.Campaign;

/// <summary>
/// The hub's walkable shell: the inside of the campaign Thunderhawk (<c>thunderhawk_interior</c>, blueprint of the same name, in the singleplayer chunk
/// <c>part_01__s_002_r001_new_greybox_1</c>; a singleplayer-content dependency of the look, docs/discovery.md "Musou Mode pass 2: the hub"). The chunk has no collision, so this is generated:
/// the numbers below are measured from the model's block 3 (<c>GEO_hawkInt_roof_floor_front</c>: floor, deck and ceiling), block 11 (<c>GEO_hawkInt_stairs</c>), the side walls (blocks 7 and 13), the tech panels
/// (blocks 6 and 9) and the rear door (block 0) in model space, rounded to centimetres (supported by evidence for the measured values; the ramp's end wall, the 24 m cap and the thicknesses are ours).
/// Nothing from the install is stored: the server composes the same triangles the client does, so the map hash holds them.
/// </summary>
/// <remarks>
/// Model space: X across (the corridor is about 4.5 m wide), Y along (the cockpit end at -5, the ramp's hinge at 22.16, the open ramp ending at about 27.7), Z up. <see cref="TfwHub"/> moves it to the world.
/// Every measure is in this space; <see cref="Corners"/> returns the triangles (three corners each, outward normals) already moved by an offset.
/// </remarks>
public static class TfwShip
{
    /// <summary>The model's blueprint, mesh and chunk, and the door that is posed open (<see cref="DoorBlock"/> on the bone <c>RIG_Frt_Dr</c>, its hinge, a further -54 degrees about X).</summary>
    public const string Chunk = "part_01__s_002_r001_new_greybox_1", Mesh = "thunderhawk_interior";
    public const int DoorBlock = 0;
    public const float DoorOpenDegrees = -54f;
    public static readonly Vector3 DoorHinge = new(0f, 22.162f, 1.942f);

    /// <summary>The door's open pose as a row-vector matrix: a further 54 degrees down about X through <see cref="DoorHinge"/> (the closed mesh then lies flat, about 4.5 m past the floor).</summary>
    public static Matrix4x4 DoorOpen =>
        Matrix4x4.CreateTranslation(-DoorHinge) * Matrix4x4.CreateRotationX(DoorOpenDegrees * MathF.PI / 180f) * Matrix4x4.CreateTranslation(DoorHinge);

    /// <summary>The floor of the long corridor (z), the front deck above it, the ramp (the posed door's top is about 2.12) and where each ends along Y.</summary>
    public const float FloorZ = 2.05f, DeckZ = 3.35f, RampZ = 2.11f;
    public const float FrontY = -5f, DeckEndY = 3.02f, StairsEndY = 5.3f, HingeY = 22.1f, RampEndY = 27.6f;
    /// <summary>Where the invisible end wall stands: the top of the ramp, where the troop bay's walls start, so nobody steps onto the ramp and sees the unfinished scene past it.</summary>
    public const float EndWallY = HingeY;
    /// <summary>The side walls' inner faces: the hull is not symmetric (the left face is at -2.2, the right at 2.3), the ramp (the door's width) is 2.6 each side.</summary>
    public const float LeftWall = -2.2f, RightWall = 2.3f, RampHalfWidth = 2.6f;
    /// <summary>The tech panels along the walls (their faces; they reach up to <see cref="PanelTop"/> and span <see cref="PanelFrom"/> to <see cref="PanelTo"/> along Y).</summary>
    public const float LeftPanel = -1.67f, RightPanel = 1.715f, PanelTop = 5.3f, PanelFrom = 5.38f, PanelTo = 20.5f;
    /// <summary>The ceiling's underside along Y (the roof slopes down toward the ramp), then an invisible cap over the open ramp, and the top of every wall: 24 m of headroom covers a full jump-pack jump.</summary>
    public static readonly (float Y, float Z)[] Ceiling = [(-5f, 5.75f), (1f, 5.95f), (7.5f, 6.3f), (24.9f, 5.45f)];
    public const float CapZ = 24f, WallTop = 25f, Thickness = 1f;
    /// <summary>The model's middle along Y: <see cref="TfwHub.Centre"/> is the floor point below it.</summary>
    public const float MidY = 12f;

    /// <summary>The ship's footprint, flat (where a player counts as aboard), relative to the centre, and its height above the floor.</summary>
    public const float HalfWidth = RampHalfWidth + Thickness;
    /// <remarks>The ship is turned half a circle in the world, so its ramp looks away from the battlefield (south) and the level is never seen through it: model Y runs south. The cockpit end reaches <see cref="Fore"/> north of the centre, the ramp's end <see cref="Aft"/> south of it.</remarks>
    public const float Fore = MidY - FrontY + Thickness, Aft = RampEndY + Thickness - MidY;
    /// <summary>The way the ramp looks in the world: along -Y, as a yaw (0 is +Y).</summary>
    public const float RampYaw = MathF.PI;
    public const float Height = WallTop - FloorZ;

    /// <summary>The model's points where the stations stand (the monitor hanging near the ramp, the right tech panel) and the four spawns (all ours; positions only, on the floor).</summary>
    public static readonly Vector2 Monitor = new(-0.1f, 18.3f), Panel = new(0.5f, 8.5f);
    public static readonly Vector2[] Spawns = [new(-0.9f, 11.5f), new(0.9f, 11.5f), new(-0.9f, 13.5f), new(0.9f, 13.5f)];

    /// <summary>A point of the model in the world of a hub whose floor's middle is at <paramref name="centre"/>: turned half a circle about Z (so a proper rotation: triangles keep their winding), the floor at the centre's height.</summary>
    public static Vector3 ToWorld(Vector3 model, Vector3 centre) => new(centre.X - model.X, centre.Y + MidY - model.Y, centre.Z + model.Z - FloorZ);

    /// <summary>The model's matrix to the world's (row-vector), the same move as <see cref="ToWorld"/>.</summary>
    public static Matrix4x4 ModelToWorld(Vector3 centre) =>
        Matrix4x4.CreateScale(-1f, -1f, 1f) * Matrix4x4.CreateTranslation(centre.X, centre.Y + MidY, centre.Z - FloorZ);

    /// <summary>The corners of every solid (three a triangle, each triangle facing out of its solid) in the world of a hub at <paramref name="centre"/>.</summary>
    public static Vector3[] Corners(Vector3 centre)
    {
        var corners = new List<Vector3>();
        foreach (var box in Boxes()) corners.AddRange(TfwMap.Triangles(box));
        foreach (var (x0, x1, profile) in Prisms()) corners.AddRange(Prism(x0, x1, profile));
        for (int i = 0; i < corners.Count; i++) corners[i] = ToWorld(corners[i], centre);
        return corners.ToArray();
    }

    /// <summary>The boxes, in model space.</summary>
    public static IReadOnlyList<TfwSolid> Boxes()
    {
        const float T = Thickness;
        float left = LeftWall - T, right = RightWall + T, rl = -RampHalfWidth - T, rr = RampHalfWidth + T;
        float bottom = FloorZ - T;
        return
        [
            new(new Vector3(left, FrontY - T, DeckZ - T), new Vector3(right, DeckEndY, DeckZ)),                    // the front deck
            new(new Vector3(left, StairsEndY, bottom), new Vector3(right, HingeY, FloorZ)),                         // the corridor's floor
            new(new Vector3(rl, HingeY, bottom), new Vector3(rr, RampEndY + T, RampZ)),                             // the open ramp
            new(new Vector3(left, FrontY - T, bottom), new Vector3(LeftWall, HingeY, WallTop)),                      // the hull's side walls
            new(new Vector3(RightWall, FrontY - T, bottom), new Vector3(right, HingeY, WallTop)),
            new(new Vector3(rl, HingeY, bottom), new Vector3(-RampHalfWidth, RampEndY + T, WallTop)),               // the ramp's invisible side walls
            new(new Vector3(RampHalfWidth, HingeY, bottom), new Vector3(rr, RampEndY + T, WallTop)),
            new(new Vector3(left, FrontY - T, bottom), new Vector3(right, FrontY, WallTop)),                         // the cockpit end
            new(new Vector3(rl, RampEndY, bottom), new Vector3(rr, RampEndY + T, WallTop)),                          // the ramp's far end, closing the ramp behind the end wall
            new(new Vector3(left, EndWallY, bottom), new Vector3(right, EndWallY + T, WallTop)),                      // the end wall at the ramp's top, full width and height: the player cannot reach the ramp
            new(new Vector3(LeftWall, PanelFrom, FloorZ), new Vector3(LeftPanel, PanelTo, PanelTop)),               // the tech panels, so a player stands before them, not in them
            new(new Vector3(RightPanel, PanelFrom, FloorZ), new Vector3(RightWall, PanelTo, PanelTop)),
            new(new Vector3(rl, Ceiling[^1].Y, CapZ), new Vector3(rr, RampEndY + T, WallTop)),                       // the cap over the open ramp
        ];
    }

    /// <summary>The convex prisms, in model space: each extruded across X from <c>X0</c> to <c>X1</c> over a (Y, Z) profile.</summary>
    static IEnumerable<(float X0, float X1, (float Y, float Z)[] Profile)> Prisms()
    {
        const float T = Thickness;
        float left = LeftWall - T, right = RightWall + T;
        yield return (left, right, [(DeckEndY, DeckZ), (StairsEndY, FloorZ), (StairsEndY, FloorZ - T), (DeckEndY, DeckZ - T)]);   // the stairs, as one ramp
        for (int i = 0; i + 1 < Ceiling.Length; i++)
        {
            var (y0, z0) = Ceiling[i];
            var (y1, z1) = Ceiling[i + 1];
            yield return (-RampHalfWidth - T, RampHalfWidth + T, [(y0, z0), (y1, z1), (y1, WallTop), (y0, WallTop)]);
        }
    }

    /// <summary>The triangles of a convex prism (three corners each, facing out): its end caps and one quad per profile edge.</summary>
    public static Vector3[] Prism(float x0, float x1, IReadOnlyList<(float Y, float Z)> profile)
    {
        var low = profile.Select(p => new Vector3(x0, p.Y, p.Z)).ToArray();
        var high = profile.Select(p => new Vector3(x1, p.Y, p.Z)).ToArray();
        var centre = (low.Aggregate(Vector3.Zero, (a, b) => a + b) + high.Aggregate(Vector3.Zero, (a, b) => a + b)) / (2f * profile.Count);
        var list = new List<Vector3>();
        void Add(Vector3 a, Vector3 b, Vector3 c)
        {
            var normal = Vector3.Cross(b - a, c - a);
            if (Vector3.Dot(normal, (a + b + c) / 3f - centre) < 0f) (b, c) = (c, b);
            list.Add(a); list.Add(b); list.Add(c);
        }
        for (int i = 1; i + 1 < profile.Count; i++)
        {
            Add(low[0], low[i], low[i + 1]);
            Add(high[0], high[i], high[i + 1]);
        }
        for (int i = 0; i < profile.Count; i++)
        {
            int j = (i + 1) % profile.Count;
            Add(low[i], low[j], high[j]);
            Add(low[i], high[j], high[i]);
        }
        return list.ToArray();
    }
}
