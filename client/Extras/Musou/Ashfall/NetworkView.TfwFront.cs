using Godot;
using SmSim;
using SmSim.Extras.Musou.Ashfall;

namespace SmClient;

/// <summary>
/// The Musou front pilot on the client (<c>tfw_war_front</c>; docs/musou-ashfall-graybox.md "Front pilot", F3). The collision the client predicts with is the composed world (three bands side by side, as on the server); the drawing is
/// per band: the composed level split back into its bands (<see cref="AshfallMap.SplitFront"/>), each drawn in its own frame under a node at its offset (graybox, kit look, outside scenery), and only the band the player stands in
/// is shown (<see cref="TfwFrontFrame"/>). The storm look is built once and follows the active band. Client only: nothing here changes the collision, the hash or the wire.
/// </summary>
public partial class NetworkView
{
    Node3D[]? _frontBands;
    Node3D? _frontStorm;
    int _frontActive = -1, _frontStormBand = -1;
    System.Collections.Generic.IReadOnlyList<(AshfallLevel Level, AshfallDressing Dressing)>? _frontSplit;

    /// <summary>
    /// Test aid (captures and free-fly shots of the front pilot): <c>--front-band n</c> draws band n (0 the wasteland, 1 the outskirts, 2 the urban core) whatever the player or the camera does, so a camera at x = 600 or 1200 sees that
    /// band. -1 (no switch, or a value out of range): the band the player stands in, as in play.
    /// </summary>
    static readonly int FrontForcedBand = ForcedFrontBand();

