using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmClient.Extras.Warfront.Dress;
using SmSim.Extras.Musou.Ashfall;
using SmSim.Extras.Warfront.Fortress;
using static SmSim.Extras.Warfront.Fortress.WarfrontFortressMap;

namespace SmClient;

/// <summary>
/// The Ork fortress's drawing, in steps a frame apart (slice "dress", part 1; the way W5d draws the lane bands): the kit look (the bands' own terrain and masonry, the kit's rubble and relics), the plated walls, the pieces (spikes, towers,
/// boards, totems, banners, the dais), the outside (ground beyond the walls, the spire behind the arena, the stacks), the look and the storm. <see cref="Steps"/> is used by the client's fortress view and by the self-view scene, so both draw the same.
/// Client only: no collision, no hash, no wire. Every size and colour is ours (placeholder demonstration, refine register).
/// </summary>
public static class WarfrontFortressDress
{
    /// <summary>Test aid <c>--dress off</c> (before/after stills): the drawing of the W14b slice, graybox only, no storm (shared with <see cref="WarfrontFarMassing.Off"/>).</summary>
    public static bool Off => WarfrontFarMassing.Off;

    /// <summary>What the last <see cref="Steps"/> run built, one line for the log.</summary>
    public static string Report { get; private set; } = "";

    /// <summary>
    /// The drawing steps for the fortress under <paramref name="node"/> (game space, at the origin), after <paramref name="graybox"/> (the grey drawing, already attached) is in place. Each step is small enough to run alone in a frame;
    /// a step that fails is logged and the drawing goes on without it.
    /// </summary>
    public static List<(string Name, Action Run)> Steps(Node3D node, WarfrontFortressBuild build, AshfallView graybox, DirectionalLight3D? sun, Godot.Environment? env)
    {
        var steps = new List<(string, Action)>();
        DressPlan? plan = null;
        int tris = 0;
        var level = build.Level;
        var dressRoot = new Node3D { Name = "Dress" };
        void Add(string name, Action run) => steps.Add((name, () =>
        {
            try { if (GodotObject.IsInstanceValid(node)) run(); }
            catch (Exception e) when (e is not (StackOverflowException or AccessViolationException or OutOfMemoryException)) { GD.Print($"warfront dress: step \"{name}\" failed ({e.GetType().Name}: {e.Message}); the fortress is drawn without it"); }
        }));

        Add("plan", () =>
        {
            plan = WarfrontFortressDressPlan.Build(level, build.Posts);
            node.AddChild(dressRoot);
        });
        Add("kit", () =>
        {
            if (plan is null || !AshfallKitLibrary.Available) return;
            var dressing = new AshfallDressing(plan.KitItems, plan.HiddenParts, WarfrontFortressMap.Version);
            var view = new AshfallKitView(level, dressing, graybox);
            node.AddChild(view);
            view.SetKit(true);   // the kit's terrain, masonry and its own rubble and relics; the plated parts are drawn below
        });
        bool greyHidden = false;
        foreach (var rule in Enum.GetValues<DressRule>().Where(r => WarfrontDressBake.ModeOf(r) >= 0))
        {
            var r = rule;   // one wall look a frame: each is its own small shader, and a shader's first draw can cost a compile
            Add("walls-" + r, () =>
            {
                if (plan is null) return;
                if (!greyHidden)
                {
                    greyHidden = true;
                    foreach (var child in graybox.GetChildren().OfType<Node3D>())   // the grey boxes of the plated parts leave (the kit look hides the whole graybox; this covers a kit folder that is missing)
                        if (child.Name.ToString() is "Wall" or "Stronghold" or "Cover") child.Visible = false;
                }
                var corners = new List<System.Numerics.Vector3>();
                for (int i = 0; i < level.Parts.Count; i++) if (plan.Rules[i] == r && plan.HiddenParts.Contains(i)) corners.AddRange(level.Parts[i].Corners);
                if (corners.Count == 0) return;
                tris += corners.Count / 3;
                dressRoot.AddChild(new MeshInstance3D { Name = "Walls" + r, Mesh = AshfallView.MeshOf([.. corners]), MaterialOverride = WarfrontDressBake.Plated(WarfrontDressBake.ModeOf(r)), CastShadow = GeometryInstance3D.ShadowCastingSetting.On });
            });
        }
        Dictionary<string, WarfrontDressBake.Soup>? soups = null, outside = null;
        Add("bake", () => { if (plan is null) return; soups = WarfrontDressBake.Soups(plan.Pieces); outside = WarfrontDressBake.Soups(WarfrontFortressDressPlan.Outside()); });
        foreach (var group in WarfrontDressBake.GroupOrder)   // one material a frame: a first draw can cost a pipeline compile
        {
            string g = group;
            Add("pieces-" + g, () => { if (soups is not null) tris += WarfrontDressBake.AttachGroup(dressRoot, soups, g, "Pieces", shadows: true); });
        }
        Add("outside-ground", () =>
            dressRoot.AddChild(new MeshInstance3D { Name = "OutsideGround", Mesh = OutsideGround(ref tris), MaterialOverride = new StandardMaterial3D { VertexColorUseAsAlbedo = true, Roughness = 1f, CullMode = BaseMaterial3D.CullModeEnum.Disabled }, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off }));
        Add("outside-scenery", () => { if (outside is null) return; foreach (var g in new[] { "Stone", "Iron", "Glow" }) tris += WarfrontDressBake.AttachGroup(dressRoot, outside, g, "Outside", shadows: false); });
        Add("look", () =>
        {
            if (sun is not null && env is not null) AshfallView.ApplyLook(sun, env);
            Report = $"{plan?.Pieces.Count ?? 0} pieces, {plan?.KitItems.Count ?? 0} kit items, {tris:N0} triangles of dressing (client only)";
        });
        Add("storm", () =>
        {
            if (sun is null || env is null || AshfallStorm.Off) return;
            var host = new Node3D { Name = "WarfrontStorm" };
            node.AddChild(host);
            try { AshfallStorm.Attach(host, level, new AshfallDressing(plan?.KitItems ?? [], plan?.HiddenParts ?? [], WarfrontFortressMap.Version), sun, env); }
            catch (Exception e) when (e is not (StackOverflowException or AccessViolationException or OutOfMemoryException)) { GD.Print($"warfront dress: the storm look failed ({e.GetType().Name}: {e.Message}); the plain look stays"); }
        });
        return steps;
    }

