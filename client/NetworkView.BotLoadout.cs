using System;
using System.Linq;
using Godot;
using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// The owner's fixed Classic PvP bot loadouts (2026-11-07; **ours**, test bots; <see cref="BotLoadouts"/>): a <c>--bot</c> with a <c>--class</c> picks one of its
/// role's presets at random once per match and joins with it. Decided when the Challenge names the map (<see cref="ClientOptions.ReviseOnMap"/>): only on a Classic
/// PvP map (<c>mp_*</c>), so a Musou Mode bot (its maps are <c>tfw_war</c> and <c>ashfall_citadel</c>) joins exactly as before. Explicit loadout flags win: with
/// <c>--primary</c>, <c>--pistol</c>, <c>--melee</c>, <c>--equipment</c>, <c>--bot-loadout</c>, <c>--perks</c>, <c>--second</c> or <c>--profile</c> no preset is used.
/// <c>--bot-preset none</c> turns it off; <c>--bot-preset a|b|c</c> forces that preset of the class's role (tests, screenshots).
/// </summary>
public partial class NetworkView
{
    static readonly string? BotPresetArg = CmdArg("bot-preset");

    /// <summary>The preset this bot joined with (null: none), for its log.</summary>
    BotLoadoutPick? _botPreset;

    /// <summary>Whether this client may use a bot preset at all (the map is checked later).</summary>
    bool BotPresetWanted()
    {
        if (!_bot || Observe || _demoPlayback is not null || string.Equals(BotPresetArg, "none", StringComparison.OrdinalIgnoreCase)) return false;
        if (_loadout.Primary is not null || _loadout.Pistol is not null || _loadout.Melee is not null || _loadout.Equipment is not null || _loadout.Random) return false;
        var args = OS.GetCmdlineUserArgs();
        return !args.Contains("--perks") && !args.Contains("--second") && !args.Contains("--profile") && !args.Contains("--bot-change-loadout");
    }

    /// <summary><see cref="ClientOptions.ReviseOnMap"/>: on a Classic PvP map, the options with a preset of the requested class's role.</summary>
    ClientOptions BotPresetOnMap(string map, ClientOptions options)
    {
        if (_config is null || !map.StartsWith("mp_", StringComparison.OrdinalIgnoreCase)) return options;
        if (options.RequestedClass == Protocol.ServerDefaultClass) { GD.Print("bot preset: no --class, the server's default loadout is used"); return options; }
        int playerClass = options.RequestedClass;
        if (Loadouts.ClassOf(_config, playerClass) is not { } loadout) return options;
        var presets = BotLoadouts.For(loadout.Role);
        if (presets.Count == 0) { GD.Print($"bot preset: none for role '{loadout.Role}' ({loadout.Name})"); return options; }
        var preset = BotPresetArg is { Length: > 0 } key ? presets.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)) : null;
        preset ??= presets[BotLoadouts.PickIndex(System.Random.Shared.Next(), presets.Count)];
        var pick = BotLoadouts.Resolve(_config, playerClass, preset);
        _botPreset = pick;
        GD.Print($"bot preset ({(BotPresetArg is { Length: > 0 } ? "--bot-preset" : "random, once per match")}): {BotLoadouts.Describe(_config, pick)}");
        static byte B(int i) => i < 0 ? Protocol.DefaultChoice : (byte)i;
        return options with
        {
            RequestedPrimary = B(pick.Primary), RequestedSecond = B(pick.Second), RequestedPistol = B(pick.Pistol), RequestedMelee = B(pick.Melee),
            RequestedEquipment = B(pick.Equipment), RequestedPerk1 = B(pick.Perk1), RequestedPerk2 = B(pick.Perk2),
        };
    }
}
