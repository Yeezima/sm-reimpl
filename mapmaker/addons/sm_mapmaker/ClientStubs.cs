using Godot;

// The two client classes the linked MapMaterials.cs / MapView.cs lean on, reduced to what an editor viewport needs (no anti-aliasing
// modes, no lighting looks). Same namespace and member names, so the client sources compile unchanged.
namespace SmClient;

/// <summary>Editor stand-in for the client's RenderQuality: cut-outs are plain alpha scissor.</summary>
internal static class RenderQuality
{
    public const int CutOutPriority = 0;
    public static void ApplyCutOut(BaseMaterial3D material) { }
    public static string CutOutRenderMode => "";
    public static string CutOutShaderLines(string texture) => "";
}

/// <summary>Editor stand-in for MapLightingView: material tuning factors keep their default.</summary>
internal static class MapLightingView
{
    public static float V(string key, float classic, float t7) => t7;
}
