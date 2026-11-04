using Godot;
using SmContent;
using SmContent.Extras.Musou;
using SmSim.Extras.Musou.Ashfall;
using SmSim.Extras.Musou.Campaign;

namespace SmClient;

/// <summary>
/// Ashfall Citadel on the client (G1, docs/musou-ashfall-graybox.md): the core's <c>LoadMap</c> asks <see cref="TfwAshfallContent"/> for the map's content (generated, nothing is read from the install)
/// and <see cref="TfwAshfallVisuals"/> to draw it (<see cref="AshfallView"/>) instead of loading a map scene. The collision the client predicts with is the generated level plus the Stormbird, as on the server.
/// </summary>
public partial class NetworkView
{
    AshfallLevel? _ashfallLevel;

    void TfwAshfallContent(string name, ref MapContent? map)
    {
        if (name != TfwMap.Name) return;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        _ashfallLevel = AshfallMap.Build();
        map = AshfallContent.ToMapContent(_ashfallLevel);
        GD.Print($"ashfall: level generated in {watch.ElapsedMilliseconds} ms, hash {_ashfallLevel.Hash:X16}; nothing read from the install");
    }

    void TfwAshfallVisuals(string name, ref bool drawn)
    {
        if (name != TfwMap.Name || _ashfallLevel is null || _gameSpace is null) return;
        try
        {
            var view = AshfallView.Build(_ashfallLevel);
            _gameSpace.AddChild(view);
            if (_sun is not null && _environment is not null) AshfallView.ApplyLook(_sun, _environment);
            drawn = true;
        }
        catch (System.Exception e) when (e is not (System.StackOverflowException or System.AccessViolationException))
        {
            GD.Print($"ashfall: the graybox view failed ({e.GetType().Name}: {e.Message}); the grey collision is drawn instead");
        }
    }
}
