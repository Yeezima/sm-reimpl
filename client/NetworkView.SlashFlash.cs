using System;
using Godot;
using SmSim;

namespace SmClient;

/// <summary>
/// fx/air-slash-landing (owner, 2026-10-31: the power sword's and chainsword's air slash landing "should appear and feel exactly like" the reference video r5; client presentation only).
/// The reference (8 frames): the pack burns purple in the dive; at the landing a huge blue-white energy burst blooms over the impact and much of the screen (white at its peak, then the
/// marine washed cyan), the ground pound's yellow chevrons show at the spot, and it is gone about 0.4 s later. The data binds no effect of its own to the MP slash; the effect that
/// matches is the jump pack's perk landing <c>jumppack_land_extreme_mp</c> (<c>actn_gp_hit</c>'s perk entry, scale 1.4): an <c>add_point_cool</c> flash bloom 9.9 m (x scale) in radius, a flash ball,
/// directional streaks, bright spikes, a lit skirt, ground flashes and a shock ring, a light. It plays here at the data's 1.4 without its crater and crack decals (<see cref="FxEffect.NoDecals"/>).
/// Ours, estimates (spec "Air slash landing flash"): the blue-white point light (the power sword's light colour, 115 186 255, 14 m, 0.4 s) and the own view's screen flash
/// (white at a close landing fading to cyan, 0.4 s, by distance). <c>--no-slash-flash</c> restores the previous look.
/// </summary>
public partial class NetworkView
{
    /// <summary>The data's perk landing effect and its scale (wargear <c>actn_gp_hit</c>, the entry requiring <c>mp_perk_jump_pack_ground_pound_damage</c>).</summary>
    const string SlashFlashEffect = "jumppack_land_extreme_mp";
    const float SlashFlashScale = 1.4f;
    /// <summary>Ours: the flash's life (s), the light's range (m), its peak energy, the screen flash's reach (m) and peak.</summary>
    const float SlashFlashSeconds = 0.4f, SlashFlashLightRange = 14f, SlashFlashLightEnergy = 8f, SlashFlashScreenReach = 14f, SlashFlashScreenPeak = 0.6f;

    /// <summary>Ours (the reference shows only blue-white; the data's burst is orange-white): the effect's hue.</summary>
    static readonly Color SlashFlashTint = new(0.5f, 0.78f, 1f);

    bool NoSlashFlash => _noSlashFlash ??= CmdFlag("no-slash-flash");
    bool? _noSlashFlash;

    MeshInstance3D? _slashCore;
    ShaderMaterial? _slashCoreMaterial;
    const float SlashCoreRadius = 2.1f;
    const string SlashCoreShader = @"
shader_type spatial;
render_mode unshaded, blend_add, cull_disabled, depth_draw_never, depth_test_disabled, fog_disabled;
uniform float strength = 0.0;
void vertex() {
    MODELVIEW_MATRIX = VIEW_MATRIX * mat4(INV_VIEW_MATRIX[0], INV_VIEW_MATRIX[1], INV_VIEW_MATRIX[2], MODEL_MATRIX[3]);
    MODELVIEW_MATRIX = MODELVIEW_MATRIX * mat4(vec4(MODEL_MATRIX[0].x,0.0,0.0,0.0), vec4(0.0,MODEL_MATRIX[1].y,0.0,0.0), vec4(0.0,0.0,MODEL_MATRIX[2].z,0.0), vec4(0.0,0.0,0.0,1.0));
}
void fragment() {
    float r = length(UV - vec2(0.5)) * 2.0;
    float core = clamp(1.0 - r * 1.25, 0.0, 1.0);
    float rim = exp(-pow((r - 0.62) / 0.28, 2.0));
    vec3 rimc = vec3(0.35, 0.72, 1.0) * rim * 0.9;
    ALBEDO = (vec3(1.0) * pow(core, 0.7) * 3.0 + rimc) * strength * (1.0 - smoothstep(0.85, 1.0, r));
}";

    OmniLight3D? _slashLight;
    double _slashFlashStart = -10;
    float _slashFlashScreen;
    ColorRect? _slashScreen;
    ShaderMaterial? _slashScreenMaterial;

    const string SlashScreenShader = @"
shader_type canvas_item;
render_mode blend_add;
uniform float strength = 0.0;
void fragment() {
    vec2 d = UV - vec2(0.5, 0.45);
    float core = exp(-dot(d, d) * 5.0);
    vec3 cyan = vec3(0.28, 0.72, 1.0);
    vec3 white = vec3(1.0);
    vec3 c = mix(cyan, white, clamp(strength * 1.8 - 0.5, 0.0, 1.0) * (0.35 + 0.65 * core));
    COLOR = vec4(c * strength * (0.55 + 0.45 * core), 1.0);
}";

