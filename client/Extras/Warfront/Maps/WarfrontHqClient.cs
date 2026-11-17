using Godot;
using SmContent;
using SmContent.Extras.Musou;
using SmContent.Extras.Warfront;
using SmSim.Extras.Musou.Ashfall;
using SmSim.Extras.Warfront.Hq;

namespace SmClient;

/// <summary>
/// The HQ defence map on the client (slice W13b): the level the server builds (<see cref="WarfrontHqLayout.Build"/>, deterministic, the same hash) as collision, drawn as the graybox of <see cref="AshfallView"/> and then dressed in steps (<see cref="WarfrontHqDress"/>: the exterior only; the keep and the caves wait for W13d). The hub is composed on it by the same path as a lane slot's. Called from <see cref="WarfrontMapContent"/> and <see cref="WarfrontMapVisuals"/> (NetworkView.WarfrontBand.cs).
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

    /// <summary>
    /// Draws the HQ level: the graybox at once, then the exterior's dressing (<see cref="WarfrontHqDress"/>: the Imperial wall and gate, the ramp, the courtyard, the outside) one step per frame, as the fortress does
    /// (<c>WarfrontDressSchedule</c> in WarfrontFortressClient.cs; the Identify is held while they run). The keep and the caves stay as the kit look draws them. Any failure leaves the grey collision or the graybox.
    /// </summary>
    void WarfrontHqVisuals(ref bool drawn)
    {
        if (_gameSpace is null) return;
        try
        {
            var node = new Node3D { Name = "WarfrontHq" };
            _gameSpace.AddChild(node);
            var build = WarfrontHqLayout.Build;
            var view = AshfallView.Build(build.Level);
            node.AddChild(view);
            if (_sun is not null && _environment is not null) AshfallView.ApplyLook(_sun, _environment);
            drawn = true;
            if (WarfrontFarMassing.Off) { GD.Print("warfront: the HQ defence drawn (graybox; --dress off)"); return; }
            _warfrontDressSteps.Clear();
            _warfrontDressReport = () => WarfrontHqDress.Report; _warfrontDressSlot = "wf_hq";
            foreach (var step in WarfrontHqDress.Steps(node, build, view, _sun, _environment)) _warfrontDressSteps.Enqueue(step);
            _warfrontDressStartUs = (long)Time.GetTicksUsec(); _warfrontDressLongestUs = 0;
            GD.Print($"warfront: the HQ defence drawn (graybox); {_warfrontDressSteps.Count} dressing steps follow, one a frame");
            WarfrontDressSchedule(node);
        }
        catch (System.Exception e) when (e is not (System.StackOverflowException or System.AccessViolationException))
        {
            GD.Print($"warfront: the HQ view failed ({e.GetType().Name}: {e.Message}); the grey collision is drawn instead");
        }
    }
}
