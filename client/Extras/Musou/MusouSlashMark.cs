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

    /// <summary>The twin name of the swing arc's player mark (owner 2026-11-15: every player model hides the arcs); its own name, so it lives beside a striker's per-attacker twin of the strokes.</summary>
    public const string PlayerTwinName = "SlashMarkPlayer";

    /// <summary>The one stencil reference of every player model's mark (above the 1..127 of <see cref="ReferenceOf"/>).</summary>
    public const int PlayerReference = 200;
    static readonly Dictionary<int, ShaderMaterial> Materials = new();

    /// <summary>The stencil reference of attacker <paramref name="player"/> (1..127; players whose ids differ by a multiple of 127 share one, which only matters if both strike in the same frame).</summary>
    public static int ReferenceOf(int player) => 1 + ((player % 127) + 127) % 127;

    static ShaderMaterial MaterialFor(int reference)
    {
        if (Materials.TryGetValue(reference, out var material) && GodotObject.IsInstanceValid(material)) return material;
        material = new ShaderMaterial
        {
            // Drawn before the strokes (the transparent pass sorts by priority), writing the reference; the colour is fully transparent.
            // The player twin (ref 200) writes first, an attacker's twin after it, so on the striker's own model the attacker's reference wins and its strokes stay hidden there (review).
            RenderPriority = reference == PlayerReference ? -11 : -10,
            Shader = new Shader { Code = $"shader_type spatial;\nrender_mode unshaded, cull_disabled, depth_draw_never, blend_mix;\nstencil_mode write, compare_always, {reference};\nvoid fragment() {{ ALBEDO = vec3(0.0); ALPHA = 0.0; }}" },
        };
        return Materials[reference] = material;
    }

    /// <summary>Frees the twins named <paramref name="twinName"/> under <paramref name="model"/> (the stencil draws they cost go with them).</summary>
    public static void Unmark(Node model, string twinName)
    {
        var stack = new Stack<Node>();
        stack.Push(model);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            foreach (var child in node.GetChildren())
            {
                if (child.Name == twinName) child.QueueFree();
                else stack.Push(child);
            }
        }
    }

    /// <summary>Gives every mesh under <paramref name="model"/> that has none a twin that writes <paramref name="reference"/>.</summary>
    public static void Mark(Node model, int reference, string twinName = TwinName)
    {
        var material = MaterialFor(reference);
        var stack = new Stack<Node>();
        stack.Push(model);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            foreach (var child in node.GetChildren())
            {
                if (child.Name.ToString().StartsWith(TwinName, System.StringComparison.Ordinal)) continue;   // any twin: a twin is never given a twin
                stack.Push(child);
            }
            if (node is not MeshInstance3D mesh || mesh.Mesh is null || mesh.HasNode(twinName)) continue;
            var twin = new MeshInstance3D
            {
                Name = twinName, Mesh = mesh.Mesh, Skin = mesh.Skin, MaterialOverride = material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                GIMode = GeometryInstance3D.GIModeEnum.Disabled, Layers = mesh.Layers,
            };
            mesh.AddChild(twin);
            if (!mesh.Skeleton.IsEmpty && mesh.GetNodeOrNull(mesh.Skeleton) is Skeleton3D skeleton) twin.Skeleton = twin.GetPathTo(skeleton);
        }
    }
}
