using System;
using System.Collections.Generic;
using Godot;

namespace SmClient;

/// <summary>
/// Executions X6: Warfront's overshield shell (owner's brief: "a soft blue shield about 5 cm off the model while the overshield lasts"). For every armour mesh of a character a second skinned
/// <see cref="MeshInstance3D"/> shares the mesh, its skin and the character's skeleton (so it follows the pose every frame), with <see cref="ShaderCode"/>: the vertices pushed
/// <see cref="Push"/> metres along their normals, an additive fresnel blue, no depth write, no shadow. Its strength follows the overshield's fraction.
/// <list type="bullet">
/// <item>Not <c>MaterialOverlay</c>: the placeholder side tint and the hit flash use it (<c>CharacterViewer.SetOverlay</c>, <c>CharacterViewer.Flash.cs</c>). The shells live under their own node below
/// the skeleton, so <c>SetOverlay</c> (direct children only) and <c>SetLook</c> (armour pieces only) never touch them; a look swap is noticed (<see cref="Matches"/>) and the shell rebuilt.</item>
/// <item>The Compatibility renderer (old-GPU mode) draws it too: plain uniforms on one material per character (an <c>instance uniform</c> is not drawn there), plain GLSL, no screen or depth texture.</item>
/// </list>
/// </summary>
public sealed class OvershieldShell
{
    /// <summary>How far the shell sits off the model (metres; the owner's "about 5 cm").</summary>
    public const float Push = 0.05f;
    /// <summary>The shell's blue (ours; spec/behavior.md "camera, input, HUD and shield (X6)").</summary>
    public static readonly Color Tint = new(0.35f, 0.62f, 1f);
    /// <summary>The strength at the smallest overshield; the rest grows with the fraction (ours), so the shell fades as the overshield goes.</summary>
    public const float StrengthFloor = 0.35f;

    public const string NodeName = "OvershieldShell";

    const string ShaderCode = """
        shader_type spatial;
        // executions X6: the overshield shell. Skinned like the armour (VERTEX and NORMAL are already skinned here), pushed out along the normal, an additive fresnel rim.
        // Plain uniforms only: the Compatibility renderer draws no instance uniforms.
        render_mode blend_add, depth_draw_never, cull_back, unshaded, shadows_disabled;
        uniform vec4 tint : source_color = vec4(0.35, 0.62, 1.0, 1.0);
        uniform float strength = 1.0;
        uniform float push = 0.05;   // model units (the metres over the model's scale)
        void vertex() {
            VERTEX += NORMAL * push;
        }
        void fragment() {
            float facing = clamp(dot(normalize(NORMAL), normalize(VIEW)), 0.0, 1.0);
            float rim = pow(1.0 - facing, 3.0);
            ALBEDO = tint.rgb;
            ALPHA = clamp((0.04 + 0.6 * rim) * strength, 0.0, 1.0);   // soft: a faint film with a brighter rim (the owner's "soft blue shield")
        }
        """;

    static Shader? _shader;

    readonly Node3D _root;
    readonly ShaderMaterial _material;
    readonly Skeleton3D _skeleton;
    readonly List<(MeshInstance3D Source, MeshInstance3D Shell)> _pieces = new();

    OvershieldShell(Skeleton3D skeleton, Node3D root, ShaderMaterial material) => (_skeleton, _root, _material) = (skeleton, root, material);

    /// <summary>Shells made (the self-test's counter).</summary>
    public static int Created { get; private set; }

    /// <summary>The shell's node (freed with its character).</summary>
    public Node3D Root => _root;
    /// <summary>The strength last set (0..1).</summary>
    public float Strength { get; private set; }
    /// <summary>Shell meshes on the character.</summary>
    public int Pieces => _pieces.Count;

    /// <summary>A shell on <paramref name="character"/>'s armour pieces, or null when it has no skeleton or no piece yet.</summary>
    public static OvershieldShell? Attach(CharacterViewer character)
    {
        if (character.Skeleton is not { } skeleton || !GodotObject.IsInstanceValid(skeleton)) return null;
        _shader ??= new Shader { Code = ShaderCode };
        var material = new ShaderMaterial { Shader = _shader };   // one per character: its strength is a plain uniform
        material.SetShaderParameter("tint", Tint);
        var root = new Node3D { Name = NodeName };
        skeleton.AddChild(root);
        var shell = new OvershieldShell(skeleton, root, material);
        foreach (var source in ArmourPieces(skeleton))
        {
            var mesh = new MeshInstance3D
            {
                Name = source.Name + "_shell", Mesh = source.Mesh, Skin = source.Skin, Skeleton = new NodePath("../.."),
                MaterialOverride = material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Visible = source.Visible,
            };
            root.AddChild(mesh);
            shell._pieces.Add((source, mesh));
        }
        if (shell._pieces.Count == 0) { root.QueueFree(); return null; }
        Created++;
        return shell;
    }

    /// <summary>The character's armour pieces: the skeleton's mesh children with a skin, the bone lines aside.</summary>
    static IEnumerable<MeshInstance3D> ArmourPieces(Skeleton3D skeleton)
    {
        foreach (var child in skeleton.GetChildren())
            if (child is MeshInstance3D mesh && mesh.Name != "Bones" && mesh.Mesh is not null && mesh.Skin is not null) yield return mesh;
    }

    /// <summary>Whether the shell still dresses <paramref name="character"/>: the same skeleton, every source piece alive and no new piece (a look swap replaces them).</summary>
    public bool Matches(CharacterViewer character)
    {
        if (!GodotObject.IsInstanceValid(_root) || character.Skeleton != _skeleton) return false;
        int count = 0;
        foreach (var piece in ArmourPieces(_skeleton))
        {
            count++;
            bool found = false;
            foreach (var p in _pieces) if (p.Source == piece) { found = true; break; }
            if (!found) return false;
        }
        return count == _pieces.Count;
    }

    /// <summary>Shows the shell at <paramref name="fraction"/> of the overshield's cap (0 hides it); hidden pieces stay hidden.</summary>
    public void Set(float fraction)
    {
        if (!GodotObject.IsInstanceValid(_root)) return;
        float f = Math.Clamp(float.IsFinite(fraction) ? fraction : 0f, 0f, 1f);
        Strength = f <= 0f ? 0f : StrengthFloor + (1f - StrengthFloor) * f;
        _root.Visible = Strength > 0f;
        // The push in model units: the skeleton's scale turns metres into them.
        float scale = _skeleton.GlobalTransform.Basis.Scale.X;
        _material.SetShaderParameter("strength", Strength);
        _material.SetShaderParameter("push", Push / (scale > 1e-4f ? scale : 1f));
        foreach (var (source, shell) in _pieces) if (GodotObject.IsInstanceValid(source) && GodotObject.IsInstanceValid(shell)) shell.Visible = source.Visible;
    }

    /// <summary>Removes the shell.</summary>
    public void Free()
    {
        if (GodotObject.IsInstanceValid(_root)) _root.QueueFree();
        _pieces.Clear();
    }
}
