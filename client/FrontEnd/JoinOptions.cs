using System;
using SmClient.Customizer;
using SmClient.Customizer.Model;
using SmContent;
using SmNet;
using SmSim;

namespace SmClient.FrontEnd;

/// <summary>
/// The front end's hand-over to the networked view. <see cref="Current"/> is the join that started the match (null for a plain <c>--connect</c> without a profile);
/// <see cref="Apply"/> fills a <see cref="ClientOptions"/> from it: the class, the item and perk indices, both races' appearance codes, the emblem source for the
/// uploads, the setting for others' emblems and the catalogue limits. Today the networked view takes the class, items and side through its existing arguments
/// (<see cref="JoinPlan.ToArgs"/>); T7 calls <see cref="Apply"/> where it builds its <c>ClientOptions</c> to send the perks and looks as well, and uses the same plan
/// for the in-match picker (<see cref="Customizer.CreatorView.Banner"/>, <see cref="Customizer.CreatorView.Deploy"/>).
/// </summary>
public static class FrontEndLaunch
{
    public static JoinRequest? Current { get; set; }

    /// <summary>The options with the front end's choices applied when the match was started from the front end or a profile; unchanged otherwise.</summary>
    public static ClientOptions WithProfile(ClientOptions options)
    {
        if (Current is not { } request) return options;
        var applied = Apply(options, request);
        Godot.GD.Print($"front end options: class {applied.RequestedClass}, perks {applied.RequestedPerk1}/{applied.RequestedPerk2}, Space Marine look {(applied.SpaceMarineAppearance.IsDefault ? "default" : "custom")}, Chaos look {(applied.ChaosAppearance.IsDefault ? "default" : "custom")}, " +
                       $"emblem {(applied.SpaceMarineAppearance.UsesEmblem ? applied.SpaceMarineAppearance.Emblem.ToHex() : applied.ChaosAppearance.UsesEmblem ? applied.ChaosAppearance.Emblem.ToHex() : "none")}, others' emblems {(applied.CustomEmblems ? "shown" : "hidden")}");
        return applied;
    }

    public static ClientOptions Apply(ClientOptions options, JoinRequest request)
    {
        var plan = request.Plan;
        var catalog = request.Services.Catalog;
        // Added content E2: a Join never asks for what the profile does not have: no Ork class (the side's default class instead) and no Ork look.
        var extras = request.Content ?? request.Services.Content;
        var choice = request.Services.Config is { } config ? ContentGate.ForJoin(plan.Choice, config, extras, LoadoutChoice.Default) : plan.Choice;
        byte B(int index) => index >= 0 && index < Protocol.DefaultChoice ? (byte)index : Protocol.DefaultChoice;
        return options with
        {
            RequestedClass = choice.Class >= 0 && choice.Class < Protocol.ServerDefaultClass ? (byte)choice.Class : Protocol.ServerDefaultClass,
            RequestedPrimary = B(choice.Primary), RequestedPistol = B(choice.Pistol), RequestedMelee = B(choice.Melee), RequestedEquipment = B(choice.Equipment),
            RequestedPerk1 = B(choice.Perk1), RequestedPerk2 = B(choice.Perk2),
            OrkLook = ContentGate.OrkLook(options.OrkLook, extras),
            RequestedSide = plan.Side,
            SpaceMarineAppearance = plan.SpaceMarineLook, ChaosAppearance = plan.ChaosLook,
            EmblemSource = request.Services.EmblemBytes,
            CustomEmblems = request.Services.Settings.ShowOthersEmblems,
            SpaceMarineLimits = catalog?.LimitsFor(CustomizationCatalog.SpaceMarine), ChaosLimits = catalog?.LimitsFor(CustomizationCatalog.Chaos),
        };
    }
}
