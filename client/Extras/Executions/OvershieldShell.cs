using System;
using System.Collections.Generic;
using Godot;

namespace SmClient;

/// <summary>
/// Executions X6: Warfront's overshield shell (owner's brief: "a soft blue shield about 5 cm off the model while the overshield lasts"). Every armour mesh of a character gets a second
/// <see cref="MeshInstance3D"/> sharing its mesh with <see cref="ShaderCode"/>: the vertices pushed <see cref="Push"/> metres along their normals, an additive fresnel blue, no depth write, no shadow.
/// <list type="bullet">
/// <item>Which meshes: every mesh under the character's skeleton (the skinned armour pieces, and any rigid piece hung on a bone: shoulder pads, helmet) and the worn items (<c>Worn_*</c>: the backpack,
/// the jump pack); never the weapons (<c>Weapon_*</c>), the bone lines, or the shell itself (media review of a18ed908: the pads and the backpack were left out). A skinned piece's shell shares its skin and
/// skeleton (it follows the pose every frame); a rigid piece's shell is its child (it follows the bone).</item>
/// <item>Strength: the glow's alpha is in proportion to the overshield's fraction of its cap times <see cref="Strength"/> (the one tunable value, shared with the HUD tint), so full, half and low read
/// differently (media review: full and half looked alike).</item>
/// <item>Not <c>MaterialOverlay</c> (the side tint and the hit flash use it). The skinned shells live under their own node below the skeleton, so <c>SetOverlay</c> (direct children) and
/// <c>SetLook</c> (armour pieces) never touch them; a look swap is noticed (<see cref="Matches"/>) and the shell rebuilt.</item>
/// <item>The Compatibility renderer draws it too: plain uniforms on one material per character (an <c>instance uniform</c> is not drawn there), plain GLSL, no screen or depth texture.</item>
/// </list>
/// </summary>
public sealed class OvershieldShell
{
    /// <summary>How far the shell sits off the model (metres; the owner's "about 5 cm").</summary>
    public const float Push = 0.05f;
    /// <summary>The shell's blue (ours; spec/behavior.md "camera, input, HUD and shield (X6)").</summary>
    public static readonly Color Tint = new(0.35f, 0.62f, 1f);
    /// <summary>A fixed strength the test aids' comparison sets (null: the owner's curve by the overshield's fraction, <see cref="OvershieldLook.StrengthAt"/>).</summary>
    public static float? Strength { get; set; }

    /// <summary>The look's strength for an overshield at <paramref name="fraction"/> of its cap: the test override, else the owner's curve (0.6 from full to half, 1.0 at low).</summary>
    public static float StrengthAt(float fraction) => Strength ?? OvershieldLook.StrengthAt(fraction);
    /// <summary>The share of the strength a barely-there overshield still shows (so a low overshield stays visible; ours).</summary>
    public const float Floor = 0.12f;

    public const string NodeName = "OvershieldShell";

    const string ShaderCode = """
        shader_type spatial;
        // executions X6: the overshield shell. Skinned like the armour (VERTEX and NORMAL are already skinned here), pushed out along the normal, an additive fresnel rim.
        // Plain uniforms only: the Compatibility renderer draws no instance uniforms.
        render_mode blend_add, depth_draw_never, cull_back, unshaded, shadows_disabled;
        uniform vec4 tint : source_color = vec4(0.35, 0.62, 1.0, 1.0);
        uniform float strength = 1.0;   // overshield fraction x the look's strength
        uniform float push = 0.05;      // model units (the metres over the model's scale)
        void vertex() {
            VERTEX += NORMAL * push;
        }
        void fragment() {
            float facing = clamp(dot(normalize(NORMAL), normalize(VIEW)), 0.0, 1.0);
            float rim = pow(1.0 - facing, 3.0);
            ALBEDO = tint.rgb;
            ALPHA = clamp((0.04 + 0.6 * rim) * strength, 0.0, 1.0);   // soft: a faint film with a brighter rim
        }
        """;

    static Shader? _shader;
    static ShaderMaterial? _nothing;

    /// <summary>A material that draws nothing (an excluded surface of a covered mesh).</summary>
    static ShaderMaterial Nothing() => _nothing ??= new ShaderMaterial
    {
        Shader = new Shader { Code = "shader_type spatial;\nrender_mode unshaded, depth_draw_never, shadows_disabled;\nvoid fragment() { discard; }\n" },
    };

    readonly Node3D _root;
    readonly ShaderMaterial _material;
    readonly Skeleton3D _skeleton;
    readonly List<(MeshInstance3D Source, MeshInstance3D Shell)> _pieces = new();

    OvershieldShell(Skeleton3D skeleton, Node3D root, ShaderMaterial material) => (_skeleton, _root, _material) = (skeleton, root, material);

    /// <summary>Shells made (the self-test's counter).</summary>
    public static int Created { get; private set; }

    /// <summary>The shell's node (freed with its character).</summary>
    public Node3D Root => _root;
    /// <summary>The shader strength last set.</summary>
    public float ShownStrength { get; private set; }
    /// <summary>Shell meshes on the character (skinned and rigid).</summary>
    public int Pieces => _pieces.Count;

