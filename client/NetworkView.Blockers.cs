using System;
using Godot;
using SmSim;

namespace SmClient;

/// <summary>
/// The spawn-room blockers in the networked view: each side's <c>MissionMovementBlocker</c> box (solid for the other side's
/// players in the shared movement, <see cref="CollisionWorld.CharacterFor"/>) is drawn for everyone as a faint translucent
/// plane through the box's middle across its thin axis, its full long side and height, in the owning side's colour (Space
/// Marines blue, Chaos orange). Since M31 the box reaches above the level's highest point (<see cref="SpawnBlocker.AboveLevel"/>;
/// a change from the original, owner's decision 2026-10-28: jump packs cannot fly over it); the plane keeps the placed box's height
/// (<see cref="CollisionWorld.PlacedBlockers"/>), so no curtain rises over the roofs: the raised part is invisible. A **placeholder demonstration**: the original's <c>prop_mp_enemy_shield</c> /
/// <c>prop_mp_allies_shield</c> effects at the <c>*_shield_*</c> markers are not reproduced.
/// </summary>
public partial class NetworkView
{
    const float ShieldAlpha = 0.16f;
    const float ShieldFadeNear = 2f, ShieldFadeFar = 8f;   // metres from the camera (placeholder drawing; ours)

    void DrawBlockers(CollisionWorld world)
    {
        _gameSpace?.GetNodeOrNull("SpawnShields")?.QueueFree();
        if (_gameSpace is null || world.Blockers.Count == 0 || !ZoneMarkers) return;
        var root = new Node3D { Name = "SpawnShields" };
        _gameSpace.AddChild(root);
        foreach (var b in world.PlacedBlockers)
        {
            var colour = b.Side == Team.SpaceMarines ? MarineSideColour : b.Side == Team.Chaos ? ChaosSideColour : new Color(0.7f, 0.7f, 0.7f);
            var material = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled, AlbedoColor = new Color(colour, ShieldAlpha),
                // Transparent within ShieldFadeNear of the camera, full at ShieldFadeFar (QA 2026-10-28: a camera inside a
                // spawn room was flooded by the plane in front of it).
                DistanceFadeMode = BaseMaterial3D.DistanceFadeModeEnum.PixelAlpha,
                DistanceFadeMinDistance = ShieldFadeNear, DistanceFadeMaxDistance = ShieldFadeFar,
            };
            // The plane's span: the box's longer horizontal side (its local X or Y), turned by the box's yaw about game Z.
            bool alongX = b.Size.X >= b.Size.Y;
            float span = alongX ? b.Size.X : b.Size.Y;
            float c = MathF.Cos(b.Yaw), s = MathF.Sin(b.Yaw);
            var across = alongX ? new Vector3(c, s, 0f) : new Vector3(-s, c, 0f);
            var centre = new Vector3(b.Center.X, b.Center.Y, b.Center.Z);
            var h = new Vector3(0f, 0f, b.Size.Z / 2f);
            var half = across * (span / 2f);
            Vector3 p0 = centre - half - h, p1 = centre + half - h, p2 = centre + half + h, p3 = centre - half + h;
            var mesh = new ImmediateMesh();
            mesh.SurfaceBegin(Mesh.PrimitiveType.Triangles, material);
            foreach (var v in new[] { p0, p1, p2, p0, p2, p3 }) mesh.SurfaceAddVertex(v);
            mesh.SurfaceEnd();
            root.AddChild(new MeshInstance3D { Mesh = mesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        }
        GD.Print($"spawn-room shields: {world.PlacedBlockers.Count} drawn (placeholder planes; solid for the other side)");
    }
}
