using System;
using System.Collections.Generic;
using Godot;
using SmNet;
using SmNet.Extras.Musou.Campaign;
using SmSim.Extras.Musou.Campaign;

namespace SmClient;

/// <summary>
/// The plasma burn on the client (the owner, 2026-11-08: Musou Mode's plasma "do burning lingering plasma damage over time"). Each tag 11 record of kind <see cref="TfwFireKind.Burn"/> marks an enemy as
/// burning for <see cref="BurnSeconds"/> from now (a later record renews it); every frame each burning enemy that is on screen gets two blue-white flame quads on his body, flickering. A placeholder
/// demonstration (the flame is a soft gradient billboard, not an original plasma effect; ours). Budgeted for crowds: one <see cref="MultiMesh"/> of <see cref="MaxQuads"/> quads in a single draw call, no
/// per-Ork node, nothing drawn beyond <see cref="DrawMetres"/> from the camera, and the table forgets an enemy as soon as his burn runs out.
/// </summary>
public partial class NetworkView
{
    /// <summary>A burn is drawn this long after its latest record: the server's 3 s plus the second between renewals (ours).</summary>
    internal const double BurnSeconds = 3.5;
    const int MaxQuads = 192;
    const float DrawMetres = 70f;

    readonly Dictionary<ushort, double> _tfwBurning = new();
    MultiMeshInstance3D? _tfwFlames;
    MultiMesh? _tfwFlameMesh;
    int _tfwBurnRecords, _tfwFlamesDrawn;

    /// <summary>Enemies the client has been told are burning (a test aid's count).</summary>
    internal int TfwBurnRecords => _tfwBurnRecords;

    static Texture2D? _flameTexture;

    /// <summary>A soft flame: white at the heart, cyan, then clear (a vertical teardrop from a radial gradient; built once).</summary>
    static Texture2D FlameTexture()
    {
        if (_flameTexture is not null) return _flameTexture;
        var gradient = new Gradient { Offsets = [0f, 0.25f, 0.6f, 1f], Colors = [new Color(1f, 1f, 1f, 1f), new Color(0.75f, 0.95f, 1f, 0.85f), new Color(0.25f, 0.55f, 1f, 0.35f), new Color(0.1f, 0.3f, 1f, 0f)] };
        return _flameTexture = new GradientTexture2D { Gradient = gradient, Fill = GradientTexture2D.FillEnum.Radial, FillFrom = new Vector2(0.5f, 0.5f), FillTo = new Vector2(0.5f, 0f), Width = 64, Height = 64 };
    }

    /// <summary>Tag 11's burn records of this frame's sections mark the burning; then the flames are drawn on the enemies shown now.</summary>
    void TfwPlasmaBurnFrame()
    {
        foreach (var section in _tfwSections)
        {
            if (section.Tag != TfwWire.FireTag || TfwWire.ReadFire(section.Payload) is not { } records) continue;
            foreach (var fire in records)
            {
                if (fire.Kind != TfwFireKind.Burn) continue;
                if (!_tfwBurning.ContainsKey(fire.Subject)) _tfwBurnRecords++;
                _tfwBurning[fire.Subject] = _connectedFor + BurnSeconds;
            }
        }
        if (_tfwBurning.Count == 0) { if (_tfwFlames is not null) _tfwFlames.Visible = false; return; }
        bool draw = !(Lean || _gameSpace is null || DisplayServer.GetName() == "headless");
        int quads = 0;
        var camera = _camera is null ? System.Numerics.Vector3.Zero : new System.Numerics.Vector3(_camera.GlobalPosition.X, -_camera.GlobalPosition.Z, _camera.GlobalPosition.Y);   // Godot to game space
        if (draw && _tfwFlames is null) BuildFlames();
        float time = (float)_connectedFor;
        for (int i = 0; i < _tfwShown.Count && draw; i++)
        {
            var e = _tfwShown[i];
            if (e.State == SmSim.Extras.Musou.MusouEnemyState.Dying || !_tfwBurning.TryGetValue(e.Id, out double until) || until < _connectedFor) continue;
            if (System.Numerics.Vector3.DistanceSquared(e.Position, camera) > DrawMetres * DrawMetres || quads + 2 > MaxQuads) continue;
            float flicker = 0.85f + 0.3f * MathF.Sin(time * 17f + e.Id * 1.7f), sway = 0.08f * MathF.Sin(time * 9f + e.Id);
            float fade = (float)Math.Min(1.0, (until - _connectedFor) / 0.5);
            _tfwFlameMesh!.SetInstanceTransform(quads, new Transform3D(Basis.FromScale(new Vector3(1.0f * flicker, 1.5f * flicker, 1f) * fade), new Vector3(e.Position.X + sway, e.Position.Y, e.Position.Z + 1.0f)));
            _tfwFlameMesh.SetInstanceColor(quads++, new Color(1f, 1f, 1f, fade));
            _tfwFlameMesh.SetInstanceTransform(quads, new Transform3D(Basis.FromScale(new Vector3(0.65f * flicker, 1.0f * flicker, 1f) * fade), new Vector3(e.Position.X - sway, e.Position.Y, e.Position.Z + 1.7f)));
            _tfwFlameMesh.SetInstanceColor(quads++, new Color(0.8f, 0.95f, 1f, fade));
        }
        _tfwFlamesDrawn = quads;
        if (_tfwFlames is not null) { _tfwFlameMesh!.VisibleInstanceCount = quads; _tfwFlames.Visible = quads > 0; }
        if (_tfwBurning.Count > 256 || time % 5f < 0.05f)
            foreach (var id in new List<ushort>(_tfwBurning.Keys)) if (_tfwBurning[id] < _connectedFor) _tfwBurning.Remove(id);
    }

    void BuildFlames()
    {
        var paint = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, BlendMode = BaseMaterial3D.BlendModeEnum.Add,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled, BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled, BillboardKeepScale = true, AlbedoTexture = FlameTexture(),
            VertexColorUseAsAlbedo = true, DisableReceiveShadows = true,
        };
        _tfwFlameMesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true, Mesh = new QuadMesh { Size = Vector2.One, Material = paint }, InstanceCount = MaxQuads, VisibleInstanceCount = 0 };
        _tfwFlames = new MultiMeshInstance3D { Multimesh = _tfwFlameMesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, ExtraCullMargin = 4096f, Name = "TfwPlasmaFlames" };
        _gameSpace!.AddChild(_tfwFlames);
    }
}
