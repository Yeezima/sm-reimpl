using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SmContent;
using SmSim;

namespace SmClient;

/// <summary>
/// M40-T13: the Chaos eye glow in the match (<see cref="EyeGlow"/>, <see cref="EyeGlowData"/>). Every living player whose deployed weapon binds
/// <c>csm_mp_eye_*</c> in its <c>while_deployed_actions</c> (the data) shows it at the helmet's eye markers; the melee weapon counts as deployed during a
/// melee attack and always for the classes that hold it all the time (as the weapon models do). The lens colour of the player's look tints it (ours).
/// The wargear files are read once in the background when the effects are ready. Called once per frame from <see cref="UpdateFx"/>.
/// </summary>
public partial class NetworkView
{
    Task<(EyeGlowData? Data, Dictionary<string, CSystem> Systems)>? _eyeLoad;
    EyeGlowData? _eyeData;
    readonly Dictionary<int, EyeGlow> _eyeGlows = new();
    readonly HashSet<int> _eyeSeen = new();

    void FxEyeGlow()
    {
        if (_fx is null || _config is null || _client is null || _assets is null || EyeGlow.Off) return;
        if (_eyeData is null)
        {
            if (_eyeLoad is null)
            {
                if (CmdFlag("no-eye-glow")) { EyeGlow.Off = true; GD.Print("eye glow: switched off (--no-eye-glow)"); return; }
                // After the map's effects (they compile from the same library on a worker; one reader at a time).
                if (_fxMapLoad is not null || _fxMapSpawned != _client.MapName) return;
                string dir = _gameDir;
                var library = _fx.Library;
                var boneNames = _assets.Rig.BoneNames;
                var names = _config.Weapons.Select(w => w.Name).Concat(_config.Classes.SelectMany(c => c.MeleeChoices.Select(m => m.Wargear))).ToList();
                _eyeLoad = Task.Run(() =>
                {
                    try
                    {
                        var data = EyeGlowData.Load(dir, names);
                        // The melee weapons' eye effects are bound by no ranged weapon, so the shot effects' load did not compile them.
                        var systems = library is null ? new Dictionary<string, CSystem>()
                            : CompileSystems(library, data.Wargear.SelectMany(w => data.For(w)).Select(p => p.Name).Distinct(StringComparer.Ordinal), new List<string>());
                        // Every helmet's eye markers here, so no frame opens a package (a helmet missed here is read in the background when first seen).
                        var notes = new List<string>();
                        EyeGlow.PreloadMarkers(dir, EyeGlow.HelmetPackages(dir), boneNames, notes);
                        foreach (var n in notes) GD.PrintErr(n);
                        return (data, systems);
                    }
                    catch (Exception ex) when (ex is System.IO.IOException or System.IO.InvalidDataException or NotSupportedException or UnauthorizedAccessException)
                    { GD.PrintErr($"eye glow: wargear unreadable: {ex.Message}; the glow is off"); return ((EyeGlowData?)null, new Dictionary<string, CSystem>()); }
                });
            }
            if (!_eyeLoad.IsCompleted) return;
            if (_eyeLoad.IsFaulted)
            {
                GD.PrintErr($"eye glow: loading failed ({_eyeLoad.Exception?.GetBaseException().GetType().Name}: {_eyeLoad.Exception?.GetBaseException().Message}); the glow is off");
                _notes.Add("eye glow: " + _eyeLoad.Exception?.GetBaseException().Message);
                EyeGlow.Off = true; return;
            }
            if (_eyeLoad.Result.Data is null) { EyeGlow.Off = true; return; }
            (_eyeData, var compiled) = _eyeLoad.Result;
            _fx.Preload(compiled, new Dictionary<string, byte[]>());
            GD.Print($"eye glow: {_eyeData!.Wargear.Count} wargear files bind an eye effect ({string.Join(", ", _eyeData.Wargear.Order(StringComparer.Ordinal))}); {compiled.Count} effects compiled");
        }
        _eyeSeen.Clear();
        EyeGlowFor(_client.PlayerId, _local, _client.Predicted);
        foreach (var state in _lastOthers) EyeGlowFor(state.PlayerId, _remotes.GetValueOrDefault(state.PlayerId), state);
        foreach (var id in _eyeGlows.Keys.Where(id => !_eyeSeen.Contains(id)).ToList()) { _eyeGlows[id].Stop(); _eyeGlows.Remove(id); }
    }

    void EyeGlowFor(int id, CharacterViewer? character, PlayerState state)
    {
        if (character is null || id <= 0) return;
        _eyeSeen.Add(id);
        if (!_eyeGlows.TryGetValue(id, out var glow)) _eyeGlows[id] = glow = new EyeGlow();
        string? melee = MeleeWargear(state);
        bool meleeInHand = state.MeleeStep > 0 || character.Weapons?.AlwaysMelee == true;
        string? deployed = meleeInHand && _eyeData!.For(melee).Count > 0 ? melee : Loadouts.InHand(_config!, state)?.Name;
        var lens = _playerLooks.TryGetValue(id, out var look) ? look.Entry.Look.Lens : null;
        glow.Update(_fx!, _gameDir, _eyeData!, character, deployed, state.Alive && character.Visible, EyeGlow.TintOf(lens));
        if ((_bot || CmdFlag("look-log")) && _eyeLogged.GetValueOrDefault(id) != glow.Key)
        {
            _eyeLogged[id] = glow.Key;
            GD.Print($"t {_connectedFor:F1} s: P{id} eye glow: {(glow.Key.Length == 0 ? "none" : glow.Key)} (weapon {deployed ?? "-"}, lens {(lens is { } l ? $"{l.R},{l.G},{l.B}" : "none")})");
        }
    }
    readonly Dictionary<int, string> _eyeLogged = new();
}
