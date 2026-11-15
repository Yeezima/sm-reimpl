using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// classic-maps-all (2026-11-15; protocol 79): a server that runs a map rotation (<c>--maps</c>) says so in the match block of an ended match
/// (<see cref="MatchView.NextMap"/>) and stops itself a second before the restart clock runs out, to come back on the next map of its list at the same address.
/// This view then asks its owner (<c>ModelViewer</c>) to join the same address again (<see cref="RotateRequested"/>): the new join reads the Challenge's map name, loads that
/// map (the loading screen shows) and plays on. The rejoin waits as long as a map takes to load on the server (<see cref="PatientConnect"/>).
/// </summary>
public partial class NetworkView
{
    /// <summary>The seconds the server's last second before its rotation covers: the ended match's ticks left at which the rejoin is asked for (30 ticks, 1 s).</summary>
    public const uint RotationRejoinTicks = MatchFlowWire.RotationRejoinTicks;

    /// <summary>The match ended on a server that follows it with another map, and the restart clock is about to run out: join the same address again.</summary>
    public bool RotateRequested { get; private set; }

    /// <summary>This view is itself a rejoin after a rotation: its connection waits up to a minute for the server's answer instead of 10 s (a map load on the server).</summary>
    public bool PatientConnect { get; init; }

    void NoteRotation(MatchView match)
    {
        if (RotateRequested || match.State != MatchState.Ended || !match.NextMap || match.TicksLeft > RotationRejoinTicks) return;
        RotateRequested = true;
        Godot.GD.Print("rotation: the server moves to its next map; joining the same address again");
    }
}
