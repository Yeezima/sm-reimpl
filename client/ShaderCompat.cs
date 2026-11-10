using System;
using System.Runtime.CompilerServices;
using Godot;

namespace SmClient;

/// <summary>
/// Polish pass P3 (2026-11-10; a rendering fix, faithful-safe): Godot's Compatibility renderer (<c>gl_compatibility</c>, old GPU / safe mode) does not read per-instance shader
/// uniforms (<c>instance uniform</c>; <c>GeometryInstance3D.SetInstanceShaderParameter</c>): every instance draws the uniform's default, so the power weapons' hit flare never
/// showed (<see cref="PowerBlade"/>, Musou's <c>ForceBlade</c>), the melta cone drew at full strength for its whole life with its noise sized for a 1 x 20 m cone
/// (<c>NetworkView.WeaponFx.cs</c>) and the slash cone would have stayed invisible (<c>NetworkView.SlashCone.cs</c>). The claw builder found it (stills claw-v3-compat-glow-on/off);
/// milestones.md's fx/character-visuals note had seen the first parameter "read as zero". On that renderer the shaders get plain <c>uniform</c>s instead (<see cref="Code"/>)
/// and each drawn instance its own copy of the material (<see cref="Own"/>, <see cref="OverlayFor"/>), set through <see cref="SetInstanceParam"/>; Forward+ keeps the instance
/// uniforms and the shared materials as before. <c>--instance-uniforms</c> keeps the instance uniforms on the Compatibility renderer too (the behaviour before P3, for a comparison).
/// </summary>
public static class ShaderCompat
{
    static readonly bool Keep = Array.IndexOf(OS.GetCmdlineUserArgs(), "--instance-uniforms") >= 0;

    /// <summary>True on the Compatibility renderer (unless <c>--instance-uniforms</c>): per-instance materials with plain uniforms stand in for instance uniforms.</summary>
    public static bool PerInstanceMaterials => SafeMode.Compatibility && !Keep;

    /// <summary>Each mesh's own overlay copy with the shared material it was copied from (weak keys: an entry goes with its mesh).</summary>
    static readonly ConditionalWeakTable<GeometryInstance3D, OwnOverlay> Overlays = new();

    sealed class OwnOverlay { public ShaderMaterial? Source, Copy; }

    /// <summary>The shader code for this renderer: on the Compatibility renderer every <c>instance uniform</c> becomes a plain <c>uniform</c>.</summary>
    public static string Code(string shaderCode) => PerInstanceMaterials ? shaderCode.Replace("instance uniform ", "uniform ", StringComparison.Ordinal) : shaderCode;

    /// <summary>A material for one drawn instance: the shared one on Forward+, its own copy on the Compatibility renderer (null stays null).</summary>
    public static ShaderMaterial? Own(ShaderMaterial? shared) => shared is null || !PerInstanceMaterials ? shared : (ShaderMaterial)shared.Duplicate();

    /// <summary>
    /// The overlay a mesh wears for a shared blade material: the shared one on Forward+; on the Compatibility renderer this mesh's own copy of it, made once per source
    /// material (a weapon switch brings another source: a new copy) and remembered for the mesh, whatever overlay the mesh wears meanwhile (Musou's Supercharged glow
    /// takes the overlay for a while: no new copy each frame).
    /// </summary>
    public static ShaderMaterial OverlayFor(GeometryInstance3D mesh, ShaderMaterial shared)
    {
        if (!PerInstanceMaterials) return shared;
        var own = Overlays.GetOrCreateValue(mesh);
        if (own.Copy is null || !ReferenceEquals(own.Source, shared)) { own.Source = shared; own.Copy = (ShaderMaterial)shared.Duplicate(); }
        return own.Copy;
    }

    /// <summary>Sets a per-instance parameter: the instance uniform on Forward+; on the Compatibility renderer the uniform of the instance's own material (override first, else overlay).</summary>
    public static void SetInstanceParam(GeometryInstance3D node, StringName name, Variant value)
    {
        if (!PerInstanceMaterials) { node.SetInstanceShaderParameter(name, value); return; }
        if (node.MaterialOverride is ShaderMaterial o) o.SetShaderParameter(name, value);
        else if (node.MaterialOverlay is ShaderMaterial ov) ov.SetShaderParameter(name, value);
    }

    /// <summary>The same for a material from <see cref="Own"/> or <see cref="OverlayFor"/>: on the Compatibility renderer that copy takes the value, whatever the node wears now.</summary>
    public static void SetInstanceParam(GeometryInstance3D node, ShaderMaterial own, StringName name, Variant value)
    {
        if (!PerInstanceMaterials) node.SetInstanceShaderParameter(name, value);
        else own.SetShaderParameter(name, value);
    }
}
