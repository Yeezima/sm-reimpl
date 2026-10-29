using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmContent;

namespace SmClient;

/// <summary>
/// M40-T13 test aid for <c>--appearance-test ... --eye-glow [wargear]</c>: an effect player and the character's <see cref="EyeGlow"/>, as the match drives them,
/// with <paramref name="wargear"/> deployed (default the Chaos bolter, <c>csm_mp_ranged_bolter</c>). Reads the effects package and the wargear file read-only.
/// </summary>
public partial class EyeGlowTest : Node3D
{
    readonly string _gameDir, _wargear;
    readonly CharacterViewer _character;
    readonly (byte R, byte G, byte B)? _lens;
    readonly Camera3D _camera;
    FxPlayer? _fx;
    EyeGlowData? _data;
    readonly EyeGlow _glow = new();
    bool _reported;
    double _age;

    public EyeGlowTest(string gameDir, CharacterViewer character, (byte R, byte G, byte B)? lens, string? wargear, Camera3D camera)
    {
        _gameDir = gameDir; _character = character; _lens = lens; _camera = camera;
        _wargear = string.IsNullOrEmpty(wargear) ? "csm_mp_ranged_bolter" : wargear;
    }
    public EyeGlowTest() : this("", null!, null, null, null!) { }

    public override void _Ready()
    {
        try
        {
            var library = FxLibrary.Load(_gameDir);
            _fx = new FxPlayer { Name = "Effects" };
            AddChild(_fx);
            if (!_fx.Initialize(library, _gameDir)) { GD.PrintErr("eye glow test: " + _fx.Status); return; }
            _data = EyeGlowData.Load(_gameDir, [_wargear]);
            var names = _data.For(_wargear).Select(p => p.Name).Distinct(StringComparer.Ordinal).ToList();
            var failures = new List<string>();
            _fx.Preload(NetworkView.CompileSystems(library, names, failures), new Dictionary<string, byte[]>());
            if (EyeGlow.HelmetOf(_character) is { } helmet && _character.Assets is { } assets) EyeGlow.PreloadMarkers(_gameDir, [helmet], assets.Rig.BoneNames, failures);
            GD.Print($"eye glow test: {_wargear} binds [{string.Join(", ", _data.For(_wargear).Select(p => p.Name + "@" + p.Marker))}]{(failures.Count > 0 ? "; not parsed: " + string.Join(", ", failures) : "")}; lens tint {(EyeGlow.TintOf(_lens) is { } t ? t.ToHtml(false) : "none (data colour)")}");
        }
        catch (Exception ex) when (ex is System.IO.IOException or System.IO.InvalidDataException or NotSupportedException or UnauthorizedAccessException)
        {
            GD.PrintErr("eye glow test: " + ex.Message);
            _fx = null;
        }
    }

    public override void _Process(double delta)
    {
        if (_fx is null || _data is null || !GodotObject.IsInstanceValid(_character)) return;
        _age += delta;
        _glow.Update(_fx, _gameDir, _data, _character, _wargear, true, EyeGlow.TintOf(_lens));
        _fx.Update(delta, _camera);
        if (!_reported && _age > 0.5) { _reported = true; GD.Print($"eye glow test: {_glow.Live} effects live ({_glow.Key}), {_fx.LastQuads} quads; helmet {EyeGlow.HelmetOf(_character) ?? "none"}"); }
    }
}
