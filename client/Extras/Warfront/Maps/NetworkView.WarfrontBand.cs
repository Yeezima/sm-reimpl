using Godot;
using SmContent;
using SmSim;
using SmContent.Extras.Musou;
using SmContent.Extras.Warfront;
using SmContent.Extras.Warfront.Maps;
using SmSim.Extras.Musou.Ashfall;
using SmSim.Extras.Warfront.Maps;

namespace SmClient;

/// <summary>
/// A Warfront lane slot on the client (slice W2; plan section 2): one band, hub plus ONE battlefield. The client builds the slot's level from (profile, seed) through <see cref="WarfrontLayoutCache"/> (the host's generator, the same hash),
/// predicts on its collision as the server does, and draws it the way the front pilot draws a band: the avenue's openings in the boundary wall, the kit look, the storm look and the pilot's outside scenery
/// (<see cref="AshfallFrontScenery"/>: the 10-degree outside ground, the avenue running on, manufactorum stacks, the far cathedral spire). The neighbouring bands' massing copies are the same lane's adjacent slots, built or read through the layout cache. Client only: nothing
/// here changes the collision, the hash or the wire. The map-name hooks (<see cref="WarfrontMapContent"/>, <see cref="WarfrontMapVisuals"/>, <see cref="WarfrontBandFrame"/>) are called from the stage flow (W5); no Musou file is changed here.
/// </summary>
public partial class NetworkView
{
    AshfallLevel? _warfrontLevel;
    AshfallDressing? _warfrontDressing;
    Node3D? _warfrontBand;
    bool _warfrontShown = true;

    // The hooks W5 calls from the dispatchers (client/Extras/NetworkView.Extras.cs, Musou/Tfw/NetworkView.Tfw.cs): declared here as partial methods of this class, implemented below, so the
    // file compiles without callers and an unused hook costs nothing; a build without Warfront (-p:SmWarfront=false) leaves this folder out and nothing refers to it.
    partial void WarfrontMapContent(string name, ref MapContent? map);
    partial void WarfrontMapVisuals(string name, ref bool drawn);
    partial void WarfrontBandFrame(PlayerState me);

    /// <summary>The map content of a lane slot's name (null for any other map): the generated band as collision, from the layout cache.</summary>
    partial void WarfrontMapContent(string name, ref MapContent? map)
    {
        if (name == WarfrontMaps.HqName) { WarfrontHqContent(ref map); return; }   // W13b (WarfrontHqClient.cs)
        if (WarfrontSlots.ByName(name) is not { } slot) return;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        if (NoAshfallCache) AshfallLayoutCache.Disabled = true;
        SetLoadingCaption("READING THE WARFRONT BATTLEFIELD");
        (_warfrontLevel, _warfrontDressing) = WarfrontLayoutCache.Build(slot, out string note);
        map = AshfallContent.ToMapContent(_warfrontLevel);
        GD.Print($"warfront: {note}; content in {watch.ElapsedMilliseconds} ms, hash {_warfrontLevel.Hash:X16}; nothing read from the install");
    }

    /// <summary>Draws a lane slot's band (graybox, kit look, storm, outside scenery) under one node at the origin; any failure leaves the grey collision.</summary>
    partial void WarfrontMapVisuals(string name, ref bool drawn)
    {
        if (name == WarfrontMaps.HqName) { WarfrontHqVisuals(ref drawn); return; }   // W13b (WarfrontHqClient.cs)
        if (WarfrontSlots.ByName(name) is not { } slot || _warfrontLevel is null || _warfrontDressing is null || _gameSpace is null) return;
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var node = new Node3D { Name = $"WarfrontBand{slot.Band}" };
            _gameSpace.AddChild(node);
            _warfrontBand = node;
            var drawn1 = AshfallMap.OpenFrontEdges(_warfrontLevel);   // drawing only: the avenue's openings in the boundary wall (the collision stays closed)
            var view = AshfallView.Build(drawn1);
            node.AddChild(view);
            TfwFrontKit(drawn1, _warfrontDressing, view, node);
            // the scenery draws the neighbouring bands as massing copies: the same lane's adjacent slots (node - 1 south, node + 1 north), read through the layout cache; node 3 has none to the north (the spire and the stacks stand there)
            var empty = new AshfallDressing([], [], _warfrontDressing.Version);
            var bands = new (AshfallLevel Level, AshfallDressing Dressing)[AshfallFront.BandCount];
            for (int k = 0; k < bands.Length; k++) bands[k] = (_warfrontLevel, empty);
            bands[slot.Band] = (_warfrontLevel, _warfrontDressing);
            foreach (int k in new[] { slot.Band - 1, slot.Band + 1 })
            {
                if (k < 0 || k >= AshfallFront.BandCount) continue;
                try { bands[k] = WarfrontLayoutCache.Build(WarfrontSlots.Of(slot.Lane, k + 1), out _); }
                catch (System.Exception e) when (e is not (System.StackOverflowException or System.AccessViolationException or System.OutOfMemoryException)) { GD.Print($"warfront: the neighbouring band {k} could not be built ({e.GetType().Name}: {e.Message}); no copy of it is drawn"); }
            }
            node.AddChild(AshfallFrontScenery.Build(slot.Band, bands));
            if (_sun is not null && _environment is not null)
            {
                AshfallView.ApplyLook(_sun, _environment);
                if (!AshfallStorm.Off)
                {
                    var host = new Node3D { Name = "WarfrontStorm" };
                    node.AddChild(host);
                    try { AshfallStorm.Attach(host, _warfrontLevel, _warfrontDressing, _sun, _environment); }
                    catch (System.Exception e) when (e is not (System.StackOverflowException or System.AccessViolationException or System.OutOfMemoryException)) { GD.Print($"warfront: the storm look failed ({e.GetType().Name}: {e.Message}); the plain look stays"); }
                }
            }
            drawn = true;
            GD.Print($"warfront: {slot.Name} ({slot.Profile}, seed {slot.Seed}) drawn in {watch.ElapsedMilliseconds} ms (outside scenery {AshfallFrontScenery.LastTriangles:N0} triangles, client only)");
        }
        catch (System.Exception e) when (e is not (System.StackOverflowException or System.AccessViolationException))
        {
            GD.Print($"warfront: the band view failed ({e.GetType().Name}: {e.Message}); the grey collision is drawn instead");
        }
    }

    /// <summary>
    /// Once a frame on a lane slot: the band is drawn only while the player (and the camera) stand in its square, as the front pilot does (<see cref="FrontBandShown"/>): its outside ground reaches 1.2 km and would bury the hub (beyond the
    /// level's south edge) and the range.
    /// </summary>
    partial void WarfrontBandFrame(PlayerState me)
    {
        if (_warfrontBand is null || !IsInstanceValid(_warfrontBand)) return;
        int shown = FrontBandShown(_client?.Predicted.Position ?? me.Position, _camera is null ? null : new System.Numerics.Vector3(_camera.GlobalPosition.X, -_camera.GlobalPosition.Z, _camera.GlobalPosition.Y));
        bool show = shown >= 0;
        if (show == _warfrontShown) return;
        _warfrontBand.Visible = show;
        _warfrontShown = show;
        GD.Print(show ? "warfront: band shown" : "warfront: band hidden (the Stormbird, the Firing Range or the flight)");
    }
}
