using SmSim;
using SmSim.Extras.Musou.Campaign;

namespace SmServer;

/// <summary>Musou Mode co-op on the server (<see cref="TfwCoop"/>; the owner, 2026-11-06): a Musou match admits at most <see cref="TfwCoop.MaxHumans"/> players, whatever the map.</summary>
public sealed partial class GameServer
{
    /// <summary>The cap under Musou (the war and the 5-wave test aid alike); any other profile keeps it.</summary>
    void TfwPlayerCap(ref int cap) => cap = TfwCoop.PlayerCap(World.Config.Extras, cap);

    /// <summary>Humans flown in to a battle under way (<see cref="TfwRun.LateJoins"/>; 0 outside a war).</summary>
    public int TfwLateJoins => World.Tfw?.LateJoins ?? 0;

    int _tfwLateJoinsLogged;

    /// <summary>Once a tick (the envelope hook): a line for each human flown in to a battle under way, and the humans in the field.</summary>
    void LogTfwCoop()
    {
        if (World.Tfw is not { } run || run.LateJoins == _tfwLateJoinsLogged) return;
        _tfwLateJoinsLogged = run.LateJoins;
        Log?.Invoke($"musou: tick {World.Tick}: a human joined the battle under way and landed at the landing zone; in the field: {string.Join(", ", run.InField.Select(id => $"P{id}"))} ({run.LateJoins} late joins this run)");
    }
}
