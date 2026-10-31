using SmNet;
using SmSim;
using SmSim.Extras.Musou.Campaign;

namespace SmServer;

/// <summary>
/// The Three-Front War on the server (TF0: the contract and stubs). <c>--content musou</c> runs on the generated map <see cref="TfwMap.Name"/> by default
/// (<see cref="TfwGeneratedMap"/>; the owner, 2026-11-01: "the three-front war mode is Musou mode"); a named map of the install (<c>--map mp_tdm_09</c>) keeps the 5-wave run as a test aid.
/// </summary>
public sealed partial class GameServer
{
    /// <summary>The generated map: the default under Musou, refused without it (<c>--map tfw_war</c> on any other profile); any other named map is the install's.</summary>
    static void TfwGeneratedMap(ContentExtras extras, ref string? name, ref GameMap? map, ref string? error)
    {
        bool musou = extras.HasFlag(ContentExtras.Musou);
        if (name == TfwMap.Name && !musou) { error = $"--map {TfwMap.Name} is the Three-Front War's map: it needs --content {ContentProfiles.Musou}"; return; }
        if (!musou || name is not (null or TfwMap.Name)) return;
        name = TfwMap.Name;
        var data = TfwMap.Build();
        map = new GameMap(TfwMap.Name, data.Collision, data.Spawns, data.Hash, StartAreas: data.StartAreas);
    }

    /// <summary>At the end of the constructor: on the war's map (<see cref="CreateMusou"/> leaves it alone), the war. TF1 builds its nav graph (null until then: no enemies).</summary>
    void CreateTfw()
    {
        if (!World.Config.Extras.HasFlag(ContentExtras.Musou) || Map?.Name != TfwMap.Name) return;
        bool started = World.StartTfw(null);
        if (started) PlayerJoined += p => World.MusouJoined(p.PlayerId);
        _musouNote = started ? $"musou: the Three-Front War runs on {TfwMap.Name} (stub map; Space Marines only, no limits)" : "musou: not started (the match has no team rules)";
    }

    /// <summary>An AI Marine's scoreboard name; every other player keeps its own.</summary>
    void TfwPlayerName(int id, ref string name)
    {
        if (World.Tfw is { } run && TfwContracts.IsPuppet(id)) name = run.PuppetName(id);
    }
}
