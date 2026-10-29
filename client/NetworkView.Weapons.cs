using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmContent;
using SmSim;

namespace SmClient;

/// <summary>
/// M24: the weapons on the characters. Every player (own and remote, Space Marine and Chaos) carries the weapons of its loadout:
/// the one in hand (the simulation's current slot) on its wargear's wield mount, the other ranged weapon on its stow marker, the
/// melee weapon in the right hand during an attack (always for the jump pack classes, Assault and Raptor) and stowed or hidden
/// otherwise; jump packs and the Devastator backpack are worn. Models, mounts and markers are the original's
/// (<see cref="WeaponModels"/>, <see cref="WeaponRig"/>); when a weapon changes hands, and the melee rule, are interpretations.
/// </summary>
public partial class NetworkView
{
    /// <summary><c>--no-weapons</c> (test aid): no weapon models are drawn (frame time baseline).</summary>
    public bool NoWeapons { get; init; }

    WeaponAssets? _weaponAssets;
    string _weaponNote = "not loaded";
    // Frame time statistics (s) for the log and the F1 line.
    double _wFrameSum, _wFrameMax;
    int _wFrames;
    double _wLogAt;

    /// <summary>Reads the weapon catalogue for every weapon, melee choice and jump pack of the configuration (~0.1 s); failures are noted and no weapon is drawn.</summary>
    void SetUpWeapons()
    {
        if (_config is null || _assets is null) return;
        if (NoWeapons || Lean) { _weaponNote = Lean ? "not loaded (lean bot)" : "switched off (--no-weapons)"; return; }   // perf/lean-bots
        try
        {
            var names = _config.Weapons.Select(w => w.Name)
                .Concat(_config.Classes.SelectMany(c => c.MeleeChoices.Select(m => m.Wargear)))
                .Concat(_config.JumpPacks.Select(j => j.Name));
            var notes = new List<string>();
            _weaponAssets = WeaponAssets.Load(_gameDir, _assets, names, notes);
            foreach (var n in notes) { _notes.Add(n); GD.Print("  note: " + n); }
            _weaponNote = $"{_weaponAssets.Catalogue.ByWargear.Count} wargear files, {_weaponAssets.Catalogue.BodyMarkers.Count} body markers, catalogue {_weaponAssets.CatalogueMilliseconds:F0} ms";
            GD.Print("weapons: " + _weaponNote);
        }
        catch (Exception ex) when (ex is not (StackOverflowException or AccessViolationException or OutOfMemoryException))
        {
            _weaponAssets = null;
            _weaponNote = "not available: " + ex.Message;
            _notes.Add("weapon models: " + ex.Message + "; no weapons are drawn");
            GD.Print("weapons: " + _weaponNote);
        }
    }