    /// <summary>The shader strength for an overshield at <paramref name="fraction"/> of its cap: in proportion to it (a floor for a barely-there one) times the look's strength (<see cref="StrengthAt"/>); 0 without one.</summary>
    public static float StrengthFor(float fraction)
    {
        float f = Math.Clamp(float.IsFinite(fraction) ? fraction : 0f, 0f, 1f);
        return f <= 0f ? 0f : StrengthAt(f) * MathF.Max(f, Floor);
    }

    /// <summary>A shell on <paramref name="character"/>'s armour, or null when it has no skeleton or no piece yet.</summary>
    public static OvershieldShell? Attach(CharacterViewer character)
    {
        if (character.Skeleton is not { } skeleton || !GodotObject.IsInstanceValid(skeleton)) return null;
        _shader ??= new Shader { Code = ShaderCode };
        var material = new ShaderMaterial { Shader = _shader };   // one per character: its strength is a plain uniform
        material.SetShaderParameter("tint", Tint);
        var root = new Node3D { Name = NodeName };
        skeleton.AddChild(root);
        var shell = new OvershieldShell(skeleton, root, material);
        foreach (var source in ArmourMeshes(skeleton))
        {
            var mesh = new MeshInstance3D
            {
                Name = source.Name + "_shell", Mesh = source.Mesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Visible = source.Visible,
            };
            // Per surface: an armour piece is covered whole, whatever its names (X6 review X6-8: a pad named "..._seal" is armour); on any other mesh (worn items, attachments) a surface named
            // as cloth, leather or an ornament draws nothing (OvershieldCoverage).
            bool armour = IsArmourPiece(source);
            for (int i = 0; i < source.Mesh.GetSurfaceCount(); i++)
                mesh.SetSurfaceOverrideMaterial(i, !OvershieldCoverage.Covered(source.Mesh is ArrayMesh am ? am.SurfaceGetName(i) : null, armour) ? Nothing() : material);
            if (source.Skin is not null && source.GetParent() == skeleton)
            {
                mesh.Skin = source.Skin;
                mesh.Skeleton = new NodePath("../..");
                root.AddChild(mesh);
            }
            else source.AddChild(mesh);   // a rigid piece (on a bone or a worn attachment): the shell rides with it
            shell._pieces.Add((source, mesh));
        }
        if (shell._pieces.Count == 0) { root.QueueFree(); return null; }
        shell.Stamp(character);
        Created++;
        return shell;
    }

    /// <summary>The meta <c>CharacterViewer.AddPieces</c> sets on every armour piece of the look (its <c>ArmourMeta</c>, "armour_piece"; read here, not written).</summary>
    const string ArmourPieceMeta = "armour_piece";

    /// <summary>
    /// Whether <paramref name="node"/> is armour, always covered whatever its name (the name exclusion is for the other worn items and attachments only): one of the look's armour pieces, or the
    /// worn wargear (the jump pack: <see cref="WeaponRig.WornPackMeta"/> on its attachment nodes) or a mesh under it (X6 re-review 6, owner: the jump pack is covered; the iron halo would be too, but the client has no Iron Halo world model: the perk shows on the HUD only).
    /// </summary>
    static bool IsArmourPiece(Node node)
    {
        for (Node? n = node; n is not null && n is not Skeleton3D; n = n.GetParent())
            if (n.HasMeta(ArmourPieceMeta) || n.HasMeta(WeaponRig.WornPackMeta)) return true;
        return false;
    }

