using Godot;
using SmContent;
using SmContent.Extras.Musou;
using SmSim.Extras.Musou.Ashfall;
using SmSim.Extras.Warfront.Fortress;
using SmSim.Extras.Warfront.Maps;

namespace SmClient;

/// <summary>
/// The Ork fortress on the client (slice W14b): the level the server builds (<see cref="WarfrontSlots.Fortress"/>, deterministic, the same collision and hash) as collision, drawn as the graybox of <see cref="AshfallView"/> (a dressing slice
/// follows). A handmade arena that is not the generated one has no drawing here: its collision is shown grey until its own client view exists. Called from <see cref="WarfrontMapContent"/> and <see cref="WarfrontMapVisuals"/> (NetworkView.WarfrontBand.cs).
/// </summary>
public partial class NetworkView
{
    /// <summary>The map content of <c>wf_fortress</c>: the generated level as collision.</summary>
    void WarfrontFortressContent(ref MapContent? map)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        SetLoadingCaption("BUILDING THE ORK FORTRESS");
        if (WarfrontSlots.Fortress is not WarfrontFortressBuild build) return;
        map = AshfallContent.ToMapContent(build.Level);
        GD.Print($"warfront: the Ork fortress built in {watch.ElapsedMilliseconds} ms, level hash {build.Level.Hash:X16}; nothing read from the install");
    }

    /// <summary>Draws the fortress level (graybox) under one node at the origin; any failure leaves the grey collision.</summary>
    void WarfrontFortressVisuals(ref bool drawn)
    {
        if (_gameSpace is null || WarfrontSlots.Fortress is not WarfrontFortressBuild build) return;
        try
        {
            var node = new Node3D { Name = "WarfrontFortress" };
            _gameSpace.AddChild(node);
            node.AddChild(AshfallView.Build(build.Level));
            if (_sun is not null && _environment is not null) AshfallView.ApplyLook(_sun, _environment);
            drawn = true;
            GD.Print("warfront: the Ork fortress drawn (graybox)");
        }
        catch (System.Exception e) when (e is not (System.StackOverflowException or System.AccessViolationException))
        {
            GD.Print($"warfront: the fortress view failed ({e.GetType().Name}: {e.Message}); the grey collision is drawn instead");
        }
    }
}
