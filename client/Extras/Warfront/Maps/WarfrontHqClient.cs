using Godot;
using SmContent;
using SmContent.Extras.Musou;
using SmContent.Extras.Warfront;
using SmSim.Extras.Musou.Ashfall;
using SmSim.Extras.Warfront.Hq;

namespace SmClient;

/// <summary>
/// The HQ defence map on the client (slice W13b): the level the server builds (<see cref="WarfrontHqLayout.Build"/>, deterministic, the same hash) as collision, drawn as the graybox of <see cref="AshfallView"/> with the map's look
/// (a dressing slice follows W13b). The hub is composed on it by the same path as a lane slot's. Called from <see cref="WarfrontMapContent"/> and <see cref="WarfrontMapVisuals"/> (NetworkView.WarfrontBand.cs).
/// </summary>
public partial class NetworkView
{
    /// <summary>The map content of <c>wf_hq</c>: the generated level as collision.</summary>
    void WarfrontHqContent(ref MapContent? map)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        SetLoadingCaption("BUILDING THE HQ DEFENCE");
        var level = WarfrontHqLayout.Build.Level;
        map = AshfallContent.ToMapContent(level);
        GD.Print($"warfront: the HQ defence built in {watch.ElapsedMilliseconds} ms, level hash {level.Hash:X16}; nothing read from the install");
    }

    /// <summary>Draws the HQ level (graybox) under one node at the origin; any failure leaves the grey collision.</summary>
    void WarfrontHqVisuals(ref bool drawn)
    {
        if (_gameSpace is null) return;
        try
        {
            var node = new Node3D { Name = "WarfrontHq" };
            _gameSpace.AddChild(node);
            node.AddChild(AshfallView.Build(WarfrontHqLayout.Build.Level));
            if (_sun is not null && _environment is not null) AshfallView.ApplyLook(_sun, _environment);
            drawn = true;
            GD.Print("warfront: the HQ defence drawn (graybox)");
        }
        catch (System.Exception e) when (e is not (System.StackOverflowException or System.AccessViolationException))
        {
            GD.Print($"warfront: the HQ view failed ({e.GetType().Name}: {e.Message}); the grey collision is drawn instead");
        }
    }
}
