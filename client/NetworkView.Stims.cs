using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmContent;
using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// perks/inert-2: combat stims in the networked view. The use is the simulation's throw with the stim's own times (<see cref="ThrownSpec.ReleaseAt"/>):
/// no toss clip and nothing thrown; the stim's clip (the player graph's "Equip - StimPack" sequencer, <see cref="StimSpec.Animation"/>) is laid over the
/// upper body while the use lasts, and at the release the wargear's <c>actn_stims_activated</c> plays its sound and its effects at their markers (the
/// default entry's; the Master-Crafted entry's bigger effects are not chosen: ours). The own player's stim time is the prediction's
/// (<see cref="PlayerState.StimLeft"/>); others' are timed from their slim Throwing flag, as their throws.
/// </summary>
public partial class NetworkView
{
    /// <summary>Per player: whether a use was seen under way, when it was seen to start (connected time) and whether its release was played.</summary>
    sealed class StimTrack { public bool Using; public double Since; public bool Released; }
    readonly Dictionary<int, StimTrack> _stimTracks = new();
    int _stimUsesSeen, _stimRemoteUses, _stimFx, _stimSounds;

    bool NoStims => _noStims ??= OS.GetCmdlineUserArgs().Contains("--no-stim-fx");
    bool? _noStims;

    /// <summary>
    /// The player's equipment item as this client knows it: the own record's (perks included); another player's from the scoreboard (protocol 40;
    /// its perks are not sent, so the base item), or null while unknown. Slim snapshot records carry no equipment, so <c>Loadouts.Equipment</c> on a
    /// remote state would name the class default (review, perks/inert-2).
    /// </summary>
    ThrownSpec? ItemOf(PlayerState s)
    {
        if (_config is null) return null;
        if (_client is null || s.PlayerId == _client.PlayerId) return Loadouts.Equipment(_config, s);
        int index = _client.EquipmentOf(s.PlayerId);
        return index >= 0 && Loadouts.ValidEquipment(_config, s.Class, index) ? Loadouts.Equipment(_config, s with { Equipment = index }) : null;
    }

    /// <summary>The player's equipment when it is a stim, else null (unknown: null).</summary>
    StimSpec? StimOf(PlayerState s) => ItemOf(s)?.Stim;

    /// <summary>Whether a throw under way may be drawn with the toss clip: the item is known and is no stim (an unknown remote item shows no toss).</summary>
    bool TossShown(PlayerState s) => ItemOf(s) is { Stim: null };


    /// <summary>The stim's clip over the upper body at the use's time (the brace's leg mask: the legs keep the locomotion).</summary>
    void StimPose(CharacterViewer character, PlayerState state, float? throwTime)
    {
        if (throwTime is not { } tt || StimOf(state) is not { Animation.Length: > 0 } stim || !state.Alive) return;
        if (_assets?.Animation(stim.Animation) is null) return;
        character.SetPoseLayer(stim.Animation, tt, 1f, false, _braceUpperBody);
    }

    /// <summary>Once per frame: the release of every stim use seen (own: the predicted state; others: the slim flag's rise plus the release time).</summary>
    void UpdateStims(PlayerState me, IReadOnlyList<PlayerState> others)
    {
        if (_config is null) return;
        StimPlayer(me, own: true);
        foreach (var o in others) StimPlayer(o, own: false);
    }

    void StimPlayer(PlayerState s, bool own)
    {
        if (!_stimTracks.TryGetValue(s.PlayerId, out var t)) _stimTracks[s.PlayerId] = t = new StimTrack();
        var stim = StimOf(s);
        bool usingNow = s.Alive && s.Throwing && stim is not null;
        if (usingNow && !t.Using) { t.Since = _connectedFor - (own ? s.ThrowTime : 0f); t.Released = false; _stimUsesSeen++; if (!own) _stimRemoteUses++; }
        t.Using = usingNow;
        if (!usingNow || t.Released || stim is null) return;
        if (_connectedFor - t.Since + 1e-3 < stim.UseRelease) return;
        t.Released = true;
        GD.Print($"t {_connectedFor:F1} s: stim release of P{s.PlayerId} ({(own ? "own" : "remote")}, {ItemOf(s)!.Name}; toss clip {(TossShown(s) ? "SHOWN" : "not shown")})");
        StimRelease(s, own, ItemOf(s)!.Name);
    }

    /// <summary>The release: the action's sound and effects.</summary>
    void StimRelease(PlayerState s, bool own, string wargear)
    {
        if (_sound is not null && _soundTriggers?.File(wargear) is { } f)
        {
            var bindings = f.Tag(StimTuning.Action);
            if (bindings.Count > 0) { PostAll(bindings, Track(s.PlayerId, own).Emitter, own, "stim"); _stimSounds++; }
        }
        if (NoStims || _fx is null || _catalogue?.Thrown.GetValueOrDefault(wargear) is not { } fx) return;
        int id = s.PlayerId;
        foreach (var p in fx.Bindings.Where(x => x.IsDefault && x.Play is not null && x.Path.Contains("/" + StimTuning.Action + "/", StringComparison.OrdinalIgnoreCase)).Select(x => x.Play!))
        {
            if (FxPlace(id, p.Marker) is not { } place) continue;
            string marker = p.Marker;
            if (_fx.Play(p.Name, place.Position, place.Axes, p.Scale <= 0f ? 1f : p.Scale, null, p.Attached ? () => FxPlace(id, marker) : null, p.Lifetime) is not null) _stimFx++;
        }
    }

    /// <summary>
    /// Test aid (perks/inert-2): <c>--perks a[,b]</c> asks for the requested class's perks by file name (<c>mp_perk_equipment_double</c>, the
    /// <c>mp_perk_</c> prefix may be left out) or English name, over what the front end's profile asked for. An unknown name is noted and skipped.
    /// </summary>
    SmNet.ClientOptions WithPerkArgs(SmNet.ClientOptions options)
    {
        var args = OS.GetCmdlineUserArgs();
        int at = Array.IndexOf(args, "--perks");
        if (at < 0 || at + 1 >= args.Length || _config is null) return options;
        int playerClass = options.RequestedClass != SmNet.Protocol.ServerDefaultClass ? options.RequestedClass : 0;
        var choices = Loadouts.PerkChoices(_config, playerClass);
        var picked = new List<byte>();
        foreach (var raw in args[at + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string want = raw.StartsWith("mp_perk_", StringComparison.OrdinalIgnoreCase) ? raw : "mp_perk_" + raw;
            int k = -1;
            for (int i = 0; i < choices.Count && k < 0; i++)
            {
                var perk = _config.Perks[choices[i]];
                if (perk.Name.Equals(want, StringComparison.OrdinalIgnoreCase) || Perks.NameOf(perk).Equals(raw, StringComparison.OrdinalIgnoreCase)) k = i;
            }
            if (k < 0) { _notes.Add($"--perks '{raw}' is no perk of class {playerClass}; skipped"); continue; }
            picked.Add((byte)k);
            GD.Print($"--perks '{raw}': requesting perk choice {k} '{_config.Perks[choices[k]].Name}' (class {playerClass})");
        }
        return picked.Count switch
        {
            0 => options,
            1 => options with { RequestedPerk1 = picked[0] },
            _ => options with { RequestedPerk1 = picked[0], RequestedPerk2 = picked[1] },
        };
    }

    /// <summary>The bot log's stim counters.</summary>
    string StimStats() => $"stims: uses seen {_stimUsesSeen} (remote {_stimRemoteUses}), sounds {_stimSounds}, effects {_stimFx}";
}