    static int ForcedFrontBand()
    {
        var args = OS.GetCmdlineUserArgs();
        int i = System.Array.IndexOf(args, "--front-band");
        return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out int b) && b >= 0 && b < AshfallFront.BandCount ? b : -1;
    }

    /// <summary>Draws the front's bands (called by <see cref="TfwAshfallVisuals"/> for the composed world); band 0 shown until the player stands in another.</summary>
    void TfwFrontVisuals(AshfallLevel level, AshfallDressing dressing)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var bands = _frontSplit = AshfallMap.SplitFront(level, dressing);
        _frontBands = new Node3D[bands.Count];
        int sceneryTris = 0;
        for (int k = 0; k < bands.Count; k++)
        {
            var node = new Node3D { Name = $"AshfallBand{k}", Position = new Vector3(AshfallFront.OffsetOf(k), 0f, 0f), Visible = k == System.Math.Max(0, FrontForcedBand) };
            _gameSpace!.AddChild(node);
            _frontBands[k] = node;
            var drawn = AshfallMap.OpenFrontEdges(bands[k].Level);   // drawing only: the avenue's openings in the boundary wall (the collision stays closed)
            var view = AshfallView.Build(drawn);
            node.AddChild(view);
            TfwFrontKit(drawn, bands[k].Dressing, view, node);
            var scenery = AshfallFrontScenery.Build(k, bands);
            sceneryTris += AshfallFrontScenery.LastTriangles;
            node.AddChild(scenery);
        }
        if (_sun is not null && _environment is not null) AshfallView.ApplyLook(_sun, _environment);
        _frontActive = System.Math.Max(0, FrontForcedBand);
        TfwFrontStorm(_frontActive);
        if (FrontForcedBand >= 0) GD.Print($"ashfall front: --front-band {FrontForcedBand}: band {FrontForcedBand} ({AshfallBandProfile.Front[FrontForcedBand].Name}) is drawn whatever the camera does (test aid)");
        GD.Print($"ashfall front: {bands.Count} bands drawn in {watch.ElapsedMilliseconds} ms (each in its own frame at x + {AshfallFront.BandSpacing:0} m a band; outside scenery {sceneryTris:N0} triangles a band, client only); band {_frontActive} ({AshfallBandProfile.Front[_frontActive].Name}) shown");
    }

    /// <summary>The kit look of one band under its node (as <see cref="TfwAshfallKit"/> does for a single level); any failure leaves that band's graybox.</summary>
    void TfwFrontKit(AshfallLevel level, AshfallDressing dressing, AshfallView graybox, Node3D parent)
    {
        try
        {
            if (!AshfallKitLibrary.Available) return;
            var args = OS.GetCmdlineUserArgs();
            int i = System.Array.IndexOf(args, "--ashfall-look");
            bool kit = !(i >= 0 && i + 1 < args.Length && args[i + 1] == "graybox");
            var view = new AshfallKitView(level, dressing, graybox);
            parent.AddChild(view);
            view.SetKit(kit);
        }
        catch (System.Exception e) when (e is not (System.StackOverflowException or System.AccessViolationException or System.OutOfMemoryException))
        {
            GD.Print($"ashfall front kit: the kit look failed ({e.GetType().Name}: {e.Message}); the band's graybox stays");
        }
    }

    /// <summary>
    /// Once a frame: the band the player stands in is the one drawn (inside its square and boundary). Anywhere else (the Stormbird, the Firing Range, the flight between) and whenever the camera is aboard the Stormbird, no band
    /// is drawn: a band's outside ground and its neighbours' copies reach 1.2 km and would bury the hub (south of the composed world) and the range (east of it). Review of 2026-11-16.
    /// </summary>
    void TfwFrontFrame(PlayerState me)
    {
        if (_frontBands is null) return;
        int band = FrontForcedBand >= 0 ? FrontForcedBand : FrontBandShown(_client?.Predicted.Position ?? me.Position, _camera is null ? null : new System.Numerics.Vector3(_camera.GlobalPosition.X, -_camera.GlobalPosition.Z, _camera.GlobalPosition.Y));
        if (band == _frontActive) return;
        for (int k = 0; k < _frontBands.Length; k++) if (IsInstanceValid(_frontBands[k])) _frontBands[k].Visible = k == band;
        if (band >= 0) TfwFrontStorm(band);   // a band hidden takes its storm with it (the storm lives under the band's node)
        _frontActive = band;
        GD.Print(band >= 0 ? $"ashfall front: band {band} ({AshfallBandProfile.Front[band].Name}) shown" : "ashfall front: no band shown (outside every band: the Stormbird, the Firing Range or the flight)");
    }

    /// <summary>The band to draw for a player at <paramref name="at"/> with the camera at <paramref name="camera"/> (game space): <see cref="AshfallFront.BandShown"/>.</summary>
    internal int FrontBandShown(System.Numerics.Vector3 at, System.Numerics.Vector3? camera) =>
        AshfallFront.BandShown(at, camera is { } cam && TfwHub is { } hub && hub.Contains(cam));

    /// <summary>
    /// The storm look on band <paramref name="band"/> (re-review of 2026-11-16): its fires, smoke and lane marks are placed from a level's own spots, so the storm is built from the shown band's level and dressing, under that band's node
    /// (its light pass, tints and grime reach that band's kit look). On a switch the old one leaves the tree first (it puts back what it wrote) and the new one is built; nothing on the first band again until shown.
    /// </summary>
    void TfwFrontStorm(int band)
    {
        if (band == _frontStormBand || _frontSplit is null || _frontBands is null || _sun is null || _environment is null || AshfallStorm.Off) return;
        if (_frontStorm is not null && IsInstanceValid(_frontStorm))
        {
            _frontStorm.GetParent()?.RemoveChild(_frontStorm);   // at once: its _ExitTree restores the environment before the new storm writes it
            _frontStorm.QueueFree();
        }
        _frontStorm = null;
        _frontStormBand = band;
        var host = new Node3D { Name = "AshfallFrontStorm" };
        _frontBands[band].AddChild(host);
        _frontStorm = host;
        try { AshfallStorm.Attach(host, _frontSplit[band].Level, _frontSplit[band].Dressing, _sun, _environment); }
        catch (System.Exception e) when (e is not (System.StackOverflowException or System.AccessViolationException or System.OutOfMemoryException)) { GD.Print($"ashfall front: the storm look failed ({e.GetType().Name}: {e.Message})"); }
    }
}
