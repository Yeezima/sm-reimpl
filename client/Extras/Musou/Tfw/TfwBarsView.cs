using System;
using System.Collections.Generic;
using Godot;
using SmNet.Extras.Musou;
using SmNet.Extras.Musou.Campaign;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// The elites' health bars (Musou Mode pass 2, item 14; the owner: "a health bar above elite's heads. it's always visible"). One MultiMesh of billboard quads drawn by one small shader: a dark
/// frame, a dark back and a red fill as long as the elite's health (the instance's custom data), a hand over its head. Up to <see cref="TfwBars.PerClient"/> instances, one draw call; no
/// per-bar node, no occlusion work. The bar is depth-tested (a wall hides it) and grows with distance (about 14 m is its true size, 2.5 times that at most) so it stays readable across a
/// swarm. Placeholder demonstration: the look and the sizes are ours (spec "Elite health bars"). Lives in game space (Z up, metres).
/// </summary>
public partial class TfwBarsView : Node3D
{
    /// <summary>A bar's width and height in metres at its true size (ours).</summary>
    public const float Width = 1.15f, Height = 0.14f;
    /// <summary>Metres above the top of the head the bar floats (ours).</summary>
    public const float Above = 0.32f;

    const string Shader = """
        shader_type spatial;
        render_mode unshaded, cull_disabled, blend_mix, depth_draw_never;
        uniform vec2 size = vec2(1.15, 0.14);
        uniform float true_size_metres = 14.0;
        uniform float max_grow = 2.5;
        varying float health;
        void vertex() {
            health = INSTANCE_CUSTOM.x;
            float d = distance(MODEL_MATRIX[3].xyz, CAMERA_POSITION_WORLD);
            float grow = clamp(d / true_size_metres, 1.0, max_grow);
            VERTEX.xy *= size * grow * INSTANCE_CUSTOM.y;
            MODELVIEW_MATRIX = VIEW_MATRIX * mat4(INV_VIEW_MATRIX[0], INV_VIEW_MATRIX[1], INV_VIEW_MATRIX[2], MODEL_MATRIX[3]);
        }
        void fragment() {
            vec2 p = vec2(UV.x, 1.0 - UV.y);
            float t = 0.16;                       // the frame, as a share of the bar's height
            float tx = t * size.y / size.x;
            vec3 col; float a;
            if (p.x < tx || p.x > 1.0 - tx || p.y < t || p.y > 1.0 - t) { col = vec3(0.03); a = 0.95; }
            else {
                float f = (p.x - tx) / (1.0 - 2.0 * tx);
                if (f <= health) { col = mix(vec3(0.75, 0.04, 0.03), vec3(1.0, 0.22, 0.1), smoothstep(0.0, 1.0, (p.y - t) / (1.0 - 2.0 * t))); a = 1.0; }
                else { col = vec3(0.16, 0.02, 0.02); a = 0.8; }
            }
            ALBEDO = col;
            ALPHA = a;
        }
        """;

    readonly MultiMesh _multi;
    readonly MultiMeshInstance3D _instance;

    /// <summary>Bars drawn this frame.</summary>
    public int Shown => _multi.VisibleInstanceCount;

    public TfwBarsView()
    {
        Name = "TfwBars";
        _multi = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData = true, Mesh = new QuadMesh { Size = Vector2.One }, InstanceCount = TfwBars.PerClient, VisibleInstanceCount = 0 };
        _instance = new MultiMeshInstance3D
        {
            Name = "Bars", Multimesh = _multi, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, CustomAabb = new Aabb(new Vector3(-3000f, -3000f, -500f), new Vector3(6000f, 6000f, 1000f)),
            MaterialOverride = new ShaderMaterial { Shader = new Shader { Code = Shader } },
        };
        AddChild(_instance);
        Visible = false;
    }

    /// <summary>
    /// Draws the bars the server named for the elites the crowd shows. A bar whose elite the crowd does not show (not seen yet, or gone) is skipped; a dying one has none. Returns how many were drawn.
    /// </summary>
    public int Show(IReadOnlyList<TfwBar> bars, IReadOnlyList<MusouShown> shown, MusouRules rules, ushort boss)
    {
        int n = 0;
        for (int i = 0; i < bars.Count && n < _multi.InstanceCount; i++)
        {
            var bar = bars[i];
            if (bar.Health == 0 || Find(shown, bar.Enemy) is not { } e || e.State == MusouEnemyState.Dying) continue;
            var spec = rules.Of(e.Type);
            float scale = e.Type == MusouEnemyType.Warboss ? rules.BossScale : 1f;
            var at = new Vector3(e.Position.X, e.Position.Y, e.Position.Z + spec.Height * scale + Above);
            _multi.SetInstanceTransform(n, new Transform3D(Basis.Identity, at));
            _multi.SetInstanceCustomData(n, new Color(bar.Health / 255f, e.Type == MusouEnemyType.Warboss || e.Id == boss ? 1.45f : 1f, 0f, 0f));
            n++;
        }
        _multi.VisibleInstanceCount = n;
        Visible = n > 0;
        return n;
    }

    static MusouShown? Find(IReadOnlyList<MusouShown> shown, ushort id)
    {
        for (int i = 0; i < shown.Count; i++) if (shown[i].Id == id) return shown[i];
        return null;
    }
}