    /// <summary>
    /// The meshes the shell covers under the skeleton: every armour piece of the look (whatever its name), and every other mesh but the weapons' (<c>Weapon_*</c> subtrees), the bone lines, earlier
    /// shells and a non-armour node (a worn item or an attachment, or one of its children) named as cloth, leather or an ornament.
    /// </summary>
    static IEnumerable<MeshInstance3D> ArmourMeshes(Skeleton3D skeleton)
    {
        var stack = new Stack<Node>();
        foreach (var child in skeleton.GetChildren()) stack.Push(child);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            string name = node.Name;
            // Never a weapon, the bone lines or a shell; the name exclusion only for what is not an armour piece (X6 review X6-8).
            if (OvershieldCoverage.Weapon(name) || name == NodeName || name == "Bones" || name.EndsWith("_shell", StringComparison.Ordinal)) continue;
            if (!OvershieldCoverage.Covered(name, IsArmourPiece(node))) continue;
            if (node is MeshInstance3D found && found.Mesh is not null && !IsEffect(found)) yield return found;
            foreach (var child in node.GetChildren()) stack.Push(child);
        }
    }

    /// <summary>Every mesh the shell would look at on <paramref name="character"/> with its surfaces' material names (the self-test's survey of the install's naming).</summary>
    public static IEnumerable<string> Describe(CharacterViewer character)
    {
        if (character.Skeleton is not { } skeleton) yield break;
        foreach (var mesh in ArmourMeshes(skeleton))
        {
            var names = new List<string>();
            for (int i = 0; i < mesh.Mesh.GetSurfaceCount(); i++) names.Add(mesh.Mesh is ArrayMesh am ? am.SurfaceGetName(i) : "?");
            bool armour = IsArmourPiece(mesh);
            var excluded = armour ? new List<string>() : names.FindAll(n => OvershieldCoverage.Excluded(n));
            yield return $"covered {(armour ? "armour" : "other")} {mesh.GetParent()?.Name}/{mesh.Name} skin {(mesh.Skin is null ? "no" : "yes")}: {string.Join(", ", names)}" +
                         (excluded.Count > 0 ? $" (surfaces left out: {string.Join(", ", excluded)})" : "");
        }
        // What the walk leaves out (weapons are not listed): non-armour nodes named as cloth, leather or an ornament.
        var stack = new Stack<Node>();
        foreach (var child in skeleton.GetChildren()) stack.Push(child);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            string name = node.Name;
            if (OvershieldCoverage.Weapon(name) || name == NodeName || name == "Bones" || name.EndsWith("_shell", StringComparison.Ordinal)) continue;
            if (!OvershieldCoverage.Covered(name, IsArmourPiece(node))) { yield return $"left out {node.GetParent()?.Name}/{name} ({node.GetClass()})"; continue; }
            foreach (var child in node.GetChildren()) stack.Push(child);
        }
    }

    /// <summary>Effect cards (exhaust flames, glows, trails) carry a shader override; armour never does.</summary>
    static bool IsEffect(MeshInstance3D mesh) => mesh.MaterialOverride is ShaderMaterial || mesh.Mesh is QuadMesh;

    /// <summary>Whether the shell still dresses <paramref name="character"/>: the same skeleton, every source piece alive and no new piece (a look swap or a new worn item replaces them).</summary>
    public bool Matches(CharacterViewer character)
    {
        if (!GodotObject.IsInstanceValid(_root) || character.Skeleton != _skeleton) return false;
        // The cheap check every frame (X6 review X6-9): the same look, the skeleton's child count unchanged (a new worn item or a look swap's pieces change it) and every source piece alive
        // under its parent (a look swap frees the old pieces at once, a worn item swap frees its node). Only when that changed is the skeleton walked again.
        if (ReferenceEquals(character.Assets, _stampAssets) && _skeleton.GetChildCount() == _stampChildren && ChildIds() == _stampIds && SourcesAlive()) return true;
        bool same = Walk();
        if (same) Stamp(character);
        return same;
    }

    MarineAssets? _stampAssets;
    int _stampChildren = -1;
    ulong _stampIds;

    /// <summary>A cheap order-sensitive hash of the skeleton's direct children's instance ids: a swap that keeps the child count still changes it (X6 re-review 5).</summary>
    ulong ChildIds()
    {
        ulong h = 1469598103934665603UL;
        int n = _skeleton.GetChildCount();
        for (int i = 0; i < n; i++) h = (h ^ _skeleton.GetChild(i).GetInstanceId()) * 1099511628211UL;
        return h;
    }

    /// <summary>Full walks of the skeleton (<see cref="Matches"/> after a change; the self-test's counter).</summary>
    public static int Walks { get; private set; }

    void Stamp(CharacterViewer character)
    {
        _stampAssets = character.Assets;
        _stampChildren = _skeleton.GetChildCount();
        _stampIds = ChildIds();
    }

    bool SourcesAlive()
    {
        foreach (var (source, _) in _pieces) if (!GodotObject.IsInstanceValid(source) || !source.IsInsideTree()) return false;
        return true;
    }

    bool Walk()
    {
        Walks++;
        int count = 0;
        foreach (var piece in ArmourMeshes(_skeleton))
        {
            count++;
            bool found = false;
            foreach (var p in _pieces) if (p.Source == piece) { found = true; break; }
            if (!found) return false;
        }
        return count == _pieces.Count;
    }

    /// <summary>Shows the shell for an overshield at <paramref name="fraction"/> of its cap (0 hides it); hidden pieces stay hidden.</summary>
    public void Set(float fraction)
    {
        if (!GodotObject.IsInstanceValid(_root)) return;
        ShownStrength = StrengthFor(fraction);
        bool on = ShownStrength > 0f;
        _root.Visible = on;
        float scale = _skeleton.GlobalTransform.Basis.Scale.X;   // the push in model units: the skeleton's scale turns metres into them
        _material.SetShaderParameter("strength", ShownStrength);
        _material.SetShaderParameter("push", Push / (scale > 1e-4f ? scale : 1f));
        foreach (var (source, shell) in _pieces)
            if (GodotObject.IsInstanceValid(source) && GodotObject.IsInstanceValid(shell)) shell.Visible = on && source.Visible;
    }

    /// <summary>Removes the shell.</summary>
    public void Free()
    {
        foreach (var (_, shell) in _pieces) if (GodotObject.IsInstanceValid(shell) && shell.GetParent() != _root) shell.QueueFree();
        if (GodotObject.IsInstanceValid(_root)) _root.QueueFree();
        _pieces.Clear();
    }
}
