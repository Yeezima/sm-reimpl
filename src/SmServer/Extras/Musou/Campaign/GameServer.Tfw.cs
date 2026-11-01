using SmNet;
using SmContent.Extras.Musou;
using SmSim;
using SmSim.Extras.Musou.Campaign;

namespace SmServer;

/// <summary>
/// Musou Mode on the server (TF0: the contract and stubs). <c>--content musou</c> runs on the composed map <see cref="TfwMap.Name"/> by default
/// (<see cref="TfwGeneratedMap"/>: Manufactorum from the install plus the Stormbird); a named map of the install (<c>--map mp_tdm_09</c>) keeps the 5-wave run as a test aid.
/// </summary>
public sealed partial class GameServer
{
    /// <summary>The composed map: the default under Musou, refused without it (<c>--map tfw_war</c> on any other profile); any other named map is the install's. An install without Manufactorum gives a clear error, no map.</summary>
    static void TfwGeneratedMap(ContentExtras extras, string install, ref string? name, ref GameMap? map, ref string? error)
    {
        bool musou = extras.HasFlag(ContentExtras.Musou);
        if (name == TfwMap.Name && !musou) { error = $"--map {TfwMap.Name} is Musou Mode's map: it needs --content {ContentProfiles.Musou}"; return; }
        if (!musou || name is not (null or TfwMap.Name)) return;
        name = TfwMap.Name;
        if (TfwWorld.Load(install, out string? problem) is { } data) map = TfwGameMap(data);
        else error = problem;
    }

    /// <summary>The server's map of a composed war map: <see cref="GameMap.Spawns"/> are the Stormbird's, so a join lands there, and the battlefield goes along for <see cref="CreateTfw"/>.</summary>
    public static GameMap TfwGameMap(TfwMapData data) =>
        new(TfwMap.Name, data.Collision, data.Spawns, data.Hash, StartAreas: data.StartAreas) { AddedContent = data.Field };

    /// <summary>At the end of the constructor: on the war's map (<see cref="CreateMusou"/> leaves it alone), the war on the arena's walking graph (<c>GameServer.TfwMap.cs</c>).</summary>
    void CreateTfw()
    {
        if (!World.Config.Extras.HasFlag(ContentExtras.Musou) || Map?.AddedContent is not TfwBattlefield field) return;
        var nav = TfwNav(field, out string navNote);
        bool started = World.StartTfw(nav, field);
        if (started) { PlayerJoined += p => World.MusouJoined(p.PlayerId); TfwUseWireBox(); }
        _musouNote = started ? $"musou: Musou Mode runs on {TfwMap.Name} ({TfwMap.Battlefield} and the Stormbird at {field.Hub.Centre}; Space Marines only, no limits); {navNote}; {World.Tfw?.Sites.Candidates} open nodes" : "musou: not started (the match has no team rules)";
    }
}
