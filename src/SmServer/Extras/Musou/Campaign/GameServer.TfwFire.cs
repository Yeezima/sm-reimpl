using SmNet;
using SmNet.Extras.Musou.Campaign;
using SmSim.Extras.Musou.Campaign;

namespace SmServer;

/// <summary>
/// Tag 11 on the server (Musou pass 2, M8): fire support's records go to every client, each shell and run in <see cref="TfwSendRules.FireCopies"/> messages on consecutive ticks (the envelope is not
/// reliable; a client plays each once), a round once (a lost tracer matters to nobody). A section holds at most <see cref="TfwWire.MaxFire"/>. Only in a war: <see cref="SendTfw"/> calls this.
/// <para>
/// Test aid <c>SM_TFW_FIRE_TEST=1</c>: the server takes the command FireTest (a barrage on demand; client <c>--tfw-fire-test</c>, key F9) and prints a <c>TFWFIRE</c> line for every shell.
/// </para>
/// </summary>
public sealed partial class GameServer
{
    static readonly string? TfwFireTest = Environment.GetEnvironmentVariable("SM_TFW_FIRE_TEST");
    readonly List<TfwFireEvent> _tfwFireTaken = new(), _tfwFireSection = new();
    readonly List<(TfwFireEvent Event, int Left)> _tfwFireWindow = new();
    int _tfwFireLogged;

    /// <summary>Fire records sent (all sections), with their repeats.</summary>
    public long TfwFireRecordsSent { get; private set; }

    void SendTfwFire()
    {
        if (World.Tfw is not { } run) return;
        if (TfwFireTest is { Length: > 0 } test) { run.Fire.TestCommands = true; TfwFireLog(run); }
        _tfwFireTaken.Clear();
        run.Fire.TakeEvents(_tfwFireTaken);
        foreach (var e in _tfwFireTaken) _tfwFireWindow.Add((e, TfwSendRules.FireCopies(e.Kind)));
        if (_tfwFireWindow.Count == 0) return;
        _tfwFireSection.Clear();
        for (int k = 0; k < _tfwFireWindow.Count && _tfwFireSection.Count < TfwWire.MaxFire; k++)
        {
            var (e, left) = _tfwFireWindow[k];
            _tfwFireSection.Add(e);
            _tfwFireWindow[k] = (e, left - 1);
        }
        _tfwFireWindow.RemoveAll(w => w.Left <= 0);
        var bytes = TfwWire.WriteFire(_tfwFireSection);
        if (SendExtras(new ExtrasUpdate(World.Tick, [(TfwWire.FireTag, bytes)]))) TfwFireRecordsSent += _tfwFireSection.Count;
    }

    void TfwFireLog(TfwRun run)
    {
        var fire = run.Fire;
        foreach (var e in fire.Events)
            if (e.Kind != TfwFireKind.Round) Console.WriteLine($"TFWFIRE tick {World.Tick}: {e.Kind} {e.Subject} lands/starts {e.Tick} at ({e.At.X:F1}, {e.At.Y:F1}, {e.At.Z:F1})" + (e.Kind == TfwFireKind.Run ? $" to ({e.To.X:F1}, {e.To.Y:F1}, {e.To.Z:F1})" : ""));
        if (World.Tick % 300 == 0) Console.WriteLine($"TFWFIRE tick {World.Tick}: {run.Phase}, battle {run.Battle is not null}, match {World.State}, next run {(TfwFireRules.RunsEnabled ? fire.NextRunTick : 0)}");
        if (fire.ShellsLanded + fire.RoundsFired == _tfwFireLogged || World.Tick % 30 != 0) return;
        _tfwFireLogged = fire.ShellsLanded + fire.RoundsFired;
        Console.WriteLine($"TFWFIRE tick {World.Tick}: barrages {fire.Barrages}, shells landed {fire.ShellsLanded}, shell kills {fire.ShellKills}; runs {fire.Runs}, rounds {fire.RoundsFired}, hits {fire.RoundsHit}, round kills {fire.RoundKills}; enemies alive {World.Musou?.Alive}");
    }
}
