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
    AshfallDressing? _ashfallDressing;

    void TfwAshfallContent(string name, ref MapContent? map)
    {
        if (name != TfwMap.Name) return;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        (_ashfallLevel, _ashfallDressing) = AshfallMap.BuildWithDressing();   // one build: the collision and the drawing share the level (Build() is BuildWithDressing().Level)
        map = AshfallContent.ToMapContent(_ashfallLevel);
        GD.Print($"ashfall: level generated in {watch.ElapsedMilliseconds} ms, hash {_ashfallLevel.Hash:X16}; nothing read from the install");
    }

    void TfwAshfallVisuals(string name, ref bool drawn)
    {
        if (name != TfwMap.Name || _ashfallLevel is null || _ashfallDressing is null || _gameSpace is null) return;
        try
        {
            var (level, dressing) = (_ashfallLevel, _ashfallDressing);   // the level the collision came from: its parts index the HiddenParts and the Skin items
            var view = AshfallView.Build(level);
            _gameSpace.AddChild(view);
            if (_sun is not null && _environment is not null) AshfallView.ApplyLook(_sun, _environment);
            drawn = true;
            TfwAshfallKit(level, dressing, view);
            TfwAshfallStorm(level, dressing, view);
            if (AshfallFrameBurst.FromArgs() is { } burst) view.AddChild(burst);   // test aid: consecutive frames of the match camera (hotfix/ashfall-floor-shimmer)
        }
        catch (System.Exception e) when (e is not (System.StackOverflowException or System.AccessViolationException))
        {
            GD.Print($"ashfall: the graybox view failed ({e.GetType().Name}: {e.Message}); the grey collision is drawn instead");
        }
    }

    /// <summary>Draft 7's map-wide storm look (<see cref="AshfallStorm"/>; <c>--ashfall-weather plain</c> leaves it out). Under the map's view, so it goes with the map; any failure leaves the map as drawn.</summary>
    void TfwAshfallStorm(AshfallLevel level, AshfallDressing dressing, AshfallView view)
    {
        if (_sun is null || _environment is null || AshfallStorm.Off) return;
        try { AshfallStorm.Attach(view, level, dressing, _sun, _environment); }
        catch (System.Exception e) when (e is not (System.StackOverflowException or System.AccessViolationException or System.OutOfMemoryException))
        {
            GD.Print($"ashfall storm: the storm look failed ({e.GetType().Name}: {e.Message}); the plain look stays");
        }
    }

    /// <summary>
    /// The kit look beside the graybox (docs/musou-ashfall-graybox.md "Kit look (client)"): <c>--ashfall-look kit|graybox</c> (default kit when the private kit folder exists, else graybox); F8 toggles.
    /// Any failure leaves the graybox as drawn.
    /// </summary>
    void TfwAshfallKit(AshfallLevel level, AshfallDressing dressing, AshfallView graybox)
    {
        try
        {
            var args = OS.GetCmdlineUserArgs();
            int i = System.Array.IndexOf(args, "--ashfall-look");
            string? asked = i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
            if (asked is not (null or "kit" or "graybox")) { GD.Print($"ashfall: --ashfall-look '{asked}' is not kit or graybox; the default is used"); asked = null; }
            if (!AshfallKitLibrary.Available)
            {
                GD.Print($"ashfall kit: the folder {AshfallKitLibrary.Root} does not exist; the graybox look is drawn (F8 has nothing to switch to)");
                return;
            }
            var kit = new AshfallKitView(level, dressing, graybox);
            _gameSpace!.AddChild(kit);
            kit.SetKit(asked != "graybox");
        }
        catch (System.Exception e) when (e is not (System.StackOverflowException or System.AccessViolationException or System.OutOfMemoryException))
        {
            GD.Print($"ashfall kit: the kit look failed ({e.GetType().Name}: {e.Message}); the graybox look stays");
        }
    }
}
