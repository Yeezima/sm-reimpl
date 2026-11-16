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
    int _frontActive = -1;

    /// <summary>Draws the front's bands (called by <see cref="TfwAshfallVisuals"/> for the composed world); band 0 shown until the player stands in another.</summary>
    void TfwFrontVisuals(AshfallLevel level, AshfallDressing dressing)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var bands = AshfallMap.SplitFront(level, dressing);
        _frontBands = new Node3D[bands.Count];
        int sceneryTris = 0;
        for (int k = 0; k < bands.Count; k++)
        {
            var node = new Node3D { Name = $"AshfallBand{k}", Position = new Vector3(AshfallFront.OffsetOf(k), 0f, 0f), Visible = k == 0 };
            _gameSpace!.AddChild(node);
            _frontBands[k] = node;
            var view = AshfallView.Build(bands[k].Level);
            node.AddChild(view);
            TfwFrontKit(bands[k].Level, bands[k].Dressing, view, node);
            var scenery = AshfallFrontScenery.Build(k, bands);
            sceneryTris += AshfallFrontScenery.LastTriangles;
            node.AddChild(scenery);
        }
        if (_sun is not null && _environment is not null)
        {
            AshfallView.ApplyLook(_sun, _environment);
            if (!AshfallStorm.Off)
            {
                _frontStorm = new Node3D { Name = "AshfallFrontStorm" };
                _gameSpace!.AddChild(_frontStorm);
                try { AshfallStorm.Attach(_frontStorm, bands[0].Level, bands[0].Dressing, _sun, _environment); }
                catch (System.Exception e) when (e is not (System.StackOverflowException or System.AccessViolationException or System.OutOfMemoryException)) { GD.Print($"ashfall front: the storm look failed ({e.GetType().Name}: {e.Message})"); }
            }
        }
        _frontActive = 0;
        GD.Print($"ashfall front: {bands.Count} bands drawn in {watch.ElapsedMilliseconds} ms (each in its own frame at x + {AshfallFront.BandSpacing:0} m a band; outside scenery {sceneryTris:N0} triangles a band, client only); band 0 ({AshfallBandProfile.Front[0].Name}) shown");
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

    /// <summary>Once a frame: the band the player stands in is the one drawn (inside its square and boundary; anywhere else, in the Stormbird or the Firing Range, the last band stays).</summary>
    void TfwFrontFrame(PlayerState me)
    {
        if (_frontBands is null) return;
        var at = _client?.Predicted.Position ?? me.Position;
        int band = AshfallFront.BandAt(at.X);
        if (System.MathF.Abs(at.X - AshfallFront.OffsetOf(band)) > AshfallMap.Half + 20f || System.MathF.Abs(at.Y) > AshfallMap.Half + 20f || band == _frontActive) return;
        for (int k = 0; k < _frontBands.Length; k++) if (IsInstanceValid(_frontBands[k])) _frontBands[k].Visible = k == band;
        if (_frontStorm is not null && IsInstanceValid(_frontStorm)) _frontStorm.Position = new Vector3(AshfallFront.OffsetOf(band), 0f, 0f);
        _frontActive = band;
        GD.Print($"ashfall front: band {band} ({AshfallBandProfile.Front[band].Name}) shown");
    }
}