    /// <summary>
    /// Per player and frame: (re)builds the character's weapon nodes when its loadout changed and puts each weapon in hand or on its
    /// stow marker for the state.
    /// </summary>
    void SyncWeapons(CharacterViewer character, PlayerState state, float delta)
    {
        bool ork = IsOrkCharacter(character);   // Ork O7a: the boy's own prop bones and weapon models (WeaponAssets of the Ork rig), the stikkbomb in hand while throwing
        var assets = ork ? OrkWeaponAssets() : _weaponAssets;
        if (assets is null || _config is null || character.Skeleton is null || state.PlayerId <= 0) return;   // id 0: the state before the first snapshot
        var rig = character.Weapons ??= new WeaponRig(character.Skeleton, assets);
        if (rig.Faulted) return;
        try
        {
            int primary = Loadouts.WeaponIndex(_config, state, WeaponSlot.Ranged), pistol = Loadouts.WeaponIndex(_config, state, WeaponSlot.Pistol);
            string? melee = MeleeWargear(state);
            var jump = Loadouts.JumpPack(_config, state);
            if (rig.Configure((state.Class, primary, pistol, melee, jump?.Name), primary >= 0 ? _config.Weapons[primary].Name : null,
                    pistol >= 0 ? _config.Weapons[pistol].Name : null, melee, jump is null ? Array.Empty<string?>() : new string?[] { jump.Name }, jump is not null,
                    ork ? OrkWeaponMounts.Stikkbomb : null))
            {
                // The armour's own backpack would sit inside a worn pack model (jump packs are drawn over the same bones).
                character.SetPieceHidden("_backpack", rig.WornCount > 0);
                if (_bot || ReferenceEquals(character, _local))
                    GD.Print($"weapons of P{state.PlayerId}: {string.Join(", ", rig.Description)}{(rig.WornCount > 0 ? $"; {rig.WornCount} worn model node(s)" : "")}{(rig.AlwaysMelee ? "; melee weapon always in hand" : "")}");
            }
            if (ork) OrkWeaponState(rig, state);
            rig.SetPackPaint(character.Assets?.PackPaint);   // the jump pack wears the look's chest paint (WornPackPaint, the owner's rule); the placeholder and the single-set path keep the side's paint
            if ((_bot || CmdFlag("look-log")) && rig.WornCount > 0 && rig.PackPrimaryColour is { } packColour && _packLogged.GetValueOrDefault(state.PlayerId) != packColour)
            {
                _packLogged[state.PlayerId] = packColour;
                GD.Print($"t {_connectedFor:F1} s: P{state.PlayerId}'s jump pack is drawn in {packColour.ToHtml(false)} (chest paint {(rig.PackPaint?.Primary?.Name ?? "none: the side's default")})");
            }
            var (release, take) = ork ? (0.5f, 0.5f) : SwapFractionsFor(state);   // fix/stow-right: the old weapon leaves the hand on its marker, the new one comes at its draw's tag
            rig.Update(state.Slot, state.Alive && state.MeleeStep > 0, state.Alive, delta, _config.WeaponSwitchTime, release, take);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            // One failing player must not stop the frame (camera, UI, deaths of the others): drop this rig's weapons, say so, go on.
            rig.Fault();
            if (_weaponFaults++ < 5) GD.PrintErr($"weapons of P{state.PlayerId} failed: {ex.GetType().Name}: {ex.Message}; no weapons are drawn on this character");
            _notes.Add($"weapons of P{state.PlayerId}: {ex.GetType().Name}: {ex.Message}");
        }
        if (!rig.Faulted) ReloadSyncSafe(character, rig, state, ReferenceEquals(character, _local), delta);   // M35 (NetworkView.Reload.cs)
    }

    int _weaponFaults;
    readonly Dictionary<int, Color> _packLogged = new();   // per player: the pack colour last logged (--bot / --look-log)

    /// <summary>
    /// The muzzle of the weapon in a player's hand: its position and the barrel direction in Godot world space (the weapon model's
    /// <c>fx_Muzzle</c> locator, its +Y axis), or null when the player has no ranged weapon in hand, no model for it or is not shown.
    /// For M25 (muzzle effects, tracers).
    /// </summary>
    public (Vector3 Position, Vector3 Direction)? MuzzleWorld(int playerId)
    {
        CharacterViewer? character = _client is not null && playerId == _client.PlayerId ? _local : _remotes.GetValueOrDefault(playerId);
        if (character?.Weapons is { } rig && rig.TryMuzzle(out var position, out var direction)) return (position, direction);
        return null;
    }

    /// <summary>A named locator of the weapon in a player's hand (<see cref="WeaponRig.TryMarker"/>) in Godot world space, or null.</summary>
    public Transform3D? MarkerWorld(int playerId, string name)
    {
        CharacterViewer? character = _client is not null && playerId == _client.PlayerId ? _local : _remotes.GetValueOrDefault(playerId);
        return character?.Weapons is { } rig && rig.TryMarker(name, out var world) ? world : null;
    }

    /// <summary>Frame-time bookkeeping for the weapons log (called every frame with the delta).</summary>
    void WeaponFrame(double delta)
    {
        _wFrames++; _wFrameSum += delta; _wFrameMax = Math.Max(_wFrameMax, delta);
        if ((_bot || _showInfo) && _connectedFor - _wLogAt >= 10.0 && _wFrames > 60)
        {
            _wLogAt = _connectedFor;
            GD.Print($"t {_connectedFor:F0} s: frame time avg {_wFrameSum / _wFrames * 1000:F1} ms, max {_wFrameMax * 1000:F1} ms over {_wFrames} frames; {WeaponStats()}; {MuzzleLog()}; {ReloadStats()}; {StimStats()}; {FallStats()}; {UpperStats()}; {SprintEdgeStats()}");
            _wFrameSum = 0; _wFrameMax = 0; _wFrames = 0;
        }
    }

    /// <summary>The own muzzle against the simulation's aim direction (log): position and the angle between the barrel and the aim.</summary>
    string MuzzleLog()
    {
        if (_client is null || _gameSpace is null || MuzzleWorld(_client.PlayerId) is not { } m) return "own muzzle: none";
        var me = _client.Predicted;
        var aim = Combat.AimDirection(me.Yaw, me.Pitch);
        var aimWorld = (_gameSpace.Basis * new Vector3(aim.X, aim.Y, aim.Z)).Normalized();
        var feet = _gameSpace.Transform * new Vector3(me.Position.X, me.Position.Y, me.Position.Z);
        float angle = Mathf.RadToDeg(m.Direction.AngleTo(aimWorld));
        return $"own muzzle {(m.Position - feet).Length():F2} m from the feet, {m.Position.Y - feet.Y:F2} m up, barrel {angle:F0} deg off the aim (pitch {Mathf.RadToDeg(me.Pitch):F0}, aiming {me.Aiming})";
    }

    string WeaponStats()
    {
        var all = _remotes.Values.Append(_local).Where(c => c?.Weapons is not null).ToList();
        return $"weapon models loaded {_weaponAssets?.ModelsLoaded ?? 0} ({_weaponAssets?.LoadMilliseconds ?? 0:F0} ms in total), weapon nodes {all.Sum(c => c!.Weapons!.WeaponCount)} + worn {all.Sum(c => c!.Weapons!.WornCount)} on {all.Count} characters";
    }

    /// <summary>The F1 line about the weapons.</summary>
    string WeaponInfoLine() =>
        $"Weapons: original models and mounts (M24); hand/stow timing and assault melee in hand: interpretation. {WeaponStats()}" +
        (_weaponAssets is null ? $" ({_weaponNote})" : "") +
        (_wFrames > 0 ? $"; frame {_wFrameSum / _wFrames * 1000:F1} ms avg" : "") +
        (MuzzleWorld(_client?.PlayerId ?? -1) is { } m ? $"; own muzzle at {m.Position.X:F1}, {m.Position.Y:F1}, {m.Position.Z:F1}" : "");
}
