using System.Collections.Generic;
using Godot;

namespace SmClient;

/// <summary>
/// The stencil mark of a striking player's model (owner, 2026-11-09: the Musou slash strokes draw over every model but the one that struck). Every mesh of the model gets an invisible twin child
/// (same mesh, skin and skeleton, so it follows the pose) whose material writes the attacker's stencil reference where the mesh is visible (depth tested, alpha 0: nothing shows); that attacker's
/// strokes (<see cref="MusouSlashView"/>) skip those pixels. A twin is a child of its mesh, so it is hidden with it. Forward+ and Mobile; the Compatibility renderer has no stencil and ignores the
/// writes and the reads alike. Client only.
/// </summary>
public static class MusouSlashMark
{
    /// <summary>Seconds between looking again for meshes the model gained (a drawn weapon, a new look).</summary>
    public const double RefreshSeconds = 0.25;
    const string TwinName = "SlashMark";
    static readonly Dictionary<int, ShaderMaterial> Materials = new();

    /// <summary>The stencil reference of attacker <paramref name="player"/> (1..127; players whose ids differ by a multiple of 127 share one, which only matters if both strike in the same frame).</summary>
    public static int ReferenceOf(int player) => 1 + ((player % 127) + 127) % 127;

    static ShaderMaterial MaterialFor(int reference)
    {
        if (Materials.TryGetValue(reference, out var material) && GodotObject.IsInstanceValid(material)) return material;
        material = new ShaderMaterial
        {
            // Drawn before the strokes (the transparent pass sorts by priority), writing the reference; the colour is fully transparent.
            RenderPriority = -10,
            Shader = new Shader { Code = $"shader_type spatial;\nrender_mode unshaded, cull_disabled, depth_draw_never, blend_mix;\nstencil_mode write, compare_always, {reference};\nvoid fragment() {{ ALBEDO = vec3(0.0); ALPHA = 0.0; }}" },
        };
        return Materials[reference] = material;
    }

    /// <summary>Gives every mesh under <paramref name="model"/> that has none a twin that writes <paramref name="reference"/>.</summary>
    public static void Mark(Node model, int reference)
    {
        var material = MaterialFor(reference);
        var stack = new Stack<Node>();
        stack.Push(model);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            foreach (var child in node.GetChildren())
            {
                if (child.Name == TwinName) continue;
                stack.Push(child);
            }
            if (node is not MeshInstance3D mesh || mesh.Mesh is null || mesh.HasNode(TwinName)) continue;
            var twin = new MeshInstance3D
            {
                Name = TwinName, Mesh = mesh.Mesh, Skin = mesh.Skin, MaterialOverride = material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                GIMode = GeometryInstance3D.GIModeEnum.Disabled, Layers = mesh.Layers,
            };
            mesh.AddChild(twin);
            if (!mesh.Skeleton.IsEmpty && mesh.GetNodeOrNull(mesh.Skeleton) is Skeleton3D skeleton) twin.Skeleton = twin.GetPathTo(skeleton);
        }
    }
}
