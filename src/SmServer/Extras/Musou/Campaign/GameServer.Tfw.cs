using SmNet;
using SmNet.Extras.Musou.Campaign;
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

    /// <summary>At the end of the constructor: on the war's map (<see cref="CreateMusou"/> leaves it alone), the war on the arena's walking graph (<c>GameServer.TfwMap.cs</c>).</summary>
    void CreateTfw()
    {
        if (!World.Config.Extras.HasFlag(ContentExtras.Musou) || Map?.Name != TfwMap.Name) return;
        var nav = TfwNav(out string navNote);
        bool started = World.StartTfw(nav);
        if (started) { PlayerJoined += p => World.MusouJoined(p.PlayerId); TfwUseWireBox(); }
        _musouNote = started ? $"musou: the Three-Front War runs on {TfwMap.Name} (placeholder arena and Stormbird; Space Marines only, no limits); {navNote}" : "musou: not started (the match has no team rules)";
    }

    /// <summary>This feature's part of the envelope hook (the dispatcher <c>GameServer.Extras.cs</c>). TF0 sends nothing; TF6: tags 7 to 10.</summary>
    void SendTfw()
    {
        if (World.Tfw is null) return;
    }

    /// <summary>A client's command (message kind 17): queued for the next tick, only under the war's tag and when well formed; the session and the rate are the core's.</summary>
    void OnTfwCommand(int player, byte tag, byte[] payload)
    {
        if (World.Tfw is not { } run || tag != TfwWire.CommandTag || TfwWire.ReadCommand(payload) is not { } command) return;
        run.Enqueue(player, command);
    }
}
