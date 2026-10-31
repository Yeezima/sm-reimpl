using SmSim.Extras.Musou.Campaign;

namespace SmServer;

/// <summary>The AI Marines on the scoreboard (TF5): a puppet has no peer, so its name comes from the run; lords' names are placeholders (design open question 7).</summary>
public sealed partial class GameServer
{
    /// <summary>An AI Marine's scoreboard name; every other player keeps its own.</summary>
    void TfwPlayerName(int id, ref string name)
    {
        if (World.Tfw is { } run && TfwContracts.IsPuppet(id)) name = run.PuppetName(id);
    }
}
