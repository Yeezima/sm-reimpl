using System;
using System.Collections.Generic;
using Godot;
using SmClient.ImpactRules;
using SmSim;

namespace SmClient;

/// <summary>
/// fx/weapon-visuals (owner, 2026-11-14; client presentation only, Classic and Musou; rules: <see cref="PowerWeaponGlow"/>): while a power weapon is in hand it throws the thunder hammer's light (blue; the
/// hammer's own comes from the data and is left as it is), and the combat knife of a wielder with the Serrated Combat Blade throws the same light in green and wears a green power-field overlay
/// (<see cref="PowerGlow.GreenField"/>). Test aids: <c>--no-power-light</c> (no added light, no knife field: the look before), <c>--log-power-glow</c>.
/// </summary>
public partial class NetworkView
{
    sealed class PowerLightSet { public PowerGlowColour Colour; public string Marker = ""; public FxEffect? Effect; }
    readonly Dictionary<int, PowerLightSet> _powerLights = new();
    readonly Dictionary<int, MeshInstance3D> _knifeField = new();
    readonly List<int> _powerGone = new();
    int _powerLightsStarted, _knifeFields;

    /// <summary>Capture aid <c>--hold-melee</c>: the own melee weapon stays in hand (use with <c>--spectate inspect-melee</c>).</summary>
    bool HoldMeleeDemo => _holdMelee ??= CmdFlag("hold-melee");
    bool? _holdMelee;

    bool NoPowerLight => _noPowerLight ??= CmdFlag("no-power-light");
    bool? _noPowerLight;

    /// <summary>Once per player per frame (from <see cref="MeleeFxFor"/>): starts, keeps or stops the light and the knife's field.</summary>
    void PowerGlowFrame(PlayerState s, CharacterViewer? character)
    {
        string? raw = MeleeWargear(s);   // the install's name, not PresentedAs: Musou's claw and spear are not power weapons here
        bool serrated = _config is not null && Loadouts.Upgrades(_config, s).Has(PowerWeaponGlow.SerratedUpgrade);
        var colour = NoPowerLight || !s.Alive || character is null || !ImpactRules.MeleeIdleGuard.Allows(IsOrkVoice(s)) || character.Weapons?.MeleeMeshInHand is null
            ? PowerGlowColour.None : PowerWeaponGlow.ColourFor(raw, serrated);
        // The knife's green field: an overlay on its mesh while it is held.
        var held = colour == PowerGlowColour.Green && PowerWeaponGlow.KnifeField(raw, serrated) ? character!.Weapons?.MeleeMeshInHand : null;
        if (_knifeField.TryGetValue(s.PlayerId, out var old) && (held is not { } now || !ReferenceEquals(now.Mesh, old)))
        {
            if (GodotObject.IsInstanceValid(old)) old.MaterialOverlay = null;
            _knifeField.Remove(s.PlayerId);
        }
        if (held is { } h)
        {
            _knifeField[s.PlayerId] = h.Mesh;
            var field = PowerGlow.GreenField();
            if (!ReferenceEquals(h.Mesh.MaterialOverlay, field)) { h.Mesh.MaterialOverlay = field; _knifeFields++; }
        }
        // The light: the data's own for the hammer; ours, at the weapon's marker, for the others.
        if (colour == PowerGlowColour.None || PowerWeaponGlow.DataBindsLight(raw)) { StopPowerLight(s.PlayerId); return; }
        int id = s.PlayerId;
        if (_powerLights.TryGetValue(id, out var set) && set.Colour != colour) { StopPowerLight(id); set = null; }
        set ??= _powerLights[id] = new PowerLightSet { Colour = colour };
        if (set.Effect is { Dead: false } live) { live.Hidden = !character!.Visible; return; }
        var rig = character!.Weapons;
        if (rig is null || _fx is null) return;
        foreach (var marker in PowerWeaponGlow.LightMarkers)
        {
            if (!rig.TryMeleeMarker(marker, out var at)) continue;
            string m = marker;
            Func<(Vector3, Basis)?> follow = () =>
            {
                var c = _client is not null && id == _client.PlayerId ? _local : _remotes.GetValueOrDefault(id);
                return c?.Weapons is { } r && r.TryMeleeMarker(m, out var t) ? (t.Origin, t.Basis) : null;
            };
            if (_fx.Play(PowerGlow.LightName(colour), at.Origin, at.Basis, 1f, null, follow, 0f, 0.0, repeat: true, priority: 1) is { } e)
            {
                e.StopOnLost = true;
                set.Effect = e; set.Marker = m;
                _powerLightsStarted++;
                if (_bot || CmdFlag("log-power-glow")) GD.Print($"t {_connectedFor:F1} s: P{id} {raw} {colour} light at {m}");
            }
            break;
        }
    }

    void StopPowerLight(int id)
    {
        if (!_powerLights.Remove(id, out var set) || set.Effect is not { } e) return;
        e.Repeat = false; e.Life = Math.Min(e.Life, Math.Max(0.0, e.Age) + 0.1);
    }

    /// <summary>Players who left: their light stops and their knife overlay goes.</summary>
    void PowerGlowPrune(HashSet<int> present)
    {
        _powerGone.Clear();
        foreach (var id in _powerLights.Keys) if (!present.Contains(id)) _powerGone.Add(id);
        foreach (var id in _knifeField.Keys) if (!present.Contains(id)) _powerGone.Add(id);
        foreach (var id in _powerGone) { StopPowerLight(id); _knifeField.Remove(id); }
    }
}
