using SmFormats.Bod;
using static SmFormats.Bod.SyntheticBodWriter;

namespace SmFormats.Fx;

/// <summary>
/// Synthetic effect files for tests (M25a): texture pages and PSystem documents following our documented interpretation of
/// the layout (docs/discovery.md). Names and values are invented; nothing is copied from the install.
/// </summary>
public static class SyntheticFxWriter
{
    /// <summary>A texture page: per rect its hash, x, y, width, height (atlas fractions) and flags.</summary>
    public static byte[] TexturePage(params (ulong Hash, float X, float Y, float Width, float Height, uint Flags)[] rects)
    {
        var bytes = new byte[8 + 32 * rects.Length];
        var w = new BinaryWriter(new MemoryStream(bytes));
        w.Write((uint)rects.Length); w.Write(0u);
        foreach (var r in rects) { w.Write(r.Hash); w.Write(r.Width); w.Write(r.Height); w.Write(r.X); w.Write(r.Y); w.Write(r.Flags); w.Write(0u); }
        return bytes;
    }

    /// <summary>A keyframed value: a keyless list of [time, value] arrays.</summary>
    public static Node Curve(params (float T, float V)[] keys) => new Values([.. keys.Select(k => (Node)new Arr(new F(k.T), new F(k.V)))]);

    /// <summary>A keyframed colour: [time, [r, g, b, a]] with 8-byte components.</summary>
    public static Node Colours(params (float T, byte R, byte G, byte B, byte A)[] keys) =>
        new Values([.. keys.Select(k => (Node)new Arr(new F(k.T), new Arr(new U(k.R), new U(k.G), new U(k.B), new U(k.A))))]);

    static (string, Node)[] Common(string name, float duration, bool looping = false) =>
        [("Name", new S(name)), ("Duration", new F(duration)), ("DurationEpsilon", new F(0.05f)), ("IsLooping", new B(looping)), ("FXLOD", new I(3)), ("Hide", new B(false)),
         ("Environment", new List()), ("FXCollisionDescriptions", new List())];

    /// <summary>
    /// A tracer-shaped system: one emitter holding a spray (one burst of one particle) whose particle is a streak beam
    /// (material <paramref name="streak"/>, USpeed 700, 3 cells in TileY) and a soft smoke beam; a one-at-random combo with
    /// a ring and a light; a decal, an audio event, a mesh, a streamer, and a description class the reader does not know.
    /// </summary>
    public static byte[] TracerSystem(string streak = "test_streak_00", string smoke = "test_smoke") => Document(new Struct("PSystem",
        ("Name", new S("")), ("BackgroundColor", new Vec(0.25f, 0.5f, 0.75f)), ("RefObject", new S("")), ("RefAnimation", new S("")),
        ("Emitters", new List(new Struct("FXEmitter",
            ("Name", new S("main")), ("Hide", new B(false)), ("Screenspace", new B(false)), ("ScreenspaceXPosition", new F(0f)), ("ScreenspaceYPosition", new F(0f)),
            ("LODDistanceFar", new F(40f)), ("RefNode", new S("fx_test")),
            ("FXDescriptions", new List(
                new Struct("FXSprayDescription", [.. Common("carrier", 0.1f),
                    ("BurstList", new List(new Struct("FXSprayBurst", ("NumParticles", new I(1)), ("BurstTime", new F(0f))))),
                    ("EmitFrameRate", Curve((0f, 0f))), ("EmitFrameDeviation", Curve((0f, 1.5f))), ("EmitFrameInitialSpeed", Curve((0f, 0f), (1f, 2f))),
                    ("InheritVelocity", new F(0.5f)), ("EmitterShape", new I(2)), ("Direction", new I(1)),
                    ("FXDescriptions", new List(
                        new Struct("FXBeamDescription", [.. Common("streak", 0.4f),
                            ("NumSegments", new I(1)), ("Billboard", new B(true)), ("MeterPerU", new F(30f)), ("USpeed", new F(700f)),
                            ("FrameLength", Curve((0.3f, 1f))), ("FrameWidth", Curve((0f, 0.2f), (1f, 0.1f))),
                            ("FrameColour", Colours((0f, 250, 240, 230, 255), (1f, 100, 90, 80, 0))),
                            ("MaterialName", new S(streak)), ("AdditiveBlend", new I(1)), ("SoftParticle", new I(1)), ("TileX", new F(1f)), ("TileY", new F(3f)), ("RandomTile", new B(true))]),
                        new Struct("FXBeamDescription", [.. Common("smoke", 0.25f),
                            ("NumSegments", new I(12)), ("Billboard", new B(true)), ("MeterPerU", new F(5f)), ("USpeed", new F(0f)),
                            ("FrameWidth", Curve((0f, 0.2f), (1f, 0.9f))), ("MaterialName", new S(smoke)), ("AdditiveBlend", new I(0))])))]),
                new Struct("FXComboDescription", [.. Common("one of", 0.2f),
                    ("OneAtRandom", new B(true)), ("FrameScale", Curve((0f, 2f))),
                    ("FXDescriptions", new List(
                        new Struct("FXRingDescription", [.. Common("puff", 0.3f),
                            ("Billboard", new B(true)), ("FrameRadius", Curve((0f, 0.1f), (0.5f, 0.3f), (1f, 0.2f))), ("EpsilonRadius", new F(0.4f)),
                            ("StartAngleZ", new F(90f)), ("UseStartAngleZ", new B(true)), ("MaterialName", new S(smoke)), ("TileX", new F(2f)), ("TileY", new F(2f))]),
                        new Struct("FXLightDescription", [.. Common("flash", 0.01f),
                            ("FrameColour", Colours((0f, 255, 200, 130, 255))), ("FrameIntensity", Curve((0f, 2f))), ("AttenStart", Curve((0f, 1.5f))),
                            ("AttenEnd", Curve((0f, 5f))), ("IsSpotLight", new B(false)), ("MaskName", new S(""))])))]),
                new Struct("FXScreenSpaceDecalDescription", [.. Common("mark", 4f),
                    ("DiffuseMap", new S("test_decal_dif")), ("NormalMap", new S("test_decal_nrm")), ("Width", new F(0.6f)), ("height", new F(0.6f)),
                    ("Depth", new F(2f)), ("NormalThreshold", new F(60f)), ("FrameColour", Colours((0f, 255, 255, 255, 255), (1f, 255, 255, 255, 0))), ("AdditiveBlend", new B(false))]),
                new Struct("FXAudioDescription", [.. Common("sound", 0f), ("OneOffAudioEventName", new S("play_test_event"))]),
                new Struct("FXMeshDescription", [.. Common("casing", 2f), ("Mesh", new S("test_casing")), ("PackageName", new S("test_fx")), ("Scale", Curve((0f, 1f)))]),
                new Struct("FXStreamerDescription", [.. Common("trail", 1f, looping: false),
                    ("Length", Curve((0f, 0.5f))), ("LengthAsSeconds", new B(true)), ("Speed", new F(3f)), ("Gravity", new F(-1f)),
                    ("Colour", Colours((0f, 10, 20, 30, 40))), ("FrameWidth", Curve((0f, 0.05f))), ("MaterialName", new S(smoke))]),
                new Struct("FXFutureDescription", [.. Common("mystery", 0.5f), ("Strange", new F(7f))])))))),
        ("SortOverride", new I(0)), ("GlobalSortOverride", new I(-1))));

