using SmNet;
using SmContent.Extras.Musou;
using SmSim;
using SmSim.Extras.Musou.Campaign;

namespace SmServer;

/// <summary>
/// Musou Mode on the server (TF0: the contract and stubs). <c>--content musou</c> runs on the composed map <see cref="TfwMap.Name"/> by default
/// (<see cref="TfwGeneratedMap"/>: Ashfall Citadel, generated in code, plus the Stormbird and the Firing Range); <c>--map tfw_war_m09</c> (<see cref="TfwMap.ManufactorumName"/>) restores the old
/// war on the install's Manufactorum; <c>--map tfw_war_field</c> (<see cref="TfwMap.FieldName"/>) runs Ashfall's draft-11 battlefield layout, <c>--map tfw_war_draft10</c> (<see cref="TfwMap.Draft10Name"/>) the draft-10 rollback (<c>tfw_war</c> is the draft-13 high-ground layout since 2026-11-14, <c>--map tfw_war_city</c> the draft-12 city); a named map of the install (<c>--map mp_tdm_09</c>) keeps the 5-wave run as a test aid.
/// </summary>
public sealed partial class GameServer
{
    /// <summary>
    /// The composed map: the default under Musou, refused without it (<c>--map tfw_war</c> or <c>tfw_war_m09</c> on any other profile); any other named map is the install's. Ashfall Citadel needs nothing from the
    /// install; <c>tfw_war_m09</c> with an install without Manufactorum gives a clear error, no map.
    /// </summary>
    static void TfwGeneratedMap(ContentExtras extras, string install, ref string? name, ref GameMap? map, ref string? error)
    {
        bool musou = extras.HasFlag(ContentExtras.Musou);
        if (TfwMap.IsWarMap(name) && !musou) { error = $"--map {name} is Musou Mode's map: it needs --content {ContentProfiles.Musou}"; return; }
        if (!musou || !(name is null || TfwMap.IsWarMap(name))) return;
        name ??= TfwMap.Name;
        if (TfwWorld.Load(install, out string? problem, name) is { } data) map = TfwGameMap(data, name);
        else error = problem;
        if (SmContent.Extras.Musou.AshfallLayoutCache.LastNote is { } cacheNote) Console.WriteLine($"ashfall: {cacheNote}");   // polish pass P2
    }

    /// <summary>The server's map of a composed war map: <see cref="GameMap.Spawns"/> are the Stormbird's, so a join lands there, and the battlefield goes along for <see cref="CreateTfw"/>.</summary>
    public static GameMap TfwGameMap(TfwMapData data, string name = TfwMap.Name) =>
        new(name, data.Collision, data.Spawns, data.Hash, StartAreas: data.StartAreas) { AddedContent = data.Field };

    /// <summary>At the end of the constructor: on the war's map (<see cref="CreateMusou"/> leaves it alone), the war on the arena's walking graph (<c>GameServer.TfwMap.cs</c>).</summary>
    void CreateTfw()
    {
        if (!World.Config.Extras.HasFlag(ContentExtras.Musou) || Map?.AddedContent is not TfwBattlefield field) return;
        var nav = TfwNav(field, out string navNote);
        bool started = World.StartTfw(nav, field);
        if (started) { PlayerJoined += p => World.MusouJoined(p.PlayerId); TfwUseWireBox(); }
        if (started && float.TryParse(Environment.GetEnvironmentVariable("SM_MUSOU_S2_DEMO"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float demo) && demo > 0f)
            World.TfwS2DemoSeconds = demo;   // expansion S2's capture aid (World.TfwSquigs.cs)
        if (started && Environment.GetEnvironmentVariable("SM_MUSOU_S4_DEMO") == "1") World.TfwMekDemo = true;   // expansion S4's capture aid (World.TfwMeks.cs)
        _musouNote = started ? $"musou: Musou Mode runs on {Map.Name} ({(Map.Name == TfwMap.ManufactorumName ? $"{TfwMap.Battlefield} from the install" : Map.Name == TfwMap.FieldName ? "Ashfall Citadel, generated, the battlefield layout (draft 11)" : Map.Name == TfwMap.Draft10Name ? "Ashfall Citadel, generated, the draft-10 rollback" : Map.Name == TfwMap.CityName ? "Ashfall Citadel, generated, the city layout (draft 12)" : "Ashfall Citadel, generated, the high-ground layout (draft 13)")} and the Stormbird at {field.Hub.Centre}; Space Marines only, no limits); {navNote}; {World.Tfw?.Sites.Candidates} open nodes" : "musou: not started (the match has no team rules)";
    }
}