    /// <summary>The landing of an air slash at <paramref name="point"/> (Godot space): the data's burst, the light and, for a landing near the camera, the screen flash.</summary>
    void SlashFlashAdd(Vector3 point, Vector3 normal, bool own)
    {
        if (NoSlashFlash || _fx is null) return;
        var up = normal.LengthSquared() < 1e-6f ? Vector3.Up : normal.Normalized();
        var e = _fx.Play(SlashFlashEffect, point + up * 0.02f, AxesFromY(up), SlashFlashScale, null, null, 0f, 0.0, false, GroundPoundFx.Priority + 1, tint: SlashFlashTint);
        if (e is not null)
        {
            e.Collisions = true; e.SoftGround = true; e.NoDecals = true;
            e.Life = Math.Max(e.Life, e.System.CollisionLife + 0.1f);
        }
        _slashFlashStart = _connectedFor;
        if (_slashCore is null || !IsInstanceValid(_slashCore))
        {
            _slashCoreMaterial = new ShaderMaterial { Shader = new Shader { Code = SlashCoreShader } };
            _slashCore = new MeshInstance3D { Name = "SlashFlashCore", Mesh = new QuadMesh { Size = Vector2.One }, MaterialOverride = _slashCoreMaterial, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, TopLevel = true, ExtraCullMargin = 16f };
            AddChild(_slashCore);
        }
        _slashCore.GlobalPosition = point + up * 2.6f;
        _slashCore.Scale = new Vector3(SlashCoreRadius * 2f, SlashCoreRadius * 2f, SlashCoreRadius * 2f);
        SlashShotsBegin();
        if (_slashLight is null || !IsInstanceValid(_slashLight))
        {
            _slashLight = new OmniLight3D { Name = "SlashFlashLight", TopLevel = true, ShadowEnabled = false, LightColor = new Color(115 / 255f, 186 / 255f, 1f), OmniAttenuation = 1.0f, OmniRange = SlashFlashLightRange };
            AddChild(_slashLight);
        }
        _slashLight.GlobalPosition = point + up * 1.2f;
        _slashLight.Visible = true;
        float dist = _camera is { } cam ? cam.GlobalPosition.DistanceTo(point) : 0f;
        _slashFlashScreen = Math.Clamp(1f - (dist - 3f) / SlashFlashScreenReach, 0f, 1f) * SlashFlashScreenPeak;
        if (_bot || SlashShotsDir is not null) GD.Print($"slash flash: effect {(e is null ? "NOT STARTED" : e.System.Name)}, screen {_slashFlashScreen:F2}, camera {dist:F1} m away");
        if (_slashScreen is null)
        {
            var layer = new CanvasLayer { Name = "SlashFlashScreen", Layer = 1 };
            _slashScreenMaterial = new ShaderMaterial { Shader = new Shader { Code = SlashScreenShader } };
            _slashScreen = new ColorRect { Name = "SlashScreen", Material = _slashScreenMaterial, MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false, Color = Colors.White };
            _slashScreen.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            layer.AddChild(_slashScreen);
            AddChild(layer);
        }
    }

    /// <summary>Each frame: the light's and the screen flash's fall.</summary>
    void UpdateSlashFlash()
    {
        double age = _connectedFor - _slashFlashStart;
        bool live = age >= 0 && age < SlashFlashSeconds;
        if (_slashLight is not null && IsInstanceValid(_slashLight))
        {
            float k = live ? MathF.Pow(1f - (float)(age / SlashFlashSeconds), 2f) * Math.Clamp((float)(age / 0.02), 0f, 1f) : 0f;
            _slashLight.LightEnergy = SlashFlashLightEnergy * k;
            _slashLight.Visible = live;
        }
        if (_slashCore is not null && _slashCoreMaterial is not null && IsInstanceValid(_slashCore))
        {
            float k = live ? MathF.Pow(1f - (float)(age / (SlashFlashSeconds * 0.7)), 1.5f) * Math.Clamp((float)(age / 0.02), 0f, 1f) : 0f;
            _slashCore.Visible = live && k > 0.002f;
            _slashCore.Scale = Vector3.One * SlashCoreRadius * 2f * (0.7f + 0.3f * Math.Clamp((float)(age / 0.1), 0f, 1f));
            _slashCoreMaterial.SetShaderParameter("strength", k);
        }
        if (_slashScreen is not null && _slashScreenMaterial is not null)
        {
            float s = live ? _slashFlashScreen * MathF.Pow(1f - (float)(age / SlashFlashSeconds), 1.6f) * Math.Clamp((float)(age / 0.03), 0f, 1f) : 0f;
            _slashScreen.Visible = s > 0.002f;
            if (_slashScreen.Visible) _slashScreenMaterial.SetShaderParameter("strength", s);
        }
    }
}

public partial class NetworkView
{
    /// <summary><c>--slash-shots &lt;dir&gt;</c> (capture aid, a windowed client): saves the frames at <see cref="SlashShotTimes"/> seconds after each air slash landing it shows as <c>slash&lt;n&gt;_&lt;ms&gt;.png</c>; at most <c>--slash-shots-max</c> (default 2) landings.</summary>
    static readonly string? SlashShotsDir = OS.GetCmdlineUserArgs() is var sa && Array.IndexOf(sa, "--slash-shots") is int si && si >= 0 && si + 1 < sa.Length ? sa[si + 1] : null;
    static readonly int SlashShotsMax = (int)CmdFloat("--slash-shots-max", 2f);
    static readonly double[] SlashShotTimes = [0.0, 0.04, 0.09, 0.13, 0.2, 0.26, 0.39, 0.52, 0.65];
    int _slashShotSet, _slashShotNext = int.MaxValue;
    double _slashShotAt;

    void SlashShotsBegin()
    {
        if (SlashShotsDir is null || _slashShotSet >= SlashShotsMax) return;
        _slashShotSet++; _slashShotAt = _connectedFor; _slashShotNext = 0;
    }

    void SlashShotsFrame()
    {
        if (SlashShotsDir is null || _slashShotNext >= SlashShotTimes.Length) return;
        double since = _connectedFor - _slashShotAt;
        if (since + 1e-6 < SlashShotTimes[_slashShotNext]) return;
        System.IO.Directory.CreateDirectory(SlashShotsDir);
        string path = System.IO.Path.Combine(SlashShotsDir, $"slash{_slashShotSet}_{(int)Math.Round(since * 1000):D4}.png");
        var err = GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print(err == Error.Ok ? $"slash shot -> {path}" : $"slash shot failed: {err}");
        while (_slashShotNext < SlashShotTimes.Length && SlashShotTimes[_slashShotNext] <= since + 1e-6) _slashShotNext++;
    }
}
