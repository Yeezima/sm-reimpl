using Godot;
using SmSim;

namespace SmClient;

/// <summary>
/// The map's movable props in the networked view (M39; the green ammo crates on Manufactorum): with --show-collision their
/// collision (<see cref="CollisionWorld.Props"/>, the shapes the simulation adds to both meshes) is drawn in translucent green
/// over the original crates, beside the grey level collision. A debugging view (**placeholder demonstration**); the crates
/// themselves are drawn by the map view with their original meshes.
/// </summary>
public partial class NetworkView
{
    void DrawProps(CollisionWorld world)
    {
        _gameSpace?.GetNodeOrNull("PropCollision")?.QueueFree();
        if (_gameSpace is null || !_showCollision || world.Props.Count == 0) return;
        var material = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled, AlbedoColor = new Color(0.1f, 1f, 0.2f, 0.45f), NoDepthTest = false,
        };
        var mesh = new ImmediateMesh();
        mesh.SurfaceBegin(Mesh.PrimitiveType.Triangles, material);
        int triangles = 0;
        foreach (var p in world.Props)
            for (int t = 0; t + 2 < p.Indices.Length; t += 3)
            {
                // Godot's front faces wind clockwise; both sides are drawn anyway (as the grey collision).
                foreach (int k in new[] { 0, 2, 1 })
                {
                    var v = p.Vertices[p.Indices[t + k]];
                    mesh.SurfaceAddVertex(new Vector3(v.X, v.Y, v.Z));
                }
                triangles++;
            }
        mesh.SurfaceEnd();
        _gameSpace.AddChild(new MeshInstance3D { Name = "PropCollision", Mesh = mesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        GD.Print($"prop collision: {world.Props.Count} props, {triangles} triangles drawn in green (--show-collision)");
    }
}
