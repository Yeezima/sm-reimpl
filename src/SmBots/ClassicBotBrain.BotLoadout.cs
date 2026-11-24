using System;
using System.Linq;
using SmNet;
using SmSim;

namespace SmBots;

/// <summary>
/// The owner's fixed Classic PvP bot loadouts (2026-11-07; **ours**, test bots; <see cref="BotLoadouts"/>): a <c>--bot</c> with a <c>--class</c> picks one of its
/// role's presets at random once per match and joins with it. Decided when the Challenge names the map (<see cref="ClientOptions.ReviseOnMap"/>): only on a Classic
/// PvP map (<c>mp_*</c>), so a Musou Mode bot (its maps are <c>tfw_war</c> and <c>ashfall_citadel</c>) joins exactly as before. Explicit loadout flags win: with
/// <c>--primary</c>, <c>--pistol</c>, <c>--melee</c>, <c>--equipment</c>, <c>--bot-loadout</c>, <c>--perks</c>, <c>--second</c> or <c>--profile</c> no preset is used.
/// <c>--bot-preset none</c> turns it off; <c>--bot-preset a|b|c</c> forces that preset of the class's role (tests, screenshots).
/// </summary>
public sealed partial class ClassicBotBrain
{
    string? BotPresetArg => CmdArg("bot-preset");

    /// <summary>The preset this bot joined with (null: none), for its log.</summary>
    BotLoadoutPick? _botPreset;

    Random? _presetRandom;

    /// <summary>
    /// The preset draw: <see cref="Random.Shared"/> as the client always drew it (one draw per match from the process's shared generator, so the distribution is
    /// unchanged), or a generator seeded with <see cref="ClassicBotOptions.PresetSeed"/> (a server's brain: the same pick for the same seed).
    /// </summary>
    int PresetDraw() => _options.PresetSeed is { } seed ? (_presetRandom ??= new Random(seed)).Next() : Random.Shared.Next();

    /// <summary>The preset this bot joined with (null: none, or not yet decided).</summary>
    public BotLoadoutPick? Preset => _botPreset;

    /// <summary><see cref="ClientOptions.ReviseOnMap"/>: on a Classic PvP map, the options with a preset of the requested class's role.</summary>
    public ClientOptions BotPresetOnMap(string map, ClientOptions options)
    {
        if (_config is null || !map.StartsWith("mp_", StringComparison.OrdinalIgnoreCase)) return options;
        if (options.RequestedClass == Protocol.ServerDefaultClass) { GD.Print("bot preset: no --class, the server's default loadout is used"); return options; }
        int playerClass = options.RequestedClass;
        if (Loadouts.ClassOf(_config, playerClass) is not { } loadout) return options;
        var presets = BotLoadouts.For(loadout.Role);
        if (presets.Count == 0) { GD.Print($"bot preset: none for role '{loadout.Role}' ({loadout.Name})"); return options; }
        var preset = BotPresetArg is { Length: > 0 } key ? presets.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)) : null;
        preset ??= presets[BotLoadouts.PickIndex(PresetDraw(), presets.Count)];
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
