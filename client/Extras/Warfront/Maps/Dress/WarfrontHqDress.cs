using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmClient.Extras.Warfront.Dress;
using SmSim.Extras.Musou.Ashfall;
using SmSim.Extras.Warfront.Hq;
using static SmSim.Extras.Warfront.Hq.WarfrontHqMap;

namespace SmClient;

/// <summary>
/// The HQ exterior's drawing, in steps a frame apart (slice "dress", part 2; the fortress's stepping, <see cref="WarfrontFortressDress"/>): the kit look (terrain, masonry, the cover walls' kit barrier, relics and rubble), the Imperial wall, the rock, the road's
/// skin on the ramp and the courtyard's paving, the pieces (buttresses, crenellations, banners, wrecks, totems, lamp posts), the outside (ground, spire, stacks, wasteland ruins and smoke), the look and the storm. The keep, tunnels, cross passage and chamber
/// are not drawn here (<see cref="DressRule.NotDressed"/>): the kit look draws them in its own stone. Client only; no collision, no hash, no wire. Every size and colour is ours (placeholder demonstration).
/// </summary>
public static class WarfrontHqDress
{
    /// <summary>What the last <see cref="Steps"/> run built, one line for the log.</summary>
    public static string Report { get; private set; } = "";

    /// <summary>The drawing steps for the HQ under <paramref name="node"/> (game space, at the origin), after <paramref name="graybox"/> is in place; a step that fails is logged and the drawing goes on without it.</summary>
    public static List<(string Name, Action Run)> Steps(Node3D node, WarfrontHqBuild build, AshfallView graybox, DirectionalLight3D? sun, Godot.Environment? env, string? gameDir = null, Action<string, Action>? later = null)
    {
        var steps = new List<(string, Action)>();
        DressPlan? plan = null;
        int tris = 0;
        var level = build.Level;
        var dressRoot = new Node3D { Name = "Dress" };
        void Add(string name, Action run) => steps.Add((name, () =>
        {
            try { if (GodotObject.IsInstanceValid(node)) run(); }
            catch (Exception e) when (e is not (StackOverflowException or AccessViolationException or OutOfMemoryException)) { GD.Print($"warfront dress: HQ step \"{name}\" failed ({e.GetType().Name}: {e.Message}); the HQ is drawn without it"); }
        }));

        Add("plan", () => { plan = WarfrontHqDressPlan.Build(level); node.AddChild(dressRoot); });
        Add("kit", () =>
        {
            if (plan is null || !AshfallKitLibrary.Available) return;
            var view = new AshfallKitView(level, new AshfallDressing(plan.KitItems, plan.HiddenParts, WarfrontHqMap.Version), graybox);
            node.AddChild(view);
            view.SetKit(true);
        });
        bool greyHidden = false;
        foreach (var rule in Enum.GetValues<DressRule>().Where(r => WarfrontDressBake.ModeOf(r) >= 0 && r is DressRule.RockCliff or DressRule.ImperialWall or DressRule.Parapet or DressRule.ParapetStair))
        {
            var r = rule;
            Add("walls-" + r, () =>
            {
                if (plan is null) return;
                if (!greyHidden)
                {
                    greyHidden = true;
                    foreach (var child in graybox.GetChildren().OfType<Node3D>())   // a missing kit folder leaves the graybox: the dressed parts' grey boxes leave it
                        if (child.Name.ToString() is "Wall" or "Cover" or "Stair") child.Visible = false;
                }
                var corners = new List<System.Numerics.Vector3>();
                for (int i = 0; i < level.Parts.Count; i++) if (plan.Rules[i] == r) corners.AddRange(level.Parts[i].Corners);
                if (corners.Count == 0) return;
                tris += corners.Count / 3;
                dressRoot.AddChild(new MeshInstance3D { Name = "Walls" + r, Mesh = AshfallView.MeshOf([.. corners]), MaterialOverride = WarfrontDressBake.Plated(WarfrontDressBake.ModeOf(r)), CastShadow = GeometryInstance3D.ShadowCastingSetting.On });
            });
        }
        Add("surfaces", () =>
        {
            // the road's skin over the ramp (a plane at the ramp's own slope, 0.1 m over it: broken asphalt, a worn centre line) and over the gate's passage, and the courtyard's flagstones (a hand over the plateau)
            const float slope = RampRise / (RampNorth - RampSouth), lift = 0.1f, rw = 12f;
            var road = new WarfrontDressBake.Soup();
            WarfrontDressBake.Quad(road, new(-rw, RampSouth, lift), new(rw, RampSouth, lift), new(rw, RampNorth, RampRise + lift), new(-rw, RampNorth, RampRise + lift));
            WarfrontDressBake.Quad(road, new(-GateWidth / 2f, WallSouth, PlateauZ + lift), new(GateWidth / 2f, WallSouth, PlateauZ + lift), new(GateWidth / 2f, WallNorth, PlateauZ + lift), new(-GateWidth / 2f, WallNorth, PlateauZ + lift));
            var paving = new WarfrontDressBake.Soup();
            WarfrontDressBake.Quad(paving, new(-CurtainX + 0.5f, WallNorth + 0.1f, PlateauZ + 0.06f), new(CurtainX - 0.5f, WallNorth + 0.1f, PlateauZ + 0.06f), new(CurtainX - 0.5f, KeepSouth - 0.1f, PlateauZ + 0.06f), new(-CurtainX + 0.5f, KeepSouth - 0.1f, PlateauZ + 0.06f));
            tris += road.Triangles + paving.Triangles;
            dressRoot.AddChild(new MeshInstance3D { Name = "RoadSkin", Mesh = road.ToMesh(), MaterialOverride = WarfrontDressBake.Plated(10), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
            dressRoot.AddChild(new MeshInstance3D { Name = "CourtyardPaving", Mesh = paving.ToMesh(), MaterialOverride = WarfrontDressBake.Plated(9), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        });
        Dictionary<string, WarfrontDressBake.Soup>? soups = null, outside = null;
        Add("bake", () => { if (plan is null) return; soups = WarfrontDressBake.Soups(plan.Pieces); outside = WarfrontDressBake.Soups(WarfrontHqDressPlan.Outside()); });
        foreach (var group in WarfrontDressBake.GroupOrder)
        {
            string g = group;
            Add("pieces-" + g, () => { if (soups is not null) tris += WarfrontDressBake.AttachGroup(dressRoot, soups, g, "Pieces", shadows: true); });
        }
        Add("outside-ground", () =>
            dressRoot.AddChild(new MeshInstance3D { Name = "OutsideGround", Mesh = WarfrontDressBake.OutsideGround(HalfWidth, SouthEdge, NorthEdge, WarfrontHqDressPlan.OutsideHeight, ref tris), MaterialOverride = new StandardMaterial3D { VertexColorUseAsAlbedo = true, Roughness = 1f, CullMode = BaseMaterial3D.CullModeEnum.Disabled }, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off }));
        Add("outside-scenery", () => { if (outside is null) return; foreach (var g in new[] { "Stone", "Iron", "Glow", "Smoke" }) tris += WarfrontDressBake.AttachGroup(dressRoot, outside, g, "Outside", shadows: false); });
        // the game's own props, read from the install a model a frame: the read starts here, the polls follow (each a frame later, queued through <paramref name="later"/> while models are still coming), then they are placed
        var props = new WarfrontDressProps();
        int polls = 0;
        Action pollStep = () => { };
        pollStep = () =>
        {
            try
            {
                if (plan is null || !GodotObject.IsInstanceValid(node)) return;
                if (!props.Poll() && polls++ < 200 && later is not null) { later("props-poll", pollStep); return; }
                tris += props.Place(dressRoot, plan);
            }
            catch (Exception e) when (e is not (StackOverflowException or AccessViolationException or OutOfMemoryException)) { GD.Print($"warfront dress: install props failed ({e.GetType().Name}: {e.Message}); the drawing goes on without them"); }
        };
        Add("props-read", () => { if (plan is not null) props.Start(gameDir ?? WarfrontDressProps.GameDir(), plan); });
        Add("props-poll", pollStep);
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
            try { AshfallStorm.Attach(host, level, new AshfallDressing(plan?.KitItems ?? [], plan?.HiddenParts ?? [], WarfrontHqMap.Version), sun, env); }
            catch (Exception e) when (e is not (StackOverflowException or AccessViolationException or OutOfMemoryException)) { GD.Print($"warfront dress: the storm look failed ({e.GetType().Name}: {e.Message}); the plain look stays"); }
        });
        return steps;
    }
}