    /// <summary>The ground beyond the walls (<see cref="WarfrontFortressDressPlan.OutsideHeight"/>): a grid of quads round the map's box, toned per quad, so no edge of the world shows from the parapets, the plaza or the spire's side.</summary>
    static ArrayMesh OutsideGround(ref int tris)
    {
        float[] xs = [-1400f, -1000f, -700f, -500f, -350f, -250f, -HalfWidth, HalfWidth, 250f, 350f, 500f, 700f, 1000f, 1400f];
        float[] ys = [-1400f, -1000f, -700f, -500f, -380f, -300f, SouthEdge, NorthEdge, 250f, 350f, 500f, 700f, 1000f, 1400f];
        var soup = new WarfrontDressBake.Soup();
        float H(float x, float y) => WarfrontFortressDressPlan.OutsideHeight(x, y);
        for (int i = 0; i + 1 < xs.Length; i++)
            for (int j = 0; j + 1 < ys.Length; j++)
            {
                float x0 = xs[i], x1 = xs[i + 1], y0 = ys[j], y1 = ys[j + 1];
                if (x0 >= -HalfWidth && x1 <= HalfWidth && y0 >= SouthEdge && y1 <= NorthEdge) continue;   // the map's own box
                float tone = 0.88f + 0.24f * DressHash.Unit(91, (uint)(i * 31 + j));
                var col = new Color(0.47f * tone, 0.44f * tone, 0.41f * tone);
                var a = new Vector3(x0, y0, H(x0, y0)); var b = new Vector3(x1, y0, H(x1, y0)); var c = new Vector3(x1, y1, H(x1, y1)); var d = new Vector3(x0, y1, H(x0, y1));
                var below = (a + b + c + d) / 4f - Vector3.Back * 5f;
                soup.Tri(a, b, c, below, col); soup.Tri(a, c, d, below, col);
            }
        tris += soup.Triangles;
        return soup.ToMesh();
    }
}
