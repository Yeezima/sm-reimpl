using SmNet;
using SmNet.Extras.Musou.Campaign;
using SmSim.Extras.Musou.Campaign;

namespace SmServer;

/// <summary>
/// The class change's smoke on the wire (Musou Mode pass 2, item 2; <see cref="TfwRun.ChangeClass"/> decides, this tells everybody). A change leaves a <see cref="TfwFireKind.Smoke"/> record in
/// <see cref="TfwRun.Smoke"/>; it goes to every client in tag 11, in <see cref="Copies"/> consecutive ticks' messages (a lost datagram does not lose the cloud; a client plays each record once by
/// its kind, tick and subject) and then leaves the window. Its own small file beside <c>GameServer.TfwSend.cs</c>, which the fire support (M8) extends for shells and runs.
/// </summary>
public sealed partial class GameServer
{
    /// <summary>Messages one smoke record goes in (ours).</summary>
    const int SmokeCopies = 3;

    readonly List<(TfwFireEvent Event, int Copies)> _tfwSmoke = new();
    readonly List<TfwFireEvent> _tfwSmokeNow = new();

    /// <summary>Smoke records sent (first copies), for the log and the tests.</summary>
    public int TfwSmokeSent { get; private set; }

    /// <summary>Once a tick from <c>SendTfw</c>: takes the run's new records, sends the window's, ages it.</summary>
    void SendTfwSmoke(uint tick)
    {
        if (World.Tfw is not { } run) return;
        // The new look is in the scoreboard's version: the next tick sends one now, so the other clients ask for it with the smoke still up (not up to a second later).
        if (run.Smoke.Count > 0 && _nextTickAt is { } next) _nextScoreboardAt = next - TimeSpan.FromMilliseconds(1);
        foreach (var e in run.Smoke)
        {
            _tfwSmoke.Add((e, 0));
            TfwSmokeSent++;
            int index = e.Subject;
            Log?.Invoke($"player {index} changes class in the Stormbird: smoke at ({e.At.X:F0}, {e.At.Y:F0}, {e.At.Z:F0})");
        }
        run.ClearSmoke();
        if (_tfwSmoke.Count == 0) return;
        _tfwSmokeNow.Clear();
        foreach (var (e, _) in _tfwSmoke) _tfwSmokeNow.Add(e);
        SendExtras(new ExtrasUpdate(tick, [(TfwWire.FireTag, TfwWire.WriteFire(_tfwSmokeNow))]));
        for (int i = _tfwSmoke.Count - 1; i >= 0; i--)
        {
            var (e, copies) = _tfwSmoke[i];
            if (copies + 1 >= SmokeCopies || e.Tick > tick) _tfwSmoke.RemoveAt(i);
            else _tfwSmoke[i] = (e, copies + 1);
        }
    }
}