    /// <summary>
    /// A "dropper" system (M41): a spray whose environment has a collision response (gravity 100) and whose only particle is a collider ring holding collision descriptions: a combo
    /// with a decal and a dust spray (its own ring), shaped like the install's ground landing effects (names and values invented).
    /// </summary>
    public static byte[] DropperSystem(string decal = "test_crater_dif", string dust = "test_smoke") => Document(new Struct("PSystem",
        ("Name", new S("")), ("BackgroundColor", new Vec(0f, 0f, 0f)), ("RefObject", new S("")), ("RefAnimation", new S("")),
        ("Emitters", new List(new Struct("FXEmitter",
            ("Name", new S("main")), ("Hide", new B(false)), ("Screenspace", new B(false)), ("ScreenspaceXPosition", new F(0f)), ("ScreenspaceYPosition", new F(0f)),
            ("LODDistanceFar", new F(40f)), ("RefNode", new S("")),
            ("FXDescriptions", new List(
                new Struct("FXSprayDescription", [.. Common("dropper", 0.1f).Where(f => f.Item1 is not ("Environment" or "FXCollisionDescriptions")),
                    ("Environment", new List(new Struct("FXSimpleEnvironment", ("Gravity", new F(100f)), ("CollisionResponse", new I(6))))),
                    ("BurstList", new List(new Struct("FXSprayBurst", ("NumParticles", new I(1)), ("BurstTime", new F(0f))))),
                    ("EmitFrameRate", Curve((0f, 0f))), ("EmitFrameDeviation", Curve((0f, 0f))), ("EmitFrameInitialSpeed", Curve((0f, 0f))),
                    ("FXDescriptions", new List(
                        new Struct("FXRingDescription", [.. Common("collider", 5f).Where(f => f.Item1 != "FXCollisionDescriptions"),
                            ("MaterialName", new S("test_collider")), ("FrameRadius", Curve((0f, 0.1f))),
                            ("FXCollisionDescriptions", new List(
                                new Struct("FXComboDescription", [.. Common("on impact", 0.1f),
                                    ("FXDescriptions", new List(
                                        new Struct("FXScreenSpaceDecalDescription", [.. Common("crater", 8f),
                                            ("DiffuseMap", new S(decal)), ("Width", new F(4f)), ("height", new F(4f)), ("Depth", new F(1f)), ("AdditiveBlend", new B(false))]),
                                        new Struct("FXRingDescription", [.. Common("dust", 2f), ("MaterialName", new S(dust)), ("FrameRadius", Curve((0f, 1f)))])))])))])))])))))),
        ("SortOverride", new I(0)), ("GlobalSortOverride", new I(-1))));

