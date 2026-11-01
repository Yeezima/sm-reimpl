using SmNet;
using SmSim;
using SmSim.Extras.Musou.Campaign;

namespace SmServer;

/// <summary>
/// Musou Mode on the server (TF0: the contract and stubs). <c>--content musou</c> runs on the generated map <see cref="TfwMap.Name"/> by default
/// (<see cref="TfwGeneratedMap"/>; the owner's decision of 2026-11-01: this war is Musou Mode); a named map of the install (<c>--map mp_tdm_09</c>) keeps the 5-wave run as a test aid.
/// </summary>
public sealed partial class GameServer
{
    /// <summary>The generated map: the default under Musou, refused without it (<c>--map tfw_war</c> on any other profile); any other named map is the install's.</summary>
    static void TfwGeneratedMap(ContentExtras extras, ref string? name, ref GameMap? map, ref string? error)
    {
        bool musou = extras.HasFlag(ContentExtras.Musou);
        if (name == TfwMap.Name && !musou) { error = $"--map {TfwMap.Name} is Musou Mode's map: it needs --content {ContentProfiles.Musou}"; return; }
        if (!musou || name is not (null or TfwMap.Name)) return;
        name = TfwMap.Name;
        var data = TfwMap.Build();
        map = new GameMap(TfwMap.Name, data.Collision, data.Spawns, data.Hash, StartAreas: data.StartAreas);
    }

    /// <summary>At the end of the constructor: on the war's map (<see cref="CreateMusou"/> leaves it alone), the war on the arena's walking graph (<c>GameServer.TfwMap.cs</c>).</summary>
    void CreateTfw()
    {
        if (!World.Config.Extras.HasFlag(ContentExtras.Musou) || Map?.Name != TfwMap.Name) return;
        var nav = TfwNav(out string navNote);
        bool started = World.StartTfw(nav);
        if (started) { PlayerJoined += p => World.MusouJoined(p.PlayerId); TfwUseWireBox(); }
        _musouNote = started ? $"musou: Musou Mode runs on {TfwMap.Name} (placeholder arena and Stormbird; Space Marines only, no limits); {navNote}" : "musou: not started (the match has no team rules)";
    }
}
