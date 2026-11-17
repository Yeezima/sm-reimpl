using Godot;
using SmContent;
using SmContent.Extras.Musou;
using SmSim.Extras.Musou.Ashfall;
using SmSim.Extras.Warfront.Fortress;
using SmSim.Extras.Warfront.Maps;

namespace SmClient;

/// <summary>
/// The Ork fortress on the client (slice W14b): the level the server builds (<see cref="WarfrontSlots.Fortress"/>, deterministic, the same collision and hash) as collision, drawn as the graybox of <see cref="AshfallView"/> and then dressed in steps (<see cref="WarfrontFortressDress"/>: kit look, plated walls, Ork pieces, storm and outside scenery). A handmade arena that is not the generated one has no drawing here: its collision is shown grey until its own client view exists. Called from <see cref="WarfrontMapContent"/> and <see cref="WarfrontMapVisuals"/> (NetworkView.WarfrontBand.cs).
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

    /// <summary>The dressing steps still to run, one per frame (dressing slice: the same stepping as the lane bands' drawing, W5d).</summary>
    readonly System.Collections.Generic.Queue<(string Name, System.Action Run)> _warfrontDressSteps = new();
    long _warfrontDressStartUs, _warfrontDressLongestUs;

    /// <summary>
    /// Draws the fortress level: the graybox at once (it hides the grey collision), then the dressing (<see cref="WarfrontFortressDress"/>: the kit look, the plated walls, the pieces, the outside scenery, the look and the storm) one step per frame,
    /// so no frame carries more than the largest step. While the steps run <c>_warfrontStepRunning</c> is set, so <c>WarfrontBandBusy</c> holds the Identify as it does for a lane band. Any failure leaves the grey collision or the graybox.
    /// </summary>
    void WarfrontFortressVisuals(ref bool drawn)
    {
        if (_gameSpace is null || WarfrontSlots.Fortress is not WarfrontFortressBuild build) return;
        try
        {
            var node = new Node3D { Name = "WarfrontFortress" };
            _gameSpace.AddChild(node);
            var view = AshfallView.Build(build.Level);
            node.AddChild(view);
            if (_sun is not null && _environment is not null) AshfallView.ApplyLook(_sun, _environment);
            drawn = true;
            if (WarfrontFarMassing.Off) { GD.Print("warfront: the Ork fortress drawn (graybox; --dress off)"); return; }
            _warfrontDressSteps.Clear();
            foreach (var step in WarfrontFortressDress.Steps(node, build, view, _sun, _environment)) _warfrontDressSteps.Enqueue(step);
            _warfrontDressStartUs = (long)Time.GetTicksUsec(); _warfrontDressLongestUs = 0;
            GD.Print($"warfront: the Ork fortress drawn (graybox); {_warfrontDressSteps.Count} dressing steps follow, one a frame");
            WarfrontDressSchedule(node);
        }
        catch (System.Exception e) when (e is not (System.StackOverflowException or System.AccessViolationException))
        {
            GD.Print($"warfront: the fortress view failed ({e.GetType().Name}: {e.Message}); the grey collision is drawn instead");
        }
    }

    /// <summary>One queued dressing step, then the next after a frame (a tree timer, as the bands' steps; the callback returns at once when a rejoin or a leave freed the view).</summary>
    void WarfrontDressSchedule(Node3D node)
    {
        if (_warfrontDressSteps.Count == 0) { _warfrontStepRunning = false; return; }
        if (!IsInsideTree()) { _warfrontStepRunning = true; while (_warfrontDressSteps.Count > 0) _warfrontDressSteps.Dequeue().Run(); _warfrontStepRunning = false; return; }
        _warfrontStepRunning = true;
        var self = this;
        GetTree().CreateTimer(0.05).Timeout += () =>
        {
            if (!IsInstanceValid(self) || !IsInstanceValid(node) || !self.IsInsideTree()) { if (IsInstanceValid(self)) { self._warfrontDressSteps.Clear(); self._warfrontStepRunning = false; } return; }
            if (self._warfrontDressSteps.Count == 0) { self._warfrontStepRunning = false; return; }
            var (name, run) = self._warfrontDressSteps.Dequeue();
            long t0 = (long)Time.GetTicksUsec();
            run();
            long us = (long)Time.GetTicksUsec() - t0;
            self._warfrontDressLongestUs = System.Math.Max(self._warfrontDressLongestUs, us);
            GD.Print($"warfront: fortress dress step \"{name}\" {us / 1000} ms");
            WarfrontFrameWatch.NoteStep("wf_fortress", name);
            if (self._warfrontDressSteps.Count == 0)
            {
                self._warfrontStepRunning = false;
                GD.Print($"warfront: fortress dressing done {((long)Time.GetTicksUsec() - self._warfrontDressStartUs) / 1000} ms after the graybox; largest step {self._warfrontDressLongestUs / 1000} ms; {WarfrontFortressDress.Report}");
            }
            else self.WarfrontDressSchedule(node);
        };
    }
}