    /// <summary>
    /// A system shaped like the install's death effects (F-D1): a spray holding two rings and a light beside it, every element with a duration of 0 (they are played over the length of their
    /// <c>play_fx</c>) except the second ring, which has <paramref name="ringDuration"/> when given (a system with one element of its own duration).
    /// </summary>
    public static byte[] ZeroDurationSystem(float? ringDuration = null) => Document(new Struct("PSystem",
        ("Name", new S("")), ("BackgroundColor", new Vec(0f, 0f, 0f)), ("RefObject", new S("")), ("RefAnimation", new S("")),
        ("Emitters", new List(new Struct("FXEmitter",
            ("Name", new S("main")), ("Hide", new B(false)), ("Screenspace", new B(false)), ("ScreenspaceXPosition", new F(0f)), ("ScreenspaceYPosition", new F(0f)),
            ("LODDistanceFar", new F(40f)), ("RefNode", new S("")),
            ("FXDescriptions", new List(
                new Struct("FXSprayDescription", [.. Common("ash", 0f),
                    ("BurstList", new List(new Struct("FXSprayBurst", ("NumParticles", new I(2)), ("BurstTime", new F(0f))))),
                    ("EmitFrameRate", Curve((0f, 0f))), ("EmitFrameDeviation", Curve((0f, 1.5f))), ("EmitFrameInitialSpeed", Curve((0f, 1f))),
                    ("FXDescriptions", new List(
                        new Struct("FXRingDescription", [.. Common("flake", 0f), ("MaterialName", new S("test_ash")), ("FrameRadius", Curve((0f, 0.1f)))]),
                        new Struct("FXRingDescription", [.. Common("fleck", ringDuration ?? 0f), ("MaterialName", new S("test_ash")), ("FrameRadius", Curve((0f, 0.1f)))])))]),
                new Struct("FXLightDescription", [.. Common("flash", 0f), ("FrameColour", Colours((0f, 255, 200, 130, 255))), ("FrameIntensity", Curve((0f, 2f))),
                    ("AttenStart", Curve((0f, 1.5f))), ("AttenEnd", Curve((0f, 5f))), ("IsSpotLight", new B(false)), ("MaskName", new S(""))])))))),
        ("SortOverride", new I(0)), ("GlobalSortOverride", new I(-1))));

    /// <summary>
    /// Collision descriptions that are not well formed (M41): a ring whose list holds a number, an unknown description class that itself holds collision descriptions, and a chain of
    /// rings each inside the previous one's collision descriptions <paramref name="chain"/> deep (past <see cref="FxSystem.MaxDepth"/> they are dropped with a note).
    /// </summary>
    public static byte[] MalformedCollisionSystem(int chain = 3)
    {
        Node Plain(string name, float duration) => new Struct("FXRingDescription", [.. Common(name, duration).Where(f => f.Item1 != "FXCollisionDescriptions"),
            ("MaterialName", new S("test_smoke")), ("FrameRadius", Curve((0f, 1f)))]);
        Node Chain(int depth) => new Struct("FXRingDescription", [.. Common("link" + depth, 1f).Where(f => f.Item1 != "FXCollisionDescriptions"),
            ("MaterialName", new S("test_smoke")), ("FXCollisionDescriptions", new List(depth <= 1 ? Plain("end", 1f) : Chain(depth - 1)))]);
        return Document(new Struct("PSystem",
            ("Name", new S("")), ("BackgroundColor", new Vec(0f, 0f, 0f)), ("RefObject", new S("")), ("RefAnimation", new S("")),
            ("Emitters", new List(new Struct("FXEmitter",
                ("Name", new S("main")), ("Hide", new B(false)), ("Screenspace", new B(false)), ("ScreenspaceXPosition", new F(0f)), ("ScreenspaceYPosition", new F(0f)),
                ("LODDistanceFar", new F(40f)), ("RefNode", new S("")),
                ("FXDescriptions", new List(
                    new Struct("FXRingDescription", [.. Common("holder", 1f).Where(f => f.Item1 != "FXCollisionDescriptions"),
                        ("MaterialName", new S("test_smoke")),
                        ("FXCollisionDescriptions", new List(new I(5), new Struct("FXFutureDescription", [.. Common("mystery", 0.5f).Where(f => f.Item1 != "FXCollisionDescriptions"),
                            ("FXCollisionDescriptions", new List(Plain("inside the mystery", 3f)))])))]),
                    Chain(chain)))))),
            ("SortOverride", new I(0)), ("GlobalSortOverride", new I(-1))));
    }
}
